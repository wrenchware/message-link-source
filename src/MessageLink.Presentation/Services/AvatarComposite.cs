using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class AvatarComposite
{
    // Keep members without photos: PersonPicture can draw their initials/person glyph beside the members whose
    // photos did load. RCS can repeat one person under the same public identity, so deduplicate only non-empty ids.
    public static IReadOnlyList<ChatParticipant> Members(IEnumerable<ChatParticipant> participants, bool isGroup, int maximum = 4)
    {
        if (!isGroup || maximum <= 0) return [];
        var result = new List<ChatParticipant>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            if (participant.IsSelf == true) continue;
            if (!string.IsNullOrEmpty(participant.Id) && !identities.Add(participant.Id)) continue;
            result.Add(participant);
            if (result.Count == maximum) break;
        }
        return result;
    }
}
