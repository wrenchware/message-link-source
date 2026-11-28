using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public readonly record struct DatedPhoto(MessageAttachment Attachment, DateTimeOffset At, bool IsVideo = false);

public static class ThreadPhotos
{
    public static int IndexOf(IReadOnlyList<MessageAttachment> photos, MessageAttachment selected)
    {
        for (var i = 0; i < photos.Count; i++) if (ReferenceEquals(photos[i], selected)) return i;
        for (var i = 0; i < photos.Count; i++) if (AttachmentIdentity.SameMedia(photos[i], selected)) return i;
        return -1;
    }
    public static MessageAttachment[] Collect(IEnumerable<ChatMessage> messages) =>
        [.. Dated(messages).Select(photo => photo.Attachment)];

    // The same photos, each with the time of the message that carried it. A gallery groups by day and an attachment
    // has no time of its own, so the message's is the only one there is.
    public static DatedPhoto[] Dated(IEnumerable<ChatMessage> messages) =>
        [.. DatedMedia(messages).Where(item => !item.IsVideo)];

    // Photos and videos both. Collect and Dated stay photographs only, because the viewer walks a set of photographs
    // with arrow keys and a clip is not a thing to page past; the gallery is a different question, where a video
    // somebody sent is as much part of what was shared as a picture is.
    public static bool IsVideo(MessageAttachment a) =>
        a.Type == "video" || a.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    public static DatedPhoto[] DatedMedia(IEnumerable<ChatMessage> messages) => messages
        .Where(m => !m.IsDeleted && !m.IsHidden)
        .SelectMany(m => m.Attachments
            .Where(a => a.Link is null && !a.IsSticker && !a.IsVoiceNote && (PhotoPresentation.IsPhoto(a) || IsVideo(a)))
            .Select(a => new DatedPhoto(a, m.Timestamp, IsVideo(a))))
        .ToArray();
}
