using MessageLink.Core.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MessageLink.WindowsPlatform;

// Windows imaging reads JPEG, PNG, GIF, BMP, TIFF and, with its codecs, HEIC and WebP.
public sealed class WindowsPhotoReencoder : IPhotoReencoder
{
    public async Task<IDecodedPhoto?> DecodeAsync(byte[] image, ulong maxPixels, CancellationToken cancellationToken)
    {
        var source = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(image);
                await writer.StoreAsync().AsTask(cancellationToken);
                writer.DetachStream();
            }
            var decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > maxPixels)
            { source.Dispose(); return null; }
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
            return new Decoded(source, bitmap);
        }
        catch (Exception) { source.Dispose(); throw; }
    }

    private sealed class Decoded(InMemoryRandomAccessStream source, SoftwareBitmap bitmap) : IDecodedPhoto
    {
        public int Width => bitmap.PixelWidth;
        public int Height => bitmap.PixelHeight;

        public async Task<byte[]> EncodeJpegAsync(int longEdge, float quality)
        {
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded,
                [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(quality, PropertyType.Single))]);
            encoder.SetSoftwareBitmap(bitmap);
            var current = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            if (longEdge < current)
            {
                var scale = (double)longEdge / current;
                encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(bitmap.PixelWidth * scale));
                encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(bitmap.PixelHeight * scale));
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            await encoder.FlushAsync();
            var bytes = new byte[checked((int)encoded.Size)];
            using var reader = new DataReader(encoded.GetInputStreamAt(0));
            await reader.LoadAsync((uint)bytes.Length);
            reader.ReadBytes(bytes);
            return bytes;
        }

        public void Dispose()
        {
            bitmap.Dispose();
            source.Dispose();
        }
    }
}
