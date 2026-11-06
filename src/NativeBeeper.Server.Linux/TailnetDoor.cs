using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Host.Network;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// The server's door on the owner's tailnet (SWITCHBOARD.md 4.7, chosen 2026-10-15): <c>messages-tailnet</c> beside the server, a Tailscale node of
/// its own in this container (experiments/Tailnet/door), run while <c>MESSAGES_TAILNET=1</c>. It passes each tailnet device's connection to the network host's relay
/// socket behind a line naming the device (<see cref="TailnetRelay"/>), so Unraid publishes the port on the home network only and the
/// server still tells devices apart.
/// <para>
/// The door is started with an empty environment (it inherits no helper key, no password, no Tailscale auth key) and its node's state in the keys volume's
/// <c>Tailnet</c> folder. Its standard output is read line by line: the sign-in link and the Tailnet Lock key go to the container's own log, as a pairing code
/// does; its report for the server's page (state, sign-in link, owner, key expiry, Tailnet Lock, access rules) to the <see cref="TailnetBoard"/> the page reads,
/// held in memory and never logged; and every other line, fixed words and counts, to the server's log. A door that ends by itself is started again, waiting 2 s and doubling to 5 minutes (back
/// to 2 s after a run of 10 minutes). The stop ends its input, which it takes as the word to close its node, and kills it after 5 s. The page's "Use another Tailscale
/// account" (<see cref="SignOut"/>) writes <c>logout</c> on its input instead: the door signs out and ends, its state folder is emptied, and it starts again at once.
/// </para>
/// </summary>
internal sealed partial class TailnetDoor
{
    public const string Executable = "messages-tailnet";
    public const string StateFolder = "Tailnet";

    /// <summary>The relay socket: a private folder in the container's own temporary space, never a volume, made fresh by each start.</summary>
    public static string RelaySocket => Path.Combine(Path.GetTempPath(), "messages-tailnet", "relay.sock");

    /// <summary>The first wait before a door that ended is started again; doubled each time up to <see cref="LongestWait"/>. A check shortens it.</summary>
    internal static TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);
    /// <summary>A run this long resets the wait: the door ending after it is not a door that cannot start.</summary>
    internal static readonly TimeSpan Settled = TimeSpan.FromMinutes(10);
    /// <summary>How long the stop gives the door to close its node after its input ends.</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);
    private const int LongestLine = 1024;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    // The door's own lines (main.go's say): "tailnet" and words, each alone or with a value of letters, digits, '_' and '-'.
    [GeneratedRegex("^tailnet(?: [a-z_]+(?:=[A-Za-z0-9_-]+)?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();

    [GeneratedRegex("^tailnet login url=https://[A-Za-z0-9.-]+(?::[0-9]+)?/[A-Za-z0-9/_.~%?=&+:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginLine();

    // What the server's page shows (page.go): one JSON object, held for the page and never written anywhere.
    [GeneratedRegex(@"^tailnet page \{.*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex PageLine();

    [GeneratedRegex("^tailnet lock sign node_key=nodekey:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LockLine();

    // A Tailscale auth key, as the door takes it (main.go's keyPattern).
    [GeneratedRegex("^tskey-[A-Za-z0-9_-]{8,500}$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthKey();

    /// <summary>Whether <paramref name="key"/> has the form of a Tailscale auth key (<c>tskey-...</c>), the only thing the door is ever handed on its input.</summary>
    internal static bool LooksLikeAuthKey(string? key) => key is not null && AuthKey().IsMatch(key);

    private readonly ProcessStartInfo start;
    private readonly string? authKey;
    private readonly Action<string> log, console;
    private readonly TailnetBoard? board;
    private readonly Func<ProcessStartInfo, Process> launch;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private Process? running;
    private Task supervising = Task.CompletedTask;
    private int unreadable;

    private readonly string stateFolder;
    private int forgetting;

    /// <summary>The exit code with which the door says it signed out on the server's "logout" line (main.go's forgetCode): its state is then removed and it starts at once.</summary>
    internal const int ForgetCode = 3;

    private TailnetDoor(ProcessStartInfo start, string stateFolder, string? authKey, Action<string> log, Action<string> console, Func<ProcessStartInfo, Process> launch,
        TailnetBoard? board)
    {
        this.start = start; this.stateFolder = stateFolder; this.authKey = authKey; this.log = log; this.console = console; this.launch = launch; this.board = board;
        if (board is not null) board.SignOut = SignOut;
    }

    /// <summary>
    /// "Use another Tailscale account" (SWITCHBOARD.md 10.8): asks the running door to sign its node out (a <c>logout</c> line on its input); once it has ended, its state
    /// folder is removed and it starts again at once, with no identity, so it asks for a new sign-in link. False when no door is running to ask.
    /// Logged: <c>server tailnet sign_out asked</c>, then <c>server tailnet forgotten</c> or <c>server tailnet forget failed type=</c>.
    /// </summary>
    public bool SignOut()
    {
        Process? process;
        lock (gate) process = running;
        if (process is null || stopping.IsCancellationRequested) return false;
        Interlocked.Exchange(ref forgetting, 1);
        try
        {
            process.StandardInput.Write("logout\n");
            process.StandardInput.Flush();
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A door that is ending already: its state is still forgotten before it starts again.
            log($"server tailnet sign_out failed type={failure.GetType().Name}");
        }
        log("server tailnet sign_out asked");
        board?.Set(null);
        return true;
    }

    // The door's state (its node key and Tailscale's own files), removed so it starts as a new machine. Whatever is in the folder is the door's: nothing else is kept there.
    private void Forget()
    {
        try
        {
            if (Directory.Exists(stateFolder))
            {
                foreach (var file in Directory.EnumerateFiles(stateFolder)) File.Delete(file);
                foreach (var folder in Directory.EnumerateDirectories(stateFolder))
                {
                    // A link is unlinked, never followed.
                    if (new DirectoryInfo(folder).LinkTarget is not null) Directory.Delete(folder, false);
                    else Directory.Delete(folder, true);
                }
            }
            log("server tailnet forgotten");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log($"server tailnet forget failed type={failure.GetType().Name}");
        }
    }

    /// <summary>
    /// Starts the door for a server on <paramref name="port"/> whose relay socket is <paramref name="socket"/>, or says why not (<c>server tailnet refused
    /// reason=name|no_door</c>) and returns null. <paramref name="console"/> is the container's own output; <paramref name="launch"/> a check's stand-in for the
    /// process start (the server's is <see cref="HelperJob.Start"/>, from the one thread a parent-death signal may come from).
    /// <paramref name="authKey"/> is <c>MESSAGES_TAILNET_AUTHKEY</c> (SWITCHBOARD.md 10.8, headless setups): written as the first line of each run's input and nowhere else, so
    /// the door's environment stays empty and no argument names it. One not of Tailscale's form is dropped (<c>server tailnet authkey refused reason=form</c>) and the
    /// sign-in link is offered as without one. Tailscale uses it only while the node has no identity yet.
    /// </summary>
    public static TailnetDoor? Start(string executable, string stateFolder, string socket, string name, int port, Action<string> log, Action<string> console,
        Func<ProcessStartInfo, Process>? launch = null, TailnetBoard? board = null, string? authKey = null, bool defaultName = false)
    {
        if (authKey is { Length: > 0 } && !LooksLikeAuthKey(authKey))
        {
            log("server tailnet authkey refused reason=form");
            authKey = null;
        }
        if (authKey is { Length: 0 }) authKey = null;
        if (!Name().IsMatch(name))
        {
            log("server tailnet refused reason=name");
            return null;
        }
        if (!File.Exists(executable))
        {
            log("server tailnet refused reason=no_door");
            return null;
        }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stateFolder);
        else Directory.CreateDirectory(stateFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var door = new TailnetDoor(StartInfo(executable, stateFolder, socket, name, port, authKey is not null, defaultName), stateFolder, authKey, log, console, launch ?? HelperJob.Start, board);
        if (authKey is not null) log("server tailnet authkey=given");
        door.supervising = Task.Run(door.SuperviseAsync);
        return door;
    }

    /// <summary>
    /// The door's command: its arguments, its pipes, and an environment holding nothing but the server's process id (parentwatch_linux.go). With
    /// <paramref name="authKeyOnInput"/> the door reads an auth key from its input's first line (<c>--authkey-stdin</c>); the key itself is never in here.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string executable, string stateFolder, string socket, string name, int port, bool authKeyOnInput = false, bool defaultName = false)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "--state", stateFolder, "--socket", socket })
            start.ArgumentList.Add(argument);
        if (!defaultName) { start.ArgumentList.Add("--hostname"); start.ArgumentList.Add(name); }
        start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        if (authKeyOnInput) start.ArgumentList.Add("--authkey-stdin");
        start.Environment.Clear();
        if (OperatingSystem.IsLinux()) start.Environment["MESSAGES_HELPER_PARENT_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        // On Windows (the PC's own server, SWITCHBOARD.md 10.7) a Go program finds the system's own libraries through SystemRoot, a path and nothing secret; the job the
        // server keeps its helpers in ends the door with the server, so it needs no parent watch.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot) start.Environment["SystemRoot"] = systemRoot;
        return start;
    }

    /// <summary>
    /// Where one of the door's lines goes: <c>page</c> (held for the server's page alone, <see cref="TailnetBoard"/>), <c>console</c> (the container's own log),
    /// <c>log</c> (the server's), or nowhere (<c>unreadable</c>).
    /// </summary>
    internal static string Route(string line) =>
        line.Length > LongestLine ? "unreadable"
        : PageLine().IsMatch(line) ? "page"
        : LoginLine().IsMatch(line) || LockLine().IsMatch(line) ? "console"
        : LogLine().IsMatch(line) && !line.StartsWith("tailnet login url", StringComparison.Ordinal) && !line.StartsWith("tailnet lock sign", StringComparison.Ordinal) ? "log"
        : "unreadable";

    private void Take(string line)
    {
        switch (Route(line))
        {
            case "console": console(line); break;
            case "log": log(line); break;
            case "page":
                if (TailnetView.Parse(line["tailnet page ".Length..]) is { } view) board?.Set(view);
                else Interlocked.Increment(ref unreadable);
                break;
            default: Interlocked.Increment(ref unreadable); break;
        }
    }

    private async Task SuperviseAsync()
    {
        var wait = FirstWait;
        while (!stopping.IsCancellationRequested)
        {
            Process? process = null;
            try { process = launch(start); }
            catch (Exception failure) { log($"server tailnet start failed type={failure.GetType().Name}"); }
            if (process is not null)
            {
                var ran = Stopwatch.StartNew();
                lock (gate) running = process;
                log("server tailnet started");
                // The auth key first, before anything could end the input: the door reads it as its first line, then waits for the input to end as before.
                if (authKey is not null) Give(process, authKey);
                // A stop that came while it was starting found nothing to end.
                if (stopping.IsCancellationRequested) EndInput(process);
                var errorLines = 0;
                var errors = Task.Run(async () =>
                {
                    // Go writes a panic here; it can name addresses, so it is only counted.
                    try { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) errorLines++; }
                    catch (Exception) { }
                });
                try
                {
                    while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) Take(line);
                }
                catch (Exception) { }
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (Exception) { }
                await errors.ConfigureAwait(false);
                lock (gate) running = null;
                // Until the next run says otherwise, the page says Tailscale is starting rather than what a door that ended last said.
                board?.Set(null);
                var code = Code(process);
                var extra = (Interlocked.Exchange(ref unreadable, 0) is var dropped and > 0 ? $" unreadable_lines={dropped}" : "") + (errorLines > 0 ? $" error_lines={errorLines}" : "");
                process.Dispose();
                if (stopping.IsCancellationRequested)
                {
                    log($"server tailnet stopped code={code}{extra}");
                    break;
                }
                // Signed out on the page's "Use another Tailscale account": its state goes, and it starts again at once to ask for a new sign-in.
                if (Interlocked.Exchange(ref forgetting, 0) == 1)
                {
                    log($"server tailnet exited code={code} reason=sign_out{extra}");
                    Forget();
                    wait = FirstWait;
                    continue;
                }
                if (ran.Elapsed >= Settled) wait = FirstWait;
                log($"server tailnet exited code={code} restart_s={(long)Math.Ceiling(wait.TotalSeconds)}{extra}");
            }
            try { await Task.Delay(wait, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            wait = wait * 2 > LongestWait ? LongestWait : wait * 2;
        }
    }

    private static string Code(Process process)
    {
        try { return process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return "unknown"; }
    }

    private void Give(Process process, string key)
    {
        try
        {
            process.StandardInput.Write(key + "\n");
            process.StandardInput.Flush();
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A door that already ended: the restart gives it again.
            log($"server tailnet authkey failed type={failure.GetType().Name}");
        }
    }

    private static void EndInput(Process process)
    {
        try { process.StandardInput.Close(); } catch (Exception) { }
    }

    /// <summary>
    /// Ends the door: no new start, its input ended (the door closes its node and exits), and after <see cref="StopWait"/> a kill. Call it after the network host
    /// has stopped, so the answers it drains still reach the devices through the door.
    /// </summary>
    public async Task StopAsync()
    {
        stopping.Cancel();
        Process? process;
        lock (gate) process = running;
        if (process is not null)
        {
            EndInput(process);
            try { await process.WaitForExitAsync().WaitAsync(StopWait).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                log("server tailnet stop forced");
                try { process.Kill(); } catch (Exception) { }
            }
            catch (Exception) { }
        }
        try { await supervising.WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
