using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// Google Voice carries a photo inside the text message, up to 2 MB, as JPEG, PNG, GIF, BMP or TIFF (upstream's limits).
// No photo goes with its location (decided, SWITCHBOARD.md "Decisions"). A JPEG, PNG, GIF or BMP that
// already fits goes with its picture untouched and its metadata taken out (PhotoMetadata). Anything larger, a TIFF, a
// file that cannot be read that way, or another image format the platform can read (HEIC, WebP), goes as a JPEG made
// smaller until it fits, which the re-encoder writes with no metadata at all. An animated GIF cannot be made smaller
// without losing the animation, so a GIF over the limit is refused instead.
// What may be sent at all is decided in GoogleVoicePhotos.Rules.cs, which has no Windows types and is checked offline.
internal static partial class GoogleVoicePhotos
{
    private static readonly string[] Sendable = ["image/jpeg", "image/png", "image/gif", "image/bmp", "image/tiff"];
    private static readonly int[] LongEdges = [2048, 1600, 1280, 1024, 800];

    public static async Task<(byte[] Bytes, string MimeType)> PrepareAsync(AttachmentFile file, IPhotoReencoder reencoder, CancellationToken cancellationToken)
    {
        if (Refusal(file) is { } reason) throw new InvalidOperationException(reason);
        byte[] original;
        await using (var stream = await file.OpenRead(cancellationToken))
        using (var copy = new MemoryStream())
        {
            await stream.CopyToAsync(copy, cancellationToken);
            if (copy.Length != file.Length || copy.Length > SourceLimit) throw new IOException("File changed.");
            original = copy.ToArray();
        }
        var mime = file.MimeType.ToLowerInvariant();
        if (original.Length <= Limit && Sendable.Contains(mime))
        {
            // The orientation block kept for a turned photo can make it a few bytes larger, so the limit is checked again.
            if (PhotoMetadata.Strip(original, mime) is { } clean && clean.Bytes.Length <= Limit)
            {
                GoogleVoiceLog.Write($"voice photo metadata stripped=yes kind={clean.Kind} bytes_removed={original.Length - clean.Bytes.Length} orientation={(clean.Turned ? "kept" : "none")}");
                return (clean.Bytes, mime);
            }
            // Never sent as it is instead. A GIF that cannot be read to the end is broken anyway, and loses its animation
            // rather than going with whatever it carries.
            GoogleVoiceLog.Write($"voice photo metadata stripped=no kind={mime[6..]} reencoding=yes");
        }
        else if (mime == "image/gif") throw new InvalidOperationException("Google Voice sends GIFs up to 2 MB.");
        IDecodedPhoto? decoded;
        try { decoded = await reencoder.DecodeAsync(original, 64_000_000, cancellationToken); }
        // A TIFF is never sent as it is, and the Linux server's decoder (Skia) cannot read one at all, so say what to do
        // rather than that it failed.
        catch (Exception failure) when (mime == "image/tiff" && failure is not OperationCanceledException)
        { throw new InvalidOperationException(TiffUnreadable); }
        using var photo = decoded ?? throw new InvalidOperationException("That photo is too large to prepare.");
        var longEdge = Math.Max(photo.Width, photo.Height);
        foreach (var edge in LongEdges.Prepend(longEdge).Where(edge => edge <= longEdge).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jpeg = await photo.EncodeJpegAsync(edge, 0.85f);
            if (jpeg.Length > Limit) continue;
            GoogleVoiceLog.Write($"voice photo reencoded metadata=none scaled={(edge < longEdge ? "yes" : "no")}");
            return (jpeg, "image/jpeg");
        }
        throw new InvalidOperationException("That photo could not be made small enough for Google Voice.");
    }
}
