using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MessageLink.App.Services;
using MessageLink.Core.Services;

namespace MessageLink.Terminal.Connection;

/// <summary>Where mlink keeps its own files.</summary>
public static class MlinkPaths
{
    /// <summary>The harness's (and a development copy's) override of <see cref="Folder"/>.</summary>
    public const string HomeVariable = "MLINK_HOME";

    /// <summary>The folder's name under the user's configuration folder off Windows.</summary>
    public const string LinuxFolderName = "message-link";

    /// <summary>The server's keys folder (<c>/keys</c> in its container image), and mlink's folder's name in it.</summary>
    public const string KeysVariable = "MESSAGES_KEYS", KeysFolderName = "mlink";

    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>mlink's own folder for this user, made (private to this user) if it is not there.</summary>
    public static string EnsureFolder() => EnsureFolder(DefaultFolder());

    /// <summary>mlink's own folder for this user, as this process's environment names it (<see cref="Folder"/>), made or not.</summary>
    public static string DefaultFolder() => Folder(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), () => AppPaths.Combine("Terminal"));

    /// <summary>
    /// The folder: <see cref="HomeVariable"/> when set; on Windows the app's data folder's <c>Terminal</c> (<paramref name="windowsFolder"/>), so a development copy's
    /// mlink keeps to that copy's folder; elsewhere, inside the server's own container (<see cref="KeysVariable"/> names a folder that is there), the keys
    /// folder's <c>mlink</c>; else <c>$XDG_CONFIG_HOME/message-link</c>, else <c>~/.config/message-link</c>.
    /// </summary>
    /// <param name="exists">Whether a folder is there; <see cref="Directory.Exists"/> unless a check gives its own.</param>
    public static string Folder(Func<string, string?> environment, bool windows, Func<string> windowsFolder, Func<string, bool>? exists = null)
    {
        if (environment(HomeVariable) is { Length: > 0 } home) return Path.GetFullPath(home);
        if (windows) return windowsFolder();
        // The container's HOME is the data folder, an appdata share. mlink's key, its device token and its copy of the server's rows belong beside the server's
        // own keys, in the volume no share reaches, rather than on it.
        if (environment(KeysVariable) is { Length: > 0 } keys && Path.IsPathRooted(keys) && (exists ?? Directory.Exists)(keys)) return Path.Combine(keys, KeysFolderName);
        // The specification ignores a relative XDG_CONFIG_HOME.
        if (environment("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)) return Path.Combine(config, LinuxFolderName);
        var user = environment("HOME") is { Length: > 0 } given ? given : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(user, ".config", LinuxFolderName);
    }

    /// <summary>Makes <paramref name="folder"/> if it is not there; off Windows it is made (or set) 0700, and every file this process makes from now on 0600.</summary>
    public static string EnsureFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA% is already this user's alone.
            Directory.CreateDirectory(folder);
            return folder;
        }
        Directory.CreateDirectory(folder, PrivateFolder);
        File.SetUnixFileMode(folder, PrivateFolder);
        RestrictNewFiles();
        return folder;
    }

    /// <summary>Off Windows, sets <paramref name="file"/> to 0600; nothing on Windows, where the folder's access list already keeps it to this user.</summary>
    public static void MakePrivate(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file)) File.SetUnixFileMode(file, PrivateFile);
    }

    /// <summary>Writes <paramref name="bytes"/> whole or not at all: beside the file, private to this user from its first byte, then moved over it.</summary>
    public static async Task WritePrivateAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }

    // Files other parts make (the log, the replica, a token written through ClientToken) are made 0600 too, without each having to ask: the folder is
    // already 0700, so this only keeps a copied file from carrying looser bits out.
    // By its file's real name: a slim image has no libc.so (the development link) for a plain "libc" to find.
    private static void RestrictNewFiles()
    {
        foreach (var name in new[] { "libc.so.6", "libc.musl-x86_64.so.1", "libc" })
        {
            if (!NativeLibrary.TryLoad(name, out var library)) continue;
            if (NativeLibrary.TryGetExport(library, "umask", out var export))
            {
                _ = Marshal.GetDelegateForFunctionPointer<Umask>(export)(0x3F);
                return;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint Umask(uint mask);
}

/// <summary>The protector mlink's own files are sealed with.</summary>
public static class MlinkProtector
{
    /// <summary>The key file off Windows, in mlink's folder.</summary>
    public const string KeyFileName = "mlink.key";

    /// <summary>
    /// On Windows, DPAPI-NG for this user (<see cref="DpapiNgProtector"/>), the very protection the app seals its own files with, so mlink reads the token the PC's
    /// server keeps for its clients. Elsewhere, AES-GCM under a key file only this user can read, made here the first time.
    /// </summary>
    public static IStateProtector Create(string folder)
    {
        if (OperatingSystem.IsWindows()) return new DpapiNgProtector();
        return new FileKeyStateProtector(EnsureKey(folder));
    }

    /// <summary>
    /// The key file in <paramref name="folder"/>, made if it is not there: written whole and flushed under a name of its own (0600 off Windows), then moved into
    /// place without replacing anything, so the key is complete or absent. Another mlink that put its key there first wins, and its key is the one used. An
    /// empty file (an earlier build stopped between making the file and writing it) never sealed anything, and is made again.
    /// </summary>
    public static string EnsureKey(string folder)
    {
        var key = Path.Combine(folder, KeyFileName);
        var found = new FileInfo(key);
        if (found.Exists && found.Length > 0) return key;
        if (found.Exists)
        {
            // Still empty: another mlink may have just put a whole key in its place.
            try { if (new FileInfo(key) is { Exists: true, Length: 0 }) File.Delete(key); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        var temporary = key + "." + Guid.NewGuid().ToString("N")[..8] + ".new";
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, key, overwrite: false); }
            // Put there by another mlink at the same moment: that one is used.
            catch (IOException) when (new FileInfo(key) is { Exists: true, Length: > 0 }) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try { File.Delete(temporary); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        return key;
    }
}

/// <summary>Finds the server mlink was joined to, or this machine's own, and connects.</summary>
public static class ServerConnector
{
    /// <summary>The server to talk to, connected or connecting; null when there is none, after saying why and what to do on <see cref="MlinkContext.Out"/>.</summary>
    public static Task<ServerLink?> ConnectAsync(MlinkContext context, CancellationToken cancellationToken) =>
        ConnectAsync(context, ServerSearch.ForThisMachine(context.Protector), cancellationToken);

    /// <summary><see cref="ConnectAsync(MlinkContext, CancellationToken)"/> over the places <paramref name="search"/> names.</summary>
    public static Task<ServerLink?> ConnectAsync(MlinkContext context, ServerSearch search, CancellationToken cancellationToken) =>
        Connecting.ConnectAsync(context, search, cancellationToken);
}

/// <summary><c>mlink join &lt;link or address&gt;</c> and <c>mlink leave</c>.</summary>
public static class JoinCommand
{
    /// <returns>The process exit code: 0 joined.</returns>
    public static Task<int> RunAsync(MlinkContext context, string target, CancellationToken cancellationToken) =>
        RunAsync(context, target, JoinOptions.ForThisMachine(), cancellationToken);

    /// <returns>The process exit code: 0 joined, 1 not joined, 2 not a link or an address.</returns>
    public static async Task<int> RunAsync(MlinkContext context, string target, JoinOptions options, CancellationToken cancellationToken)
    {
        var outcome = await Joining.RunAsync(context, target, options, cancellationToken).ConfigureAwait(false);
        return outcome switch { Joining.Result.Joined => 0, Joining.Result.NotUnderstood => 2, _ => 1 };
    }

    /// <summary>Forgets the server <c>mlink join</c> saved (its token and pinned certificate), so plain <c>mlink</c> goes back to this machine's own.</summary>
    public static async Task<int> LeaveAsync(MlinkContext context)
    {
        var store = new JoinedServerStore(context.Folder, context.Protector);
        if (!store.Exists)
        {
            context.Out.WriteLine("This terminal hasn't joined a server.");
            context.Log("mlink leave outcome=none");
            return 0;
        }
        var saved = await store.ReadAsync().ConfigureAwait(false);
        try { store.Forget(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            context.Out.WriteLine("This terminal couldn't forget the server. Close any other mlink, then try again.");
            context.Log($"mlink leave outcome=failed type={failure.GetType().Name}");
            return 1;
        }
        // What this terminal kept of that server's rows (names, saved messages) goes with it.
        if (saved is not null) Joining.ForgetReplica(context, saved.ServerId);
        context.Out.WriteLine(saved is null
            ? "Forgot the saved server. Remove this terminal on the server's page too."
            : $"Forgot {saved.Name}. Remove this terminal on the server's page too.");
        context.Log("mlink leave outcome=forgot" + (saved is null ? "" : $" server={saved.Tag}"));
        return 0;
    }
}
