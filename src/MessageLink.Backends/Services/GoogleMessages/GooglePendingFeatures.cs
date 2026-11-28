using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Explicit capabilities for features not yet ported. Never delegates to another network.
internal sealed class GooglePendingFeatures : IAttachmentBackend, IReadStateWriter,
    IMediaReader, IMessageActions, IMessageMutations, ISearchBackend, IContactBackend, IGroupBackend
{
    private const string Reason = "Google Messages doesn't offer this here.";
    private static BackendException Unavailable() => new(BackendFailure.Unsupported, Reason);
    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default) => Task.FromResult(new SendPermission(false, Reason));
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) => GetPermissionAsync(chatId, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) => Task.FromResult(new AttachmentCaptionSupport(false, null));
    public Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null) => throw Unavailable();
    public bool CanSave(MessageAttachment attachment) => false;
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false) => throw Unavailable();
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default) => Task.FromResult(new MessageActionOptions(false, [], []));
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(new ContactOptions(false, false, Reason));
    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(new GroupOptions(false, Reason));
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default) => throw Unavailable();
}
