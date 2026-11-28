using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class ConversationReading
{
    public static bool ShowJumpToLatest(bool isAwayFromBottom, bool followLatest, bool hasConversation)
        => isAwayFromBottom && !followLatest && hasConversation;

    public static bool ContinuesGroup(ChatMessage message, ChatMessage? previous) => previous is not null &&
        message.ChatId == previous.ChatId &&
        message.IsOutgoing == previous.IsOutgoing && (message.IsOutgoing || !string.IsNullOrEmpty(message.SenderId) && message.SenderId == previous.SenderId) &&
        message.Timestamp.LocalDateTime.Date == previous.Timestamp.LocalDateTime.Date &&
        message.Timestamp >= previous.Timestamp && message.Timestamp - previous.Timestamp <= TimeSpan.FromMinutes(5) &&
        !message.IsDeleted && !previous.IsDeleted && !message.IsHidden && !previous.IsHidden && message.ReplyToId is null;

    public static (double Width, double Height) MediaSize(double? width, double? height)
    {
        if (width is not > 0 || height is not > 0 || !double.IsFinite(width.Value) || !double.IsFinite(height.Value) || width * height > 80_000_000)
            return (320, 240);
        var scale = Math.Min(1, Math.Min(420 / width.Value, 320 / height.Value));
        return (Math.Max(1, width.Value * scale), Math.Max(1, height.Value * scale));
    }
}

// Counts messages received while reading above the bottom, not server-wide unread receipts.
public sealed class NewMessageTracker
{
    private readonly HashSet<string> known = [];
    private readonly HashSet<string> unseen = [];
    private bool initialized;
    public int Count => unseen.Count;
    public void Reset() { known.Clear(); unseen.Clear(); initialized = false; }
    public void MarkViewed() => unseen.Clear();
    public void Update(IEnumerable<ChatMessage> messages, bool live, bool away)
    {
        var snapshot = messages.ToArray();
        foreach (var message in snapshot)
        {
            if (known.Add(MessageIdentity.Key(message)) && initialized && live && away && !message.IsOutgoing && !message.IsDeleted && !message.IsHidden)
                unseen.Add(MessageIdentity.Key(message));
            if (message.IsDeleted || message.IsHidden) unseen.Remove(MessageIdentity.Key(message));
        }
        unseen.IntersectWith(snapshot.Select(MessageIdentity.Key));
        initialized = true;
        if (!away) MarkViewed();
    }
}
