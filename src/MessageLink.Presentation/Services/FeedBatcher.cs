using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

/// <summary>A full resync the list has just applied: when it began (ProgressIndicator.SinceLaunch) and why it was asked for.</summary>
public sealed record FeedResync(TimeSpan Began, string Reason);

/// <summary>
/// What the window adds to a feed pass: its open conversations, the composer's typing and its status line. mlink has none of these and gives no hook. Every
/// member is called on the loop the passes run on.
/// </summary>
public interface IFeedConversations
{
    /// <summary>How long a burst is gathered, given the ordinary window: the window widens it while somebody types.</summary>
    TimeSpan BatchWindow(TimeSpan ordinary);

    /// <summary>How soon a pass offers held events again while nothing new arrives, or null when nothing is held.</summary>
    TimeSpan? HoldRetry { get; }

    /// <summary>A service started or stopped waiting on a sign-in (<see cref="FeedBatcher.NeedsSignIn"/>).</summary>
    void SignInChanged();

    /// <summary>The pass found nothing connected. The batch was not applied (its connection events still reached the tap).</summary>
    void Offline(IReadOnlyList<RealtimeEvent> batch);

    /// <summary>Something is connected: whether the pass may apply now, or is held whole because the list (<paramref name="listBusy"/>) or a conversation is busy.</summary>
    bool TakePass(bool listBusy);

    /// <summary>
    /// The pass itself: the list (<see cref="FeedPass.ListAsync"/>), the open conversations, then the outbox (<see cref="FeedPass.OutboxAsync"/>). False when
    /// it stopped at a busy conversation, which holds the events for it.
    /// </summary>
    Task<bool> ApplyAsync(FeedPass pass, CancellationToken token);

    /// <summary>The pass threw. The batcher has logged it and will retry with a full resync.</summary>
    void Failed(Exception failure);
}

/// <summary>One pass over a batch, as <see cref="IFeedConversations.ApplyAsync"/> is handed it.</summary>
public sealed class FeedPass
{
    private readonly FeedBatcher owner;

    internal FeedPass(FeedBatcher owner, IReadOnlyList<RealtimeEvent> events, bool full, string reason)
    {
        this.owner = owner; Events = events; Full = full; Reason = reason;
    }

    public IReadOnlyList<RealtimeEvent> Events { get; }
    /// <summary>Whether the list is read whole this pass.</summary>
    public bool Full { get; }
    /// <summary>Why it is (connected, required, reconnected, failed, overflow, start), or "none".</summary>
    public string Reason { get; }
    /// <summary>Where the pass has got to, for the line a failure logs.</summary>
    public string Stage { get; set; } = "list";

    /// <summary>The list takes the batch (whole when <see cref="Full"/>), then the tap and a full resync's listeners hear of it, before any conversation re-reads.</summary>
    public Task ListAsync(CancellationToken token) => owner.ApplyListAsync(this, token);

    /// <summary>The outbox settles its sends by these echoes, or by everything when <paramref name="full"/>.</summary>
    public Task OutboxAsync(IReadOnlyList<RealtimeEvent> changes, bool full, CancellationToken token) =>
        owner.Outbox.ReconcileAsync(cancellationToken: token, changes: full ? null : changes);

    /// <summary>How long the measured events among these have waited since they reached the batcher: how many, the mean and the worst.</summary>
    public (int Count, long AvgMs, long WorstMs) Waited(IEnumerable<RealtimeEvent> events) => owner.Waited(events);
}

/// <summary>
/// The server's realtime feed read in batches and applied to the conversation list, for the window (<see cref="SyncViewModel"/>, which adds its open
/// conversations through <see cref="IFeedConversations"/>) and mlink alike. A burst is gathered for a moment; a batch that arrives while the list is busy is
/// held whole and offered again; more than a pass can carry becomes a full resync. A connection, the server saying it could not resume this reader,
/// <see cref="Resync"/> or a failed pass makes the next pass a full resync, and a failure doubles the wait before it, to 30 s. A resync asked for while nothing
/// reads as connected is kept until a pass finds a connection (owner, 2026-11-26): the server's state comes back after the connection to it does, so the pass a
/// reconnection starts can find nothing connected yet, and <see cref="ConnectionChanged"/> wakes the batcher for the next. Every pass runs on the loop given
/// (the interface thread, or mlink's session loop).
/// </summary>
public sealed class FeedBatcher : IDisposable
{
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(150);
    // How soon a batch held back for a busy list is offered again. Short: it is already late.
    private static readonly TimeSpan DeferredRetry = TimeSpan.FromMilliseconds(120);
    private const int MostPerPass = 128;

    private readonly IRealtimeFeed feed;
    private readonly ChatListViewModel chats;
    private readonly IUiDispatcher loop;
    private readonly IProviderConnectionState? connections;
    private readonly Action<string> log;
    private readonly IFeedConversations? conversations;
    private readonly bool resyncAtStart;
    private readonly Dictionary<string, RealtimeKind> unavailable = new(StringComparer.Ordinal);
    private readonly HashSet<string> signInWaiting = new(StringComparer.Ordinal);
    // When each realtime event reached the batcher, as a monotonic stamp, for the window's "sync event_applied" line. Not RealtimeEvent.ReceivedAt: that
    // is wall-clock, set by only some backends. By reference, so a stamp goes with its event and needs no cleaning up.
    private readonly ConditionalWeakTable<RealtimeEvent, StrongBox<long>> arrivals = new();
    private readonly TaskCompletionSource firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool resyncWanted, connectionChanged;
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? session;
    private Task? running;
    private int waitsEnded;

    /// <summary>How many times the idle wait has ended, for a check that an idle batcher stays idle.</summary>
    internal int WaitsEnded => Volatile.Read(ref waitsEnded);

    /// <param name="log">Lines in the window's log words ("sync failed …"); mlink puts its own prefix on them.</param>
    /// <param name="resyncAtStart">Whether the first pass reads the list whole: mlink's, as what the server held before it started is in no event. The
    /// window reads its list itself.</param>
    public FeedBatcher(IRealtimeFeed feed, ChatListViewModel chats, Outbox outbox, IUiDispatcher loop, IProviderConnectionState? connections,
        Action<string> log, IFeedConversations? conversations = null, bool resyncAtStart = false)
    {
        this.feed = feed; this.chats = chats; Outbox = outbox; this.loop = loop; this.connections = connections; this.log = log;
        this.conversations = conversations; this.resyncAtStart = resyncAtStart;
    }

    internal Outbox Outbox { get; }

    /// <summary>How long a full resync waits after a connection, and the first wait after a failed one (it then doubles, to 30 s). Read at each start.</summary>
    public TimeSpan ResyncDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Every batch once, on the loop, at the moment it has been applied to the list and before any open conversation re-reads. A batch held back for a busy
    /// list is raised when it is finally applied. With nothing connected only its connection and resync events are raised, with no conversations. A
    /// handler must be quick.
    /// </summary>
    public event EventHandler<AppliedBatch>? BatchApplied;

    /// <summary>Every full resync of the list, on the loop, also one that carried no events (the retry after a failed one). A handler must be quick.</summary>
    public event EventHandler<FeedResync>? ListResynced;

    /// <summary>
    /// Completes when the first pass has run to its end, whatever it found: applied, nothing connected, or a failure. A pass held whole for a busy list does
    /// not count. Also completes when the batcher stops first.
    /// </summary>
    public Task FirstPass => firstPass.Task;

    /// <summary>The services (by connection scope) a pass last heard go, with what they said, until they connect again. On the loop.</summary>
    public IReadOnlyDictionary<string, RealtimeKind> Unavailable => unavailable;

    /// <summary>Forgets each service in <see cref="Unavailable"/> that reads as connected now. On the loop.</summary>
    public void ForgetConnected()
    {
        foreach (var scope in unavailable.Keys.Where(scope => !IsUnavailable(scope)).ToArray()) unavailable.Remove(scope);
    }

    /// <summary>Set up here and not connected now, or nothing to tell by.</summary>
    public bool IsUnavailable(string scope) => connections is null || connections.IsConfigured(scope) && !connections.IsScopeConnected(scope);

    /// <summary>
    /// Whether a service (by its connection scope) has stopped and waits on the user: it said authorization is needed (a session expired, a checkpoint, a
    /// pairing or sign-in Google no longer accepts) and has not connected since. A connection that only dropped is not this. On the loop.
    /// </summary>
    public bool NeedsSignIn(string scope) => signInWaiting.Contains(scope) && IsUnavailable(scope);

    /// <summary>Started and not yet stopped.</summary>
    public bool IsRunning => running is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        session?.Dispose(); session = new();
        unavailable.Clear();
        var token = session.Token;
        var delay = ResyncDelay;
        running = Task.Run(() => RunAsync(delay, token));
    }

    /// <summary>Asks for a full resync at the next pass: the connection to the server came back, and what changed meanwhile is read again.</summary>
    public void Resync()
    {
        // The flag, then the wake: the loop reads the wake before the flag, so it sees one or the other.
        resyncWanted = true;
        Interlocked.Exchange(ref wake, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>What reads as connected may have changed (the server's state arrived, or the connection to it went): a resync kept for a connection is tried again.</summary>
    public void ConnectionChanged()
    {
        connectionChanged = true;
        Interlocked.Exchange(ref wake, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Stops reading and waits for the pass under way, or at most <paramref name="bound"/>. Awaited rather than waited on: a pass may be finishing on the
    /// loop this is called from.
    /// </summary>
    public async Task StopAsync(TimeSpan? bound = null)
    {
        session?.Cancel();
        // Replaced, not just completed, as Resync and ConnectionChanged do: the launch stops the batcher before its first Start, and a later loop that
        // found this wake already complete returned from every wait at once, about one core and 1.3 GB a second for the whole session (0.1.500, 2026-11-26).
        Interlocked.Exchange(ref wake, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        if (running is not { } run) return;
        if (bound is not { } most) { await run; return; }
        _ = run.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        await Task.WhenAny(run, Task.Delay(most));
    }

    public void Dispose() { var previous = session; session = null; previous?.Cancel(); previous?.Dispose(); }

    private async Task RunAsync(TimeSpan resyncDelay, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = linked.Token;
        // Provider events and confirmed automatic unarchives can arrive from separate workers.
        var channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var producer = ProduceAsync(channel, token);
        var online = false;
        var needsResync = resyncAtStart;
        // Why the next pass is a full one. A full pass re-reads every conversation from every provider, which is where a synchronization's whole
        // allocation turned out to live, so the reason is the next thing worth knowing.
        var reason = resyncAtStart ? "start" : "none";
        // The resync is kept, and the last pass found nothing connected: the next pass waits for an event or a change of connection.
        var awaitingConnection = false;
        var retry = resyncDelay;
        // Events held back because the list was busy when they arrived. Kept rather than dropped: the only way to recover a dropped batch is a full
        // resync, which re-reads every conversation from every provider (eighteen megabytes, measured, for a handful of events still in hand).
        var carried = new List<RealtimeEvent>();
        Task<bool>? readable = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Read before the flags: a Resync() or ConnectionChanged() between the two shows in its flag below, or completes this wake.
                var woken = Volatile.Read(ref wake).Task;
                if (resyncWanted) { resyncWanted = false; needsResync = true; reason = "reconnected"; awaitingConnection = false; }
                if (connectionChanged) { connectionChanged = false; awaitingConnection = false; }
                var hold = conversations?.HoldRetry;
                if (carried.Count == 0 && (!needsResync || awaitingConnection) && hold is null)
                {
                    // A wait still pending from a wake is waited on again rather than joined by another.
                    if (readable is null || readable.IsCompleted) readable = channel.Reader.WaitToReadAsync(token).AsTask();
                    await Task.WhenAny(readable, woken);
                    Interlocked.Increment(ref waitsEnded);
                    // Woken: the flags say why.
                    if (!readable.IsCompleted) continue;
                    if (!await readable)
                    {
                        if (resyncWanted) continue;
                        break;
                    }
                    awaitingConnection = false;
                }
                // The start's resync waits only for a burst to gather: mlink's console opens after this first pass, and the full resync delay held every
                // launch for a second. A reconnection or a failure still waits the delay, and a failure doubles it.
                var ordinary = conversations?.BatchWindow(BatchWindow) ?? BatchWindow;
                await Task.Delay(needsResync ? reason == "start" ? BatchWindow : retry
                    : carried.Count > 0 ? DeferredRetry : hold is { } held && !channel.Reader.TryPeek(out _) ? held : ordinary, token);
                var batch = carried;
                carried = [];
                while (batch.Count < MostPerPass && channel.Reader.TryRead(out var change)) batch.Add(change);
                foreach (var change in batch)
                {
                    if (change.Kind == RealtimeKind.Connected) { online = true; needsResync = true; reason = "connected"; }
                    if (change.Kind is RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired) online = false;
                    if (change.Kind == RealtimeKind.ResyncRequired) { needsResync = true; reason = "required"; }
                }
                var deferred = false;
                var failed = false;
                var offline = false;
                var completed = false;
                var full = needsResync;
                var passReason = reason;
                await loop.InvokeAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    var signInChanged = false;
                    foreach (var change in batch)
                    {
                        if (change.Scope is not { } scope) continue;
                        if (change.Kind == RealtimeKind.Connected && (connections is null || !IsUnavailable(scope)))
                        {
                            unavailable.Remove(scope);
                            signInChanged |= signInWaiting.Remove(scope);
                        }
                        else if (change.Kind is RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired) unavailable[scope] = change.Kind;
                        if (change.Kind == RealtimeKind.AuthenticationRequired) signInChanged |= signInWaiting.Add(scope);
                    }
                    if (signInChanged) conversations?.SignInChanged();
                    if (connections is not null) online = connections.AnyConnected;
                    if (!online)
                    {
                        offline = true;
                        conversations?.Offline(batch);
                        // Still told while nothing is connected: a disconnect is worth knowing about, and a resync request moves the stream's since, as it
                        // does the notifier's.
                        Tap([.. batch.Where(e => e.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired
                            or RealtimeKind.ResyncRequired)], false, false);
                        return;
                    }
                    // Nothing has been applied yet, so the batch is still whole and worth keeping.
                    if (!(conversations?.TakePass(chats.IsBusy) ?? !chats.IsBusy)) { deferred = true; return; }
                    var pass = new FeedPass(this, batch, full, passReason);
                    try
                    {
                        if (conversations is null)
                        {
                            await pass.ListAsync(token);
                            pass.Stage = "outbox";
                            await pass.OutboxAsync(batch, full, token);
                            completed = true;
                        }
                        else completed = await conversations.ApplyAsync(pass, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception failure)
                    {
                        failed = true;
                        // The exception's type, never its message: a provider's message can carry a name or a number.
                        log($"sync failed stage={pass.Stage} type={failure.GetType().Name} inner={failure.InnerException?.GetType().Name ?? "none"} " +
                            $"hresult=0x{failure.HResult:X8} events={batch.Count.ToString(CultureInfo.InvariantCulture)} " +
                            $"retry_ms={Math.Min(30000, (long)retry.TotalMilliseconds * 2).ToString(CultureInfo.InvariantCulture)}");
                        conversations?.Failed(failure);
                    }
                });
                if (deferred)
                {
                    // More than a pass can carry is cheaper read again whole, which is what the old behaviour always did.
                    if (batch.Count >= MostPerPass) { needsResync = true; reason = "overflow"; }
                    else carried = batch;
                    continue;
                }
                firstPass.TrySetResult();
                if (failed)
                {
                    needsResync = true; reason = "failed";
                    retry = TimeSpan.FromMilliseconds(Math.Min(30000, retry.TotalMilliseconds * 2));
                    continue;
                }
                // Nothing connected to resync with: the resync is kept, with its reason, until a pass finds a connection.
                if (offline)
                {
                    if (full) awaitingConnection = true;
                    continue;
                }
                needsResync = false; reason = "none";
                // A pass that stopped at a busy conversation is not over: the wait after a failure stays as it was until one completes.
                if (completed) retry = resyncDelay;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { log($"sync feed ended error={failure.GetType().Name}"); }
        finally
        {
            linked.Cancel();
            try { await producer; } catch (OperationCanceledException) { }
            firstPass.TrySetResult();
        }
    }

    internal async Task ApplyListAsync(FeedPass pass, CancellationToken token)
    {
        // A pass that only offers held events to a conversation has nothing for the list.
        var began = ProgressIndicator.SinceLaunch;
        if (pass.Events.Count > 0 || pass.Full) await chats.ApplyEventsAsync(pass.Events, pass.Full, token);
        // The list is fresh and no open conversation has re-read yet.
        Tap(pass.Events, pass.Full, true);
        if (pass.Full && ListResynced is { } listeners)
        {
            // Whatever a listener does wrong, the pass goes on. The type alone: an exception's message may name anything.
            try { listeners(this, new FeedResync(began, pass.Reason)); }
            catch (Exception failure) { log($"resync listener failed error={failure.GetType().Name}"); }
        }
    }

    // The batch as applied, with the list's copy of every conversation it names.
    private void Tap(IReadOnlyList<RealtimeEvent> events, bool full, bool withChats)
    {
        if (events.Count == 0 || BatchApplied is not { } tap) return;
        try
        {
            var summaries = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
            if (withChats)
            {
                var named = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in events)
                {
                    if (change.ChatId is { } id) named.Add(id);
                    if (change.Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted && change.Ids is { } ids) named.UnionWith(ids);
                }
                if (named.Count > 0)
                    foreach (var chat in chats.AllLoadedChats)
                        if (named.Contains(chat.Id)) summaries[chat.Id] = chat;
            }
            tap(this, new AppliedBatch([.. events], full, summaries));
        }
        // Whatever a listener does wrong, the pass goes on. The type alone: an exception's message may name anything.
        catch (Exception failure) { log($"activity tap failed error={failure.GetType().Name}"); }
    }

    // Live changes only: a history snapshot is old state refreshing the baseline, and connection events apply nothing.
    private static bool IsMeasured(RealtimeEvent change) => !change.IsHistory &&
        change.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted or RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted;

    internal (int Count, long AvgMs, long WorstMs) Waited(IEnumerable<RealtimeEvent> events)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        int count = 0; long total = 0, worst = 0;
        foreach (var change in events)
        {
            if (!arrivals.TryGetValue(change, out var arrived)) continue;
            var ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(arrived.Value, now).TotalMilliseconds;
            count++; total += ms; worst = Math.Max(worst, ms);
        }
        return (count, count == 0 ? 0 : total / count, worst);
    }

    private async Task ProduceAsync(Channel<RealtimeEvent> channel, CancellationToken token)
    {
        // Stamped before the write, which waits when the channel is full, so that wait is part of the time measured.
        try
        {
            await feed.RunAsync((change, ct) =>
            {
                if (IsMeasured(change)) arrivals.AddOrUpdate(change, new(System.Diagnostics.Stopwatch.GetTimestamp()));
                return channel.Writer.WriteAsync(change, ct).AsTask();
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { channel.Writer.TryWrite(new(RealtimeKind.AuthenticationRequired)); }
        finally { channel.Writer.TryComplete(); }
    }
}
