using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Hermes;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

/// <summary>
/// The one lookup from an id to the service that owns it, for all four routers. An id carries its service's prefix
/// (ServiceTags), an added account's ids included; an id no service owns is refused and logged by its prefix alone,
/// never handed to Google Messages (owner, 2026-11-21). Attachments are found by their media names, and one nothing
/// claims is still Google's: its media ids are not all prefixed.
/// </summary>
internal sealed class ConversationOwners
{
    private readonly IConversationService[] services;
    // Attachments are claimed in the order the routers always asked: a media name is checked on the id and the source, so
    // an attachment could carry two.
    private readonly IConversationService[] mediaOrder;
    private readonly IConversationService mediaFallback;
    private readonly Action<string> log;

    public ConversationOwners(GoogleMessagesBackend google, GoogleMediaBackend googleMedia, MessengerBackend messenger, GoogleVoiceBackend voice,
        InstagramBackend instagram, HermesBackend? hermes = null) : this(google, googleMedia, messenger, voice, instagram, hermes, AppLog.Write) { }

    internal ConversationOwners(GoogleMessagesBackend google, GoogleMediaBackend googleMedia, MessengerBackend messenger, GoogleVoiceBackend voice,
        InstagramBackend instagram, HermesBackend? hermes, Action<string> log)
    {
        (Google, GoogleMedia, Messenger, Voice, Instagram, Hermes, this.log) = (google, googleMedia, messenger, voice, instagram, hermes, log);
        var googleService = new GoogleMessagesConversations(google, googleMedia, () => Accounts);
        var messengerService = new MessengerConversations(messenger, () => Accounts);
        var voiceService = new GoogleVoiceConversations(voice);
        var instagramService = new InstagramConversations(instagram, () => Accounts);
        var hermesService = new HermesConversations(hermes);
        services = [googleService, messengerService, voiceService, instagramService, hermesService];
        mediaOrder = [hermesService, voiceService, instagramService, messengerService];
        mediaFallback = googleService;
        Unowned = new UnownedConversations(log);
    }

    /// <summary>The added accounts, once the host has them: each service reads its account's backend from here.</summary>
    internal ProviderAccounts? Accounts { get; set; }

    /// <summary>Every service, in ServiceTags.All's order.</summary>
    internal IReadOnlyList<IConversationService> Services => services;

    /// <summary>The service whose prefix the id carries, or the refusal for an id none does.</summary>
    internal IConversationService Of(string id)
    {
        foreach (var service in services) if (service.Tag.Owns(id)) return service;
        return Unowned;
    }

    internal IConversationService OfMedia(MessageAttachment attachment)
    {
        foreach (var service in mediaOrder) if (service.OwnsMedia(attachment)) return service;
        return mediaFallback;
    }

    internal IConversationService Unowned { get; }

    // The backends themselves, for what is read from every service at once rather than routed by id: the combined chat
    // list, and constants of the one service that has them (Messenger's group limits, Instagram's first message).
    internal GoogleMessagesBackend Google { get; }
    internal GoogleMediaBackend GoogleMedia { get; }
    internal MessengerBackend Messenger { get; }
    internal GoogleVoiceBackend Voice { get; }
    internal InstagramBackend Instagram { get; }
    internal HermesBackend? Hermes { get; }
}

/// <summary>
/// The answer for an id no service owns. An action is refused as unavailable and logged with the id's prefix alone; a
/// question about it (connected? can it be read here?) is answered no, without a line, since lists ask those often.
/// Typing goes nowhere, as it always did for such an id.
/// </summary>
internal sealed class UnownedConversations(Action<string> log) : IConversationService
{
    internal const string ConversationWords = "This conversation isn't available.";
    internal const string AccountWords = "This account isn't available.";
    private static readonly ServiceTag None = new("", "", HasHelper: false);
    public ServiceTag Tag => None;
    public bool OwnsMedia(MessageAttachment attachment) => false;

    private Exception Refused(string id, string action, string words)
    {
        log($"conversation refused owner=none prefix={ServiceTags.LogPrefix(id)} action={action}");
        return new MessageLink.Core.Backend.BackendException(MessageLink.Core.Backend.BackendFailure.Unsupported, words);
    }
    private Task Fail(string id, string action) => Task.FromException(Refused(id, action, ConversationWords));
    private Task<T> Fail<T>(string id, string action) => Task.FromException<T>(Refused(id, action, ConversationWords));
    private Task<T> FailAccount<T>(string accountId, string action) => Task.FromException<T>(Refused(accountId, action, AccountWords));

    public bool AnyConnected => false;
    public bool IsConnected(string chatId) => false;
    public bool IsScopeConnected(string scope) => false;
    public bool IsConfigured(string scope) => false;
    public bool NeedsSignInOnly(string scope) => false;

    public bool CanReadWindow(string chatId) => false;
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken) => Fail<HistoryWindow>(chatId, "window");
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Task.FromResult<HeldHistory?>(null);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => Fail<ChatSummary>(chatId, "chat");
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken) => Fail<Page<ChatMessage>>(chatId, "messages");
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => Fail<ChatMessage>(chatId, "resolve");
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult<ChatMessage?>(null);
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult<ChatMessage?>(null);
    public bool HoldsPartialCopies => false;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => Task.FromResult(false);
    // As every service's but Hermes': PrivateReadingGate still decides first, as it did when such an id went on to Google.
    public bool TellsReads => true;
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => Fail(chatId, "read");

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => Fail<SendPermission>(chatId, "send_permission");
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => Fail<SendReceipt>(chatId, "send");
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken) => Fail(chatId, "edit");
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken) => Fail(chatId, "delete");
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Fail<MessageActionOptions>(chatId, "options");
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken) => Fail<SendReceipt>(chatId, "reply");
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken) => Fail(chatId, "react");
    public Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => Fail<ChatSettings>(chatId, "settings");
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => Fail<ChatSummary>(chatId, "set");
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Fail(chatId, "delete_chat");

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => Fail<SendPermission>(chatId, "attachment_permission");
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Fail<SendPermission>(chatId, "file_permission");
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Fail<AttachmentCaptionSupport>(chatId, "caption");
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => Fail<SendReceipt>(chatId, "send_file");
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => false;
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken) => Fail<SendReceipt>(chatId, "send_batch");
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;

    // Never reached: an attachment nothing claims is Google's (ConversationOwners.OfMedia).
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken) => Fail<byte[]>("", "image");
    public Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken) => Fail<byte[]>("", "video");
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken) => Fail("", "copy");
    public string? ClipKey(MessageAttachment clip) => null;
    public bool CanSave(MessageAttachment attachment) => false;
    public string? WarmKey(MessageAttachment picture) => null;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => FailAccount<ContactOptions>(accountId, "contact_options");
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => FailAccount<IReadOnlyList<ContactPerson>>(accountId, "find_contacts");
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => FailAccount<ChatSummary>(contact.AccountId, "start_direct");
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => FailAccount<ChatSummary>(contact.AccountId, "start_with_message");
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>?>(null);
    public ChatSummary? FindDirectChat(ContactPerson contact) => null;
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken) => FailAccount<GroupOptions>(accountId, "group_options");
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken) => FailAccount<ChatSummary>(accountId, "create_group");
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => Fail<ChatSummary>(chatId, "rename_group");
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => Fail<ChatSummary>(chatId, "add_members");
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => Fail<ChatSummary>(chatId, "remove_member");

    public bool SendsTyping => false;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Task.CompletedTask;
    public void NoteComposerActivity(string? chatId) { }
    public IEnumerable<ITypingService> TypingSources => [];
}
