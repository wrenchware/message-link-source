using System.Diagnostics;
using System.Runtime.InteropServices;
using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;

namespace NativeBeeper.Server;

/// <summary>
/// <c>MessagesServer.exe --browser-selftest</c> (hidden, SWITCHBOARD.md 3.1c): the Google renewal's browser run once through the server's own STA
/// thread against an empty, fresh profile, and the outcome printed. An empty profile has no sign-in, so the right answer is
/// <c>missing_cookies</c>. It names a new folder under %TEMP% as the data root before anything reads a path, refuses to go on if the root it ends
/// up with is not there, takes no lease and opens no store, provider, helper or account, and shows no window. The real profiles are never opened.
/// </summary>
internal static class BrowserSelfTest
{
    public const string Flag = "--browser-selftest";

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>Exit code 0 when the browser answered <c>missing_cookies</c> on the STA thread, 1 when it answered anything else, 3 when it would not start.</summary>
    public static int Run()
    {
        // Before the first read of AppPaths (which fixes its root for the whole run), and whatever the caller's environment named.
        var root = Path.Combine(Path.GetTempPath(), "messages-browser-selftest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NATIVEBEEPER_DATA_ROOT", root);
        // A WinExe has no console of its own; this shows the answer in the one it was started from, and does nothing when output is redirected.
        AttachConsole(-1);
        if (!AppPaths.IsScratch || !AppPaths.DataRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("browser-selftest refused reason=root_not_scratch");
            return 3;
        }
        var outcome = 1;
        var shell = new ServerShell(new Lifecycle(), Console.WriteLine);
        var code = shell.Run(() => Task.Run(async () =>
        {
            try { outcome = await ExerciseAsync(shell.Sta!); }
            catch (Exception failure) { Console.WriteLine($"browser-selftest failed type={failure.GetType().Name}"); }
            finally { shell.Stop("selftest"); }
        }));
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
        Console.WriteLine($"browser-selftest exit={(code != 0 ? code : outcome)}");
        return code != 0 ? code : outcome;
    }

    private static async Task<int> ExerciseAsync(NativeBeeper.WindowsPlatform.IStaQueue sta)
    {
        // From off the thread (this is the pool): not on it, and work put there runs there, in an STA, and its awaits come back to it.
        var offThread = !sta.OnThread;
        var onThread = await sta.RunAsync(() => Task.FromResult(sta.OnThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA));
        var resumed = await sta.RunAsync(async () => { await Task.Delay(25); return sta.OnThread; });
        Console.WriteLine($"browser-selftest thread off_thread={offThread} work_on_sta={onThread} await_resumes_on_sta={resumed}");

        var profile = GoogleSignInProfile.CreateAttempt(GoogleSignInProfile.MessagesService);
        var browser = new WebViewGoogleBrowser(sta);
        var clock = Stopwatch.StartNew();
        GoogleRefreshResult refreshed;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            refreshed = await browser.RefreshAsync(GoogleSignInProfile.MessagesService, profile.Id, timeout.Token);
        var cookies = refreshed.Cookies is not null;
        refreshed.Cookies?.Clear();
        // Nothing was shown: the process has no window of its own on screen (the shell's is never made visible).
        var visible = Process.GetCurrentProcess().MainWindowHandle != 0;
        Console.WriteLine($"browser-selftest refresh outcome={(cookies ? "cookies" : refreshed.Reason)} ms={clock.ElapsedMilliseconds} window_visible={visible}");
        await browser.DeleteProfileAsync(profile.Id, GoogleSignInProfile.MessagesService);
        return offThread && onThread && resumed && !cookies && !visible && refreshed.Reason == "missing_cookies" ? 0 : 1;
    }

    // Nothing to stop, nothing to refuse: the shell is only the thread the browser runs on.
    private sealed class Lifecycle : IServerLifecycle
    {
        public string? RefuseQuit() => null;
        public Task StopAsync(string reason, ShutdownBudget budget) => Task.CompletedTask;
        public void SessionQuery() { }
        public void SessionCancel() { }
    }
}
