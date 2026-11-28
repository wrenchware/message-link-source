using MessageLink.Core.Messaging;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

/// <summary>What Up in an empty composer would edit, and a fixed word saying why nothing when there is nothing.</summary>
public sealed record EditTargetChoice(ChatMessage? Message, string Reason);

/// <summary>
/// Up in an empty composer edits your newest message in the composer, where the service can edit one: Messenger and Instagram.
/// Only the newest outgoing message on the send route is ever considered. When that one cannot be edited the answer is
/// nothing, never an older message — editing something further up by surprise is worse than doing nothing.
/// </summary>
/// <remarks>
/// A message known only from its send receipt, or still local, is never a target: Instagram answers the options lookup
/// from memory for a message it has shown on a history page, but for one it has not it reads the thread again, and a key
/// press must not cost a request on a real account. Pure: reads the rows it is given and nothing else.
/// </remarks>
public static class EditTarget
{
    public const string NoMessage = "no_message", NotEditable = "not_editable", NotConfirmed = "not_confirmed";
    private static readonly string[] EditableNetworks = ["Messenger", "Instagram"];

    /// <summary>
    /// The <c>fromProviderPage</c> rule a conversation view passes: the message is on the current snapshot, and for
    /// Instagram the service still holds it in its own memory. The snapshot alone is not enough there: a cold open keeps
    /// rows from the previous run's saved copy that are older than the fresh page, and Instagram's memory of the messages
    /// it has read is bounded, so a row can be on screen while Instagram's options lookup for it would read the thread.
    /// <paramref name="instagramHolds"/> must answer from memory only, never with a request.
    /// </summary>
    public static Func<ChatMessage, bool> ProviderHolds(Func<ChatMessage, bool> onSnapshot, Func<ChatMessage, bool> instagramHolds) =>
        message => onSnapshot(message) && (!message.ChatId.StartsWith("instagram:", StringComparison.Ordinal) || instagramHolds(message));

    public static ChatMessage? Newest(IReadOnlyList<MessageItemViewModel> rows, string? sendChatId, Func<ChatMessage, bool> fromProviderPage) =>
        Choose(rows, sendChatId, fromProviderPage).Message;

    public static EditTargetChoice Choose(IReadOnlyList<MessageItemViewModel> rows, string? sendChatId, Func<ChatMessage, bool> fromProviderPage)
    {
        if (sendChatId is null) return new(null, NoMessage);
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index];
            var message = row.Message;
            // A scheduled message is not sent yet: it has Edit of its own, and the newest sent message is still Up's.
            if (message.ChatId != sendChatId || !message.IsOutgoing || message.IsHidden || message.Type == "system" || ScheduledSendRows.Is(message)) continue;
            // The newest outgoing message decides; nothing below it is looked at.
            if (message.IsDeleted) return new(null, NoMessage);
            if (!EditableNetworks.Contains(row.SourceNetwork, StringComparer.Ordinal)) return new(null, NotEditable);
            if (message.Attachments.Count > 0 || string.IsNullOrWhiteSpace(MessageText.PlainText(message.Text ?? ""))) return new(null, NotEditable);
            if (message.Id.StartsWith("local:", StringComparison.Ordinal) || message.Delivery.Local is not null || message.Delivery.SendState is not (null or "SUCCESS") ||
                !fromProviderPage(message)) return new(null, NotConfirmed);
            return new(message, "");
        }
        return new(null, NoMessage);
    }
}
