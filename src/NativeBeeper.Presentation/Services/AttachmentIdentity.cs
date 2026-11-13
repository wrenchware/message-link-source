using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class AttachmentIdentity
{
    // A download URL, filename or size may be enriched by a later message lookup.
    // Stable attachment IDs identify the media independently of those metadata changes.
    public static bool SameMedia(MessageAttachment first, MessageAttachment second)
    {
        if (Kind(first) != Kind(second)) return false;
        if (!string.IsNullOrWhiteSpace(first.Id) && !string.IsNullOrWhiteSpace(second.Id)) return first.Id == second.Id;
        if (!string.IsNullOrWhiteSpace(first.SourceUrl)) return first.SourceUrl == second.SourceUrl;
        // A link card with no picture (Instagram's share with no media, Messenger's card of words) has neither: it is
        // the same card while it leads to the same place. Called different, it was rebuilt on every receipt.
        return string.IsNullOrWhiteSpace(first.Id) && string.IsNullOrWhiteSpace(second.Id) && string.IsNullOrWhiteSpace(second.SourceUrl) &&
            first.Link is not null && first.Link == second.Link;
    }

    // The same picture, possibly now available in full. Google Messages names a photo by a hash that includes its
    // full-size media id, which a newly arrived photo does not have yet: it comes in as a preview, and when the phone
    // finishes downloading it the same photo returns under a new id. Treated as a different picture, its tile was
    // rebuilt empty and faded in again — the blink the user saw on a photo that had just arrived. Only for the
    // attachment at the same place in the same message, which the caller establishes, and only for that upgrade.
    // A photo still downloading to the phone is the same picture too: it arrives with no media of its own yet, then as a
    // preview or in full under the ids Google gives it then. Rebuilt for that, its tile was emptied and its preview lost.
    // A preview still waiting for the full size can also gain a thumbnail of its own first, under another id: our own sent
    // photo does, about a second after it goes. Rebuilt for that, a set lost the photo it was showing (2026-10-01).
    public static bool SameMediaOrUpgrade(MessageAttachment before, MessageAttachment after) =>
        SameMedia(before, after) || Kind(before) == "image" && Kind(after) == "image" && before.Link == after.Link &&
            (before.Availability == "preview" && after.Availability != "downloading" ||
             before.Availability == "downloading" && after.Availability is null or "preview");

    private static string? Kind(MessageAttachment a) =>
        a.Type == "img" || a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image" :
        a.Type == "video" || a.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? "video" : a.Type;
}
