using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// <c>media.prepare</c> as a client asks it (SWITCHBOARD.md 3.0b): the host warms a picture or a GIF clip into its media cache and nothing comes
/// back. One request per call and never repeated here; a call that fails is the caller's ordinary failure (the prefetch tries nothing again, and
/// the conversation reads the picture itself when it is drawn).
/// </summary>
public sealed class MediaPrepareProxy(ProtocolClient client) : IMediaPreparer
{
    public async Task<long?> PrepareImageAsync(MessageAttachment picture, CancellationToken cancellationToken = default) =>
        (await client.CallAsync<MediaPrepareParams, MediaPrepared>(ProtocolMethods.MediaPrepare, new(picture), cancellationToken).ConfigureAwait(false)).Bytes;

    public async Task<bool> PrepareClipAsync(MessageAttachment clip, long limit, CancellationToken cancellationToken = default) =>
        (await client.CallAsync<MediaPrepareParams, MediaPrepared>(ProtocolMethods.MediaPrepare, new(clip, limit), cancellationToken).ConfigureAwait(false)).Fetched;
}

/// <summary><c>media.requestCard</c> as a client asks it: the client's setting and its list's view of the conversation go with the request, the host's rules decide.</summary>
public sealed class IncomingCardsProxy(ProtocolClient client) : IIncomingCards
{
    public Task RequestAsync(string chatId, string messageId, bool linkPreviews, bool? listedAsSpam, CancellationToken cancellationToken = default) =>
        client.CallAsync<RequestCardParams, Done>(ProtocolMethods.MediaRequestCard, new(chatId, messageId, linkPreviews, listedAsSpam), cancellationToken);
}

/// <summary>The host's catch-ups (the <c>caughtUp</c> kind of its event feed) as the client's cache hears them, handed on as they come.</summary>
public sealed class CaughtUpProxy : ICaughtUpFeed, IDisposable
{
    private readonly HostStateCache state;
    private readonly EventHandler<CaughtUpNotice> forward;

    public CaughtUpProxy(HostStateCache state)
    {
        this.state = state;
        forward = (_, notice) => CaughtUp?.Invoke(notice);
        state.CaughtUpArrived += forward;
    }

    public event Action<CaughtUpNotice>? CaughtUp;

    public void Dispose() => state.CaughtUpArrived -= forward;
}
