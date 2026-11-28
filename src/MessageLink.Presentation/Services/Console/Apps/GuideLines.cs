using System.Globalization;
using MessageLink.Presentation.Help;

namespace MessageLink.Presentation.Services;

/// <summary>The Message Link guide as plain lines for the console's pager: a terminal has no window to open it in.</summary>
public static class GuideLines
{
    /// <summary>
    /// The chapter a topic names (a guide id, a title or words), or with none found the guide's start and its chapters. Null when the guide can't be
    /// read.
    /// </summary>
    public static IReadOnlyList<ConsoleOutLine>? Render(string? topic)
    {
        HelpGuide guide;
        try { guide = HelpGuide.Load(); }
        catch (InvalidOperationException) { return null; }
        var lines = new List<ConsoleOutLine>();
        if (guide.Resolve(topic) is { } found)
        {
            lines.Add(ConsoleOutLine.Plain(found.Chapter.Title, ConsoleTone.Strong));
            lines.Add(ConsoleOutLine.Plain(""));
            foreach (var block in found.Chapter.Blocks) Add(lines, block);
        }
        else
        {
            lines.Add(ConsoleOutLine.Plain(guide.Title, ConsoleTone.Strong));
            lines.Add(ConsoleOutLine.Plain(""));
            foreach (var block in guide.Intro) Add(lines, block);
            lines.Add(ConsoleOutLine.Plain(""));
            foreach (var chapter in guide.Chapters) lines.Add(ConsoleOutLine.Plain($"{chapter.Number.ToString(CultureInfo.CurrentCulture)}. {chapter.Title}"));
        }
        return lines;
    }

    private static void Add(List<ConsoleOutLine> lines, HelpBlock block)
    {
        var text = HelpGuide.PlainText(block);
        var tone = block is HelpBlock.Heading ? ConsoleTone.Strong : block is HelpBlock.Code ? ConsoleTone.Dim : ConsoleTone.Default;
        foreach (var line in text.Split('\n')) lines.Add(ConsoleOutLine.Plain(line.TrimEnd('\r'), tone));
        lines.Add(ConsoleOutLine.Plain(""));
    }
}
