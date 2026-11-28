namespace MessageLink.Core.Services;

/// <summary>Decodes a photo in any format the platform can read, so it can be written out again as a smaller JPEG.</summary>
public interface IPhotoReencoder
{
    /// <summary>
    /// The photo upright (its EXIF orientation applied) and in sRGB, or null when it has no pixels or more than
    /// <paramref name="maxPixels"/>. Throws when the platform cannot read it.
    /// </summary>
    Task<IDecodedPhoto?> DecodeAsync(byte[] image, ulong maxPixels, CancellationToken cancellationToken);
}

/// <summary>A decoded photo, held until disposed.</summary>
public interface IDecodedPhoto : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>A JPEG of the photo scaled down so its longer side is <paramref name="longEdge"/> (not scaled up).</summary>
    Task<byte[]> EncodeJpegAsync(int longEdge, float quality);
}
