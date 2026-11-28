using MessageLink.App.Services.Messenger;

namespace MessageLink.App.Services.Backup;

/// <summary>A backup step that failed, by a short code the Settings line turns into words (BackupSettings.Failure).</summary>
internal sealed class BackupFailedException(string reason, Exception? inner = null) : Exception(reason, inner)
{
    public string Reason => Message;
}

/// <summary>A backup file in place: what it holds, where it is and how big it is.</summary>
internal sealed record BackupWritten(BackupManifest Manifest, string Path, long Bytes);

/// <summary>
/// Writes one backup file into its folder: under a temporary name beside it first, then moved into place, so only a
/// finished file ever carries a backup's name.
/// </summary>
internal static class NewBackupFile
{
    /// <summary>
    /// Writes the backup, with Messenger's history when <paramref name="messenger"/> is given. Reading that history can
    /// fail on its own — a store the helper could not open, an export that expired, a helper that went away — and
    /// then the file is written again without it, its manifest saying "failed", rather than losing the drafts, names
    /// and preferences along with it. <paramref name="messengerFailed"/> hears why before the second write. Any other
    /// failure, and cancellation, fails the write and leaves nothing behind.
    /// </summary>
    /// <remarks>A Messenger failure arrives as a <see cref="BackupFailedException"/> with reason "messenger" from
    /// the pages, or as the <see cref="InvalidDataException"/> BackupArchive throws for pages out of sequence.</remarks>
    public static BackupWritten Write(string folder, string install, string password, LocalStateSnapshot local,
        IEnumerable<MessengerHistoryPage>? messenger, string? leftOut, string app, DateTimeOffset created, DateTime localTime,
        Action<Exception>? messengerFailed = null)
    {
        try { return Once(messenger, leftOut); }
        catch (Exception e) when (messenger is not null && e is BackupFailedException { Reason: "messenger" } or InvalidDataException)
        {
            messengerFailed?.Invoke(e is BackupFailedException { InnerException: { } inner } ? inner : e);
            return Once(null, "failed");
        }

        BackupWritten Once(IEnumerable<MessengerHistoryPage>? pages, string? without)
        {
            string final, temporary;
            try { final = BackupFolder.NextPath(folder, localTime, install); temporary = BackupFolder.TemporaryPath(final); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new BackupFailedException(Classify(e, "folder"), e); }
            try
            {
                BackupManifest manifest;
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    using var sealedPayload = BackupFile.Create(file, password);
                    manifest = BackupArchive.Write(sealedPayload, local, pages, without, app, created);
                    sealedPayload.Complete();
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, final, overwrite: false);
                return new(manifest, final, new FileInfo(final).Length);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
                throw;
            }
        }
    }

    /// <summary>A folder or disk failure by its code, or <paramref name="otherwise"/>.</summary>
    public static string Classify(Exception e, string otherwise) => e switch
    {
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => "space",
        UnauthorizedAccessException or DirectoryNotFoundException or DriveNotFoundException or PathTooLongException => "folder",
        _ => otherwise,
    };
}
