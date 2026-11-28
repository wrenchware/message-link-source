using System.Globalization;
using System.Text;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Vt;

/// <summary>One text element as the terminal draws it: its cells and the SGR sequence it is drawn in ("" for none).</summary>
public readonly record struct VtCell(string Text, int Width, string Style);

/// <summary>
/// The console's inks as VT sequences: 24-bit SGR from one <see cref="ConsoleTheme"/>, bold for Strong and Match, Match underlined. Service ink is each
/// service's tag colour moved only as far as it must be to read on the theme's surface (<see cref="BubbleColours.TagInk"/>), as the window drew it.
/// </summary>
public sealed class VtPalette
{
    public const string Reset = "\u001b[0m";
    private readonly Dictionary<BubbleFamily, uint> services = [];
    private readonly Dictionary<(ConsoleTone, BubbleFamily?, uint?, bool?, bool), string> styles = [];

    /// <param name="styles">Whether any SGR is written at all: false when the output is not a terminal.</param>
    /// <param name="colors">Whether colour is written; false keeps only bold, underline and reverse (NO_COLOR).</param>
    public VtPalette(string? theme, bool styles = true, bool colors = true)
    {
        Theme = ConsoleThemes.Find(theme);
        Styles = styles;
        Colors = styles && colors;
    }

    public ConsoleTheme Theme { get; }
    public bool Styles { get; }
    public bool Colors { get; }

    /// <summary>The ink of a tone as 0xAARRGGBB.</summary>
    public uint Ink(ConsoleTone tone, BubbleFamily? family = null) => tone switch
    {
        ConsoleTone.Dim => Theme.Dim,
        ConsoleTone.Accent => Theme.Accent,
        ConsoleTone.Ok => Theme.Ok,
        ConsoleTone.Warn => Theme.Warn,
        ConsoleTone.Error => Theme.Error,
        ConsoleTone.Strong => Theme.Strong,
        ConsoleTone.Match => Theme.Match,
        ConsoleTone.Service => family is { } f ? Service(f) : Theme.Strong,
        _ => Theme.Text,
    };

    private uint Service(BubbleFamily family)
    {
        if (services.TryGetValue(family, out var known)) return known;
        var surface = new Rgba((byte)(Theme.Surface >> 16), (byte)(Theme.Surface >> 8), (byte)Theme.Surface);
        var ink = BubbleColours.TagInk(family, null, true, surface);
        return services[family] = 0xFF000000u | (uint)ink.R << 16 | (uint)ink.G << 8 | ink.B;
    }

    /// <summary>The whole SGR for a run: reset, then ink, weight, underline and background. "" when styles are off.</summary>
    /// <param name="bold">Null for the tone's own weight (Strong and Match bold).</param>
    public string Style(ConsoleTone tone, BubbleFamily? family = null, uint? background = null, bool? bold = null, bool underline = false)
    {
        if (!Styles) return "";
        var key = (tone, family, background, bold, underline);
        if (styles.TryGetValue(key, out var known)) return known;
        var text = new StringBuilder("\u001b[0");
        if (Colors) Rgb(text, 38, Ink(tone, family));
        if (bold ?? tone is ConsoleTone.Strong or ConsoleTone.Match) text.Append(";1");
        if (underline || tone == ConsoleTone.Match) text.Append(";4");
        if (background is { } back)
        {
            if (Colors) Rgb(text, 48, back);
            else text.Append(";7");
        }
        return styles[key] = text.Append('m').ToString();
    }

    private static void Rgb(StringBuilder text, int which, uint argb) =>
        text.Append(CultureInfo.InvariantCulture, $";{which};2;{(argb >> 16) & 0xFF};{(argb >> 8) & 0xFF};{argb & 0xFF}");
}

/// <summary>Cells: text elements measured as a terminal draws them, rows wrapped and fitted to a width, and rows written as VT.</summary>
public static class VtText
{
    /// <summary>
    /// The text's elements with their cell widths. Anything a terminal would act on rather than draw (escape, the other C0 and C1 controls) becomes
    /// U+FFFD, so a message can never move the cursor or change the terminal; a tab is a space and a carriage return is dropped. A newline is kept
    /// as its own element when <paramref name="newlines"/>, else drawn as ↵.
    /// </summary>
    public static IEnumerable<(string Text, int Width)> Elements(string text, bool newlines = true)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (element == "\n" || element == "\r\n") { yield return newlines ? ("\n", 0) : ("↵", 1); continue; }
            if (element == "\r") continue;
            if (element == "\t") { yield return (" ", 1); continue; }
            var safe = Safe(element);
            yield return (safe, ConsoleCells.Width(safe));
        }
    }

    private static string Safe(string element)
    {
        foreach (var c in element)
            if (c < ' ' || c is >= '\u007f' and <= '\u009f')
            {
                var clean = new StringBuilder(element.Length);
                foreach (var d in element) clean.Append(d < ' ' || d is >= '\u007f' and <= '\u009f' ? '�' : d);
                return clean.ToString();
            }
        return element;
    }

    public static void Add(List<VtCell> row, string text, string style, bool newlines = false)
    {
        foreach (var (element, width) in Elements(text, newlines)) row.Add(new VtCell(element, width, style));
    }

    public static int Width(IReadOnlyList<VtCell> row)
    {
        var width = 0;
        foreach (var cell in row) width += cell.Width;
        return width;
    }

    public static string Plain(IReadOnlyList<VtCell> row) => string.Concat(row.Select(cell => cell.Text));

    /// <summary>The row cut to <paramref name="width"/> cells, with … in the last cell's style when it did not fit.</summary>
    public static List<VtCell> Fit(IReadOnlyList<VtCell> row, int width)
    {
        if (Width(row) <= width) return [.. row];
        var fitted = new List<VtCell>();
        var used = 0;
        foreach (var cell in row)
        {
            if (used + cell.Width > width - 1) break;
            fitted.Add(cell);
            used += cell.Width;
        }
        if (width > 0) fitted.Add(new VtCell("…", 1, fitted.Count > 0 ? fitted[^1].Style : row[0].Style));
        return fitted;
    }

    /// <summary>Pads the row with spaces in <paramref name="style"/> to exactly <paramref name="width"/> cells (cut first when wider).</summary>
    public static List<VtCell> Pad(IReadOnlyList<VtCell> row, int width, string style)
    {
        var padded = Fit(row, width);
        for (var used = Width(padded); used < width; used++) padded.Add(new VtCell(" ", 1, style));
        return padded;
    }

    /// <summary>
    /// Wraps cells to rows of at most <paramref name="width"/> cells: at a space where there is one (the space at the break is dropped), else
    /// mid-word; a newline element always breaks. A wide element is never split across rows.
    /// </summary>
    public static List<List<VtCell>> Wrap(IEnumerable<VtCell> cells, int width)
    {
        width = Math.Max(1, width);
        var rows = new List<List<VtCell>> { new() };
        var used = 0;
        var breakAt = -1;
        foreach (var cell in cells)
        {
            if (cell.Text == "\n") { rows.Add([]); used = 0; breakAt = -1; continue; }
            var row = rows[^1];
            if (used + cell.Width > width && row.Count > 0)
            {
                if (cell.Text == " ") { TrimEnd(row); rows.Add([]); used = 0; breakAt = -1; continue; }
                if (breakAt > 0 && breakAt < row.Count)
                {
                    var tail = row.GetRange(breakAt, row.Count - breakAt);
                    row.RemoveRange(breakAt, row.Count - breakAt);
                    TrimEnd(row);
                    rows.Add(tail);
                    used = Width(tail);
                    row = tail;
                }
                breakAt = -1;
                if (used + cell.Width > width && row.Count > 0)
                {
                    TrimEnd(row);
                    row = [];
                    rows.Add(row);
                    used = 0;
                }
            }
            row.Add(cell);
            used += cell.Width;
            if (cell.Text == " ") breakAt = row.Count;
        }
        return rows;
    }

    private static void TrimEnd(List<VtCell> row)
    {
        while (row.Count > 0 && row[^1].Text == " ") row.RemoveAt(row.Count - 1);
    }

    /// <summary>Writes the row: each change of style once, then a reset when any style was written.</summary>
    public static void Draw(StringBuilder output, IReadOnlyList<VtCell> row)
    {
        var current = "";
        foreach (var cell in row)
        {
            if (cell.Style != current)
            {
                output.Append(cell.Style.Length == 0 ? VtPalette.Reset : cell.Style);
                current = cell.Style;
            }
            output.Append(cell.Text);
        }
        if (current.Length > 0) output.Append(VtPalette.Reset);
    }

    public static string Draw(IReadOnlyList<VtCell> row)
    {
        var output = new StringBuilder();
        Draw(output, row);
        return output.ToString();
    }
}
