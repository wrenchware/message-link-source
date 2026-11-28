using System.Diagnostics;
using MessageLink.App.Services;
using MessageLink.Presentation.Services;
using MessageLink.WindowsPlatform;

namespace MessageLink.Server.Windows;

/// <summary>
/// The server's own Message Link icon (owner, 2026-11-22): one icon while anything runs. A Message Link window open on this data root owns the icon; while none is,
/// this one is in the notification area, saying that the server runs and how many accounts are connected, with Open Message Link, Manage server and Quit Message
/// Link Server. It lives on the shell's thread, whose loop pumps its window. Whether a window is open comes from <see cref="TrayPresence"/>: looked for at the start,
/// when the app says its window was made, and when the app it watched has ended.
/// </summary>
internal sealed class ServerTray : IDisposable
{
    // The tooltip's count is looked at this often while the icon shows; the shell is told only when it changed.
    private static readonly TimeSpan TipInterval = TimeSpan.FromSeconds(5);

    private readonly IStaQueue sta;
    private readonly Func<int?> connectedAccounts;
    private readonly Func<bool> pageReady;
    private readonly Action quit;
    private readonly Action<string> log;
    private readonly TrayIcon? icon;
    private readonly Timer tipTimer;
    private int? watchedApp;
    private bool stopping, disposed;

    /// <param name="connectedAccounts">How many accounts are connected now; null before the server knows.</param>
    /// <param name="quit">Quit Message Link Server: the stop a <c>--quit</c> makes.</param>
    /// <param name="pageReady">Whether the server's page is up: Manage server is offered only then, so it never opens a page that is not there yet.</param>
    public ServerTray(IStaQueue sta, Func<int?> connectedAccounts, Func<bool> pageReady, Action quit, Action<string> log)
    {
        this.sta = sta; this.connectedAccounts = connectedAccounts; this.pageReady = pageReady; this.quit = quit; this.log = log;
        try
        {
            icon = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), null, TrayIcon.ServerWindowClass, "Message Link Server tray" + AppPaths.InstanceSuffix,
                TrayMenuModel.ServerTip(null), show: false)
            { Fallback = TrayMenuModel.ServerMenuFor(pageReady: false), MenuProvider = () => TrayMenuModel.ServerMenuFor(SafeReady()) };
            icon.Command += Chosen;
            icon.Open += () => OpenApp(null, "click");
        }
        catch (Exception failure) { log($"server tray unavailable error={failure.GetType().Name}"); }
        tipTimer = new Timer(_ => sta.TryEnqueue(UpdateTip), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Look("start");
    }

    /// <summary>A Message Link window said it was made (<see cref="TrayPresence.AppArrivedMessage"/>): looked for again, on the shell's thread.</summary>
    public void AppArrived() => Look("app_arrived");

    /// <summary>The server is stopping: the icon goes at once and never comes back in this run.</summary>
    public void Stopping()
    {
        if (stopping) return;
        stopping = true;
        tipTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        icon?.Hide();
    }

    // Decides who owns the icon now. Called on the shell's thread only.
    private void Look(string reason)
    {
        if (disposed || stopping || icon is null) return;
        var app = TrayPresence.FindApp();
        if (app is { } pid)
        {
            icon.Hide();
            tipTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (watchedApp != pid) Watch(pid);
            log($"server tray owner=app reason={reason}");
            return;
        }
        watchedApp = null;
        UpdateTip();
        icon.Show();
        tipTimer.Change(TipInterval, TipInterval);
        log($"server tray owner=server reason={reason} added={icon.Added}");
    }

    // The app's process, waited on off this thread; its end brings the look back here. One that cannot be opened leaves the icon to the app until the next
    // window says it was made: looking again at once could go round for as long as that window stays.
    private void Watch(int pid)
    {
        watchedApp = pid;
        Process process;
        try { process = Process.GetProcessById(pid); }
        // Ended since its window was found: its window has gone with it, so a look now finds the next one, or none.
        catch (ArgumentException) { sta.TryEnqueue(() => { if (watchedApp == pid) watchedApp = null; Look("app_ended"); }); return; }
        catch (Exception failure) { log($"server tray watch failed type={failure.GetType().Name}"); return; }
        // Opened after its window was found: if the app ended in between and its id went to another process, its window is gone too, so the window is asked
        // again now that the process is held (a process held open keeps its id).
        if (TrayPresence.FindApp() != pid)
        {
            process.Dispose();
            sta.TryEnqueue(() => { if (watchedApp == pid) watchedApp = null; Look("app_changed"); });
            return;
        }
        _ = Task.Run(async () =>
        {
            using (process)
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            sta.TryEnqueue(() => { if (watchedApp == pid) watchedApp = null; Look("app_ended"); });
        });
    }

    private bool SafeReady()
    {
        try { return pageReady(); }
        catch (Exception) { return false; }
    }

    private void UpdateTip()
    {
        if (disposed || stopping || icon is null) return;
        int? count;
        try { count = connectedAccounts(); }
        catch (Exception) { count = null; }
        icon.SetTip(TrayMenuModel.ServerTip(count));
    }

    private void Chosen(TrayMenuItem item)
    {
        if (stopping) return;
        log($"server tray command={item.Command switch { TrayCommand.Open => "open", TrayCommand.ServerPage => "server_page", TrayCommand.StopServer => "quit", _ => "other" }}");
        switch (item.Command)
        {
            case TrayCommand.Open: OpenApp(null, "menu"); break;
            case TrayCommand.ServerPage: OpenApp(LaunchCommand.ManageServerArgument, "manage"); break;
            case TrayCommand.StopServer:
                Stopping();
                quit();
                break;
        }
    }

    /// <summary>
    /// Message Link beside this program (a development server finds the development app it was started by), with this process's environment, so a development
    /// copy's data folder is the same one. A running one is brought forward through its own activation; its icon then takes this one's place.
    /// </summary>
    private void OpenApp(string? argument, string why)
    {
        var executable = AppExecutable();
        if (executable is null) { log($"server tray open outcome=missing reason={why}"); return; }
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            if (argument is not null) start.ArgumentList.Add(argument);
            using var started = Process.Start(start);
            log($"server tray open outcome={(started is null ? "failed" : "started")} reason={why}");
        }
        catch (Exception failure) { log($"server tray open outcome=failed reason={why} type={failure.GetType().Name}"); }
    }

    /// <summary>
    /// MessageLink.exe beside this program. A Debug build also takes the app named by <see cref="AppVariable"/> (the development app sets it for the server it starts,
    /// whose own folder holds no app); a Release build never reads it, so a variable cannot name another program to start.
    /// </summary>
    internal static string? AppExecutable()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "MessageLink.exe");
        if (File.Exists(beside)) return beside;
#if DEBUG
        if (Environment.GetEnvironmentVariable(AppVariable) is { Length: > 0 } given && string.Equals(Path.GetFileName(given), "MessageLink.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(given)) return given;
#endif
        return null;
    }

    public const string AppVariable = "MESSAGELINK_APP_EXE";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tipTimer.Dispose();
        icon?.Dispose();
    }
}
