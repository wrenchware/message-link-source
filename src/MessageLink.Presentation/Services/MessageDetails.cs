using MessageLink.Core.Messaging;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

public sealed record MessageDetail(string Name, string Value);
public sealed record MessageDetails(IReadOnlyList<MessageDetail> Fields, IReadOnlyList<MessageDetail> Technical)
{
    // Describe the selected source message, including an individual photo in a grouped set.
    // No lookups, network requests, media downloads, URLs or session material belong here.
    public static MessageDetails Create(ChatMessage message, ChatSummary source, string? accountName = null)
    {
        if (message.ChatId != source.Id) throw new ArgumentException("Message details require the original conversation.");
        var row = new MessageItemViewModel(message, null, source, new Dictionary<string, ChatMessage>());
        var fields = new List<MessageDetail>();
        var technical = new List<MessageDetail>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(name, value)); }
        void Raw(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) technical.Add(new(name, value)); }
        static string When(DateTimeOffset value) => value.ToLocalTime().ToString("F") + " (UTC" + value.ToLocalTime().ToString("zzz") + ")";

        var account = accountName?.Trim();
        Add("Account / service", source.Network + (!string.IsNullOrEmpty(account) &&
            !string.Equals(account, source.Network.Trim(), StringComparison.OrdinalIgnoreCase) ? " · " + account : ""));
        Add("Sender", row.Sender);
        Add("Conversation", source.Title);
        Add("Sent", When(message.Timestamp));
        Add("Status", message.IsDeleted ? "Deleted" : message.IsHidden ? "Unavailable" :
            row.Status.Length > 0 ? row.Status : !message.IsOutgoing && message.Type != "system" ? "Received" : null);
        if (!PendingRows.IsLocal(message)) Add("Sent as", message.Transport);
        // This message's own answer where the network marks each one (Google Messages). Unmarked is not "not encrypted",
        // except by SMS or MMS, which never are.
        Add("Encryption", message.IsEncrypted == true ? "End-to-end encrypted"
            : message.Transport is "SMS" or "MMS" or "SMS/MMS" ? "Not end-to-end encrypted" : null);
        if (message.EditedAt is { } edited) Add("Edited", When(edited));
        if (message.Delivery.SeenAt is { } read && row.ReceiptStage == "read") Add("Read", When(read));
        if (message.Delivery.DeliveredParticipantCount is > 0) Add("Delivered to", message.Delivery.DeliveredParticipantCount + " participant(s)");
        // Everyone who has read it, not only those whose face sits here: a face marks where somebody stopped, so the
        // faces under one message leave out everyone who has read further.
        Add("Read by", ReceiptRules.ReaderNames(message, source));
        if (source.IsEncrypted is { } encrypted) Add("Conversation encryption", encrypted ? "End-to-end encrypted" : "Not end-to-end encrypted");
        Add("Attachments", message.Attachments.Count == 0 ? "None" : message.Attachments.Count.ToString());
        for (var i = 0; i < message.Attachments.Count; i++)
        {
            var attachment = message.Attachments[i];
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(attachment.Name)) parts.Add(attachment.Name);
            if (!string.IsNullOrWhiteSpace(attachment.MimeType)) parts.Add(attachment.MimeType);
            else if (!string.IsNullOrWhiteSpace(attachment.Type)) parts.Add(attachment.Type);
            if (attachment.Size is >= 0) parts.Add($"{attachment.Size:N0} bytes");
            if (attachment.Width is > 0 && attachment.Height is > 0) parts.Add($"{attachment.Width:0} × {attachment.Height:0}");
            if (attachment.DurationSeconds is >= 0) parts.Add($"{attachment.DurationSeconds:0.#} seconds");
            if (!string.IsNullOrWhiteSpace(attachment.Availability)) parts.Add(attachment.Availability);
            Add($"Attachment {i + 1}", string.Join(" · ", parts));
        }
        Raw("Message ID", message.Id);
        Raw("Conversation ID", message.ChatId);
        Raw("Account ID", source.AccountId);
        Raw("Sender ID", message.SenderId);
        Raw("Message type", message.Type);
        Raw("Send state", message.Delivery.SendState);
        Raw("Reply to ID", message.ReplyToId);
        Raw("Call type", message.CallKind);
        return new(fields, technical);
    }
}
