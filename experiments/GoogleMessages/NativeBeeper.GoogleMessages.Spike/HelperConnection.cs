using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NativeBeeper.GoogleMessages.Spike;

// Where a helper executable is: beside the application, with the platform's executable suffix.
internal static class HelperExecutables
{
    public static string PathFor(string name) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
}

// Private inherited pipes, no TCP listener, secrets in arguments, or shell invocation.
internal sealed class HelperConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HelperRequests requests;
    // User-initiated requests go ahead of queued background reads (see HelperCallGate).
    private static readonly HashSet<string> UserActions = new(StringComparer.Ordinal)
    { "ui.sendText", "ui.sendMedia", "ui.mediaStage", "ui.sendPermission", "ui.react", "ui.markRead", "ui.updateChat", "ui.deleteChat", "ui.startDirect", "ui.startGroup" };
    private readonly Task pump;
    private readonly Task errors;
    private readonly object disposeSync = new();
    private Task? disposal;
    public event Action<JsonElement>? Typing;
    public event Action? Disconnected;
    public event Action<string, JsonElement>? Received;
    // The app's own log, where there is one. Given fixed words, helper file names, counts and exit codes only.
    internal static Action<string>? Trace { get; set; }
    // Every connection not yet disposed, so shutdown can close what no backend owns (a pairing window, a renewal) and
    // say how many are still open. One leaves only once its process is gone.
    private static readonly HashSet<HelperConnection> Live = [];
    private static int endedOnInput, killed;
    private static volatile bool refusing;
    private readonly string name;
    // The data folder every helper is started with, as NATIVEBEEPER_DATA_ROOT in the helper's own environment and never
    // this process's (the app reads that variable as "this is a development copy"). The app sets it once its folder is
    // settled (Program.Main), before any helper can start. The Messenger helper works out its folder from that variable,
    // and without it from %LOCALAPPDATA%\NativeBeeper, the folder's name through 0.1.361: a helper started without it
    // would keep its history and log there, beside an app that has moved on. Null in the diagnostics, whose helpers
    // inherit their environment as before.
    internal static string? DataRoot { get; set; }
    // The helpers this host may start, by file name; null starts any. A host that cannot run a provider (the Linux server:
    // Google Messages, Google Voice, Instagram) names only the ones it can, so a route it forgot to refuse still cannot
    // reach that provider's service.
    internal static Func<string, bool>? Allowed { get; set; }
    // Instagram's helper needs an explicit --read-only argument to construct a client at all; its default mode
    // deliberately contacts nothing. Optional, so the two helpers that take no arguments are unaffected.
    public HelperConnection(string executable, string? arguments = null) : this(Spawn(executable, arguments), false) { }
    // Starting a helper process took 200-430 ms each on the UI thread at launch (owner logs, 2026-10-08), queued ahead of
    // the cached chat list and Root.Loaded until the reveal hold fired. Only the process start and the job assignment
    // leave the caller's thread; the connection itself is built back on the caller's context, so its pump, its Received
    // and Disconnected handlers and everything after run exactly where they did.
    public static async Task<HelperConnection> StartAsync(string executable, string? arguments = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var spawned = await Task.Run(() =>
        {
            var started = Spawn(executable, arguments, environment);
            AfterSpawn?.Invoke();
            return started;
        }, CancellationToken.None);
        // Shutdown, or the caller giving up, may have come while the process was starting: nothing owns it yet, so it
        // would outlive the sweep that closes every helper.
        if (refusing)
        {
            Discard(spawned.Process);
            Trace?.Invoke($"helper start refused name={spawned.Name} reason=shutdown");
            throw new IOException("Helper could not start.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            Discard(spawned.Process);
            throw new OperationCanceledException(cancellationToken);
        }
        return new HelperConnection(spawned, true);
    }
    // Runs on the pool right after a process has started, so the harness can begin a shutdown at that moment.
    internal static Action? AfterSpawn { get; set; }
    private static void Discard(Process started)
    {
        try { started.Kill(); } catch (Exception) { }
        try { started.Dispose(); } catch (Exception) { }
    }
    [ThreadStatic] private static IReadOnlyDictionary<string, string>? extraEnvironment;
    private readonly record struct Spawned(string Name, Process Process, string Placement, bool DataRootGiven, long Milliseconds, bool OnPool);
    private static Spawned Spawn(string executable, string? arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        // Once the app has begun closing its helpers, a reconnect or recovery racing it must not start one that nothing
        // will close: it would run on the same account's stores as the next launch's.
        if (refusing) { Trace?.Invoke($"helper start refused name={name} reason=shutdown"); throw new IOException("Helper could not start."); }
        if (Allowed is { } allowed && !allowed(name)) { Trace?.Invoke($"helper start refused name={name} reason=not_on_this_host"); throw new IOException("Helper could not start."); }
        var dataRoot = DataRoot;
        var clock = Stopwatch.StartNew();
        // Extra environment for this one start (the account move's offline history helper), read by StartInfo on this same thread.
        extraEnvironment = environment;
        Process started;
        try { started = HelperJob.Start(StartInfo(executable, arguments, dataRoot)); }
        finally { extraEnvironment = null; }
        var placement = HelperJob.Assign(started);
        return new Spawned(name, started, placement, dataRoot is not null, clock.ElapsedMilliseconds, Thread.CurrentThread.IsThreadPoolThread);
    }
    private HelperConnection(Spawned spawned, bool offloaded)
    {
        name = spawned.Name;
        process = spawned.Process;
        JobPlacement = spawned.Placement;
        SpawnedOnPool = spawned.OnPool;
        // The offloaded start also says how long the spawn took and where; the plain constructor's line is as it was.
        Trace?.Invoke($"helper start name={name} job={JobPlacement}" + (spawned.DataRootGiven ? " data_root=given" : "")
            + (offloaded ? $" spawn_ms={spawned.Milliseconds} thread={(spawned.OnPool ? "pool" : "caller")}" : ""));
        requests = new HelperRequests(async (frame, token) =>
        {
            await process.StandardInput.WriteLineAsync(frame.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }, () => { try { if (!process.HasExited) process.Kill(); } catch (Exception) { } });
        errors = DrainErrorsAsync();
        pump = PumpAsync();
        lock (Live) Live.Add(this);
    }
    // How every helper is started: private pipes, no window, no shell, and the data folder when there is one.
    internal static ProcessStartInfo StartInfo(string executable, string? arguments, string? dataRoot)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            Arguments = arguments ?? "",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        if (dataRoot is { Length: > 0 }) start.Environment["NATIVEBEEPER_DATA_ROOT"] = dataRoot;
        if (extraEnvironment is { } extra) foreach (var (name, value) in extra) start.Environment[name] = value;
        // The process the helper ends with (HelperJob.Start).
        if (OperatingSystem.IsLinux()) start.Environment["MESSAGES_HELPER_PARENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }
    // Whether its process was started on a thread-pool thread (StartAsync), for the harness.
    internal bool SpawnedOnPool { get; }
    // Whether Windows ends this helper with the app however the app ends (see HelperJob).
    public string JobPlacement { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    // The connection scope this helper serves ("messenger:", "gvoice:<key>:"), set by its owner right after it starts.
    // Only a scoped helper is a service's own and appears in Snapshot; a pairing or renewal helper stays unscoped. Each
    // time a connection takes a scope counts as one start of that scope's helper for this app session.
    public string? Scope
    {
        get { lock (Registry) return scope; }
        set
        {
            lock (Registry)
            {
                if (value == scope) return;
                scope = value;
                if (value is null) return;
                if (!Registry.TryGetValue(value, out var record)) Registry[value] = record = new ScopeRecord();
                record.Starts++;
                record.Latest = this;
            }
        }
    }
    private string? scope;
    private volatile bool ended;
    // Per scope: how many helpers it has started this session, the newest of them, and how the last one ended.
    private sealed class ScopeRecord { public int Starts; public HelperConnection? Latest; public HelperExit? LastExit; }
    private static readonly Dictionary<string, ScopeRecord> Registry = new(StringComparer.Ordinal);
    // The most recent exits of any helper, scoped or not. Bounded: a helper restarting all day keeps the newest only.
    private static readonly Queue<HelperExit> Ended = new();
    internal const int ExitHistory = 16;
    internal static IReadOnlyList<HelperExit> RecentExits { get { lock (Registry) return [.. Ended]; } }
    // What each service's own helper is doing, from this app's registry rather than from process names, which also
    // count a development copy's helpers. Fixed words, times and exit codes only: never LastError, the helper's words.
    public static IReadOnlyList<HelperStatus> Snapshot()
    {
        lock (Registry)
        {
            var statuses = new List<HelperStatus>(Registry.Count);
            foreach (var (key, record) in Registry.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (record.Latest is not { } latest) continue;
                var running = false;
                int? processId = null;
                var lastExit = record.LastExit;
                if (!latest.ended)
                {
                    // Stopped by itself and not yet closed by its owner: not running, and how it ended is already known.
                    try
                    {
                        if (latest.process.HasExited)
                        {
                            var at = new DateTimeOffset(latest.process.ExitTime);
                            lastExit = new HelperExit(latest.name, key, "gone", latest.ExitCodeOrUnknown(), at, at - latest.StartedAt);
                        }
                        else
                        {
                            running = true;
                            processId = latest.process.Id;
                        }
                    }
                    catch (Exception) { }
                }
                statuses.Add(new HelperStatus(key, latest.name, latest.StartedAt, record.Starts, lastExit, running, processId));
            }
            return statuses;
        }
    }
    private void RecordExit(string how)
    {
        var code = ExitCodeOrUnknown();
        var at = DateTimeOffset.UtcNow;
        lock (Registry)
        {
            var exit = new HelperExit(name, scope, how, code, at, at - StartedAt);
            Ended.Enqueue(exit);
            while (Ended.Count > ExitHistory) Ended.Dequeue();
            if (scope is not null && Registry.TryGetValue(scope, out var record) && ReferenceEquals(record.Latest, this)) record.LastExit = exit;
            ended = true;
        }
    }
    internal int ProcessId => process.Id;
    public static int LiveCount { get { lock (Live) return Live.Count; } }
    // How helpers have closed in this process: on their own once their input ended, or killed when they did not.
    public static (int EndedOnInput, int Killed) Exits => (Volatile.Read(ref endedOnInput), Volatile.Read(ref killed));
    // Set by shutdown for the rest of the process. Settable back only so the offline harness can test it.
    internal static bool RefuseStarts { get => refusing; set => refusing = value; }
    // Contact-visible calls still waiting for their answer, across every open helper.
    public static int MutationsInFlight { get { lock (Live) return Live.Sum(connection => connection.requests.MutationsInFlight); } }
    // The same, on this helper alone: what a restart of one service waits for.
    public int OwnMutationsInFlight => requests.MutationsInFlight;
    // Stops this helper taking calls, but only while none that someone else would see is under way, for a restart that
    // closes it next: one cut off mid-call leaves its outcome unknown. False, with nothing changed, while one is; after
    // true every call fails before it is written. The process runs on until its owner closes it.
    public bool SealIfIdle() => requests.CloseIfIdle();
    private static long lastId;
    private readonly long id = Interlocked.Increment(ref lastId);
    // The counter above starts again with every process, so a mark also names the run: a question asked of a host that has since
    // restarted must never match the new run's helper 1 (review, 2026-10-08).
    private static readonly string Run = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    // Which helper this is and how many contact-visible calls have begun on it, as one opaque word. Read when a restart
    // asks whether to cut off the sends in flight; a later look that finds it changed means a send began after.
    public string OwnMutationMark => $"{Run}.{id}.{requests.MutationsStarted}";
    // Like SealIfIdle, but for a restart somebody agreed to under the sends a mark names: it closes the helper with those
    // in flight, and refuses, with nothing changed, when the mark is another run's or helper's or a send has begun since.
    public bool SealThrough(string mark)
    {
        var parts = mark.Split('.');
        return parts.Length == 3 && parts[0] == Run && long.TryParse(parts[1], out var helper) && long.TryParse(parts[2], out var began)
            && helper == id && requests.CloseIfNoneStartedSince(began);
    }
    public static async Task<bool> WhenMutationsSettledAsync(TimeSpan budget)
    {
        HelperConnection[] open;
        lock (Live) open = [.. Live];
        var settled = await Task.WhenAll(open.Select(connection => connection.requests.WhenMutationsSettledAsync(budget)));
        return settled.All(done => done);
    }
    // Closes every connection still open, each of which already waits a bounded time and then kills, and waits no
    // longer than the bound for all of them. True when every one finished inside it.
    public static async Task<bool> StopAllAsync(TimeSpan bound)
    {
        HelperConnection[] open;
        lock (Live) open = [.. Live];
        return await WithinAsync([.. open.Select(connection => (Func<Task>)(() => connection.DisposeAsync().AsTask()))], bound) == open.Length;
    }
    // Runs every close together and waits for them all, or for the bound. Returns how many finished inside it; one that
    // failed has finished too. A close still running at the bound is abandoned, not cancelled: at shutdown, the process
    // ending is what ends it.
    public static async Task<int> WithinAsync(IReadOnlyList<Func<Task>> closes, TimeSpan bound)
    {
        var finished = 0;
        var running = closes.Select(async close =>
        {
            try { await close(); } catch (Exception) { }
            Interlocked.Increment(ref finished);
        }).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(bound));
        return Volatile.Read(ref finished);
    }
    // A helper that dies says why on its standard error — a Go runtime failure prints its reason there and nowhere
    // else. Draining it without keeping anything meant a helper could vanish and leave no account of it at all. Only
    // the opening lines are kept: those carry the reason and the goroutine header, never rows or message content.
    // The reason a process died is the *last* thing it says, not the first: a Go runtime failure prints after whatever
    // the process wrote on the way up. Keeping the opening lines, as this first did, threw the reason away and kept
    // the startup noise.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> lastErrors = new();
    private volatile string? termination;
    public string? LastError
    {
        get
        {
            var said = lastErrors.IsEmpty ? null : string.Join(" | ", lastErrors);
            var why = termination is { } reason ? reason : process.HasExited ? $"exit {ExitCodeOrUnknown()}" : null;
            return (why, said) switch
            {
                (null, null) => null,
                (null, { } text) => text,
                ({ } cause, null) => cause,
                var (cause, text) => $"{cause}: {text}",
            };
        }
    }
    private string ExitCodeOrUnknown() { try { return process.ExitCode.ToString(); } catch (Exception) { return "unknown"; } }
    private async Task DrainErrorsAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length == 0) continue;
                lastErrors.Enqueue(line.Length > 200 ? line[..200] : line);
                while (lastErrors.Count > 4) lastErrors.TryDequeue(out _);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }
    private async Task PumpAsync()
    {
        // Where a failed pump was, for its log line: fixed words and a length, never the line itself.
        var stage = "read";
        var length = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                (stage, length) = ("parse", line.Length);
                if (line.Length > 8 * 1024 * 1024) throw new IOException("Helper response exceeded limit.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var kind))
                {
                    stage = "event";
                    if (root.TryGetProperty("result", out var data))
                    {
                        if (kind.GetString() == "typing") Typing?.Invoke(data.Clone());
                        Received?.Invoke(kind.GetString() ?? "", data.Clone());
                    }
                }
                else
                {
                    stage = "reply";
                    requests.Accept(root);
                }
                stage = "read";
            }
            termination ??= "the helper closed its output";
            requests.Close();
        }
        catch (Exception failure)
        {
            termination ??= $"the connection to the helper failed ({failure.GetType().Name})";
            requests.Close();
            if (failure is not OperationCanceledException)
            {
                Trace?.Invoke($"helper pipe failed name={name} stage={stage} type={failure.GetType().Name} chars={length}");
                // Nothing reads this helper any more and its owner starts another on the same account. Left running, it
                // kept its connections to the service for hours (messenger-helper.log.previous still growing, 2026-11-08).
                try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
            }
        }
        finally
        {
            // What the helper said on its way out arrives on the other stream, and it is the reason the stop happened.
            // Announcing the stop before that has been read reports every death as having no reason given.
            try { await Task.WhenAny(errors, Task.Delay(TimeSpan.FromSeconds(2))); } catch (Exception) { }
            Disconnected?.Invoke();
        }
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken, bool skipIfBusy = false)
    {
        var reply = await requests.CallAsync(method, parameters, cancellationToken, UserActions.Contains(method), skipIfBusy);
        return Unwrap(reply);
    }
    // A reply's result, or the error it carries as one of our own fixed codes. A call skipped because the pipe was busy
    // is handed back as it is, {"skipped":true}, which is what its callers test for. Reading "result" from it threw
    // KeyNotFoundException instead, so the history crawl and the priority catch-up failed where they meant to wait a
    // moment and ask again.
    internal static JsonElement Unwrap(JsonElement reply)
    {
        if (HelperRequests.IsSkipped(reply)) return reply;
        if (reply.TryGetProperty("error", out var error))
        {
            // Only expose our own fixed error codes, never arbitrary upstream response text.
            throw new InvalidOperationException(HelperErrorCodes.Sanitize(error.GetString()));
        }
        // The reply is already HelperRequests' own copy, independent of the pump's pooled document, and a part of it
        // lives as long as it does. Copying it again cost a second large-heap buffer for every media chunk.
        return reply.GetProperty("result");
    }
    public ValueTask DisposeAsync()
    {
        lock (disposeSync) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            requests.Close();
            // "gone": it had already stopped by itself. "eof": it stopped once its input ended. "killed": it had not
            // stopped three seconds later.
            var how = process.HasExited ? "gone" : "eof";
            // Throws while an unanswered frame is still being written, or when the helper has gone and taken the pipe
            // with it. Neither may stop the rest of the close, which a shutdown is waiting on.
            try { process.StandardInput.Close(); } catch (Exception) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                how = "killed";
                // Waited for briefly, so a connection that has left the live count really has no process behind it.
                try { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            }
            Interlocked.Increment(ref how == "killed" ? ref killed : ref endedOnInput);
            // The exit code only. LastError is never logged: it carries the helper's own words.
            Trace?.Invoke($"helper exit name={name} how={how} ms={clock.ElapsedMilliseconds} code={ExitCodeOrUnknown()}");
            RecordExit(how);
            lifetime.Cancel();
            await Task.WhenAll(pump, errors);
        }
        finally
        {
            // Its process is about to be released, so Snapshot, which reads it under this lock, must not ask it again.
            lock (Registry) ended = true;
            lock (Live) Live.Remove(this);
            process.Dispose(); lifetime.Dispose();
        }
    }
}

// How one helper ended: its executable's name, the scope it served, gone (it had stopped by itself), eof (it stopped once
// its input ended) or killed, its exit code, when, and how long it had run.
internal sealed record HelperExit(string Name, string? Scope, string How, string Code, DateTimeOffset At, TimeSpan Uptime);
// A service's helper as Connection health shows it: the newest one for the scope, how many the scope has started this
// session, how the last one ended, whether the newest is running, and its process id while it runs (the console's /top).
internal sealed record HelperStatus(string Scope, string Name, DateTimeOffset StartedAt, int Starts, HelperExit? LastExit, bool Running,
    int? ProcessId = null);
