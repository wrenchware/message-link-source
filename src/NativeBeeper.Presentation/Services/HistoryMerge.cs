using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class HistoryMerge
{
    /// <summary>
    /// A conversation as this PC's copy of a server's whole history holds it, while that copy is known current (the app sets this when it is a server's
    /// client; HistoryCopies.Snapshot): its messages oldest first, whether more lie before them, and the page position to read on from. Null otherwise.
    /// </summary>
    public static Func<string, ServerHistoryCopy?>? ServerCopy { get; set; }

    // Keeps history loaded earlier when a fresh newest page overlaps it, instead of shrinking the conversation
    // back to one page on every open or reconnect. The fresh page is authoritative for its own time window, so
    // messages deleted there disappear. Without overlap (a gap), or when the fresh page is the whole history,
    // the fresh page is used as is. Rows a copy took in while it was closed (HistorySnapshot.KeptAfter) do not count as
    // overlap: messages nothing named may be missing below them, and keeping the older rows would keep that gap for good.
    public static HistorySnapshot KeepOlder(HistorySnapshot? saved, HistorySnapshot fresh, bool freshHasMore)
    {
        if (saved is not null && saved.Chat.Id == fresh.Chat.Id && MergedConversations.IsMerged(fresh.Chat.Id) && fresh.UnavailableSources is { Count: > 0 } missing)
            return KeepUnavailable(saved, fresh, missing);
        // Following a server's whole history (SWITCHBOARD.md stage 9, B3): one copy. The rows are the PC's copy of the server's, as far back as it holds,
        // never this screen's own older rows, which nothing corrects below the fresh window.
        if (!MergedConversations.IsMerged(fresh.Chat.Id) && fresh.Messages.Count > 0 && ServerCopy?.Invoke(fresh.Chat.Id) is { Messages.Count: > 0 } whole)
        {
            var held = whole.Messages.Select(MessageIdentity.Key).ToHashSet();
            // Only a copy that reaches back as far as what is drawn: one shorter (a page after a restart, one started again) would cut the conversation
            // under someone scrolled back in it (review, 2026-10-30); the rows then join as before. At the copy's oldest instant only those it holds count:
            // messages sharing a time are ordered by more than it.
            var reaches = !whole.MoreOlder || saved is null || saved.Chat.Id != fresh.Chat.Id || saved.Messages.Count == 0
                || saved.Messages.All(m => m.Timestamp > whole.Messages[0].Timestamp || held.Contains(MessageIdentity.Key(m)));
            if (reaches && fresh.Messages.All(m => held.Contains(MessageIdentity.Key(m))))
                return fresh with
                {
                    Messages = [.. whole.Messages.TakeLast(ConversationCache.MaxMessages)], Cursor = whole.Cursor, HasMore = whole.MoreOlder,
                    AtLimit = false, PaginationStalled = false,
                };
        }
        if (saved is null || saved.Chat.Id != fresh.Chat.Id || saved.Messages.Count == 0 || fresh.Messages.Count == 0 || !freshHasMore) return fresh;
        var freshIds = fresh.Messages.Select(MessageIdentity.Key).ToHashSet();
        if (!Read(saved).Any(m => freshIds.Contains(MessageIdentity.Key(m)))) return fresh;
        var windowStart = fresh.Messages.Min(m => m.Timestamp);
        var older = saved.Messages.Where(m => m.Timestamp < windowStart && !freshIds.Contains(MessageIdentity.Key(m))).ToArray();
        if (older.Length == 0) return fresh;
        var merged = older.Concat(fresh.Messages).OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal).TakeLast(ConversationCache.MaxMessages).ToArray();
        var atLimit = merged.Length >= ConversationCache.MaxMessages || saved.AtLimit;
        var keep = Continues(saved, fresh);
        return fresh with
        {
            Messages = merged, Cursor = keep ? saved.Cursor : fresh.Cursor,
            HasMore = (keep ? saved.HasMore : fresh.HasMore) && !atLimit,
            SeenCursors = keep ? saved.SeenCursors : fresh.SeenCursors,
            AtLimit = atLimit, PaginationStalled = keep ? saved.PaginationStalled : fresh.PaginationStalled
        };
    }

    // The rows a copy holds from reads: all of them, unless it took some in while it was closed (KeptAfter).
    private static IEnumerable<ChatMessage> Read(HistorySnapshot copy) =>
        copy.KeptAfter is { } after ? copy.Messages.Where(m => m.Timestamp <= after) : copy.Messages;

    // A merged page some linked service did not answer keeps that service's saved rows. The services that did answer keep,
    // as above, what was loaded of them before the fresh window. Dropping it left the top of the history to the missing
    // service alone until paging put the rest back between rows already drawn, and the smaller history was saved in that
    // shape. A merged page holds every answering service's rows newer than its oldest, so rows older than it cannot hide a
    // gap; without an overlap there may be one, and the fresh page stands alone as before. So do the missing service's
    // rows: only those as new as the fresh page are kept. Its older ones reached back past where the others then reached,
    // and paging, starting again from the fresh page, filled the history to its 1,000 rows before it brought the others
    // back that far, leaving their rows between out of reach (seen 2026-10-02: Instagram's reaching 274 days, Google's 22).
    // The fresh paging token has not read the missing service past the fresh page, so they come back with paging, in order.
    private static HistorySnapshot KeepUnavailable(HistorySnapshot saved, HistorySnapshot fresh, IReadOnlyCollection<string> missing)
    {
        var freshIds = fresh.Messages.Select(MessageIdentity.Key).ToHashSet();
        var overlaps = fresh.Messages.Count > 0 && Read(saved).Any(m => !missing.Contains(m.ChatId) && freshIds.Contains(MessageIdentity.Key(m)));
        var windowStart = overlaps ? fresh.Messages.Min(m => m.Timestamp) : DateTimeOffset.MinValue;
        var answered = saved.Messages.Where(m => !missing.Contains(m.ChatId) && m.Timestamp < windowStart && !freshIds.Contains(MessageIdentity.Key(m))).ToArray();
        var floor = overlaps || fresh.Messages.Count == 0 || !fresh.HasMore ? DateTimeOffset.MinValue : fresh.Messages.Min(m => m.Timestamp);
        var retained = saved.Messages.Where(m => missing.Contains(m.ChatId) && m.Timestamp >= floor);
        var combined = fresh.Messages.Concat(retained).Concat(answered).DistinctBy(MessageIdentity.Key).OrderBy(m => m.Timestamp)
            .ThenBy(m => m.SortKey, StringComparer.Ordinal).ThenBy(MessageIdentity.Key, StringComparer.Ordinal).TakeLast(ConversationCache.MaxMessages).ToArray();
        var full = combined.Length >= ConversationCache.MaxMessages;
        // Rows kept from before continue from where they were loaded to, as on the path above.
        var keep = answered.Length > 0 && Continues(saved, fresh);
        return fresh with
        {
            Messages = combined, AtLimit = full || keep && saved.AtLimit,
            Cursor = keep ? saved.Cursor : fresh.Cursor, SeenCursors = keep ? saved.SeenCursors : fresh.SeenCursors,
            HasMore = (keep ? saved.HasMore : fresh.HasMore) && !full,
            PaginationStalled = keep ? saved.PaginationStalled : fresh.PaginationStalled
        };
    }

    // Whether paging goes on from where the kept history had got to. A merged conversation whose members changed can:
    // its paging token follows the change (MergedChatReader.Reconcile), where starting again from every service's newest
    // page re-read everything already on screen, an Instagram request per page among them. With no token left there is
    // nothing to follow, so paging starts again from the fresh page. So it does from paging that had stopped: a fresh read is the "Refresh to try again".
    private static bool Continues(HistorySnapshot saved, HistorySnapshot fresh) =>
        !saved.PaginationStalled && (!MergedConversations.IsMerged(fresh.Chat.Id) || saved.Cursor is not null ||
        saved.Chat.MemberChatIds.ToHashSet(StringComparer.Ordinal).SetEquals(fresh.Chat.MemberChatIds));

    // A link card a helper made for the address in a message's words (Google Messages', and Messenger's, generated
    // previews: ids under "link-preview:"). Google's helper keeps the cards it made in memory only, so after a restart a
    // fresh copy of the message came without the card the saved copy had: the card went, the row shrank, and it grew
    // again seconds later when the helper fetched the page once more (2026-10-02). The same message with the same words
    // keeps the card it had until the helper supplies its own — the same id, made from the message and the address — and
    // never across an edit to the words or for a message taken back.
    public static bool IsMadeCard(MessageAttachment attachment, string? text) =>
        attachment.Link is { Length: > 0 } link && attachment.Id?.Contains("link-preview:", StringComparison.Ordinal) == true &&
        text?.Contains(link, StringComparison.Ordinal) == true;

    public static ChatMessage KeepCard(ChatMessage? before, ChatMessage after)
    {
        if (before is null || ReferenceEquals(before, after) || after.IsDeleted || after.IsHidden || before.IsDeleted || before.Attachments.Count == 0 ||
            !string.Equals(before.Text, after.Text, StringComparison.Ordinal) || before.ChatId != after.ChatId || before.Id != after.Id) return after;
        List<MessageAttachment>? cards = null;
        foreach (var card in before.Attachments)
            if (IsMadeCard(card, before.Text) && !after.Attachments.Any(item => item.Link == card.Link)) (cards ??= []).Add(card);
        return cards is null ? after : after with { Attachments = [.. after.Attachments, .. cards] };
    }

    // The same for a whole fresh page set over what was there: each fresh row keeps the card its earlier copy had. The
    // fresh snapshot itself when nothing needed one; kept counts the rows that did.
    public static HistorySnapshot KeepCards(HistorySnapshot? before, HistorySnapshot fresh) => KeepCards(before, fresh, out _);
    public static HistorySnapshot KeepCards(HistorySnapshot? before, HistorySnapshot fresh, out int kept)
    {
        kept = 0;
        if (before is null || before.Messages.Count == 0 || fresh.Messages.Count == 0) return fresh;
        Dictionary<string, ChatMessage>? carded = null;
        foreach (var message in before.Messages)
            if (!message.IsDeleted && message.Attachments.Count > 0 && message.Attachments.Any(item => IsMadeCard(item, message.Text)))
                (carded ??= new(StringComparer.Ordinal))[MessageIdentity.Key(message)] = message;
        if (carded is null) return fresh;
        ChatMessage[]? rows = null;
        for (var index = 0; index < fresh.Messages.Count; index++)
        {
            var message = fresh.Messages[index];
            if (!carded.TryGetValue(MessageIdentity.Key(message), out var earlier)) continue;
            var withCard = KeepCard(earlier, message);
            if (ReferenceEquals(withCard, message)) continue;
            rows ??= [.. fresh.Messages];
            rows[index] = withCard;
            kept++;
        }
        return rows is null ? fresh : fresh with { Messages = rows };
    }
}

/// <summary>A conversation as this PC's copy of a server's whole history holds it (<see cref="HistoryMerge.ServerCopy"/>).</summary>
public sealed record ServerHistoryCopy(IReadOnlyList<ChatMessage> Messages, bool MoreOlder, string? Cursor);
