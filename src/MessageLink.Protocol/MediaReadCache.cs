namespace MessageLink.Protocol;

/// <summary>
/// The bytes of a few media reads, kept for a short while so that the chunks of one read (<c>media.read</c>) are cut from one
/// fetch instead of asking the provider again per chunk. Not a media cache: the disk cache under the routers keeps pictures for
/// days, and this lives for the seconds a client takes to read them through. Bounded in bytes and in number; a read that fails is
/// not kept; two callers asking for the same key at once share one fetch. Disposed with its connection: what it held goes, and
/// it keeps nothing after.
/// </summary>
public sealed class MediaReadCache(TimeProvider? time = null) : IDisposable
{
    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public readonly TaskCompletionSource<byte[]> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Size;
        public DateTimeOffset Expires = DateTimeOffset.MaxValue;
        public bool Ready;
    }

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long held;
    // Wakes at the earliest expiry, so a read's bytes (a saved 200 MB video) go when their time is up, not at this connection's next read or its close.
    private ITimer? timer;
    private DateTimeOffset timerAt = DateTimeOffset.MaxValue;
    private bool disposed;

    /// <summary>The most items held at once.</summary>
    public int MaxEntries { get; set; } = 8;

    /// <summary>The most bytes held at once; an item larger than this is handed to the caller and not kept (so each chunk of it fetches again).</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How long an item stays after it was last asked for.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an item stays once its last chunk has gone out: a read that is over need not hold memory, but a second look right after is free.</summary>
    public TimeSpan FinishedLifetime { get; set; } = TimeSpan.FromSeconds(2);

    public int Count { get { lock (gate) return entries.Count; } }
    public long HeldBytes { get { lock (gate) return held; } }

    /// <summary>The bytes for <paramref name="key"/>: those kept, or what <paramref name="fetch"/> returns (once, however many callers ask).</summary>
    public async Task<byte[]> GetAsync(string key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            Entry entry;
            bool owner;
            lock (gate)
            {
                if (disposed) break;
                Purge();
                if (entries.TryGetValue(key, out entry!))
                {
                    owner = false;
                    if (entry.Ready) entry.Expires = clock.GetUtcNow() + Lifetime;
                    Arm();
                }
                else
                {
                    entry = new Entry(key);
                    entries[key] = entry;
                    owner = true;
                }
            }

            if (owner)
            {
                // A fetch nobody else is waiting on that fails leaves a faulted task nobody reads.
                _ = entry.Done.Task.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                byte[] bytes;
                try { bytes = await fetch(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (gate) { if (entries.TryGetValue(key, out var current) && current == entry) entries.Remove(key); }
                    entry.Done.TrySetException(ex);
                    throw;
                }
                lock (gate)
                {
                    // Fetched past the connection's close: handed on, not kept, and no wake set for it.
                    if (disposed) { }
                    else if (bytes.LongLength > MaxBytes) entries.Remove(key);
                    else
                    {
                        entry.Size = bytes.LongLength;
                        entry.Ready = true;
                        entry.Expires = clock.GetUtcNow() + Lifetime;
                        held += entry.Size;
                        Evict(entry);
                        Arm();
                    }
                }
                entry.Done.TrySetResult(bytes);
                return bytes;
            }

            try { return await entry.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            // The caller whose fetch this was gave up; this one did not, so it asks again.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2) { }
        }
        return await fetch(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lets go of every item and the wake: the bytes a closed connection read go now, not when the wake's 30 s are up, and nothing is kept after.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            entries.Clear();
            held = 0;
            timer?.Dispose();
            (timer, timerAt) = (null, DateTimeOffset.MaxValue);
        }
    }

    /// <summary>Marks the item's last chunk as sent: it is dropped after <see cref="FinishedLifetime"/> rather than <see cref="Lifetime"/>.</summary>
    public void Finish(string key)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry) && entry.Ready)
            {
                var soon = clock.GetUtcNow() + FinishedLifetime;
                if (soon < entry.Expires) entry.Expires = soon;
                Arm();
            }
        }
    }

    // Caller holds the gate. Sets the wake for the earliest expiry; none when nothing is kept. A wake that comes early (an item asked for again since) purges
    // nothing and is set again.
    private void Arm()
    {
        var next = DateTimeOffset.MaxValue;
        foreach (var entry in entries.Values) if (entry.Ready && entry.Expires < next) next = entry.Expires;
        if (next == DateTimeOffset.MaxValue)
        {
            timer?.Dispose();
            (timer, timerAt) = (null, DateTimeOffset.MaxValue);
            return;
        }
        if (timer is not null && timerAt <= next) return;
        var due = next - clock.GetUtcNow();
        timer ??= clock.CreateTimer(static state => ((MediaReadCache)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        timerAt = next;
    }

    private void Expire()
    {
        lock (gate)
        {
            timerAt = DateTimeOffset.MaxValue;
            Purge();
            Arm();
        }
    }

    // Caller holds the gate. Entries still being fetched are never dropped.
    private void Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in entries.Values.Where(entry => entry.Ready && entry.Expires <= now).ToList()) Drop(entry);
    }

    // Caller holds the gate. Drops the least recently used finished items until the bounds hold, never the one just stored.
    private void Evict(Entry keep)
    {
        while (entries.Count > MaxEntries || held > MaxBytes)
        {
            var oldest = entries.Values.Where(entry => entry.Ready && entry != keep).OrderBy(entry => entry.Expires).FirstOrDefault();
            if (oldest is null) break;
            Drop(oldest);
        }
    }

    private void Drop(Entry entry)
    {
        if (entries.Remove(entry.Key)) held -= entry.Size;
    }
}
