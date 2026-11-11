using System.Globalization;

namespace NativeBeeper.Presentation.Services;

// A one-time sign-in code in an incoming text ("Your code is 482913", "G-482913 is your Google verification code"),
// offered as Copy code in the message menu (Windows itself offers one on the notification). Found here, locally, from the text alone. What it
// finds is shown to the person the message was sent to and copied only when they ask; it is never logged or saved.
//
// Deliberately cautious: a number is a code only beside a word that says so, and anything that reads as a phone
// number, an amount, a time, a date, a year or part of a link is passed over. Two different numbers equally close to
// such a word give nothing rather than a guess.
public static class OneTimeCode
{
    // Matched without regard to case, as whole words.
    private static readonly string[] Words =
        ["security code", "verification", "authentication", "passcode", "one-time", "one time", "sign-in", "sign in", "log in", "login", "verify", "code"];
    // Matched exactly as written: in lower case "pin" is as often a map pin as a number to type.
    private static readonly string[] Acronyms = ["OTP", "PIN", "Pin", "2FA"];
    // What may join digit groups into one number: "123-456", "12:30", "1,234", "2026/09/24", "555 123 4567".
    private const string Joiners = " -.,:/";

    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var plain = MessageText.PlainText(text);
        if (plain.Length == 0) return null;
        var keywords = Keywords(plain);
        var candidates = new List<(int Start, int End, string Digits, bool Google)>();
        for (var i = 0; i < plain.Length;)
        {
            if (!char.IsAsciiDigit(plain[i])) { i++; continue; }
            var start = i;
            var groups = new List<(int Start, int End)>();
            var joins = new List<char>();
            i = Digits(plain, i); groups.Add((start, i));
            while (i + 1 < plain.Length && Joiners.Contains(plain[i]) && char.IsAsciiDigit(plain[i + 1]))
            {
                joins.Add(plain[i]);
                var groupStart = i + 1;
                i = Digits(plain, groupStart); groups.Add((groupStart, i));
            }
            if (Candidate(plain, start, i, groups, joins, keywords) is { } found) candidates.Add(found);
        }
        if (candidates.Count == 0 || keywords.Count == 0 && !candidates.Any(candidate => candidate.Google)) return null;
        var ranked = candidates.Select(candidate => (candidate.Digits, Distance: candidate.Google ? 0 : Distance(candidate.Start, candidate.End, keywords)))
            .Where(candidate => candidate.Distance < int.MaxValue).ToList();
        if (ranked.Count == 0) return null;
        var nearest = ranked.Min(candidate => candidate.Distance);
        var closest = ranked.Where(candidate => candidate.Distance == nearest).Select(candidate => candidate.Digits).Distinct(StringComparer.Ordinal).ToList();
        return closest.Count == 1 ? closest[0] : null;
    }

    private static int Digits(string text, int index)
    {
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
        return index;
    }

    private static (int Start, int End, string Digits, bool Google)? Candidate(string text, int start, int end,
        List<(int Start, int End)> groups, List<char> joins, List<(int Start, int End)> keywords)
    {
        string digits;
        if (groups.Count == 1)
        {
            // Nine digits or more reads as a phone or account number.
            if (end - start is < 4 or > 8) return null;
            digits = text[start..end];
        }
        // Six digits written in two halves, as many services send them. Every other joined number is a phone number,
        // a time, a date, an amount or a version.
        else if (groups.Count == 2 && joins[0] is '-' or ' ' && groups.All(group => group.End - group.Start == 3))
            digits = text[groups[0].Start..groups[0].End] + text[groups[1].Start..groups[1].End];
        else return null;

        var before = start > 0 ? text[start - 1] : ' ';
        var after = end < text.Length ? text[end] : ' ';
        if (char.IsLetterOrDigit(after)) return null;
        var google = start >= 2 && before == '-' && text[start - 2] == 'G' && (start == 2 || !char.IsLetterOrDigit(text[start - 3]));
        if (!google)
        {
            if (char.IsLetterOrDigit(before) || before is '+' or '_') return null;
            // "ID-4821" or "A-4821": a reference, not a code.
            if (before == '-' && start >= 2 && char.IsLetterOrDigit(text[start - 2])) return null;
        }
        if (Currency(Previous(text, start)) || Currency(Next(text, end)) || Next(text, end) == '%') return null;
        if (Meridiem(text, end)) return null;
        if (InLink(text, start, end)) return null;
        if (groups.Count == 1 && !DirectlyAfterKeyword(text, start, keywords))
        {
            if (digits.Length == 4 && Year(digits)) return null;
            if (digits.Length == 8 && (Year(digits[..4]) && MonthDay(digits[4..6], digits[6..]) ||
                Year(digits[4..]) && (MonthDay(digits[..2], digits[2..4]) || MonthDay(digits[2..4], digits[..2])))) return null;
        }
        return (start, end, digits, google);
    }

    // The nearest character, looking past one space: "$ 1234", "1234 %".
    private static char Previous(string text, int start)
    {
        var index = start - 1;
        if (index >= 0 && text[index] == ' ') index--;
        return index >= 0 ? text[index] : ' ';
    }

    private static char Next(string text, int end)
    {
        var index = end;
        if (index < text.Length && text[index] == ' ') index++;
        return index < text.Length ? text[index] : ' ';
    }

    private static bool Currency(char value) => CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.CurrencySymbol;

    // "1230 pm", "0930 a.m."
    private static bool Meridiem(string text, int end)
    {
        var index = end;
        if (index < text.Length && text[index] == ' ') index++;
        if (index + 1 >= text.Length) return false;
        var first = char.ToLowerInvariant(text[index]);
        if (first is not ('a' or 'p')) return false;
        var rest = text[(index + 1)..];
        return rest.StartsWith("m", StringComparison.OrdinalIgnoreCase) && (rest.Length == 1 || !char.IsLetter(rest[1])) ||
            rest.StartsWith(".m", StringComparison.OrdinalIgnoreCase);
    }

    // The whole run of non-space characters around the number, which for a link is the link.
    private static bool InLink(string text, int start, int end)
    {
        var from = start; while (from > 0 && !char.IsWhiteSpace(text[from - 1])) from--;
        var to = end; while (to < text.Length && !char.IsWhiteSpace(text[to])) to++;
        var token = text[from..to];
        return token.Contains("://", StringComparison.Ordinal) || token.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            token.IndexOfAny(['/', '=', '?', '&', '@']) >= 0;
    }

    private static bool Year(string digits) => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2099;

    private static bool MonthDay(string month, string day) =>
        int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12 &&
        int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31;

    // "PIN: 2019" is a code, where "Your 2019 login history" is a year: only punctuation and spaces between the word and
    // the number.
    private static bool DirectlyAfterKeyword(string text, int start, List<(int Start, int End)> keywords) =>
        keywords.Any(keyword => keyword.End <= start && text.AsSpan(keyword.End, start - keyword.End).IndexOfAnyExcept(" \t:-#") < 0);

    private static int Distance(int start, int end, List<(int Start, int End)> keywords)
    {
        var best = int.MaxValue;
        foreach (var (keywordStart, keywordEnd) in keywords)
        {
            var distance = keywordEnd <= start ? start - keywordEnd : end <= keywordStart ? keywordStart - end : 0;
            best = Math.Min(best, distance);
        }
        return best;
    }

    private static List<(int Start, int End)> Keywords(string text)
    {
        var found = new List<(int Start, int End)>();
        void Search(string word, StringComparison comparison)
        {
            for (var index = text.IndexOf(word, comparison); index >= 0; index = text.IndexOf(word, index + 1, comparison))
            {
                var end = index + word.Length;
                if (index > 0 && char.IsLetterOrDigit(text[index - 1])) continue;
                if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
                found.Add((index, end));
            }
        }
        foreach (var word in Words) Search(word, StringComparison.OrdinalIgnoreCase);
        foreach (var acronym in Acronyms) Search(acronym, StringComparison.Ordinal);
        return found;
    }
}
