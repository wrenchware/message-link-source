using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// Instant reaction display while Google confirms. Google allows one reaction per person, so adding
// replaces your existing reaction and removing clears it.
public static class ReactionPrediction
{
    public const string LocalSelf = "local:self";

    public static IReadOnlyList<MessageReaction> Apply(IReadOnlyList<MessageReaction> before, string key, bool remove, IReadOnlyCollection<string> selfIds)
    {
        var mine = before.Where(r => selfIds.Contains(r.ParticipantId) || r.ParticipantId == LocalSelf).ToArray();
        var result = before.Where(r => !mine.Contains(r)).ToList();
        if (!remove) result.Add(new(key, selfIds.FirstOrDefault() ?? LocalSelf));
        else if (mine.Length == 0 && result.FindIndex(r => r.Key == key) is var index and >= 0) result.RemoveAt(index); // Own reaction not identifiable.
        return result;
    }
}
