using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>One row of a console app's screen. Selected rows are drawn on the selection colour across the whole width.</summary>
public sealed record ConsoleRow(IReadOnlyList<ConsoleSpan> Spans, bool Selected = false)
{
    public string Text => string.Concat(Spans.Select(span => span.Text));
    public static ConsoleRow Blank { get; } = new([]);
    public static ConsoleRow Of(string text, ConsoleTone tone = ConsoleTone.Default) => new([new ConsoleSpan(text, tone)]);
}

/// <summary>A key a console app is handed. Char carries the character for Char; Wheel carries lines in Delta.</summary>
public enum ConsoleKeyKind { Up, Down, PageUp, PageDown, Home, End, Left, Right, Enter, Tab, BackTab, Escape, Char, Wheel }
public readonly record struct ConsoleKeyPress(ConsoleKeyKind Kind, char Char = '\0', int Delta = 0)
{
    public static ConsoleKeyPress Of(char c) => new(ConsoleKeyKind.Char, c);
    public bool Is(char c) => Kind == ConsoleKeyKind.Char && char.ToLowerInvariant(Char) == c;
}

/// <summary>
/// What a full-screen console app draws: a title line, exactly the body rows it was given room for, and the keys it
/// answers to (the htop-style bar along the bottom). A transient message, when set, replaces the keys for a moment.
/// </summary>
public sealed record ConsoleScreen(IReadOnlyList<ConsoleSpan> Title, IReadOnlyList<ConsoleRow> Rows,
    IReadOnlyList<(string Key, string Label)> Keys, string? Message = null);

/// <summary>
/// What a console app may ask of the console session it runs in. Every action is one the user asked for with a key; the
/// session echoes it into the scroll as the command it stands for, so the scroll still says what happened.
/// </summary>
public interface IConsoleAppHost
{
    IConsoleHost Host { get; }
    ConversationTargets Targets { get; }
    ActivityStream Stream { get; }
    TimeProvider Clock { get; }
    ConsoleLineFormatter Formatter { get; }
    /// <summary>Whether moving pictures are welcome (Windows' animation effects).</summary>
    bool Motion { get; }
    void Close();
    /// <summary>Closes the app, then runs a console command line as if it had been typed.</summary>
    Task RunAsync(string line);
    void SetTarget(ChatTarget target);
    Task<string> ReadAsync(ChatTarget target);
    Task<string> MuteAsync(ChatTarget target, bool muted);
    bool IsMuted(ChatTarget target);
    /// <summary>Hands the prompt to the app as its filter line (/ in less).</summary>
    void BeginFilter();
}

/// <summary>
/// A full-screen console app: the terminal's alternate screen. While it runs the scroll waits underneath, unchanged, and
/// keys go to the app; q or Esc returns. It renders a whole screen at a time, to the cells it is given, so the window
/// only draws rows and never scrolls anything itself.
/// </summary>
public abstract class ConsoleApp(IConsoleAppHost shell)
{
    protected IConsoleAppHost Shell { get; } = shell;
    protected DateTimeOffset Now => Shell.Clock.GetLocalNow();

    /// <summary>The command word that opened it, which the prompt shows: "top" for top&gt;.</summary>
    public abstract string Name { get; }
    /// <summary>How often <see cref="Tick"/> runs and the screen is drawn again; zero for only when something changes.</summary>
    public virtual TimeSpan Refresh => TimeSpan.FromSeconds(1);
    public virtual bool CanFilter => false;
    public string Filter { get; private set; } = "";
    /// <summary>A short note in place of the key bar, until the next key.</summary>
    public string? Message { get; protected set; }

    /// <summary>Raised when the app's screen changed outside a key or a tick (an answer arrived).</summary>
    public event Action? Changed;
    protected void Invalidate() => Changed?.Invoke();
    /// <summary>Asks the window to draw the screen again.</summary>
    public void Redraw() => Invalidate();

    public virtual void SetFilter(string text) { Filter = text; FilterChanged(); }
    protected virtual void FilterChanged() { }

    public abstract ConsoleScreen Render(int width, int height);
    /// <summary>A key; true when the app used it. q and Esc that the app does not use close it.</summary>
    public virtual bool Key(ConsoleKeyPress key) => false;
    /// <summary>A click on body row <paramref name="row"/>; double for a double click.</summary>
    public virtual void Click(int row, bool twice) { }
    /// <summary>Every <see cref="Refresh"/> while the app is shown.</summary>
    public virtual void Tick() { }
    public virtual void Start() { }
    public virtual void Stop() { }

    protected ConsoleScreen Screen(IReadOnlyList<ConsoleSpan> title, IReadOnlyList<ConsoleRow> rows, params (string, string)[] keys)
    {
        var message = Message;
        return new ConsoleScreen(title, rows, keys, message);
    }

    /// <summary>Body rows padded with blanks (or cut) to exactly <paramref name="height"/>.</summary>
    protected static IReadOnlyList<ConsoleRow> Fill(IEnumerable<ConsoleRow> rows, int height)
    {
        var list = rows.Take(Math.Max(0, height)).ToList();
        while (list.Count < height) list.Add(ConsoleRow.Blank);
        return list;
    }

    /// <summary>A selectable list's scroll position: the selection kept in view with a row of context where there is room.</summary>
    protected static int Scroll(int top, int selected, int count, int visible)
    {
        if (visible <= 0) return 0;
        if (selected < top + 1) top = selected - 1;
        if (selected > top + visible - 2) top = selected - visible + 2;
        return Math.Clamp(top, 0, Math.Max(0, count - visible));
    }

    /// <summary>Up, Down, PageUp, PageDown, Home, End and the wheel applied to a selection; null for any other key.</summary>
    protected static int? Move(ConsoleKeyPress key, int selected, int count, int page)
    {
        if (count == 0) return null;
        int? next = key.Kind switch
        {
            ConsoleKeyKind.Up => selected - 1,
            ConsoleKeyKind.Down => selected + 1,
            ConsoleKeyKind.PageUp => selected - Math.Max(1, page - 1),
            ConsoleKeyKind.PageDown => selected + Math.Max(1, page - 1),
            ConsoleKeyKind.Home => 0,
            ConsoleKeyKind.End => count - 1,
            ConsoleKeyKind.Wheel => selected + key.Delta,
            ConsoleKeyKind.Char when key.Char == 'k' => selected - 1,
            ConsoleKeyKind.Char when key.Char == 'j' => selected + 1,
            ConsoleKeyKind.Char when key.Char == 'g' => 0,
            ConsoleKeyKind.Char when key.Char == 'G' => count - 1,
            _ => null,
        };
        return next is { } n ? Math.Clamp(n, 0, count - 1) : null;
    }
}
