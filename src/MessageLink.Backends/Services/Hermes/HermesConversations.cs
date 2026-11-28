using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Hermes;

// Hermes' answers to the routers (IConversationService). Hermes answers only for its own threads: text and pictures in,
// its reply out. Everything else is refused by name, and nothing here hands it another conversation (the owner's
// privacy line, 2026-11-14). Its threads are its profiles, set up in Settings: nobody is found, started or grouped there.
internal sealed class HermesConversations(HermesBackend? hermes) : IConversationService
{
    private HermesBackend H => hermes ?? throw new BackendException(BackendFailure.Unsupported, "Hermes isn't available here.");
    private static Exception Cannot(string what) => new BackendException(BackendFailure.Unsupported, $"Hermes in Message Link cannot {what}.");
    private const string FromProfiles = "Hermes conversations come from its profiles in Settings.";
    private static Task<T> NotHere<T>() => Task.FromException<T>(new SendException(false, FromProfiles));

    public ServiceTag Tag => ServiceTags.Hermes;
    public bool OwnsMedia(MessageAttachment attachment) => HermesBackend.OwnsMedia(attachment);

    public bool AnyConnected => hermes?.AnyConnected ?? false;
    public bool IsConnected(string chatId) => hermes?.IsConnected(chatId) == true;
    public bool IsScopeConnected(string scope) => scope == HermesIds.Scope ? hermes?.AnyConnected ?? false : hermes?.IsConnected(scope) == true;
    public bool IsConfigured(string scope) => hermes?.Configured == true;
    public bool NeedsSignInOnly(string scope) => false;

    public bool CanReadWindow(string chatId) => false;
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken)
        => Task.FromException<HistoryWindow>(new NotSupportedException(RoutedWords.CantOpenAtMessage));
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Task.FromResult<HeldHistory?>(null);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => H.GetChatAsync(chatId, cancellationToken);
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken) => H.GetMessagesAsync(chatId, olderThan, cancellationToken);
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => H.ResolveMessageAsync(chatId, messageId, cancellationToken);
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult(hermes?.PeekMessage(chatId, messageId));
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => PeekAsync(chatId, messageId, cancellationToken);
    public bool HoldsPartialCopies => false;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => hermes?.CanMarkReadAsync(chatId, cancellationToken) ?? Task.FromResult(false);
    // Hermes keeps its read state here alone and tells no service, so private reading has nothing to hold back.
    public bool TellsReads => false;
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => H.MarkReadAsync(chatId, messageId, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => H.GetSendPermissionAsync(chatId, cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => H.SendTextAsync(chatId, text, cancellationToken);
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken) => Task.FromException(Cannot("edit a message"));
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken) => Task.FromException(Cannot("remove a message"));
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Task.FromResult(new MessageActionOptions(false, [], []));
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken) => Task.FromException<SendReceipt>(Cannot("reply to a message"));
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken) => Task.FromException(Cannot("react to a message"));
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => new(await H.GetChatAsync(chatId, cancellationToken), false);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => Task.FromException<ChatSummary>(Cannot("change what a conversation is"));
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Task.FromException(Cannot("delete a conversation"));

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => H.GetPermissionAsync(chatId, cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => H.GetFilePermissionAsync(chatId, file, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => H.GetCaptionSupportAsync(chatId, file, cancellationToken);
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => H.SendAsync(chatId, file, cancellationToken, caption);
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => false;
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken)
        => throw new NotSupportedException(RoutedWords.NoPhotoSet);
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;

    // Its pictures are its own store's, read fresh; never warmed ahead and never kept as clips.
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken) => H.ReadImageAsync(attachment, cancellationToken);
    public async Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await H.CopyToAsync(video, memory, cancellationToken);
        return memory.ToArray();
    }
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken) => H.CopyToAsync(attachment, destination, cancellationToken);
    public string? ClipKey(MessageAttachment clip) => null;
    public bool CanSave(MessageAttachment attachment) => hermes?.CanSave(attachment) == true;
    public string? WarmKey(MessageAttachment picture) => null;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => NotHere<ContactOptions>();
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>>([]);
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => NotHere<ChatSummary>();
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => throw new SendException(false, RoutedWords.OpensWithoutSending);
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>?>(null);
    public ChatSummary? FindDirectChat(ContactPerson contact) => null;
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult(new GroupOptions(false, FromProfiles));
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken) => NotHere<ChatSummary>();
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();

    // Hermes only ever reports its own working line into its threads; nothing is sent to it.
    public bool SendsTyping => false;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Task.CompletedTask;
    public void NoteComposerActivity(string? chatId) { }
    public IEnumerable<ITypingService> TypingSources => hermes is null ? [] : [hermes];
}
