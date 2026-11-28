using MessageLink.Core.Messaging;

namespace MessageLink.Protocol;

// What the incoming-photo and recent-photo services ask of the server. Neither is journaled: both are reads from the
// provider's point of view (a download into the host's cache; a request for a link's card, which the provider answers with the message's own
// change), a repeat finds the cache or the card already there, and an interrupted call is an ordinary failure the services already treat as
// "the conversation asks again when it is drawn".
//
//   media.prepare. An absent limit is a picture (IMediaPreparer.PrepareImageAsync); a present one is a GIF clip of at most that many bytes
//   (PrepareClipAsync), and a server holds it to PreparedClipCeiling whatever it is asked. Nothing the server downloads comes back: a client that
//   will draw a photo reads it with media.read, which fills the same cache.
//
//   media.requestCard. The client says what only it knows (whether link previews are on, and how its list shows the conversation); the host
//   applies its own rules about whose links may be fetched on a person's behalf (GoogleIncomingCards).
//
//   The `caughtUp` kind of the event feed (EventKinds.CaughtUp, CaughtUpNotice) is the third part: it is in the server's sequence like any
//   event, so a client that resumes is given the ones it missed, and one that does not know the kind ignores it.
public static partial class ProtocolMethods
{
    public const string MediaPrepare = "media.prepare";
    public const string MediaRequestCard = "media.requestCard";

    private static Entry[] IncomingEntries() =>
    [
        new(MediaPrepare, "IMediaPreparer.PrepareImageAsync and PrepareClipAsync", typeof(MediaPrepareParams), typeof(MediaPrepared), ProtocolVersion.Original),
        new(MediaRequestCard, "IIncomingCards.RequestAsync", typeof(RequestCardParams), typeof(Done), ProtocolVersion.Original),
    ];
}

/// <summary>A picture to warm when <paramref name="Limit"/> is absent; a GIF clip of at most that many bytes when it is present.</summary>
public sealed record MediaPrepareParams(MessageAttachment Attachment, long? Limit = null);

/// <summary>
/// <paramref name="Fetched"/>: something was downloaded. <paramref name="Bytes"/>: a picture's answer as <c>IMediaPreparer.PrepareImageAsync</c>
/// gives it (absent: not warmed this way; 0: already held); absent for a clip.
/// </summary>
public sealed record MediaPrepared(bool Fetched, long? Bytes = null);

/// <summary>
/// <paramref name="LinkPreviews"/> is the person's setting and must be stated: a flag left out would read as "off", which is safe, but
/// a request that does not say is a request that does not know. <paramref name="Spam"/> is how the client's list shows the conversation
/// (absent: not shown at all).
/// </summary>
public sealed record RequestCardParams(string ChatId, string MessageId, bool LinkPreviews, bool? Spam = null);
