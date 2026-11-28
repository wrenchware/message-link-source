using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A plain conversation's Merge dialog joining a merged conversation that already exists (2026-10-04). Until then only the
/// merged row's own Add could take a conversation in: from a plain row, the person's other conversations that were already
/// merged were not offered at all, so the only way in was to find the merged row first. Now the dialog lists each merged
/// conversation the plain one could join as a row of its own, the likeliest first, and choosing it adds the plain one (and
/// any plain ones chosen with it) to that merge exactly as its Add would: name, preferences, sending choice and pin stay,
/// and nothing is made twice. A conversation that already belongs to a merge is never offered on its own (it is in one
/// merge at most); typing its name finds the merged conversation that holds it. Local only, like every merge.
/// </summary>
public static class MergeJoins
{
    /// <summary>A merged conversation offered to join: its row as the list draws it, its members, and the room it has left.</summary>
    public sealed record Target(ChatSummary Row, IReadOnlyList<ChatSummary> Members, int Room);

    /// <summary>What the dialog's choice means: a new merge (JoinId null) or a join of that merged conversation.</summary>
    /// <param name="Additions">Every conversation being linked: the starting one and the plain ones chosen.</param>
    public sealed record Choice(string? JoinId, IReadOnlyList<ChatSummary> Additions);

    /// <summary>
    /// The merged conversations these conversations could all join now, by name. Only ones the add would accept
    /// (MergedConversations.CanAdd): with room for the starting conversation, and none holding the same Voice person.
    /// </summary>
    public static IReadOnlyList<Target> Targets(MergedConversations merges, IReadOnlyCollection<ChatSummary> anchor)
    {
        var targets = new List<Target>();
        foreach (var id in merges.GroupIds)
        {
            if (!merges.CanAdd(id, anchor) || merges.Find(id) is not { } group) continue;
            targets.Add(new(merges.Summary(id), merges.Members(id), MergedConversations.MaxMembers - group.Members.Length));
        }
        return [.. targets.OrderBy(t => t.Row.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Row.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The merged conversations worth listing first: those whose members a plain suggestion would offer for the starting
    /// conversation, with the best of their members' reasons. A hint, as every suggestion is.
    /// </summary>
    public static IReadOnlyList<MergeSuggestion> Suggest(IReadOnlyCollection<ChatSummary> anchor, IReadOnlyList<Target> targets) =>
        [.. targets.Select(target => (target, best: MergeSuggestions.Find(anchor, target.Members).FirstOrDefault()))
            .Where(pair => pair.best is not null)
            .Select(pair => new MergeSuggestion(pair.target.Row, pair.best!.Kind, pair.best.Score))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Chat.LastActivity).ThenBy(s => s.Chat.Id, StringComparer.Ordinal)
            .Take(MergeSuggestions.MaxShown)];

    /// <summary>The second line of a merged conversation's row: which services it links.</summary>
    public static string Detail(Target target) =>
        "Merged · " + string.Join(", ", target.Members.Select(m => m.Network).Distinct(StringComparer.Ordinal));

    /// <summary>What the dialog's filter matches a merged conversation by: its name, and each member's name and service.</summary>
    public static string SearchText(Target target) =>
        string.Join(" ", target.Members.SelectMany(m => new[] { m.Title, m.Network }).Prepend("Merged").Prepend(target.Row.Title));

    /// <summary>
    /// What the chosen rows mean. No merged conversation: a new merge of the starting conversations and the chosen ones,
    /// from one to <paramref name="maxNew"/> of them. One merged conversation: the starting conversations and any plain ones
    /// chosen join it, if it has room for them all. Null for anything else, two merged conversations among it: a merge
    /// holds conversations, not merges, and combining two would be an unmerge and a merge the user did not ask for.
    /// </summary>
    public static Choice? Choose(IReadOnlyCollection<ChatSummary> anchor, IReadOnlyList<ChatSummary> selected,
        IReadOnlyDictionary<string, int> rooms, int maxNew)
    {
        var merged = selected.Where(c => MergedConversations.IsMerged(c.Id)).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
        var plain = selected.Where(c => !MergedConversations.IsMerged(c.Id)).DistinctBy(c => c.Id).ToArray();
        if (merged.Length > 1) return null;
        if (merged.Length == 1)
        {
            var additions = anchor.Concat(plain).DistinctBy(c => c.Id).ToArray();
            return rooms.TryGetValue(merged[0], out var room) && additions.Length > 0 && additions.Length <= room ? new(merged[0], additions) : null;
        }
        return plain.Length > 0 && plain.Length <= maxNew ? new(null, [.. anchor.Concat(plain).DistinctBy(c => c.Id)]) : null;
    }
}
