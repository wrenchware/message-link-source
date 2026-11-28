using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

// Messenger's answers to the routers (IConversationService), each on the account its id names. Messenger does
// everything the routers route, on either of its connections; it alone changes an existing group.
internal sealed class MessengerConversations(MessengerBackend messenger, Func<ProviderAccounts?> accounts) : IConversationService
{
    private MessengerBackend Messenger(string id) => accounts()?.Messenger(id) ?? messenger;
    private MessengerBackend Messenger(MessageAttachment attachment) => Messenger(attachment.Id ?? attachment.SourceUrl ?? "");

    public ServiceTag Tag => ServiceTags.Messenger;
    public bool OwnsMedia(MessageAttachment attachment) => MessengerModels.OwnsMedia(attachment);

    public bool AnyConnected => accounts()?.Messengers.Any(m => m.IsConnected) ?? messenger.IsConnected;
    public bool IsConnected(string chatId) => Messenger(chatId).IsConnected;
    public bool IsScopeConnected(string scope) => scope == Tag.Prefix && messenger.IsConnected;
    public bool IsConfigured(string scope) => Messenger(scope).SessionSaved;
    public bool NeedsSignInOnly(string scope) => false;

    // Messenger's history is kept on this PC by its helper, so a window around any moment can be read.
    public bool CanReadWindow(string chatId) => true;
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken) => Messenger(chatId).ReadWindowAsync(chatId, at, older, newer, cancellationToken);
    // Its opening waits for its read: that comes from the helper's memory within milliseconds, and the messages it delivers
    // as it connects reach a merge's saved copy through their events.
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Task.FromResult<HeldHistory?>(null);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).GetChatAsync(chatId, cancellationToken);
    // Not announced to MediaNews: Messenger's "unavailable" can be a Facebook address that expired and is being fetched
    // again behind the answer, which a page read proves nothing about; its events and catch-up already say what it handed out.
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken) => Messenger(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken);
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => Messenger(chatId).LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken);
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => Messenger(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken);
    // Its helper's own store, skipped when the pipe is busy: still never a request.
    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => await Messenger(chatId).PeekMessageAsync(chatId, messageId, cancellationToken);
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => PeekAsync(chatId, messageId, cancellationToken);
    public bool HoldsPartialCopies => false;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).CanMarkReadAsync(chatId, cancellationToken);
    public bool TellsReads => true;
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => Messenger(chatId).MarkReadAsync(chatId, messageId, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).GetSendPermissionAsync(chatId, cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => Messenger(chatId).SendTextAsync(chatId, text, cancellationToken);
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken) => Messenger(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken);
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken) => Messenger(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken);
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Messenger(chatId).GetOptionsAsync(chatId, messageId, cancellationToken);
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken) => Messenger(chatId).ReplyAsync(chatId, messageId, text, cancellationToken);
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken) => Messenger(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken);
    public Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).GetSettingsAsync(chatId, cancellationToken);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => Messenger(chatId).SetAsync(chatId, setting, value, cancellationToken);
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).DeleteChatAsync(chatId, cancellationToken);

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => Messenger(chatId).GetPermissionAsync(chatId, cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Messenger(chatId).GetFilePermissionAsync(chatId, file, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Messenger(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken);
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => Messenger(chatId).SendAsync(chatId, file, cancellationToken, caption);
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => Messenger(chatId).CanSendBatch(chatId, files);
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken) => Messenger(chatId).SendBatchAsync(chatId, files, cancellationToken);
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;

    // Messenger photos persist in the same encrypted media cache as Google Messages photos, so a conversation seen before
    // draws them at once on the next launch, even before Messenger has reconnected, instead of every helper session
    // downloading and decrypting each photo again. Link cards' pictures the helper made are among them (ImageCacheKey).
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken)
        => shelf.KeptAsync(MessengerBackend.ImageCacheKey(attachment, inline), token => Messenger(attachment).ReadImageAsync(attachment, token, inline), cancellationToken);
    public async Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await Messenger(video).CopyToAsync(video, memory, cancellationToken);
        return memory.ToArray();
    }
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken)
        => Messenger(attachment).CopyToAsync(attachment, destination, cancellationToken);
    public string? ClipKey(MessageAttachment clip) => MessengerBackend.ClipCacheKey(clip);
    public bool CanSave(MessageAttachment attachment) => Messenger(attachment).CanSave(attachment);
    // The smaller copy's source, as its read above keeps it.
    public string? WarmKey(MessageAttachment picture) => MessengerBackend.ImageCacheKey(picture, inline: true);

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => Messenger(accountId).GetContactOptionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => Messenger(accountId).FindContactsAsync(accountId, query, cancellationToken);
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => Messenger(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken);
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => throw new SendException(false, RoutedWords.OpensWithoutSending);
    // No ranking of who this account writes to most that the helper exposes; unknown is nothing.
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>?>(null);
    public ChatSummary? FindDirectChat(ContactPerson contact) => Messenger(contact.AccountId).FindDirectChat(contact);
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken) => Messenger(accountId).GetGroupOptionsAsync(accountId, cancellationToken);
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken)
        => Messenger(accountId).CreateGroupAsync(accountId, people, title, cancellationToken);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => ((IGroupManagement)Messenger(chatId)).RenameGroupAsync(chatId, title, cancellationToken);
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => ((IGroupManagement)Messenger(chatId)).AddGroupMembersAsync(chatId, people, cancellationToken);
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => ((IGroupManagement)Messenger(chatId)).RemoveGroupMemberAsync(chatId, personId, cancellationToken);

    public bool SendsTyping => true;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Messenger(chatId).SetTypingAsync(chatId, isTyping, cancellationToken);
    // Puts off telling the other end the typing stopped. A removed account can remain open in a client until its next refresh.
    public void NoteComposerActivity(string? chatId)
    {
        if (chatId is null || !Tag.Owns(chatId)) return;
        try { Messenger(chatId).NoteComposerActivity(chatId); }
        catch (MessageLink.Core.Backend.BackendException) { }
    }
    public IEnumerable<ITypingService> TypingSources => accounts()?.Messengers ?? [messenger];
}
