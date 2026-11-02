using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

// Bounded in-memory LRU with optional protected SQLite snapshots for startup display.
public sealed class ConversationCache(PrivateState? state = null)
{
    // KeptAfter appended: a copy saved before it existed reads as one with every row from a read.
    private sealed record Saved(ChatSummary Chat, ChatMessage[] Messages, string? Cursor, bool HasMore, string[] Seen, bool AtLimit, bool Stalled,
        DateTimeOffset? KeptAfter = null);
    public async Task<HistorySnapshot?> ReadSavedAsync(string chatId)
    {
        if (state is null) return null;
        try
        {
            var profile = await state.ProfileAsync(); if (profile is null) return null;
            var saved = await state.ReadAsync<Saved>(profile, "cache", "history:" + chatId);
            // One saved with its paging stopped says nothing about the next run: it is read afresh rather than opened saying it cannot go back (2026-11-02).
            return saved is null || saved.Chat.Id != chatId || saved.Stalled ? null : new(ReceiptRules.FromSaved(saved.Chat), saved.Messages, saved.Cursor, saved.HasMore, saved.Seen.ToHashSet(), saved.AtLimit, saved.Stalled,
                KeptAfter: saved.KeptAfter);
        }
        catch { return null; } // Cache is expendable; fall through to authoritative network reads.
    }
    // keepPlace: a closed conversation kept up to date, which keeps its place among the saved copies, as Peek and Replace
    // keep its place here: only opening one counts as using it.
    public async Task SaveAsync(HistorySnapshot snapshot, bool keepPlace = false)
    {
        if (state is null || snapshot.PaginationStalled) return;
        try
        {
            var profile = await state.ProfileAsync(); if (profile is null) return;
            var saved = new Saved(snapshot.Chat, snapshot.Messages.ToArray(), snapshot.Cursor, snapshot.HasMore,
                snapshot.SeenCursors.ToArray(), snapshot.AtLimit, snapshot.PaginationStalled, snapshot.KeptAfter);
            if (keepPlace) await state.WriteInPlaceAsync(profile, "cache", "history:" + snapshot.Chat.Id, saved);
            else await state.WriteAsync(profile, "cache", "history:" + snapshot.Chat.Id, saved);
        }
        catch { } // Failed cache writes must not make successful network reads fail.
    }
    private readonly Dictionary<string, HistorySnapshot> snapshots = [];
    private readonly LinkedList<string> recency = [];
    public const int MaxMessages = 1000;
    public int SnapshotCount => snapshots.Count;
    public HistorySnapshot? Get(string chatId)
    {
        if (!snapshots.TryGetValue(chatId, out var value)) return null;
        recency.Remove(chatId); recency.AddLast(chatId);
        return value;
    }
    public void Put(HistorySnapshot snapshot)
    {
        snapshots[snapshot.Chat.Id] = snapshot;
        recency.Remove(snapshot.Chat.Id); recency.AddLast(snapshot.Chat.Id);
        while (recency.Count > 8) { snapshots.Remove(recency.First!.Value); recency.RemoveFirst(); }
    }
    public void Remove(string chatId) { if (snapshots.Remove(chatId)) recency.Remove(chatId); }
    // A kept history read and written back without either counting as a use of it, so keeping it up to date while it is
    // not open does not keep it from making room. The write lands only while the history read is still the one kept.
    public HistorySnapshot? Peek(string chatId) => snapshots.GetValueOrDefault(chatId);
    public bool Replace(HistorySnapshot read, HistorySnapshot next)
    {
        if (!snapshots.TryGetValue(read.Chat.Id, out var held) || !ReferenceEquals(held, read)) return false;
        snapshots[read.Chat.Id] = next;
        return true;
    }
    // Every copy kept, each to be read when it next opens. A resync or a reconnect says something may have moved anywhere,
    // not what: dropping them all (as every provider's connection did, four times at launch and again after each wake) sent
    // each reopening back to an older saved copy, and the messages since slid in after its read (2026-10-02). Kept, each is
    // drawn at once as it was and then read, as a merge kept from its members' events already is.
    public int MarkAllNeedRead()
    {
        var marked = 0;
        foreach (var (id, snapshot) in snapshots.ToArray())
            if (!snapshot.NeedsRead) { snapshots[id] = snapshot with { NeedsRead = true }; marked++; }
        return marked;
    }
    public void Clear() { snapshots.Clear(); recency.Clear(); }

    // One keeper at a time per conversation. Keeping a closed conversation current reads its copy, waits on lookups, and
    // writes the copy back; two at once (a sync batch's members and Instagram's catch-up, at launch) each read the same
    // saved copy and the later write dropped the other's rows — the reel the catch-up had just put in, which then slid in
    // again on opening. Turns, rather than a compare-and-swap, because a saved copy is not held in memory to compare.
    private readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> keeping = new(StringComparer.Ordinal);
    public async Task<IDisposable> KeepingAsync(string chatId, CancellationToken token = default)
    {
        SemaphoreSlim gate;
        lock (keeping)
        {
            var entry = keeping.GetValueOrDefault(chatId);
            gate = entry.Gate ?? new SemaphoreSlim(1, 1);
            keeping[chatId] = (gate, entry.Users + 1);
        }
        try { await gate.WaitAsync(token); }
        catch { Leave(chatId, gate, entered: false); throw; }
        return new Turn(() => Leave(chatId, gate, entered: true));
    }
    private void Leave(string chatId, SemaphoreSlim gate, bool entered)
    {
        if (entered) gate.Release();
        lock (keeping)
        {
            var entry = keeping[chatId];
            if (entry.Users <= 1) keeping.Remove(chatId);
            else keeping[chatId] = (gate, entry.Users - 1);
        }
    }
    private sealed class Turn(Action leave) : IDisposable
    {
        private Action? leave = leave;
        public void Dispose() => Interlocked.Exchange(ref leave, null)?.Invoke();
    }

    // Every window applies the same sync batch, and the conversations it names that nobody has open are kept current once,
    // by the first window to ask: the rest found them already current, but only after looking every message up again and
    // reading every saved copy again, while the next batch waited (2026-10-03 review).
    private object? keptBatch;
    public bool ClaimBatch(object batch)
    {
        if (ReferenceEquals(keptBatch, batch)) return false;
        keptBatch = batch;
        return true;
    }

    // The conversation each view has open, every window's: one open anywhere is kept current by its own view, so keeping
    // closed ones current (KeptHistory) leaves it alone rather than writing a second copy over the first. Held weakly: a
    // view let go without being closed must not be kept alive by this.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> showing = new();
    public void Showing(object view, string? chatId) { if (chatId is null) showing.Remove(view); else showing.AddOrUpdate(view, chatId); }
    public bool IsShowing(string chatId)
    {
        foreach (var (_, open) in showing) if (open == chatId) return true;
        return false;
    }
}

public sealed record HistorySnapshot(ChatSummary Chat, IReadOnlyList<ChatMessage> Messages, string? Cursor,
    bool HasMore, IReadOnlySet<string> SeenCursors, bool AtLimit = false, bool PaginationStalled = false,
    // Transient provider state. It belongs in the in-memory cache so switching away and back cannot make an
    // unfinished refresh look complete; protected disk snapshots intentionally start a new check after launch.
    bool IsRefreshing = false, IReadOnlyList<string>? UnavailableSources = null, string? Warning = null,
    // A conversation that is not open, kept up to date from the events that name it (ConversationViewModel, KeptHistory):
    // its rows are current as far as those events tell, so opening draws them at once, and still reads, since events say
    // nothing of what its details became or of a message no event named.
    bool NeedsRead = false,
    // Rows newer than this came into the copy while it was closed — from events, or a catch-up's page that did not reach
    // back to the copy — rather than from a read of the newest page, so messages nothing named may be missing between them
    // and the rows before. Opening counts only rows up to here as overlapping what it reads (HistoryMerge.KeepOlder): a
    // read that meets only the kept rows could otherwise keep the older rows below a gap for good. Null when every row
    // came from reads. Saved with the copy.
    DateTimeOffset? KeptAfter = null);
