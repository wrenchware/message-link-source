using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

/// <summary>
/// Puts a picture or a GIF clip in the host's media cache ahead of the conversation drawing it (SWITCHBOARD.md 3.0b,
/// <c>media.prepare</c>). The bytes stay where the cache is: nothing is returned for the caller to decode, which is why the client
/// still reads and decodes the photos it will draw itself (the preview cache is its own). Only ever the reads the app's own
/// prefetch made before this was a port, and a host that is asked for more refuses it.
/// </summary>
public interface IMediaPreparer
{
    /// <summary>
    /// The smaller copy a conversation draws of a picture already in a conversation. How many bytes were downloaded; 0 when the cache
    /// already held it; null for a picture not warmed this way (Instagram's, one sent from this PC, any whose copy the cache does not keep).
    /// </summary>
    Task<long?> PrepareImageAsync(MessageAttachment picture, CancellationToken cancellationToken = default);

    /// <summary>
    /// A GIF clip's bytes, never more than <paramref name="limit"/> of them. Whether anything was downloaded: false for a clip already
    /// kept, one whose size says more than the limit, and one no provider here keeps.
    /// </summary>
    Task<bool> PrepareClipAsync(MessageAttachment clip, long limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the host to make the card for a link that has just come in (SWITCHBOARD.md 3.0b, <c>media.requestCard</c>), under the rules the
/// host applies to whose links may be fetched on a person's behalf. The caller says what only it knows: whether link previews are on, and
/// whether its conversation list shows the conversation as spam (null when it does not show it at all).
/// </summary>
public interface IIncomingCards
{
    Task RequestAsync(string chatId, string messageId, bool linkPreviews, bool? listedAsSpam, CancellationToken cancellationToken = default);
}

/// <summary>
/// Messages a provider caught up on after connecting in one conversation: what came while the app was closed, now held, so their pictures
/// can be prepared before the conversation opens. Ids only. <paramref name="Announced"/>: their own events follow in the feed, so they are
/// only remembered rather than queued twice.
/// </summary>
public sealed record CaughtUpNotice(string ChatId, IReadOnlyList<string> Ids, bool Announced = false);

/// <summary>
/// Where the providers' catch-ups are heard (<c>caughtUp</c> in the host's event feed). Raised on whichever thread delivered it, which is
/// not the interface thread, and never for a catch-up that named nothing.
/// </summary>
public interface ICaughtUpFeed
{
    event Action<CaughtUpNotice>? CaughtUp;
}
