using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

// IsVoiceNote: recorded here as a voice message, so a service that draws one differently from an audio file (Messenger,
// Instagram) can be told. DurationSeconds: how long that recording runs, for a service that carries it with the message
// (Messenger's encrypted conversations); null for any other file. Both appended: built positionally.
public sealed record AttachmentFile(string Name, string MimeType, long Length, Func<CancellationToken, Task<Stream>> OpenRead,
    bool IsVoiceNote = false, double? DurationSeconds = null);
public sealed record AttachmentCaptionSupport(bool Supported, int? MaxLength);
public interface IAttachmentBackend
{
    Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default);
    Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default);
    Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default);
    Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null);
    bool CanSave(MessageAttachment attachment);
    Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default);
}

// Some providers can put several selected photos into one native message. Keep this separate from the one-file
// contract so providers without an album operation retain their established send behavior.
public interface IAttachmentBatchBackend
{
    bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files);
    // The same question for a caller that can wait. The answer depends on the conversation's own permission, which a client of a
    // remote host cannot read without asking, so the window asks this one (a proxy carries it over the wire); a port that answers from
    // memory needs nothing more than the default.
    Task<bool> CanSendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default) =>
        Task.FromResult(CanSendBatch(chatId, files));
    Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default);
    // A provider with no album operation whose photos, sent one at a time, can all be drawn the moment Send is pressed
    // (Outbox.SendEachAsync), with the words written with them under them (Outbox.SendWhenReady): each receipt names its
    // message by its own id or by an id its copy carries (ChatMessage.PendingId), so every copy is known exactly. Off
    // unless a provider says so.
    bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => false;
}
