using System.Globalization;
using System.Text;

namespace MessageLink.Presentation.Services;

/// <summary>How a command in a chain follows the one before it: first, ";" (always) or "&amp;&amp;" (only if it worked).</summary>
public enum ConsoleJoin { First, Always, IfOk }

/// <summary>One stage after a pipe: a filter over the lines the command before it printed.</summary>
public sealed record ConsoleFilter(string Name, IReadOnlyList<string> Args);

/// <summary>One command of a chain with the filters its output goes through.</summary>
public sealed record ConsolePipeline(ConsoleJoin Join, string Command, IReadOnlyList<ConsoleFilter> Filters);

/// <summary>A line of command output: its inks, and whether it reports a failure.</summary>
public sealed record ConsoleOutLine(IReadOnlyList<ConsoleSpan> Spans, bool Error = false)
{
    public string Text { get; } = string.Concat(Spans.Select(span => span.Text));
    public static ConsoleOutLine Plain(string text, ConsoleTone tone = ConsoleTone.Default) => new([new ConsoleSpan(text, tone)]);
}

/// <summary>
/// The shell around console commands: splitting a line into a chain (";", "&amp;&amp;") of pipelines ("|"), and the filters a
/// pipeline's output goes through. Pure. A line that can send — plain text, //text, /r, /w and its aliases — is never
/// split: it is one message, whatever it contains, and nothing that sends can be chained or piped.
/// </summary>
public static class ConsoleShell
{
    public static readonly IReadOnlyList<string> FilterNames = ["grep", "head", "tail", "sort", "uniq", "wc", "nl", "count", "copy", "less", "cowsay"];
    public const string NoSendsInChains = "Sends can't be chained or piped. Type the message on a line of its own.";

    /// <summary>The chain a line is, or the reason it is not one.</summary>
    public static (IReadOnlyList<ConsolePipeline> Chain, string? Error) Split(string line)
    {
        var text = line.Trim();
        if (ConsoleCatalog.StartsWithSend(text) || IsWord(text, "alias") || IsWord(text, "unalias"))
            return ([new ConsolePipeline(ConsoleJoin.First, text, [])], null);
        var chain = new List<ConsolePipeline>();
        var join = ConsoleJoin.First;
        string? command = null;
        var filters = new List<ConsoleFilter>();
        var piece = new StringBuilder();
        var quoted = false;

        string? Close(bool pipe)
        {
            var part = piece.ToString().Trim();
            piece.Clear();
            if (part.Length == 0) return "Something is missing around a ; | or &&.";
            if (command is null)
            {
                if (ConsoleCatalog.StartsWithSend(part)) return NoSendsInChains;
                command = part;
            }
            else
            {
                var words = ConsoleCommandParser.Words(part);
                // A filter of only an empty quote ("" or " ") has no words at all.
                if (words.Count == 0) return "Something is missing around a ; | or &&.";
                var name = words[0].TrimStart('/').ToLowerInvariant();
                if (!FilterNames.Contains(name))
                    return $"There's no filter called “{words[0]}”. Filters: {string.Join(", ", FilterNames)}.";
                filters.Add(new ConsoleFilter(name, [.. words.Skip(1)]));
            }
            if (!pipe)
            {
                chain.Add(new ConsolePipeline(join, command, [.. filters]));
                command = null;
                filters.Clear();
            }
            return null;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '"') { quoted = !quoted; piece.Append(c); continue; }
            if (quoted) { piece.Append(c); continue; }
            string? error = null;
            if (c == '|') error = Close(pipe: true);
            else if (c == ';') { error = Close(pipe: false); join = ConsoleJoin.Always; }
            else if (c == '&' && index + 1 < text.Length && text[index + 1] == '&') { error = Close(pipe: false); join = ConsoleJoin.IfOk; index++; }
            else { piece.Append(c); continue; }
            if (error is not null) return ([], error);
        }
        if (Close(pipe: false) is { } last) return ([], last);
        return (chain, null);
    }

    private static bool IsWord(string text, string word) =>
        text.StartsWith("/" + word, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == word.Length + 1 || char.IsWhiteSpace(text[word.Length + 1]));

    /// <summary>
    /// Applies the filters to the lines in order. less and copy end a pipeline: what reaches them is handed to
    /// <paramref name="pager"/> or <paramref name="copy"/>, and nothing is printed (null comes back).
    /// </summary>
    public static IReadOnlyList<ConsoleOutLine>? Apply(IReadOnlyList<ConsoleOutLine> lines, IReadOnlyList<ConsoleFilter> filters,
        Action<IReadOnlyList<ConsoleOutLine>> pager, Action<string> copy, out string? error)
    {
        error = null;
        IReadOnlyList<ConsoleOutLine> current = lines;
        foreach (var filter in filters)
        {
            switch (filter.Name)
            {
                case "grep":
                    var invert = filter.Args.Any(a => a == "-v");
                    var pattern = string.Join(' ', filter.Args.Where(a => a is not ("-v" or "-i")));
                    if (pattern.Length == 0) { error = "Type what to look for: | grep words"; return null; }
                    // Smart case, as ripgrep and vim have it: a capital letter in the pattern makes it exact.
                    var comparison = pattern.Any(char.IsUpper) && !filter.Args.Contains("-i") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    current = [.. current.Where(line => line.Text.Contains(pattern, comparison) != invert)
                        .Select(line => invert ? line : Highlight(line, pattern, comparison))];
                    break;
                case "head" or "tail":
                    if (Count(filter.Args, 10) is not { } n) { error = $"Use | {filter.Name} or | {filter.Name} 20."; return null; }
                    current = filter.Name == "head" ? [.. current.Take(n)] : [.. current.TakeLast(n)];
                    break;
                case "sort":
                    var ordered = current.OrderBy(line => line.Text, StringComparer.CurrentCultureIgnoreCase);
                    current = filter.Args.Contains("-r") ? [.. ordered.Reverse()] : [.. ordered];
                    break;
                case "uniq":
                    var kept = new List<ConsoleOutLine>();
                    foreach (var line in current) if (kept.Count == 0 || kept[^1].Text != line.Text) kept.Add(line);
                    current = kept;
                    break;
                case "wc" or "count":
                    var words = current.Sum(line => line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                    var chars = current.Sum(line => line.Text.Length);
                    current = filter.Name == "count" || filter.Args.Contains("-l")
                        ? [ConsoleOutLine.Plain(current.Count.ToString(CultureInfo.CurrentCulture))]
                        : [ConsoleOutLine.Plain($"{current.Count,7} {words,7} {chars,7}")];
                    break;
                case "nl":
                    current = [.. current.Select((line, i) => new ConsoleOutLine(
                        [new ConsoleSpan($"{i + 1,5}  ", ConsoleTone.Dim), .. line.Spans], line.Error))];
                    break;
                case "cowsay":
                    current = [.. Cow(string.Join(" ", current.Select(line => line.Text.Trim())).Trim()).Select(row => ConsoleOutLine.Plain(row))];
                    break;
                case "copy":
                    copy(string.Join(Environment.NewLine, current.Select(line => line.Text)));
                    return [ConsoleOutLine.Plain($"Copied {Lines(current.Count)}.", ConsoleTone.Dim)];
                case "less":
                    pager(current);
                    return null;
            }
        }
        return current;
    }

    private static string Lines(int count) => count == 1 ? "1 line" : count.ToString(CultureInfo.CurrentCulture) + " lines";

    // "20", "-20" or "-n 20".
    private static int? Count(IReadOnlyList<string> args, int fallback)
    {
        var words = args.Where(a => a != "-n").ToArray();
        if (words.Length == 0) return fallback;
        var word = words[0].TrimStart('-');
        return words.Length == 1 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 100000 ? n : null;
    }

    /// <summary>The line with every occurrence of the pattern in the Match ink, other inks kept.</summary>
    public static ConsoleOutLine Highlight(ConsoleOutLine line, string pattern, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (pattern.Length == 0) return line;
        var spans = new List<ConsoleSpan>();
        foreach (var span in line.Spans)
        {
            var at = 0;
            while (at < span.Text.Length)
            {
                var found = span.Text.IndexOf(pattern, at, comparison);
                if (found < 0) { spans.Add(span with { Text = span.Text[at..] }); break; }
                if (found > at) spans.Add(span with { Text = span.Text[at..found] });
                spans.Add(new ConsoleSpan(span.Text.Substring(found, pattern.Length), ConsoleTone.Match));
                at = found + pattern.Length;
            }
        }
        return new ConsoleOutLine(spans, line.Error);
    }

    /// <summary>A cow, saying it, wrapped at 40 columns.</summary>
    public static IReadOnlyList<string> Cow(string text)
    {
        if (text.Length == 0) text = "Moo.";
        var lines = Wrap(text, 40);
        var width = lines.Max(line => line.Length);
        var rows = new List<string> { " " + new string('_', width + 2) };
        for (var i = 0; i < lines.Count; i++)
        {
            var (open, close) = lines.Count == 1 ? ('<', '>') : i == 0 ? ('/', '\\') : i == lines.Count - 1 ? ('\\', '/') : ('|', '|');
            rows.Add($"{open} {lines[i].PadRight(width)} {close}");
        }
        rows.Add(" " + new string('-', width + 2));
        rows.AddRange(["        \\   ^__^", "         \\  (oo)\\_______", "            (__)\\       )\\/\\", "                ||----w |", "                ||     ||"]);
        return rows;
    }

    /// <summary>Words wrapped to a width; a word longer than the width is cut.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (rest.Length > width) { if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); } lines.Add(rest[..width]); rest = rest[width..]; }
            if (current.Length > 0 && current.Length + 1 + rest.Length > width) { lines.Add(current.ToString()); current.Clear(); }
            if (current.Length > 0) current.Append(' ');
            current.Append(rest);
        }
        if (current.Length > 0 || lines.Count == 0) lines.Add(current.ToString());
        return lines;
    }
}
