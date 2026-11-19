using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    // ---- Conversations of their own kept current while closed ----

    // At most this many messages looked up, and conversations kept, per batch. Each lookup is memory a backend already holds —
    // Messenger's is a call to its own helper — and each conversation not in memory is one read and one write of its saved
    // copy on the store's one gate; the batch's sends and receipts wait behind them, so a backfill naming hundreds is left
    // to the openings' reads, as before.
    // A saved copy costs a read and a write on that gate, each a few to a hundred milliseconds, and the next batch waits for
    // them: fewer of those than of copies already in memory.
    private const int KeptLookupsPerBatch = 100, KeptChatsPerBatch = 32, KeptSavedPerBatch = 6;

    // A conversation of its own that is not open takes the messages a batch names into the copy kept of it: the one in
    // memory, marked to be read when it opens, or else its saved copy on disk. Only what a service holds in memory, and
    // whole (IMessagePeek.PeekWholeAsync): no request, and never the list's words for an Instagram photo. Before, any event
    // naming it dropped its copy and the saved copy was never touched while it was closed, so a message that came meanwhile
    // slid in a second after the click, behind the read: an Instagram read is about 600 ms, and Google Voice reads on every
    // opening. One that cannot be had whole is left to that read, as before. A merge has its own path
    // (RefreshKeptMergesAsync); a conversation in a merge is drawn only as part of it.
    private async Task RefreshKeptSinglesAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, CancellationToken token)
    {
        if ((peek ?? lookup as IMessagePeek) is not { } memory) return;
        // Once per batch, by whichever window asks first (ConversationCache.ClaimBatch).
        if (!cache.ClaimBatch(events)) return;
        int chats = 0, updated = 0, unchanged = 0, superseded = 0, saved = 0, changed = 0, missing = 0, deferred = 0, lookups = 0, fromDisk = 0;
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var named in events.Where(e => e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted && !e.ReceiptOnly &&
            e.ChatId is not null && e.Ids is { Count: > 0 }).GroupBy(e => e.ChatId!, StringComparer.Ordinal))
        {
            var chatId = named.Key;
            // Open here or in another window, it is that view's to keep current.
            if (chatId == ActiveChatId || cache.IsShowing(chatId) || MergedConversations.IsMerged(chatId) || merges is not null && merges.DisplayId(chatId) != chatId) continue;
            var wanted = named.SelectMany(e => e.Ids!.Select(id => (chatId, id, e.Kind == RealtimeKind.MessageDeleted))).Distinct().ToArray();
            var asks = wanted.Count(item => !item.Item3);
            var onDisk = cache.Peek(chatId) is null;
            if (chats >= KeptChatsPerBatch || lookups + asks > KeptLookupsPerBatch || onDisk && fromDisk >= KeptSavedPerBatch) { deferred++; continue; }
            if (onDisk) fromDisk++;
            lookups += asks;
            chats++;
            KeptHistory.Result result;
            try
            {
                result = await KeptHistory.KeepAsync(cache, chatId, () => !cache.IsShowing(chatId),
                    copy => KeptHistory.BringInAsync(copy, wanted, memory, token, LinkPreviewsEnabled));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { superseded++; continue; }
            if (result.From == "saved" && result.Outcome != "none") saved++;
            switch (result.Outcome)
            {
                case "updated": updated++; break;
                case "superseded": superseded++; break;
                case "unchanged": unchanged++; break;
            }
            changed += result.Changed; missing += result.Missing;
        }
        if (chats + deferred == 0) return;
        // Counts and a time only: how many closed conversations a batch kept current, from memory or their saved copy, and
        // how many of the messages it named no service could give whole.
        Timed?.Invoke(this, $"history kept singles chats={chats} updated={updated} unchanged={unchanged} superseded={superseded} " +
            $"from_saved={saved} changed={changed} missing={missing} deferred={deferred} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
    }

    // A message for a closed merge's kept copy: memory's whole copy when there is one, else the service's own resolve, as
    // before — unless memory holds only part of it (an Instagram glimpse of a photo or a share in a conversation not read
    // this session), which the resolve would hand back as it is and a reel would be drawn with no picture. That one is
    // left to the opening's read: null.
    private async Task<ChatMessage?> KeptCopyAsync(IMessageSender lookup, string chatId, string messageId, CancellationToken token)
    {
        if ((peek ?? lookup as IMessagePeek) is { } memory)
        {
            if (await memory.PeekWholeAsync(chatId, messageId, token) is { } whole && whole.ChatId == chatId && whole.Id == messageId) return whole;
            // Only a service that holds copies in part (Instagram) answers "part" this way; Messenger misses a whole copy when its
            // pipe is merely busy, and its own resolve, below, is its helper's store as well.
            if (memory.HoldsPartialCopies(chatId) && await memory.PeekAsync(chatId, messageId, token) is not null) return null;
        }
        return await lookup.ResolveMessageAsync(chatId, messageId, token);
    }

    // A copy in memory whose details can stand without a read: not one kept current while closed, whose details no event
    // says anything about. Those used to be dropped, which sent their details to the service as this does.
    private HistorySnapshot? Clean(string chatId) => cache.Get(chatId) is { NeedsRead: false } clean ? clean : null;

    // ---- Quoted messages ----

    // Messages that replies quote, which are not loaded rows themselves, as a service's memory gave them whole. A reply drew
    // "Earlier message isn't loaded" even while the service held the message it quotes; asked before the rows are drawn,
    // the quote is drawn once, as it is. Bounded; a quote memory cannot give is not asked again this opening and keeps
    // the words it always had.
    private readonly Dictionary<string, ChatMessage> quotes = new(StringComparer.Ordinal);
    private readonly HashSet<string> quoteMisses = new(StringComparer.Ordinal);
    private const int QuotesPerDrawing = 24, QuotesKept = 500;
    // A drawing never waits longer than this for them; the rest keep their words and are asked again on the next drawing.
    private static readonly TimeSpan QuoteWait = TimeSpan.FromMilliseconds(100);

    private async Task PeekQuotesAsync(IReadOnlyList<ChatMessage> rows, IMessagePeek? source, long version, CancellationToken token)
    {
        if ((peek ?? source) is not { } memory) return;
        // What these rows quote, newest reply first, up to the bound: the newest are the ones drawn where the reader is. Only
        // these are kept or asked for, so the bound holds however many replies are loaded; keeping every quote the rows
        // named let a long conversation pass it (2026-11-18 review). An older reply past the bound keeps its words.
        HashSet<string>? loaded = null;
        var ranked = new List<(string Chat, string Id, string Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = rows.Count - 1; index >= 0 && ranked.Count < QuotesKept; index--)
        {
            var row = rows[index];
            if (row.ReplyToId is not { } reply || row.IsDeleted) continue;
            var key = MessageIdentity.Key(row.ChatId, reply);
            if (!seen.Add(key)) continue;
            loaded ??= rows.Select(MessageIdentity.Key).ToHashSet(StringComparer.Ordinal);
            if (!loaded.Contains(key)) ranked.Add((row.ChatId, reply, key));
        }
        // Bounded before this drawing's answers go in, so they are there for its render, and only by what the rows no longer
        // need: trimmed with Keys.First() after them, a full map let go of the quotes just found (a Dictionary refills freed
        // slots last in, first out), so they never drew and were asked for again on every drawing.
        if (quotes.Count > QuotesKept - QuotesPerDrawing || quoteMisses.Count > QuotesKept - QuotesPerDrawing)
        {
            var kept = ranked.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in quotes.Keys.Where(key => !kept.Contains(key)).ToArray()) quotes.Remove(key);
            quoteMisses.IntersectWith(kept);
        }
        var wanted = ranked.Where(item => !quotes.ContainsKey(item.Key) && !quoteMisses.Contains(item.Key)).ToList();
        if (wanted.Count == 0) return;
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        int asked = 0, found = 0;
        foreach (var (chat, id, key) in wanted.Take(QuotesPerDrawing))
        {
            var left = QuoteWait - System.Diagnostics.Stopwatch.GetElapsedTime(began);
            if (left <= TimeSpan.Zero) break;
            ChatMessage? message;
            // Each ask is held to what is left of the wait: one Messenger ask is a helper call that may take two seconds, and
            // the drawing waits for it. One cut short is not a miss; it is asked again on the next drawing.
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
            bound.CancelAfter(left);
            try { message = await memory.PeekWholeAsync(chat, id, bound.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (Exception) { break; }
            if (bound.IsCancellationRequested && !token.IsCancellationRequested) break;
            if (version != generation) return;
            asked++;
            if (message is { } whole && whole.ChatId == chat && whole.Id == id) { quotes[key] = whole; found++; }
            else quoteMisses.Add(key);
        }
        if (asked > 0)
            Timed?.Invoke(this, $"history quotes asked={asked} found={found} left={wanted.Count - asked} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
    }

    // A message an event names may have changed: its quote is asked again.
    private void ForgetQuotes(IReadOnlyList<RealtimeEvent> events)
    {
        if (quotes.Count == 0 && quoteMisses.Count == 0) return;
        foreach (var change in events)
            if (change is { Kind: RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted, ChatId: { } chat, Ids: { } ids })
                foreach (var id in ids) { var key = MessageIdentity.Key(chat, id); quotes.Remove(key); quoteMisses.Remove(key); }
    }

    // ---- Link cards a helper made ----

    // A fresh read over rows already held keeps the link cards a helper made for those rows' words until it supplies its
    // own (HistoryMerge.KeepCard). Not while link previews are off here: the card then goes, as it always did.
    private HistorySnapshot WithCards(HistorySnapshot? before, HistorySnapshot fresh)
    {
        if (!LinkPreviewsEnabled) return fresh;
        var carried = HistoryMerge.KeepCards(before, fresh, out var kept);
        cardsKept += kept;
        return carried;
    }
    // Rows an opening's read left with the card they had, for its line (history open … cards=).
    private int cardsKept;
    private ChatMessage WithCard(ChatMessage? before, ChatMessage after) =>
        LinkPreviewsEnabled ? HistoryMerge.KeepCard(before, after) : after;
}
