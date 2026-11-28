using MessageLink.Core.Services;
using SkiaSharp;

namespace MessageLink.Server.Linux;

/// <summary>
/// The Linux server's <see cref="IPhotoReencoder"/>, with the guarantees of the Windows one (WindowsPhotoReencoder): the photo upright,
/// in sRGB, measured from its header before any pixel is decoded, and written out as a JPEG that carries none of the original's metadata (no EXIF, GPS,
/// XMP, comment or ICC profile), never scaled up. Skia reads JPEG, PNG, GIF (its first frame, as Windows does), BMP, WebP, ICO and WBMP; it cannot read
/// TIFF or HEIC, which then fail to prepare rather than send.
/// </summary>
/// <remarks>
/// Failures are <see cref="InvalidDataException"/>, never <see cref="InvalidOperationException"/>: GoogleVoicePhotos shows an
/// InvalidOperationException's message to the person as the refusal, and these messages are not written for them.
/// </remarks>
internal sealed class SkiaPhotoReencoder : IPhotoReencoder
{
    public Task<IDecodedPhoto?> DecodeAsync(byte[] image, ulong maxPixels, CancellationToken cancellationToken) =>
        Task.Run(() => Decode(image, maxPixels, cancellationToken), cancellationToken);

    private static IDecodedPhoto? Decode(byte[] image, ulong maxPixels, CancellationToken cancellationToken)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Not an image this host can read.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (ulong)info.Width * (ulong)info.Height > maxPixels) return null;
        cancellationToken.ThrowIfCancellationRequested();

        // Decoded straight into sRGB (Skia converts by the embedded ICC profile, as ColorManageToSRgb does); premultiplied when it has alpha, so it
        // can be drawn onto white below.
        var opaque = info.AlphaType == SKAlphaType.Opaque;
        var decodedInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        SKBitmap? decoded = new(decodedInfo);
        try
        {
            // A file that ends early (IncompleteInput) is kept with its missing part filled, as Windows' decoder keeps it (measured 2026-10-09: a
            // JPEG cut in half or to its first 700 bytes, one without its end marker, a PNG cut in half). Skia's JPEG reader reports even a missing
            // end marker alone as incomplete, so refusing these would refuse photos the Windows app sends.
            if (codec.GetPixels(decodedInfo, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException("The image could not be decoded.");
            cancellationToken.ThrowIfCancellationRequested();

            // Most photos are opaque and stored upright: they are kept as decoded, so a large one is held once rather than twice.
            var origin = codec.EncodedOrigin;
            if (opaque && origin == SKEncodedOrigin.TopLeft) { var kept = decoded; decoded = null; return new Decoded(kept); }

            var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            // Opaque on white: a JPEG has no alpha, and dropping alpha instead would show whatever colour an editor left under erased (transparent) pixels.
            var upright = new SKBitmap(new SKImageInfo(turned ? info.Height : info.Width, turned ? info.Width : info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque,
                SKColorSpace.CreateSrgb()));
            try
            {
                using var canvas = new SKCanvas(upright);
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(Upright(origin, info.Width, info.Height));
                // Nearest: a quarter turn or a mirror lands every pixel exactly on one, so nothing is blended.
                canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
                canvas.Flush();
                return new Decoded(upright);
            }
            catch { upright.Dispose(); throw; }
        }
        finally { decoded?.Dispose(); }
    }

    // The EXIF orientation as a drawing transform from the stored pixels (width x height) to the upright picture.
    private static SKMatrix Upright(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private sealed class Decoded(SKBitmap bitmap) : IDecodedPhoto
    {
        public int Width => bitmap.Width;
        public int Height => bitmap.Height;

        public Task<byte[]> EncodeJpegAsync(int longEdge, float quality) => Task.Run(() =>
        {
            var current = Math.Max(bitmap.Width, bitmap.Height);
            SKBitmap? scaled = null;
            try
            {
                if (longEdge < current)
                {
                    var scale = (double)longEdge / current;
                    var info = bitmap.Info.WithSize((int)Math.Max(1, Math.Round(bitmap.Width * scale)), (int)Math.Max(1, Math.Round(bitmap.Height * scale)));
                    // Trilinear (mipmapped) so a large reduction averages its pixels, as Windows' Fant does, rather than skipping them.
                    scaled = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                        ?? throw new InvalidDataException("The image could not be scaled.");
                }
                // Encoded through a view that names no colour space: the pixels are already sRGB, and an image that named one would have the
                // encoder embed an ICC profile, which the Windows encoder does not write.
                var source = scaled ?? bitmap;
                using var image = SKImage.FromPixels(new SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType), source.GetPixels(), source.RowBytes)
                    ?? throw new InvalidDataException("The image could not be encoded.");
                using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp((int)Math.Round(quality * 100), 0, 100))
                    ?? throw new InvalidDataException("The image could not be encoded.");
                return jpeg.ToArray();
            }
            finally { scaled?.Dispose(); }
        });

        public void Dispose() => bitmap.Dispose();
    }
}
