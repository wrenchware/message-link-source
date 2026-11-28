using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MessageLink.App.Services;
using MessageLink.Core.Services;

namespace MessageLink.Terminal.Connection;

/// <summary>What starting <c>MessageLinkServer.exe</c> came to. <paramref name="Error"/> is one word, never a path or a message.</summary>
/// <param name="Exited">Completes with the exit code if the process ends; null when nothing was started or it cannot be watched.</param>
public sealed record ServerStart(bool Started, string? Error = null, Task<int>? Exited = null);

/// <summary>
/// Where <see cref="ServerConnector"/> looks for a server when <c>mlink join</c> saved none. <see cref="ForThisMachine"/> is this machine's; a check names scratch
/// folders, its own pipe and its own start, because the app's data folder is read once per process (<see cref="AppPaths"/>).
/// </summary>
public sealed record ServerSearch
{
    /// <summary>Windows looks for this PC's own server over the pipe; elsewhere a server on 127.0.0.1 at <see cref="LoopbackPort"/>.</summary>
    public bool Windows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>The app's data folder (Windows): its <c>Server</c> folder says whether it runs its own server, and holds that server's client token.</summary>
    public string? DataRoot { get; init; }

    /// <summary>What tells this data folder's pipe and server lock apart from another copy's (<see cref="AppPaths.InstanceSuffix"/>).</summary>
    public string PipeSuffix { get; init; } = "";

    /// <summary>The mutex a running server holds for this data folder; null to go by the pipe alone.</summary>
    public string? ServerLockName { get; init; }

    /// <summary>Whether a server is running for this data folder now; the lock's when left out.</summary>
    public Func<bool>? ServerRunning { get; init; }

    /// <summary>Starts this PC's own server when none is running; null when mlink may not (a Store copy's, or no server to start).</summary>
    public Func<ServerStart>? StartServer { get; init; }

    /// <summary>The protector of the app's own files (Windows: the same DPAPI-NG mlink uses); mlink's own when left out.</summary>
    public IStateProtector? AppProtector { get; init; }

    /// <summary>The port a server answers on inside its own container (<c>MESSAGES_PORT</c>, else 8787).</summary>
    public int LoopbackPort { get; init; } = Protocol.NetworkEndpoint.DefaultPort;

    /// <summary>How long the first connection may take before mlink says why it could not connect.</summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a server mlink started (or found starting) may take to answer.</summary>
    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long one try of the pipe waits for it to be there.</summary>
    public TimeSpan PipeOpenWait { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>What <c>mlink join</c> calls this terminal when it asks a server to let it in.</summary>
    public JoinOptions Join { get; init; } = JoinOptions.ForThisMachine();

    /// <summary>
    /// This machine's: the app's data folder as the app would choose it (a development copy's when <c>MESSAGELINK_DATA_ROOT</c> is set), or, when that holds no
    /// server and mlink is not itself in a package, the folder of the app installed from the Microsoft Store, with that copy's own pipe and lock.
    /// </summary>
    public static ServerSearch ForThisMachine(IStateProtector protector)
    {
        if (!OperatingSystem.IsWindows())
            return new ServerSearch { Windows = false, LoopbackPort = PortFrom(Environment.GetEnvironmentVariable("MESSAGES_PORT")), AppProtector = protector };
        var root = AppPaths.DataRoot;
        if (!Directory.Exists(Path.Combine(root, ServerFolder)) && !AppPaths.IsPackaged && !AppPaths.IsIsolated && StoreRoot() is { } store)
        {
            var suffix = Tag("PACKAGE:" + store.Family.ToUpperInvariant());
            // Not started from here: the server beside this mlink is the unpackaged install's, not the Store copy's.
            return new ServerSearch { DataRoot = store.Root, PipeSuffix = suffix, ServerLockName = LockPrefix + suffix, AppProtector = protector };
        }
        return new ServerSearch
        {
            DataRoot = root, PipeSuffix = AppPaths.InstanceSuffix, ServerLockName = AppPaths.ServerLockName, AppProtector = protector,
            StartServer = () => LocalServerStart.StartBeside(AppContext.BaseDirectory),
        };
    }

    internal const string ServerFolder = "Server";
    private const string LockPrefix = @"Local\Messages.Server";

    /// <summary>The Microsoft Store package's identity name (installer\Build-StorePackage.ps1, the Partner Center reservation).</summary>
    public const string StoreIdentityName = "Wrenchware.Switchboard-MessagesforWindows";

    internal static int PortFrom(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : Protocol.NetworkEndpoint.DefaultPort;

    // The Store copy's folder, when one of its LocalState folders has a Server folder in it.
    private static (string Family, string Root)? StoreRoot()
    {
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (var folder in Directory.EnumerateDirectories(packages, StoreIdentityName + "_*"))
            {
                var local = Path.Combine(folder, "LocalState");
                if (Directory.Exists(Path.Combine(local, ServerFolder))) return (Path.GetFileName(folder), local);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // As AppPaths tags a package's (or a development copy's) named objects.
    internal static string Tag(string tagged) => "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tagged)))[..12];

    internal bool LockHeld()
    {
        if (ServerRunning is not null) return ServerRunning();
        if (ServerLockName is null || !OperatingSystem.IsWindows()) return false;
        try
        {
            if (!Mutex.TryOpenExisting(ServerLockName, out var held)) return false;
            held.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }
}

/// <summary>What <c>mlink join</c> calls this terminal on the server's page.</summary>
public sealed record JoinOptions(string DeviceName)
{
    /// <summary>"&lt;machine name&gt; terminal"; inside the server's container the machine name is the container's host name, as it is.</summary>
    public static JoinOptions ForThisMachine()
    {
        string machine;
        try { machine = Environment.MachineName; }
        catch (InvalidOperationException) { machine = OperatingSystem.IsWindows() ? "Windows" : "Linux"; }
        return new($"{machine} terminal");
    }

    /// <summary>How long a connection to the server may take to open.</summary>
    public TimeSpan ConnectTimeout { get; init; } = Protocol.NetworkEndpoint.ConnectTimeout;
}
