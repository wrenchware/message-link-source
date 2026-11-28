using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class PhotoPresentation
{
    public static bool IsPhoto(MessageAttachment a) => a.Type == "img" || a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    public static (double Width, double Height) StripTile(MessageAttachment photo)
    {
        if (photo.Width is not > 0 || photo.Height is not > 0 ||
            !double.IsFinite(photo.Width.Value) || !double.IsFinite(photo.Height.Value)) return (220, 220);
        var aspect = photo.Height.Value / photo.Width.Value;
        if (!double.IsFinite(aspect) || aspect <= 0) return (220, 220);
        // The small overlapping set shows whole pictures. Its tile must share the picture's
        // aspect ratio, rather than centering a short screenshot in a 140px-tall gray box.
        return aspect <= 280d / 220 ? (220, 220 * aspect) : (280 / aspect, 280);
    }
    // Provider message text remains authoritative. Filenames and link descriptions are not captions.
    public static string Captions(ChatMessage message)
    {
        if (message.IsDeleted || message.IsHidden) return "";
        var body = MessageText.PlainText(message.Text ?? "").Trim();
        return string.Join("\n", message.Attachments.Select(a => a.Caption?.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c) && !body.Contains(MessageText.PlainText(c!), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal));
    }

    public static (int Column, int Row, int ColumnSpan, int RowSpan) Cell(int count, int index)
    {
        if (count == 3) return index == 0 ? (0, 0, 1, 2) : (1, index - 1, 1, 1);
        var columns = count <= 4 ? 2 : 3;
        return (index % columns, index / columns, 1, 1);
    }
}
