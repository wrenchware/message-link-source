using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed record ReactionChip(string Emoji, string People);

/// <summary>One person's small face, shown beside the newest message they have read.</summary>
public sealed record ReadFace(string ParticipantId, string? AvatarUrl, string Name, string? AvatarOverrideId = null);

public sealed class MessageItemViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public ChatMessage Message { get; private set; }
    // Exact outbox confirmation only; ordinary history updates cannot assert this link.
    public string? ConfirmedLocalId { get; private set; }
    public string SourceNetwork { get; private set; }
    public ChatSummary SourceChat { get; private set; }
    public bool IsMergedSource { get; private set; }
    public string Sender { get; private set; }
    public bool IsOutgoing => Message.IsOutgoing;
    public bool IsIncoming => !IsOutgoing;
    // A conversation event from the phone (for example switching between SMS and RCS), shown as a centered note.
    public bool IsSystem => Message.Type == "system";
    public string Body => Message.IsDeleted ? "Message deleted" : NativeBeeper.Presentation.Services.MetaAiMessages.Text(Message) ?? (Message.Attachments.Count == 0 ? $"[{Message.Type ?? "Message"}]" : "");
    // Whether the bubble draws the body: a body that is only the address of the reel card under it is left to the card
    // (SharedLinkPresentation.CardReplacesText). The body itself is unchanged for copying, search and the list preview.
    public bool ShowsBody => Body.Length > 0 &&
        (Message.IsDeleted || Message.IsHidden || !SharedLinkPresentation.CardReplacesText(Message.Text, Message.Attachments));
    // A service's own note arrives as markup: Google's "Link previews are off.<br />Learn more or turn on in <u>Settings</u>."
    // showed its tags. Read as text, the way a bubble reads its body, without the links a note has no use for.
    public string SystemBody => IsSystem ? EncryptionLineText(NativeBeeper.Presentation.Services.MessageText.PlainText(Body)) : "";
    // The line Google adds when a conversation becomes end-to-end encrypted. Its text is the phone's, the same as the
    // line before it ("RCS chat with …" twice, 2026-10-01); Messages for Web draws it as a lock and "Private chat with …".
    public bool IsEncryptionLine => IsSystem && Message.EncryptionSwitch == true;
    private string EncryptionLineText(string text) =>
        IsEncryptionLine && text.StartsWith("RCS chat with ", StringComparison.Ordinal) ? "Private chat with " + text["RCS chat with ".Length..] : text;
    public string Time => Message.Timestamp.ToLocalTime().ToString("t");
    // Reported route in text too, so transport is never conveyed by bubble color alone.
    public string FullTime => Message.Timestamp.ToLocalTime().ToString("f") +
        (IsMergedSource ? " · " + SourceNetwork : "") +
        (Message.Transport is { Length: > 0 } transport && Message.Delivery.SendState?.StartsWith("LOCAL_", StringComparison.Ordinal) != true ? " · " + transport : "");
    public bool ShowDate { get; private set; }
    private DateTimeOffset headerTimestamp;
    public string DateLabel => headerTimestamp.LocalDateTime.Date == DateTime.Today ? "Today" : headerTimestamp.ToLocalTime().ToString("D");
    // Centered divider on a new day; within a day, group headers carry the time.
    public bool ShowDivider => ShowDate;
    public string DividerText => DateLabel;
    // "New messages" above the first message that was unread when the conversation was opened (ConversationViewModel.UnreadDivider).
    public bool ShowUnreadDivider { get; private set; }
    internal void SetUnreadDivider(bool value) => ShowUnreadDivider = value;
    // Teams-style line above a message: the sender's name in a group chat when the sender changes, and the time only after
    // a gap of TimeGap or on a new day. A time over every turn read as clutter, five in three minutes (owner, 2026-10-29);
    // each message's own time stays on hover.
    public bool ShowHeader { get; private set; }
    public bool ShowTime { get; private set; }
    public string HeaderTime => headerTimestamp.ToLocalTime().ToString("t");
    public static readonly TimeSpan TimeGap = TimeSpan.FromMinutes(10);
    public string HeaderName { get; private set; }
    public bool HasHeaderName => HeaderName.Length > 0;
    public string SystemContext => IsSystem ? IsMergedSource ? Time : HasHeaderName ? HeaderName + " · " + Time : "" : "";
    public bool HasSystemContext => SystemContext.Length > 0;
    // A button on a service's own line (Messenger's "View buyer profile"); only an https address is ever opened.
    public Uri? SystemActionUri => IsSystem && Message.Action is { } action && Uri.TryCreate(action.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    public string SystemActionTitle => SystemActionUri is null ? "" : Message.Action!.Title;
    public bool HasSystemAction => SystemActionUri is not null;
    public bool ShowSender { get; private set; }
    public bool ContinuesGroup { get; private set; }
    public IReadOnlyList<ChatMessage> PhotoGroupMessages { get; private set; } = [];
    public bool IsPhotoGroupMember { get; private set; }
    // What the message toolbar and reaction picker act on. Messenger's own client treats a photo set as its first
    // photo: a reaction added to the set lands there. Right-clicking a single photo still acts on that photo.
    public ChatMessage ActionMessage => PhotoGroupMessages.Count > 1 ? PhotoGroupMessages[0] : Message;
    internal void SetPhotoGroup(IReadOnlyList<ChatMessage> messages, bool hidden, MessageItemViewModel first,
        IReadOnlyList<MessageItemViewModel>? members = null)
    {
        PhotoGroupMessages = messages; IsPhotoGroupMember = hidden;
        if (hidden) return;
        ShowDate = first.ShowDate; ShowHeader = first.ShowHeader; ShowTime = first.ShowTime; ContinuesGroup = first.ContinuesGroup;
        if (members is null) return;
        // A set is one picture on screen, as Messenger's phone client shows it: every photo's reactions and every
        // reader's face appear under the set rather than on hidden member rows.
        ReactionChips = members.SelectMany(member => member.ReactionChips).GroupBy(chip => chip.Emoji)
            .Select(chips => new ReactionChip(chips.Key, string.Join(", ",
                chips.SelectMany(chip => chip.People.Split(", ", StringSplitOptions.RemoveEmptyEntries)).Distinct()))).ToArray();
        Reactions = string.Join("   ", ReactionChips.Select(chip => chip.Emoji));
        ReadBy = members.SelectMany(member => member.ReadBy).Reverse().DistinctBy(face => face.ParticipantId).Reverse().ToArray();
    }
    public string Reply { get; private set; }
    public string ReplyAuthor { get; private set; }
    public bool HasReply => Reply.Length > 0;
    public string Attachments { get; private set; }
    public bool HasAttachments => Attachments.Length > 0;
    public string Reactions { get; private set; }
    public IReadOnlyList<ReactionChip> ReactionChips { get; private set; }
    public bool HasReactions => Reactions.Length > 0;
    // Words for the delivery state (tooltip, screen readers, and the text-receipt setting).
    public string Status { get; private set; }
    public bool HasStatus => Status.Length > 0;
    // Words shown beside the receipt icon too, for a state the icon alone would leave looking like an ordinary send.
    public string AttentionText => Message.IsDeleted ? "" : IsScheduled ? ScheduledSendRows.Describe(Message.Delivery.SendState, Message.Timestamp, DateTimeOffset.Now)
        : Message.Delivery.SendState switch
    {
        "LOCAL_UNCONFIRMED" => "Not confirmed yet",
        "LOCAL_UNKNOWN" => "May not have sent",
        _ => ""
    };
    // Send later: a message kept on this PC for its time (or one whose time passed unsent), with Send now, Edit and Cancel.
    public bool IsScheduled => ScheduledSendRows.Is(Message);
    // Receipt icon stage: sending, sent, delivered, read, failed or unknown; empty for incoming or deleted messages.
    public string ReceiptStage { get; private set; }
    public bool IsEdited => Message.EditedAt is not null && !Message.IsDeleted;
    // Undo send's wait (Outbox.Hold): the message has not reached the provider, and its row offers Undo.
    public bool IsHeld => Message.Delivery.SendState == "LOCAL_HELD";
    // The latest sent message, plus any message still sending, failed, uncertain or edited.
    public bool ShowReceipt { get; private set; }
    public bool IsLargeEmoji { get; private set; }
    public string? SenderAvatarUrl { get; private set; }
    // The people whose reading has reached this message and no further: a row of small faces under the last message
    // each person has seen, for any service whose messages say where each reader stopped. Group conversations only —
    // with one other person the receipt already says it in words, and a face beside it says the same thing twice. A
    // merged conversation is always one person, so it never draws them either.
    public IReadOnlyList<ReadFace> ReadBy { get; private set; } = [];
    public bool HasReadBy => ReadBy.Count > 0;
    // A small lock beside the receipt under the newest message, when the conversation is known to be end-to-end
    // encrypted and that message went the encrypted way — where Google Messages shows it. Never shown on a guess.
    public bool ShowLock { get; private set; }
    // The same thing in words, for the tooltip and for anyone reading the conversation with a screen reader.
    public string ReadByText => ReadBy.Count == 0 ? "" : "Read by " + string.Join(", ", ReadBy.Select(person => person.Name));

    // Set for a message that arrived live at the end of the conversation; the view plays its entrance once.
    public bool AnimateEntrance { get; set; }
    // Its link's page was not fetched by itself (a stranger's link, or private reading on; ConversationViewModel.LinkPreviews),
    // so the row offers Load preview where the card would be. Set by the conversation after each render, not by UpdateFrom.
    public bool PreviewHeld { get; private set; }
    internal void SetPreviewHeld(bool value)
    {
        if (PreviewHeld == value) return;
        PreviewHeld = value;
        OnPropertyChanged(nameof(PreviewHeld));
    }
    // Incremented only when a provider becomes ready after an initial failure. The bubble uses it to replace failed
    // media controls once; ordinary receipt/reaction updates keep the already-decoded image in place.
    public int MediaReloadVersion { get; private set; }
    internal void SetMediaReloadVersion(int value) => MediaReloadVersion = value;
    internal void ConfirmFrom(MessageItemViewModel value)
    {
        ConfirmedLocalId ??= Message.Id;
        UpdateFrom(value);
    }
    public void UpdateFrom(MessageItemViewModel value)
    {
        Message = value.Message; Sender = value.Sender; ShowDate = value.ShowDate; ShowUnreadDivider = value.ShowUnreadDivider;
        headerTimestamp = value.headerTimestamp;
        SourceNetwork = value.SourceNetwork;
        SourceChat = value.SourceChat; IsMergedSource = value.IsMergedSource;
        ShowSender = value.ShowSender; Reply = value.Reply; Attachments = value.Attachments;
        ReplyAuthor = value.ReplyAuthor;
        ContinuesGroup = value.ContinuesGroup;
        PhotoGroupMessages = value.PhotoGroupMessages; IsPhotoGroupMember = value.IsPhotoGroupMember;
        Reactions = value.Reactions; Status = value.Status; IsLargeEmoji = value.IsLargeEmoji;
        ReactionChips = value.ReactionChips;
        SenderAvatarUrl = value.SenderAvatarUrl; ReadBy = value.ReadBy; ShowLock = value.ShowLock;
        MediaReloadVersion = value.MediaReloadVersion;
        ShowHeader = value.ShowHeader; ShowTime = value.ShowTime; HeaderName = value.HeaderName; ReceiptStage = value.ReceiptStage; ShowReceipt = value.ShowReceipt;
        OnPropertyChanged(string.Empty);
    }

    public bool HasSameContent(MessageItemViewModel other) =>
        (Message with { Attachments = other.Message.Attachments, Reactions = other.Message.Reactions, Delivery = other.Message.Delivery }) == other.Message &&
        Message.Attachments.SequenceEqual(other.Message.Attachments) && Message.Reactions.SequenceEqual(other.Message.Reactions) &&
        (Message.Delivery with { SeenBy = other.Message.Delivery.SeenBy, ReadMark = other.Message.Delivery.ReadMark }) == other.Message.Delivery &&
        Message.Delivery.SeenBy.Count == other.Message.Delivery.SeenBy.Count &&
        Message.Delivery.SeenBy.All(p => other.Message.Delivery.SeenBy.TryGetValue(p.Key, out var value) && value == p.Value) &&
        SourceNetwork == other.SourceNetwork && IsMergedSource == other.IsMergedSource &&
        SourceChat.AccountId == other.SourceChat.AccountId && SourceChat.Title == other.SourceChat.Title && SourceChat.IsEncrypted == other.SourceChat.IsEncrypted &&
        Status == other.Status && Sender == other.Sender && SenderAvatarUrl == other.SenderAvatarUrl && Reply == other.Reply && ReplyAuthor == other.ReplyAuthor &&
        ShowDate == other.ShowDate && DateLabel == other.DateLabel && ShowUnreadDivider == other.ShowUnreadDivider && ShowSender == other.ShowSender && ContinuesGroup == other.ContinuesGroup &&
        ShowHeader == other.ShowHeader && ShowTime == other.ShowTime && (!ShowTime || HeaderTime == other.HeaderTime) && HeaderName == other.HeaderName && ReceiptStage == other.ReceiptStage && ShowReceipt == other.ShowReceipt &&
        // A reader's face moves from one message to another as they read on, which changes no other part of the row.
        ReadBy.SequenceEqual(other.ReadBy) && ShowLock == other.ShowLock && IsPhotoGroupMember == other.IsPhotoGroupMember &&
        PhotoGroupMessages.SequenceEqual(other.PhotoGroupMessages) && MediaReloadVersion == other.MediaReloadVersion;

    // Quoted: messages a reply quotes that are not loaded rows, by MessageIdentity.Key, as a service's memory gave them whole
    // (ConversationViewModel.PeekQuotesAsync). Not rows of the conversation, so they never place a reader's face.
    public MessageItemViewModel(ChatMessage message, ChatMessage? previous, ChatSummary chat, IReadOnlyDictionary<string, ChatMessage> loaded, ChatMessage? next=null, bool latestOutgoing=true, bool newest=false, bool showService=false,
        IReadOnlyDictionary<string, ChatMessage>? quoted = null, DateTimeOffset? now = null)
    {
        Message = message;
        SourceNetwork = chat.Network;
        SourceChat = chat; IsMergedSource = showService;
        ShowLock = newest && !message.IsDeleted && !message.IsHidden && ConversationEncryption.LockAllowed(message, chat);
        // A send still going says so only on the newest row, like any receipt: photos sent one at a time each carried a
        // "sending" mark that went as each was confirmed, and each row shrank by it. One that needs attention keeps it.
        ShowReceipt=latestOutgoing || message.EditedAt is not null ||
            (message.IsOutgoing && message.Delivery.SendState is not (null or "SUCCESS" or "LOCAL_SENDING" or "LOCAL_PENDING" or "PENDING"));
        ContinuesGroup = NativeBeeper.Presentation.Services.ConversationReading.ContinuesGroup(message, previous);
        SenderAvatarUrl = chat.Participants.FirstOrDefault(p => p.Id == message.SenderId)?.AvatarUrl;
        IsLargeEmoji = !message.IsDeleted && !message.IsHidden && message.Attachments.Count == 0 && message.ReplyToId is null &&
            NativeBeeper.Presentation.Services.EmojiText.IsShortEmojiOnly(NativeBeeper.Presentation.Services.MessageText.PlainText(message.Text ?? ""));
        Sender = message.IsOutgoing ? "You" : message.SenderName ?? chat.Participants.FirstOrDefault(p => p.Id == message.SenderId)?.DisplayName ?? "Unknown sender";
        // Scheduled rows sit after today's history; their due time belongs to the footer. Group them as a send of now
        // so Send now neither removes tomorrow's divider nor adds today's date/time header in an old or empty thread.
        var current = now ?? DateTimeOffset.Now;
        headerTimestamp = ScheduledSendRows.Is(message) ? current : message.Timestamp;
        var previousTime = previous is not null && ScheduledSendRows.Is(previous) ? current : previous?.Timestamp;
        ShowDate = previousTime is null || previousTime.Value.LocalDateTime.Date != headerTimestamp.LocalDateTime.Date;
        var headed = !message.IsHidden && message.Type != "system";
        ShowTime = headed && (ShowDate || headerTimestamp - previousTime!.Value >= TimeGap);
        HeaderName = !message.IsOutgoing && chat.Type == "group" ? Sender : "";
        ShowHeader = ShowTime || headed && HeaderName.Length > 0 && !ContinuesGroup;
        ShowSender = !message.IsOutgoing && (ShowDate || previous!.SenderId != message.SenderId || previous.IsOutgoing != message.IsOutgoing ||
            message.Timestamp - previous.Timestamp > TimeSpan.FromMinutes(5));
        var original = message.ReplyToId is not { } replyId ? null
            : loaded.TryGetValue(replyId, out var quotedRow) ? quotedRow : quoted?.GetValueOrDefault(MessageIdentity.Key(message.ChatId, replyId));
        Reply = message.IsDeleted || message.ReplyToId is null ? "" : original is not null ? PreviewText(original) : "Earlier message isn’t loaded";
        ReplyAuthor = original is null ? "Replying to a message" : original.IsOutgoing ? "Replying to you" : original.SenderName ??
            chat.Participants.FirstOrDefault(p => p.Id == original.SenderId)?.DisplayName ?? "Original message";
        Attachments = message.IsDeleted ? "" : string.Join("\n", message.Attachments.Where(a => a.Type != "img" && a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true).Select(a =>
            $"{(a.IsVoiceNote ? "Voice message" : a.IsSticker ? "Sticker" : a.IsGif ? "GIF" : a.Type switch { "img" => "Image", "video" => "Video", "audio" => "Audio", _ => "File" })}" +
            (string.IsNullOrWhiteSpace(a.Name) ? "" : $" · {a.Name}") + (a.Size is > 0 ? $" · {a.Size / 1024.0:0.#} KB" : "")));
        ReactionChips = message.IsDeleted ? [] : message.Reactions.GroupBy(r => r.Key).Select(g => new ReactionChip(g.Key,
            string.Join(", ", g.Select(r => r.ParticipantId).Distinct().Select(id =>
            {
                var person = chat.Participants.FirstOrDefault(p => p.Id == id);
                return person?.IsSelf == true || id == NativeBeeper.Presentation.Services.ReactionPrediction.LocalSelf ? "You"
                    : person?.DisplayName ?? (id == message.SenderId ? Sender : "Unknown participant");
            })))).ToArray();
        Reactions = string.Join("   ", ReactionChips.Select(r => r.Emoji));
        Status = string.Join(" · ", new[] { message.EditedAt is not null && !message.IsDeleted ? "Edited" : null, DeliveryText(message, chat) }.Where(s => !string.IsNullOrEmpty(s)));
        ReceiptStage = Stage(message, chat) ?? "";
        ReadBy = showService ? [] : ReadFaces(message, chat, loaded);
    }

    public static string PreviewText(ChatMessage message)
    {
        if (message.IsDeleted) return "Message deleted";
        if (message.IsHidden) return "Message unavailable";
        var media = message.Attachments.FirstOrDefault();
        var mediaLabel = media is null ? null : media.IsGif ? "GIF" : media.IsSticker ? "Sticker" : media.IsVoiceNote ? "Voice message" :
            media.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || media.Type == "img" ? "Photo" : media.Type == "video" ? "Video" : "File";
        var readableText = NativeBeeper.Presentation.Services.MetaAiMessages.Text(message);
        var text = string.IsNullOrWhiteSpace(readableText) ? mediaLabel ?? message.Type ?? "Message" : readableText;
        text = NativeBeeper.Presentation.Services.MessageText.PlainText(text).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    // ReadMark is a conversation position, which may be on the reader's own message.
    // SeenBy separately controls whether an outgoing message is labelled Read.
    private static IReadOnlyList<ReadFace> ReadFaces(ChatMessage message, ChatSummary chat, IReadOnlyDictionary<string, ChatMessage> loaded)
    {
        if (!ReceiptRules.IsGroup(chat) || !ReceiptRules.HasReceipts(chat) || message.IsDeleted || message.IsHidden) return [];
        if (message.Delivery.ReadMark is not { Count: > 0 } marks) return [];
        var faces = new List<ReadFace>();
        foreach (var id in marks.Distinct(StringComparer.Ordinal))
        {
            // Saved/older pages can still carry the previous mark after the current page has
            // moved it. Keep only the newest explicit mark; SeenBy alone never invents one.
            if (loaded.Values.Any(other => other.ChatId == message.ChatId && !other.IsDeleted && !other.IsHidden &&
                other.Delivery.ReadMark?.Contains(id) == true &&
                (other.Timestamp > message.Timestamp || other.Timestamp == message.Timestamp &&
                    string.CompareOrdinal(other.Id, message.Id) > 0))) continue;
            var person = chat.Participants.FirstOrDefault(candidate => candidate.Id == id);
            if (person is { IsSelf: true }) continue;
            // Somebody who has left the conversation, or whose participant row has not arrived, still read it.
            faces.Add(new(id, person?.AvatarUrl, person?.DisplayName ?? "Someone", NativeBeeper.Presentation.Services.AvatarOverrideIdentity.ForParticipant(chat, id)));
        }
        return faces;
    }

    private static string? DeliveryText(ChatMessage message, ChatSummary chat)
    {
        if (!message.IsOutgoing || message.IsDeleted) return null;
        var delivery = message.Delivery;
        if (ScheduledSendRows.Is(delivery.SendState)) return ScheduledSendRows.Describe(delivery.SendState, message.Timestamp, DateTimeOffset.Now);
        if (ReceiptRules.IsRead(message, chat)) return "Read";
        if (ReceiptRules.IsDelivered(message, chat)) return "Delivered";
        return delivery.SendState switch
        {
            "LOCAL_SENDING" => "Sending…", "LOCAL_PENDING" => "Sending…",
            "LOCAL_HELD" => "Not sent yet · Undo takes it back",
            "LOCAL_UNCONFIRMED" => "Not confirmed yet · It won't be sent twice",
            "LOCAL_UNKNOWN" => "May not have sent · Check the conversation before sending it again",
            "LOCAL_REJECTED" => "Not sent · Restore draft to try again",
            "LOCAL_ATTACHMENT_REJECTED" => "Not sent · Select the attachment again to retry",
            "SUCCESS" => "Sent", "PENDING" => "Sending…", "FAIL_RETRIABLE" => "Send failed", "FAIL_PERMANENT" => "Send failed", _ => null
        };
    }

    // Sending, failed and uncertain states come first; otherwise read, delivered, then sent. SMS never reports read, and
    // a group is read only once everyone else in it has read (ReceiptRules).
    private static string? Stage(ChatMessage message, ChatSummary chat)
    {
        if (!message.IsOutgoing || message.IsDeleted) return null;
        var delivery = message.Delivery;
        switch (delivery.SendState)
        {
            case "LOCAL_SENDING" or "LOCAL_PENDING" or "LOCAL_UNCONFIRMED" or "PENDING" or "LOCAL_HELD" or ScheduledSendRows.Scheduled or ScheduledSendRows.Scheduling: return "sending";
            case var state when ScheduledSendRows.IsNotSent(state): return "failed";
            case "LOCAL_UNKNOWN": return "unknown";
            case "LOCAL_REJECTED" or "LOCAL_ATTACHMENT_REJECTED" or "FAIL_RETRIABLE" or "FAIL_PERMANENT": return "failed";
        }
        if (ReceiptRules.IsRead(message, chat)) return "read";
        if (ReceiptRules.IsDelivered(message, chat)) return "delivered";
        return delivery.SendState == "SUCCESS" ? "sent" : null;
    }
}
