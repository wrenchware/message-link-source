using System.Diagnostics;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Host;

namespace NativeBeeper.Server;

// MessagesServer.exe (SWITCHBOARD.md stage 3, step 3.1): hosts the backends for one data root. One per root, however it was started: the lease
// file decides, the named mutex is for the installer. Nothing connects unless it was started with --restore.
public static class Program
{
    private static string RunMarkerPath => AppPaths.Combine("server-run.marker");

    [STAThread]
    private static int Main(string[] args)
    {
        // The reference points for the start-up steps logged below: when Main was entered, and the time since.
        var entered = DateTime.Now;
        var prelude = Stopwatch.StartNew();
        var options = ServerOptions.Parse(args);
        // Hidden: the browser alone on a scratch folder in %TEMP% (BrowserSelfTest names the root itself); no lease, log, store or account.
        if (args.Contains(BrowserSelfTest.Flag)) return BrowserSelfTest.Run();
        // Before the lease, the log and the marker: a quit is a visitor, and must not replace the log of the server it is asking.
        if (options.Quit) return Quit();

        // The real data (the installed app's accounts) is served only once the window has made this PC's own server its host (SWITCHBOARD.md 10.7, the lift of
        // 3.2b, owner D13 2026-10-30): the window takes HostLease whenever it hosts, so the lease keeps the two apart, and the move marker says the switch was made
        // (MoveMarker.ServerMayOpen). While the switch is under way only the window's own offline start for the move may open it; a start that connects accounts (a
        // restore, the sign-in Run value) waits for the switch to be confirmed. The real data is the default root, and also that folder named in NATIVEBEEPER_DATA_ROOT
        // (review, 2026-10-08), or a junction or link to it, or to a folder above it (IsRealData compares the paths as the file system resolves them, and one it cannot
        // resolve is the real data). On a root that was never switched it reads the marker and nothing else, writes nothing, not even its refusal: that folder is the
        // installed app's, and only the exit code says why. A harness check fails if this ever opens a root without the app taking HostLease.
        if (!AppPaths.IsScratch && !MoveMarker.ServerMayOpen(AppPaths.DataRoot, options.Connects)) return 3;
        // Before the first line of any log, the refusals included: the app's choice to record nothing is the server's too.
        ServerPreferences.Apply();
        // A window that hosts holds the host lease (3.2b), so the lease below refuses then. This older check stays for a window that does not take the
        // lease (a build from before 3.2b), except for the window that started this server as its owner: that one is a client (NATIVEBEEPER_PROTOCOL=local)
        // and holds the data mutex as every window does.
        // A root that runs its own server has a window that is always its client (it never hosts there without the lease, which this start then finds held), and
        // both are started at sign-in, in either order.
        if (AppRunning() && !OwnerIsWindow(options.OwnerPid) && !MoveMarker.RunsOwnServer(MoveMarker.Read(AppPaths.DataRoot))) return Refused("app_running");
        // Before the log: a refused start must not replace the log of the server that holds the lease.
        using var lease = HostLease.TryAcquire(AppPaths.DataRoot);
        if (lease is null) return Refused("host_locked");
        using var mutex = new Mutex(false, ServerShell.MutexName);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return Refused("mutex_held");
        // This process is the server of this root now; what its start needs and that touches no data runs beside the window's making (SWITCHBOARD.md 3.1d).
        ServerWarmUp.Start();

        // Its own files, so that a server run never moves the window's logs to .previous or adds to them.
        AppLog.UseFile("server.log", "server log opened");
        Diagnostics.ProviderLogFile = "server.log";
        var previousExit = RunMarker.Begin(RunMarkerPath);
        HelperConnection.DataRoot = AppPaths.DataRoot;
        HelperConnection.Trace = AppLog.Write;
        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            // Code locations only, as the app's crash record: an exception's message can carry private content.
            AppLog.WriteNow($"unhandled exception type={(error.ExceptionObject as Exception)?.GetType().Name ?? "unknown"} thread=worker");
            FlushLogs();
        };
        AppLog.Write($"server started version={typeof(Program).Assembly.GetName().Version?.ToString(3)} data_root={(AppPaths.IsIsolated ? "given" : "default")} lease=ok " +
            $"previous_exit={previousExit} restore={options.Restore} background={options.Background}");
        if (options.OwnerPid is { } owner) AppLog.Write($"server owner pid={owner}");
        // runtime: the process's start to Main (the .NET host and runtime, which nothing here can shorten); prelude: Main to here (arguments, lease, mutex, the log file).
        AppLog.Write($"server compose step=prelude ms={prelude.ElapsedMilliseconds}");
        // Asked of Windows on the pool: the process's start time costs a few milliseconds the start does not need to wait for.
        _ = Task.Run(() => AppLog.Write($"server compose step=runtime ms={(long)(entered - Process.GetCurrentProcess().StartTime).TotalMilliseconds}"));

        var run = new ServerRun(options, previousExit);
        var shell = new ServerShell(run, AppLog.Write);
        var code = shell.Run(() => run.Start(shell));
        FlushLogs();
        GC.KeepAlive(mutex);
        // A start that could not take the pipe is a refusal like the others (3).
        return code != 0 ? code : run.ExitCode;
    }

    // Asks the server of this data root to quit and waits for it to end. 0 it ended, 1 none is running, 2 it is still running (it refused, or is slow).
    // The wait is the stop's own worst case (ServerStopBounds.WorstCase), so a stop that is working through its bounds is not reported as stuck while it
    // is still inside them; a refusal does not wait at all (the server answers the command with it), so a caller that asks again every few seconds
    // (Stop-Messages.ps1) gets to.
    private static int Quit()
    {
        var window = Native.FindWindowW(ServerShell.WindowClassName, ServerShell.WindowTitle);
        // A server that holds its mutex but has no window yet is still starting: the quit is not "none running".
        for (var waited = 0; window == 0 && waited < 5000 && ServerStarting(); waited += 100)
        {
            Thread.Sleep(100);
            window = Native.FindWindowW(ServerShell.WindowClassName, ServerShell.WindowTitle);
        }
        if (window == 0) return 1;
        Native.GetWindowThreadProcessId(window, out var id);
        Process? server = null;
        try { server = Process.GetProcessById((int)id); } catch (ArgumentException) { }
        using (server)
        {
            // Sent, so the answer comes back: taken (the stop is under way, or was) or refused (something is still being sent). A window that does not
            // answer in time is a server that is busy, not one that is gone.
            if (Native.SendMessageTimeoutW(window, ServerShell.QuitMessage, 0, 0, Native.SMTO_ABORTIFHUNG, 5000, out var answer) == 0)
                return server is null || server.HasExited ? 1 : 2;
            if (answer == ServerShell.QuitRefused) return 2;
            return server is null || server.WaitForExit(ServerStopBounds.WorstCase) ? 0 : 2;
        }
    }

    // Looked at, never taken: a probe that held the lease or the mutex for an instant could make a server that is starting refuse itself.
    private static bool ServerStarting()
    {
        try
        {
            if (!Mutex.TryOpenExisting(ServerShell.MutexName, out var held)) return false;
            held.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    // The owner named on the command line is a running Messages window: the same program name the app has, alive now.
    private static bool OwnerIsWindow(int? pid)
    {
        if (pid is not { } id) return false;
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && string.Equals(process.ProcessName, "Messages", StringComparison.OrdinalIgnoreCase);
        }
        // Gone, never there, or not ours to look at (an access refusal is a SystemException too).
        catch (SystemException) { return false; }
    }

    private static bool AppRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(AppPaths.AppDataLockName, out var held)) return false;
            held.Dispose();
            return true;
        }
        // Something holds it that this user cannot open: not ours to share the root with.
        catch (UnauthorizedAccessException) { return true; }
    }

    // Appended, never rotated: the log is the running server's.
    private static int Refused(string reason)
    {
        if (!Diagnostics.Recording) return 3;
        try { File.AppendAllText(AppPaths.Combine("server.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} server start refused reason={reason}\n"); }
        catch (Exception) { }
        return 3;
    }

    internal static void FlushLogs()
    {
        AppLog.Flush();
        GoogleMessagesLog.Flush();
        MessengerLog.Flush();
        GoogleVoiceLog.Flush();
        InstagramLog.Flush();
    }
}
