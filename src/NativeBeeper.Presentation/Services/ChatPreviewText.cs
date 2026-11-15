using System.Text.RegularExpressions;

namespace NativeBeeper.Presentation.Services;

public static partial class ChatPreviewText
{
    // Google's reaction previews look like: 😂 to " Unfortunate looking ".
    [GeneratedRegex("^(?<emoji>\\S{1,16}) to [\"“]\\s*(?<text>.*?)\\s*[\"”]$")]
    private static partial Regex Reaction();

    // sender: who wrote an incoming group message, as the row should name them (see Sender), or null to name nobody.
    public static string Format(bool outgoing, string text, string? sender = null)
    {
        var match = Reaction().Match(text);
        if (match.Success && !match.Groups["emoji"].Value.Any(char.IsLetterOrDigit))
            return (outgoing ? "You reacted " : sender is { Length: > 0 } ? sender + " reacted " : "Reacted ") + match.Groups["emoji"].Value + " to “" + match.Groups["text"].Value + "”";
        return (outgoing ? "You: " : sender is { Length: > 0 } ? sender + ": " : "") + text;
    }

    /// <summary>
    /// The name a group row puts before its last message: the sender's first name, or their whole name when another
    /// member shares that first name or the name is a number rather than a word.
    /// </summary>
    public static string? Sender(string? name, IEnumerable<string?> members)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!name.Any(char.IsLetter)) return name;
        var first = ListTyping.FirstName(name);
        var shared = members.Count(m => m is { Length: > 0 } && !string.Equals(m.Trim(), name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ListTyping.FirstName(m), first, StringComparison.OrdinalIgnoreCase));
        return shared > 0 ? name : first;
    }
}
