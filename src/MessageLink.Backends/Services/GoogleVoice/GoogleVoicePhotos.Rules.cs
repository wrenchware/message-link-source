using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// Google Voice's send call carries a picture and nothing else (libgv's ReqSendSMS media types are JPEG, PNG, TIFF, GIF,
// BMP and WebP; upstream's bridge declares images only). So anything that is not an image, a recorded voice message or a
// chosen audio file among them, is refused here before a byte is staged, and the helper's ui.sendMedia refuses it again.
internal static partial class GoogleVoicePhotos
{
    public const int Limit = 2 * 1024 * 1024;
    private const long SourceLimit = 50 * 1024 * 1024;
    public const string PhotosOnly = "Google Voice sends photos only.";
    public const string TiffUnreadable = "This TIFF can't be prepared for Google Voice. Save it as a JPEG or PNG and send that.";

    // Why this file cannot be sent with Google Voice, or null when it can (possibly after being made smaller).
    public static string? Refusal(AttachmentFile file) => file switch
    {
        { Length: <= 0 } => "That file is empty.",
        { Length: > SourceLimit } => "Choose a photo under 50 MB.",
        _ when !file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => PhotosOnly,
        _ when string.Equals(file.MimeType, "image/gif", StringComparison.OrdinalIgnoreCase) && file.Length > Limit => "Google Voice sends GIFs up to 2 MB.",
        _ => null,
    };
}
