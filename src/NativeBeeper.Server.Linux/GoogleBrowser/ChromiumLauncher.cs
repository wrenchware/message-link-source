using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NativeBeeper.Server.Linux;

/// <summary>One running browser: the two ends of its DevTools pipe, and its end.</summary>
internal interface IChromiumProcess : IAsyncDisposable
{
    Stream FromBrowser { get; }
    Stream ToBrowser { get; }
    /// <summary>Completes when the browser has exited.</summary>
    Task Exited { get; }
    /// <summary>Ends the browser and everything it started, at once.</summary>
    void Kill();
}

/// <summary>Starts one browser on one profile folder. The real one is <see cref="ChromiumLauncher"/>; the harness gives a fake that speaks the protocol.</summary>
internal interface IChromiumLauncher
{
    IChromiumProcess Launch(string profileFolder);
}

/// <summary>One running sign-in browser: a headed Chromium whose DevTools are on a loopback port, not the pipe.</summary>
internal interface IChromiumSignInProcess : IAsyncDisposable
{
    /// <summary>Reads the port the browser chose (<see cref="DevToolsActivePort"/>) and connects to it. Fails if the browser exits first or does not come up in time.</summary>
    Task<CdpConnection> ConnectAsync(CancellationToken cancellationToken);
    /// <summary>Completes when the browser has exited.</summary>
    Task Exited { get; }
    /// <summary>Ends the browser and everything it started, at once.</summary>
    void Kill();
}

/// <summary>Starts the sign-in browser (SWITCHBOARD.md 5.4). The real one is <see cref="ChromiumLauncher"/>; the harness gives a fake.</summary>
internal interface IChromiumSignInLauncher
{
    IChromiumSignInProcess LaunchSignIn(string profileFolder);
}

/// <summary>
/// Chromium on Linux, one process per renewal and per profile (SWITCHBOARD.md 4.3: one browser per account, never a shared one switched between
/// accounts), driven over <c>--remote-debugging-pipe</c>: the DevTools protocol on the browser's file descriptors 3 and 4, with no port at all.
/// <para>
/// .NET hands a child only its standard streams, so the browser is started through <c>/bin/sh</c>, which moves them: this process's pipe to the
/// child's input becomes the browser's descriptor 3 (commands in), the pipe from the child's output its descriptor 4 (answers out), and the browser's
/// own input, output and error go to <c>/dev/null</c>, so nothing it prints (addresses, page errors) reaches the server's log. <c>exec</c> replaces the
/// shell, so the process is the browser itself, and the arguments are passed as <c>"$@"</c>, never through the shell's parsing. <c>setsid</c> (util-linux,
/// on every Debian and Ubuntu) gives the browser a process group of its own, whose id is its process id, so ending the run ends the zygote and
/// renderers too, even after the browser itself has gone (<see cref="Running.Kill"/>).
/// </para>
/// <para>
/// The environment is not the server's: the browser gets a home, configuration and cache folder inside its own profile (the server's home is the data
/// volume) and nothing else of the server's, which includes where the helpers' keys are.
/// </para>
/// <para>
/// Headed (<c>MESSAGES_CHROMIUM_HEADED=1</c>, SWITCHBOARD.md 5.4): the same browser without <c>--headless</c>, on a virtual X display made for this run
/// alone (<see cref="HeadedShell"/>): one <c>Xvfb</c> per browser, in the browser's own process group, on a display number Xvfb picks itself, listening on
/// no TCP port, and ended when the browser has. The display is named to the browser only; the rest of the environment is unchanged.
/// </para>
/// </summary>
internal sealed class ChromiumLauncher(string executable, bool noSandbox, bool headed = false) : IChromiumLauncher, IChromiumSignInLauncher
{
    // The redirections run in order: 3 and 4 take the pipes before 0 and 1 are pointed at /dev/null. The shell .NET starts is not a group leader, so
    // setsid makes the new session in place (no fork) and the process .NET watches stays the browser.
    internal const string Shell = "exec setsid \"$0\" \"$@\" 3<&0 4>&1 0</dev/null 1>/dev/null 2>/dev/null";

    /// <summary>
    /// The headed form of <see cref="Shell"/>: a shell of its own in the new session makes the DevTools descriptors and points its standard ones away,
    /// starts <c>Xvfb</c> (which picks a free display number and writes it to descriptor 5, here a pipe the shell reads, so two runs can never race for a
    /// number; no TCP listener; neither DevTools descriptor nor the server's pipes are given to it), and runs the browser as its child with
    /// <c>DISPLAY</c> set for that one command. When the browser has gone the display is ended and the shell exits, so <see cref="IChromiumProcess.Exited"/>
    /// means nothing of this run is left. A run ended hard is ended whole by <see cref="Running.Kill"/>: they share one process group. A display that
    /// does not start ends the run (the browser is never started on another display or none). No single quote may appear in the script.
    /// </summary>
    internal const string HeadedShell = "exec setsid /bin/sh -c '" + HeadedScript + "' \"$0\" \"$@\"";
    internal const string HeadedScript = "exec 3<&0 4>&1 0</dev/null 1>/dev/null 2>/dev/null\n" + ClosedDisplay;

    /// <summary>
    /// The sign-in's form of <see cref="HeadedShell"/> (SWITCHBOARD.md 5.4): the same display and the same process group, but no DevTools descriptors. The browser is told a loopback
    /// port instead (<see cref="Arguments"/> with <c>devToolsPort</c>), so nothing marks the page as driven through a pipe; the server's pipes go nowhere, as they do for the pipe form.
    /// </summary>
    internal const string SignInShell = "exec setsid /bin/sh -c '" + SignInScript + "' \"$0\" \"$@\"";

    internal const string SignInScript = "exec 0</dev/null 1>/dev/null 2>/dev/null\n" + ClosedDisplay;

    // Every headed display (a renewal's and the sign-in's) is closed to everything that has no key to it: no TCP listener and no local (abstract) socket, so the only way in is the socket
    // file under /tmp/.X11-unix, and the server asks for the magic cookie in .xauth (written by the launcher into the 0700 profile folder, named to the browser by XAUTHORITY alone,
    // and removed at the end). If that socket file is not there once Xvfb has said which display it took, the run ends rather than start the browser on a display it cannot reach.
    private const string ClosedDisplay = DisplayHead + "Xvfb -displayfd 5 -nolisten tcp -nolisten local -auth ./" + XAuthorityFile + XvfbTail + DisplayWait
        + "[ -S \"/tmp/.X11-unix/X$number\" ] || { kill \"$xvfb\" 2>/dev/null; exit 72; }\n" + DisplayRun + "rm -f ./" + XAuthorityFile + "\n" + DisplayEnd;

    /// <summary>The file in the profile folder the sign-in's display takes its magic cookie from.</summary>
    internal const string XAuthorityFile = ".xauth";

    // The parts the two forms share: a display of this run's own, the browser on it, and the display ended with the browser.
    private const string DisplayHead =
        "fifo=./.display\n"
        + "rm -f \"$fifo\"\n"
        + "mkfifo -m 600 \"$fifo\" || exit 70\n";

    private const string XvfbTail = " -screen 0 " + DisplaySize + "x24 3<&- 4<&- 5>\"$fifo\" &\n";

    private const string DisplayWait =
        "xvfb=$!\n"
        + "read -r number <\"$fifo\"\n"
        + "rm -f \"$fifo\"\n"
        + "case \"$number\" in \"\"|*[!0-9]*) kill \"$xvfb\" 2>/dev/null; exit 71;; esac\n";

    private const string DisplayRun =
        "DISPLAY=\":$number\" \"$0\" \"$@\"\n"
        + "status=$?\n"
        + "kill \"$xvfb\" 2>/dev/null\n"
        + "wait \"$xvfb\" 2>/dev/null\n";

    private const string DisplayEnd = "exit \"$status\"\n";

    /// <summary>The virtual screen, and the window that fills it: a size an ordinary desktop browser has.</summary>
    internal const string DisplaySize = "1280x800";

    private const string BrowserPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>Where the headed form finds Xvfb: the browser's own <c>PATH</c>. Null when it is in none of its folders.</summary>
    internal static string? FindXvfb(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var folder in BrowserPath.Split(':'))
            if (exists(folder + "/Xvfb")) return folder + "/Xvfb";
        return null;
    }

    /// <summary>
    /// What the browser is started with. For a renewal, never a debugging port or address, never automation switches beyond the pipe itself, never a flag that hides
    /// what it is (PROVIDERS.md: no spoofed user agent, no hidden automation). For the sign-in (<paramref name="devToolsPort"/>, always headed) the pipe is replaced by DevTools on a
    /// loopback port the browser picks itself and writes to <c>DevToolsActivePort</c> in the profile: only the pipe marks a page as automated, and the person is signing in there.
    /// Nothing else differs: no flag is added that changes what the browser says it is or hides that it is driven.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string profileFolder, bool noSandbox, bool headed = false, bool devToolsPort = false)
    {
        var arguments = new List<string>();
        if (devToolsPort)
        {
            // Loopback only, and a port the browser chooses (0), so two never collide and none is a known number.
            arguments.Add("--remote-debugging-address=127.0.0.1");
            arguments.Add("--remote-debugging-port=0");
        }
        else arguments.Add("--remote-debugging-pipe");
        arguments.AddRange(new[]
        {
            "--user-data-dir=" + profileFolder,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-sync",
            "--disable-component-update",
            "--disable-default-apps",
            "--disable-background-networking",
            "--disable-breakpad",
            "--disable-crash-reporter",
            "--metrics-recording-only",
            // A container's /dev/shm is 64 MB, too small for a renderer.
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--mute-audio",
            // No desktop keyring in a container: the profile's cookie store is protected by its folder (0700, on the keys volume), not by a keyring.
            "--password-store=basic",
        });
        // Chromium's own sandbox needs user namespaces or a setuid helper, which a container with every capability dropped and no-new-privileges
        // does not give it under Docker's default seccomp profile (chrome-seccomp.json or seccomp=unconfined does). Off only when the host says so (MESSAGES_CHROMIUM_NO_SANDBOX=1), so that is a choice made where it is visible.
        if (noSandbox) arguments.Add("--no-sandbox");
        if (headed)
        {
            // A browser with a window, on the virtual display (which has no window manager, so it is told where to be and how big). X11 named outright,
            // so it never looks for a Wayland session.
            arguments.Add("--ozone-platform=x11");
            arguments.Add("--window-position=0,0");
            arguments.Add("--window-size=" + DisplaySize.Replace('x', ','));
        }
        else arguments.Add("--headless=new");
        arguments.Add("about:blank");
        return arguments;
    }

    public IChromiumProcess Launch(string profileFolder)
    {
        if (!headed) return new Running(Start(profileFolder, Shell, Arguments(profileFolder, noSandbox, headed)));
        // A headed renewal's display is closed like the sign-in's (ClosedDisplay): a fresh key for the run, in the profile folder, named to the browser alone and deleted with the run.
        var cookie = Path.Combine(profileFolder, XAuthorityFile);
        XAuthority.Write(cookie);
        return new Running(Start(profileFolder, HeadedShell, Arguments(profileFolder, noSandbox, headed), cookie), cookie);
    }

    /// <summary>The sign-in's browser (SWITCHBOARD.md 5.4): always headed, on a display of its own, with DevTools on a loopback port; needs Xvfb, as the headed renewal does.</summary>
    public IChromiumSignInProcess LaunchSignIn(string profileFolder)
    {
        var cookie = Path.Combine(profileFolder, XAuthorityFile);
        XAuthority.Write(cookie);
        return new SignInRunning(new Running(Start(profileFolder, SignInShell, Arguments(profileFolder, noSandbox, headed: true, devToolsPort: true), cookie), cookie), profileFolder);
    }

    private Process Start(string profileFolder, string shell, IReadOnlyList<string> arguments, string? xauthority = null)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            WorkingDirectory = profileFolder,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(shell);
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Not DISPLAY: the headed shell names the display to the browser alone, once Xvfb has said which it took.
        start.Environment.Clear();
        start.Environment["PATH"] = BrowserPath;
        start.Environment["HOME"] = Path.Combine(profileFolder, ".home");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(profileFolder, ".home", "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(profileFolder, ".home", "cache");
        if (Environment.GetEnvironmentVariable("TZ") is { Length: > 0 } zone) start.Environment["TZ"] = zone;
        // The sign-in's display asks for a cookie (SignInScript); the browser is told which file it is in, and nothing else of the server's environment.
        if (xauthority is not null) start.Environment["XAUTHORITY"] = xauthority;
        return Process.Start(start) ?? throw new IOException("browser_not_started");
    }

    // The sign-in browser: the same process handling as a renewal's, and DevTools reached by the port the browser wrote into its profile.
    private sealed class SignInRunning(Running running, string profileFolder) : IChromiumSignInProcess
    {
        public Task Exited => running.Exited;
        public void Kill() => running.Kill();
        public ValueTask DisposeAsync() => running.DisposeAsync();

        public async Task<CdpConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            var (port, path) = await DevToolsActivePort.WaitAsync(profileFolder, running.Exited, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var socket = new System.Net.WebSockets.ClientWebSocket();
            try
            {
                // 127.0.0.1 and the port the browser wrote itself. The path names a random id but is no secret: GET /json/version hands it to anyone who can reach the port, and the
                // browser refuses only clients that send an Origin. No Origin is sent here.
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{path}"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
            var connection = CdpConnection.OverWebSocket(socket);
            connection.Start();
            return connection;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalProcess(int pid, int signal);

    private sealed class Running(Process process, string? key = null) : IChromiumProcess
    {
        private const int SigKill = 9;
        private readonly int group = process.Id;
        public Stream FromBrowser { get; } = process.StandardOutput.BaseStream;
        public Stream ToBrowser { get; } = process.StandardInput.BaseStream;
        public Task Exited { get; } = process.WaitForExitAsync();

        // The whole group, always, even when the browser itself has already exited: its zygote and renderers outlive it, handed to whatever reaps
        // orphans, and no longer count as its children, so a tree kill from a gone parent finds nothing.
        public void Kill()
        {
            try { SignalProcess(-group, SigKill); }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* Already gone, or going. */ }
        }

        // Returns once nothing of the group is left (or after three seconds), so a profile deleted next is not being written to by a renderer.
        public async ValueTask DisposeAsync()
        {
            Kill();
            try { await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
            for (var waited = 0; waited < 3000 && GroupAlive(); waited += 50) await Task.Delay(50).ConfigureAwait(false);
            process.Dispose();
            // The display's key is for the run alone, and does not stay in a profile that is kept.
            if (key is not null) XAuthority.Delete(key);
        }

        private bool GroupAlive()
        {
            try { return SignalProcess(-group, 0) == 0; }
            catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}
