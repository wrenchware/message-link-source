using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class MetaAiMessages
{
    // Provider identity used by pinned mautrix-meta's connector/userinfo.go; never infer a bot from its name.
    public const string SenderId = "fb:156025504001094";
    public const string UnavailableReply = "Meta AI reply — open in Messenger";

    public static string? Text(ChatMessage message) =>
        message.ChatId.StartsWith("messenger:", StringComparison.Ordinal) && message.SenderId == SenderId &&
        !message.IsOutgoing && !message.IsDeleted && !message.IsHidden &&
        string.IsNullOrWhiteSpace(message.Text) && message.Attachments.Count == 0
            ? UnavailableReply : message.Text is null ? null : MessageText.Markup(message.ChatId, message.Text);
}
