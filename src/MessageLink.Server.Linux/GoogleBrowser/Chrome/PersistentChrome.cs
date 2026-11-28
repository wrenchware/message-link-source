using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MessageLink.Server.Linux;

/// <summary>One running Chrome, on a display of its own.</summary>
internal interface IChromeRunning : IAsyncDisposable
{
    /// <summary>The X display number Chrome is on, once it is up (<c>.display-number</c> in the profile folder); null if it exited first or was too slow.</summary>
    Task<int?> DisplayAsync(CancellationToken cancellationToken);
    /// <summary>The file holding the display's key.</summary>
    string AuthorityFile { get; }
    /// <summary>Completes when Chrome (and so its display) has gone.</summary>
    Task Exited { get; }
    /// <summary>The exit status once it has gone (the launch script's own 70-72 when the display never came up, else Chrome's), or null.</summary>
    int? ExitCode => null;
    /// <summary>The process group Chrome and what it starts run in (the launch script's id), for reading what its processes did from /proc; null when there is none to read (the harness's).</summary>
    int? ProcessGroup => null;
    /// <summary>Asks Chrome to close itself (SIGTERM), so it writes its cookies and its state as a closing browser does.</summary>
    void Close();
    /// <summary>Ends Chrome and everything it started at once.</summary>
    void Kill();
}

/// <summary>Starts Chrome on a profile folder. The real one is <see cref="ChromeLauncher"/>; the harness gives a fake.</summary>
internal interface IChromeLauncher
{
    /// <param name="page">The page Chrome opens; null is <see cref="ChromeArguments.Page"/>. A sign-in opens Google's account chooser instead (<see cref="ChromeArguments.SignInPage"/>).</param>
    IChromeRunning Start(string profileFolder, string? page = null);
}

/// <summary>What Chrome is started with (SWITCHBOARD.md 5.4, Chrome on the server).</summary>
internal static class ChromeArguments
{
    /// <summary>The one page Chrome shows: Messages for web's configuration address, never the web app itself (loading that could start a pairing of its own).</summary>
    public const string Page = "https://messages.google.com/web/config";

    /// <summary>
    /// Where a sign-in starts: Google's account chooser, continuing to <see cref="Page"/>, as this app's own sign-in window does (GoogleSignInRules.LoginUrl). Opening <see cref="Page"/>
    /// itself showed the person only that page's data, with no way to reach a sign-in (no address bar), the first live run (2026-10-10).
    /// </summary>
    public const string SignInPage = MessageLink.App.Services.GoogleMessages.GoogleSignInRules.LoginUrl;

    /// <summary>
    /// The arguments. An ordinary Chrome on an ordinary display: no remote-debugging switch of any kind (nothing attaches, so nothing marks the page as automated, and no port or pipe exists), no
    /// switch that changes what the browser says it is, and no switch that hides anything. The profile is the given folder; passwords are kept by the browser itself (no desktop keyring in a
    /// container: <c>--password-store=basic</c>); the window is the whole display and is an application window of the one page, so the picture the person is shown is the page and nothing else. What
    /// the browser may visit, save and sync is set by its managed policy file, not here (<see cref="ChromePolicy"/>).
    /// </summary>
    public static IReadOnlyList<string> Build(string profileFolder, bool noSandbox, string? page = null)
    {
        var arguments = new List<string>
        {
            "--user-data-dir=" + profileFolder,
            "--no-first-run",
            "--no-default-browser-check",
            "--password-store=basic",
            "--ozone-platform=x11",
            "--window-position=0,0",
            "--window-size=" + ChromeLauncher.DisplaySize.Replace('x', ','),
            "--lang=en-US",
            // A container's /dev/shm is 64 MB, too small for a renderer; and there is no GPU on the virtual display.
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--mute-audio",
        };
        // Chrome's own sandbox needs user namespaces or a setuid helper, which a container with every capability dropped and no-new-privileges does not give it. Off only when the host says so.
        if (noSandbox) arguments.Add("--no-sandbox");
        // The kept Chrome is an application window of its one page. A sign-in is an ordinary window with its address bar, so the person always sees where they are (a page the
        // policy refuses says only "This page is blocked"; the live run of 2026-10-10 could not tell which page that was).
        if (page is null) arguments.Add("--app=" + Page);
        else { arguments.Add("--new-window"); arguments.Add(page); }
        return arguments;
    }

    /// <summary>Whether an argument list names any way to attach to the browser from outside.</summary>
    public static bool AllowsAttaching(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.StartsWith("--remote-debugging", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--remote-allow-origins", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--enable-automation", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The policy file that contains Chrome (SWITCHBOARD.md 5.4, Chrome on the server): Chrome is a real browser running without its own sandbox, so which sites it may load is not left to it. The file
/// is baked into the image (<c>docker/messages-server/chrome-policy.json</c>, in Chrome's managed policy folder) and Chrome is not started without it. The path is fixed: the server reads no
/// environment variable for it, so nothing the container is started with can point the check at another file (only the harness, through <see cref="LinuxServerOptions.ChromePolicyGiven"/>).
/// <para>
/// What the policy can and cannot do. <c>URLBlocklist</c> and <c>URLAllowlist</c> govern NAVIGATIONS (a page, a frame), by the most specific matching filter: the allow list names only the hosts the
/// sign-in and Messages for web's configuration page need, plus <c>messages.google.com/web/config</c> by path against a block of the rest of that host, and Facebook's and Instagram's own sites for their
/// sign-ins (SWITCHBOARD.md 10.5) with <c>instagram.com/direct</c> blocked by path against them, so Instagram's inbox never loads (the PC's window refuses it too). They do not govern what those pages then load
/// (scripts, images, fetches follow what the pages ask for), which is why the list is short rather than <c>google.com</c> and its relatives as a whole. Developer tools, remote debugging,
/// downloads, file dialogs, extensions, saved passwords, autofill and sync are off.
/// </para>
/// </summary>
internal static class ChromePolicy
{
    public const string DefaultPath = "/etc/opt/chrome/policies/managed/messages-server.json";

    // Hosts that a pattern on its own would open the whole of Google (or its user content) to.
    private static readonly string[] TooBroad = ["google.com", "googleapis.com", "googleusercontent.com", "gstatic.com", "youtube.com", "*"];

    /// <summary>True when the file is there and says the minimum: every address blocked but a short allow list, no saved passwords or autofill, no sync, no developer tools, no remote debugging, no downloads or file dialogs.</summary>
    public static bool Present(string path)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            bool False(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.False;
            bool Number(string name, int expected) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.GetInt32() == expected;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!root.TryGetProperty("URLBlocklist", out var block) || block.ValueKind != System.Text.Json.JsonValueKind.Array || !block.EnumerateArray().Any(entry => entry.GetString() == "*")) return false;
            if (!root.TryGetProperty("URLAllowlist", out var allow) || allow.ValueKind != System.Text.Json.JsonValueKind.Array || allow.GetArrayLength() == 0) return false;
            // Nothing in the allow list is a whole domain of Google's that also serves anyone's content.
            foreach (var entry in allow.EnumerateArray())
            {
                var host = (entry.GetString() ?? "*").Replace("https://", "", StringComparison.Ordinal);
                if (!host.Contains('/') && TooBroad.Contains(host, StringComparer.OrdinalIgnoreCase)) return false;
                if (!(entry.GetString() ?? "").StartsWith("https://", StringComparison.Ordinal)) return false;
            }
            return False("PasswordManagerEnabled") && False("AutofillAddressEnabled") && False("AutofillCreditCardEnabled") && False("RemoteDebuggingAllowed") && False("AllowFileSelectionDialogs")
                && root.TryGetProperty("SyncDisabled", out var sync) && sync.ValueKind == System.Text.Json.JsonValueKind.True
                && Number("DeveloperToolsAvailability", 2) && Number("DownloadRestrictions", 3) && Number("BrowserSignin", 0);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { return false; }
    }
}

/// <summary>
/// Chrome on Linux, kept running (SWITCHBOARD.md 5.4, Chrome on the server). <c>setsid</c> gives it a process group of its own, whose id is the shell's process id, and the shell starts
/// <c>Xvfb</c> on a display number Xvfb picks itself (<c>-displayfd</c>, so two runs can never race for a number), listening on no TCP port and no local socket, closed to everything without the key
/// the server wrote into the profile folder (<see cref="XAuthority"/>), runs Chrome on it as a child, and when Chrome has gone ends the display and removes the key. A SIGTERM to the shell is
/// passed on to Chrome alone, which then closes the way a person closing it does (its cookies and its state are written), and the shell ends the display after it. The browser gets none of the
/// server's environment (where the helper keys are): a home and caches inside its profile folder, a path, the display and its key, and the time zone.
/// </summary>
internal sealed class ChromeLauncher(string executable, bool noSandbox) : IChromeLauncher
{
    /// <summary>The virtual screen, and the window that fills it.</summary>
    internal const string DisplaySize = "1280x800";
    internal const string DisplayFile = ".display-number", KeyFile = ".xauth";

    // The script is one argument, never parsed with the browser's: the browser's path is $0 and its arguments "$@".
    internal const string Script =
        "exec 0</dev/null 1>/dev/null 2>/dev/null\n"
        + "fifo=./.display\n"
        + "rm -f \"$fifo\" ./" + DisplayFile + "\n"
        + "mkfifo -m 600 \"$fifo\" || exit 70\n"
        + "Xvfb -displayfd 5 -nolisten tcp -nolisten local -auth ./" + KeyFile + " -screen 0 " + DisplaySize + "x24 5>\"$fifo\" 2>./.display-error.log &\n"
        + "xvfb=$!\n"
        + "read -r number <\"$fifo\"\n"
        + "rm -f \"$fifo\"\n"
        + "case \"$number\" in \"\"|*[!0-9]*) kill \"$xvfb\" 2>/dev/null; exit 71;; esac\n"
        + "[ -S \"/tmp/.X11-unix/X$number\" ] || { kill \"$xvfb\" 2>/dev/null; exit 72; }\n"
        + "echo \"$number\" >./" + DisplayFile + ".tmp && mv ./" + DisplayFile + ".tmp ./" + DisplayFile + "\n"
        // What Chrome says about itself goes to a file in this 0700 profile folder (the keys volume), never to the server's log: a start that failed at once was
        // otherwise unexplained (the first live run, 2026-10-10), and Chrome's own lines can name pages.
        + "DISPLAY=\":$number\" \"$0\" \"$@\" 2>./.chrome-error.log &\n"
        + "chrome=$!\n"
        + "trap 'kill -TERM \"$chrome\" 2>/dev/null' TERM INT HUP\n"
        + "wait \"$chrome\"; status=$?\n"
        + "while kill -0 \"$chrome\" 2>/dev/null; do wait \"$chrome\"; status=$?; done\n"
        + "rm -f ./" + DisplayFile + "\n"
        + "kill \"$xvfb\" 2>/dev/null\n"
        + "wait \"$xvfb\" 2>/dev/null\n"
        + "rm -f ./" + KeyFile + "\n"
        + "exit \"$status\"\n";

    private const string BrowserPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>Chrome's own profile locks, links naming the host and process that last ran it.</summary>
    internal static readonly string[] LockFiles = ["SingletonLock", "SingletonCookie", "SingletonSocket"];

    // A container gets a new host name at every reinstall, so the lock an earlier container's Chrome left reads as "in use by another computer" and Chrome exits at once (code 21,
    // the live run of 2026-10-10: "delete the files SingletonLock, SingletonCookie, SingletonSocket"). Only this server runs Chrome on these folders, one Chrome at a time
    // (PersistentChrome), so a lock found before a start is always stale. They are links: deleting removes the link, never what it names.
    internal static void ClearLocks(string profileFolder)
    {
        foreach (var name in LockFiles)
        {
            var path = Path.Combine(profileFolder, name);
            try { if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    // TMPDIR: with --disable-dev-shm-usage Chrome's shared memory (the pages it draws, the sign-in among them) is backed by files in the temporary folder, so it is a folder
    // of this profile's own, 0700 (code review 2026-10-11). In the container's /tmp, not the keys volume: on Unraid that volume does not take Chrome's shared mappings, and
    // every Chrome started with its temporary folder there died at once with code 133 (the live server, 2026-10-11).
    internal static string TemporaryFolder(string profileFolder) => Path.Combine(Path.GetTempPath(), "messages-chrome-" + Path.GetFileName(Path.TrimEndingDirectorySeparator(profileFolder)));

    // All Chrome is given; nothing of the server's own environment.
    internal static IReadOnlyList<(string Name, string Value)> EnvironmentFor(string profileFolder, string authorityFile)
    {
        var home = Path.Combine(profileFolder, ".home");
        var variables = new List<(string, string)>
        {
            ("PATH", BrowserPath), ("HOME", home), ("XDG_CONFIG_HOME", Path.Combine(home, "config")), ("XDG_CACHE_HOME", Path.Combine(home, "cache")),
            ("TMPDIR", TemporaryFolder(profileFolder)), ("LANG", "C.UTF-8"), ("XAUTHORITY", authorityFile),
        };
        if (Environment.GetEnvironmentVariable("TZ") is { Length: > 0 } zone) variables.Add(("TZ", zone));
        return variables;
    }

    public IChromeRunning Start(string profileFolder, string? page = null)
    {
        ClearLocks(profileFolder);
        var key = Path.Combine(profileFolder, KeyFile);
        XAuthority.Write(key);
        try
        {
            var start = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false, WorkingDirectory = profileFolder, RedirectStandardInput = false };
            start.ArgumentList.Add("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(Script);
            start.ArgumentList.Add(executable);
            foreach (var argument in ChromeArguments.Build(profileFolder, noSandbox, page)) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var (name, value) in EnvironmentFor(profileFolder, key)) start.Environment[name] = value;
            Directory.CreateDirectory(Path.Combine(profileFolder, ".home"));
            if (!OperatingSystem.IsWindows())
            {
                // Private even when an earlier run left it: the mode is set, not only asked for at creation.
                var temporary = TemporaryFolder(profileFolder);
                Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return new Running(Process.Start(start) ?? throw new IOException("browser_not_started"), profileFolder, key);
        }
        catch
        {
            XAuthority.Delete(key);
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalProcess(int pid, int signal);

    private sealed class Running(Process process, string profileFolder, string authorityFile) : IChromeRunning
    {
        private readonly int group = process.Id;
        public string AuthorityFile { get; } = authorityFile;
        public int? ProcessGroup => group;
        public Task Exited { get; } = process.WaitForExitAsync();
        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public async Task<int?> DisplayAsync(CancellationToken cancellationToken)
        {
            var file = Path.Combine(profileFolder, DisplayFile);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30) && !Exited.IsCompleted)
            {
                try
                {
                    if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var number) && number >= 0) return number;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        public void Close()
        {
            try { SignalProcess(group, 15); }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { }
        }

        // The whole group, even when the shell has already exited: its zygote and renderers outlive it.
        public void Kill()
        {
            try { SignalProcess(-group, 9); }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* Already gone, or going. */ }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try { await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            for (var waited = 0; waited < 3000 && GroupAlive(); waited += 50) await Task.Delay(50).ConfigureAwait(false);
            process.Dispose();
            XAuthority.Delete(AuthorityFile);
            try { File.Delete(Path.Combine(profileFolder, DisplayFile)); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }

        private bool GroupAlive()
        {
            try { return SignalProcess(-group, 0) == 0; }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}

/// <summary>
/// The server's one Chrome, kept running (SWITCHBOARD.md 5.4, chosen 2026-10-10): started when there is a profile to start it on and for a first sign-in, restarted if it dies (a few times
/// an hour, then it stays down and says so), and closed gently when the server stops. One at a time uses its display (<see cref="HoldAsync"/>). Logged as fixed words and counts only.
/// </summary>
internal sealed class PersistentChrome(IChromeLauncher launcher, IXConnector connector, Action<string> log, Func<IChromeRunning, CancellationToken, Task>? inspect = null) : IAsyncDisposable
{
    /// <summary>Restarts allowed inside <see cref="RestartWindow"/> before it stays down.</summary>
    public int MaxRestarts { get; init; } = 5;
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a closing Chrome gets to write its state before it is ended.</summary>
    public TimeSpan CloseBudget { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StartBudget { get; init; } = TimeSpan.FromSeconds(40);
    /// <summary>How much longer than <see cref="StartBudget"/> a caller waits for Chrome to come up before it gives up.</summary>
    public TimeSpan StartMargin { get; init; } = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object state = new();
    private CancellationTokenSource? stopping;
    private Task? supervisor;
    private string? profile;
    private volatile IChromeRunning? current;
    private volatile int display = -1;
    private TaskCompletionSource<bool> ready = NewReady();
    // The end of the newest Chrome that was asked to stop: whatever starts next waits for it, so two never share the fields below or a profile folder.
    private Task closing = Task.CompletedTask;
    // The unplanned starts inside the window, kept across supervisors so that asking again does not forget how often it died; and when it gave up (sticky for the window).
    private readonly List<long> starts = [];
    private long? gaveUp;

    private static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The profile folder Chrome is (being) run on, or null.</summary>
    public string? Profile { get { lock (state) return profile; } }

    /// <summary>Whether Chrome is up and its display is known.</summary>
    public bool Running => current is not null && display >= 0 && !(current?.Exited.IsCompleted ?? true);

    /// <summary>Starts Chrome on <paramref name="profileFolder"/> (or keeps it running on it) and returns when its display is up; false when it did not come up.</summary>
    /// <param name="page">The page a Chrome started now opens (null: <see cref="ChromeArguments.Page"/>); a Chrome already running on the folder keeps the page it has.</param>
    public async Task<bool> EnsureAsync(string profileFolder, CancellationToken cancellationToken, string? page = null)
    {
        Task<bool> wait;
        lock (state)
        {
            if (gaveUp is { } at)
            {
                // Sticky for the window: asking again does not start the count over.
                if (Stopwatch.GetElapsedTime(at) < RestartWindow) return false;
                gaveUp = null;
                starts.Clear();
            }
            if (profile is not null && profile != profileFolder) StopLockedAsync();
            if (supervisor is null || supervisor.IsCompleted)
            {
                profile = profileFolder;
                stopping = new CancellationTokenSource();
                // Each supervisor answers its own wait: an old one ending late can never settle the wait of the one that replaced it.
                var mine = NewReady();
                ready = mine;
                var token = stopping.Token;
                var folder = profileFolder;
                var opens = page;
                var before = closing;
                supervisor = Task.Run(async () =>
                {
                    try { await before.ConfigureAwait(false); } catch (Exception) { }
                    await SuperviseAsync(folder, opens, token, mine).ConfigureAwait(false);
                });
            }
            wait = ready.Task;
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(StartBudget + StartMargin);
        try { return await wait.WaitAsync(bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>Closes Chrome gently (so it writes its cookies) and starts it again on <paramref name="profileFolder"/>, the way the renewal gives it a fresh page.</summary>
    public async Task<bool> RestartAsync(string profileFolder, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        return await EnsureAsync(profileFolder, cancellationToken).ConfigureAwait(false);
    }

    // A start that failed leaves what Chrome and its display said beside the profile folders (the keys volume, 0600), as last-chrome-error.log: a sign-in's copy is deleted
    // when it fails, and its own error file with it (the first live runs, 2026-10-10). Never in the server's log, since Chrome's lines can name pages. Read with docker exec.
    private static void KeepLastErrors(string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(folder));
            if (parent is null) return;
            var text = string.Join("\n", new[] { ".chrome-error.log", ".display-error.log" }
                .Select(name => Path.Combine(folder, name)).Where(File.Exists)
                .Select(path => "== " + Path.GetFileName(path) + "\n" + string.Join("\n", File.ReadLines(path).TakeLast(40))));
            var target = Path.Combine(parent, "last-chrome-error.log");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var output = new StreamWriter(new FileStream(target, options));
            output.Write(text);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    private async Task SuperviseAsync(string folder, string? page, CancellationToken stop, TaskCompletionSource<bool> ready)
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            IChromeRunning? running = null;
            try
            {
                running = launcher.Start(folder, page);
                current = running;
                using var up = CancellationTokenSource.CreateLinkedTokenSource(stop);
                up.CancelAfter(StartBudget);
                var number = await running.DisplayAsync(up.Token).ConfigureAwait(false);
                if (number is null)
                {
                    log($"google chrome start=failed reason={(running.Exited.IsCompleted ? "exited" : "slow")} code={running.ExitCode?.ToString() ?? "none"}");
                    KeepLastErrors(folder);
                }
                else
                {
                    display = number.Value;
                    log(attempt == 0 ? "google chrome started" : $"google chrome restarted n={attempt}");
                    ready.TrySetResult(true);
                    // What Chrome's processes show about its sandbox, said once by the check itself (ChromeSandbox.InspectAsync); never a reason to stop Chrome.
                    if (inspect is not null) _ = Task.Run(async () => { try { await inspect(running, stop).ConfigureAwait(false); } catch (Exception) { } });
                    await running.Exited.WaitAsync(stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception failure) { log($"google chrome start=failed type={failure.GetType().Name}"); }
            finally
            {
                display = -1;
                current = null;
                if (running is not null) await CloseAsync(running).ConfigureAwait(false);
            }
            if (stop.IsCancellationRequested) break;
            // It died, or never came up. A few tries an hour; past that it stays down for the hour, and says so once.
            var now = Stopwatch.GetTimestamp();
            bool over;
            lock (state)
            {
                starts.RemoveAll(at => Stopwatch.GetElapsedTime(at, now) > RestartWindow);
                starts.Add(now);
                over = starts.Count > MaxRestarts;
                if (over) gaveUp = now;
            }
            if (over)
            {
                log("google chrome gave_up reason=restarts");
                ready.TrySetResult(false);
                return;
            }
            attempt++;
            try { await Task.Delay(RestartDelay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        ready.TrySetResult(false);
    }

    // Asked to close, so it writes what it holds; ended if it has not gone by the budget. Everything it started is gone when this returns.
    private async Task CloseAsync(IChromeRunning running)
    {
        running.Close();
        try { await running.Exited.WaitAsync(CloseBudget).ConfigureAwait(false); }
        catch (Exception) { log("google chrome close=forced"); }
        await running.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The display Chrome is on, opened for the caller to use and dispose; null when Chrome is not up.</summary>
    public IXDisplay? Open()
    {
        var running = current;
        var number = display;
        if (running is null || number < 0 || running.Exited.IsCompleted) return null;
        try { return connector.Open(number, running.AuthorityFile); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// The one use of Chrome's display at a time (a sign-in, a reload): the caller disposes the lease. <c>sign_in_busy</c> (an <see cref="InvalidOperationException"/>) when it is not free within <paramref name="wait"/>.
    /// </summary>
    public async Task<IDisposable> HoldAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(wait);
        try { await gate.WaitAsync(bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("sign_in_busy"); }
        return new Hold(gate);
    }

    private sealed class Hold(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }

    /// <summary>Closes Chrome gently (it writes its cookies and state), and does not restart it. Safe to call when it is not running.</summary>
    public Task StopAsync()
    {
        lock (state) return StopLockedAsync();
    }

    private Task StopLockedAsync()
    {
        var task = supervisor;
        stopping?.Cancel();
        supervisor = null;
        profile = null;
        if (task is not null) closing = task;
        return task ?? closing;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}
