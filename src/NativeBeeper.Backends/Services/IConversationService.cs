using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

/// <summary>
/// One service's answer to everything the routers hand on by id: a conversation id, a scope, an account id or an
/// attachment it owns. Each service answers every action, refusing in its own words what it cannot do, so the routers
/// only find the owner (ConversationOwners) and pass the call on. A new service is one of these and one entry there.
/// </summary>
internal interface IConversationService
{
    ServiceTag Tag { get; }
    bool OwnsMedia(MessageAttachment attachment);

    // ---- connection (MessagingRouter, IProviderConnectionState) ----
    bool AnyConnected { get; }
    bool IsConnected(string chatId);
    /// <summary>A scope without an account key: the prefix itself, or Voice's per-account scope.</summary>
    bool IsScopeConnected(string scope);
    bool IsConfigured(string scope);
    bool NeedsSignInOnly(string scope);

    // ---- reading (MessagingRouter) ----
    bool CanReadWindow(string chatId);
    Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken);
    Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken);
    Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken);
    Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken);
    Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken);
    Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken);
    /// <summary>Only memory the service already holds: never a request (IMessagePeek).</summary>
    ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken);
    ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken);
    bool HoldsPartialCopies { get; }
    Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken);
    /// <summary>Whether its read markers reach a service, so they pass PrivateReadingGate first and ReadDrift after.</summary>
    bool TellsReads { get; }
    Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken);
    /// <summary>What the server's whole history reads and fills by (its budget, its echo of sends, its events).</summary>
    HistoryFacts HistoryFacts => HistoryFacts.Plain;
    /// <summary>One read nobody asked for, out of the account's budget where it has one.</summary>
    bool TryTakeUnaskedRead(string chatId) => true;

    // ---- sending and actions (MessagingRouter) ----
    Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken);
    Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken);
    Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken);
    Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken);
    Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken);
    Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken);
    Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken);
    Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken);
    Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken);
    Task DeleteChatAsync(string chatId, CancellationToken cancellationToken);

    // ---- attachments out (MediaRouter) ----
    Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken);
    Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken);
    Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken);
    Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken);
    bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files);
    Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken);
    bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files);

    // ---- attachments in (MediaRouter): the shelf is the router's disk cache and video-still notes ----
    Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken);
    /// <summary>The whole video a still is made from (VideoStills), read as playing it reads it.</summary>
    Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken);
    /// <summary>The attachment's bytes for a save, or for a kept clip's first read (ClipKey). Drafts and saved photos the router serves first.</summary>
    Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken);
    /// <summary>The disk key a GIF delivered as a clip is kept under, so it plays without a download; null where its clips are not kept.</summary>
    string? ClipKey(MessageAttachment clip);
    bool CanSave(MessageAttachment attachment);
    /// <summary>The disk key a picture is warmed under ahead of its conversation (RecentPhotos); null for one not warmed.</summary>
    string? WarmKey(MessageAttachment picture);

    // ---- directory (DirectoryRouter), by account id; groups by conversation id ----
    Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken);
    Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken);
    Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken);
    ChatSummary? FindDirectChat(ContactPerson contact);
    Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken);
    Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken);
    Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken);
    Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken);
    Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken);

    // ---- typing (TypingRouter) ----
    /// <summary>Whether this account's typing is told to the other end.</summary>
    bool SendsTyping { get; }
    Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken);
    /// <summary>Every composer edit, in any conversation; a service decides for itself whether it cares.</summary>
    void NoteComposerActivity(string? chatId);
    IEnumerable<ITypingService> TypingSources { get; }
}

/// <summary>What MediaRouter lends a service for reading: its encrypted disk cache, and its note of a provider's own video still.</summary>
internal interface IMediaShelf
{
    /// <summary>From the disk cache when it holds <paramref name="key"/>, otherwise fetched and then kept. A null key is fetched each time.</summary>
    Task<byte[]> KeptAsync(string? key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken);
    void NoteProviderStill(MessageAttachment video);
}
