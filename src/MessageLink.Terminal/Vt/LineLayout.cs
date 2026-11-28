using System.Globalization;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Vt;

/// <summary>
/// One console line laid out in cells, as the window's rows drew it: a time column, a service tag column and the body, the body wrapped under itself
/// (a hanging indent to the body column). A wide line (command output) runs the whole width, its leading spaces kept as the indent its wrapped rows
/// hang under. In a terminal too narrow for the columns, the time and tag run in front of the body instead. Every row is at most the width it is laid
/// out to.
/// </summary>
public sealed class LineLayout
{
    public const int Gap = 1;
    // The fewest cells a body column is laid out to; a terminal with less than that lays the line out narrow.
    private const int MinBody = 10;
    private const string You = "You → ";
    private static readonly string[] Tags = ["Google", "Messenger", "Instagram", "Voice"];

    public LineLayout(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        // The widest time the culture writes, of every hour at :58, or the widest date: a line from an earlier day shows its date above its time.
        TimeWidth = Math.Max(
            Enumerable.Range(0, 24).Max(hour => ConsoleCells.Width(new DateTime(2000, 1, 1, hour, 58, 0).ToString("t", culture))),
            Enumerable.Range(1, 12).Max(month => ConsoleCells.Width(new DateTime(2000, month, 28).ToString("d MMM", culture))));
        TagWidth = Tags.Max(ConsoleCells.Width);
    }

    public int TimeWidth { get; }
    public int TagWidth { get; }
    public int BodyColumn => TimeWidth + Gap + TagWidth + Gap;

    /// <summary>Whether a send in this state is still waiting for its answer: its line is drawn dim.</summary>
    public static bool Pending(ConsoleSendState state) => state is not (ConsoleSendState.None or ConsoleSendState.Sent);

    /// <summary>The line's rows, each written as VT (or plain text when the palette has styles off).</summary>
    public IReadOnlyList<string> Render(ConsoleLine line, ConsoleSendState state, int width, VtPalette ink) =>
        [.. Rows(line, state, width, ink).Select(VtText.Draw)];

    public List<List<VtCell>> Rows(ConsoleLine line, ConsoleSendState state, int width, VtPalette ink)
    {
        width = Math.Max(1, width);
        if (line.Wide) return WideRows(line, width, ink);
        return width - BodyColumn < MinBody ? NarrowRows(line, state, width, ink) : ColumnRows(line, state, width, ink);
    }

    // Narrower than the columns and a body of ten cells: the time and the tag run in front of the body, and the rows wrap at the edge.
    private List<List<VtCell>> NarrowRows(ConsoleLine line, ConsoleSendState state, int width, VtPalette ink)
    {
        var cells = new List<VtCell>();
        if (line.Time.Length > 0)
        {
            VtText.Add(cells, line.Time.Replace('\n', ' '), ink.Style(ConsoleTone.Dim));
            cells.AddRange(Spaces(Gap));
        }
        if (line.ServiceTag.Length > 0)
        {
            VtText.Add(cells, line.ServiceTag.Replace('\n', ' '), TagStyle(line, ink));
            cells.AddRange(Spaces(Gap));
        }
        cells.AddRange(Body(line, state, ink));
        return [.. VtText.Wrap(cells, width).Select(Trimmed)];
    }

    private static string TagStyle(ConsoleLine line, VtPalette ink) =>
        line.Family is { } family ? ink.Style(ConsoleTone.Service, family, bold: false) : ink.Style(ConsoleTone.Default);

    private List<List<VtCell>> ColumnRows(ConsoleLine line, ConsoleSendState state, int width, VtPalette ink)
    {
        var dim = ink.Style(ConsoleTone.Dim);
        var (date, time) = SplitTime(line.Time);
        var body = VtText.Wrap(Body(line, state, ink), width - BodyColumn);
        if (date is not null && body.Count < 2) body.Add([]);
        var tagStyle = TagStyle(line, ink);
        var rows = new List<List<VtCell>>(body.Count);
        for (var index = 0; index < body.Count; index++)
        {
            var row = new List<VtCell>();
            if (index == 0)
            {
                row.AddRange(Column(date ?? time, TimeWidth, dim));
                row.AddRange(Spaces(Gap));
                row.AddRange(Column(line.ServiceTag, TagWidth, tagStyle));
                row.AddRange(Spaces(Gap));
            }
            else if (index == 1 && date is not null)
            {
                row.AddRange(Column(time, TimeWidth, dim));
                row.AddRange(Spaces(BodyColumn - TimeWidth));
            }
            else row.AddRange(Spaces(BodyColumn));
            row.AddRange(body[index]);
            rows.Add(Trimmed(row));
        }
        return rows;
    }

    private static List<List<VtCell>> WideRows(ConsoleLine line, int width, VtPalette ink)
    {
        // Leading spaces become a margin, so a long line wraps under its own indent (a manual page's description, a hop of /route).
        var indent = line.Body.Length - line.Body.TrimStart(' ').Length;
        if (indent == line.Body.Length) indent = 0;
        indent = Math.Min(indent, width / 2);
        var cells = new List<VtCell>();
        if (line.Spans is { } spans)
        {
            var skip = indent;
            foreach (var whole in spans)
            {
                var text = whole.Text;
                if (skip > 0)
                {
                    var take = Math.Min(skip, text.Length - text.TrimStart(' ').Length);
                    skip = take == text.Length ? skip - take : 0;
                    text = text[take..];
                    if (text.Length == 0) continue;
                }
                VtText.Add(cells, text, ink.Style(whole.Tone, whole.Family), newlines: true);
            }
        }
        else VtText.Add(cells, line.Body[Math.Min(indent, line.Body.Length)..], BodyStyle(line, ConsoleSendState.None, ink), newlines: true);
        var rows = VtText.Wrap(cells, Math.Max(1, width - indent));
        return [.. rows.Select(body => Trimmed([.. Spaces(indent), .. body]))];
    }

    private static List<VtCell> Body(ConsoleLine line, ConsoleSendState state, VtPalette ink)
    {
        var cells = new List<VtCell>();
        if (line.Spans is { } spans)
        {
            foreach (var span in spans) VtText.Add(cells, span.Text, ink.Style(span.Tone, span.Family), newlines: true);
            return cells;
        }
        if (line.Kind == ConsoleLineKind.Outgoing && !Pending(state) && line.Body.StartsWith(You, StringComparison.Ordinal))
        {
            VtText.Add(cells, You, ink.Style(ConsoleTone.Dim));
            VtText.Add(cells, line.Body[You.Length..], ink.Style(ConsoleTone.Default), newlines: true);
            return cells;
        }
        VtText.Add(cells, line.Body, BodyStyle(line, state, ink), newlines: true);
        return cells;
    }

    private static string BodyStyle(ConsoleLine line, ConsoleSendState state, VtPalette ink) => ink.Style(line.Kind switch
    {
        ConsoleLineKind.Error => ConsoleTone.Error,
        ConsoleLineKind.Event or ConsoleLineKind.Connection or ConsoleLineKind.Day => ConsoleTone.Dim,
        _ when Pending(state) => ConsoleTone.Dim,
        _ => ConsoleTone.Default,
    });

    /// <summary>"sent · You → Sam: on my way…": what a send's line, scrolled out of reach, would now say.</summary>
    public List<VtCell> FollowUp(ConsoleLine line, string word, int width, VtPalette ink)
    {
        var indent = line.Wide ? 0 : Math.Min(BodyColumn, width / 2);
        var room = Math.Max(1, width - indent);
        var text = new List<VtCell>();
        VtText.Add(text, word + " · " + line.Body, ink.Style(ConsoleTone.Dim));
        return [.. Spaces(indent), .. VtText.Fit(text, Math.Min(room, word.Length + 3 + 40))];
    }

    // "23 Sep 11:58 PM" in a column the width of a time: the date on the first row, the time under it. Split at the first space after which the rest
    // fits, which works for any culture's order of date and time.
    private (string? Date, string Time) SplitTime(string time)
    {
        if (ConsoleCells.Width(time) <= TimeWidth) return (null, time);
        for (var at = time.IndexOf(' '); at > 0; at = time.IndexOf(' ', at + 1))
            if (ConsoleCells.Width(time[(at + 1)..]) <= TimeWidth) return (time[..at], time[(at + 1)..]);
        return (null, time);
    }

    private static List<VtCell> Column(string text, int width, string style)
    {
        var cells = new List<VtCell>();
        VtText.Add(cells, text.Replace('\n', ' '), style);
        return VtText.Pad(cells, width, "");
    }

    private static IEnumerable<VtCell> Spaces(int count) => Enumerable.Repeat(new VtCell(" ", 1, ""), Math.Max(0, count));

    private static List<VtCell> Trimmed(List<VtCell> row)
    {
        while (row.Count > 0 && row[^1].Text == " " && row[^1].Style.Length == 0) row.RemoveAt(row.Count - 1);
        return row;
    }
}
