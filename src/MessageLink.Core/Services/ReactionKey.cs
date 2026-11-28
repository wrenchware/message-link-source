using System.Globalization;
using System.Text;

namespace MessageLink.Core.Services;

/// <summary>A reaction a person picked from a full emoji picker: one emoji, nothing that reads as words.</summary>
public static class ReactionKey
{
    /// <summary>Longer than any emoji sequence (a family with tones is about 25 UTF-16 units).</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// One grapheme of at least one emoji symbol, with no letter, space or control in it. A digit, # or * counts only as a keycap's base (1️⃣).
    /// Text sent as a "reaction" would reach the other person as words, so anything else is refused before it goes.
    /// </summary>
    public static bool IsEmoji(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength || new StringInfo(key).LengthInTextElements != 1) return false;
        var keycap = key.Contains('\u20E3');
        var symbol = keycap;
        foreach (var rune in key.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || Rune.IsLetter(rune)) return false;
            if (Rune.IsDigit(rune) && !keycap) return false;
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol) symbol = true;
        }
        return symbol;
    }

    /// <summary>Refuses, before anything goes, a reaction to add that is not one emoji. Taking one away names whatever is there.</summary>
    public static void EnsureAddable(string reaction, bool remove)
    {
        if (!remove && !IsEmoji(reaction)) throw new SendException(false, NotAnEmoji);
    }

    public const string NotAnEmoji = "Only an emoji can be a reaction.";

    /// <summary>Whether two reactions are the same emoji, whichever variation selector each carries: services write ❤ and ❤️ either way.</summary>
    public static bool Same(string a, string b) => Bare(a) == Bare(b);

    private static string Bare(string key) => key.Replace("\uFE0F", "", StringComparison.Ordinal).Replace("\uFE0E", "", StringComparison.Ordinal);
}
