using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Google Messages' answers to the routers (IConversationService). Each call goes to the account its id names, the paired
// phone's own when it names none. Google can remove a message but has no way to edit one; the backend refuses that.
internal sealed class GoogleMessagesConversations(GoogleMessagesBackend google, GoogleMediaBackend media, Func<ProviderAccounts?> accounts) : IConversationService
{
    private GoogleMessagesBackend Google(string id) => accounts()?.Google(id) ?? google;
    private GoogleMediaBackend Media(string id) => accounts()?.GoogleMedia(id) ?? media;
    private GoogleMediaBackend Media(MessageAttachment attachment) => Media(attachment.Id ?? attachment.SourceUrl ?? "");

    public ServiceTag Tag => ServiceTags.GoogleMessages;
    // Not asked by the lookup, which gives Google every attachment nothing else claims (ConversationOwners.OfMedia).
    public bool OwnsMedia(MessageAttachment attachment) => Named(attachment.Id) || Named(attachment.SourceUrl);
    private static bool Named(string? value) => value is not null && (value.StartsWith("google-media:", StringComparison.Ordinal) || value.StartsWith("google-preview:", StringComparison.Ordinal));

    public bool AnyConnected => accounts()?.Googles.Any(g => g.CanReceive) ?? google.CanReceive;
    public bool IsConnected(string chatId) => Google(chatId).CanReceive;
    public bool IsScopeConnected(string scope) => scope == Tag.Prefix && google.CanReceive;
    public bool IsConfigured(string scope) => Google(scope).Files.SessionSaved;
    public bool NeedsSignInOnly(string scope) => Google(scope).SignInExpired;

    // A window around a moment is read from Google's archive, for a conversation it has crawled to the beginning.
    public bool CanReadWindow(string chatId) => Google(chatId).CanReadWindow(chatId);
    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken)
        => Google(chatId).CanReadWindow(chatId) ? Google(chatId).ReadWindowAsync(chatId, at, older, newer, cancellationToken)
            : Task.FromException<HistoryWindow>(new NotSupportedException(RoutedWords.CantOpenAtMessage));
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken) => Google(chatId).ReadHeldAsync(chatId, after, limit, cancellationToken);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken) => Google(chatId).GetChatAsync(chatId, cancellationToken);

    // A page Google's helper has just read hands its messages out for this session, so their pictures can now be fetched
    // (MediaNews): one waiting for exactly that asks again, and one refused after it is gone.
    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken)
    {
        var page = await Google(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken);
        foreach (var read in page.Items.GroupBy(message => message.ChatId))
            NativeBeeper.Presentation.Services.MediaNews.Read(read.Key, read.Select(message => message.Id));
        return page;
    }

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken)
        => Google(chatId) is ILinkPreviewLoader previews ? previews.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : Task.CompletedTask;
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken) => Google(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken);
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken) => ValueTask.FromResult(Google(chatId).PeekMessage(chatId, messageId));
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken) => PeekAsync(chatId, messageId, cancellationToken);
    public bool HoldsPartialCopies => false;
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken) => Google(chatId).CanMarkReadAsync(chatId, cancellationToken);
    public bool TellsReads => true;
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken) => Google(chatId).MarkReadAsync(chatId, messageId, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => Google(chatId).GetSendPermissionAsync(chatId, cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken) => Google(chatId).SendTextAsync(chatId, text, cancellationToken);
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken) => Google(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken);
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken) => Google(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken);
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken) => Google(chatId).GetOptionsAsync(chatId, messageId, cancellationToken);
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken) => Google(chatId).ReplyAsync(chatId, messageId, text, cancellationToken);
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken) => Google(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken);
    public Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken) => Google(chatId).GetSettingsAsync(chatId, cancellationToken);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken) => Google(chatId).SetAsync(chatId, setting, value, cancellationToken);
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken) => Google(chatId).DeleteChatAsync(chatId, cancellationToken);

    public Task<SendPermission> GetAttachmentPermissionAsync(string chatId, CancellationToken cancellationToken) => Media(chatId).GetPermissionAsync(chatId, cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Media(chatId).GetFilePermissionAsync(chatId, file, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken) => Media(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken);
    public Task<SendReceipt> SendFileAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken) => Media(chatId).SendAsync(chatId, file, cancellationToken, caption);
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => false;
    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken)
        => throw new NotSupportedException(RoutedWords.NoPhotoSet);

    // Google Messages sends each photo as its own message, and each copy carries its receipt's transaction id, so all of
    // them can be drawn when Send is pressed. Still photos only, as chosen together in the composer.
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files)
        => files.Count is >= 2 and <= NativeBeeper.Presentation.Services.AttachmentBatchRules.ItemLimit &&
            files.All(file => file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));

    // Google's media keeps its own cache (GoogleMediaBackend). A drawn video still that Google sent itself is noted, so
    // the log says how often it sends none.
    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, bool inline, IMediaShelf shelf, CancellationToken cancellationToken)
    {
        var bytes = await Media(attachment).ReadImageAsync(attachment, cancellationToken, inline);
        if (inline && attachment.Type == "video" && !attachment.IsGif && ProviderMedia.IsVideoStill(attachment.SourceUrl)) shelf.NoteProviderStill(attachment);
        return bytes;
    }

    public async Task<byte[]> ReadVideoAsync(MessageAttachment video, IMediaShelf shelf, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await Media(video).CopyToAsync(video, memory, cancellationToken);
        return memory.ToArray();
    }

    public Task CopyToAsync(MessageAttachment attachment, Stream destination, IMediaShelf shelf, CancellationToken cancellationToken)
        => Media(attachment).CopyToAsync(attachment, destination, cancellationToken);
    public string? ClipKey(MessageAttachment clip) => null;
    public bool CanSave(MessageAttachment attachment) => Media(attachment).CanSave(attachment);
    // By its id, a video's still under ":thumbnail", as GoogleMediaBackend keeps them.
    public string? WarmKey(MessageAttachment picture) => Media(picture).CanSave(picture) ? picture.Id + (picture.Type == "video" ? ":thumbnail" : "") : null;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken) => Google(accountId).GetContactOptionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken) => Google(accountId).FindContactsAsync(accountId, query, cancellationToken);
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken) => Google(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken);
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken) => throw new SendException(false, RoutedWords.OpensWithoutSending);
    // Only Google Messages' phone keeps a ranking of who this account writes to most.
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken) => Google(accountId).GetFrequentContactsAsync(accountId, cancellationToken);
    public ChatSummary? FindDirectChat(ContactPerson contact) => ((IDirectChatLookup)Google(contact.AccountId)).FindDirectChat(contact);
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken) => Google(accountId).GetGroupOptionsAsync(accountId, cancellationToken);
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken)
        => Google(accountId).CreateGroupAsync(accountId, people, title, cancellationToken);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken) => throw RoutedWords.NotMessengerGroup();

    public bool SendsTyping => true;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken) => Google(chatId).SetTypingAsync(chatId, isTyping, cancellationToken);
    // Every actual composer edit yields Google history indexing, in any conversation, on every account.
    public void NoteComposerActivity(string? chatId)
    {
        foreach (var account in accounts()?.Googles ?? [google]) account.NoteComposerActivity(chatId);
    }
    public IEnumerable<ITypingService> TypingSources => accounts()?.Googles ?? [google];
}
