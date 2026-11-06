using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>
/// The caller's end of one connection: greets the host, sends a <c>req</c> for each call and completes it from the
/// matching <c>res</c>. Many calls may be outstanding at once. Cancelling a call's token sends a <c>cancel</c> and the call
/// still completes from the answer: <c>cancelled</c> becomes an <see cref="OperationCanceledException"/>, a result that
/// had already been produced is returned.
/// </summary>
/// <remarks>
/// Failures reach callers as the exceptions a direct call to the port would have raised (<see cref="ProtocolErrors.ToException"/>),
/// with one exception: the protocol-level errors (invalid_request, unknown_method, protocol_unsupported, internal)
/// become <see cref="BackendFailure.InvalidResponse"/> with a fixed sentence, because the interface never sees a
/// <see cref="ProtocolException"/> and a host's own words are not shown for them. A closed connection fails what is outstanding
/// and every later call with <see cref="BackendFailure.Unavailable"/>; a mutation that was outstanding when it closed is instead an
/// unknown-outcome <see cref="SendException"/> (see <see cref="MutateAsync{TParams, TResult}"/>).
/// <para>Made with a channel it has that one connection for its whole life. Made with a connector it makes a new connection after a
/// loss (SWITCHBOARD.md 3.0c): in the background after a growing pause, and at once when a call finds the connection gone, though that
/// call still fails unavailable and nothing is queued behind a connection that is not there. A new connection says hello again; the
/// event feed's owner subscribes again from the position it last applied (<see cref="RealtimeFeedProxy"/>). A mutation is never sent
/// again on any connection. Those that were outstanding at the loss are asked about with <c>ops.status</c> once the new connection is
/// up, and the host's answers come out of <see cref="Reconciled"/>.</para>
/// </remarks>
public sealed class ProtocolClient : IDisposable, IOperationOutcomes
{
    public const string LostSentence = "The connection to the server was lost.";
    public const string FailedSentence = "The server could not complete that request.";
    public const string UnreadableSentence = "The server's answer could not be read.";
    public const string VersionSentence = "This version of Message Link cannot talk to that server.";

    /// <summary>How many interrupted mutations a client remembers to ask about; the oldest is forgotten first.</summary>
    public const int RememberedOperations = 256;

    private sealed class Waiter(string? operationId)
    {
        public TaskCompletionSource<ResponseFrame> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The mutation this call carries, once its request may have reached the host; a read has none.</summary>
        public string? OperationId { get; } = operationId;
        public volatile bool Sent;
    }

    // One connection: what a closed channel takes with it. A reconnecting client makes a new one and leaves this behind.
    private sealed class Link(IFrameChannel channel)
    {
        public IFrameChannel Channel { get; } = channel;
        public ConcurrentDictionary<string, Waiter> Pending { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<int> Greeting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Closed;
        public EventQueue? Inbox;
    }

    private readonly Func<CancellationToken, Task<IFrameChannel>>? connector;
    private readonly IFrameChannel? onlyChannel;
    private readonly string appVersion;
    private readonly string client;
    private readonly ProtocolCallStats? stats;
    private readonly Action<string>? log;
    private readonly int protocol;
    private readonly int minProtocol;
    private readonly HostStateCache? cache;
    private readonly CancellationTokenSource stop = new();
    private readonly Lazy<Task<Link>> first;
    private volatile Link? link;
    private volatile Link? attempting;
    private volatile bool stopping;
    private volatile BackendException? refusal;
    private long lastId;

    /// <summary>A client with one connection for its whole life: once it ends, every later call is unavailable.</summary>
    public ProtocolClient(IFrameChannel channel, string appVersion, string client = "windows", ProtocolCallStats? stats = null,
        Action<string>? log = null, int protocol = ProtocolVersion.Current, int minProtocol = ProtocolVersion.Minimum, HostStateCache? cache = null)
        : this(null, channel, appVersion, client, stats, log, protocol, minProtocol, cache) { }

    /// <summary>A client that connects when first used and makes a new connection after it is lost. The connector fails fast when nothing is there to connect to.</summary>
    public ProtocolClient(Func<CancellationToken, Task<IFrameChannel>> connector, string appVersion, string client = "windows", ProtocolCallStats? stats = null,
        Action<string>? log = null, int protocol = ProtocolVersion.Current, int minProtocol = ProtocolVersion.Minimum, HostStateCache? cache = null)
        : this(connector, null, appVersion, client, stats, log, protocol, minProtocol, cache) { }

    private ProtocolClient(Func<CancellationToken, Task<IFrameChannel>>? connector, IFrameChannel? channel, string appVersion, string client, ProtocolCallStats? stats,
        Action<string>? log, int protocol, int minProtocol, HostStateCache? cache)
    {
        this.connector = connector; onlyChannel = channel; this.appVersion = appVersion; this.client = client; this.stats = stats;
        this.log = log; this.protocol = protocol; this.minProtocol = minProtocol; this.cache = cache;
        first = new Lazy<Task<Link>>(OpenFirstAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The pause before the first background attempt after a loss; each failed attempt doubles it up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// The pause after an attempt the host refused for good: a token it revoked, or a certificate that is not the one pinned. Asking again changes neither, so each
    /// such refusal doubles it up to <see cref="MaxRefusedReconnectDelay"/>, jittered, and a call that finds the connection gone does not hurry it. A connection that
    /// is made starts the count again, and so does <see cref="RetryRefused"/>, which the person's own pairing again calls.
    /// </summary>
    public TimeSpan RefusedReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxRefusedReconnectDelay { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long a new connection may take to answer its hello before the attempt is given up.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The least time between one attempt's end and the next one a call asks for.</summary>
    public TimeSpan CallRetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>An operation the host says is still running is asked about again after this long for its first <see cref="RunningChecks"/> looks, then every <see cref="SlowCheckDelay"/>.</summary>
    public TimeSpan RunningCheckDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int RunningChecks { get; init; } = 15;
    public TimeSpan SlowCheckDelay { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long after it was first asked about an operation may stay running before it is answered unknown. A Google send can run for over two minutes.</summary>
    public TimeSpan RunningGiveUp { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The application version the host must report in its hello; a host of any other is refused like one that speaks no protocol this client can
    /// (<see cref="VersionSentence"/>, <c>protocol refused reason=version</c>) and is not tried again. A server left running from another install is
    /// replaced, not reached (SWITCHBOARD.md 3.2b). Left out, any host is accepted (the in-process loopback, where it cannot differ).
    /// </summary>
    public string? RequiredServerApp { get; init; }

    /// <summary>The host was refused for its version: no connection will be tried again, and every call fails.</summary>
    public bool Refused => refusal is not null;

    /// <summary>The version the host chose, once the greeting is done.</summary>
    public int? Version { get; private set; }

    private int linkNumber;

    /// <summary>
    /// How many connections have been made. It moves just before a connection can be seen as the current one (<see cref="IsConnected"/>) and so before
    /// <see cref="Connected"/> is raised for it: a listener that has been told of connection n and sees a higher number knows one is up that it has not been told
    /// of (the replica of the shared state, which must not take the last connection's word for this one).
    /// </summary>
    public int ConnectionNumber => Volatile.Read(ref linkNumber);

    /// <summary>There is a connection that has finished its hello and not ended.</summary>
    public bool IsConnected => link is { Closed: false, Greeting.Task.IsCompletedSuccessfully: true };

    /// <summary>A connection was made after an earlier one was lost, before anything is asked about the operations that were outstanding.</summary>
    public event EventHandler? Reconnected;

    /// <summary>
    /// A connection finished its hello, the first or any later one, and calls now go through it (<see cref="Version"/> is the version it chose). Raised on
    /// the thread that connected; a listener must not wait on this client.
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>
    /// A commit to the host's shared state (<see cref="EventKinds.Shared"/>), as its frame is read, on the connection's reader: a listener queues it and
    /// returns. It comes only while this connection has an event feed, and never through the feed's own items.
    /// </summary>
    public event EventHandler<SharedChange>? SharedChanged;

    /// <summary>The host handed this connection's feed a resync, as its frame is read: whatever follows the feed may have missed something before it.</summary>
    public event EventHandler? FeedResynced;

    /// <summary>The host's answer for a mutation that was outstanding when its connection was lost (<see cref="IOperationOutcomes"/>).</summary>
    public event EventHandler<OperationOutcome>? Reconciled;

    /// <summary>Greets the host, once; later calls return the same outcome. Calls connect by themselves, so this is for a host that wants to fail early.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await LinkAsync(cancellationToken).ConfigureAwait(false);

    // The connection a call uses. The first call waits for the greeting; with one fixed channel that outcome is final. With a connector
    // only the first attempt is waited for: a connection that is lost is not waited for, because a call must not queue behind it.
    private async Task<Link> LinkAsync(CancellationToken cancellationToken)
    {
        var opening = first.Value;
        if (connector is null || !opening.IsCompleted) return await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (link is { Closed: false } current) return current;
        WakeReconnect();
        throw refusal ?? Lost();
    }

    private async Task<Link> OpenFirstAsync()
    {
        if (connector is null)
        {
            var only = new Link(onlyChannel!);
            Interlocked.Increment(ref linkNumber);
            link = only;
            await HandshakeAsync(only, null).ConfigureAwait(false);
            Raise(Connected);
            return only;
        }
        try
        {
            var opened = await ConnectOnceAsync().ConfigureAwait(false);
            // Operations an earlier run left unknown (Reconcile) are waiting for this connection.
            RequestReconcile();
            return opened;
        }
        catch (Exception)
        {
            StartReconnecting();
            throw;
        }
    }

    private async Task HandshakeAsync(Link opened, CancellationToken? limit)
    {
        // A greeting nobody is waiting on any more (the connection failed first) is not an unobserved fault.
        _ = opened.Greeting.Task.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = Task.Run(() => ReadAsync(opened), CancellationToken.None);
        var system = ClientSystem.Current;
        await SendAsync(opened, new HelloFrame(protocol, appVersion, Client: client, MinProtocol: minProtocol, Os: system.Os, OsVersion: system.OsVersion, Model: system.Model))
            .ConfigureAwait(false);
        await (limit is { } token ? opened.Greeting.Task.WaitAsync(token) : opened.Greeting.Task).ConfigureAwait(false);
    }

    // One attempt through the connector: a connection with its hello done, or an unavailable BackendException (a version refusal as it is).
    private async Task<Link> ConnectOnceAsync()
    {
        Link? opened = null;
        lastRefusedForGood = false;
        try
        {
            var channel = await connector!(stop.Token).ConfigureAwait(false);
            opened = new Link(channel);
            attempting = opened;
            // Close writes stopping and then reads attempting; this writes attempting and then reads stopping. Without a full fence between
            // each pair both can read the old value, and a connection made during shutdown is never closed.
            Interlocked.MemoryBarrier();
            if (stopping) throw Lost();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            limit.CancelAfter(HelloTimeout);
            await HandshakeAsync(opened, limit.Token).ConfigureAwait(false);
            Interlocked.Increment(ref linkNumber);
            link = opened;
            // Ended between its greeting and here: not a connection to call on.
            if (opened.Closed) throw Lost();
            // Once a call made from a listener goes through this connection.
            Raise(Connected);
            return opened;
        }
        catch (Exception ex)
        {
            opened?.Channel.Close();
            if (ex is BackendException { Failure: BackendFailure.InvalidResponse, Message: VersionSentence } refused) refusal = refused;
            if (ex is BackendException) throw;
            // Still a connection that is not there to every caller; only the background attempts are paced apart for it (ReconnectAsync).
            lastRefusedForGood = RefusedForGood(ex);
            throw Lost();
        }
        finally { attempting = null; }
    }

    // Whether the last attempt that failed was refused for good. One attempt runs at a time (the first, then the reconnect loop's), and the loop reads this
    // straight after its own.
    private volatile bool lastRefusedForGood;

    // What the connectors say when the host will refuse every attempt until a person does something: a token it does not accept (NetworkEndpoint, PipeHandshake),
    // or a certificate that is not the one pinned. Nothing else: a network that failed, a host not yet up, no token to present or a pairing code that was paced
    // may answer differently the next time.
    internal static bool RefusedForGood(Exception failure) => failure is CertificateMismatchException or UnauthorizedAccessException;

    // ---- Reconnecting ----

    private int reconnecting;
    private volatile TaskCompletionSource? wake;
    private long lastAttemptEnded;

    private void StartReconnecting()
    {
        if (connector is null || stopping || refusal is not null) return;
        if (Interlocked.CompareExchange(ref reconnecting, 1, 0) != 0) return;
        _ = Task.Run(ReconnectAsync, CancellationToken.None);
    }

    /// <summary>
    /// The host was just started and is expected any moment (the Windows app starting its own server, SWITCHBOARD.md 10.7): the background attempt need not wait out its
    /// pause, as when a call finds the connection gone, within <see cref="CallRetryInterval"/>. Nothing while connected or refused.
    /// </summary>
    public void Hurry()
    {
        if (link is { Closed: false } || refusal is not null) return;
        WakeReconnect();
    }

    // A call found the connection gone: the background attempt need not wait out its pause, if the last one is not too recent. Not while the host refuses this
    // client for good: a call changes nothing about that, and the person's own pairing again is what wakes it (RetryRefused).
    private void WakeReconnect()
    {
        if (Volatile.Read(ref reconnecting) == 0) { StartReconnecting(); return; }
        // The wake first, then whether it may be used: the loop sets the one before it publishes the other.
        var current = wake;
        if (refusedWait) return;
        var ended = Volatile.Read(ref lastAttemptEnded);
        if (ended != 0 && Stopwatch.GetElapsedTime(ended) < CallRetryInterval) return;
        current?.TrySetResult();
    }

    // Set while the loop waits out a pause after a refusal for good.
    private volatile bool refusedWait;

    // Every client in this process waiting out a refusal for good, woken by RetryRefused. A client is here only while it waits.
    private static event Action? RetryRefusedRequested;

    /// <summary>
    /// The person has just paired this device again (Settings' Connect, Pair again or Ask to join kept a new key): every client in this process waiting out a
    /// refused token or certificate tries again now, and its pauses start again from the beginning. Nothing for a client that is not waiting.
    /// </summary>
    public static void RetryRefused() => RetryRefusedRequested?.Invoke();

    // Jittered, so clients refused by one host at once do not keep arriving at once: three quarters to five quarters of the pause.
    internal static TimeSpan Jittered(TimeSpan pause) => TimeSpan.FromTicks((long)(pause.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    private async Task ReconnectAsync()
    {
        var pause = ReconnectDelay;
        // The pause after a refusal for good: null until the first in this run, then doubled at each one up to the longest. Kept apart from the ordinary pause,
        // so a network that also drops now and then does not start it again; only a connection made (a new run) or the person pairing again does.
        TimeSpan? refusedPause = lastRefusedForGood ? RefusedReconnectDelay : null;
        // Whether the attempt just made (or the first, which failed before this run began) was refused for good: which of the two pauses comes next.
        var refusedLast = lastRefusedForGood;
        var attempt = 0;
        try
        {
            while (!stopping && refusal is null)
            {
                var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var retried = false;
                void Retry() { retried = true; woken.TrySetResult(); }
                if (refusedLast) RetryRefusedRequested += Retry;
                // Before the wake is published: a call that sees this wake also sees that it may not use it (WakeReconnect).
                refusedWait = refusedLast;
                wake = woken;
                try
                {
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    await Task.WhenAny(Task.Delay(refusedLast ? Jittered(refusedPause!.Value) : pause, waiting.Token), woken.Task).ConfigureAwait(false);
                    waiting.Cancel();
                }
                finally
                {
                    refusedWait = false;
                    if (refusedLast) RetryRefusedRequested -= Retry;
                }
                if (stopping) break;
                // Paired again: a refusal now is the first again.
                if (retried) refusedPause = null;
                attempt++;
                var started = Stopwatch.GetTimestamp();
                var ok = false;
                try { await ConnectOnceAsync().ConfigureAwait(false); ok = true; }
                catch (Exception) { }
                finally { Volatile.Write(ref lastAttemptEnded, Stopwatch.GetTimestamp()); }
                refusedLast = !ok && lastRefusedForGood;
                if (refusedLast)
                    refusedPause = refusedPause is { } last ? TimeSpan.FromTicks(Math.Min(last.Ticks * 2, MaxRefusedReconnectDelay.Ticks)) : RefusedReconnectDelay;
                // The first few, then one in a hundred: a window waiting on a server of its own tries eight times a second, and a server that never answers filled the log with them.
                if (ok || refusedLast || attempt <= 10 || attempt % 100 == 0)
                    log?.Invoke($"protocol reconnect attempt={attempt} ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} outcome={(ok ? "ok" : refusedLast ? "refused" : "failed")}" +
                        (refusedLast ? $" wait_s={(long)refusedPause!.Value.TotalSeconds}" : ""));
                if (ok)
                {
                    Raise(Reconnected);
                    RequestReconcile();
                    break;
                }
                if (!refusedLast) pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }
        finally
        {
            wake = null;
            refusedWait = false;
            // A full fence, not a release write: the read of link.Closed below must not move before this write, or a connection lost
            // in between (whose reader writes Closed and then reads reconnecting) and this both see the other's old value and nobody reconnects.
            Interlocked.Exchange(ref reconnecting, 0);
        }
        // Lost again between the last attempt and here: nobody else was going to notice.
        if (!stopping && link is not { Closed: false }) StartReconnecting();
    }

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener that fails must not fail the connection */ }
    }

    // ---- Operations outstanding at a loss ----
    //
    // A mutation whose request may have reached the host when its connection ended is an unknown outcome for its caller, and it is never
    // sent again. The host's journal knows what became of it, so once a connection is back it is asked (ops.status, a lookup that runs
    // nothing) and the answer is given to whoever listens. An id the host has no record of (it restarted, or pruned it) stays unknown.

    private readonly object rememberedGate = new();
    private readonly List<string> remembered = [];
    private int reconciling;
    private int reconcileAgain;

    private void Remember(string clientOpId)
    {
        if (connector is null) return;
        lock (rememberedGate)
        {
            if (remembered.Contains(clientOpId)) return;
            if (remembered.Count >= RememberedOperations) remembered.RemoveAt(0);
            remembered.Add(clientOpId);
        }
        // Noticed after the connection came back (a write that failed before the reader saw the end): ask now.
        if (link is { Closed: false }) RequestReconcile();
    }

    /// <summary>
    /// Adds operations an earlier run of the application left unknown (<c>InterruptedSends.OperationIds</c>) to those to ask the host about:
    /// now if connected, otherwise once connected. A lookup only; nothing is run or sent again.
    /// </summary>
    public void Reconcile(IEnumerable<string> clientOpIds)
    {
        foreach (var id in clientOpIds) Remember(id);
        if (link is { Closed: false }) RequestReconcile();
    }

    private void Forget(string clientOpId)
    {
        lock (rememberedGate) remembered.Remove(clientOpId);
    }

    private void RequestReconcile()
    {
        Interlocked.Exchange(ref reconcileAgain, 1);
        if (Interlocked.CompareExchange(ref reconciling, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref reconcileAgain, 0) == 1 && !stopping) await ReconcileAsync().ConfigureAwait(false);
            }
            catch (Exception) { /* a pass that fails leaves its operations remembered for the next connection */ }
            finally { Volatile.Write(ref reconciling, 0); }
            // Asked for between the last check and the release.
            if (Volatile.Read(ref reconcileAgain) == 1 && !stopping) RequestReconcile();
        }, CancellationToken.None);
    }

    private async Task ReconcileAsync()
    {
        string[] ids;
        lock (rememberedGate) ids = [.. remembered];
        if (ids.Length == 0) return;
        int asked = 0, done = 0, failed = 0, unknown = 0;
        // When each operation was first asked about in this pass, for the point where waiting is given up.
        var firstAsked = new Dictionary<string, long>(StringComparer.Ordinal);
        void Answer(OperationOutcome outcome)
        {
            Forget(outcome.OperationId);
            switch (outcome.Verdict)
            {
                case OperationVerdict.Done: done++; break;
                case OperationVerdict.Failed: failed++; break;
                default: unknown++; break;
            }
            try { Reconciled?.Invoke(this, outcome); } catch (Exception) { /* a listener that fails must not fail the others */ }
        }
        try
        {
            var open = ids.AsEnumerable();
            for (var check = 1; ; check++)
            {
                var running = new List<string>();
                foreach (var id in open)
                {
                    OpStatus status;
                    if (firstAsked.TryAdd(id, Stopwatch.GetTimestamp())) asked++;
                    // The connection went again, or the host could not answer: this and the rest stay remembered for the next connection.
                    try { status = await OperationStatusAsync(id, stop.Token).ConfigureAwait(false); }
                    catch (Exception) { return; }
                    if (status.State == OpState.Running) running.Add(id);
                    else Answer(Outcome(id, status));
                }
                // Still running after the time allowed: it may yet end, but not in time to wait for. It stays unknown and is never sent again.
                var waiting = new List<string>();
                foreach (var id in running)
                {
                    if (Stopwatch.GetElapsedTime(firstAsked[id]) >= RunningGiveUp) Answer(new OperationOutcome(id, OperationVerdict.Unknown));
                    else waiting.Add(id);
                }
                if (waiting.Count == 0) return;
                try { await Task.Delay(check < RunningChecks ? RunningCheckDelay : SlowCheckDelay, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                // A send lost while this waited is asked about now, not after the slow ones are over.
                string[] added;
                lock (rememberedGate) added = [.. remembered.Where(id => !firstAsked.ContainsKey(id))];
                open = waiting.Concat(added);
            }
        }
        finally { log?.Invoke($"ops reconcile asked={asked} done={done} failed={failed} unknown={unknown}"); }
    }

    // What an answer from the journal means for the caller. Anything that does not say plainly that the mutation was done, or was not
    // done, is unknown: the same rule the call itself follows (TranslateMutation).
    private static OperationOutcome Outcome(string clientOpId, OpStatus status)
    {
        switch (status.State)
        {
            case OpState.Done:
                SendReceipt? receipt = null;
                try { receipt = status.Result?.Deserialize<SendReceipt>(ProtocolJson.Options); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
                // A result that is not a send's receipt (a chat, an empty object) is done all the same.
                if (receipt is not { ChatId.Length: > 0, PendingMessageId.Length: > 0 }) receipt = null;
                return new OperationOutcome(clientOpId, OperationVerdict.Done, receipt);
            case OpState.Failed when status.Error is { } error:
                if (!ProtocolErrors.IsKnown(error.Code) || error.Code is ProtocolErrorCodes.Internal or ProtocolErrorCodes.OutcomeUnknown || error.OutcomeUnknown)
                    return new OperationOutcome(clientOpId, OperationVerdict.Unknown);
                var exception = ProtocolErrors.ToException(error);
                return new OperationOutcome(clientOpId, OperationVerdict.Failed, null, exception is SendException or BackendException ? exception.Message : null);
            default:
                return new OperationOutcome(clientOpId, OperationVerdict.Unknown);
        }
    }

    // ---- Calls ----

    public Task<TResult> CallAsync<TResult>(string method, CancellationToken cancellationToken = default) =>
        CallAsync<TResult>(method, default, cancellationToken);

    public Task<TResult> CallAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default) =>
        CallAsync<TResult>(method, ProtocolJson.ToElement(parameters), cancellationToken);

    /// <summary>For a method whose port may answer null (a miss, nothing kept): null comes back as null.</summary>
    public Task<TResult?> CallNullableAsync<TResult>(string method, CancellationToken cancellationToken = default) =>
        CallNullableAsync<TResult>(method, default, cancellationToken);

    public Task<TResult?> CallNullableAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default) =>
        CallNullableAsync<TResult>(method, ProtocolJson.ToElement(parameters), cancellationToken);

    /// <summary>
    /// The host's answer as it came, error and all, for a caller in this library that must tell the protocol's own refusals apart (a write of shared state the
    /// host will never take, which retrying cannot change). Everything else goes through the calls above, which never show a protocol error.
    /// </summary>
    internal Task<ResponseFrame> AnswerAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken) =>
        RoundTripAsync(method, ProtocolJson.ToElement(parameters), cancellationToken);

    private async Task<TResult> CallAsync<TResult>(string method, JsonElement parameters, CancellationToken cancellationToken, Link? on = null)
    {
        var value = await CallNullableAsync<TResult>(method, parameters, cancellationToken, on).ConfigureAwait(false);
        return value ?? throw new BackendException(BackendFailure.InvalidResponse, UnreadableSentence);
    }

    private async Task<TResult?> CallNullableAsync<TResult>(string method, JsonElement parameters, CancellationToken cancellationToken, Link? on = null)
    {
        var response = await RoundTripAsync(method, parameters, cancellationToken, on: on).ConfigureAwait(false);
        if (response.Error is { } error) throw Translate(error, cancellationToken);
        try { return response.ResultAs<TResult>(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { throw new BackendException(BackendFailure.InvalidResponse, UnreadableSentence); }
    }

    // ---- Mutations ----
    //
    // A mutation is never retried here. Once its request has been handed to the connection it may have been done, so whatever
    // then goes wrong (the connection closing, an answer that cannot be read, a failure the host cannot describe) reaches the
    // caller as an unknown outcome, never as a plain failure it could safely try again. Only what the host affirms is
    // reported as it is: a result, an error the backend itself raised, or `cancelled` (the host never asked the provider).

    /// <summary>The sentence for a send whose connection ended before its answer arrived.</summary>
    public const string InterruptedSentence = "The connection to the server was lost while this was being sent. It may or may not have gone through.";
    /// <summary>The sentence for a send whose answer the server could not give clearly.</summary>
    public const string UncertainSentence = "The server's answer was not clear. It may or may not have gone through.";

    /// <summary>A new name for one user action. A mutation's proxy makes one per call and never reuses it for another.</summary>
    public static string NewOperationId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The name for a send (text, reply, attachments): the one its caller planned (<see cref="PlannedOperation"/>, the outbox's, saved before the send so a restart
    /// can ask the host about it), else a new one.
    /// </summary>
    public static string SendOperationId() => PlannedOperation.Take() ?? NewOperationId();

    /// <param name="progress">
    /// Receives what the host reports while the call runs (a Google sign-in's pairing emoji): its <c>progress</c> frames name this call's
    /// operation id, arrive before the answer, and go to no other call and no other connection.
    /// </param>
    /// <remarks>
    /// A send whose connection ended carries its operation id in <see cref="SendException.OperationId"/>; a client made with a connector asks
    /// the host about it after reconnecting and says what it learned through <see cref="Reconciled"/>.
    /// </remarks>
    public async Task<TResult> MutateAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
        where TParams : MutationParams
    {
        var attempt = new Attempt();
        ResponseFrame response;
        // Registered before the request leaves, so the first report cannot find nobody to hear it.
        if (progress is not null) sinks[parameters.ClientOpId] = progress;
        try { response = await RoundTripAsync(method, ProtocolJson.ToElement(parameters), cancellationToken, attempt, operationId: parameters.ClientOpId).ConfigureAwait(false); }
        // The connection ended after the request was handed to it (or while it was being written).
        // Anything that ends the round trip once the request may have reached the host leaves the outcome unknown, however
        // the transport reports it (a closed loopback, an IOException from a pipe or socket): never a plain failure, which
        // the Outbox would read as "not sent".
        catch (Exception ex) when (attempt.Sent && ex is not SendException)
        {
            Remember(parameters.ClientOpId);
            throw new SendException(true, InterruptedSentence) { OperationId = parameters.ClientOpId };
        }
        finally
        {
            if (progress is not null) sinks.TryRemove(parameters.ClientOpId, out _);
        }
        if (response.Error is { } error) throw TranslateMutation(error, cancellationToken);
        TResult? value;
        try { value = response.ResultAs<TResult>(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { throw new SendException(true, UncertainSentence); }
        return value ?? throw new SendException(true, UncertainSentence);
    }

    /// <summary>A mutation whose port returns nothing.</summary>
    public async Task MutateAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default)
        where TParams : MutationParams =>
        await MutateAsync<TParams, Done>(method, parameters, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What the host's journal holds for an operation this client started: for a caller that lost its connection and wants to know
    /// what became of the call. A lookup; it never runs or repeats anything.
    /// </summary>
    public Task<OpStatus> OperationStatusAsync(string clientOpId, CancellationToken cancellationToken = default) =>
        CallAsync<OpStatusParams, OpStatus>(ProtocolMethods.OpsStatus, new(clientOpId), cancellationToken);

    /// <summary>
    /// How many mutations (sends, reactions, edits, deletions, settings) this client has handed to the host on its current connection and has no answer to yet.
    /// What a quit waits for beyond the outbox's sends: a reaction or an unsend in flight has the same question after the connection is let go, and the answer
    /// that would settle it goes with the connection.
    /// </summary>
    public int MutationsInFlight => link is { } current ? current.Pending.Values.Count(waiter => waiter.OperationId is not null && waiter.Sent && !waiter.Done.Task.IsCompleted) : 0;

    private sealed class Attempt { public volatile bool Sent; }

    // The callers waiting for progress, by the operation id of the call. Only this client's own calls are in it.
    private readonly ConcurrentDictionary<string, IProgress<string>> sinks = new(StringComparer.Ordinal);

    private static Exception TranslateMutation(ProtocolError error, CancellationToken cancellationToken)
    {
        // A code this build does not know says nothing certain about whether the thing was done.
        if (!ProtocolErrors.IsKnown(error.Code)) return new SendException(true, UncertainSentence);
        var exception = ProtocolErrors.ToException(error);
        // The host's own failure (not an error the backend raised) can follow the provider call, as when its answer cannot be written.
        if (exception is ProtocolException { Error.Code: ProtocolErrorCodes.Internal }) return new SendException(true, UncertainSentence);
        return Translate(exception, cancellationToken);
    }

    private async Task<ResponseFrame> RoundTripAsync(string method, JsonElement parameters, CancellationToken cancellationToken, Attempt? attempt = null,
        Link? on = null, string? operationId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = on ?? await LinkAsync(cancellationToken).ConfigureAwait(false);
        var id = "r" + Interlocked.Increment(ref lastId);
        var waiter = new Waiter(operationId);
        connection.Pending[id] = waiter;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Registered before the connection is checked, so a close that lands in between still fails this call.
            if (connection.Closed) throw Lost();
            // From here the host may have the request.
            if (attempt is not null) attempt.Sent = true;
            waiter.Sent = operationId is not null;
            await SendAsync(connection, new RequestFrame(id, method, parameters)).ConfigureAwait(false);
            // After the request is sent, so the host never sees a cancel for a request it has not read.
            using var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(static state =>
                {
                    var (self, on, requestId, waiting, token) = ((ProtocolClient, Link, string, Waiter, CancellationToken))state!;
                    _ = self.SendCancelAsync(on, requestId);
                    _ = self.GiveUpAfterCancelAsync(waiting, token);
                }, (this, connection, id, waiter, cancellationToken))
                : default;
            return await waiter.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            connection.Pending.TryRemove(id, out _);
            stats?.Record(method, Stopwatch.GetElapsedTime(started));
        }
    }

    private static Exception Translate(ProtocolError error, CancellationToken cancellationToken) => Translate(ProtocolErrors.ToException(error), cancellationToken);

    private static Exception Translate(Exception thrown, CancellationToken cancellationToken) => thrown switch
    {
        ProtocolException => new BackendException(BackendFailure.InvalidResponse, FailedSentence),
        OperationCanceledException when cancellationToken.IsCancellationRequested => new OperationCanceledException(cancellationToken),
        var exception => exception,
    };

    private static BackendException Lost() => new(BackendFailure.Unavailable, LostSentence);

    private static async Task SendAsync(Link on, Frame frame)
    {
        try { await on.Channel.SendAsync(Frames.Serialize(frame)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ChannelClosedException or ObjectDisposedException) { throw Lost(); }
    }

    /// <summary>
    /// How long a call whose caller has given up (cancelled, or ran out of its own time) still waits for the host's answer before it ends without one. The cancel
    /// frame asks the host to stop and to answer; a host that answers pings but keeps the answer back (a stuck provider call that ignores the cancel) must not leave
    /// the caller waiting for ever on a connection that looks alive. Long enough for a host that is merely slow to answer a cancel (its provider call is cooperative
    /// and a busy host can take seconds), short enough that a send stuck in "Sending" is reported within the time a person would still be looking.
    /// </summary>
    public static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(12);

    // Ends a call that is still unanswered a grace after its caller cancelled it. A mutation ends as an interrupted one (MutateAsync reads it as the connection
    // ending after the request was sent: outcome unknown, remembered, never sent again); a read ends as cancelled, a plain failure. An answer that comes later finds
    // nobody waiting and is dropped (Receive ignores an unknown id).
    private async Task GiveUpAfterCancelAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try { await waiter.Done.Task.WaitAsync(CancelGrace).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            waiter.Done.TrySetException(waiter.OperationId is not null ? Lost() : new OperationCanceledException(cancellationToken));
        }
        catch (Exception) { /* answered, or the connection ended: either way the call has its outcome */ }
    }

    private async Task SendCancelAsync(Link on, string id)
    {
        try { await on.Channel.SendAsync(Frames.Serialize(new CancelFrame(id))).ConfigureAwait(false); }
        catch (Exception) { /* the connection is gone, and its close answers the call */ }
    }

    private async Task ReadAsync(Link on)
    {
        try
        {
            while (await on.Channel.Incoming.WaitToReadAsync().ConfigureAwait(false))
                while (on.Channel.Incoming.TryRead(out var text))
                    Receive(on, text);
        }
        catch (Exception) { }
        on.Closed = true;
        var lost = Lost();
        // The mutations whose request was handed over are remembered before anyone hears the connection ended, so a reconnect that is
        // quick cannot ask before they are known.
        foreach (var waiter in on.Pending.Values)
            if (waiter is { Sent: true, OperationId: { } operationId }) Remember(operationId);
        on.Greeting.TrySetException(lost);
        foreach (var waiter in on.Pending.Values) waiter.Done.TrySetException(lost);
        // What the feed had already received is still read, then the reader is told the connection is gone.
        Interlocked.Exchange(ref on.Inbox, null)?.Complete(lost);
        if (connector is not null && !stopping && ReferenceEquals(link, on))
        {
            // Nothing is connected through a host that cannot be reached.
            cache?.MarkLost();
            StartReconnecting();
        }
    }

    // ---- The event feed ----

    /// <summary>How many events a client holds for a reader that has not caught up, before it replaces them with a resync.</summary>
    public const int EventInboxCapacity = 4096;

    /// <summary>
    /// Starts this connection's event feed from the position the caller last saw (<paramref name="since"/> in <paramref name="epoch"/>),
    /// or from nowhere. Replaces any feed this client already has. The returned <see cref="ClientEvents"/> reads the items in order;
    /// once the connection is lost it reads what had arrived and then throws an unavailable <see cref="BackendException"/>. A feed does not
    /// carry over to a new connection: its owner subscribes again from the position it last applied, which resumes if the host is the
    /// same run and still holds it, and otherwise starts with a resync.
    /// </summary>
    public async Task<ClientEvents> SubscribeEventsAsync(long? since, string? epoch, CancellationToken cancellationToken = default, bool typing = false)
    {
        var connection = await LinkAsync(cancellationToken).ConfigureAwait(false);
        var queue = new EventQueue(EventInboxCapacity);
        // In place before the request leaves, so the first frame after the answer finds somewhere to go.
        Interlocked.Exchange(ref connection.Inbox, queue)?.Complete();
        try
        {
            var answer = await CallAsync<EventsSubscribed>(ProtocolMethods.EventsSubscribe, ProtocolJson.ToElement(new EventsSubscribeParams(since, epoch, typing)), cancellationToken, connection).ConfigureAwait(false);
            return new ClientEvents(answer, queue);
        }
        catch
        {
            Interlocked.CompareExchange(ref connection.Inbox, null, queue);
            queue.Complete();
            throw;
        }
    }

    /// <summary>Ends this connection's event feed on the host. Safe when there is none; the host stops sending before it answers.</summary>
    public async Task UnsubscribeEventsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await LinkAsync(cancellationToken).ConfigureAwait(false);
        await CallAsync<Done>(ProtocolMethods.EventsUnsubscribe, default, cancellationToken, connection).ConfigureAwait(false);
        Interlocked.Exchange(ref connection.Inbox, null)?.Complete();
    }

    /// <summary>
    /// Keeps the host's state coming into the cache before anything reads the event feed: the window's sync loop subscribes only once it starts, and a
    /// launch that decides what to restore from the account ports needs the state first (SWITCHBOARD.md 3.2b). It subscribes only if this connection
    /// has no feed, so it never replaces the sync loop's; the feed it holds is discarded here, since the cache already took each state as its frame
    /// was read, and the sync loop's own subscription (which replaces this one, on the host as here) begins from nowhere anyway. Returns when
    /// that happens or the connection ends. A client without a cache has nothing to feed and returns at once.
    /// </summary>
    public async Task HoldStateFeedAsync(CancellationToken cancellationToken = default)
    {
        if (cache is null) return;
        var connection = await LinkAsync(cancellationToken).ConfigureAwait(false);
        var queue = new EventQueue(EventInboxCapacity);
        if (Interlocked.CompareExchange(ref connection.Inbox, queue, null) is not null) return;
        try
        {
            await CallAsync<EventsSubscribed>(ProtocolMethods.EventsSubscribe, ProtocolJson.ToElement(new EventsSubscribeParams(null, null, false)), cancellationToken, connection).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref connection.Inbox, null, queue);
            queue.Complete();
            throw;
        }
        try
        {
            while (await queue.ReadAsync(cancellationToken).ConfigureAwait(false) is not null) { }
        }
        catch (BackendException) { /* the connection ended; the sync loop subscribes again from nowhere */ }
    }

    // The cache raises its change events from here, on the connection's reader. Its listeners are the account ports' Changed handlers, which
    // are written to be raised from any thread (a backend raises them from whichever thread noticed) and must not wait on this client.
    private void ApplyState(EventFrame frame)
    {
        HostState? pushed = null;
        try { pushed = frame.Payload.Deserialize<HostState>(ProtocolJson.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        if (pushed is not null) cache!.Apply(pushed, frame.Epoch, frame.Seq);
    }

    private void ReceiveProgress(EventFrame frame)
    {
        ProgressPayload? report = null;
        try { report = frame.HasPayload ? frame.Payload.Deserialize<ProgressPayload>(ProtocolJson.Options) : null; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        if (report?.ClientOpId is null || report.Text is null || !sinks.TryGetValue(report.ClientOpId, out var sink)) return;
        // A sink that throws must not end the connection's reader.
        try { sink.Report(report.Text); } catch (Exception) { }
    }

    private void ReceiveTyping(EventFrame frame)
    {
        if (cache is null) return;
        TypingUpdate? update = null;
        try { update = frame.HasPayload ? frame.Payload.Deserialize<TypingUpdate>(ProtocolJson.Options) : null; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        // A snapshot this build cannot read says nothing it can show; the next change replaces it.
        if (update?.ChatId is not null && update.Participants is not null) cache.ReceiveTyping(update);
    }

    private void ReceiveShared(EventFrame frame)
    {
        SharedChange? change = null;
        try { change = frame.HasPayload ? frame.Payload.Deserialize<SharedChange>(ProtocolJson.Options) : null; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        // A commit this build cannot read is one the replica is missing: a resync makes it read what it missed (state.sync).
        if (change?.Epoch is null || change.Rows is null || change.Rows.Any(row => row?.Key is null))
        {
            Raise(FeedResynced);
            return;
        }
        try { SharedChanged?.Invoke(this, change); } catch (Exception) { /* a listener that fails must not fail the connection */ }
    }

    /// <summary>A change to the server's whole history (protocol 24), raised on the connection's reader. A change this build cannot read is dropped.</summary>
    public event EventHandler<HistoryChange>? HistoryChanged;

    /// <summary>
    /// A realtime event as its frame is read, on the connection's reader and ahead of the feed's own item for it: what keeps a copy of the history (<see cref="HistoryCopies"/>)
    /// learns that a conversation is about to change before anything reads it for the change. A listener queues and returns.
    /// </summary>
    public event EventHandler<RealtimeEvent>? RealtimeRead;

    /// <summary>A history frame that could not be read, as it is read: whatever copy it changed is behind until it asks again.</summary>
    public event EventHandler? HistoryUnreadable;

    /// <summary>The current connection has an event feed (subscribed, or subscribing): changes reach <see cref="HistoryChanged"/> as they are made.</summary>
    public bool HasEventFeed => link is { Closed: false } current && Volatile.Read(ref current.Inbox) is not null;

    private void ReceiveHistory(EventFrame frame)
    {
        HistoryChange? change = null;
        try { change = frame.HasPayload ? frame.Payload.Deserialize<HistoryChange>(ProtocolJson.Options) : null; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        try
        {
            if (change?.ChatId is null || change.To is null) HistoryUnreadable?.Invoke(this, EventArgs.Empty);
            else HistoryChanged?.Invoke(this, change);
        }
        catch (Exception) { /* a listener that fails must not fail the connection */ }
    }

    private void ReceiveEvent(Link on, EventFrame frame)
    {
        // Progress is for the call that is running, not for the feed.
        if (frame.Kind == EventKinds.Progress)
        {
            ReceiveProgress(frame);
            return;
        }
        // A state is applied the moment its frame is read, ahead of everything that follows it on the connection, the answer to the
        // call that caused it included: the window that reads the state right after an account action sees it (SWITCHBOARD.md 2.6c).
        // The feed delivers the same state again in its place, and the cache ignores what it already holds.
        if (frame.Kind == EventKinds.State && cache is not null && frame.HasPayload) ApplyState(frame);
        // Typing is not part of the sequence the feed resumes by: it goes straight to the cache's listeners, which keep the newest
        // snapshot of each conversation, and never takes a place in the inbox or moves the position.
        if (frame.Kind == EventKinds.Typing)
        {
            ReceiveTyping(frame);
            return;
        }
        // A shared-state commit goes to the replica as it is read and takes no place in the inbox: the application's feed has nothing to do with it, and a
        // feed that resumes from an earlier position is given it again, which the replica ignores by its rev.
        if (frame.Kind == EventKinds.Shared)
        {
            ReceiveShared(frame);
            return;
        }
        // A change to the server's whole history (protocol 24) goes to whoever keeps a copy and takes no place in the inbox either: the realtime event that caused it
        // follows it there, as it always did.
        if (frame.Kind == EventKinds.History)
        {
            ReceiveHistory(frame);
            return;
        }
        if (frame.Kind == EventKinds.Resync) Raise(FeedResynced);
        if (on.Inbox is not { } queue) return;
        EventItem item;
        if (frame.Kind == EventKinds.Resync) item = new EventItem(frame.Seq, null);
        else if (frame.Kind == EventKinds.Realtime)
        {
            // A payload this build cannot read is a change it cannot apply: a resync at that position makes up for it.
            RealtimeEvent? change = null;
            try { change = frame.HasPayload ? frame.Payload.Deserialize<RealtimeEvent>(ProtocolJson.Options) : null; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            if (change is not null)
            {
                try { RealtimeRead?.Invoke(this, change); } catch (Exception) { /* a listener that fails must not fail the connection */ }
            }
            item = new EventItem(frame.Seq, change);
        }
        else if (frame.Kind == EventKinds.State)
        {
            // A state this build cannot read changes nothing it knows of: the cache keeps the last it could read, and the next one replaces it.
            HostState? state = null;
            try { state = frame.HasPayload ? frame.Payload.Deserialize<HostState>(ProtocolJson.Options) : null; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            if (state is null) return;
            item = new EventItem(frame.Seq, null, state);
        }
        else if (frame.Kind == EventKinds.Account)
        {
            // A signal this build cannot read, or one it does not know, changes nothing it knows of.
            AccountSignal? signal = null;
            try { signal = frame.HasPayload ? frame.Payload.Deserialize<AccountSignal>(ProtocolJson.Options) : null; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            if (signal is null) return;
            item = new EventItem(frame.Seq, null, null, signal);
        }
        else if (frame.Kind == EventKinds.CaughtUp)
        {
            // A catch-up this build cannot read prepares nothing ahead; the conversation asks for its pictures when it is drawn.
            CaughtUpNotice? notice = null;
            try { notice = frame.HasPayload ? frame.Payload.Deserialize<CaughtUpNotice>(ProtocolJson.Options) : null; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            if (notice?.ChatId is null || notice.Ids is null) return;
            item = new EventItem(frame.Seq, null, null, null, notice);
        }
        // A kind this build does not know says nothing it can act on.
        else return;
        var dropped = queue.Push(item);
        if (dropped > 0) log?.Invoke($"protocol events inbox overflow dropped={dropped} action=resync");
    }

    private void Receive(Link on, string text)
    {
        // A frame this build cannot read is dropped: the call it answers, if any, waits for the connection to end.
        if (!Frames.TryParse(text, out var frame, out _)) return;
        switch (frame)
        {
            case HelloFrame hello:
                if (hello.Error is not null || hello.Protocol < minProtocol || hello.Protocol > protocol
                    || RequiredServerApp is { } required && !string.Equals(hello.App, required, StringComparison.Ordinal))
                {
                    log?.Invoke("protocol refused reason=version");
                    on.Greeting.TrySetException(new BackendException(BackendFailure.InvalidResponse, VersionSentence));
                }
                else
                {
                    Version = hello.Protocol;
                    log?.Invoke($"protocol connected version={hello.Protocol}");
                    on.Greeting.TrySetResult(hello.Protocol);
                }
                break;
            case ResponseFrame response:
                if (on.Pending.TryGetValue(response.Id, out var waiter)) waiter.Done.TrySetResult(response);
                break;
            case EventFrame feedFrame:
                ReceiveEvent(on, feedFrame);
                break;
        }
    }

    /// <summary>Closes the connection; what is outstanding fails as unavailable. A client with a connector does not connect again.</summary>
    public void Close()
    {
        if (connector is null)
        {
            onlyChannel!.Close();
            return;
        }
        stopping = true;
        Interlocked.MemoryBarrier();
        stop.Cancel();
        attempting?.Channel.Close();
        link?.Channel.Close();
    }

    public void Dispose()
    {
        stats?.Flush();
        Close();
    }
}
