using System.Globalization;

namespace MessageLink.Presentation.Services;

public static class SearchSnippet
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    // Word-start matches for each query word, ignoring case and accents, ordered and merged.
    public static IReadOnlyList<(int Start, int Length)> Matches(string text, string query)
    {
        var found = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (var word in TextSearchIndex.Words(query).Distinct(StringComparer.Ordinal))
        {
            for (var at = 0; at < text.Length;)
            {
                var relative = Compare.IndexOf(text.AsSpan(at), word, Options, out var length);
                if (relative < 0) break;
                var index = at + relative;
                if (index == 0 || !char.IsLetterOrDigit(text[index - 1])) found.Add((index, length));
                at = index + Math.Max(1, length);
            }
        }
        var merged = new List<(int Start, int Length)>();
        foreach (var match in found.OrderBy(m => m.Start))
        {
            if (merged.Count > 0 && match.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, match.Start + match.Length) - last.Start);
            }
            else merged.Add(match);
        }
        return merged;
    }

    // One line of text around the first match, trimmed at word boundaries.
    public static string Excerpt(string text, string query, int maxLength = 100)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= maxLength) return text;
        var first = Matches(text, query) is { Count: > 0 } matches ? matches[0].Start : 0;
        var start = 0;
        if (first > 30)
        {
            var space = text.LastIndexOf(' ', first - 20);
            start = space >= 0 ? space + 1 : first - 20;
        }
        var end = Math.Min(text.Length, start + maxLength);
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, Math.Min(20, end - start));
            if (space > start) end = space;
        }
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }
}
