using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// One set of read-receipt rules for every service. A provider says what it knows — who a message reached, who has
// read it, where each reader stopped — and these decide what the conversation claims from that, the same way whichever
// network the message travelled on. Nothing here is inferred beyond what the data says: reading a message means it
// arrived, so a reader counts as a delivery, but nothing ever counts as a reader.
public static class ReceiptRules
{
    // A network that says outright it has no receipts (Google Voice) shows Sent and nothing after it. Unknown is not no:
    // a provider that has not said still shows whatever receipts its messages carry.
    public static bool HasReceipts(ChatSummary chat) => chat.Capabilities.ReadReceipts != false;

    // Builds before these rules declared Google Messages to have no receipts, and nothing read it. Google now declares
    // them or leaves them unknown, never absent, so a saved Google conversation that says absent is from then and is
    // read as unknown. Otherwise the saved copy would say Delivered until the network's summary said Read.
    public static ChatSummary FromSaved(ChatSummary chat) =>
        chat.Capabilities.ReadReceipts == false && chat.Id.StartsWith("google-direct:", StringComparison.Ordinal)
            ? chat with { Capabilities = chat.Capabilities with { ReadReceipts = null } } : chat;

    public static bool IsGroup(ChatSummary chat) => chat.Type == "group";

    // Everyone the provider says has read this message, apart from whoever sent it and this account.
    public static IReadOnlyList<string> Readers(ChatMessage message, ChatSummary chat)
    {
        if (!HasReceipts(chat) || message.Delivery.SeenBy.Count == 0) return [];
        return message.Delivery.SeenBy.Where(reader => reader.Value == true && IsOther(reader.Key, message, chat))
            .Select(reader => reader.Key).Distinct(StringComparer.Ordinal).ToArray();
    }

    // One-to-one: the other person has read it. A group: every other member has, by name — and only when the member list
    // is known in full. No participants, or fewer than the conversation says it has, never stands in for the whole.
    // These run for every row of every render, so they walk the lists rather than build sets from them.
    public static bool IsRead(ChatMessage message, ChatSummary chat)
    {
        if (!message.IsOutgoing || message.IsDeleted || !HasReceipts(chat)) return false;
        if (!IsGroup(chat)) return message.Delivery.Seen == true || HasReader(message, chat);
        if (chat.Participants.Count == 0 || chat.ParticipantCount > chat.Participants.Count) return false;
        var others = 0;
        foreach (var person in chat.Participants)
        {
            if (person.IsSelf == true || person.Id == message.SenderId) continue;
            others++;
            if (!message.Delivery.SeenBy.TryGetValue(person.Id, out var seen) || seen != true) return false;
        }
        return others > 0;
    }

    // Delivered to somebody. In a group a reader, or the network's own "seen" flag, says at least that much.
    public static bool IsDelivered(ChatMessage message, ChatSummary chat)
    {
        if (!message.IsOutgoing || message.IsDeleted) return false;
        if (message.Delivery.DeliveredParticipantCount is > 0) return true;
        return HasReceipts(chat) && (message.Delivery.Seen == true || HasReader(message, chat));
    }

    private static bool HasReader(ChatMessage message, ChatSummary chat)
    {
        if (message.Delivery.SeenBy.Count == 0) return false;
        foreach (var (reader, seen) in message.Delivery.SeenBy)
            if (seen == true && IsOther(reader, message, chat)) return true;
        return false;
    }

    private static bool IsOther(string id, ChatMessage message, ChatSummary chat)
    {
        if (id == message.SenderId) return false;
        foreach (var person in chat.Participants)
            if (person.IsSelf == true && person.Id == id) return false;
        return true;
    }

    // The readers in words, for Message details: participants by name in the conversation's own order, and anyone the
    // conversation no longer lists counted rather than named.
    public static string ReaderNames(ChatMessage message, ChatSummary chat)
    {
        var readers = Readers(message, chat);
        if (readers.Count == 0) return "";
        var known = chat.Participants.Where(person => readers.Contains(person.Id, StringComparer.Ordinal))
            .DistinctBy(person => person.Id).Select(person => person.DisplayName).ToList();
        var unknown = readers.Count - known.Count;
        if (unknown > 0) known.Add(unknown == 1 ? "1 other" : $"{unknown} others");
        return string.Join(", ", known);
    }

    // Where each reader's face goes when a provider says who has read each message but not where each of them stopped:
    // the newest message, within what is loaded, that names them as a reader. A conversation any of whose messages
    // carries a mark keeps the provider's own marks untouched. Returns the same array when nothing was derived.
    public static ChatMessage[] DeriveReadMarks(ChatMessage[] messages)
    {
        Dictionary<string, Dictionary<string, int>>? newest = null;
        HashSet<string>? marked = null;
        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            if (message.Delivery.ReadMark is not null) (marked ??= new(StringComparer.Ordinal)).Add(message.ChatId);
            if (message.Delivery.SeenBy.Count == 0 || message.IsDeleted || message.IsHidden || message.Type == "system" ||
                marked?.Contains(message.ChatId) == true) continue;
            newest ??= new(StringComparer.Ordinal);
            if (!newest.TryGetValue(message.ChatId, out var readers)) newest[message.ChatId] = readers = new(StringComparer.Ordinal);
            // Oldest first, so a later message naming the same reader replaces an earlier one.
            foreach (var (reader, seen) in message.Delivery.SeenBy)
                if (seen == true && reader != message.SenderId) readers[reader] = index;
        }
        if (newest is null) return messages;
        var marks = new Dictionary<int, List<string>>();
        foreach (var (chat, readers) in newest)
        {
            if (marked?.Contains(chat) == true) continue;
            foreach (var (reader, index) in readers)
                (marks.TryGetValue(index, out var list) ? list : marks[index] = []).Add(reader);
        }
        if (marks.Count == 0) return messages;
        var derived = messages.ToArray();
        foreach (var (index, readers) in marks)
            derived[index] = derived[index] with { Delivery = derived[index].Delivery with { ReadMark = [.. readers.Order(StringComparer.Ordinal)] } };
        return derived;
    }
}
