using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Instagram;

// Instagram's answers to the routers (IConversationService), each on the account its id names. Instagram sends,
// replies, reacts, edits, takes back and marks read; changing a conversation's own settings and deleting one are not
// implemented, and are refused by name.
internal sealed class InstagramConversations(InstagramBackend instagram, Func<ProviderAccounts?> accounts) : IConversationService
{
    private InstagramBackend Instagram(string id) => accounts()?.Instagram(id) ?? instagram;
    private InstagramBackend Instagram(MessageAttachment attachment) => Instagram(attachment.Id ?? attachment.SourceUrl ?? "");
    private static Task Cannot(string what) => Task.FromException(InstagramBackend.Refused($"Instagram in Message Link cannot {what}."));
    private static Task<T> Cannot<T>(string what) => Task.FromException<T>(InstagramBackend.Refused($"Instagram in Message Link cannot {what}."));

    public ServiceTag Tag => ServiceTags.Instagram;
    public bool OwnsMedia(MessageAttachment attachment) => InstagramIdentity.OwnsMedia(attachment);

    public bool AnyConnected => accounts()?.Instagrams.Any(i => i.IsConnected) ?? instagram.IsConnected;
    public bool IsConnected(string chatId) => Instagram(chatId).IsConnected;
    public bool IsScopeConnected(string scope) => scope == Tag.Prefix && instagram.IsConnected;
    public bool IsConfigured(string scope) => Instagram(scope).HasSavedSession;
    public bool NeedsSignInOnly(string scope) => false;

    // Instagram searches only what this session loaded, which paging back reaches. Its newest pages answer a held read.
    public bool CanReadWindow(string chatId) => false;
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken)
        => Task.FromException<HistoryWindow>(new NotSupportedException(RoutedWords.CantOpenAtMessage));
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Instagram(chatId).ReadHeldAsync(chatId, after, limit, cancellationToken);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => Instagram(chatId).GetChatAsync(chatId, cancellationToken);

    // A page Instagram's helper has just read hands its messages out for this session, so their pictures can now be
    // fetched (MediaNews): one waiting for exactly that asks again, and one refused after it is gone.
    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken)
    {
        var page = await Instagram(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken);
        foreach (var read in page.Items.GroupBy(message => message.ChatId))
            MessageLink.Presentation.Services.MediaNews.Read(read.Key, read.Select(message => message.Id));
        return page;
    }

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => Instagram(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken);
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult(Instagram(chatId).PeekMessage(messageId));
    // Instagram alone keeps some messages in part: the list's words for a photo or a share. A whole copy leaves those out.
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult(Instagram(chatId).PeekWholeMessage(messageId));
    public bool HoldsPartialCopies => true;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => Instagram(chatId).CanMarkReadAsync(chatId, cancellationToken);
    public bool TellsReads => true;
    // Every read is a request on the real account; Instagram announces only what arrives, and a poll names only the
    // newest of what arrived since the last.
    public HistoryFacts HistoryFacts { get; } = new(ReadsCostBudget: true, EchoesOwnSends: false, EventsNameEveryArrival: false);
    public bool TryTakeUnaskedRead(string chatId) => Instagram(chatId).TryTakeHistoryRead(DateTimeOffset.UtcNow);
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => Instagram(chatId).MarkReadAsync(chatId, messageId, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => Instagram(chatId).GetSendPermissionAsync(chatId, cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => Instagram(chatId).SendTextAsync(chatId, text, cancellationToken);
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken) => Instagram(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken);
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken) => Instagram(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken);
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Instagram(chatId).GetOptionsAsync(chatId, messageId, cancellationToken);
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken) => Instagram(chatId).ReplyAsync(chatId, messageId, text, cancellationToken);
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken) => Instagram(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken);
    // Instagram's own archive is not an operation this build performs, so the conversation is described without offering
    // to change it.
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => new(await Instagram(chatId).GetChatAsync(chatId, cancellationToken), false);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => Cannot<ChatSummary>("change what a conversation is");
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Cannot("delete a conversation");

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => Instagram(chatId).GetPermissionAsync(chatId, cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Instagram(chatId).GetFilePermissionAsync(chatId, file, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Instagram(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken);
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => Instagram(chatId).SendAsync(chatId, file, cancellationToken, caption);
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => false;
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken)
        => throw new NotSupportedException(RoutedWords.NoPhotoSet);
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;

    // Instagram photos and profile pictures persist in the same encrypted media cache as everything else, so a
    // conversation seen before draws them at once on the next launch, even before Instagram has reconnected, instead
    // of each helper session downloading them again. Instagram's own urls expire; the id does not.
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken)
        => shelf.KeptAsync(InstagramBackend.ImageCacheKey(attachment, inline), token => Instagram(attachment).ReadImageAsync(attachment, token, inline), cancellationToken);
    public async Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await Instagram(video).CopyToAsync(video, memory, cancellationToken);
        return memory.ToArray();
    }
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken)
        => Instagram(attachment).CopyToAsync(attachment, destination, cancellationToken);
    // Keyed by the clip's own id, which names the same clip in every helper session: without this each scroll past one
    // downloaded it again, a request of the user's real account every time.
    public string? ClipKey(MessageAttachment clip) => "instagram-clip:" + InstagramIdentity.NativeMedia(clip);
    public bool CanSave(MessageAttachment attachment) => Instagram(attachment).CanSave(attachment);
    // Never warmed ahead: every request is the user's real account.
    public string? WarmKey(MessageAttachment picture) => null;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => Instagram(accountId).GetContactOptionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => Instagram(accountId).FindContactsAsync(accountId, query, cancellationToken);
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => Instagram(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken);
    // The only service that cannot open a conversation without sending into it.
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => Instagram(contact.AccountId).StartDirectWithMessageAsync(contact, text, cancellationToken);
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContactPerson>?>(null);
    public ChatSummary? FindDirectChat(ContactPerson contact) => Instagram(contact.AccountId).FindDirectChat(contact);
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken) => Instagram(accountId).GetGroupOptionsAsync(accountId, cancellationToken);
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken)
        => Instagram(accountId).CreateGroupAsync(accountId, people, title, cancellationToken);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();

    public bool SendsTyping => true;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Instagram(chatId).SetTypingAsync(chatId, isTyping, cancellationToken);
    // Puts off telling the other end the typing stopped. A removed account can remain open in a client until its next refresh.
    public void NoteComposerActivity(string? chatId)
    {
        if (chatId is null || !Tag.Owns(chatId)) return;
        try { Instagram(chatId).NoteComposerActivity(chatId); }
        catch (MessageLink.Core.Backend.BackendException) { }
    }
    public IEnumerable<ITypingService> TypingSources => accounts()?.Instagrams ?? [instagram];
}
