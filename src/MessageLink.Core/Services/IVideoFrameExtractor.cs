namespace MessageLink.Core.Services;

/// <summary>
/// Makes a still picture from a video the platform can play, for the tile that stands for it when its network sent none.
/// The video and the picture stay in memory: nothing is written to disk, since every other store here is protected.
/// </summary>
public interface IVideoFrameExtractor
{
    /// <summary>
    /// A JPEG of one frame from near the start of <paramref name="video"/>, upright and scaled down so its longer side is
    /// at most <paramref name="longEdge"/>, or null when no frame could be made: a format the platform cannot play, a clip
    /// with no picture, or too long a wait. Never throws for those; only cancellation does.
    /// </summary>
    Task<byte[]?> ExtractJpegAsync(byte[] video, string mimeType, int longEdge, CancellationToken cancellationToken);
}
