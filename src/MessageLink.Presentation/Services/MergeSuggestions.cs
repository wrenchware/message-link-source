using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public enum MergeMatchKind { SamePhone, SameName, SimilarName }
public sealed record MergeSuggestion(ChatSummary Chat, MergeMatchKind Kind, int Score);

/// <summary>
/// Which loaded conversations might be the same person as the one being merged, for the merge picker to list first.
/// Only a hint: nothing is checked for the user, and nothing merges until they choose. It works from what this PC
/// already has — names as the list shows them and the numbers Google Messages and Google Voice give — and asks no
/// service anything. A name is never taken as proof: a match on names alone is "similar", or "same name", never "same
/// person", and it has to come from a different service or account, because one service lists one person once.
/// </summary>
public static partial class MergeSuggestions
{
    public const int MaxShown = 5;
    private const int Threshold = 55;
    private static readonly HashSet<string> Honorifics = new(["mr", "mrs", "ms", "dr", "jr", "sr", "ii", "iii"], StringComparer.Ordinal);

    // Saved Google directory rows can be older than the inbox's loaded prefix. A loaded row is the current copy;
    // including a saved row here never opens its history, starts a conversation or marks it read.
    public static IReadOnlyList<ChatSummary> Candidates(IReadOnlyCollection<ChatSummary> anchor,
        IEnumerable<ChatSummary> loaded, IReadOnlySet<string> merged, IEnumerable<ChatSummary>? saved = null)
    {
        var own = anchor.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return loaded.Concat(saved ?? []).DistinctBy(c => c.Id)
            .Where(c => !own.Contains(c.Id) && MergedConversations.CanMerge(c) && !merged.Contains(c.Id) &&
                !anchor.Any(a => MergedConversations.SameVoicePerson(a, c) || MergedConversations.SameVoicePerson(c, a)))
            .GroupBy(c => MergedConversations.IsCall(c) ? "calls:" + c.AccountId + "\n" +
                string.Join("\n", c.Participants.Where(p => p.IsSelf != true).Select(p => p.Id).Order(StringComparer.Ordinal)) : c.Id)
            .Select(g => g.OrderByDescending(c => c.LastActivity).First())
            .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<MergeSuggestion> Find(IReadOnlyCollection<ChatSummary> anchor, IEnumerable<ChatSummary> candidates,
        IReadOnlySet<string>? merged = null)
    {
        if (anchor.Count == 0) return [];
        var anchorIds = anchor.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var anchorKeys = anchor.Select(Key).ToArray();
        var anchorPhones = anchorKeys.SelectMany(k => k.Phones).ToHashSet(StringComparer.Ordinal);
        var anchorNames = anchorKeys.SelectMany(k => k.Names).ToArray();
        var anchorRoutes = anchor.Select(a => (a.Network, a.AccountId)).ToHashSet();
        var found = new List<MergeSuggestion>();
        foreach (var candidate in candidates)
        {
            if (anchorIds.Contains(candidate.Id) || !MergedConversations.CanMerge(candidate) || merged?.Contains(candidate.Id) == true ||
                anchor.Any(a => MergedConversations.SameVoicePerson(a, candidate) || MergedConversations.SameVoicePerson(candidate, a))) continue;
            var key = Key(candidate);
            if (key.Phones.Any(anchorPhones.Contains)) { found.Add(new(candidate, MergeMatchKind.SamePhone, 100)); continue; }
            // One service lists a person once, so a namesake on the same service and account is somebody else.
            if (anchorRoutes.Contains((candidate.Network, candidate.AccountId))) continue;
            var best = (Kind: MergeMatchKind.SimilarName, Score: 0);
            foreach (var mine in anchorNames)
                foreach (var theirs in key.Names)
                    if (Score(mine, theirs) is { Score: > 0 } match && match.Score > best.Score) best = match;
            if (best.Score >= Threshold) found.Add(new(candidate, best.Kind, best.Score));
        }
        return found.OrderByDescending(s => s.Score).ThenByDescending(s => s.Chat.LastActivity)
            .ThenBy(s => s.Chat.Id, StringComparer.Ordinal).Take(MaxShown).ToArray();
    }

    // Never "same person": two people can share a name, and a family can share a phone.
    public static string Reason(MergeMatchKind kind) => kind switch
    {
        MergeMatchKind.SamePhone => "Same phone number",
        MergeMatchKind.SameName => "Same name",
        _ => "Similar name"
    };

    /// <summary>Counts only: what the picker considered and offered, by kind. Never a name, number or id.</summary>
    public static string LogLine(int candidates, IReadOnlyList<MergeSuggestion> offered) =>
        $"merge suggestions candidates={candidates} offered={offered.Count} phone={offered.Count(s => s.Kind == MergeMatchKind.SamePhone)} " +
        $"name={offered.Count(s => s.Kind == MergeMatchKind.SameName)} similar={offered.Count(s => s.Kind == MergeMatchKind.SimilarName)}";

    /// <summary>
    /// Whether the conversations being linked carry different phone numbers: at least two of them carry a number the
    /// service gave for the other person, and some pair of those shares none. Only a caution, never a refusal; short codes
    /// and the user's own number never count, and Messenger and Instagram carry no number, so they never raise it.
    /// </summary>
    public static bool PhonesDiffer(IEnumerable<ChatSummary> chats)
    {
        var numbered = chats.DistinctBy(c => c.Id).Select(Phones).Where(phones => phones.Length > 0).ToArray();
        for (var i = 0; i < numbered.Length; i++)
            for (var j = i + 1; j < numbered.Length; j++)
                if (!numbered[i].Any(numbered[j].Contains)) return true;
        return false;
    }

    private static (MergeMatchKind Kind, int Score) Score(string[] a, string[] b)
    {
        if (a.Length >= 2 && a.SequenceEqual(b, StringComparer.Ordinal)) return (MergeMatchKind.SameName, 90);
        // "O'Brien" and "OBrien", or "Mary Ann" and "Maryann".
        if ((a.Length >= 2 || b.Length >= 2) && string.Concat(a) == string.Concat(b)) return (MergeMatchKind.SameName, 90);
        if (a.Length >= 2 && b.Length >= 2)
        {
            if (!SameGiven(a[0], b[0])) return default;
            if (a[^1] == b[^1]) return (MergeMatchKind.SimilarName, 80);
            if (a[^1].Length >= 5 && b[^1].Length >= 5 && WithinOneEdit(a[^1], b[^1])) return (MergeMatchKind.SimilarName, 60);
            return default;
        }
        if (a.Length == 1 && b.Length == 1)
            return a[0].Length >= 3 && a[0] == b[0] ? (MergeMatchKind.SameName, 70) : default;
        // A first name on its own against a full name. The weakest hint, and not for two letters that match half a list.
        var (single, full) = a.Length == 1 ? (a[0], b) : (b[0], a);
        return single.Length >= 3 && full[0].Length >= 3 && SameGiven(single, full[0]) ? (MergeMatchKind.SimilarName, Threshold) : default;
    }

    private sealed record PersonKey(string[][] Names, string[] Phones);
    private static PersonKey Key(ChatSummary chat)
    {
        var others = Counterparts(chat).ToArray();
        var names = others.Select(p => p.DisplayName).Prepend(chat.Title).Select(Tokens).Where(t => t.Length > 0)
            .DistinctBy(t => string.Join(' ', t)).ToArray();
        return new(names, Phones(chat));
    }
    // Only a number a service gave as one. Messenger and Instagram ids are numeric too, and are nobody's phone.
    private static string[] Phones(ChatSummary chat) =>
        Counterparts(chat).Select(p => PhoneKey(p.Phone)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    // Google can carry hidden RCS rows for the viewer without marking them self. Its helper assigns Phone only
    // to counterparts, so those rows must not contribute names either (as in the Details Accounts projection).
    internal static IEnumerable<ChatParticipant> Counterparts(ChatSummary chat) =>
        chat.Participants.Where(p => p.IsSelf != true &&
            (!chat.Network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(p.Phone)));

    // The words of a name as suggestions compare them. Public for the offline checks.
    public static string[] Tokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var text = new System.Text.StringBuilder(name.Length);
        var depth = 0;
        // "Alex (work)" and "Alex [old number]" name the same Alex.
        foreach (var ch in name)
        {
            if (ch is '(' or '[') { depth++; continue; }
            if (ch is ')' or ']') { if (depth > 0) depth--; continue; }
            if (depth == 0) text.Append(ch);
        }
        return TextSearchIndex.Words(text.ToString()).Where(w => !w.All(char.IsDigit) && !Honorifics.Contains(w)).ToArray();
    }

    // The app's rule for one number written several ways: its last ten digits, and at least seven of them, so a short
    // code is never somebody's number.
    internal static string? PhoneKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var digits = new string([.. value.Where(char.IsDigit)]);
        return digits.Length < 7 ? null : digits.Length > 10 ? digits[^10..] : digits;
    }

    internal static bool SameGiven(string a, string b) =>
        a == b || Nicknames.TryGetValue(a, out var mine) && Nicknames.TryGetValue(b, out var theirs) && mine.Overlaps(theirs);

    // One substitution, insertion, deletion or swap of neighbours: "Smith" and "Smyth", "Thompson" and "Thomson".
    private static bool WithinOneEdit(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        if (a.Length == b.Length)
            return a[(start + 1)..] == b[(start + 1)..] ||
                start + 1 < a.Length && a[start] == b[start + 1] && a[start + 1] == b[start] && a[(start + 2)..] == b[(start + 2)..];
        return a.Length > b.Length ? a[(start + 1)..] == b[start..] : a[start..] == b[(start + 1)..];
    }
}
