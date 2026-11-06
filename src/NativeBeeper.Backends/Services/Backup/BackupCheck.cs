using System.Text.Json;

namespace NativeBeeper.App.Services.Backup;

/// <summary>An opened backup file: authenticated, its manifest read, its parts readable until disposed.</summary>
internal sealed class OpenedBackup : IDisposable
{
    private readonly FileStream file;
    private readonly BackupReader reader;
    public BackupContents Contents { get; }
    public BackupManifest Manifest => Contents.Manifest;

    private OpenedBackup(FileStream file, BackupReader reader, BackupContents contents) { this.file = file; this.reader = reader; Contents = contents; }

    public static OpenedBackup Open(string path, string password)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            var reader = BackupFile.Open(file, password);
            try { return new(file, reader, BackupContents.Open(reader)); }
            catch { reader.Dispose(); throw; }
        }
        catch { file.Dispose(); throw; }
    }

    public void Dispose() { Contents.Dispose(); reader.Dispose(); file.Dispose(); }
}

/// <summary>
/// What reading the latest backup back found. Result is one of ok, unreadable, unsupported, missing, online_only,
/// old_password or busy; Name is the file checked, and the counts are what it holds when it read back.
/// </summary>
internal sealed record BackupCheckResult(string Result, string? Name, int Pages, int LocalItems, long Bytes)
{
    public static BackupCheckResult Busy { get; } = new("busy", null, 0, 0, 0);
}

/// <summary>
/// Reads this PC's latest backup back, the way a restore would, without restoring anything: backups go daily into a
/// synced folder the app never reads again, so one that no longer decrypts would otherwise be found only during a
/// restore.
/// </summary>
/// <remarks>
/// Nothing in the folder is written, pruned or swept, and a file a sync service keeps only online is left there rather
/// than downloaded. A file another program holds — a sync client uploading it — is busy, never damage: it is checked
/// again at the next poll. Provider-free, so the contract harness runs it as the app does.
/// </remarks>
internal static class BackupCheck
{
    // A cloud placeholder: reading it would make the sync service download it.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000, RecallOnOpen = (FileAttributes)0x40000;

    /// <summary>
    /// Checks <paramref name="lastName"/> when it is still there, otherwise this install's newest backup. When
    /// <paramref name="passwordSetAt"/> is later than the backup, the password saved here is not the one it was sealed
    /// under: that is old_password, with nothing decrypted.
    /// </summary>
    public static BackupCheckResult Run(string folder, string install, string? lastName, string password,
        Func<string, FileAttributes> attributes, CancellationToken ct, DateTimeOffset? passwordSetAt = null)
    {
        ct.ThrowIfCancellationRequested();
        string? path;
        try
        {
            path = BackupFolder.IsOwnBackupName(lastName, install) && Path.Combine(folder, lastName!) is var last && File.Exists(last)
                ? last : BackupFolder.Newest(folder, install);
        }
        // A folder that is not there, or not readable, has no backup of this PC in it that could be checked.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { path = null; }
        if (path is null) return new("missing", null, 0, 0, 0);
        var name = Path.GetFileName(path);

        long bytes;
        try
        {
            if ((attributes(path) & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0) return new("online_only", name, 0, 0, 0);
            var info = new FileInfo(path);
            bytes = info.Length;
            // Written after the password changed means sealed under the new one (BackupService moves PasswordSetAt past
            // a backup that read the old password before the change and finished after it).
            if (passwordSetAt is { } changed && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < changed) return new("old_password", name, 0, 0, bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new("missing", name, 0, 0, 0); }
        catch (IOException e) when (Held(e)) { return BackupCheckResult.Busy with { Name = name }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new("unreadable", name, 0, 0, 0); }

        try
        {
            using var opened = OpenedBackup.Open(path, password);
            var manifest = opened.Manifest;
            _ = opened.Contents.ReadLocal();
            var pages = 0;
            foreach (var _ in opened.Contents.ReadPages())
            {
                ct.ThrowIfCancellationRequested();
                pages++;
            }
            if (pages != (manifest.Messenger?.Pages ?? 0) || !OnlyKnownEntries(opened.Contents.EntryNames, pages)) return new("unreadable", name, 0, 0, bytes);
            return new("ok", name, pages, manifest.LocalItems, bytes);
        }
        catch (BackupFileException failure) { return new(failure.Failure == BackupFileFailure.Unsupported ? "unsupported" : "unreadable", name, 0, 0, bytes); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new("missing", name, 0, 0, 0); }
        // Another program holding the file (a sync client uploading it): said as busy, not damage, and tried again later.
        catch (IOException e) when (Held(e)) { return BackupCheckResult.Busy with { Name = name }; }
        // Anything else that stops it being read (access refused, a read error on the disk or share) is a backup that
        // could not be read back, as a restore would find it.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new("unreadable", name, 0, 0, bytes); }
        // What decompressing or parsing a part throws once it has authenticated: the file holds something no backup has.
        catch (Exception e) when (e is InvalidDataException or JsonException or NotSupportedException) { return new("unreadable", name, 0, 0, bytes); }
    }

    /// <summary>
    /// Runs a check only when nothing else holds <paramref name="running"/>: a check never overlaps a backup or a
    /// restore. When one does, the answer is busy and the check is tried again later.
    /// </summary>
    public static async Task<BackupCheckResult> GatedAsync(SemaphoreSlim running, Func<Task<BackupCheckResult>> check)
    {
        if (!await running.WaitAsync(0)) return BackupCheckResult.Busy;
        try { return await check(); }
        finally { running.Release(); }
    }

    // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another program has the file open, or a range of it locked.
    private static bool Held(IOException e) => e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    // Exactly the parts a backup is written with: its manifest, its local rows and each of its pages, nothing more.
    private static bool OnlyKnownEntries(IReadOnlyList<string> names, int pages)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { BackupArchive.ManifestEntry, BackupArchive.LocalEntry };
        for (var number = 0; number < pages; number++) expected.Add(BackupArchive.PageEntry(number));
        return names.All(expected.Contains) && names.Contains(BackupArchive.ManifestEntry) && names.Contains(BackupArchive.LocalEntry);
    }

    /// <summary>
    /// What a check found, in plain words. A folder that is busy is never worded as a damaged backup. With daily backups
    /// off (<paramref name="daily"/> false) nothing follows an unreadable backup by itself, so the words ask for one.
    /// </summary>
    public static string Describe(string result, bool daily = true) => result switch
    {
        "ok" => "The latest backup reads back correctly.",
        "unreadable" when !daily => "The latest backup couldn't be read back. Back up now to replace it.",
        "unreadable" =>"The latest backup couldn't be read back. A new backup will run shortly.",
        "online_only" => "The latest backup is stored online only, so it wasn't checked.",
        "missing" => "No backup from this PC is in the folder.",
        "old_password" => "The latest backup uses your previous password. The next one will use the new password.",
        "unsupported" => "The latest backup was made by a newer version of Messages.",
        "password" => BackupSettings.Failure("password"),
        _ => "The backup folder is busy. It will be checked again later.",
    };
}
