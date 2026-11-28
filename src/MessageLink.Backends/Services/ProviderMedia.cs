using MessageLink.Core.Messaging;

namespace MessageLink.App.Services;

// One place a provider says how its media is named and what can be done with it.
//
// Adding Instagram meant adding "instagram-media:" by hand to the video-still test in MessageBubble and to the avatar
// cache test in ProviderAvatarReader, and each omission failed silently and looked like something else: first an empty
// grey play button where a video's still belonged, then avatars that downloaded again at every launch. Nothing fails
// a build or a test when a fourth list appears, so the lists read from this table instead. A provider added here is
// added everywhere; a provider missing from here is missing visibly, because the contract harness says so.
internal sealed record ProviderMedia(
    // What the app calls this provider in a diagnostic line. Never anything drawn.
    string Diagnostic,
    // How every media identifier from this provider begins.
    string MediaPrefix,
    // How a still to draw behind a video's play button is named, where the provider supplies one at all. Google names
    // its stills separately from its media; the others reuse the media prefix. A provider with none here draws the
    // plain play button rather than an empty one.
    string? StillPrefix,
    // Whether this provider's media id means the same picture in every helper session, so the bytes are worth keeping
    // on disk. Instagram's avatar id is a digest of the address, which is stable while the picture is unchanged and
    // simply becomes a different id when it changes. A provider that says no downloads every avatar at every launch.
    bool AvatarsKeepTheirId,
    // Whether an avatar read has to carry the identifier as well as the source. Messenger's helper tells an avatar
    // from a photo by a null id; Voice needs its account-qualified id to route the read back to the right account.
    bool AvatarNeedsItsId,
    // Whether a recording is fetched only when it is played, never ahead of it as the message comes on screen. Instagram's
    // are: every download is a request of the user's real account, and the length it gives shows before one. Appended.
    bool RecordingsWaitForPlay = false,
    // Whether a still is made from the video itself when the provider sends none (VideoStills). Google Messages often sends
    // no thumbnail for a video and Google Voice never does; Messenger and Instagram name theirs in the attachment. Appended.
    bool StillFromVideo = false)
{
    public static readonly IReadOnlyList<ProviderMedia> All =
    [
        new("google", "google-media:", "google-preview:", AvatarsKeepTheirId: false, AvatarNeedsItsId: false, StillFromVideo: true),
        new("messenger", "messenger-media:", "messenger-media:", AvatarsKeepTheirId: true, AvatarNeedsItsId: false),
        new("voice", "gvoice-media:", null, AvatarsKeepTheirId: true, AvatarNeedsItsId: true, StillFromVideo: true),
        new("instagram", "instagram-media:", "instagram-media:", AvatarsKeepTheirId: true, AvatarNeedsItsId: false,
            RecordingsWaitForPlay: true),
    ];

    private static bool Starts(string? value, string prefix) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal);

    // The provider a media identifier belongs to, or null where nothing claims it.
    public static ProviderMedia? For(string? mediaId) =>
        All.FirstOrDefault(provider => Starts(mediaId, provider.MediaPrefix));

    // What a diagnostic line calls the provider behind this identifier. Never an identifier itself.
    public static string DiagnosticFor(string? mediaId) => For(mediaId)?.Diagnostic ?? "other";

    // Whether an avatar from this source keeps the same identifier between sessions, so it belongs on disk.
    public static bool AvatarIsCacheable(string source) => For(source) is { AvatarsKeepTheirId: true };

    // Whether an avatar read from this source has to carry its identifier as well.
    public static bool AvatarCarriesItsId(string source) => For(source) is { AvatarNeedsItsId: true };

    // Whether a recording from this source is left alone until it is played.
    public static bool RecordingWaitsForPlay(string? mediaId) => For(mediaId) is { RecordingsWaitForPlay: true };

    // Whether this source names a still that can be drawn behind a video's play button.
    public static bool IsVideoStill(string? source) =>
        All.Any(provider => provider.StillPrefix is { } still && Starts(source, still));

    // Whether this video has no still of its own but one can be made from the clip: its provider sends none and makes
    // them from the video. A GIF delivered as a clip loops by itself and has no use for one.
    public static bool DerivesVideoStill(MessageAttachment video) =>
        !video.IsGif && !IsVideoStill(video.SourceUrl) && For(video.Id) is { StillFromVideo: true } &&
        (video.Type == "video" || video.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);
}
