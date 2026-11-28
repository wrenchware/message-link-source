using System.Globalization;

namespace MessageLink.Presentation.Services;

public static class EmojiText
{
    private static readonly Lazy<HashSet<string>> Sequences = new(() =>
    {
        using var stream = typeof(EmojiText).Assembly.GetManifestResourceStream("MessageLink.Presentation.Services.EmojiSequences.txt")!;
        using var reader = new StreamReader(stream);
        var values = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) values.Add(string.Concat(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(code => char.ConvertFromUtf32(int.Parse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))));
        return values;
    });

    // One to three complete emoji; ordinary text, bare digits and malformed sequences stay normal.
    public static bool IsShortEmojiOnly(string text)
    {
        if (text.Length > 256) return false;
        var elements = StringInfo.GetTextElementEnumerator(text.Trim());
        var count = 0;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (++count > 3 || !Sequences.Value.Contains(element)) return false;
        }
        return count > 0;
    }
}
