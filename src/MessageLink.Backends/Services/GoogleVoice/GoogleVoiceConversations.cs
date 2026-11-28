using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// Google Voice's answers to the routers (IConversationService). Every Voice id carries its account, and the one backend
// serves every account. Voice sends text and photos, marks read, archives, takes a conversation out of spam and
// deletes one; it cannot edit, remove, reply to or react to a message, and sends no typing.
internal sealed class GoogleVoiceConversations(GoogleVoiceBackend voice) : IConversationService
{
    public ServiceTag Tag => ServiceTags.GoogleVoice;
    public bool OwnsMedia(MessageAttachment attachment) => GoogleVoiceModels.OwnsMedia(attachment);

    public bool AnyConnected => voice.AnyConnected;
    public bool IsConnected(string chatId) => voice.IsConnected(chatId);
    public bool IsScopeConnected(string scope) => voice.IsScopeConnected(scope);
    public bool IsConfigured(string scope) => voice.IsConfigured(scope);
    public bool NeedsSignInOnly(string scope) => false;

    // Voice searches only what this session loaded, which paging back reaches.
    public bool CanReadWindow(string chatId) => false;
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken)
        => Task.FromException<HistoryWindow>(new NotSupportedException(RoutedWords.CantOpenAtMessage));
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Task.FromResult<HeldHistory?>(null);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => voice.GetChatAsync(chatId, cancellationToken);
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken) => voice.GetMessagesAsync(chatId, olderThan, cancellationToken);
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => voice.ResolveMessageAsync(chatId, messageId, cancellationToken);
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult(voice.PeekMessage(chatId, messageId));
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => PeekAsync(chatId, messageId, cancellationToken);
    public bool HoldsPartialCopies => false;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => voice.CanMarkReadAsync(chatId);
    public bool TellsReads => true;
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => voice.MarkReadAsync(chatId, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => voice.GetSendPermissionAsync(chatId);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => voice.SendTextAsync(chatId, text, cancellationToken);
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken)
        => Task.FromException(GoogleVoiceBackend.Refused("Google Voice can't edit a sent message."));
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken)
        => Task.FromException(GoogleVoiceBackend.Refused("Google Voice can't remove a sent message."));
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Task.FromResult(new MessageActionOptions(false, [], []));
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken)
        => Task.FromException<SendReceipt>(GoogleVoiceBackend.Refused("Google Voice doesn't support replies."));
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken)
        => Task.FromException(GoogleVoiceBackend.Refused("Google Voice doesn't support reactions."));
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => new(await voice.GetChatAsync(chatId, cancellationToken), true);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => setting switch
    {
        ChatSetting.Archived => voice.SetArchivedAsync(chatId, value, cancellationToken),
        ChatSetting.Spam when !value => voice.RemoveSpamAsync(chatId, cancellationToken),
        ChatSetting.Spam => Task.FromException<ChatSummary>(GoogleVoiceBackend.Refused("Message Link can remove a Google Voice conversation from spam, but cannot mark one as spam.")),
        _ => Task.FromException<ChatSummary>(GoogleVoiceBackend.Refused("That setting is kept on this PC only, not on Google Voice.")),
    };
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => voice.DeleteChatAsync(chatId, cancellationToken);

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => voice.GetSendPermissionAsync(chatId);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => voice.GetPhotoPermissionAsync(chatId, file);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Task.FromResult(new AttachmentCaptionSupport(true, null));
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => voice.SendPhotoAsync(chatId, file, caption, cancellationToken);
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => false;
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken)
        => throw new NotSupportedException(RoutedWords.NoPhotoSet);
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;

    // Google Voice photos and voicemail audio are kept in the same encrypted, profile-scoped cache, keyed by their
    // account-qualified id, so a voicemail played once plays again at once and without asking Google. A save is made
    // from the same copy instead of downloading it again.
    private Task<byte[]> ReadKeptAsync(MessageAttachment attachment, IMediaShelf shelf, CancellationToken cancellationToken) =>
        shelf.KeptAsync(attachment.Id, token => voice.ReadImageAsync(attachment, token), cancellationToken);
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken) => ReadKeptAsync(attachment, shelf, cancellationToken);
    public Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken) => ReadKeptAsync(video, shelf, cancellationToken);
    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken)
        => await destination.WriteAsync(await ReadKeptAsync(attachment, shelf, cancellationToken), cancellationToken);
    public string? ClipKey(MessageAttachment clip) => null;
    public bool CanSave(MessageAttachment attachment) => voice.CanSave(attachment);
    public string? WarmKey(MessageAttachment picture) => picture.Id;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => voice.GetContactOptionsAsync(accountId);
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => voice.FindContactsAsync(accountId, query, cancellationToken);
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => voice.StartDirectAsync(contact);
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => throw new SendException(false, RoutedWords.OpensWithoutSending);
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>?>(null);
    public ChatSummary? FindDirectChat(ContactPerson contact) => voice.FindDirectChat(contact);
    // What New chat shows once Voice is picked. Upstream has no call for starting a group text at all, and Beeper does not
    // offer it either.
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken)
        => Task.FromResult(new GroupOptions(false, "Google Voice group texts can't be started from New chat."));
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken)
        => throw new SendException(false, "Google Voice group texts can't be started from New chat.");
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();

    public bool SendsTyping => false;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Task.CompletedTask;
    public void NoteComposerActivity(string? chatId) { }
    public IEnumerable<ITypingService> TypingSources => [];
}
