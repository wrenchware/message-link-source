using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public sealed record AttachmentBatchItem(string MimeType, long Length);
public sealed record AttachmentTextPlan(bool UseCaption, bool SendSeparately);

public static class AttachmentBatchRules
{
    public const int ItemLimit = 10;
    public const long ItemBytesLimit = 50L * 1024 * 1024;
    public const long BatchBytesLimit = 100L * 1024 * 1024;
    public const long AllDraftBytesLimit = 200L * 1024 * 1024;

    public static string? Validate(int existingCount, long existingBytes, long allDraftBytes, IReadOnlyList<AttachmentBatchItem> additions)
    {
        if (additions.Count == 0) return "Choose at least one file.";
        if (existingCount + additions.Count > ItemLimit) return $"Attach up to {ItemLimit} photos at a time.";
        if (additions.Any(item => item.Length < 0 || item.Length > ItemBytesLimit)) return "Choose files under 50 MB each.";
        if (existingBytes + additions.Sum(item => item.Length) > BatchBytesLimit) return "Keep the photo set under 100 MB.";
        if (allDraftBytes + additions.Sum(item => item.Length) > AllDraftBytesLimit) return "Send or remove another attachment draft before adding these photos.";
        if (existingCount + additions.Count > 1 && additions.Any(item => !item.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            return "Choose photos for multiple selection. Other files can be sent one at a time.";
        return null;
    }

    // The type a picked or dropped file is staged and sent as. Windows names it from the registry's Content Type for the
    // extension, and a PC whose .heic/.heif carry only PerceivedType=image (seen 2026-11-10) gave iPhone photos no type at
    // all, so they went as files and a set of them was refused for multiple selection. A type Windows did name wins.
    public static string FileType(string name, string? reported)
    {
        if (!string.IsNullOrWhiteSpace(reported) && !reported.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) return reported;
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
            ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
            ".heic" => "image/heic", ".heif" => "image/heif", ".avif" => "image/avif",
            ".mp4" or ".m4v" => "video/mp4", ".mov" => "video/quicktime", ".3gp" => "video/3gpp", ".webm" => "video/webm",
            ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".aac" => "audio/aac", ".wav" => "audio/wav", ".ogg" or ".opus" => "audio/ogg",
            _ => "application/octet-stream",
        };
    }

    // Staged as a JPEG instead. Google Messages for Web sent a HEIC as an unreadable .bin (owner, 2026-11-10), and a JPEG
    // is the one photo format every service and phone takes.
    public static bool ConvertsToJpeg(string mimeType) =>
        mimeType.Equals("image/heic", StringComparison.OrdinalIgnoreCase) || mimeType.Equals("image/heif", StringComparison.OrdinalIgnoreCase);

    public static AttachmentTextPlan Text(int attachmentCount, bool hasText, bool captionSupported) =>
        !hasText ? new(false, false) : attachmentCount == 1 && captionSupported ? new(true, false) : new(false, true);

    // An unknown result may already be visible to the other person and must never remain in the retryable draft.
    public static bool RemoveSubmittedDraft(OutgoingState state) => state != OutgoingState.Rejected;
    public static bool ContinueBatch(OutgoingState state) => state is not (OutgoingState.Rejected or OutgoingState.Unknown);

    // A file or video on its way, drawn as its confirmed copy will be (2026-10-04): its card or play button, with any
    // caption as the message's words above it, the way a captioned photo has been drawn since 2026-09-24. It was a text
    // bubble ("Video", "Attachment" and the caption) that became a card when the send was confirmed, and the row changed
    // shape. The kind follows the providers' own mapping (video/, audio/, anything else a file), so the confirmed row
    // draws the same control; sourceUrl is the local copy, so its card can be saved while it is still sending.
    public static MessageAttachment PendingFile(string id, string? name, string mimeType, long size, string? sourceUrl, string? caption = null) =>
        new(string.IsNullOrWhiteSpace(name) ? "Attachment" : name, PendingKind(mimeType), mimeType, size, false, false, false, id, sourceUrl,
            Caption: string.IsNullOrEmpty(caption) ? null : caption);
    public static string PendingKind(string mimeType) =>
        mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video"
        : mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio" : "file";

    // Enter while an attachment uploads in this conversation (2026-10-04). It used to do nothing until every photo was
    // accepted. Now plain words are queued behind the upload (Outbox.SendAfter): out of the box at once, sent after the
    // attachments and anything already queued, and never before them, and given back unsent if one of them is not
    // accepted. A reply is not queued (a queued send carries none) and is said instead; an edit is saved as before; and
    // another conversation's upload leaves Enter as it was. Staged photos here mean Send is for them, as before.
    public enum UploadEnter { NotUploading, Queue, ReplyWaits, Ignore }
    public static UploadEnter WhileUploading(bool uploadingHere, bool busy, bool hasStagedPhoto, bool editing, bool hasReply, string text) =>
        !busy ? UploadEnter.NotUploading
        : !uploadingHere || hasStagedPhoto || editing || string.IsNullOrWhiteSpace(text) ? UploadEnter.Ignore
        : hasReply ? UploadEnter.ReplyWaits : UploadEnter.Queue;
    public const string ReplyWaitsForUpload = "Wait for the attachment to finish sending before sending a reply.";
    public const string QueuedTextKept = "An attachment didn't send, so the words written after it were kept here and not sent.";
}
