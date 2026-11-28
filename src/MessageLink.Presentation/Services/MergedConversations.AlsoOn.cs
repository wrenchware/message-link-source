using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// Which loaded conversations could join a merge, and the few a merged conversation's Details points out: "Also on
// Instagram". Only a hint, from the merge picker's own suggestions (MergeSuggestions); nothing joins until the user
// confirms it in the Add dialog, and no service is asked anything.
public sealed partial class MergedConversations
{
    /// <summary>How recently a conversation has to have been active for Details to point it out.</summary>
    public static readonly TimeSpan AlsoOnRecent = TimeSpan.FromDays(30);
    public const int AlsoOnShown = 2;

    /// <summary>
    /// Loaded one-to-one conversations no merge holds yet, one choice per Voice person's call records. Which conversations
    /// merges hold is worked out once: asking for each row walked every merge's members for every loaded conversation.
    /// </summary>
    public IReadOnlyList<ChatSummary> CandidatesFor(IReadOnlyCollection<ChatSummary> anchor, IEnumerable<ChatSummary> loaded)
    {
        var merged = MemberIds();
        var own = anchor.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return loaded.Where(c => !own.Contains(c.Id) && CanMerge(c) && !merged.Contains(c.Id) &&
                // One person's call records cannot belong to two histories, nor join the one that already has them.
                !anchor.Any(a => SameVoicePerson(a, c) || SameVoicePerson(c, a)))
            // A person can have many separate Voice call records. One choice links that account/person's events.
            .GroupBy(c => IsCall(c) ? "calls:" + c.AccountId + "\n" +
                string.Join("\n", c.Participants.Where(p => p.IsSelf != true).Select(p => p.Id).Order(StringComparer.Ordinal)) : c.Id)
            .Select(g => g.OrderByDescending(c => c.LastActivity).First()).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// Conversations Details offers to add to a merged conversation: the merge picker's suggestions for its members, kept
    /// to services (network and account) the merge does not have yet, active within <see cref="AlsoOnRecent"/>, at most one
    /// per service and <see cref="AlsoOnShown"/> in all. None once the merge is full. Reads and writes nothing.
    /// </summary>
    public IReadOnlyList<MergeSuggestion> AlsoOn(string id, IEnumerable<ChatSummary> loaded, DateTimeOffset now)
    {
        if (Find(id) is not { } group || group.Members.Length >= MaxMembers) return [];
        var members = Members(group);
        var routes = members.Select(m => (m.Network, m.AccountId)).ToHashSet();
        var recent = CandidatesFor(members, loaded).Where(c => !routes.Contains((c.Network, c.AccountId)) &&
            c.LastActivity is { } at && now - at <= AlsoOnRecent).ToArray();
        return recent.Length == 0 ? [] : [.. MergeSuggestions.Find(members, recent).DistinctBy(s => (s.Chat.Network, s.Chat.AccountId)).Take(AlsoOnShown)];
    }
}
