using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A conversation that is not open, kept as current as what each service already holds says it is, so opening it draws
/// what is there now and nothing slides in when its read comes back. Its copy kept in memory is brought up to date where
/// it is; one with none in memory has its saved copy brought up to date on disk instead, without taking a place among the
/// copies kept in memory, which belong to the conversations opened. Either way it is marked to be read when it opens.
/// </summary>
/// <remarks>
/// Nothing here asks a service anything: rows come from memory a backend already holds, and only whole copies of them
/// (IMessagePeek.PeekWholeAsync, IHistoryWindowReader.ReadHeldAsync). A message that cannot be had whole is left out and
/// arrives with the opening's read, as before. Rows taken in this way say nothing of messages no one named between them and
/// the rows before, so the copy remembers where its read rows end (HistorySnapshot.KeptAfter).
/// </remarks>
public static class KeptHistory
{
    public readonly record struct Brought(HistorySnapshot? Updated, int Changed, int Missing);
    public readonly record struct Result(string Outcome, string From, int Changed, int Missing, int Rows);

    /// <summary>The named messages, each taken whole from memory or left out; the deleted ones removed.</summary>
    public static async Task<Brought> BringInAsync(HistorySnapshot copy, IEnumerable<(string ChatId, string Id, bool Deleted)> named,
        IMessagePeek peek, CancellationToken token, bool keepCards = true)
    {
        var changes = new List<(string Key, ChatMessage? Message)>();
        var missing = 0;
        foreach (var (chat, id, deleted) in named)
        {
            if (deleted) { changes.Add((MessageIdentity.Key(chat, id), null)); continue; }
            var message = await peek.PeekWholeAsync(chat, id, token);
            if (message is null || message.ChatId != chat || message.Id != id) { missing++; continue; }
            changes.Add((MessageIdentity.Key(message), message));
        }
        var (updated, changed) = Apply(copy, changes, contiguous: false, keepCards);
        return new(updated, changed, missing);
    }

    /// <summary>
    /// Rows put into a copy (a null message removes its row), marked to be read on opening; null when none changed it.
    /// Contiguous: the rows reach back to the copy's own with nothing between (a held page that is complete), so the
    /// rows read before them still meet what they bring.
    /// </summary>
    public static (HistorySnapshot? Updated, int Changed) Apply(HistorySnapshot copy, IEnumerable<(string Key, ChatMessage? Message)> changes, bool contiguous,
        bool keepCards = true)
    {
        var rows = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var row in copy.Messages) rows[MessageIdentity.Key(row)] = row;
        var changed = 0;
        var added = false;
        foreach (var (key, message) in changes)
        {
            if (message is null) { if (rows.Remove(key)) changed++; continue; }
            rows.TryGetValue(key, out var before);
            if (before is null && BeforeHeld(copy, message)) continue;
            // A card a helper made is kept only while link previews are on, as a read keeps it (ConversationViewModel.WithCard).
            var next = keepCards ? HistoryMerge.KeepCard(before, message) : message;
            if (before is not null && Same(before, next)) continue;
            added |= before is null;
            rows[key] = next;
            changed++;
        }
        if (changed == 0) return (null, 0);
        var ordered = rows.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .ThenBy(MessageIdentity.Key, StringComparer.Ordinal).TakeLast(ConversationCache.MaxMessages).ToArray();
        var atLimit = ordered.Length >= ConversationCache.MaxMessages || copy.AtLimit;
        var keptAfter = !added || contiguous ? copy.KeptAfter : ReadThrough(copy);
        return (copy with { Messages = ordered, NeedsRead = true, KeptAfter = keptAfter, AtLimit = atLimit, HasMore = copy.HasMore && !atLimit }, changed);
    }

    /// <summary>
    /// Whether a message is older than everything the copy holds while there is more history before it: a reaction or an
    /// edit to an old message, which taken in would sit above a gap, drawn out of place until Load earlier reached it. It
    /// is left to the read that pages back to it.
    /// </summary>
    public static bool BeforeHeld(HistorySnapshot copy, ChatMessage message) =>
        copy.HasMore && copy.Messages.Count > 0 && message.Timestamp < copy.Messages.Min(m => m.Timestamp);

    /// <summary>
    /// Where a copy's rows from reads end, for a copy about to take in a new row while closed: its newest row, or where an
    /// earlier such row already left it. A copy with no rows has nothing a read could meet.
    /// </summary>
    public static DateTimeOffset ReadThrough(HistorySnapshot copy)
    {
        if (copy.Messages.Count == 0) return DateTimeOffset.MinValue;
        var newest = copy.Messages.Max(m => m.Timestamp);
        return copy.KeptAfter is { } earlier && earlier < newest ? earlier : newest;
    }

    /// <summary>
    /// Brings changes into a closed conversation's copy: the one kept in memory, else its saved copy, which is written back
    /// either way so the first opening after a restart starts from it too. Closed is asked again before anything lands; a
    /// copy opened, put back or changed meanwhile stands as it is.
    /// </summary>
    public static async Task<Result> KeepAsync(ConversationCache cache, string displayId, Func<bool> closed,
        Func<HistorySnapshot, Task<Brought>> bring, Func<HistorySnapshot, bool>? usable = null)
    {
        // One keeper at a time for this conversation (ConversationCache.KeepingAsync), from reading its copy to writing it.
        using var turn = await cache.KeepingAsync(displayId);
        var copy = cache.Peek(displayId);
        var fromSaved = copy is null;
        var from = fromSaved ? "saved" : "memory";
        if (fromSaved)
        {
            copy = await cache.ReadSavedAsync(displayId);
            if (copy is null || usable?.Invoke(copy) == false) return new("none", from, 0, 0, 0);
            if (!closed() || cache.Peek(displayId) is not null) return new("superseded", from, 0, 0, copy.Messages.Count);
        }
        var brought = await bring(copy!);
        if (brought.Updated is not { } updated) return new("unchanged", from, 0, brought.Missing, copy!.Messages.Count);
        var kept = fromSaved ? closed() && cache.Peek(displayId) is null : closed() && cache.Replace(copy!, updated);
        if (kept) await cache.SaveAsync(updated, keepPlace: true);
        return new(kept ? "updated" : "superseded", from, brought.Changed, brought.Missing, updated.Messages.Count);
    }

    // The same message as it was: a copy a service gives again unchanged moves nothing, writes nothing, and lets a second
    // window applying the same batch find the copy already current.
    private static bool Same(ChatMessage a, ChatMessage b) =>
        ReferenceEquals(a, b) || JsonSerializer.SerializeToUtf8Bytes(a).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b));
}

/// <summary>
/// What a connection caught up on in a conversation nobody has open (Instagram's catch-up: what came while the app was
/// closed), put into that conversation's kept or saved copy, merged or not. The catch-up holds its newest page for the
/// opening to draw (ReadHeldAsync), but a held page stops answering as soon as anything newer is listed, and the reel
/// that came while the app was closed then slid in again behind the next text (2026-10-02). Written into the copy, what
/// arrives later builds on it. From memory only, through the same held page: no request, no notification.
/// </summary>
public sealed class CaughtUpHistory(ConversationCache cache, IHistoryWindowReader held, MergedConversations? merges,
    Func<string, bool> isOpen, Action<string>? log = null, Func<bool>? linkPreviews = null)
{
    private const int HeldRows = 100;

    /// <summary>On the interface thread, where the cache is kept. Named: how many messages the catch-up found new.</summary>
    public async Task OfferAsync(string chatId, int named, CancellationToken token = default)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        if (MergedConversations.IsMerged(chatId)) return;
        var display = chatId;
        if (merges is not null)
        {
            try { await merges.EnsureLoadedAsync(); }
            catch (Exception) { Write("failed", "none", 0, false, 0); return; }
            display = merges.DisplayId(chatId);
        }
        if (isOpen(display)) { Write("open", "none", 0, false, 0); return; }
        var complete = false;
        KeptHistory.Result result;
        try
        {
            result = await KeptHistory.KeepAsync(cache, display, () => !isOpen(display), async copy =>
            {
                // From this conversation's newest row in the copy; a merge holding none of its rows, from the copy's newest,
                // which its read held every member's messages up to.
                var own = copy.Messages.Where(m => m.ChatId == chatId).Select(m => m.Timestamp).ToArray();
                var after = own.Length > 0 ? own.Max() : copy.Messages.Count > 0 ? copy.Messages.Max(m => m.Timestamp) : DateTimeOffset.MinValue;
                if (await held.ReadHeldAsync(chatId, after, HeldRows, token) is not { Messages.Count: > 0 } page) return new(null, 0, 0);
                complete = page.Complete;
                var rows = page.Messages.Where(m => m.ChatId == chatId).Select(m => (MessageIdentity.Key(m), (ChatMessage?)m));
                var (updated, changed) = KeptHistory.Apply(copy, rows, contiguous: page.Complete, keepCards: linkPreviews?.Invoke() ?? true);
                return new(updated, changed, 0);
            },
            // A merge's copy made before the user added a conversation to it opens as it always did (LoadInitialAsync).
            usable: copy => display == chatId || merges?.AddedSince(display, copy.Chat.MemberChatIds) != true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Write("failed", "none", 0, false, 0); return; }
        Write(result.Outcome, result.From, result.Changed, complete, result.Rows);

        void Write(string outcome, string from, int changed, bool whole, int rows) =>
            log?.Invoke($"history caught up outcome={outcome} from={from} merged={MergedConversations.IsMerged(display)} named={named} " +
                $"changed={changed} complete={whole} rows={rows} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
    }
}
