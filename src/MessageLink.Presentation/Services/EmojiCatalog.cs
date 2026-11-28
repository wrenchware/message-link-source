namespace MessageLink.Presentation.Services;

// One emoji the picker can offer. Tonable marks a base that accepts a Fitzpatrick skin-tone modifier.
public sealed record CatalogEmoji(string Glyph, string Name, string Keywords, bool Tonable);

public sealed record EmojiGroup(string Name, string Glyph, IReadOnlyList<CatalogEmoji> Emoji);

// The emoji the in-app picker offers, as a curated set rather than the whole of Unicode: every entry here is one this
// app has a name and search terms for, which is what makes the picker searchable and what the Windows panel cannot be
// (its own GIF results do not work in this app at all). Flags are deliberately absent — several hundred entries whose
// names carry meaning this app has no business asserting, and nobody reaches for them in a message.
//
// Data only, no Windows types, so the offline harness can check the shape of every entry.
public static partial class EmojiCatalog
{
    public const int SearchLimit = 120;
    // Fitzpatrick modifiers, in the order the picker offers them. Index 0 is the default, which adds nothing.
    private static readonly string[] Tones = ["", "\U0001F3FB", "\U0001F3FC", "\U0001F3FD", "\U0001F3FE", "\U0001F3FF"];
    public static int ToneCount => Tones.Length;

    // A hand for each tone, for the tone picker itself.
    public static string ToneSample(int tone) => WithTone("\u270B\uFE0F", tone);

    // Applies a skin tone to a glyph. The modifier goes straight after the base scalar and replaces a variation
    // selector that followed it, which is what turns ☝️ into ☝🏽 and 🙋‍♀️ into 🙋🏽‍♀️ rather than breaking the sequence.
    public static string WithTone(string glyph, int tone)
    {
        if (tone <= 0 || tone >= Tones.Length || string.IsNullOrEmpty(glyph)) return glyph;
        var head = char.IsHighSurrogate(glyph[0]) && glyph.Length > 1 ? 2 : 1;
        var rest = head < glyph.Length && glyph[head] == '\uFE0F' ? head + 1 : head;
        return glyph[..head] + Tones[tone] + glyph[rest..];
    }

    public static string WithTone(CatalogEmoji emoji, int tone) => emoji.Tonable ? WithTone(emoji.Glyph, tone) : emoji.Glyph;

    private static readonly Lazy<IReadOnlyList<EmojiGroup>> groups = new(Build);
    public static IReadOnlyList<EmojiGroup> Groups => groups.Value;

    private static readonly Lazy<IReadOnlyList<CatalogEmoji>> all =
        new(() => Groups.SelectMany(group => group.Emoji).ToArray());
    public static IReadOnlyList<CatalogEmoji> All => all.Value;

    // Whole words first, then anything containing the term, so "car" offers the car before the carrot and "hand"
    // offers the hand gestures before the handshake. A blank search returns nothing; the picker shows a group then.
    public static IReadOnlyList<CatalogEmoji> Search(string query)
    {
        var term = query.Trim();
        if (term.Length == 0) return [];
        var scored = new List<(int Rank, int Index, CatalogEmoji Emoji)>();
        for (var index = 0; index < All.Count; index++)
        {
            var emoji = All[index];
            var rank = Rank(emoji, term);
            if (rank < int.MaxValue) scored.Add((rank, index, emoji));
        }
        // Among equally good matches the entry whose name is most nearly just the term comes first, so "heart" is the
        // red heart rather than heart hands, and the catalog order settles anything still tied.
        return scored.OrderBy(entry => entry.Rank).ThenBy(entry => entry.Emoji.Name.Length).ThenBy(entry => entry.Index)
            .Take(SearchLimit).Select(entry => entry.Emoji).ToArray();
    }

    // A search term that is exactly one of an entry's own keywords beats a longer name that merely happens to contain
    // the word: "car" is the automobile, whose keyword is car, not the racing car.
    private static int Rank(CatalogEmoji emoji, string term)
    {
        if (string.Equals(emoji.Name, term, StringComparison.OrdinalIgnoreCase)) return 0;
        if (HasWord(emoji.Keywords, term, whole: true)) return 1;
        if (emoji.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 2;
        if (HasWord(emoji.Name, term, whole: false)) return 3;
        if (HasWord(emoji.Keywords, term, whole: false)) return 4;
        if (emoji.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return 5;
        if (emoji.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)) return 6;
        return int.MaxValue;
    }

    private static bool HasWord(string text, string term, bool whole)
    {
        var at = 0;
        while (at <= text.Length)
        {
            var next = text.IndexOf(' ', at);
            var word = next < 0 ? text[at..] : text[at..next];
            if (whole ? string.Equals(word, term, StringComparison.OrdinalIgnoreCase)
                      : word.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return true;
            if (next < 0) break;
            at = next + 1;
        }
        return false;
    }

    private static IReadOnlyList<EmojiGroup> Build() =>
    [
        Group("Smileys", "\U0001F600", Smileys),
        Group("People", "\U0001F44B", People),
        Group("Animals & nature", "\U0001F43B", Animals),
        Group("Food & drink", "\U0001F354", Food),
        Group("Activity", "\u26BD", Activity),
        Group("Travel & places", "\u2708\uFE0F", Travel),
        Group("Objects", "\U0001F4A1", Objects),
        Group("Symbols", "\u2764\uFE0F", Symbols),
    ];

    // Each line is glyph|name|space-separated search terms, and a glyph prefixed with ~ takes a skin tone.
    private static EmojiGroup Group(string name, string glyph, string data)
    {
        var emoji = new List<CatalogEmoji>();
        foreach (var line in data.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length == 0) continue;
            var parts = entry.Split('|');
            if (parts.Length != 3) continue;
            var tonable = parts[0].StartsWith('~');
            emoji.Add(new CatalogEmoji(tonable ? parts[0][1..] : parts[0], parts[1].Trim(), parts[2].Trim(), tonable));
        }
        return new EmojiGroup(name, glyph, emoji);
    }

    // The catalog entry a glyph came from, so a remembered recent can still show its name. Skin tones and variation
    // selectors are ignored on both sides: 👍🏽 and 👍 are the same entry, and so are ☝🏽 and ☝️.
    private static readonly Lazy<Dictionary<string, CatalogEmoji>> byGlyph = new(() =>
    {
        var map = new Dictionary<string, CatalogEmoji>(StringComparer.Ordinal);
        foreach (var emoji in All) map.TryAdd(Strip(emoji.Glyph), emoji);
        return map;
    });

    public static CatalogEmoji? Find(string glyph) =>
        !string.IsNullOrEmpty(glyph) && byGlyph.Value.TryGetValue(Strip(glyph), out var emoji) ? emoji : null;

    private static string Strip(string glyph)
    {
        var text = new System.Text.StringBuilder(glyph.Length);
        for (var index = 0; index < glyph.Length; index++)
        {
            if (glyph[index] == '\uFE0F') continue;
            if (index + 1 < glyph.Length && glyph[index] == '\uD83C' && glyph[index + 1] is >= '\uDFFB' and <= '\uDFFF')
            { index++; continue; }
            text.Append(glyph[index]);
        }
        return text.ToString();
    }
}

// The emoji a person reached for last, newest first, kept as the glyphs they actually inserted so a chosen skin tone
// comes back with them. Pure list handling, so the harness can check the ordering and the cap.
public static class EmojiRecents
{
    public const int Limit = 32;

    public static string[] Add(IEnumerable<string>? existing, string glyph)
    {
        if (string.IsNullOrWhiteSpace(glyph)) return Clean(existing);
        return Clean(new[] { glyph }.Concat(existing ?? []));
    }

    public static string[] Clean(IEnumerable<string>? existing) =>
        (existing ?? []).Where(glyph => !string.IsNullOrWhiteSpace(glyph))
            .Distinct(StringComparer.Ordinal).Take(Limit).ToArray();
}

// One tile in the picker: the glyph as it will be inserted, with the chosen skin tone already applied, and the name
// that is its tooltip and its accessible name. Public because the picker's item template binds to it by reflection.
public sealed record EmojiChoice(string Glyph, string Name);
