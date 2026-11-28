using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Where "New messages" goes: above the oldest message from someone else that came after this account's read watermark,
/// as the conversation stood when it was opened. The facts are fixed at the open, per source conversation (each linked
/// service of a merged one has its own): the watermark, and the newest activity then. So a message arriving afterwards
/// never moves the line or draws one, and a history that fills in later (a saved copy first, then the service's page)
/// places it again from the same facts. Every service's unread count is only a flag, so only a service that says where
/// reading stopped gets a line — Messenger and Instagram; Google Messages and Google Voice do not say, and a line placed
/// by guessing would claim messages were unread that were not.
/// </summary>
public static class UnreadDivider
{
    /// <summary>What was unread in one source conversation when it was opened.</summary>
    public readonly record struct Opened(DateTimeOffset ReadUpTo, DateTimeOffset? NewestAt);

    /// <summary>The unread facts of the conversation being opened: its sources that were unread and say how far they were read.</summary>
    public static IReadOnlyDictionary<string, Opened> AtOpen(IEnumerable<ChatSummary> sources)
    {
        var opened = new Dictionary<string, Opened>();
        foreach (var chat in sources)
            if (chat.UnreadCount > 0 && !chat.IsSpam && chat.ReadUpTo is { } read) opened.TryAdd(chat.Id, new(read, chat.LastActivity));
        return opened;
    }

    /// <summary>
    /// The identity (MessageIdentity.Key) of the message the line goes above, or null for no line. When the loaded history
    /// does not reach back to the watermark, the oldest loaded message after it. Gone once you have sent something after
    /// the open, as a reply says you have read them.
    /// </summary>
    public static string? Anchor(IReadOnlyList<ChatMessage> visible, IReadOnlyDictionary<string, Opened> opened, DateTimeOffset openedAt)
    {
        if (opened.Count == 0) return null;
        for (var index = 0; index < visible.Count; index++)
        {
            var message = visible[index];
            if (!opened.TryGetValue(message.ChatId, out var facts) || !Counts(message)) continue;
            if (message.Timestamp <= facts.ReadUpTo || facts.NewestAt is { } newest && message.Timestamp > newest + Slack) continue;
            for (var after = index + 1; after < visible.Count; after++)
                if (visible[after].IsOutgoing && visible[after].Timestamp > openedAt) return null;
            return MessageIdentity.Key(message);
        }
        return null;
    }

    // A summary's newest activity and its newest message's time can come from different places; a message this close
    // after it was there at the open.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    // What makes a conversation unread: a message from someone else, not a service's own note.
    private static bool Counts(ChatMessage message) => !message.IsOutgoing && message.Type != "system" && !message.IsHidden;
}
