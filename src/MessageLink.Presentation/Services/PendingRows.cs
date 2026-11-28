using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The sends of this PC's that have not settled, as rows of their conversation: each outbox item drawn with its typed state
/// (<see cref="LocalSend"/>) until the service's own copy is known to be it, then that copy in its place; the scheduled
/// messages after everything; and the reactions, edits and deletes made here shown at once while their service makes them.
/// One for each conversation view. What it knows beyond its inputs is when each confirmed send was made here, and which
/// rows a conversation already held when each send was made.
/// </summary>
public sealed class PendingRows
{
    /// <summary>
    /// A copy with no receipt is a send's when it has the same words, is the account's own, and is timed from this long before
    /// the send to <see cref="EchoAfter"/> after it (#91, owner 2026-11-26: Android's rule, in both apps).
    /// </summary>
    public static readonly TimeSpan EchoBefore = TimeSpan.FromSeconds(5), EchoAfter = TimeSpan.FromMinutes(2);

    // When each send confirmed here was made on this PC (Outbox CreatedAt), by its message: a send still on its way is
    // drawn after what was sent before it, never after what was sent after it (Project).
    private readonly Dictionary<string, DateTimeOffset> sentAt = [];
    private readonly Queue<string> sentOrder = new();
    // The rows a conversation already held when each send was made (or sent again by Retry, which makes it anew), by the
    // send's local id, with the moment it was made: never taken as its copy.
    private readonly Dictionary<string, (DateTimeOffset At, HashSet<string> Rows)> known = new(StringComparer.Ordinal);
    private readonly Queue<string> knownOrder = new();
    // The copies taken by the words of a send, by copy, with the send: still that send's once it has settled and left the
    // outbox, so a later send of the same words is never taken for it too (one copy per send, #91).
    private readonly Dictionary<string, string> taken = new(StringComparer.Ordinal);
    private readonly Queue<string> takenOrder = new();
    /// <summary>How soon after a send is made the rows its conversation holds are noted as held at Send: a send first seen
    /// later (made while another conversation showed, or before a window opened) knows none, since its copy may be among them.</summary>
    public static readonly TimeSpan SeenAtSend = TimeSpan.FromSeconds(2);

    /// <summary>The state a send's row shows.</summary>
    public static LocalSend StateOf(OutgoingMessage item, bool canRetry = false) => new(item switch
    {
        { State: OutgoingState.Rejected, CanRestoreText: false } => LocalSendState.AttachmentRejected,
        { State: OutgoingState.Rejected } => LocalSendState.Rejected,
        { State: OutgoingState.Pending, Unconfirmed: true } => LocalSendState.Unconfirmed,
        { State: OutgoingState.Pending } => LocalSendState.Pending,
        { State: OutgoingState.Held } => LocalSendState.Held,
        { State: OutgoingState.Unknown } => LocalSendState.Unknown,
        _ => LocalSendState.Sending,
    }, item.State == OutgoingState.Rejected ? item.Explanation : null, item.State == OutgoingState.Rejected && canRetry);

    /// <summary>
    /// Where a row's send is, when it is one not settled: an outbox row's own state, or a scheduled message on its way, whose
    /// state comes from a server's schedule as a wire word. Null for a service's message.
    /// </summary>
    public static LocalSendState? Of(ChatMessage message) =>
        message.Delivery.Local?.State ?? (message.Delivery.SendState == ScheduledSendRows.Sending ? LocalSendState.Sending : null);

    /// <summary>A row of this PC's making (a send not settled, or a scheduled message), not a service's message.</summary>
    public static bool IsLocal(ChatMessage message) =>
        message.Delivery.Local is not null || ScheduledSendRows.Is(message) || message.Delivery.SendState == ScheduledSendRows.Sending;

    /// <summary>The ids a send's copy can carry that say it is that send: the receipt, the confirmed id, each photo's.</summary>
    public static IEnumerable<string> ReceiptIds(OutgoingMessage item) =>
        new[] { item.PendingId, item.ConfirmedId }.OfType<string>().Concat(item.PendingMessageIds ?? []);

    /// <summary>A send confirmed as this message was made at this moment here.</summary>
    public void NoteSentAt(ChatMessage message, DateTimeOffset at)
    {
        var key = MessageIdentity.Key(message);
        sentAt[key] = at;
        Keep(sentAt, sentOrder, key, 1000);
    }

    /// <summary>
    /// A send has just been made while its conversation showed these rows: none of them is its copy, however alike. Noted
    /// once, the first time the send is seen.
    /// </summary>
    public void NoteSent(OutgoingMessage item, IEnumerable<ChatMessage> shown, DateTimeOffset? now = null)
    {
        if (Knows(item) || (now ?? DateTimeOffset.UtcNow) - item.CreatedAt > SeenAtSend) return;
        known[item.LocalId] = (item.CreatedAt, shown.Where(m => m.ChatId == item.ChatId && m.IsOutgoing && m.Text == item.Text).Select(MessageIdentity.Key).ToHashSet(StringComparer.Ordinal));
        Keep(known, knownOrder, item.LocalId, 200);
    }

    /// <summary>Whether this send was seen being made (this time: Retry makes it again), so the rows held then are known.</summary>
    public bool Knows(OutgoingMessage item) => known.TryGetValue(item.LocalId, out var seen) && seen.At == item.CreatedAt;

    /// <summary>A deleted conversation's own entries.</summary>
    public void Forget(string chatId)
    {
        // MessageIdentity.Key begins with the conversation id and its length.
        var prefix = $"{chatId.Length}:{chatId}";
        foreach (var key in sentAt.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) sentAt.Remove(key);
    }

    /// <summary>
    /// A conversation's rows: its history, the confirmed sends a read may not have yet, its sends not settled, and its scheduled
    /// messages, in the order drawn. <paramref name="items"/> are the outbox's for the conversation (or each member of a merge);
    /// <paramref name="scheduled"/> the schedule's not taken by the outbox yet.
    /// </summary>
    public IEnumerable<ChatMessage> Project(IReadOnlyList<ChatMessage> history, IEnumerable<ChatMessage> confirmations, IEnumerable<OutgoingMessage> items,
        IEnumerable<ScheduledSend> scheduled, Func<string, string?> bubbleFamily, Func<OutgoingMessage, bool>? canRetry = null)
    {
        var rows = history.ToDictionary(MessageIdentity.Key);
        foreach (var message in confirmations) rows[MessageIdentity.Key(message)] = message;
        // The network's copy of a sent message can arrive before the send call has returned its id, and for that moment
        // nothing links the two: both were drawn, then one vanished and the list jumped. A still-unlinked pending message
        // is also matched to an outgoing message with the same text sent around the same time — each such message
        // matching at most one pending copy, oldest first, so two identical sends in a row still show as two.
        var pendingItems = items.OrderBy(item => item.CreatedAt).ToArray();
        var claimed = pendingItems.SelectMany(item => new[] { item.PendingId, item.ConfirmedId }.OfType<string>().Select(id => MessageIdentity.Key(item.ChatId, id))).ToHashSet();
        // Our own copies that carry the id their receipt named them by (Google's transaction id), by that id.
        Dictionary<string, string>? byReceipt = null;
        if (pendingItems.Any(item => item.ConfirmedId is null && (item.PendingId is not null || item.PendingMessageIds is not null)))
            foreach (var (key, row) in rows)
                if (row.PendingId is { } pendingId) (byReceipt ??= new(StringComparer.Ordinal)).TryAdd(MessageIdentity.Key(row.ChatId, pendingId), key);
        bool HideCopy(string chatId, string id)
        {
            var hidden = rows.Remove(MessageIdentity.Key(chatId, id));
            if (byReceipt?.GetValueOrDefault(MessageIdentity.Key(chatId, id)) is { } copy) hidden |= rows.Remove(copy);
            return hidden;
        }
        var drawn = new List<OutgoingMessage>(pendingItems.Length);
        foreach (var item in pendingItems)
        {
            var pendingPhotoSet = item.Attachments is { Count: > 1 } && item.ConfirmedId is null;
            var copyHidden = false;
            if (pendingPhotoSet)
            {
                // Only receipt-identified copies are covered by the still-pending collage. Albums and files are never
                // matched by count, kind, size or time (#91): a send that may not have gone would read as sent.
                foreach (var id in item.PendingMessageIds ?? (item.PendingId is null ? [] : new[] { item.PendingId }))
                    HideCopy(item.ChatId, id);
            }
            else if ((item.PendingId is not null && rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.PendingId))) || (item.ConfirmedId is not null && rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.ConfirmedId)))) continue;
            // A copy that arrives before its send is confirmed was drawn beside the pending message and then replaced it:
            // for a photo, a new tile that downloaded our own picture again, an empty box and a spinner where the photo had
            // been. Known exactly by its receipt, the copy waits behind the pending message instead, and the confirmation
            // hands the message its identity in place (OutboxResolved).
            else if (item is { ConfirmedId: null, PendingId: { } single }) copyHidden = HideCopy(item.ChatId, single);
            // Linked only by a pending id the network's copy does not carry (Google's transaction id) counts as unlinked too:
            // that gap, between the send call returning and the copy being confirmed, drew a second copy for half a second.
            if (!copyHidden && item.Attachments is not { Count: > 0 } && item.ConfirmedId is null && item.State is OutgoingState.Sending or OutgoingState.Pending &&
                EchoOf(item, history, claimed) is { } echo)
            {
                claimed.Add(MessageIdentity.Key(echo));
                if (!taken.ContainsKey(MessageIdentity.Key(echo))) { taken[MessageIdentity.Key(echo)] = item.LocalId; Keep(taken, takenOrder, MessageIdentity.Key(echo), 1000); }
                continue;
            }
            drawn.Add(item);
        }
        // A send still on its way is drawn after everything sent before it. Its time is this PC's moment of sending, and a
        // message sent just before it takes the service's time when confirmed, which can be later: photos sent one at a
        // time were confirmed one by one with times after the words written with them, and the words jumped above them.
        // What was sent after it does not count, though it may be confirmed first; each keeps its order among the others.
        var sent = drawn.Count == 0 ? [] : rows.Values.Where(m => m.IsOutgoing)
            .Select(m => (m.Timestamp, From: sentAt.TryGetValue(MessageIdentity.Key(m), out var made) ? made : (DateTimeOffset?)null)).ToArray();
        var after = DateTimeOffset.MinValue;
        foreach (var item in drawn)
        {
            foreach (var (time, from) in sent) if (time > after && (from is null || from < item.CreatedAt)) after = time;
            var at = item.CreatedAt > after ? item.CreatedAt : after.AddTicks(1);
            after = at;
            rows[MessageIdentity.Key(item.ChatId, item.LocalId)] = Row(item, at, bubbleFamily(item.ChatId), canRetry?.Invoke(item) == true);
        }
        var ordered = rows.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal);
        // Scheduled messages come after everything sent, soonest first, whatever their time. One the outbox has taken is
        // already drawn above as a send, under the same id.
        ChatMessage[] waiting = [.. scheduled
            .Where(item => !rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.Id)))
            .OrderBy(item => item.Due).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => ScheduledSendRows.Row(item, bubbleFamily(item.ChatId)))];
        return waiting.Length == 0 ? ordered : ordered.Concat(waiting);
    }

    // The service's copy of a text send with no receipt (#91): the account's own message with the same words in the same
    // conversation, timed from EchoBefore before the send to EchoAfter after it, not one the conversation held when Send was
    // pressed, and not one an earlier send took.
    private ChatMessage? EchoOf(OutgoingMessage item, IReadOnlyList<ChatMessage> history, HashSet<string> claimed)
    {
        var held = known.TryGetValue(item.LocalId, out var seen) && seen.At == item.CreatedAt ? seen.Rows : null;
        return history.FirstOrDefault(m => m.ChatId == item.ChatId && m.IsOutgoing && m.Text == item.Text &&
            m.Timestamp >= item.CreatedAt - EchoBefore && m.Timestamp <= item.CreatedAt + EchoAfter &&
            !claimed.Contains(MessageIdentity.Key(m)) && held?.Contains(MessageIdentity.Key(m)) != true &&
            // Taken by another send already, or confirmed as one (its time noted when it was).
            (taken.TryGetValue(MessageIdentity.Key(m), out var by) ? by == item.LocalId : !sentAt.ContainsKey(MessageIdentity.Key(m))));
    }

    // A send's row. A captioned photo is drawn as it will be confirmed: the services that take captions (Messenger, Google
    // Voice) return the caption as the message's text, so it sits in a bubble above the photo. Drawn under the photo until
    // then, the caption jumped above it and the row grew the moment the send was confirmed.
    private static ChatMessage Row(OutgoingMessage item, DateTimeOffset at, string? bubbleFamily, bool canRetry) =>
        new(item.LocalId, item.ChatId, "local:self", "You", at, item.LocalId,
            item.Attachments is [{ Caption: { Length: > 0 } caption }] ? caption : item.Attachments is { Count: > 0 } ? null : item.Text,
            item.Attachments is { Count: > 0 } ? "media" : "text",
            true, false, false, null, item.ReplyToId, item.Attachments ?? [], [],
            new(null, null, null, null, new Dictionary<string, bool?>()) { Local = StateOf(item, canRetry) },
            bubbleFamily);

    // The order each map's keys came in, oldest first, for its cap. A Dictionary's own order is not that once anything has
    // been removed: it fills freed slots last in, first out, so Keys.First() was most often the entry just added, and a full
    // map kept its oldest entries and let every new one go.
    internal static void Keep<TValue>(Dictionary<string, TValue> map, Queue<string> order, string key, int cap)
    {
        order.Enqueue(key);
        while (map.Count > cap && order.TryDequeue(out var oldest))
            // A key given again since is let go at its later place.
            if (!order.Contains(oldest)) map.Remove(oldest);
        // Keys removed by other means leave their places behind; dropped once there are too many.
        if (order.Count > cap * 2)
        {
            var live = order.Reverse().Where(map.ContainsKey).Distinct(StringComparer.Ordinal).Reverse().ToArray();
            order.Clear();
            foreach (var kept in live) order.Enqueue(kept);
        }
    }

    // ---- What is made here and shown before its service makes it ----

    // How long a reaction shows before the service confirms it. The sample preview, with no service to confirm one, holds
    // its predictions for as long as it runs (ShellNavigationProbe).
    public static TimeSpan ReactionPredictionTimeout { get; set; } = TimeSpan.FromMinutes(1);
    // How long an edit or a delete shows before the service's copy confirms it; after that the conversation draws the service's copy again.
    public static TimeSpan ChangePredictionTimeout { get; set; } = TimeSpan.FromMinutes(1);
    private sealed record PendingReaction(string Key, bool Remove, IReadOnlyList<MessageReaction> Before, DateTimeOffset At);
    private sealed record PendingChange(string? Text, bool? Everyone, string? BeforeText, bool BeforeDeleted, DateTimeOffset At);
    private readonly Dictionary<string, PendingReaction> pendingReactions = [];
    private readonly Dictionary<string, PendingChange> pendingChanges = [];

    /// <summary>A reaction made here, shown while Google confirms it. <paramref name="actual"/> is the message as the conversation holds it. Returns when it was made, for <see cref="ExpireReaction"/>.</summary>
    public DateTimeOffset ShowReaction(ChatMessage actual, string key, bool remove)
    {
        var at = DateTimeOffset.UtcNow;
        pendingReactions[MessageIdentity.Key(actual)] = new(key, remove, actual.Reactions, at);
        return at;
    }

    /// <summary>The reaction shown for any of these rows goes. True when one did.</summary>
    public bool ClearReaction(IEnumerable<string> keys)
    {
        var changed = false;
        foreach (var key in keys) changed |= pendingReactions.Remove(key);
        return changed;
    }

    /// <summary>A minute has passed: the reaction made at <paramref name="at"/> goes if it is still the one shown. True when it went.</summary>
    public bool ExpireReaction(string key, DateTimeOffset at) => pendingReactions.TryGetValue(key, out var pending) && pending.At == at && pendingReactions.Remove(key);

    /// <summary>
    /// An edit (<paramref name="text"/>) or a delete (<paramref name="everyone"/>) made here, shown at once while the service makes it. A delete for this account
    /// takes the message away; one for everyone draws it as deleted. Returns when it was made, for <see cref="ExpireChange"/>.
    /// </summary>
    public DateTimeOffset ShowChange(ChatMessage actual, string? text, bool? everyone)
    {
        var at = DateTimeOffset.UtcNow;
        pendingChanges[MessageIdentity.Key(actual)] = new(text, everyone, actual.Text, actual.IsDeleted, at);
        return at;
    }

    /// <summary>The change shown for this row goes (refused). True when one did.</summary>
    public bool ClearChange(string key) => pendingChanges.Remove(key);

    /// <summary>A minute has passed: the change made at <paramref name="at"/> goes if it is still the one shown. True when it went.</summary>
    public bool ExpireChange(string key, DateTimeOffset at) => pendingChanges.TryGetValue(key, out var pending) && pending.At == at && pendingChanges.Remove(key);

    /// <summary>
    /// A row as it shows with what was made here and not confirmed yet; null for one deleted here for this account alone, which is
    /// not drawn while the service removes it. A prediction ends when the service's copy changes from what it was, or after its time.
    /// </summary>
    public ChatMessage? Overlay(ChatMessage message, Func<string, IReadOnlyList<string>> self) => WithReaction(WithChange(message), self);

    /// <summary>A row with only the reaction shown for it (link previews and the like, which no edit changes).</summary>
    public ChatMessage OverlayReaction(ChatMessage message, Func<string, IReadOnlyList<string>> self) => WithReaction(message, self)!;

    private ChatMessage? WithChange(ChatMessage message)
    {
        if (pendingChanges.Count == 0 || !pendingChanges.TryGetValue(MessageIdentity.Key(message), out var pending)) return message;
        if (message.Text != pending.BeforeText || message.IsDeleted != pending.BeforeDeleted || DateTimeOffset.UtcNow - pending.At > ChangePredictionTimeout)
        {
            pendingChanges.Remove(MessageIdentity.Key(message));
            return message;
        }
        return pending switch
        {
            { Text: { } text } => message with { Text = text, EditedAt = message.EditedAt ?? pending.At },
            { Everyone: true } => message with { IsDeleted = true },
            _ => null,
        };
    }

    private ChatMessage? WithReaction(ChatMessage? message, Func<string, IReadOnlyList<string>> self)
    {
        if (message is null || !pendingReactions.TryGetValue(MessageIdentity.Key(message), out var pending)) return message;
        if (!message.Reactions.SequenceEqual(pending.Before) || DateTimeOffset.UtcNow - pending.At > ReactionPredictionTimeout)
        {
            pendingReactions.Remove(MessageIdentity.Key(message));
            return message;
        }
        return message with { Reactions = ReactionPrediction.Apply(message.Reactions, pending.Key, pending.Remove, self(message.ChatId)) };
    }
}
