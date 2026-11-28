using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// Which of a message's attachments each kind of control in its bubble draws, in message order. The bubble builds its
// controls from these and, when a row is reused for the same message, hands each control the attachment at its place in
// the same list, so a refreshed file name or media type reaches the control that shows it.
public static class BubbleMedia
{
    public static bool IsVideo(MessageAttachment a) => a.Type == "video" || a.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    // A GIF delivered as a clip loops by itself; only real videos get a play button.
    public static MessageAttachment[] Clips(IEnumerable<MessageAttachment> attachments) => attachments.Where(a => a.IsGif && IsVideo(a)).ToArray();
    public static MessageAttachment[] Videos(IEnumerable<MessageAttachment> attachments) => attachments.Where(a => !a.IsGif && IsVideo(a)).ToArray();
    public static MessageAttachment[] Recordings(IEnumerable<MessageAttachment> attachments) => attachments.Where(a => a.Type == "audio").ToArray();
    public static MessageAttachment[] Files(IEnumerable<MessageAttachment> attachments) =>
        attachments.Where(a => a.Type != "audio" && !PhotoPresentation.IsPhoto(a) && !IsVideo(a)).ToArray();
}
