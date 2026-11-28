using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// Who is typing, for one reader, kept as the newest snapshot of each conversation. A <see cref="TypingUpdate"/>
/// is the whole answer for its conversation, so a newer one replaces an older one that was not read yet instead of queueing behind it:
/// a reader that falls behind skips to the present, and a burst in one conversation costs one slot. At most <c>capacity</c>
/// conversations are held; past that the one waiting longest is dropped. Nothing here is a sequence, a ring or a log.
/// </summary>
internal sealed class TypingSlot(int capacity = TypingSlot.DefaultCapacity)
{
    public const int DefaultCapacity = 256;

    private readonly object gate = new();
    private readonly Dictionary<string, TypingUpdate> latest = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private TaskCompletionSource? waiter;
    private bool completed;
    private long replaced, dropped;

    /// <summary>How many updates were replaced by a newer one for the same conversation before they were read.</summary>
    public long Replaced => Volatile.Read(ref replaced);

    /// <summary>How many conversations' updates were dropped because the reader was too far behind.</summary>
    public long Dropped => Volatile.Read(ref dropped);

    public void Push(TypingUpdate update)
    {
        var chat = update.ChatId ?? "";
        TaskCompletionSource? wake;
        lock (gate)
        {
            if (completed) return;
            if (latest.ContainsKey(chat))
            {
                // It keeps its place in line: what is waiting is whoever has waited longest.
                latest[chat] = update;
                Interlocked.Increment(ref replaced);
            }
            else
            {
                if (latest.Count >= capacity)
                    while (order.TryDequeue(out var oldest))
                        if (latest.Remove(oldest)) { Interlocked.Increment(ref dropped); break; }
                latest[chat] = update;
                order.Enqueue(chat);
            }
            wake = Take();
        }
        wake?.TrySetResult();
    }

    /// <summary>The next update, waiting for one; null once the slot is completed and empty. Cancelling throws, as a channel's reader does.</summary>
    public async Task<TypingUpdate?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                while (order.TryDequeue(out var chat))
                    if (latest.Remove(chat, out var update)) return update;
                if (completed) return null;
                waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = waiter.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        TaskCompletionSource? wake;
        lock (gate)
        {
            if (completed) return;
            completed = true;
            wake = Take();
        }
        wake?.TrySetResult();
    }

    private TaskCompletionSource? Take()
    {
        var taken = waiter;
        waiter = null;
        return taken;
    }
}

/// <summary>One connection's reading of the server's typing. Dispose it to stop.</summary>
public sealed class TypingSubscription : IDisposable
{
    private readonly TypingHub owner;

    internal TypingSubscription(TypingHub owner, TypingSlot slot)
    {
        this.owner = owner;
        Slot = slot;
    }

    internal TypingSlot Slot { get; }

    /// <summary>How many conversations' updates this reader lost by falling too far behind.</summary>
    public long Dropped => Slot.Dropped;

    /// <summary>How many updates were folded into a newer one for the same conversation before this reader took them.</summary>
    public long Replaced => Slot.Replaced;

    /// <summary>The next snapshot of one conversation's typing, waiting for one; null once the subscription has ended.</summary>
    public Task<TypingUpdate?> ReadAsync(CancellationToken cancellationToken = default) => Slot.ReadAsync(cancellationToken);

    public void Dispose()
    {
        owner.Remove(this);
        Slot.Complete();
    }
}

/// <summary>
/// The host's one reading of the typing port (<see cref="ITypingService.WatchAsync"/>), shared by every connection that asked for typing.
/// The source is watched only while somebody is subscribed, since some providers poll for it. Typing is ephemeral, so it is not part of
/// the <see cref="EventLog"/>: nothing is stored, numbered or replayed, a reader that lost its connection simply hears the next change,
/// and a flood of typing can neither crowd real events out of a subscriber's queue nor make one resync. A source that ends is watched
/// again after <see cref="RetryDelay"/>, as the event log does for the providers' feed. Typing names people: what is read here is
/// never logged, only that the source started or ended.
/// </summary>
public sealed class TypingHub(ITypingService source, Action<string>? log = null) : IDisposable
{
    private readonly object gate = new();
    private readonly List<TypingSubscription> subscribers = [];
    private CancellationTokenSource? stop;
    private bool disposed;
    private int sourceRuns;

    /// <summary>How long the source is left alone after it ends before it is watched again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    public int SubscriberCount { get { lock (gate) return subscribers.Count; } }

    /// <summary>How many times the source has been watched: once however many readers there are, more only after it ended or after nobody was reading.</summary>
    public int SourceRuns => Volatile.Read(ref sourceRuns);

    /// <summary>Whether the source is being watched now.</summary>
    public bool Watching { get { lock (gate) return stop is not null; } }

    /// <summary>Starts a reader. The first one starts watching the source.</summary>
    public TypingSubscription Subscribe(int capacity = TypingSlot.DefaultCapacity)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var subscription = new TypingSubscription(this, new TypingSlot(capacity));
            subscribers.Add(subscription);
            if (stop is null)
            {
                stop = new CancellationTokenSource();
                var token = stop.Token;
                _ = Task.Run(() => PumpAsync(token), CancellationToken.None);
            }
            return subscription;
        }
    }

    internal void Remove(TypingSubscription subscription)
    {
        lock (gate)
        {
            subscribers.Remove(subscription);
            // Nobody is reading: the source is left alone until somebody is.
            if (subscribers.Count == 0 && stop is { } running)
            {
                stop = null;
                running.Cancel();
            }
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Interlocked.Increment(ref sourceRuns);
            Write("protocol typing source started");
            try
            {
                await foreach (var update in source.WatchAsync(token).WithCancellation(token).ConfigureAwait(false)) Fan(update);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { }
            if (token.IsCancellationRequested) return;
            Write("protocol typing source ended");
            try { await Task.Delay(RetryDelay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Fan(TypingUpdate update)
    {
        TypingSubscription[] readers;
        lock (gate) readers = [.. subscribers];
        foreach (var reader in readers) reader.Slot.Push(update);
    }

    private void Write(string line)
    {
        try { log?.Invoke(line); } catch (Exception) { /* a logger that fails must not fail the host */ }
    }

    /// <summary>Stops watching the source and ends every reader.</summary>
    public void Dispose()
    {
        TypingSubscription[] ending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ending = [.. subscribers];
            subscribers.Clear();
            stop?.Cancel();
            stop = null;
        }
        foreach (var reader in ending) reader.Slot.Complete();
    }
}
