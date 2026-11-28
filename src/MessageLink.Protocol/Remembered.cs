namespace MessageLink.Protocol;

/// <summary>
/// A client's copy of answers it cannot ask for synchronously (SWITCHBOARD.md 2.6c). A synchronous port member on a window's thread
/// must not wait for the host, so it reads what this holds, and a read of something missing or old asks the host in the background
/// and keeps the answer for the next read. Until an answer arrives the member says the conservative thing (<c>unknown</c>: no SIM, not
/// in memory, not readable), as an unknown capability is unavailable everywhere else.
/// </summary>
/// <remarks>
/// <para>An answer is served stale while it is refreshed, so a value never flickers to <c>unknown</c> because it was old. A refresh that
/// finds a different value calls <c>changed</c> (key, before, after) on a thread-pool thread, so a caller that already acted on the
/// old answer can read again; a refresh that fails keeps the old value and tries again after the lifetime. Every key is asked once at a
/// time. The number of keys is bounded: when it is full the oldest are dropped by emptying the table.</para>
/// <para>Nothing here blocks, and nothing here throws into a caller.</para>
/// </remarks>
internal sealed class Remembered<TKey, TValue> where TKey : notnull
{
    private sealed class Entry(TValue value)
    {
        public TValue Value = value;
        public long Asked;
        public bool Ever;
        public bool Asking;
        public bool Stale;
        // Moved by every Put and MarkStale: a refresh asked before either brings an answer older than what they said, which it must not put back.
        public long Version;
    }

    private readonly Func<TKey, Task<TValue>> ask;
    private readonly TValue unknown;
    private readonly TimeSpan lifetime;
    private readonly int capacity;
    private readonly Action<TKey, TValue, TValue>? changed;
    private readonly object gate = new();
    private readonly Dictionary<TKey, Entry> entries = [];

    public Remembered(Func<TKey, Task<TValue>> ask, TValue unknown, TimeSpan lifetime, Action<TKey, TValue, TValue>? changed = null, int capacity = 512)
    {
        this.ask = ask;
        this.unknown = unknown;
        this.lifetime = lifetime;
        this.changed = changed;
        this.capacity = capacity;
    }

    /// <summary>What is held for <paramref name="key"/>, or the conservative answer when nothing is; a missing or old one is asked for in the background.</summary>
    public TValue Get(TKey key)
    {
        var start = false;
        TValue value;
        Entry? asking = null;
        long version = 0;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count >= capacity) entries.Clear();
                entries[key] = entry = new Entry(unknown);
            }
            value = entry.Value;
            if (!entry.Asking && (entry.Stale || !entry.Ever || Environment.TickCount64 - entry.Asked >= (long)lifetime.TotalMilliseconds))
            {
                entry.Asking = true;
                start = true;
                (asking, version) = (entry, entry.Version);
            }
        }
        if (start) _ = Task.Run(() => RefreshAsync(key, asking!, version));
        return value;
    }

    /// <summary>Starts asking for <paramref name="key"/> if nothing fresh is held, without reading it: for an answer likely to be wanted soon.</summary>
    public void Warm(TKey key) => _ = Get(key);

    /// <summary>Records an answer the caller got another way (a result it just received), as if it had been asked for now.</summary>
    public void Put(TKey key, TValue value)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count >= capacity) entries.Clear();
                entries[key] = entry = new Entry(value);
            }
            entry.Value = value;
            entry.Asked = Environment.TickCount64;
            entry.Ever = true;
            entry.Stale = false;
            entry.Version++;
        }
    }

    /// <summary>Everything held is old from now: still served, and asked for again at the next read.</summary>
    public void MarkStale()
    {
        lock (gate)
            foreach (var entry in entries.Values)
            {
                entry.Stale = true;
                entry.Version++;
            }
    }

    private async Task RefreshAsync(TKey key, Entry asked, long version)
    {
        TValue fresh = unknown;
        var answered = false;
        try
        {
            fresh = await ask(key).ConfigureAwait(false);
            answered = true;
        }
        catch (Exception) { /* the old answer stands until the next read after the lifetime */ }

        TValue before;
        bool differs;
        lock (gate)
        {
            // Dropped (the table was emptied) or replaced since: nothing of this answer is kept.
            if (!entries.TryGetValue(key, out var entry) || entry != asked) return;
            entry.Asking = false;
            // A Put or a MarkStale since the question: the Put's answer is newer, and kept. A MarkStale wants a question asked after it, asked now: a read
            // made while this one was out got the old answer and started nothing, and may be the last read for a while (the SIM chip reads only when told
            // of a change, which this question's answer would have been: review, 2026-11-18).
            if (entry.Version != version)
            {
                if (!entry.Stale) return;
                entry.Asking = true;
                var again = entry.Version;
                _ = Task.Run(() => RefreshAsync(key, asked, again));
                return;
            }
            entry.Asked = Environment.TickCount64;
            entry.Ever = true;
            entry.Stale = false;
            before = entry.Value;
            differs = answered && !EqualityComparer<TValue>.Default.Equals(before, fresh);
            if (answered) entry.Value = fresh;
        }
        if (differs && changed is not null)
        {
            try { changed(key, before, fresh); } catch (Exception) { /* a listener that fails must not fail the refresh */ }
        }
    }
}
