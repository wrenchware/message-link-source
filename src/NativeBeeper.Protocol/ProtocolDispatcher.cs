using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>
/// The host's end of one connection: answers <c>hello</c>, then runs each <c>req</c> against the real port and sends
/// exactly one <c>res</c> for it. Requests run at once, each on its own task, so a slow phone read never holds up
/// another call. A <c>cancel</c> signals the request's token and nothing more: a request that had not finished answers
/// <c>cancelled</c>, one that had answers its result (a backend that ignores its token still gets to answer).
/// Errors reach the wire only through <see cref="ProtocolErrors.From"/>, so no exception message leaves the host. A mutation
/// goes through the <see cref="OperationJournal"/>, so a repeat of the same client operation id is answered, never done twice.
/// </summary>
public sealed partial class ProtocolDispatcher(IFrameChannel channel, ProtocolServices services, string appVersion,
    ProtocolCallStats? stats = null, Action<string>? log = null, OperationJournal? journal = null, MediaReadCache? mediaCache = null)
{
    private ConversationSync? SyncService => services.ConversationSync;
    private IWholeHistory? WholeHistoryService => services.WholeHistory;

    private delegate Task<byte[]> Run(ProtocolServices services, OperationJournal journal, RequestFrame request, CancellationToken cancellationToken);

    /// <summary>A mutation checked and ready: the caller's operation id and the call, not yet made.</summary>
    private sealed record Prepared(string ClientOpId, Func<CancellationToken, Task<byte[]>> Call, string? SendChatId = null,
        Func<CancellationToken, Task>? Before = null, bool Outlives = false);

    private delegate Prepared Prepare(ProtocolDispatcher connection, ProtocolServices services, RequestFrame request);

    /// <summary>What a method that belongs to the connection, not to a port, may do once its answer has been sent.</summary>
    private sealed class AfterResponse { public Action? Then; }

    private delegate Task<byte[]> Own(ProtocolDispatcher connection, RequestFrame request, AfterResponse after, CancellationToken cancellationToken);

    // A read has Run; a mutation has Prepare, which validates and finds its port without recording anything, so a request
    // that is refused at the door never takes up an operation id. A method about the connection itself (the event feed) has Own.
    // Settles: the call can change an account's state, so the answer waits for the state it caused (see SettleAsync).
    private sealed record Route(Type? Params, Type Result, Run? Run, Prepare? Prepare = null, Own? Own = null, bool Settles = false);

    /// <summary>
    /// What makes a mutation happen at most once. A host that serves more than one connection passes the same journal to each
    /// dispatcher, so a client that reconnects finds the operation it started; a dispatcher given none keeps its own.
    /// </summary>
    public OperationJournal Journal { get; } = journal ?? new OperationJournal(log: log);

    // The bytes of the media this connection is reading, between one chunk's request and the next (see MediaReadCache).
    private readonly MediaReadCache readCache = mediaCache ?? new MediaReadCache();

    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);
    // The running requests that change something at a provider (a send, a reaction, a read mark): they outlive the connection by OutlivingGrace.
    private readonly ConcurrentDictionary<string, byte> outliving = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a provider change already under way keeps running once its connection has closed. Cut off at once, a send that had reached the service
    /// was journaled as an unknown outcome and went anyway: the phone dropped mid-send and showed its two texts as uncertain until Google's copies came
    /// 40 s later (owner's phone, 2026-10-22). Let run, the journal holds what happened, and the client that comes back reads it (ops.status).
    /// </summary>
    public static TimeSpan OutlivingGrace { get; set; } = TimeSpan.FromMinutes(2);
    // Only the read loop touches it.
    private bool greeted;
    // The version this connection's hello chose. Written by the read loop before any request of the connection is run, and read by those requests and the feed.
    private volatile int negotiated = ProtocolVersion.Minimum;

    /// <summary>Reads frames until the connection closes, then cancels what is still running and closes it from this end.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (await channel.Incoming.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (channel.Incoming.TryRead(out var text))
                    await ReceiveAsync(text).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A source is never disposed: a request's own task may be racing a cancel, and an undisposed source that never
            // started a timer or linked to another holds nothing.
            foreach (var (id, source) in running)
                if (outliving.ContainsKey(id)) source.CancelAfter(OutlivingGrace); else source.Cancel();
            await EndFeedAsync().ConfigureAwait(false);
            // What this connection stated no longer counts (HostClientState), nor its version.
            EndClientState();
            services.Connections?.Closed(this);
            channel.Close();
        }
    }

    // ---- The event feed: at most one per connection ----

    private sealed record Feed(EventSubscription Subscription, CancellationTokenSource Cancel, TaskCompletionSource Begin, Task Pump, FeedPosition Sent,
        Task? TypingPump = null);

    private readonly SemaphoreSlim feedGate = new(1, 1);
    private Feed? feed;

    // The feed's frames follow the answer that announced it, never precede it: the pump waits for Begin, which is
    // released once that answer has been sent.
    private async Task<byte[]> SubscribeAsync(RequestFrame request, AfterResponse after, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<EventsSubscribeParams>(request);
        var events = services.Events ?? throw new NotSupportedException();
        await feedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopFeedAsync().ConfigureAwait(false);
            var subscription = events.Subscribe(parameters.Since, parameters.Epoch);
            var cancel = new CancellationTokenSource();
            var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // What the client already holds counts as sent: a feed that resumes after `since` has nothing older to deliver.
            var sent = new FeedPosition(subscription.Resync ? 0 : parameters.Since ?? 0);
            // Typing of others, for a client that asked: beside the events, not in their sequence (EventKinds.Typing).
            Task? typingPump = null;
            if (parameters.Typing && services.TypingFeed is { } hub)
            {
                var typing = hub.Subscribe();
                typingPump = Task.Run(() => TypingPumpAsync(typing, begin.Task, subscription.Epoch, cancel.Token), CancellationToken.None);
            }
            feed = new Feed(subscription, cancel, begin, Task.Run(() => PumpAsync(subscription, begin.Task, sent, cancel.Token), CancellationToken.None), sent, typingPump);
            after.Then = () => begin.TrySetResult();
            log?.Invoke($"protocol events subscribed resync={(subscription.Resync ? "true" : "false")}");
            return JsonSerializer.SerializeToUtf8Bytes(new EventsSubscribed(subscription.Epoch, subscription.Head, subscription.Resync), ProtocolJson.Options);
        }
        finally { feedGate.Release(); }
    }

    private async Task<byte[]> UnsubscribeAsync(CancellationToken cancellationToken)
    {
        await feedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopFeedAsync().ConfigureAwait(false); }
        finally { feedGate.Release(); }
        return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
    }

    private async Task EndFeedAsync()
    {
        await feedGate.WaitAsync().ConfigureAwait(false);
        try { await StopFeedAsync().ConfigureAwait(false); }
        finally { feedGate.Release(); }
    }

    // Returns once the pump has stopped, so no frame of the old feed follows whatever answer comes next.
    private async Task StopFeedAsync()
    {
        if (feed is not { } old) return;
        feed = null;
        old.Subscription.Dispose();
        old.Cancel.Cancel();
        old.Begin.TrySetCanceled();
        await old.Pump.ConfigureAwait(false);
        if (old.TypingPump is { } typing) await typing.ConfigureAwait(false);
    }

    // A typing frame is not part of the feed's sequence: seq 0, never replayed, and what a slow connection has not taken yet is replaced by
    // the newest snapshot of each conversation (TypingSlot). It follows the answer that announced the feed, like the feed itself.
    private async Task TypingPumpAsync(TypingSubscription typing, Task begin, string epoch, CancellationToken cancellationToken)
    {
        try
        {
            await begin.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (await typing.ReadAsync(cancellationToken).ConfigureAwait(false) is { } update)
                await channel.SendAsync(Frames.Serialize(new EventFrame(0, epoch, EventKinds.Typing, ProtocolJson.ToElement(update))), cancellationToken).ConfigureAwait(false);
        }
        // A connection that cannot be written to has nobody to tell; its own close ends the dispatcher.
        catch (Exception) { }
        finally { typing.Dispose(); }
    }

    private async Task PumpAsync(EventSubscription subscription, Task begin, FeedPosition sent, CancellationToken cancellationToken)
    {
        try
        {
            await begin.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (await subscription.ReadAsync(cancellationToken).ConfigureAwait(false) is { } item)
            {
                // A commit to the shared state that this connection's version does not see takes no frame, but its place in the sequence counts as sent.
                if (item.Shared is { } commit)
                {
                    if (SharedFor(commit) is not { } visible)
                    {
                        sent.Advance(item.Seq);
                        continue;
                    }
                    item = item with { Shared = visible };
                }
                // A change to the whole history goes only to a connection of 24 or later; below, its place counts as sent (stage 8).
                // The backup signal (protocol 26) likewise: an older client was never told the word.
                if (item.History is not null && negotiated < ProtocolVersion.WholeHistory || item.Row is not null && negotiated < ProtocolVersion.Feed
                    || item.Signal is { Name: AccountSignals.Backup } && negotiated < ProtocolVersion.ServerBackup
                    || item.Signal is { Name: AccountSignals.Push } && negotiated < ProtocolVersion.PushTest
                    || item.Signal is { Name: AccountSignals.PhoneContacts } && negotiated < ProtocolVersion.PhoneContacts)
                {
                    sent.Advance(item.Seq);
                    continue;
                }
                var frame = item.IsResync ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.Resync)
                    : item.History is { } history ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.History, ProtocolJson.ToElement(history))
                    : item.Row is { } row ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.Row, ProtocolJson.ToElement(row))
                    : item.State is { } state ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.State, ProtocolJson.ToElement(ForConnection(state)))
                    : item.Signal is { } signal ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.Account, ProtocolJson.ToElement(signal))
                    : item.CaughtUp is { } caughtUp ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.CaughtUp, ProtocolJson.ToElement(caughtUp))
                    : item.Shared is { } shared ? new EventFrame(item.Seq, subscription.Epoch, EventKinds.Shared, ProtocolJson.ToElement(shared))
                    : new EventFrame(item.Seq, subscription.Epoch, EventKinds.Realtime, ProtocolJson.ToElement(item.Event!));
                await channel.SendAsync(Frames.Serialize(frame), cancellationToken).ConfigureAwait(false);
                // Only now is the frame in the channel ahead of whatever is written next, which is what an answer that waits for it needs.
                sent.Advance(item.Seq);
            }
        }
        // A connection that cannot be written to has nobody to feed; its own close ends the dispatcher.
        catch (Exception) { }
        finally
        {
            subscription.Dispose();
            sent.End();
        }
    }

    // The state as this connection's version can read it. A client below 16 has no "deviceMissing" among the encrypted states, and the word in a state frame would make the whole frame
    // unreadable to it (it would keep no state at all). It is given Down: encrypted conversations are on and carry nothing, which is true, says "can't send or receive here right now" without
    // claiming Facebook removed the device (DeviceGone) and without the "turn it on again to register" prompt that Facebook's removal gets, and is never the healthy "On" that Ready and Unknown show.
    // The host's own state, and a client of 16, keep the real word.
    private HostState ForConnection(HostState state) =>
        negotiated < ProtocolVersion.AccountMove && state.Messenger is { Encrypted: MessengerEncryption.DeviceMissing } messenger
            ? state with { Messenger = messenger with { Encrypted = MessengerEncryption.Down } }
            : state;

    /// <summary>
    /// How far this connection's feed has written: the highest sequence number in the channel. An answer that must follow a state waits
    /// for it, so on the one connection the state precedes the answer. Ending the feed releases every waiter.
    /// </summary>
    private sealed class FeedPosition(long start)
    {
        private readonly object gate = new();
        private readonly List<(long Seq, TaskCompletionSource Reached)> waiters = [];
        private long sent = start;
        private bool ended;

        public void Advance(long seq)
        {
            List<TaskCompletionSource>? ready = null;
            lock (gate)
            {
                if (seq > sent) sent = seq;
                for (var i = waiters.Count - 1; i >= 0; i--)
                    if (waiters[i].Seq <= sent) { (ready ??= []).Add(waiters[i].Reached); waiters.RemoveAt(i); }
            }
            if (ready is not null) foreach (var waiter in ready) waiter.TrySetResult();
        }

        public void End()
        {
            (long Seq, TaskCompletionSource Reached)[] all;
            lock (gate)
            {
                ended = true;
                all = [.. waiters];
                waiters.Clear();
            }
            foreach (var (_, reached) in all) reached.TrySetResult();
        }

        public Task ReachedAsync(long seq)
        {
            lock (gate)
            {
                if (seq <= sent || ended) return Task.CompletedTask;
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((seq, reached));
                return reached.Task;
            }
        }
    }

    // ---- Closing: answer what was accepted, accept nothing more ----

    private readonly object answeringGate = new();
    private int answering;
    private TaskCompletionSource? answered;
    // Read and written only under answeringGate, so that "is it draining" and "count this request" are one step: a request accepted a moment before
    // draining began is counted (and waited for), one after it is refused, and none can slip between the two uncounted.
    private bool draining;

    /// <summary>From now on a request is refused (<c>unavailable</c>, before anything runs); the ones already accepted go on to their answers (see <see cref="WhenAnsweredAsync"/>).</summary>
    public void BeginDraining()
    {
        lock (answeringGate) draining = true;
    }

    /// <summary>Completes once every request accepted so far has had its answer written to the channel. A request accepted after this call is not waited for.</summary>
    public Task WhenAnsweredAsync()
    {
        lock (answeringGate) return answered?.Task ?? Task.CompletedTask;
    }

    /// <summary>
    /// The most requests one connection may have accepted and not yet answered. Each runs on a task of its own and may hold a response in memory until
    /// the peer reads it, so a connection that asks without end (or never reads what it is sent) is held to this rather than to the host's memory. Real
    /// use is a few dozen at a burst, a stress run of 200 at once among them; a request over it is refused, before anything runs, as <c>unavailable</c> and transient, the
    /// same refusal and the same standing as one made while the server is closing: it never reached the journal, so a mutation refused this way
    /// definitely did not happen. One that was accepted is never dropped.
    /// </summary>
    public const int DefaultMaxRequestsInFlight = 256;

    /// <summary>This connection's ceiling (<see cref="DefaultMaxRequestsInFlight"/>); the harness lowers it.</summary>
    public int MaxRequestsInFlight { get; init; } = DefaultMaxRequestsInFlight;

    private enum Admission { Accepted, Draining, Busy }

    // Not Accepted when draining has begun or the connection is at its ceiling: nothing is counted, and the request is refused.
    private Admission TryBeginAnswer()
    {
        lock (answeringGate)
        {
            if (draining) return Admission.Draining;
            if (answering >= MaxRequestsInFlight) return Admission.Busy;
            if (answering++ == 0) answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Admission.Accepted;
        }
    }

    // The first refusal and then one in a hundred, as the door's refusals: a peer that keeps asking does not fill the log.
    private int busy;

    private void NoteBusy()
    {
        var count = Interlocked.Increment(ref busy);
        if (count == 1 || count % 100 == 0) log?.Invoke($"protocol requests refused reason=busy limit={MaxRequestsInFlight} count={count}");
    }

    private void EndAnswer()
    {
        TaskCompletionSource? done = null;
        lock (answeringGate)
        {
            if (--answering == 0) { done = answered; answered = null; }
        }
        done?.TrySetResult();
    }

    /// <summary><see cref="RunAsync"/> on its own task, so the caller never waits on the first frame.</summary>
    public Task Start(CancellationToken cancellationToken = default) => Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);

    private async Task ReceiveAsync(string text)
    {
        if (!Frames.TryParse(text, out var frame, out var refusal))
        {
            // Answer a request whose shape was wrong rather than leave its caller waiting; anything else has nobody to answer.
            if (SalvageId(text) is { } id) await SendAsync(ResponseFrame.Failure(id, refusal)).ConfigureAwait(false);
            return;
        }
        switch (frame)
        {
            case HelloFrame hello:
                await GreetAsync(hello).ConfigureAwait(false);
                break;
            case RequestFrame request when !greeted:
                await SendAsync(ResponseFrame.Failure(request.Id, new ProtocolError(ProtocolErrorCodes.InvalidRequest, "Say hello first."))).ConfigureAwait(false);
                break;
            case RequestFrame request:
                // Before anything is run: a request refused for this is one the journal never saw, so its caller may ask again of the next host. The
                // check and the count are one step (TryBeginAnswer), so a request is either waited for by a drain or refused by it.
                var admission = TryBeginAnswer();
                if (admission != Admission.Accepted)
                {
                    await SendAsync(ResponseFrame.Failure(request.Id, new ProtocolError(ProtocolErrorCodes.Unavailable,
                        admission == Admission.Busy ? "The server has too many requests from this device at once." : "The server is closing.", Transient: true))).ConfigureAwait(false);
                    if (admission == Admission.Busy) NoteBusy();
                    break;
                }
                var source = new CancellationTokenSource();
                if (!running.TryAdd(request.Id, source))
                {
                    EndAnswer();
                    await SendAsync(ResponseFrame.Failure(request.Id, new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That request id is already in use."))).ConfigureAwait(false);
                    break;
                }
                // Registered here, before the task starts, so a cancel that follows this frame always finds it.
                _ = Task.Run(async () =>
                {
                    try { await ServeAsync(request, source).ConfigureAwait(false); }
                    finally { EndAnswer(); }
                }, CancellationToken.None);
                break;
            case CancelFrame cancel:
                if (running.TryGetValue(cancel.Id, out var target)) target.Cancel();
                break;
            // A response or event from a client has no meaning here.
        }
    }

    /// <summary>
    /// The highest protocol version this connection offers: <see cref="ProtocolVersion.Current"/>, unless a host is made to speak less (the harness's stand-in
    /// for a host from before a version, which a client must still work with).
    /// </summary>
    public int HighestProtocol { get; init; } = ProtocolVersion.Current;

    private async Task GreetAsync(HelloFrame hello)
    {
        if (ProtocolVersion.Negotiate(hello.Protocol, hello.MinProtocol, HighestProtocol) is not { } version)
        {
            log?.Invoke("protocol hello refused reason=version");
            await SendAsync(new HelloFrame(Math.Min(HighestProtocol, ProtocolVersion.Current), appVersion, Server: HelloFrame.ServerName, MinProtocol: ProtocolVersion.Minimum,
                Error: new ProtocolError(ProtocolErrorCodes.ProtocolUnsupported, "This version of Messages cannot talk to that server."))).ConfigureAwait(false);
            return;
        }
        greeted = true;
        negotiated = version;
        // Before the answer, so the host goes by this connection's version before it can ask for anything.
        services.Connections?.Opened(this, version);
        // The peer's version word is logged only when it looks like one.
        log?.Invoke($"protocol hello version={version} app={(VersionWord().IsMatch(hello.App) ? hello.App : "other")}");
        await SendAsync(new HelloFrame(version, appVersion, Server: HelloFrame.ServerName)).ConfigureAwait(false);
    }

    private async Task ServeAsync(RequestFrame request, CancellationTokenSource source)
    {
        var started = Stopwatch.GetTimestamp();
        string response;
        Route? route = null;
        var after = new AfterResponse();
        try
        {
            if (!Routes.TryGetValue(request.Method, out route))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.UnknownMethod, "The server does not offer that method."));
            // The result is serialized once, straight into the frame, rather than into a JSON tree and out of it again.
            if (route.Own is { } own) response = Frames.SerializeResult(request.Id, await own(this, request, after, source.Token).ConfigureAwait(false));
            else if (route.Prepare is { } prepare)
            {
                var mutation = prepare(this, services, request);
                if (mutation.Outlives) outliving[request.Id] = 0;
                // Connection admission applies to cached answers too: the journal skips Call on a replay.
                if (mutation.Before is { } before) await before(source.Token).ConfigureAwait(false);
                response = Frames.SerializeResult(request.Id, await Journal.ExecuteAsync(mutation.ClientOpId,
                    OperationJournal.Fingerprint(request.Method, request.Params), mutation.Call, source.Token, mutation.SendChatId).ConfigureAwait(false));
            }
            else response = Frames.SerializeResult(request.Id, await route.Run!(services, Journal, request, source.Token).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            response = Frames.Serialize(ResponseFrame.Failure(request.Id, ProtocolErrors.From(exception)));
        }
        running.TryRemove(request.Id, out _);
        outliving.TryRemove(request.Id, out _);
        // Only a method this host knows is named in the measurements; whatever else a peer sends is not echoed anywhere.
        if (route is not null) stats?.Record(request.Method, Stopwatch.GetElapsedTime(started));
        // After a failure too: what an action did before it failed (a sign-in that expired) is in the state the caller reads next.
        if (route is { Settles: true }) await SettleAsync().ConfigureAwait(false);
        await SendTextAsync(response).ConfigureAwait(false);
        after.Then?.Invoke();
    }

    private Task SendAsync(Frame frame) => SendTextAsync(Frames.Serialize(frame));

    private async Task SendTextAsync(string text)
    {
        try { await channel.SendAsync(text).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string? SalvageId(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 128 } value ? value : null;
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex("^[0-9A-Za-z.+-]{1,32}$")]
    private static partial Regex VersionWord();

    // ---- The catalogue, one route per method ----

    /// <summary>The methods this dispatcher serves.</summary>
    public static IReadOnlyCollection<string> Methods => Routes.Keys;

    /// <summary>
    /// Where the routes and <see cref="ProtocolMethods.Catalog"/> disagree: a method with no route, a route with no
    /// method, or a params or result type that differs. Empty when they match.
    /// </summary>
    public static IReadOnlyList<string> Mismatches()
    {
        var problems = new List<string>();
        foreach (var entry in ProtocolMethods.Catalog)
        {
            if (!Routes.TryGetValue(entry.Name, out var route)) problems.Add(entry.Name + " has no route");
            else if (route.Params != entry.Params || route.Result != entry.Result) problems.Add(entry.Name + " route types differ from the catalogue");
            else if (entry.Mutation != (route.Prepare is not null)) problems.Add(entry.Name + " is a mutation in one place and not in the other");
            else if (entry.Mutation && !(entry.Params?.IsAssignableTo(typeof(MutationParams)) ?? false)) problems.Add(entry.Name + " is a mutation whose params carry no clientOpId");
        }
        problems.AddRange(Routes.Keys.Where(name => !ProtocolMethods.All.Contains(name)).Select(name => name + " is not in the catalogue"));
        return problems;
    }

    private static Route Of<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port, Func<TPort, TParams, CancellationToken, Task<TResult>> call)
        where TPort : class where TParams : class
        => new(typeof(TParams), typeof(TResult), async (services, _, request, cancellationToken) =>
        {
            var parameters = ReadParams<TParams>(request);
            var target = port(services) ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(target, parameters, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
        });

    // The whole history's reads (stage 8): only to a connection of 24 or later, and only on a host that keeps one.
    private static Route WholeHistoryRoute<TParams, TResult>(Func<IWholeHistory, TParams, CancellationToken, Task<TResult>> call, int since = ProtocolVersion.WholeHistory)
        where TParams : class
        => new(typeof(TParams), typeof(TResult), null, Own: async (connection, request, _, cancellationToken) =>
        {
            if (connection.negotiated < since) throw new NotSupportedException();
            var port = connection.WholeHistoryService ?? throw new NotSupportedException();
            if (!ParamsShape.HasRequiredValues(typeof(TParams), request.Params)) throw Invalid();
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(port, ReadParams<TParams>(request), cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
        });

    private static Route Of<TPort, TResult>(Func<ProtocolServices, TPort?> port, Func<TPort, CancellationToken, Task<TResult>> call)
        where TPort : class
        => new(null, typeof(TResult), async (services, _, _, cancellationToken) =>
        {
            var target = port(services) ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(target, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
        });

    // A read that needs more of the host than its port (the host's own restore, for the calls that connect Google Messages).
    private static Route OfHost<TPort, TResult>(Func<ProtocolServices, TPort?> port, Func<ProtocolServices, TPort, CancellationToken, Task<TResult>> call)
        where TPort : class
        => new(null, typeof(TResult), async (services, _, _, cancellationToken) =>
        {
            var target = port(services) ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(services, target, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
        });

    // A mutation. Everything that can be wrong with the request is found here, before the journal records anything; what
    // Call does is the one thing the journal guards.
    private static Route Mutation<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port, Func<TPort, TParams, CancellationToken, Task<TResult>> call)
        where TPort : class where TParams : MutationParams
        => Mutation<TPort, TParams, TResult>(port, (_, target, parameters, cancellationToken) => call(target, parameters, cancellationToken));

    // A mutation whose call needs more of the host than its port (a send of uploaded files needs the upload store), and that
    // checks the request against it at the door, before the journal records anything. `check` throws what refuses the request.
    private static Route Mutation<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port,
        Func<ProtocolServices, TPort, TParams, CancellationToken, Task<TResult>> call, Action<ProtocolServices, TParams>? check = null, bool settles = false)
        where TPort : class where TParams : MutationParams
        => Mutation<TPort, TParams, TResult>(port, (services, target, parameters, _, cancellationToken) => call(services, target, parameters, cancellationToken), check, settles);

    // The full form: a mutation that reports progress to the connection that made it. The reports are written, in order, before the call
    // returns, so they reach the caller ahead of the answer; the journal never sees them. `settles`: see SettleAsync.
    private static Route Mutation<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port,
        Func<ProtocolServices, TPort, TParams, IProgress<string>, CancellationToken, Task<TResult>> call, Action<ProtocolServices, TParams>? check, bool settles)
        where TPort : class where TParams : MutationParams
        => new(typeof(TParams), typeof(TResult), null, (connection, services, request) =>
        {
            var parameters = ReadParams<TParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(TParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var target = port(services) ?? throw new NotSupportedException();
            check?.Invoke(services, parameters);
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await using var progress = new ConnectionProgress(connection, services, parameters.ClientOpId);
                return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(services, target, parameters, progress, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }, parameters switch
            {
                SendTextParams p => p.ChatId,
                ReplyParams p => p.ChatId,
                AttachmentSendParams p => p.ChatId,
                AttachmentBatchParams p => p.ChatId,
                _ => null,
            }, Outlives: true);
        }, Settles: settles);

    // Void ports answer an empty object.
    private static Route Mutation<TPort, TParams>(Func<ProtocolServices, TPort?> port, Func<TPort, TParams, CancellationToken, Task> call)
        where TPort : class where TParams : MutationParams
        => Mutation<TPort, TParams, Done>(port, async (target, parameters, cancellationToken) =>
        {
            await call(target, parameters, cancellationToken).ConfigureAwait(false);
            return new Done();
        });

    private static TParams ReadParams<TParams>(RequestFrame request) where TParams : class
    {
        TParams? value = null;
        // Params left out read as an empty object, so a method whose members are all optional (chats.list) may omit them.
        try { value = request.HasParams ? request.ParamsAs<TParams>() : JsonSerializer.Deserialize<TParams>("{}", ProtocolJson.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        if (value is null || !ParamsShape.IsComplete(value))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        return value;
    }

    private static readonly Dictionary<string, Route> Routes = AddPhoneContactsRoutes(AddBackupRoutes(AddPushRoutes(AddAdminRoutes(AddHostLogsRoutes(AddAccountMoveRoutes(AddStateImportRoutes(AddScheduledRoutes(AddSharedStateRoutes(AddDeviceRoutes(AddShutdownRoutes(AddClientStateRoutes(AddIncomingRoutes(AddDiagnosticsRoutes(AddReadingRoutes(AddAccountRoutes(new(StringComparer.Ordinal)
    {
        [ProtocolMethods.BackendDiscover] = OfHost<IBeeperBackend, BackendInfo>(s => s.Backend, async (services, port, ct) => { await WaitForHostGoogleAsync(services, ct).ConfigureAwait(false); return await port.DiscoverAsync(ct).ConfigureAwait(false); }),
        [ProtocolMethods.AccountsList] = OfHost<IBeeperBackend, IReadOnlyList<ChatAccount>>(s => s.Backend, async (services, port, ct) => { await WaitForHostGoogleAsync(services, ct).ConfigureAwait(false); return await port.GetAccountsAsync(ct).ConfigureAwait(false); }),
        [ProtocolMethods.ChatsList] = Of<IChatReader, ChatsListParams, Page<ChatSummary>>(s => s.Chats, (port, p, ct) => port.GetChatsAsync(p.OlderThan, ct)),
        [ProtocolMethods.ChatsSync] = new(typeof(ChatsSyncParams), typeof(ChatsSyncResult), null, Own: async (connection, request, _, ct) =>
        {
            if (connection.negotiated < ProtocolVersion.ConversationSync) throw new NotSupportedException();
            var sync = connection.SyncService ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes(await sync.ReadAsync(ReadParams<ChatsSyncParams>(request), ct).ConfigureAwait(false), ProtocolJson.Options);
        }),
        [ProtocolMethods.HistorySync] = WholeHistoryRoute<HistorySyncParams, HistorySyncResult>((port, p, ct) => port.SyncAsync(p, ct)),
        [ProtocolMethods.HistoryOlder] = WholeHistoryRoute<HistoryOlderParams, HistoryOlderResult>((port, p, ct) => port.OlderAsync(p, ct)),
        [ProtocolMethods.HistoryChanges] = WholeHistoryRoute<HistoryChangesParams, HistoryChangesResult>((port, p, ct) => port.ChangesAsync(p, ct)),
        [ProtocolMethods.HistoryFeed] = WholeHistoryRoute<HistoryFeedParams, HistoryFeedResult>((port, p, ct) => port.FeedAsync(p, ct), ProtocolVersion.Feed),
        [ProtocolMethods.HistoryVerify] = WholeHistoryRoute<HistoryVerifyParams, HistoryVerifyResult>((port, p, ct) => port.VerifyAsync(p, ct), ProtocolVersion.Feed),
        [ProtocolMethods.HistoryMirror] = WholeHistoryRoute<HistoryMirrorParams, HistoryMirrorResult>((port, p, ct) => port.MirrorAsync(p, ct), ProtocolVersion.Feed),
        [ProtocolMethods.ChatsGet] = Of<IChatReader, ChatIdParams, ChatSummary>(s => s.Chats, (port, p, ct) => port.GetChatAsync(p.ChatId, ct)),
        [ProtocolMethods.ChatsSettings] = Of<IChatSettingsBackend, ChatIdParams, ChatSettings>(s => s.Settings, (port, p, ct) => port.GetSettingsAsync(p.ChatId, ct)),
        [ProtocolMethods.ChatsSendPermission] = Of<IMessageSender, ChatIdParams, SendPermission>(s => s.Sender, (port, p, ct) => port.GetSendPermissionAsync(p.ChatId, ct)),
        [ProtocolMethods.ChatsAttachmentPermission] = Of<IAttachmentBackend, ChatIdParams, SendPermission>(s => s.Attachments, (port, p, ct) => port.GetPermissionAsync(p.ChatId, ct)),
        [ProtocolMethods.ChatsCanMarkRead] = Of<IReadStateWriter, ChatIdParams, bool>(s => s.ReadState, (port, p, ct) => port.CanMarkReadAsync(p.ChatId, ct)),
        [ProtocolMethods.MessagesPage] = Of<IChatReader, MessagesPageParams, Page<ChatMessage>>(s => s.Chats, (port, p, ct) => port.GetMessagesAsync(p.ChatId, p.OlderThan, ct)),
        [ProtocolMethods.MessagesResolve] = Of<IMessageSender, MessageIdParams, ChatMessage>(s => s.Sender, (port, p, ct) => port.ResolveMessageAsync(p.ChatId, p.MessageId, ct)),
        [ProtocolMethods.MessagesPeek] = Of<IMessagePeek, MessageIdParams, ChatMessage?>(s => s.Peek, (port, p, ct) => port.PeekAsync(p.ChatId, p.MessageId, ct).AsTask()),
        [ProtocolMethods.MessagesPeekWhole] = Of<IMessagePeek, MessageIdParams, ChatMessage?>(s => s.Peek, (port, p, ct) => port.PeekWholeAsync(p.ChatId, p.MessageId, ct).AsTask()),
        [ProtocolMethods.MessagesActionOptions] = Of<IMessageActions, MessageIdParams, MessageActionOptions>(s => s.Actions, (port, p, ct) => port.GetOptionsAsync(p.ChatId, p.MessageId, ct)),
        [ProtocolMethods.HistoryHeld] = Of<IHistoryWindowReader, HistoryHeldParams, HeldHistory?>(s => s.History, (port, p, ct) => port.ReadHeldAsync(p.ChatId, p.After, p.Limit, ct)),
        [ProtocolMethods.ContactsOptions] = Of<IContactBackend, AccountIdParams, ContactOptions>(s => s.Contacts, (port, p, ct) => port.GetContactOptionsAsync(p.AccountId, ct)),
        [ProtocolMethods.ContactsFind] = Of<IContactBackend, ContactsFindParams, IReadOnlyList<ContactPerson>>(s => s.Contacts, (port, p, ct) => port.FindContactsAsync(p.AccountId, p.Query, ct)),
        [ProtocolMethods.ContactsFrequent] = Of<IFrequentContacts, AccountIdParams, IReadOnlyList<ContactPerson>?>(s => s.FrequentContacts, (port, p, ct) => port.GetFrequentContactsAsync(p.AccountId, ct)),
        [ProtocolMethods.GroupsOptions] = Of<IGroupBackend, AccountIdParams, GroupOptions>(s => s.Groups, (port, p, ct) => port.GetGroupOptionsAsync(p.AccountId, ct)),
        [ProtocolMethods.SearchQuery] = SearchRoute(),
        [ProtocolMethods.SearchWarmKept] = WarmKeptRoute(),
        [ProtocolMethods.SnoozesGet] = Of<IChatSnoozes, SnoozeGetParams, DateTimeOffset?>(s => s.Snoozes, (port, p, ct) => port.GetAsync(p.Profile, p.ChatId, ct)),
        [ProtocolMethods.SnoozesList] = Of<IChatSnoozes, IReadOnlyList<ChatSnooze>>(s => s.Snoozes, (port, ct) => port.ListAsync(ct)),

        [ProtocolMethods.MessagesSend] = Mutation<IMessageSender, SendTextParams, SendReceipt>(s => s.Sender, (port, p, ct) => port.SendTextAsync(p.ChatId, p.Text, ct)),
        [ProtocolMethods.MessagesReply] = Mutation<IMessageActions, ReplyParams, SendReceipt>(s => s.Actions, (port, p, ct) => port.ReplyAsync(p.ChatId, p.MessageId, p.Text, ct)),
        [ProtocolMethods.MessagesReact] = Mutation<IMessageActions, ReactParams>(s => s.Actions, (port, p, ct) => port.ReactAsync(p.ChatId, p.MessageId, p.Reaction, p.Remove, ct)),
        [ProtocolMethods.MessagesEdit] = Mutation<IMessageMutations, EditParams>(s => s.Mutations, (port, p, ct) => port.EditAsync(p.ChatId, p.MessageId, p.OriginalText, p.Text, ct)),
        [ProtocolMethods.MessagesDelete] = Mutation<IMessageMutations, DeleteParams>(s => s.Mutations, (port, p, ct) => port.DeleteAsync(p.ChatId, p.MessageId, p.ForEveryone, ct)),
        [ProtocolMethods.ChatsSet] = Mutation<IChatSettingsBackend, ChatSetParams, ChatSummary>(s => s.Settings, (port, p, ct) => port.SetAsync(p.ChatId, p.Setting, p.Value, ct)),
        [ProtocolMethods.ChatsDelete] = Mutation<IChatDeletion, ChatMutationParams>(s => s.Deletion, (port, p, ct) => port.DeleteChatAsync(p.ChatId, ct)),
        [ProtocolMethods.GroupsCreate] = Mutation<IGroupBackend, GroupCreateParams, ChatSummary>(s => s.Groups, (port, p, ct) => port.CreateGroupAsync(p.AccountId, p.People, p.Title, ct)),
        [ProtocolMethods.GroupsRename] = Mutation<IGroupManagement, GroupRenameParams, ChatSummary>(s => s.GroupManagement, (port, p, ct) => port.RenameGroupAsync(p.ChatId, p.Title, ct)),
        [ProtocolMethods.GroupsAddMembers] = Mutation<IGroupManagement, GroupMembersParams, ChatSummary>(s => s.GroupManagement, (port, p, ct) => port.AddGroupMembersAsync(p.ChatId, p.People, ct)),
        [ProtocolMethods.GroupsRemoveMember] = Mutation<IGroupManagement, GroupMemberParams, ChatSummary>(s => s.GroupManagement, (port, p, ct) => port.RemoveGroupMemberAsync(p.ChatId, p.PersonId, ct)),
        [ProtocolMethods.ContactsStartDirect] = Mutation<IContactBackend, StartDirectParams, ChatSummary>(s => s.Contacts, (port, p, ct) => port.StartDirectAsync(p.Contact, p.AllowInvite, ct)),
        [ProtocolMethods.ContactsStartWithMessage] = Mutation<IFirstMessageStarter, StartWithMessageParams, ChatSummary>(s => s.FirstMessage, (port, p, ct) => port.StartDirectWithMessageAsync(p.Contact, p.Text, ct)),
        [ProtocolMethods.SnoozesSet] = Mutation<IChatSnoozes, SnoozeSetParams>(s => s.Snoozes, (port, p, ct) => port.SetAsync(p.Profile, p.ChatId, p.Until, ct)),

        // Files to send: the upload is claimed when the send starts and deleted when it ends, however it ended. A send that names an
        // upload this host does not hold finished and unclaimed fails invalid_request, before anything is asked of a provider.
        [ProtocolMethods.AttachmentsSend] = Mutation<IAttachmentBackend, AttachmentSendParams, SendReceipt>(s => s.Attachments,
            async (s, port, p, ct) =>
            {
                using var claim = s.Uploads!.Claim([p.Upload]);
                return await port.SendAsync(p.ChatId, claim.FileAt(0), ct, p.Caption).ConfigureAwait(false);
            }, (s, p) => CheckUploads(s, [p.Upload])),
        [ProtocolMethods.AttachmentsSendBatch] = Mutation<IAttachmentBatchBackend, AttachmentBatchParams, SendReceipt>(s => s.AttachmentBatches,
            async (s, port, p, ct) =>
            {
                using var claim = s.Uploads!.Claim(p.Uploads);
                return await port.SendBatchAsync(p.ChatId, claim.Files(), ct).ConfigureAwait(false);
            }, (s, p) => CheckUploads(s, p.Uploads)),

        // What describes a file, never its bytes.
        [ProtocolMethods.AttachmentsFilePermission] = Of<IAttachmentBackend, AttachmentFileParams, SendPermission>(s => s.Attachments,
            (port, p, ct) => port.GetFilePermissionAsync(p.ChatId, Described(p.File), ct)),
        [ProtocolMethods.AttachmentsCaptionSupport] = Of<IAttachmentBackend, AttachmentFileParams, AttachmentCaptionSupport>(s => s.Attachments,
            (port, p, ct) => port.GetCaptionSupportAsync(p.ChatId, Described(p.File), ct)),

        // Uploads and media reads. None is a mutation: nothing is sent until a send names an upload.
        [ProtocolMethods.MediaUploadBegin] = OnUploads<UploadBeginParams, UploadBegun>((store, p, _) => Task.FromResult(new UploadBegun(store.Begin(p.MimeType, p.Length))), requireValues: true),
        [ProtocolMethods.MediaUploadChunk] = OnUploads<UploadChunkParams, UploadReceived>(async (store, p, ct) =>
            new UploadReceived(await store.AppendAsync(p.UploadId, p.Offset, DecodeChunk(p.Data), ct).ConfigureAwait(false)), requireValues: true),
        [ProtocolMethods.MediaUploadEnd] = OnUploads<UploadIdParams, UploadEnded>(async (store, p, ct) =>
            new UploadEnded(p.UploadId, await store.EndAsync(p.UploadId, ct).ConfigureAwait(false))),
        [ProtocolMethods.MediaUploadAbort] = OnUploads<UploadIdParams, Done>((store, p, _) => { store.Abort(p.UploadId); return Task.FromResult(new Done()); }),
        [ProtocolMethods.MediaRead] = new(typeof(MediaReadParams), typeof(MediaChunk), null, Own: (connection, request, _, ct) => connection.ReadMediaAsync(request, ct)),

        // The journal's own answer; it runs nothing.
        [ProtocolMethods.OpsStatus] = new(typeof(OpStatusParams), typeof(OpStatus), (_, journal, request, _) =>
        {
            var parameters = ReadParams<OpStatusParams>(request);
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(journal.Lookup(parameters.ClientOpId), ProtocolJson.Options));
        }),

        // The connection's own event feed (EventLog): the answer, then event frames until it is ended.
        [ProtocolMethods.EventsSubscribe] = new(typeof(EventsSubscribeParams), typeof(EventsSubscribed), null, Own: (connection, request, after, ct) => connection.SubscribeAsync(request, after, ct)),
        [ProtocolMethods.EventsUnsubscribe] = new(null, typeof(Done), null, Own: (connection, _, _, ct) => connection.UnsubscribeAsync(ct)),
    }))))))))))))))));
}
