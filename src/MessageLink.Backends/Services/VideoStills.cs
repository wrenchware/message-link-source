using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// The still behind a video's play button for a network that sent none (Google Messages often sends no thumbnail for a video,
// an iPhone's over RCS especially; Google Voice never does). The video is read whole the way playing it reads it, so
// pressing play afterwards finds it in the provider's own cache and downloads nothing, a frame is made from those bytes in
// memory (IVideoFrameExtractor), and that frame is kept in the protected media cache under its own key, so it is made once.
//
// Asked only by a tile that is on screen, and by nothing that sweeps ahead (MediaRouter.PrepareImageAsync does not come
// here). Concurrent asks for one video share one download and one extraction.
//
// Whom a frame is kept for is the caller's to say. In process it is the Google profile, as for every entry of the media cache. A
// client of a Messages Server makes its own frames (ClientVideoStills, 2026-10-12) and keeps them in a cache of its own under the
// server's id: its profile follows the server's Google pairing, and a server with Google unpaired would keep nothing.
internal sealed class VideoStills(IVideoFrameExtractor extractor, MessageLink.Storage.MediaDiskCache disk, Func<CancellationToken, Task<string?>> profileOf,
    Action<string>? log = null)
{
    public VideoStills(IVideoFrameExtractor extractor, MessageLink.Storage.MediaDiskCache disk, IAccessTokenStore tokens, Action<string>? log = null)
        : this(extractor, disk, token => MessageLink.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, token), log) { }

    // The longest side of a kept frame, and its JPEG quality: the conversation draws a still no larger than 840 by 640.
    internal const int LongEdge = 960;
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);
    // How long a video that gave no frame is left alone before it is tried again. It was downloaded and tried once; trying
    // it again each time its tile scrolls into view would decode a clip the platform cannot play, over and over.
    private static readonly TimeSpan GaveUpFor = TimeSpan.FromMinutes(10);
    private readonly Action<string> write = log ?? MessageLink.App.Services.AppLog.Write;
    private readonly SemaphoreSlim extraction = new(1);
    private readonly object gate = new();
    private readonly Dictionary<string, Flight> flights = [];
    private readonly Dictionary<string, DateTimeOffset> gaveUp = [];
    private readonly HashSet<string> told = [];

    private sealed class Flight
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<byte[]> Task = null!;
        public int Users;
    }

    /// <summary>The key a video's kept frame has in the media cache: apart from the video's own bytes and from a provider's thumbnail.</summary>
    internal static string FrameKey(MessageAttachment video) => video.Id + ":frame";

    /// <summary>
    /// The still the provider itself sent was used. One line per video and session, so the log says how often a network
    /// sends none without a line for every redraw of a tile.
    /// </summary>
    internal void NoteProvider(MessageAttachment video) => Tell(video, "provider", 0, 0, once: true);

    /// <summary>
    /// A JPEG of the video's still, from the kept frame when there is one, otherwise made from the video that
    /// <paramref name="readVideo"/> returns. Throws as an unavailable picture does (<c>media_unavailable</c>) when no frame
    /// can be made, and passes on a transient failure of the read (not connected, a network error) unchanged and unremembered.
    /// </summary>
    internal async Task<byte[]> ReadAsync(MessageAttachment video, Func<CancellationToken, Task<byte[]>> readVideo, CancellationToken cancellationToken)
    {
        var profile = await profileOf(cancellationToken);
        var key = FrameKey(video);
        if (profile is not null && await disk.ReadAsync(profile, key, cancellationToken, renewFor: Kept) is { } saved)
        {
            Tell(video, "frame", 0, 0, once: true);
            return saved;
        }
        var flightKey = (profile ?? "") + "\n" + key;
        Flight flight;
        lock (gate)
        {
            if (gaveUp.TryGetValue(flightKey, out var until) && until > DateTimeOffset.UtcNow) throw Unavailable();
            if (!flights.TryGetValue(flightKey, out flight!))
            {
                flight = new();
                flights[flightKey] = flight;
                flight.Task = MakeAsync(video, profile, key, flightKey, readVideo, flight.Cancellation.Token);
            }
            flight.Users++;
        }
        try { return await flight.Task.WaitAsync(cancellationToken); }
        finally
        {
            lock (gate)
            {
                if (--flight.Users == 0)
                {
                    if (flights.GetValueOrDefault(flightKey) == flight) flights.Remove(flightKey);
                    // Nobody is waiting for it any more: a tile scrolled away before the video arrived. Let it go.
                    if (!flight.Task.IsCompleted) flight.Cancellation.Cancel();
                    _ = RetireAsync(flight);
                }
            }
        }
    }

    private static async Task RetireAsync(Flight flight)
    {
        try { await flight.Task; } catch { /* Observed so that a late failure is not unhandled. */ }
        finally { flight.Cancellation.Dispose(); }
    }

    private async Task<byte[]> MakeAsync(MessageAttachment video, string? profile, string key, string flightKey,
        Func<CancellationToken, Task<byte[]>> readVideo, CancellationToken token)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        long videoBytes = 0;
        byte[]? frame = null;
        var tried = false;
        try
        {
            var bytes = await readVideo(token);
            videoBytes = bytes.LongLength;
            if (bytes.Length > 0)
            {
                await extraction.WaitAsync(token);
                try
                {
                    tried = true;
                    frame = await extractor.ExtractJpegAsync(bytes, video.MimeType ?? "video/mp4", LongEdge, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { frame = null; }
                finally { extraction.Release(); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) when (!Permanent(failure)) { throw; }
        catch (Exception) { /* The provider says there is no such video: nothing to make a frame from. */ }
        if (frame is not { Length: > 0 })
        {
            // A clip that was downloaded and gave no frame is not tried again for a while. One the provider could not
            // produce is asked about again, since "unavailable" also means a message not yet handed out to the helper.
            if (tried)
                lock (gate)
                {
                    if (gaveUp.Count >= 512) gaveUp.Clear();
                    gaveUp[flightKey] = DateTimeOffset.UtcNow + GaveUpFor;
                }
            Tell(video, "none", System.Diagnostics.Stopwatch.GetElapsedTime(began), videoBytes, once: !tried);
            throw Unavailable();
        }
        if (profile is not null)
        {
            try { await disk.WriteAsync(profile, key, frame, Kept, CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* Only the next launch makes it again. */ }
        }
        Tell(video, "frame", System.Diagnostics.Stopwatch.GetElapsedTime(began), videoBytes, once: false);
        return frame;
    }

    // What a read says when the video is not there to be had, as against failing for a moment.
    private static bool Permanent(Exception failure) => failure is InvalidOperationException { Message: "media_unavailable" }
        or BackendException { Failure: BackendFailure.Unsupported };

    private static InvalidOperationException Unavailable() => new("media_unavailable");

    // Fixed words and numbers only: never an identifier, a name, a type or anything from the clip.
    private void Tell(MessageAttachment video, string source, TimeSpan took, long videoBytes, bool once)
        => Tell(video, source, (long)took.TotalMilliseconds, videoBytes / 1024, once);

    private void Tell(MessageAttachment video, string source, long milliseconds, long videoKilobytes, bool once)
    {
        var provider = MessageLink.App.Services.ProviderMedia.DiagnosticFor(video.Id);
        lock (gate)
        {
            // A decision made now is remembered too, so the cache hits that follow it are not told again.
            if (told.Count >= 4096) told.Clear();
            if (!told.Add(video.Id + "\n" + source) && once) return;
        }
        write($"media video still provider={provider} source={source} ms={milliseconds} video_kb={videoKilobytes}");
    }
}
