using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host;

/// <summary>
/// The sends that do not go through the whole history's own ports (files, and a first message that makes its conversation), each read back into the copy
/// once it is made (SWITCHBOARD.md stage 9, A1): Instagram announces only what arrives, so a photo sent from here reached no copy and no client drawing from
/// one. Everything else passes straight through.
/// </summary>
internal sealed class HistoryReadBack(IAttachmentBackend attachments, IAttachmentBatchBackend batches, IFirstMessageStarter? starter, ConversationHistory history)
    : IAttachmentBackend, IAttachmentBatchBackend, IFirstMessageStarter
{
    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default) => attachments.GetPermissionAsync(chatId, cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        attachments.GetFilePermissionAsync(chatId, file, cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        attachments.GetCaptionSupportAsync(chatId, file, cancellationToken);
    public bool CanSave(MessageAttachment attachment) => attachments.CanSave(attachment);
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default) =>
        attachments.CopyToAsync(attachment, destination, cancellationToken);

    public async Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
    {
        var receipt = await attachments.SendAsync(chatId, file, cancellationToken, caption).ConfigureAwait(false);
        history.ReadBackSent(chatId);
        return receipt;
    }

    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => batches.CanSendBatch(chatId, files);
    public Task<bool> CanSendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default) =>
        batches.CanSendBatchAsync(chatId, files, cancellationToken);
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => batches.DrawsPhotosAtOnce(chatId, files);

    public async Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
    {
        var receipt = await batches.SendBatchAsync(chatId, files, cancellationToken).ConfigureAwait(false);
        history.ReadBackSent(chatId);
        return receipt;
    }

    public int MaxFirstMessageLength => (starter ?? throw new NotSupportedException()).MaxFirstMessageLength;

    public async Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default)
    {
        var chat = await (starter ?? throw new NotSupportedException()).StartDirectWithMessageAsync(contact, text, cancellationToken).ConfigureAwait(false);
        history.Started(chat.Id);
        return chat;
    }
}
