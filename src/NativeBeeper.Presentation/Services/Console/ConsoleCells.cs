using System.Globalization;
using System.Text;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Monospace layout: how many cells a string takes, and the small formats a terminal lives on (sizes, ages, bars,
/// sparklines). An emoji or a CJK character takes two cells, a combining mark none, as a terminal draws them.
/// </summary>
public static class ConsoleCells
{
    private const string Blocks = "▁▂▃▄▅▆▇█";

    public static int Width(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += ElementWidth((string)elements.Current);
        return width;
    }

    private static int ElementWidth(string element)
    {
        if (element.Length == 0) return 0;
        // VS16 asks for the emoji picture, which a terminal that draws one draws two cells wide (❤️, ☀️, 1️⃣).
        if (element.Length > 1 && element.Contains('️')) return 2;
        var rune = Rune.GetRuneAt(element, 0).Value;
        if (rune < 0x1100) return 1;
        // A flag is two regional indicators in one element; one alone is a letter in a box, one cell.
        if (rune is >= 0x1F1E6 and <= 0x1F1FF)
            return element.Length >= 4 && Rune.GetRuneAt(element, 2).Value is >= 0x1F1E6 and <= 0x1F1FF ? 2 : 1;
        return rune is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD || WideSymbol(rune) ? 2 : 1;
    }

    // East Asian Width W outside the CJK blocks: the symbols that are emoji by default (⌛ ✅ ❌ ⭐ ⚡ ☕ 🀄 🆎), which terminals draw two cells wide.
    private static bool WideSymbol(int rune) => rune is
        0x231A or 0x231B or 0x2329 or 0x232A or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE or 0x2614 or 0x2615
        or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 or 0x26CE
        or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E
        or >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55
        or 0x1F004 or 0x1F0CF or 0x1F18E or >= 0x1F191 and <= 0x1F19A or >= 0x1F200 and <= 0x1F202 or >= 0x1F210 and <= 0x1F23B
        or >= 0x1F240 and <= 0x1F248 or 0x1F250 or 0x1F251 or >= 0x1F260 and <= 0x1F265;

    /// <summary>The text cut to <paramref name="width"/> cells with … when it did not fit.</summary>
    public static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        if (Width(flat) <= width) return flat;
        var result = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(flat);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var w = ElementWidth(element);
            if (used + w > width - 1) break;
            result.Append(element);
            used += w;
        }
        return result.Append('…').ToString();
    }

    /// <summary>Fitted, then padded with spaces to exactly <paramref name="width"/> cells (right-aligned when asked).</summary>
    public static string Pad(string text, int width, bool right = false)
    {
        var fitted = Fit(text, width);
        var gap = new string(' ', Math.Max(0, width - Width(fitted)));
        return right ? gap + fitted : fitted + gap;
    }

    /// <summary>"512B", "48.2K", "1.4M", "2.1G".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + "B";
        double value = bytes;
        foreach (var unit in new[] { "K", "M", "G", "T" })
        {
            value /= 1024;
            if (value < 1024 || unit == "T") return (value < 100 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture)) + unit;
        }
        return bytes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"now", "12s", "4m", "3h", "2d": how long ago, as a status column says it.</summary>
    public static string Age(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1)) return "now";
        if (span < TimeSpan.FromMinutes(1)) return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        if (span < TimeSpan.FromHours(1)) return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        if (span < TimeSpan.FromDays(2)) return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        return ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
    }

    public static string Age(DateTimeOffset? at, DateTimeOffset now) => at is { } when ? Age(now - when) : "—";

    /// <summary>"3:12:44", or "2d 03:12" past a day: how long something has run.</summary>
    public static string Uptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}"
            : $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>"3 hours, 12 minutes", as uptime(1) words it.</summary>
    public static string Duration(TimeSpan span)
    {
        string Part(int n, string unit) => n.ToString(CultureInfo.CurrentCulture) + " " + unit + (n == 1 ? "" : "s");
        if (span.TotalMinutes < 1) return Part(Math.Max(0, (int)span.TotalSeconds), "second");
        var parts = new List<string>();
        if (span.Days > 0) parts.Add(Part(span.Days, "day"));
        if (span.Hours > 0) parts.Add(Part(span.Hours, "hour"));
        if (span.Days == 0 && span.Minutes > 0) parts.Add(Part(span.Minutes, "minute"));
        return string.Join(", ", parts);
    }

    /// <summary>A sparkline: one block per value, scaled to the largest. Zeros draw as the lowest block.</summary>
    public static string Spark(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return "";
        var top = values.Max();
        var text = new StringBuilder(values.Count);
        foreach (var value in values)
            text.Append(top <= 0 || value <= 0 ? Blocks[0] : Blocks[Math.Clamp((int)Math.Ceiling(value / top * (Blocks.Length - 1)), 1, Blocks.Length - 1)]);
        return text.ToString();
    }

    /// <summary>"████████░░░░" for a fraction, <paramref name="width"/> cells long.</summary>
    public static (string Filled, string Empty) Bar(double fraction, int width)
    {
        var filled = (int)Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * width);
        return (new string('█', filled), new string('░', width - filled));
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// A table of fixed and flexible columns laid out to a width, one ink per cell, two spaces between columns. Auto columns
/// take their widest cell; the flexible column gives way first when the width runs short.
/// </summary>
public sealed class ConsoleTable(params ConsoleTable.Column[] columns)
{
    /// <param name="Width">Cells; 0 for as wide as its widest cell.</param>
    /// <param name="Flex">Takes what is left of the width, and gives way first when there is not enough.</param>
    public sealed record Column(string Header, int Width = 0, bool Right = false, bool Flex = false, int Min = 3);

    private readonly List<ConsoleSpan[]> rows = [];
    private readonly List<bool> selected = [];

    public int Count => rows.Count;

    public void Add(params ConsoleSpan[] cells) => AddRow(false, cells);

    public void AddRow(bool isSelected, params ConsoleSpan[] cells)
    {
        rows.Add(cells);
        selected.Add(isSelected);
    }

    public int[] Widths(int width)
    {
        var widths = new int[columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            var column = columns[c];
            widths[c] = column.Width > 0 ? column.Width
                : Math.Max(ConsoleCells.Width(column.Header), rows.Count == 0 ? 0 : rows.Max(row => c < row.Length ? ConsoleCells.Width(row[c].Text) : 0));
        }
        var total = widths.Sum() + 2 * (columns.Length - 1);
        var flex = Array.FindIndex(columns, column => column.Flex);
        if (flex >= 0 && total < width) widths[flex] += width - total;
        // Too wide: the flexible column first, then the widest others, down to their minimums.
        while (total > width)
        {
            total = widths.Sum() + 2 * (columns.Length - 1);
            if (total <= width) break;
            var shrink = flex >= 0 && widths[flex] > columns[flex].Min ? flex
                : Enumerable.Range(0, columns.Length).Where(c => widths[c] > columns[c].Min).OrderByDescending(c => widths[c]).DefaultIfEmpty(-1).First();
            if (shrink < 0) break;
            widths[shrink] -= Math.Min(widths[shrink] - columns[shrink].Min, total - width);
        }
        return widths;
    }

    /// <summary>The header (dim) and every row, each a list of spans. Selected rows carry the Selected ink throughout.</summary>
    public IReadOnlyList<(IReadOnlyList<ConsoleSpan> Spans, bool Selected)> Render(int width, bool header = true)
    {
        var widths = Widths(width);
        var output = new List<(IReadOnlyList<ConsoleSpan>, bool)>();
        if (header)
            output.Add(([new ConsoleSpan(string.Join("  ", columns.Select((column, c) => ConsoleCells.Pad(column.Header, widths[c], column.Right))).TrimEnd(), ConsoleTone.Dim)], false));
        for (var r = 0; r < rows.Count; r++)
        {
            var spans = new List<ConsoleSpan>();
            for (var c = 0; c < columns.Length; c++)
            {
                var cell = c < rows[r].Length ? rows[r][c] : new ConsoleSpan("");
                if (c > 0) spans.Add(new ConsoleSpan("  "));
                var text = ConsoleCells.Pad(cell.Text, widths[c], columns[c].Right);
                if (c == columns.Length - 1) text = text.TrimEnd();
                spans.Add(cell with { Text = text });
            }
            output.Add((spans, selected[r]));
        }
        return output;
    }
}
