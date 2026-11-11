using System.Security.Cryptography;
using NativeBeeper.App.Services;
using NativeBeeper.Host;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// The keys folder of a Linux host (<c>MESSAGES_KEYS</c>, <c>/keys</c> in the image: a Docker named volume, so it sits outside every share the data folder
/// may be on) and the two keys it seals with, each a 32-byte file private to its owner: the host's own (<see cref="FileKeyStateProtector"/>) and the
/// Messenger helper's (<c>MESSAGES_HELPER_KEY_FILE</c>, <c>protection_linux.go</c>). Everything that is a secret in itself lives there too: the TLS
/// certificate and its key (<c>tls/</c>), the paired devices' generation, and everything the Messenger helper writes (<c>Messenger/</c>,
/// <see cref="HelperFolder"/>: whatsmeow's tables hold the device's Signal keys unsealed, and messages.db keeps who talks to whom, the contact, thread
/// and message ids and times, in clear columns). What stays on the data folder is sealed with these keys or holds
/// nothing secret, so a copy of the data folder alone is useless.
/// <para>
/// A key is made only when the keys folder holds no key and the data folder nothing sealed. A missing key beside data means the keys were lost or the
/// wrong volume is mounted, and a new key would silently orphan everything sealed under the old (the server foundation's rule, "never replace a master
/// key"), so the host refuses to start (<c>key_missing</c>). The keys carry a random id (<see cref="IdFile"/>, not derived from them), and the data folder
/// names the id it was sealed under (<see cref="MarkerFile"/>): a keys folder from another server is refused (<c>key_mismatch</c>) rather than read as
/// garbage. A keys folder that is missing, or is the data folder or overlaps it, is refused too (<c>keys_folder_missing</c>, <c>keys_in_data</c>).
/// </para>
/// <para>
/// Earlier builds kept the keys in <c>&lt;data&gt;/keys</c>, the certificate in <c>&lt;data&gt;/tls</c> and the Messenger helper's account folders and log
/// in <c>&lt;data&gt;/Messenger</c>: each is moved into the keys folder once, before anything opens it (written whole and flushed there, then removed from the data
/// folder).
/// </para>
/// </summary>
internal sealed record HostKeys(string Folder, string StateKey, string HelperKey)
{
    public const string LegacyFolder = "keys", StateFile = "state.key", HelperFile = "messenger-helper.key";
    /// <summary>The keys' id in the keys folder, and the copy of it in the data folder that says which keys sealed it.</summary>
    public const string IdFile = "key.id", MarkerFile = "key.id";
    public const string TlsFolder = "tls", MessengerFolder = "Messenger";
    /// <summary>mlink's folder when it runs in the server's container (its key, its device token and its copy of the shared rows): never read here.</summary>
    public const string TerminalFolder = "mlink";
    private const int Length = 32;

    /// <summary>Where the Messenger helper keeps everything it writes (<c>MESSAGES_HELPER_PRIVATE_ROOT</c>, storage.go).</summary>
    public string HelperFolder => Path.Combine(Folder, MessengerFolder);

    /// <summary>
    /// The keys, made where allowed and checked where they exist; or a fixed refusal word (<c>keys_folder_missing</c>, <c>keys_in_data</c>, <c>key_missing</c>,
    /// <c>key_invalid</c>, <c>key_mismatch</c>).
    /// </summary>
    public static (HostKeys? Keys, string? Refusal) Prepare(string dataRoot, string keysRoot, Action<string> log)
    {
        try
        {
            dataRoot = Path.GetFullPath(dataRoot);
            keysRoot = Path.GetFullPath(keysRoot);
            // Never made here: a missing folder is a volume that was not mounted, and keys made in the container's own layer would vanish with it.
            if (!Directory.Exists(keysRoot)) return (null, "keys_folder_missing");
            if (Overlaps(dataRoot, keysRoot)) return (null, "keys_in_data");
            MoveFromData(dataRoot, keysRoot, log);
            // Asked before either key is made, so the first key made never makes the folders look "not fresh" to the second.
            var fresh = NothingSealed(dataRoot) && NoKeys(keysRoot);
            var keys = new HostKeys(keysRoot, Path.Combine(keysRoot, StateFile), Path.Combine(keysRoot, HelperFile));
            foreach (var (path, name) in new[] { (keys.StateKey, "state"), (keys.HelperKey, "helper") })
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (!Valid(path, Length)) return (null, "key_invalid");
                    continue;
                }
                if (!fresh) return (null, "key_missing");
                Create(path, RandomNumberGenerator.GetBytes(Length));
                if (!Valid(path, Length)) return (null, "key_invalid");
                log($"server key created name={name}");
            }
            // The id: made with the keys (or for keys from before it), and written into the data folder the first time the two meet.
            var idPath = Path.Combine(keysRoot, IdFile);
            if (!File.Exists(idPath))
            {
                Create(idPath, System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))));
                log("server key id created");
            }
            var id = ReadId(idPath);
            if (id is null) return (null, "key_invalid");
            var markerPath = Path.Combine(dataRoot, MarkerFile);
            if (!File.Exists(markerPath)) Create(markerPath, System.Text.Encoding.ASCII.GetBytes(id));
            else if (ReadId(markerPath) is not { } marked || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(marked), System.Text.Encoding.ASCII.GetBytes(id)))
                return (null, "key_mismatch");
            return (keys, null);
        }
        // The kind of failure only: its message names paths.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return (null, $"key_invalid error={failure.GetType().Name}");
        }
    }

    // The same folder, one inside the other by path, or one inside the other through a mount or a link that paths cannot show (the same host folder
    // given as both volumes): a file made in the keys folder is looked for in the data folder's tree.
    private static bool Overlaps(string dataRoot, string keysRoot)
    {
        static string Slash(string path) => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Slash(keysRoot).StartsWith(Slash(dataRoot), comparison) || Slash(dataRoot).StartsWith(Slash(keysRoot), comparison)) return true;
        var probe = ".keys-probe-" + Guid.NewGuid().ToString("N");
        var probePath = Path.Combine(keysRoot, probe);
        using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            var seen = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dataRoot, "*", options))
            {
                if (Path.GetFileName(entry) == probe) return true;
                // Bounded: a data folder is a handful of stores; one this large is not walked to the end on every start.
                if (++seen > 50_000) break;
            }
        }
        return false;
    }

    // What a host has written in its data folder before it has sealed anything: its log, its lease, its run marker. Anything else may hold sealed data
    // (the key id marker above all), so a folder holding anything else is not fresh: an unknown name counts as sealed.
    private static bool NothingSealed(string root)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "server.log", LogRotation.PreviousPath("server.log"), HostLease.FileName,
            "server-run.marker", RunMarker.VersionPath("server-run.marker"),
        };
        return Directory.EnumerateFileSystemEntries(root).All(entry => allowed.Contains(Path.GetFileName(entry)));
    }

    // A keys folder with no key in it: empty, or holding only what a first start that stopped part-way leaves (one key, or temporaries), and mlink's own
    // folder (MlinkPaths, docker exec ... mlink in the container), whose key seals only mlink's files and was never one of the server's.
    private static bool NoKeys(string keysRoot)
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { StateFile, HelperFile, StateFile + ".new", HelperFile + ".new", IdFile + ".new", TerminalFolder };
        return Directory.EnumerateFileSystemEntries(keysRoot).All(entry => known.Contains(Path.GetFileName(entry)));
    }

    // The earlier layout's keys, certificate and device stores, moved into the keys folder. Each goes only where nothing is yet; one that is in both
    // places stays in both, and the helper then refuses to open either (a device store still in the data folder), so nothing is chosen silently.
    private static void MoveFromData(string dataRoot, string keysRoot, Action<string> log)
    {
        var legacy = Path.Combine(dataRoot, LegacyFolder);
        foreach (var (file, name) in new[] { (StateFile, "state"), (HelperFile, "helper") })
            if (MoveFile(Path.Combine(legacy, file), Path.Combine(keysRoot, file))) log($"server key moved name={name}");
        if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any(entry => !entry.EndsWith(".new", StringComparison.Ordinal)))
        {
            foreach (var temporary in Directory.EnumerateFiles(legacy)) File.Delete(temporary);
            Directory.Delete(legacy);
        }
        var tls = Path.Combine(dataRoot, TlsFolder);
        if (Directory.Exists(tls) && !Directory.Exists(Path.Combine(keysRoot, TlsFolder)))
        {
            // The key first, as the certificate's own writer does: a stop between the two leaves a key with no certificate, which is made again.
            foreach (var file in new[] { "server.key", "server.crt" })
                MoveFile(Path.Combine(tls, file), Path.Combine(keysRoot, TlsFolder, file));
            if (!Directory.EnumerateFileSystemEntries(tls).Any()) Directory.Delete(tls);
            log("server tls moved");
        }
        // Both: the keys folder's is the one served; the old one is left for the owner to delete, and said every start until then.
        else if (Directory.Exists(tls)) log("server tls left_in_data");
        // Everything the Messenger helper wrote: each account's folder (messages.db, e2ee.db, an export copy) and its log. The .NET side's own files
        // in Messenger/ (the sealed session and chat list, the encrypted.on flag, its log) stay on the data folder.
        var helperData = Path.Combine(dataRoot, MessengerFolder);
        var helperKeys = Path.Combine(keysRoot, MessengerFolder);
        foreach (var name in new[] { "helper.log", LogRotation.PreviousPath("helper.log") }) MoveFile(Path.Combine(helperData, name), Path.Combine(helperKeys, name));
        var accounts = Path.Combine(helperData, "accounts");
        if (!Directory.Exists(accounts)) return;
        int moved = 0, left = 0;
        foreach (var account in Directory.EnumerateDirectories(accounts))
        {
            var target = Path.Combine(helperKeys, "accounts", Path.GetFileName(account));
            // A journal belongs to its database: moved first, so a store is never found without the rollback it needs.
            var files = Directory.EnumerateFiles(account).OrderBy(file => file.EndsWith("-journal", StringComparison.Ordinal) || file.EndsWith("-wal", StringComparison.Ordinal)
                || file.EndsWith("-shm", StringComparison.Ordinal) ? 0 : 1).ToList();
            foreach (var file in files)
            {
                if (MoveFile(file, Path.Combine(target, Path.GetFileName(file)))) moved++;
                else if (File.Exists(file)) left++;
            }
            if (!Directory.EnumerateFileSystemEntries(account).Any()) Directory.Delete(account);
        }
        if (!Directory.EnumerateFileSystemEntries(accounts).Any()) Directory.Delete(accounts);
        if (moved > 0) log($"server helper store moved files={moved}");
        // In both places: the helper opens neither (storage.go), so nothing is chosen silently. Said every start until the owner settles it.
        if (left > 0) log($"server helper store left_in_data files={left}");
    }

    // Written whole and flushed under a temporary name in the keys folder, moved into place, and only then removed from the data folder: a stop at any
    // point leaves the file complete in at least one of the two.
    private static bool MoveFile(string source, string target)
    {
        if (!File.Exists(source) || File.Exists(target)) return false;
        var folder = Path.GetDirectoryName(target)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = target + ".moving";
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var to = new FileStream(temporary, options))
        {
            from.CopyTo(to);
            to.Flush(flushToDisk: true);
        }
        File.Move(temporary, target, overwrite: false);
        File.Delete(source);
        return true;
    }

    // Written whole under a temporary name, created private, then moved into place: the file is either complete or absent. A temporary left by a stop
    // part-way was never a key anything sealed with.
    private static void Create(string path, byte[] content)
    {
        var temporary = path + ".new";
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var file = new FileStream(temporary, options);
            file.Write(content);
            file.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
        File.Move(temporary, path, overwrite: false);
    }

    private static string? ReadId(string path) =>
        Valid(path, 32) && File.ReadAllText(path) is { Length: 32 } text && text.All(char.IsAsciiHexDigitLower) ? text : null;

    // A regular file of exactly the given size that only its owner can read or write. Checked here so a bad key stops the start rather than the first
    // sign-in. The bytes are never kept.
    private static bool Valid(string path, int length)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file.SafeFileHandle) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) return false;
            return file.Length == length;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }
}
