using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MessageLink.Presentation.Help;

public enum HelpInlineKind { Text, Bold, Italic, Code, Key, Link }

/// <summary>A run of guide text. Link's Target is a guide id ("console", "send-later"); Key is a keycap ("Ctrl+K").</summary>
public sealed record HelpInline(HelpInlineKind Kind, string Text, string? Target = null);

public abstract record HelpBlock
{
    public sealed record Paragraph(IReadOnlyList<HelpInline> Inlines) : HelpBlock;
    /// <summary>A heading inside a chapter: level 3 is a section (listed in the contents and linkable), 4 a minor one.</summary>
    public sealed record Heading(int Level, string Text, string Id) : HelpBlock;
    public sealed record List(bool Ordered, IReadOnlyList<HelpListItem> Items) : HelpBlock;
    public sealed record Table(IReadOnlyList<IReadOnlyList<HelpInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<HelpInline>>> Rows) : HelpBlock;
    /// <summary>"Tip", "Note", "Careful" or "Safe": a boxed aside, from a quote that starts with that word in bold.</summary>
    public sealed record Callout(string Kind, IReadOnlyList<HelpInline> Inlines) : HelpBlock;
    public sealed record Code(string Text) : HelpBlock;
}

public sealed record HelpListItem(IReadOnlyList<HelpInline> Inlines, IReadOnlyList<HelpListItem> Children);

/// <summary>A level-2 part of the guide, shown as one page, with its level-3 sections for the contents.</summary>
public sealed record HelpChapter(string Id, string Title, int Number, IReadOnlyList<HelpBlock> Blocks, IReadOnlyList<(string Id, string Title)> Sections);

public sealed record HelpSearchResult(HelpChapter Chapter, string? SectionId, string Title, string Snippet, int Score);

/// <summary>
/// The Messages guide: one Markdown file shipped inside the app (Help/guide.md), read into chapters for the Help window
/// and searched there. The Markdown is a small, strict subset so it reads the same on GitHub and in the app: # title,
/// ## chapters and ### sections (an explicit {#id} names them), paragraphs, - and 1. lists (one level of nesting),
/// | tables |, ``` code ```, &gt; **Tip:** asides, and inline **bold**, *italic*, `code`, &lt;kbd&gt;keys&lt;/kbd&gt; and
/// [links](#id) to other parts of the guide. Pure: no I/O beyond reading the embedded file.
/// </summary>
public sealed partial class HelpGuide
{
    public string Title { get; }
    public IReadOnlyList<HelpBlock> Intro { get; }
    public IReadOnlyList<HelpChapter> Chapters { get; }
    // Every id, chapter or section, to the chapter holding it.
    private readonly Dictionary<string, HelpChapter> ids;

    private HelpGuide(string title, IReadOnlyList<HelpBlock> intro, IReadOnlyList<HelpChapter> chapters)
    {
        Title = title; Intro = intro; Chapters = chapters;
        ids = new Dictionary<string, HelpChapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            ids.TryAdd(chapter.Id, chapter);
            foreach (var block in chapter.Blocks.OfType<HelpBlock.Heading>()) ids.TryAdd(block.Id, chapter);
        }
    }

    private static HelpGuide? loaded;

    /// <summary>The guide shipped inside the app, read once.</summary>
    public static HelpGuide Load()
    {
        if (loaded is not null) return loaded;
        using var stream = typeof(HelpGuide).Assembly.GetManifestResourceStream("MessageLink.Presentation.Help.guide.md")
            ?? throw new InvalidOperationException("The guide is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return loaded = Parse(reader.ReadToEnd());
    }

    /// <summary>Every id the guide defines, chapters and sections.</summary>
    public IEnumerable<string> Ids => ids.Keys;

    /// <summary>The chapter an id is in; null for an id the guide does not have.</summary>
    public HelpChapter? ChapterOf(string id) => ids.GetValueOrDefault(id.Trim().TrimStart('#'));

    /// <summary>
    /// Where a topic typed by a person goes: an id, then a chapter or section whose title starts with or contains it,
    /// then the best search result. Null when nothing matches.
    /// </summary>
    public (HelpChapter Chapter, string? SectionId)? Resolve(string? topic)
    {
        var text = (topic ?? "").Trim().TrimStart('/', '#');
        if (text.Length == 0) return null;
        if (ChapterOf(text) is { } exact) return (exact, exact.Id.Equals(text, StringComparison.OrdinalIgnoreCase) ? null : text.ToLowerInvariant());
        foreach (var chapter in Chapters)
            if (chapter.Title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return (chapter, null);
        foreach (var chapter in Chapters)
            foreach (var (id, title) in chapter.Sections)
                if (title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return (chapter, id);
        foreach (var chapter in Chapters)
            if (chapter.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return (chapter, null);
        return Search(text).FirstOrDefault() is { } best ? (best.Chapter, best.SectionId) : null;
    }

    // ---- Search ----

    /// <summary>
    /// Sections holding every word of the query, best first: a word in a title counts far more than one in the text.
    /// Each result has a snippet around the first match.
    /// </summary>
    public IReadOnlyList<HelpSearchResult> Search(string query, int limit = 30)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"')).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return [];
        var results = new List<HelpSearchResult>();
        foreach (var chapter in Chapters)
            foreach (var (id, title, text) in Parts(chapter))
            {
                var haystack = title + "\n" + text;
                if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) continue;
                var score = words.Sum(w => (title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ? 20 : 0) + Count(text, w));
                if (id is null) score += 2;
                results.Add(new HelpSearchResult(chapter, id, title, Snippet(text, words[0]), score));
            }
        return [.. results.OrderByDescending(r => r.Score).ThenBy(r => r.Chapter.Number).Take(limit)];
    }

    private static int Count(string text, string word)
    {
        var count = 0;
        for (var at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase); at >= 0 && count < 10; at = text.IndexOf(word, at + word.Length, StringComparison.CurrentCultureIgnoreCase)) count++;
        return count;
    }

    // A chapter's opening (before its first section) and each section, as plain text.
    private static IEnumerable<(string? Id, string Title, string Text)> Parts(HelpChapter chapter)
    {
        string? id = null;
        var title = chapter.Title;
        var text = new StringBuilder();
        foreach (var block in chapter.Blocks)
        {
            if (block is HelpBlock.Heading { Level: 3 } heading)
            {
                yield return (id, title, text.ToString());
                id = heading.Id; title = heading.Text; text.Clear();
                continue;
            }
            text.Append(PlainText(block)).Append('\n');
        }
        yield return (id, title, text.ToString());
    }

    private static string Snippet(string text, string word)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        var at = flat.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return flat.Length > 140 ? flat[..140].TrimEnd() + "…" : flat;
        var start = Math.Max(0, at - 50);
        if (start > 0) { var space = flat.IndexOf(' ', start); if (space >= 0 && space < at) start = space + 1; }
        var end = Math.Min(flat.Length, start + 150);
        return (start > 0 ? "…" : "") + flat[start..end].Trim() + (end < flat.Length ? "…" : "");
    }

    /// <summary>A block's words without marks, as search and a screen reader read them.</summary>
    public static string PlainText(HelpBlock block) => block switch
    {
        HelpBlock.Paragraph p => Plain(p.Inlines),
        HelpBlock.Heading h => h.Text,
        HelpBlock.List l => string.Join("\n", Flatten(l.Items).Select(Plain)),
        HelpBlock.Table t => string.Join("\n", new[] { t.Header }.Concat(t.Rows).Select(row => string.Join(" · ", row.Select(Plain)))),
        HelpBlock.Callout c => c.Kind + ": " + Plain(c.Inlines),
        HelpBlock.Code c => c.Text,
        _ => "",
    };

    public static string Plain(IEnumerable<HelpInline> inlines) => string.Concat(inlines.Select(i => i.Text));

    private static IEnumerable<IReadOnlyList<HelpInline>> Flatten(IEnumerable<HelpListItem> items)
    {
        foreach (var item in items)
        {
            yield return item.Inlines;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    /// <summary>The whole guide as plain text, for checks.</summary>
    public string AllText => string.Join("\n", Intro.Select(PlainText).Concat(Chapters.SelectMany(c => new[] { c.Title }.Concat(c.Blocks.Select(PlainText)))));

    /// <summary>Every link target the guide uses.</summary>
    public IEnumerable<string> LinkTargets => Intro.Concat(Chapters.SelectMany(c => c.Blocks)).SelectMany(InlinesOf).Where(i => i.Kind == HelpInlineKind.Link).Select(i => i.Target!);

    private static IEnumerable<HelpInline> InlinesOf(HelpBlock block) => block switch
    {
        HelpBlock.Paragraph p => p.Inlines,
        HelpBlock.List l => Flatten(l.Items).SelectMany(i => i),
        HelpBlock.Table t => t.Header.SelectMany(c => c).Concat(t.Rows.SelectMany(r => r.SelectMany(c => c))),
        HelpBlock.Callout c => c.Inlines,
        _ => [],
    };

    // ---- Parsing ----

    [GeneratedRegex(@"^(#{1,4})\s+(.*?)\s*(?:\{#([a-z0-9-]+)\})?\s*$")]
    private static partial Regex HeadingLine();
    [GeneratedRegex(@"^(\s*)(?:[-*]|(\d+)\.)\s+(.*)$")]
    private static partial Regex ListLine();
    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();
    [GeneratedRegex(@"\*\*(.+?)\*\*|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|`([^`]+)`|<kbd>(.+?)</kbd>|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineMark();

    public static HelpGuide Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var title = "Help";
        var intro = new List<HelpBlock>();
        var chapters = new List<HelpChapter>();
        string? chapterId = null, chapterTitle = null;
        var blocks = intro;
        var current = new List<HelpBlock>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CloseChapter()
        {
            if (chapterId is null) return;
            var sections = current.OfType<HelpBlock.Heading>().Where(h => h.Level == 3).Select(h => (h.Id, h.Text)).ToArray();
            chapters.Add(new HelpChapter(chapterId, chapterTitle!, chapters.Count + 1, [.. current], sections));
        }

        string Unique(string id)
        {
            var candidate = id.Length == 0 ? "section" : id;
            for (var n = 2; !used.Add(candidate); n++) candidate = id + "-" + n;
            return candidate;
        }

        var index = 0;
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new HelpBlock.Paragraph(Inlines(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                FlushParagraph();
                while (index < lines.Length && !lines[index].Contains("-->", StringComparison.Ordinal)) index++;
                index++;
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); index++; continue; }
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index++]);
                index++;
                blocks.Add(new HelpBlock.Code(string.Join("\n", code)));
                continue;
            }
            if (HeadingLine().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length;
                var text = heading.Groups[2].Value;
                var id = heading.Groups[3].Success ? heading.Groups[3].Value : Slug(text);
                if (level == 1) title = text;
                else if (level == 2)
                {
                    CloseChapter();
                    chapterId = Unique(id); chapterTitle = StripNumber(text);
                    current = [];
                    blocks = current;
                }
                else blocks.Add(new HelpBlock.Heading(level, text, Unique(id)));
                index++;
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>')) quote.Add(lines[index++].TrimStart()[1..].Trim());
                var body = string.Join(" ", quote);
                var kind = "Note";
                if (Regex.Match(body, @"^\*\*(Tip|Note|Careful|Safe|Try it)[:.]?\*\*:?\s*") is { Success: true } lead)
                {
                    kind = lead.Groups[1].Value;
                    body = body[lead.Length..];
                }
                blocks.Add(new HelpBlock.Callout(kind, Inlines(body)));
                continue;
            }
            if (trimmed.StartsWith('|') && index + 1 < lines.Length && TableRule().IsMatch(lines[index + 1].Trim()))
            {
                FlushParagraph();
                var header = Cells(trimmed);
                index += 2;
                var rows = new List<IReadOnlyList<IReadOnlyList<HelpInline>>>();
                while (index < lines.Length && lines[index].Trim().StartsWith('|')) rows.Add(Cells(lines[index++].Trim()));
                blocks.Add(new HelpBlock.Table(header, rows));
                continue;
            }
            if (ListLine().Match(line) is { Success: true } first)
            {
                FlushParagraph();
                var ordered = first.Groups[2].Success;
                var items = new List<HelpListItem>();
                var baseIndent = first.Groups[1].Value.Length;
                while (index < lines.Length && ListLine().Match(lines[index]) is { Success: true } item)
                {
                    var indent = item.Groups[1].Value.Length;
                    // A numbered list straight after a bulleted one (or the reverse) is a list of its own.
                    if (indent <= baseIndent && item.Groups[2].Success != ordered && items.Count > 0) break;
                    var text = item.Groups[3].Value;
                    index++;
                    // A wrapped item continues on indented lines that are not items themselves.
                    while (index < lines.Length && lines[index].StartsWith("  ", StringComparison.Ordinal) && lines[index].Trim().Length > 0 &&
                        !ListLine().IsMatch(lines[index])) text += " " + lines[index++].Trim();
                    if (indent > baseIndent && items.Count > 0)
                        items[^1] = items[^1] with { Children = [.. items[^1].Children, new HelpListItem(Inlines(text), [])] };
                    else items.Add(new HelpListItem(Inlines(text), []));
                }
                blocks.Add(new HelpBlock.List(ordered, items));
                continue;
            }
            paragraph.Add(trimmed);
            index++;
        }
        FlushParagraph();
        CloseChapter();
        return new HelpGuide(title, intro, chapters);
    }

    // "16. The chat console" → "The chat console": numbers are the app's to draw.
    private static string StripNumber(string text) => Regex.Replace(text, @"^\d+\.\s+", "");

    public static string Slug(string text)
    {
        var slug = Regex.Replace(StripNumber(text).ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }

    private static IReadOnlyList<IReadOnlyList<HelpInline>> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];
        // A "\|" inside a cell is a literal bar (a keyboard shortcut list, a command synopsis).
        return [.. Regex.Split(text, @"(?<!\\)\|").Select(cell => Inlines(cell.Trim().Replace("\\|", "|")))];
    }

    /// <summary>Inline marks read into runs.</summary>
    public static IReadOnlyList<HelpInline> Inlines(string text)
    {
        var result = new List<HelpInline>();
        var at = 0;
        foreach (Match match in InlineMark().Matches(text))
        {
            if (match.Index > at) result.Add(new HelpInline(HelpInlineKind.Text, Unescape(text[at..match.Index])));
            if (match.Groups[1].Success) result.Add(new HelpInline(HelpInlineKind.Bold, Unescape(match.Groups[1].Value)));
            else if (match.Groups[2].Success) result.Add(new HelpInline(HelpInlineKind.Italic, Unescape(match.Groups[2].Value)));
            else if (match.Groups[3].Success) result.Add(new HelpInline(HelpInlineKind.Code, match.Groups[3].Value));
            else if (match.Groups[4].Success) result.Add(new HelpInline(HelpInlineKind.Key, match.Groups[4].Value));
            else
            {
                var target = match.Groups[6].Value;
                // Only the guide's own parts are links; anything else stays words, so the help never opens a browser.
                result.Add(target.StartsWith('#')
                    ? new HelpInline(HelpInlineKind.Link, Unescape(match.Groups[5].Value), target[1..])
                    : new HelpInline(HelpInlineKind.Text, Unescape(match.Groups[5].Value)));
            }
            at = match.Index + match.Length;
        }
        if (at < text.Length) result.Add(new HelpInline(HelpInlineKind.Text, Unescape(text[at..])));
        return result;
    }

    private static string Unescape(string text) => Regex.Replace(text, @"\\([\\`*_\[\]#|<>])", "$1");
}
