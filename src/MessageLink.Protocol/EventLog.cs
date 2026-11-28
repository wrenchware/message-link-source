using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// One item of a feed: a realtime event with its position, a host state (<see cref="State"/>, SWITCHBOARD.md 2.6b), an account
/// <see cref="Signal"/> (2.6c), a provider's <see cref="CaughtUp"/> (3.0b), or (all null) a resync, which says that anything may have changed and the reader should read it all
/// again. A resync's <see cref="Seq"/> is the position after which events continue. A <see cref="Shared"/> item is one commit to the host's shared state (stage 6).
/// A <see cref="History"/> item is one change to the server's whole copy of a conversation (stage 8).
/// </summary>
public sealed record EventItem(long Seq, RealtimeEvent? Event, HostState? State = null, AccountSignal? Signal = null, CaughtUpNotice? CaughtUp = null,
    SharedChange? Shared = null, HistoryChange? History = null, HistoryRowChange? Row = null)
{
    public bool IsResync => Event is null && State is null && Signal is null && CaughtUp is null && Shared is null && History is null && Row is null;
}

/// <summary>
/// The host's record of its realtime events (SWITCHBOARD.md 2.3). The source feed (the providers' composite) is run once, by
/// this log, and every event it produces gets the next sequence number and is kept in a ring of the last
/// <c>capacity</c>. Any number of subscribers then read the same events without splitting them, each from its own bounded
/// queue, so a slow reader costs the host and the others nothing.
/// </summary>
/// <remarks>
/// <para>The <see cref="Epoch"/> is new each time the host starts. A subscriber that remembers the epoch and the last sequence
/// number it saw resumes exactly where it stopped, if the ring still holds what follows; otherwise (a first subscription,
/// another epoch, a position that has aged out) it is handed a resync first and live events after it. A subscriber whose queue
/// overflows loses the queue and is handed a resync, once, the way a provider's backlog does (<c>RealtimeBuffer</c>). So the
/// events a subscriber receives are in sequence order with no gap that a resync does not announce.</para>
/// <para>The host's state (<see cref="HostState"/>) travels in the same sequence, as items of its own kind: <see cref="AppendState"/>
/// gives one the next sequence number like any event. The log remembers the newest, and every resync it hands a subscriber (the
/// opening one, or one that replaces an overflowed queue) is followed by that state, so a reader that has just been told
/// "read everything again" also has the current state to read it against.</para>
/// <para>Events are carried as they are, ids and flags only. Putting the message itself in the event, so a remote client
/// does not call back once per id, is a later optimisation that belongs with the batch reads.</para>
/// </remarks>
public sealed class EventLog : IDisposable
{
    public const int DefaultCapacity = 4096;
    public const int DefaultSubscriberQueue = 1024;

    private readonly object gate = new();
    private readonly IRealtimeFeed source;
    private readonly EventItem[] ring;
    private readonly int subscriberQueue;
    private readonly Action<string>? log;
    private readonly List<EventSubscription> subscribers = [];
    private HostState? latestState;
    private long head;
    private int held;
    private int sourceRuns;
    private int overflows;
    private CancellationTokenSource? stop;
    private Task? pump;
    private bool disposed;

    public EventLog(IRealtimeFeed source, int capacity = DefaultCapacity, int subscriberQueue = DefaultSubscriberQueue, Action<string>? log = null,
        string? epoch = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(subscriberQueue, 2);
        this.source = source;
        this.subscriberQueue = subscriberQueue;
        this.log = log;
        ring = new EventItem[capacity];
        Epoch = epoch ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>Names this run of the host. A sequence number means nothing under another epoch.</summary>
    public string Epoch { get; }

    /// <summary>How long the source feed waits after it ends before it is run again, as the composite does for a failed provider.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Called with each event from the source feed just before it is recorded, on the thread that delivers it and with no lock held.
    /// The host's state publisher uses it to record a connection change in the state first, so a reader that is told "connected"
    /// already holds a state that says so. A hook that throws is ignored.
    /// </summary>
    public Action<RealtimeEvent>? BeforeSourceEvent { get; init; }

    /// <summary>The state most recently appended, or none before the first.</summary>
    public HostState? LatestState { get { lock (gate) return latestState; } }

    /// <summary>The sequence number of the latest event; 0 before the first.</summary>
    public long Head { get { lock (gate) return head; } }

    /// <summary>How many events the ring holds now.</summary>
    public int Held { get { lock (gate) return held; } }

    public int SubscriberCount { get { lock (gate) return subscribers.Count; } }

    /// <summary>How many times the source feed has been started: one however many subscribers there are, more only after it ended.</summary>
    public int SourceRuns => Volatile.Read(ref sourceRuns);

    /// <summary>How many times a subscriber's queue has overflowed and been replaced by a resync.</summary>
    public int Overflows => Volatile.Read(ref overflows);

    /// <summary>
    /// Records an event and hands it to every subscriber without waiting for any. The source feed's events come here; a host may
    /// add its own. Returns the sequence number it was given.
    /// </summary>
    public long Append(RealtimeEvent change) => Record(position => new EventItem(position, change), null);

    /// <summary>
    /// Records the host's state as an item of its own kind and remembers it as the newest. The caller appends only a state that
    /// differs from the last (<see cref="StatePublisher"/>); a state is not deduplicated here. Returns its sequence number.
    /// </summary>
    public long AppendState(HostState state) => Record(position => new EventItem(position, null, state), state);

    /// <summary>
    /// Records a fact about an account that no state shows (SWITCHBOARD.md 2.6c) in the sequence, so a reader that resumes is given it
    /// too. A reader that fell behind and was handed a resync reads everything again, which is what a missed signal needs.
    /// </summary>
    public long AppendSignal(AccountSignal signal) => Record(position => new EventItem(position, null, null, signal), null);

    /// <summary>
    /// Records that a provider caught up on messages after connecting (SWITCHBOARD.md 3.0b), in the sequence, so a reader that resumes is given
    /// it too. A reader that fell behind and was handed a resync loses it, which costs only a picture prepared ahead.
    /// </summary>
    public long AppendCaughtUp(CaughtUpNotice notice) => Record(position => new EventItem(position, null, null, null, notice), null);

    /// <summary>
    /// Records one commit to the host's shared state (SWITCHBOARD.md stage 6) in the sequence, so a reader that resumes is given it too. The store appends
    /// each commit while it still holds its own lock, so the commits are in the sequence in the order of their revs. A reader that fell behind and was
    /// handed a resync reads what it missed with <c>state.sync</c>.
    /// </summary>
    public long AppendShared(SharedChange change) => Record(position => new EventItem(position, null, Shared: change), null);

    /// <summary>
    /// Records one change to the server's whole copy of a conversation (SWITCHBOARD.md stage 8) in the sequence, just after the realtime event that caused it, so a
    /// reader that resumes is given it too. A reader that fell behind and was handed a resync reads what it missed with <c>history.changes</c>.
    /// </summary>
    public long AppendHistory(HistoryChange change) => Record(position => new EventItem(position, null, History: change), null);

    /// <summary>Records a conversation's list row as the server now keeps it (stage 9), for connections of 25 and later.</summary>
    public long AppendRow(HistoryRowChange change) => Record(position => new EventItem(position, null, Row: change), null);

    /// <summary>
    /// Starts running the source feed with no reader yet: a host that keeps something of its own from the events (the whole history) needs them whether or not
    /// a client is connected. Does nothing once the feed runs.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pump is not null) return;
            stop = new CancellationTokenSource();
            var token = stop.Token;
            pump = Task.Run(() => PumpAsync(token), CancellationToken.None);
        }
    }

    private long Record(Func<long, EventItem> make, HostState? state)
    {
        lock (gate)
        {
            if (disposed) return head;
            if (state is not null) latestState = state;
            var item = make(++head);
            ring[(head - 1) % ring.Length] = item;
            if (held < ring.Length) held++;
            foreach (var subscriber in subscribers)
            {
                var dropped = subscriber.Queue.Push(item);
                if (dropped == 0) continue;
                overflows++;
                Write($"protocol events subscriber overflow dropped={dropped} action=resync");
            }
            return head;
        }
    }

    /// <summary>
    /// Starts a feed for one reader. With the <paramref name="epoch"/> and <paramref name="since"/> of the last event it saw, and
    /// the ring still holding everything after it, the feed begins with exactly those events; otherwise with a resync. Live
    /// events follow either way. Starts the source feed if this is its first reader.
    /// </summary>
    public EventSubscription Subscribe(long? since = null, string? epoch = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var resumable = since is { } last && string.Equals(epoch, Epoch, StringComparison.Ordinal) && last >= head - held && last <= head;
            var queue = new EventQueue(subscriberQueue);
            if (resumable)
                queue.Prefill(Enumerable.Range(1, (int)(head - since!.Value)).Select(offset => ring[(since.Value + offset - 1) % ring.Length]));
            // A resync is always followed by the current state, when there is one.
            else queue.Prefill(latestState is { } current ? [new EventItem(head, null), new EventItem(head, null, current)] : [new EventItem(head, null)]);
            var subscription = new EventSubscription(this, queue, Epoch, head, !resumable);
            subscribers.Add(subscription);
            if (pump is null)
            {
                stop = new CancellationTokenSource();
                var token = stop.Token;
                pump = Task.Run(() => PumpAsync(token), CancellationToken.None);
            }
            return subscription;
        }
    }

    internal void Remove(EventSubscription subscription)
    {
        lock (gate) subscribers.Remove(subscription);
    }

    // The one consumer of the source. A source that ends (the composite never does) is the provider failure it would be inside
    // the composite: the readers hear a disconnect, and after a pause the source runs again followed by a resync, since
    // anything that happened in between was missed.
    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Interlocked.Increment(ref sourceRuns);
            try { await source.RunAsync((change, _) => { Prepare(change); Append(change); return Task.CompletedTask; }, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { }
            Write("protocol events source ended");
            Append(new RealtimeEvent(RealtimeKind.Disconnected));
            try { await Task.Delay(RetryDelay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Append(new RealtimeEvent(RealtimeKind.ResyncRequired));
        }
    }

    private void Prepare(RealtimeEvent change)
    {
        try { BeforeSourceEvent?.Invoke(change); } catch (Exception) { /* a hook that fails must not fail the feed */ }
    }

    private void Write(string line)
    {
        try { log?.Invoke(line); } catch (Exception) { /* a logger that fails must not fail the feed */ }
    }

    /// <summary>Stops the source feed and ends every subscriber's feed.</summary>
    public void Dispose()
    {
        EventSubscription[] ending;
        Task? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ending = [.. subscribers];
            subscribers.Clear();
            running = pump;
            stop?.Cancel();
        }
        foreach (var subscriber in ending) subscriber.Queue.Complete();
        try { running?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}

/// <summary>One reader's feed from an <see cref="EventLog"/>. Dispose it to stop; it also ends when the log is disposed.</summary>
public sealed class EventSubscription : IDisposable
{
    private readonly EventLog owner;

    internal EventSubscription(EventLog owner, EventQueue queue, string epoch, long head, bool resync)
    {
        this.owner = owner;
        Queue = queue;
        Epoch = epoch;
        Head = head;
        Resync = resync;
    }

    internal EventQueue Queue { get; }

    public string Epoch { get; }

    /// <summary>The log's latest sequence number when this feed began.</summary>
    public long Head { get; }

    /// <summary>The feed begins with a resync, not with a replay of what the reader missed.</summary>
    public bool Resync { get; }

    /// <summary>The next item, waiting for one; null once the feed has ended and what was queued has been read.</summary>
    public Task<EventItem?> ReadAsync(CancellationToken cancellationToken = default) => Queue.ReadAsync(cancellationToken);

    public void Dispose()
    {
        owner.Remove(this);
        Queue.Complete();
    }
}

/// <summary>
/// A bounded queue of feed items for one reader. When it would overflow it is emptied and replaced by a single resync that
/// carries the position of the newest item lost, so what follows is still in order and the loss is announced once. The
/// first items given to <see cref="Prefill"/> (a replay, or the opening resync) do not count against the bound; they are limited by
/// what the log holds.
/// </summary>
internal sealed class EventQueue(int capacity)
{
    private readonly object gate = new();
    private readonly Queue<EventItem> items = new();
    private int exempt;
    private TaskCompletionSource? waiter;
    private bool completed;
    private Exception? fault;

    public void Prefill(IEnumerable<EventItem> first)
    {
        lock (gate)
        {
            foreach (var item in first) items.Enqueue(item);
            exempt = items.Count;
        }
    }

    /// <summary>Adds an item; returns how many events were lost to overflow by doing so (0 almost always).</summary>
    public int Push(EventItem item)
    {
        var dropped = 0;
        TaskCompletionSource? wake;
        lock (gate)
        {
            if (completed) return 0;
            if (items.Count - exempt >= capacity)
            {
                dropped = items.Count(queued => !queued.IsResync) + (item.IsResync ? 0 : 1);
                // The newest state this queue has seen, dropped with the rest, goes back right after the resync that replaces them:
                // a resync is always followed by the current state (SWITCHBOARD.md 2.6b).
                var state = item.State ?? items.LastOrDefault(queued => queued.State is not null)?.State;
                items.Clear();
                exempt = 0;
                items.Enqueue(new EventItem(item.Seq, null));
                if (state is not null)
                {
                    items.Enqueue(new EventItem(item.Seq, null, state));
                    // Not counted against the bound, so the bound still means events.
                    exempt = 1;
                }
            }
            else items.Enqueue(item);
            wake = Take();
        }
        wake?.TrySetResult();
        return dropped;
    }

    /// <summary>Ends the queue: what it holds is still read, then <see cref="ReadAsync"/> returns null, or throws <paramref name="failure"/>.</summary>
    public void Complete(Exception? failure = null)
    {
        TaskCompletionSource? wake;
        lock (gate)
        {
            if (completed) return;
            completed = true;
            fault = failure;
            wake = Take();
        }
        wake?.TrySetResult();
    }

    // One reader at a time.
    public async Task<EventItem?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (items.TryDequeue(out var item))
                {
                    if (exempt > 0) exempt--;
                    return item;
                }
                if (completed)
                {
                    if (fault is not null) throw fault;
                    return null;
                }
                waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = waiter.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private TaskCompletionSource? Take()
    {
        var taken = waiter;
        waiter = null;
        return taken;
    }
}
