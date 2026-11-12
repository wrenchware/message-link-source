namespace NativeBeeper.Core.Messaging;

public sealed record Page<T>(IReadOnlyList<T> Items, bool HasMore, string? OldestCursor, string? NewestCursor,
    // The page is usable cached content, but its provider is still refreshing it in the background.
    // Appended so existing providers and call sites keep their previous behavior.
    bool IsRefreshing = false,
    // Usable partial results were returned, but one source needs the same operation retried.
    string? Warning = null,
    // A failed source has not answered that its conversations disappeared. Preserve its previously loaded rows.
    IReadOnlyList<string>? UnavailableScopes = null);
public sealed record ChatSummary(string Id, string AccountId, string Title, string Network, string Type,
    DateTimeOffset? LastActivity, int UnreadCount, bool IsMuted, bool IsPinned, bool IsArchived,
    bool IsReadOnly, string? AvatarUrl, ChatMessage? Preview, IReadOnlyList<string> MemberChatIds,
    string? DefaultMemberChatId, string? MergedIntoChatId, ChatCapabilities Capabilities,
    IReadOnlyList<ChatParticipant> Participants, int ParticipantCount, string? ExpectedTransport = null, string? AvatarColor = null,
    // End-to-end encrypted, when the network says so outright (Messenger). Null means not known, never "no". Appended.
    bool? IsEncrypted = null,
    // Marked unread on this PC by the user; cleared when the conversation is read here. Appended.
    bool IsMarkedUnread = false,
    // Filed as spam by the network (Google Voice). Kept out of every view but Spam. Appended.
    bool IsSpam = false,
    // Notifications are held until this moment, when one reminder is raised for whatever is unread then.
    // Local to this PC and appended.
    DateTimeOffset? SnoozedUntil = null,
    // A provider event record (call/voicemail), not a destination for a new text message.
    bool IsCallHistory = false,
    // The newest encryption switch this PC has saved for the conversation outside its loaded history (Google's search
    // archive). Google says it only in system lines, and the one that matters is rarely in the newest page. Appended.
    SavedEncryptionSwitch? SavedSwitch = null,
    // When a timed mute set on this PC ends; null for no mute or a mute until turned off. IsMuted already says whether
    // the mute held when the summary was read. Local to this PC and appended.
    DateTimeOffset? MutedUntil = null,
    // How far this account has read, as the network says it (Messenger and Instagram keep a watermark): "New messages"
    // goes above what came after it. Null where the network gives only an unread flag. Appended.
    DateTimeOffset? ReadUpTo = null,
    // Muted on the service itself, from this or another of the account's devices (Messenger reports it), as the
    // provider said it; null when the service reports none or does not say. Received only: nothing here sends a mute.
    // LocalPreferenceChatReader folds it into IsMuted and MutedUntil. Appended.
    ServiceMute? ServiceMute = null,
    // A Marketplace buyer and seller's chat (Messenger says so by the chat's own kind). Appended, and written only when
    // true, so every saved row
    // and wire copy of any other conversation reads exactly as before.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool IsMarketplace = false);
// A mute the service reports. Until: when it ends; null for a mute until turned off. Key: how a mute set aside on this
// PC names the one it set aside, so a newer mute from the service shows again (LocalChatPreferences.ServiceMuteSetAside).
public sealed record ServiceMute(DateTimeOffset? Until)
{
    public long Key => Until?.ToUnixTimeMilliseconds() ?? -1;
    public bool HoldsAt(DateTimeOffset now) => Until is null || Until > now;
}
// Whether a saved switch line turned end-to-end encryption on, when, and how far the saved history runs on from it
// without a gap. Anything after SavedThrough may be missing, so a switch on counts only when loaded history reaches
// back to cover the rest. Null means only the switch line itself is known.
public sealed record SavedEncryptionSwitch(bool Encrypted, DateTimeOffset At, DateTimeOffset? SavedThrough = null);
public sealed record ChatParticipant(string Id, string DisplayName, string? AvatarUrl = null, bool? IsSelf = null,
    // A provider-supplied phone number for display and contact matching. Appended because participants are built positionally.
    string? Phone = null,
    // A provider-supplied handle, never an opaque person id. Appended for saved/positional compatibility.
    string? Username = null,
    // The service says this person is one of the account's saved contacts (Google Messages: the phone gives a first name
    // only for those). Null where the service does not say, which is never read as yes. Appended.
    bool? IsContact = null);

// Unknown capability values remain unknown; flags alone never enable an unimplemented action.
// RenameGroup, AddMembers, RemoveMembers: changes to an existing group, each seen by everybody in it, offered only where
// the provider has said this account may make them for this conversation (Messenger's ordinary groups). Appended.
public sealed record ChatCapabilities(int? Reply, int? Edit, int? Delete, int? Reaction,
    bool? ReadReceipts, bool? TypingNotifications, int? MaxTextLength = null,
    bool? RenameGroup = null, bool? AddMembers = null, bool? RemoveMembers = null)
{
    public static ChatCapabilities Unknown { get; } = new(null, null, null, null, null, null);
}

public sealed record ChatMessage(string Id, string ChatId, string SenderId, string? SenderName,
    DateTimeOffset Timestamp, string SortKey, string? Text, string? Type, bool IsOutgoing,
    bool IsDeleted, bool IsHidden, DateTimeOffset? EditedAt, string? ReplyToId,
    IReadOnlyList<MessageAttachment> Attachments, IReadOnlyList<MessageReaction> Reactions,
    MessageDelivery Delivery, string? Transport = null,
    // A system line recording that the conversation switched to (true) or away from (false) end-to-end encryption.
    // Google Messages has no other signal. Appended: this record is built positionally.
    bool? EncryptionSwitch = null,
    // Provider call subtype used for small call/voicemail affordances. Appended: this record is built positionally.
    string? CallKind = null,
    // The network reports that this message itself went end-to-end encrypted (Google Messages marks each one). Null is
    // unknown, never "not encrypted". Appended: built positionally.
    bool? IsEncrypted = null,
    // Our own message's copy as the provider returns it can carry the id the send's receipt named it by — Google Messages'
    // transaction id, which is not the message's id. It is how a still-pending send knows its copy has arrived, exactly
    // and not by guessing from the time. Null for a provider that answers a receipt with the message's own id. Appended.
    string? PendingId = null,
    // A button a service's own line carries, as its app draws one: Messenger's "View buyer profile" after a Marketplace buyer's
    // first line. Only on a system line, and written only when there is one, so every other saved row reads as before. Appended.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NoticeAction? Action = null);
/// <summary>A button on a service's own line: its words, and the https address it opens in the browser.</summary>
public sealed record NoticeAction(string Title, string Url);
public sealed record MessageAttachment(string? Name, string Type, string? MimeType, long? Size,
    bool IsVoiceNote, bool IsGif, bool IsSticker, string? Id = null, string? SourceUrl = null, double? Width = null, double? Height = null,
    string? Availability = null, // null, "preview" (full size requested from the phone), or "downloading" / "manual" / "failed" while the phone has not downloaded the media
    // Where a shared reel, post or link leads. Appended rather than inserted: these are positional everywhere they are
    // built, so a new field in the middle silently rebinds the ones after it.
    string? Link = null,
    // The picture stands for something that plays — a shared reel or video. Appended, like Link: these are built
    // positionally.
    bool Play = false,
    // What a linked page says about itself, shown under its title in the link card. Appended.
    string? Description = null,
    string? Caption = null,
    // How long a recording runs, when the network says so before it is downloaded (a Google Voice voicemail). Appended.
    double? DurationSeconds = null,
    // What the service's button on a shared card says ("View listing"), in its words. The card itself is what opens. Appended.
    string? ActionTitle = null);
public sealed record MessageReaction(string Key, string ParticipantId);
public sealed record MessageDelivery(string? SendState, int? DeliveredParticipantCount,
    bool? Seen, DateTimeOffset? SeenAt, IReadOnlyDictionary<string, bool?> SeenBy,
    // Those for whom this is the newest message they have read: where a small face is drawn. Kept apart from SeenBy,
    // which stays true for everything behind the watermark and is what says "Read" in words. Appended, like the rest.
    IReadOnlyList<string>? ReadMark = null);
