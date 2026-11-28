using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public enum SharedContentKind { Media, Links, Files, Audio }
public sealed record SharedContentItem(ChatMessage Message, MessageAttachment? Attachment, Uri? Link, string Title);

// A read-only projection of available history. A linked preview is a link, not a second photograph.
public static class SharedContent
{
    public static bool IsAudio(MessageAttachment attachment) => attachment.IsVoiceNote ||
        attachment.Type is "audio" or "voice" || attachment.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    public static IReadOnlyList<SharedContentItem> Collect(IEnumerable<ChatMessage> messages, SharedContentKind kind)
    {
        var result = new List<SharedContentItem>();
        foreach (var message in messages.Where(m => !m.IsDeleted && !m.IsHidden).DistinctBy(MessageIdentity.Key)
                     .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.SortKey, StringComparer.Ordinal))
        {
            var links = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attachment in message.Attachments)
            {
                if (attachment.Link is { } target)
                {
                    if (kind == SharedContentKind.Links && MessageText.WebLink(target) is { } link && links.Add(link.AbsoluteUri))
                        result.Add(new(message, attachment, link, attachment.Name ?? link.Host));
                    continue;
                }
                if (attachment.IsSticker) continue;
                var actual = IsAudio(attachment) ? SharedContentKind.Audio :
                    PhotoPresentation.IsPhoto(attachment) || ThreadPhotos.IsVideo(attachment) ? SharedContentKind.Media : SharedContentKind.Files;
                if (actual == kind) result.Add(new(message, attachment, null, attachment.Name ?? (actual == SharedContentKind.Audio ? "Audio" : "Attachment")));
            }
            if (kind != SharedContentKind.Links || string.IsNullOrWhiteSpace(message.Text)) continue;
            foreach (var run in MessageText.Parse(MessageText.Markup(message)))
                if (run.Link is { } link && MessageText.IsWeb(link) && links.Add(link.AbsoluteUri)) result.Add(new(message, null, link, link.Host));
        }
        return result;
    }
}
