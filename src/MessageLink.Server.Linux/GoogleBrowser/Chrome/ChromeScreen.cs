using System.Runtime.InteropServices;
using MessageLink.Protocol;
using SkiaSharp;

namespace MessageLink.Server.Linux;

/// <summary>Turns a display's pixels into a picture the client can show.</summary>
internal interface IFrameEncoder
{
    /// <summary>A JPEG of <paramref name="bgra"/> (tightly packed B, G, R, unused), as base64, or null when it cannot be made.</summary>
    string? Encode(byte[] bgra, int width, int height, int quality);
}

/// <summary>The real encoder: the SkiaSharp the server already carries for photos.</summary>
internal sealed class SkiaFrameEncoder : IFrameEncoder
{
    public string? Encode(byte[] bgra, int width, int height, int quality)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            // The fourth byte of an X pixel is unused, not alpha: the picture is opaque.
            using var image = SKImage.FromPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), pinned.AddrOfPinnedObject(), width * 4);
            if (image is null) return null;
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            return jpeg is null ? null : Convert.ToBase64String(jpeg.AsSpan());
        }
        finally { pinned.Free(); }
    }
}

/// <summary>
/// What the person sees while they sign in to Google on the server's Chrome (SWITCHBOARD.md 5.4): the whole display, captured from the X server, as a JPEG, about ten times a second but only when it
/// changed (and once every few seconds regardless, so a client that missed one is not left with an old picture). The pictures go to the output as they are made and are never decoded again, written,
/// logged or kept; the output keeps only the newest. What a picture shows (an address, a code, a password field) is the person's business, so nothing here counts or names it.
/// </summary>
internal sealed class ChromeScreenStream(IXDisplay display, IFrameEncoder encoder, IRemoteSignInOutput output)
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>A picture is sent this often even when nothing changed.</summary>
    public TimeSpan Refresh { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Pictures sent, for the run's one log line. A count; it says nothing of what was shown.</summary>
    public int Frames => Volatile.Read(ref frames);
    private int frames;

    /// <summary>The qualities tried, best first, until the picture fits the protocol's size.</summary>
    internal static readonly int[] Qualities = [70, 45, 25];
    internal const int MaxMisses = 50;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ulong last = 0;
        var haveLast = false;
        var misses = 0;
        var sentAt = long.MinValue;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            byte[]? pixels;
            try { pixels = display.Capture(); }
            catch (IOException) { return; }
            // A display that answers nothing for this many looks in a row is gone, as one that raises an error is: the stream ends, and the sign-in with it.
            if (pixels is null) { if (++misses >= MaxMisses) return; continue; }
            misses = 0;
            var hash = Hash(pixels);
            var due = !haveLast || hash != last || sentAt == long.MinValue || System.Diagnostics.Stopwatch.GetElapsedTime(sentAt) >= Refresh;
            if (!due) continue;
            if (Send(pixels)) { last = hash; haveLast = true; sentAt = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }
        while (await NextAsync(timer, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> NextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try { return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private bool Send(byte[] pixels)
    {
        foreach (var quality in Qualities)
        {
            var jpeg = encoder.Encode(pixels, display.Width, display.Height, quality);
            if (jpeg is null || jpeg.Length > RemoteSignIn.MaxFrameCharacters) continue;
            Interlocked.Increment(ref frames);
            output.Frame(new RemoteSignInFrame(jpeg, display.Width, display.Height));
            return true;
        }
        return false;
    }

    // A cheap hash of every pixel, to tell a changed display from an unchanged one; it is compared and dropped.
    internal static ulong Hash(byte[] pixels)
    {
        var hash = 14695981039346656037UL;
        foreach (var word in MemoryMarshal.Cast<byte, ulong>(pixels.AsSpan(0, pixels.Length / 8 * 8))) hash = (hash ^ word) * 1099511628211UL;
        for (var i = pixels.Length / 8 * 8; i < pixels.Length; i++) hash = (hash ^ pixels[i]) * 1099511628211UL;
        return hash;
    }
}
