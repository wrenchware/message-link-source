using System.Globalization;
using System.Text.RegularExpressions;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// A pager. As /logs it tails the sanitized diagnostic logs (the Settings viewer's fixed list, nothing else) and follows
/// them live; as "| less" it pages a command's output. / greps as you type, f follows, g and G jump, c copies what is
/// shown, Tab moves to the next log.
/// </summary>
public sealed partial class PagerApp : ConsoleApp
{
    public const int Keep = 5000;

    private readonly IReadOnlyList<string> sources;
    private readonly List<ConsoleOutLine> lines = [];
    private readonly string name;
    private int source;
    private long position;
    private bool follow, reading;
    private int top, page = 10;
    private int generation;

    /// <summary>A pager over a command's output.</summary>
    public PagerApp(IConsoleAppHost shell, IReadOnlyList<ConsoleOutLine> output) : base(shell)
    {
        name = "less";
        sources = [];
        lines.AddRange(output);
    }

    /// <summary>The live log viewer, on the log whose name best matches <paramref name="wanted"/>.</summary>
    public PagerApp(IConsoleAppHost shell, string wanted) : base(shell)
    {
        name = "logs";
        sources = shell.Host.LogSources;
        source = Pick(sources, wanted);
        follow = true;
    }

    public override string Name => name;
    public override bool CanFilter => true;
    public override TimeSpan Refresh => IsLog ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
    public bool IsLog => sources.Count > 0;
    public string? Source => IsLog ? sources[source] : null;
    public int LineCount => lines.Count;

    /// <summary>The source a word means: "messenger" the app's Messenger log, "helper" Messenger's helper, "voice" Voice.</summary>
    public static int Pick(IReadOnlyList<string> sources, string wanted)
    {
        var word = wanted.Trim().ToLowerInvariant();
        if (word.Length == 0 || sources.Count == 0) return 0;
        string[] aliases = word switch
        {
            "app" or "messages" => ["Messages"],
            "google" or "gmessages" or "sms" => ["Google Messages"],
            "voice" or "gvoice" => ["Google Voice"],
            "helper" or "messenger-helper" => ["Messenger helper"],
            "ig" or "insta" => ["Instagram"],
            "signin" or "sign-in" => ["Google Messages · sign-in history"],
            _ => [],
        };
        foreach (var exact in aliases.Concat([wanted.Trim()]))
            if (sources.ToList().FindIndex(s => s.Equals(exact, StringComparison.OrdinalIgnoreCase)) is var at and >= 0) return at;
        var starts = sources.ToList().FindIndex(s => s.StartsWith(word, StringComparison.OrdinalIgnoreCase));
        if (starts >= 0) return starts;
        var contains = sources.ToList().FindIndex(s => s.Contains(word, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, contains);
    }

    public override void Start() { if (IsLog) _ = ReadAsync(); }
    public override void Tick() { if (IsLog) _ = ReadAsync(); }

    private async Task ReadAsync()
    {
        if (reading) return;
        reading = true;
        var version = generation;
        try
        {
            var read = await Shell.Host.ReadLogAsync(sources[source], position);
            if (version != generation) return;
            if (read.Reset) { lines.Clear(); top = 0; }
            position = read.Position;
            if (read.Lines.Count == 0 && !read.Reset) return;
            lines.AddRange(read.Lines.Select(Ink));
            if (lines.Count > Keep) { var cut = lines.Count - Keep; lines.RemoveRange(0, cut); top = Math.Max(0, top - cut); }
            Invalidate();
        }
        catch (Exception) { Message = "This log could not be read."; Invalidate(); }
        finally { reading = false; }
    }

    private void Switch(int to)
    {
        source = (to + sources.Count) % sources.Count;
        generation++;
        position = 0;
        lines.Clear();
        top = 0;
        follow = true;
        reading = false;
        _ = ReadAsync();
    }

    [GeneratedRegex(@"^(\d{1,2}:\d{2}:\d{2}(?:\.\d+)?|\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?\S*)\s")]
    private static partial Regex Stamp();
    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_./-]*)=(\S*)")]
    private static partial Regex Pair();
    [GeneratedRegex(@"\b(error|errors|failed|failure|fail|exception|crash|crashed|fatal|unclean|killed|refused|denied)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Bad();
    [GeneratedRegex(@"\b(warn|warning|timeout|timed out|retry|retrying|stall|stalled|slow|offline|disconnected|gone|late|unknown)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Doubtful();

    /// <summary>A log line in inks: the time dim, key= dim, the line red where it reports a failure and yellow where it doubts.</summary>
    public static ConsoleOutLine Ink(string line)
    {
        var spans = new List<ConsoleSpan>();
        var rest = line;
        if (Stamp().Match(line) is { Success: true } stamp)
        {
            spans.Add(new ConsoleSpan(stamp.Value, ConsoleTone.Dim));
            rest = line[stamp.Length..];
        }
        var tone = Bad().IsMatch(rest) ? ConsoleTone.Error : Doubtful().IsMatch(rest) ? ConsoleTone.Warn : ConsoleTone.Default;
        var at = 0;
        foreach (Match pair in Pair().Matches(rest))
        {
            if (pair.Index > at) spans.Add(new ConsoleSpan(rest[at..pair.Index], tone));
            spans.Add(new ConsoleSpan(pair.Groups[1].Value + "=", ConsoleTone.Dim));
            spans.Add(new ConsoleSpan(pair.Groups[2].Value, tone == ConsoleTone.Default ? ConsoleTone.Strong : tone));
            at = pair.Index + pair.Length;
        }
        if (at < rest.Length) spans.Add(new ConsoleSpan(rest[at..], tone));
        return new ConsoleOutLine(spans, tone == ConsoleTone.Error);
    }

    private IReadOnlyList<ConsoleOutLine> Shown() => Filter.Length == 0 ? lines
        : [.. lines.Where(line => line.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase)).Select(line => ConsoleShell.Highlight(line, Filter))];

    protected override void FilterChanged() { top = 0; follow = IsLog; }

    public override bool Key(ConsoleKeyPress key)
    {
        Message = null;
        var shown = Shown();
        var last = Math.Max(0, shown.Count - page);
        int? next = key.Kind switch
        {
            ConsoleKeyKind.Up => top - 1,
            ConsoleKeyKind.Down => top + 1,
            ConsoleKeyKind.PageUp => top - page,
            ConsoleKeyKind.PageDown => top + page,
            ConsoleKeyKind.Char when key.Char == ' ' => top + page,
            ConsoleKeyKind.Home => 0,
            ConsoleKeyKind.End => last,
            ConsoleKeyKind.Wheel => top + key.Delta,
            ConsoleKeyKind.Char when key.Char == 'k' => top - 1,
            ConsoleKeyKind.Char when key.Char == 'j' => top + 1,
            ConsoleKeyKind.Char when key.Char == 'g' => 0,
            ConsoleKeyKind.Char when key.Char == 'G' => last,
            _ => null,
        };
        if (next is { } n)
        {
            top = Math.Clamp(n, 0, last);
            // Following stops the moment you scroll up, and starts again at the bottom, as a terminal's scrollback does.
            follow = IsLog && top >= last;
            return true;
        }
        if (IsLog && key.Kind is ConsoleKeyKind.Tab or ConsoleKeyKind.Right) { Switch(source + 1); return true; }
        if (IsLog && key.Kind is ConsoleKeyKind.BackTab or ConsoleKeyKind.Left) { Switch(source - 1); return true; }
        if (key.Kind != ConsoleKeyKind.Char) return false;
        switch (key.Char)
        {
            case '/': Shell.BeginFilter(); return true;
            case 'f' or 'F' when IsLog: follow = !follow; if (follow) top = last; return true;
            case 'c':
                var visible = shown.Skip(top).Take(page).Select(line => line.Text).ToArray();
                Shell.Host.Copy(string.Join(Environment.NewLine, visible));
                Message = $"Copied {visible.Length.ToString(CultureInfo.CurrentCulture)} lines.";
                return true;
            case 'C':
                Shell.Host.Copy(string.Join(Environment.NewLine, shown.Select(line => line.Text)));
                Message = $"Copied all {shown.Count.ToString(CultureInfo.CurrentCulture)} lines.";
                return true;
        }
        return false;
    }

    public override ConsoleScreen Render(int width, int height)
    {
        page = Math.Max(1, height);
        var shown = Shown();
        var last = Math.Max(0, shown.Count - page);
        if (follow) top = last;
        top = Math.Clamp(top, 0, last);
        var title = new List<ConsoleSpan> { new(name, ConsoleTone.Accent), new("  ") };
        if (Source is { } current) title.Add(new ConsoleSpan(current, ConsoleTone.Strong));
        title.Add(new ConsoleSpan($"  {ConsoleCells.Count(shown.Count)} lines", ConsoleTone.Dim));
        if (Filter.Length > 0) title.Add(new ConsoleSpan($"  /{Filter}", ConsoleTone.Match));
        title.Add(new ConsoleSpan(IsLog ? (follow ? "  following" : $"  {(shown.Count == 0 ? 100 : (top + page) * 100 / shown.Count).ToString(CultureInfo.InvariantCulture)}%") : "",
            follow ? ConsoleTone.Ok : ConsoleTone.Dim));
        var rows = shown.Skip(top).Take(page).Select(line => new ConsoleRow(Clip(line.Spans, width))).ToList();
        if (shown.Count == 0) rows.Add(ConsoleRow.Of(IsLog ? (lines.Count == 0 ? "Reading…" : "No line matches.") : "Nothing to show.", ConsoleTone.Dim));
        return IsLog
            ? Screen(title, Fill(rows, height), ("Tab", "next log"), ("f", follow ? "stop following" : "follow"), ("/", "grep"), ("g/G", "top/end"),
                ("c", "copy"), ("q", "quit"))
            : Screen(title, Fill(rows, height), ("↑↓", "scroll"), ("Space", "page"), ("/", "grep"), ("g/G", "top/end"), ("c", "copy"), ("q", "quit"));
    }

    // One screen row per line: a long line is cut at the edge with an ellipsis rather than wrapped, as less -S does.
    private static IReadOnlyList<ConsoleSpan> Clip(IReadOnlyList<ConsoleSpan> spans, int width)
    {
        var result = new List<ConsoleSpan>();
        var used = 0;
        foreach (var span in spans)
        {
            var w = ConsoleCells.Width(span.Text);
            if (used + w <= width) { result.Add(span); used += w; continue; }
            result.Add(span with { Text = ConsoleCells.Fit(span.Text, width - used) });
            break;
        }
        return result;
    }
}
