namespace MessageLink.App.Services.GoogleMessages;

// Google Messages numbers its conversations on the phone, and the phone can number them again: leaving the Messages beta
// and pairing again did. Everything this PC files under those numbers — pins, labels, merges, drafts, kept pictures —
// then sat on somebody else's conversation. These are the pure rules the reset and its warning share.
internal static class GoogleRenumbering
{
    private const string Prefix = "google-direct:";

    // A conversation filed under Google's number: not Google's account id, a pending send or a preview row.
    public static bool IsGoogleChat(string? id) =>
        id is { Length: > 14 } && GoogleModels.Owns(id) && id != GoogleModels.Account && id.IndexOf(':', Prefix.Length) < 0;

    public const int SweepCap = 100_000;

    // Stores that keep only a digest of the id cannot be listed, so a reset also clears every number from 0 to twice the
    // largest seen (at most SweepCap), which finds conversations no longer listed anywhere. Null when any id seen is not
    // a plain number: then there is no range to walk and only the ids seen are cleared.
    public static int? SweepLimit(IEnumerable<string> chatIds)
    {
        long largest = -1;
        foreach (var id in chatIds)
        {
            if (!IsGoogleChat(id)) continue;
            var native = id[Prefix.Length..];
            if (native.Length > 9 || !native.All(char.IsAsciiDigit)) return null;
            largest = Math.Max(largest, long.Parse(native, System.Globalization.CultureInfo.InvariantCulture));
        }
        return largest < 0 ? null : (int)Math.Min(SweepCap, largest * 2);
    }

    // The ids seen plus, where the numbers allow, the whole range SweepLimit names.
    public static HashSet<string> Sweep(IReadOnlyCollection<string> chatIds, int? limit)
    {
        var all = new HashSet<string>(chatIds.Where(IsGoogleChat), StringComparer.Ordinal);
        if (limit is { } last) for (var n = 0; n <= last; n++) all.Add(Prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return all;
    }

    // Who is in a conversation besides this account, as a one-way value: a digest of the sorted numbers (the last ten
    // digits, so formatting does not matter), keyed by the profile. Null when nobody is named, which is never compared.
    public static string? Fingerprint(string profile, GoogleChat chat)
    {
        var people = (chat.Participants ?? []).Where(p => p is { Self: false })
            .Select(p => GoogleDirectory.Phone(p.Id) is { } phone && GoogleDirectory.PhoneKey(phone) is { Length: > 0 } key ? key : p.Id?.Trim() ?? "")
            .Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return people.Length == 0 ? null
            : MessageLink.Presentation.Services.NotificationIdentity.Hash(profile + "\ngoogle-identity\n" + string.Join("\n", people))[..32];
    }

    // Native conversation id to fingerprint, for every conversation that names somebody.
    public static Dictionary<string, string> Fingerprints(string profile, IEnumerable<GoogleChat> chats)
    {
        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chat in chats)
            if (chat.Id is { Length: > 0 } && Fingerprint(profile, chat) is { } print) prints[chat.Id] = print;
        return prints;
    }

    // Enough ids now naming other people to be a renumbering rather than a group gaining or losing someone: at least
    // three, or more than a fifth of those compared once two have changed. Only ids in both maps are compared, so the
    // first list, with nothing saved, only seeds.
    public static (bool Renumbered, int Changed, int Compared) Compare(IReadOnlyDictionary<string, string> saved, IReadOnlyDictionary<string, string> fresh)
    {
        int compared = 0, changed = 0;
        foreach (var (id, print) in fresh)
        {
            if (!saved.TryGetValue(id, out var before)) continue;
            compared++;
            if (!string.Equals(before, print, StringComparison.Ordinal)) changed++;
        }
        return (changed >= 3 || changed >= 2 && changed * 5 > compared, changed, compared);
    }
}
