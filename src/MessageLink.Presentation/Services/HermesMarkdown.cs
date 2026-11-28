using System.Text;
using System.Text.RegularExpressions;

namespace MessageLink.Presentation.Services;

public enum MarkdownBlockKind { Paragraph, Heading, Item, Code, Table, Rule }

/// <summary>
/// One block of a Hermes reply. <see cref="Depth"/>: an item's list level, or one more than the item a paragraph, code block or table
/// sits under. <see cref="Quote"/>: how many quotes it is inside. <see cref="Marker"/>: an item's bullet or number as shown.
/// <see cref="Code"/>: a code block's text. <see cref="Rows"/>: a table's cells, the header first; <see cref="Align"/> each column's
/// alignment (l, c or r).
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MessageTextRun> Runs, int Depth = 0, int Quote = 0, string? Marker = null,
    string Code = "", IReadOnlyList<IReadOnlyList<IReadOnlyList<MessageTextRun>>>? Rows = null, string Align = "");

/// <summary>
/// A Hermes reply's Markdown, read into blocks of styled runs for the bubble (owner, 2026-11-18). Data only, as MessageText is: there is no
/// HTML here at all, so "&lt;b&gt;" and "&amp;amp;" stay the words they are (HermesReportChecks); a link opens only an http or https
/// address, never one its words disguise (MessageText.Misleading), and a bare address is linked as anywhere else. Images are never
/// fetched: one is shown as a link to it. Written for what models write, not the whole of CommonMark: fenced code (no indented code),
/// ATX and underlined headings, nested lists, quotes, pipe tables and rules. A reply still streaming reads sensibly at every step: a
/// code fence not yet closed is code to the end, an emphasis not yet closed is its literal asterisks until it is.
/// </summary>
public static partial class HermesMarkdown
{
    private const int MaxQuotes = 8, MaxInlineDepth = 16, MaxLinkLength = 2000;

    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length > 65536) text = text[..65536] + "…";
        text = MessageText.Spoken(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        var blocks = new List<MarkdownBlock>();
        Blocks(text.Split('\n'), 0, blocks);
        return blocks;
    }

    /// <summary>The reply's words without its marks, a block a line: for the list's preview line and a notification.</summary>
    public static string PlainText(string? text) => string.Join('\n', Parse(text).Where(b => b.Kind != MarkdownBlockKind.Rule).Select(b => b.Kind switch
    {
        MarkdownBlockKind.Code => b.Code,
        MarkdownBlockKind.Table => string.Join('\n', (b.Rows ?? []).Select(row => string.Join("  ", row.Select(Words)))),
        MarkdownBlockKind.Item => b.Marker + " " + Words(b.Runs),
        _ => Words(b.Runs),
    }));
    private static string Words(IReadOnlyList<MessageTextRun> runs) => string.Concat(runs.Select(run => run.Text));

    // ---- Blocks ----

    [GeneratedRegex(@"^(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"^([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex Rule();
    // Three or more, so the first "-" of a list item still streaming in never turns the words above it into a heading for a moment.
    [GeneratedRegex(@"^(={3,}|-{3,})[ \t]*$")]
    private static partial Regex Underline();
    [GeneratedRegex(@"^([-*+]|(\d{1,9})([.)]))( +|$)(.*)$")]
    private static partial Regex ListMarker();
    [GeneratedRegex(@"^\[([ xX])\][ \t]+(.*)$")]
    private static partial Regex TaskBox();
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$")]
    private static partial Regex Delimiter();

    private static readonly string[] Bullets = ["•", "◦", "▪"];

    private static void Blocks(string[] lines, int quote, List<MarkdownBlock> into)
    {
        // Open lists: the column each item's marker is in and the column its words start at.
        var lists = new List<(int Marker, int Content)>();
        StringBuilder? para = null;
        var paraKind = MarkdownBlockKind.Paragraph;
        var paraDepth = 0;
        string? paraMarker = null;
        var blank = false;
        string? fence = null;
        var fenceIndent = 0;
        var fenceDepth = 0;
        var code = new StringBuilder();

        void Flush()
        {
            if (para is null) return;
            var words = para.ToString().Trim();
            if (words.Length > 0 || paraKind == MarkdownBlockKind.Item)
                into.Add(new(paraKind, Inline(words), paraDepth, quote, paraMarker));
            para = null; paraMarker = null;
        }
        void Start(MarkdownBlockKind kind, int depth, string? marker, string words)
        { para = new StringBuilder(Line(words)); paraKind = kind; paraDepth = depth; paraMarker = marker; }
        void Close()
        {
            var shown = code.ToString();
            if (shown.EndsWith('\n')) shown = shown[..^1];
            into.Add(new(MarkdownBlockKind.Code, [], fenceDepth, quote, Code: shown));
            fence = null; code.Clear();
        }
        // The level under the open lists that a block indented this far belongs to: deeper than an item's marker is inside it.
        int Within(int indent)
        {
            while (lists.Count > 0 && indent <= lists[^1].Marker) lists.RemoveAt(lists.Count - 1);
            return lists.Count;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Replace("\t", "    ", StringComparison.Ordinal);
            if (fence is not null)
            {
                var trimmed = line.TrimStart(' ');
                if (Indent(line) <= fenceIndent + 3 && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimEnd().All(c => c == fence[0])) { Close(); continue; }
                var strip = Math.Min(fenceIndent, Indent(line));
                code.Append(line[strip..]).Append('\n');
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { Flush(); blank = true; continue; }
            var indent = Indent(line);
            var body = line[indent..].TrimEnd();

            // A quote: this line and the ones after it that are quoted too, read again inside it.
            if (body.StartsWith('>') && quote < MaxQuotes)
            {
                Flush(); lists.Clear();
                var inside = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart(' ', '\t').StartsWith('>'); i++)
                {
                    var quoted = lines[i].TrimStart(' ', '\t')[1..];
                    inside.Add(quoted.StartsWith(' ') ? quoted[1..] : quoted);
                }
                i--;
                Blocks([.. inside], quote + 1, into);
                blank = false;
                continue;
            }
            if ((body.StartsWith("```", StringComparison.Ordinal) || body.StartsWith("~~~", StringComparison.Ordinal)) &&
                !(body[0] == '`' && body.TrimStart('`').Contains('`')))
            {
                Flush();
                fence = new string(body[0], body.TakeWhile(c => c == body[0]).Count());
                fenceIndent = indent; fenceDepth = Within(indent);
                blank = false;
                continue;
            }
            if (para is { } open && paraKind == MarkdownBlockKind.Paragraph && !blank && Underline().IsMatch(body) && open.Length > 0)
            {
                // Words underlined with = or - are a heading.
                para = null;
                into.Add(new(MarkdownBlockKind.Heading, Inline(open.ToString().Trim()), paraDepth, quote));
                continue;
            }
            if (Heading().Match(body) is { Success: true } heading && indent <= 3 + (lists.Count > 0 ? lists[^1].Content : 0))
            {
                Flush();
                var depth = Within(indent);
                into.Add(new(MarkdownBlockKind.Heading, Inline(heading.Groups[2].Value.Trim()), depth, quote));
                blank = false;
                continue;
            }
            if (Rule().IsMatch(body))
            {
                Flush(); lists.Clear();
                into.Add(new(MarkdownBlockKind.Rule, [], 0, quote));
                blank = false;
                continue;
            }
            if (body.Contains('|') && i + 1 < lines.Length && lines[i + 1].Contains('|') && Delimiter().IsMatch(lines[i + 1]))
            {
                Flush();
                var depth = Within(indent);
                var header = Cells(body);
                var align = new string([.. Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') && c.Length > 1 ? 'c' : c.EndsWith(':') ? 'r' : 'l')]);
                var rows = new List<IReadOnlyList<IReadOnlyList<MessageTextRun>>> { Row(header, header.Count) };
                for (i += 2; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'); i++) rows.Add(Row(Cells(lines[i]), header.Count));
                i--;
                into.Add(new(MarkdownBlockKind.Table, [], depth, quote, Rows: rows, Align: align.PadRight(header.Count, 'l')[..header.Count]));
                blank = false;
                continue;
            }
            if (ListMarker().Match(body) is { Success: true } item &&
                // A number only starts a list inside a paragraph when it is 1, so "2019. A good year." stays words.
                (para is null || blank || paraKind == MarkdownBlockKind.Item || !item.Groups[2].Success || item.Groups[2].Value == "1"))
            {
                Flush();
                var spaces = item.Groups[4].Value.Length;
                var content = indent + item.Groups[1].Value.Length + (spaces is 0 or > 4 ? 1 : spaces);
                while (lists.Count > 0 && indent < lists[^1].Marker) lists.RemoveAt(lists.Count - 1);
                if (lists.Count > 0 && indent <= lists[^1].Marker) lists[^1] = (indent, content);
                else lists.Add((indent, content));
                var level = lists.Count - 1;
                var marker = item.Groups[2].Success ? item.Groups[2].Value + item.Groups[3].Value : Bullets[Math.Min(level, Bullets.Length - 1)];
                var words = item.Groups[5].Value;
                // A task's box stands in for its bullet or number: "- [x] Done" is a ticked box, as GitHub draws it.
                if (TaskBox().Match(words) is { Success: true } task) { marker = task.Groups[1].Value == " " ? "☐" : "☑"; words = task.Groups[2].Value; }
                Start(MarkdownBlockKind.Item, level, marker, words);
                blank = false;
                continue;
            }
            // Words: more of the open paragraph, or a new one.
            if (para is not null && !blank) { para.Append('\n').Append(Line(body)); continue; }
            Flush();
            Start(MarkdownBlockKind.Paragraph, Within(indent), null, body);
            blank = false;
        }
        Flush();
        // Still streaming: what came after an open fence is code so far.
        if (fence is not null) Close();
    }

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }

    // A line's own break marks (two spaces or a backslash at its end) are the break that is kept anyway.
    private static string Line(string words)
    {
        words = words.TrimEnd();
        return words.EndsWith('\\') && !words.EndsWith("\\\\", StringComparison.Ordinal) ? words[..^1].TrimEnd() : words;
    }

    private static List<string> Cells(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal)) row = row[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var ticks = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (c == '`') ticks = !ticks;
            if (c == '|' && !ticks) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static IReadOnlyList<IReadOnlyList<MessageTextRun>> Row(List<string> cells, int columns) =>
        [.. Enumerable.Range(0, columns).Select(i => (IReadOnlyList<MessageTextRun>)(i < cells.Count ? Inline(cells[i]) : []))];

    // ---- Inline ----

    private readonly record struct Style(bool Bold, bool Italic, bool Strike);

    public static IReadOnlyList<MessageTextRun> Inline(string text)
    {
        var runs = new List<MessageTextRun>();
        Inline(text, default, runs, 0);
        // Neighbours in one style are one run: fewer pieces to draw, and a word is never split between two.
        var merged = new List<MessageTextRun>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1] is var last && last with { Text = run.Text } == run) merged[^1] = last with { Text = last.Text + run.Text };
            else merged.Add(run);
        }
        return merged;
    }

    private static void Inline(string s, Style style, List<MessageTextRun> runs, int depth)
    {
        var text = new StringBuilder();
        // Where a search for a closing mark already failed: a later one from further on fails too, so a reply full of lone asterisks or
        // backticks is read in one pass, not one pass for each.
        var failed = new Dictionary<(char, int), int>();
        int Find(char mark, int count, int from)
        {
            if (failed.TryGetValue((mark, count), out var after) && from >= after) return -1;
            var found = mark == '`' ? Ticks(s, from, count) : Closer(s, from, mark, count);
            if (found < 0) failed[(mark, count)] = failed.TryGetValue((mark, count), out var known) ? Math.Min(known, from) : from;
            return found;
        }
        void Flush()
        {
            if (text.Length == 0) return;
            MessageText.Linked(text.ToString(), runs, style.Bold, style.Italic, false, style.Strike);
            text.Clear();
        }
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            // A backslash before ASCII punctuation shows the character itself.
            if (c == '\\' && i + 1 < s.Length && s[i + 1] is > ' ' and < '\u007F' && !char.IsAsciiLetterOrDigit(s[i + 1]))
            { text.Append(s[i + 1]); i += 2; continue; }
            if (c == '`')
            {
                var n = Count(s, i, '`');
                var close = Find('`', n, i + n);
                if (close < 0) { text.Append(s, i, n); i += n; continue; }
                Flush();
                var code = s[(i + n)..close].Replace('\n', ' ');
                if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                runs.Add(new(code, null, style.Bold, style.Italic, true, style.Strike));
                i = close + n;
                continue;
            }
            if (c is '*' or '_' or '~' && depth < MaxInlineDepth)
            {
                var n = Count(s, i, c);
                var length = c == '~' ? 2 : Math.Min(n, 3);
                if ((c != '~' || n == 2) && n == length && Opens(s, i, length, c) && Find(c, length, i + length) is var close and >= 0)
                {
                    Flush();
                    var inner = c == '~' ? style with { Strike = true } : length == 3 ? style with { Bold = true, Italic = true }
                        : length == 2 ? style with { Bold = true } : style with { Italic = true };
                    Inline(s[(i + length)..close], inner, runs, depth + 1);
                    i = close + length;
                    continue;
                }
                text.Append(s, i, n); i += n;
                continue;
            }
            if ((c == '[' || c == '!' && i + 1 < s.Length && s[i + 1] == '[') && Link(s, c == '!' ? i + 1 : i) is { } link && depth < MaxInlineDepth)
            {
                Flush();
                var image = c == '!';
                var words = new List<MessageTextRun>();
                Inline(link.Words.Length > 0 ? link.Words : image ? link.Url : "", style, words, depth + 1);
                var target = MessageText.WebLink(link.Url);
                var shown = string.Concat(words.Select(run => run.Text));
                if (target is not null && shown.Trim().Length == 0) runs.Add(new(link.Url, target, style.Bold, style.Italic, false, style.Strike));
                // Words that name another site are shown as the words they are, an address in them linked to itself (MessageText.Misleading).
                else if (target is not null && !MessageText.Misleading(shown, target)) runs.AddRange(words.Select(run => run with { Link = target }));
                else runs.AddRange(words);
                i = link.End;
                continue;
            }
            if (c == '<' && s.IndexOf('>', i + 1) is var end and > 0 && s[(i + 1)..end] is var inside && !inside.Contains(' ') &&
                MessageText.WebLink(inside) is { } address)
            {
                Flush();
                runs.Add(new(inside, address, style.Bold, style.Italic, false, style.Strike));
                i = end + 1;
                continue;
            }
            text.Append(c);
            i++;
        }
        Flush();
    }

    private static int Count(string s, int at, char c)
    {
        var end = at;
        while (end < s.Length && s[end] == c) end++;
        return end - at;
    }

    // The start of the next run of exactly n backticks.
    private static int Ticks(string s, int from, int n)
    {
        for (var j = from; j < s.Length;)
        {
            if (s[j] != '`') { j++; continue; }
            var run = Count(s, j, '`');
            if (run == n) return j;
            j += run;
        }
        return -1;
    }

    // An emphasis opens before a word, and an underscore only at a word's start (snake_case stays as it is).
    private static bool Opens(string s, int at, int length, char c) =>
        at + length < s.Length && !char.IsWhiteSpace(s[at + length]) && (c != '_' || at == 0 || !char.IsLetterOrDigit(s[at - 1]));

    private static int Closer(string s, int from, char c, int length)
    {
        for (var j = from + 1; j < s.Length;)
        {
            if (s[j] == '`') { var n = Count(s, j, '`'); var close = Ticks(s, j + n, n); j = close < 0 ? j + n : close + n; continue; }
            if (s[j] != c) { j++; continue; }
            var run = Count(s, j, c);
            if (run == length && !char.IsWhiteSpace(s[j - 1]) && (c != '_' || j + run >= s.Length || !char.IsLetterOrDigit(s[j + run]))) return j;
            j += run;
        }
        return -1;
    }

    private sealed record LinkText(string Words, string Url, int End);

    // [words](address "title"), from the opening bracket.
    private static LinkText? Link(string s, int at)
    {
        var nest = 0;
        var close = -1;
        for (var j = at; j < s.Length && j < at + MaxLinkLength; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == '`') { var n = Count(s, j, '`'); var end = Ticks(s, j + n, n); if (end >= 0) j = end + n - 1; continue; }
            if (s[j] == '[') nest++;
            else if (s[j] == ']' && --nest == 0) { close = j; break; }
        }
        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(') return null;
        var k = close + 2;
        while (k < s.Length && s[k] == ' ') k++;
        string url;
        if (k < s.Length && s[k] == '<')
        {
            var end = s.IndexOf('>', k);
            if (end < 0) return null;
            url = s[(k + 1)..end];
            k = end + 1;
        }
        else
        {
            var start = k;
            var parens = 0;
            for (; k < s.Length && !char.IsWhiteSpace(s[k]); k++)
            {
                if (s[k] == '(') parens++;
                else if (s[k] == ')' && parens-- == 0) break;
            }
            url = s[start..k];
        }
        while (k < s.Length && s[k] == ' ') k++;
        if (k < s.Length && s[k] is '"' or '\'')
        {
            var end = s.IndexOf(s[k], k + 1);
            if (end < 0) return null;
            k = end + 1;
            while (k < s.Length && s[k] == ' ') k++;
        }
        if (k >= s.Length || s[k] != ')') return null;
        return new(s[(at + 1)..close], url, k + 1);
    }
}
