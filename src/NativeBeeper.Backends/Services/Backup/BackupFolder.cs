using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NativeBeeper.App.Services.Backup;

/// <summary>A folder a cloud service keeps in sync, found on this PC, as a one-click place for backups.</summary>
internal sealed record SyncRoot(string Service, string Path)
{
    public string Suggested => System.IO.Path.Combine(Path, BackupFolder.SuggestedName);
}

/// <summary>What a prune did: the names it removed, and how many old backups it left because they are spared.</summary>
internal sealed record PruneResult(IReadOnlyList<string> Removed, int Spared);

/// <summary>
/// The backup folder: how a backup is named, how the folder is kept to the newest few, and which synced folders to
/// suggest. The folder is the user's, and may hold anything else, so nothing here touches a file whose name this app
/// did not give it.
/// </summary>
/// <remarks>
/// Every PC is offered the same synced folder, so several installs may share one. Each signs its backups' names with a
/// short random tag of its own and prunes only names carrying that tag, so one PC's backups are never pushed out by
/// another's, however long it stays off.
/// </remarks>
internal static class BackupFolder
{
    public const int Keep = 5;
    public const string SuggestedName = "Messages backups";
    private const string TagPattern = "[a-z0-9]{4}";
    private static readonly Regex Tag = new("^" + TagPattern + "$", RegexOptions.CultureInvariant);
    // Exactly the names FileName gives, and the temporary names a write in progress uses beside them.
    private static readonly Regex Name = new(@"^Messages backup (\d{4}-\d{2}-\d{2} \d{4}) (" + TagPattern + @")(?: \(([2-9]|[1-9]\d{1,2})\))?\.msgbackup$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Temporary = new(@"^Messages backup \d{4}-\d{2}-\d{2} \d{4} (" + TagPattern + @")(?: \((?:[2-9]|[1-9]\d{1,2})\))?\.msgbackup\.[0-9a-f]{32}\.tmp$",
        RegexOptions.CultureInvariant);

    /// <summary>A new install's tag: random, and saying nothing about the PC, the user or an account.</summary>
    public static string NewInstallTag() => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 4);

    public static bool IsInstallTag(string? tag) => tag is not null && Tag.IsMatch(tag);

    public static string FileName(DateTime local, string install, int copy = 1) =>
        "Messages backup " + local.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + " " + install + (copy > 1 ? $" ({copy})" : "") + BackupFile.Extension;

    /// <summary>Whether a name is one some install of this app gave a backup.</summary>
    public static bool IsBackupName(string name) => Name.IsMatch(name);

    /// <summary>The path the next backup takes: named for the minute, with a number when that minute already has one.</summary>
    public static string NextPath(string folder, DateTime local, string install)
    {
        for (var copy = 1; copy < 1000; copy++)
            if (Path.Combine(folder, FileName(local, install, copy)) is var path && !File.Exists(path) && !Directory.Exists(path)) return path;
        throw new IOException("Too many backups for one minute.");
    }

    public static string TemporaryPath(string final) => final + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>
    /// Removes all but the newest <paramref name="keep"/> of this install's backups, and nothing else. A name in
    /// <paramref name="spare"/> is never removed: it holds history no newer backup has.
    /// </summary>
    public static PruneResult Prune(string folder, string install, IEnumerable<string>? spare = null, int keep = Keep)
    {
        var spared = new HashSet<string>(spare ?? [], StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        var left = 0;
        foreach (var path in Ours(folder, install).Skip(keep))
        {
            if (spared.Contains(Path.GetFileName(path))) { left++; continue; }
            try { File.Delete(path); removed.Add(Path.GetFileName(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Tried again after the next backup. */ }
        }
        return new(removed, left);
    }

    /// <summary>
    /// This install's newest backup in the folder, by the minute its name carries and then its copy number, or null when
    /// there is none. Another PC's backups, and a write still arriving under its temporary name, are never picked. Reads
    /// the folder's listing only; nothing is opened.
    /// </summary>
    public static string? Newest(string folder, string install) => Ours(folder, install).FirstOrDefault();

    /// <summary>Whether <paramref name="name"/> is a bare file name this install gave one of its backups.</summary>
    public static bool IsOwnBackupName(string? name, string install) =>
        name is not null && Name.Match(name) is { Success: true } match && match.Groups[2].Value == install;

    // This install's backups, newest first: the order Prune keeps by and Newest picks from.
    private static IReadOnlyList<string> Ours(string folder, string install)
    {
        var ours = new List<(string Path, DateTime At, int Copy)>();
        foreach (var path in Directory.GetFiles(folder))
        {
            if (Name.Match(Path.GetFileName(path)) is not { Success: true } match || match.Groups[2].Value != install) continue;
            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            ours.Add((path, at, match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 1));
        }
        return ours.OrderByDescending(item => item.At).ThenByDescending(item => item.Copy).Select(item => item.Path).ToArray();
    }

    /// <summary>Removes what this install's backup interrupted mid-write left behind, once it is old enough that nothing
    /// can still be writing it. Only this install's temporary names are touched: another PC's may still be arriving.</summary>
    public static int SweepTemporary(string folder, string install, TimeSpan age, DateTime nowUtc)
    {
        var removed = 0;
        foreach (var path in Directory.GetFiles(folder))
        {
            if (Temporary.Match(Path.GetFileName(path)) is not { Success: true } match || match.Groups[1].Value != install) continue;
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(path) < age) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>
    /// Folders OneDrive, Google Drive for desktop and Dropbox keep in sync here, found from where each says it keeps
    /// them. Read-only and local: environment variables, a settings file Dropbox writes, and whether a folder exists.
    /// </summary>
    public static IReadOnlyList<SyncRoot> DetectSyncRoots(Func<string, string?> environment, IEnumerable<string> drives)
    {
        var found = new List<SyncRoot>();
        void Add(string service, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim());
                if (Directory.Exists(full) && !found.Any(root => string.Equals(root.Path, full, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new(service, full));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException) { }
        }

        // OneDrive sets these for the personal account and each work account it syncs. A work folder is named for its
        // organisation ("OneDrive - Contoso"), which is the clearest label it has.
        Add("OneDrive", environment("OneDriveConsumer"));
        foreach (var name in new[] { "OneDriveCommercial", "OneDrive" })
            if (environment(name) is { Length: > 0 } path) Add(Label(path, "OneDrive"), path);

        // Google Drive for desktop shows "My Drive" on a drive letter of its own, or in the profile when mirrored.
        foreach (var drive in drives) Add("Google Drive", Path.Combine(drive, "My Drive"));
        if (environment("USERPROFILE") is { Length: > 0 } profile) Add("Google Drive", Path.Combine(profile, "My Drive"));

        // Dropbox records where it syncs in info.json, per account; the profile folder is its default.
        foreach (var root in new[] { environment("LOCALAPPDATA"), environment("APPDATA") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                var info = Path.Combine(root, "Dropbox", "info.json");
                if (!File.Exists(info) || new FileInfo(info).Length > 64 << 10) continue;
                using var json = JsonDocument.Parse(File.ReadAllBytes(info));
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var account in json.RootElement.EnumerateObject())
                    if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var path) &&
                        path.ValueKind == JsonValueKind.String)
                        Add(Label(path.GetString()!, "Dropbox"), path.GetString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (environment("USERPROFILE") is { Length: > 0 } home) Add("Dropbox", Path.Combine(home, "Dropbox"));
        return found;
    }

    private static string Label(string path, string service)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path.Trim()));
        return name.StartsWith(service, StringComparison.OrdinalIgnoreCase) && name.Length <= 48 ? name : service;
    }
}
