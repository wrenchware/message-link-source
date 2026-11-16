using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

// A photo read from a message for Forward, ready to stage in another conversation's composer as a pasted photo is.
public sealed record ForwardedPhoto(byte[] Bytes, string MimeType, string Name);

// Forward with photos (2026-10-04). Forward has put a message's text in the chosen conversation's composer since it was
// built; a message's photos now go with it, staged in that composer beside the text exactly as dropped photos are, and
// sent, if the user presses Send, by that conversation's own photo send. Nothing here sends, on any service: Instagram
// included, whose composer it fills the same way. The photos are read from the message once the user has chosen where
// they go — reading a received one can fetch it from the service, which is why only Forward's own choice does it — and
// only photos the service can hand over now (IAttachmentBackend.CanSave); unknown is unavailable.
public static class ForwardContent
{
    // The pictures a message forwards: its own photos, or every photo of the set when it stands for a set drawn as one
    // picture (MessageItemViewModel.ActionMessage). Stickers and link previews are not photos the user sent, and are left;
    // at most as many as a composer takes at once (AttachmentBatchRules.ItemLimit), in the order they were shown.
    public static IReadOnlyList<MessageAttachment> Photos(ChatMessage message, IReadOnlyList<ChatMessage> set)
    {
        if (message.IsDeleted || message.IsHidden) return [];
        var from = set.Count > 1 && set[0].Id == message.Id ? set : [message];
        return [.. from.Where(member => !member.IsDeleted && !member.IsHidden).SelectMany(member => member.Attachments)
            .Where(attachment => PhotoPresentation.IsPhoto(attachment) && !attachment.IsSticker && attachment.Link is null)
            .Take(AttachmentBatchRules.ItemLimit)];
    }

    // Where Forward and Share may put something: never a Hermes thread (the owner's privacy line, 2026-11-14), which gets only what is typed
    // or sent in it. Forwarding out of one is the person's own choice and stays.
    public static bool IsTarget(ChatSummary chat) => !HermesThreads.Is(chat);

    // The words that go with them: the message's text as plain text, or none.
    public static string? Text(ChatMessage message) =>
        message.IsDeleted || message.IsHidden || string.IsNullOrWhiteSpace(message.Text) ? null : MessageText.PlainText(message);

    // What the picture really is, by its first bytes, over what the service called it: a staged photo is sent with this
    // type, and a service told the wrong one can refuse the picture or show it broken. Null when it is not a picture the
    // composer takes, which leaves it out of the forward rather than sending something unknown.
    public static string? MimeType(byte[] bytes, string? declared)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
        // HEIC and the rest have no simple signature here: trusted only when the service said it was a picture.
        return declared?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? declared.ToLowerInvariant() : null;
    }

    // A name with the extension its type has, as the service will be given: a received photo is often named only
    // "Photo", or by an id, and a name that says .png over a JPEG misleads whoever saves it.
    public static string FileName(string? name, string mimeType)
    {
        var extension = mimeType switch
        {
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp",
            "image/heic" => ".heic", "image/heif" => ".heif", _ => ".jpg",
        };
        var stem = string.IsNullOrWhiteSpace(name) ? "Photo" : Path.GetFileNameWithoutExtension(name.Trim());
        if (string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) stem = "Photo";
        return stem + extension;
    }
}
