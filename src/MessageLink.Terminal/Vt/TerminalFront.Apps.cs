using System.Globalization;
using System.Text;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Vt;

// Full-screen console apps on the terminal's alternate screen: a title bar with a clock, the app's rows, and its key bar along the bottom (or its
// filter line while filtering). The scroll waits underneath unchanged; lines that arrive meanwhile are printed when the app closes.
public sealed partial class TerminalFront
{
    private ConsoleApp? shownApp;
    private bool appShown, frameDirty, fullFrame, filtering;
    private string filterText = "";
    private int filterCaret;
    private Timer? appTimer;

    private void ShowApp(ConsoleApp? app)
    {
        if (ReferenceEquals(app, shownApp)) return;
        StopApp();
        if (app is null) { LeaveAlternate(); return; }
        if (!interactive)
        {
            // Output that is not a terminal has no screen to take over: the app is closed again, and its command printed nothing.
            log("terminal app outcome=not_interactive");
            Post(() => session?.CloseApp());
            return;
        }
        shownApp = app;
        filtering = false;
        filterText = "";
        cycling = false;
        EndSearch(null);
        app.Changed += App_Changed;
        if (app.Refresh > TimeSpan.Zero) appTimer = new Timer(_ => Post(AppTick), null, app.Refresh, app.Refresh);
        if (!appShown)
        {
            var output = new StringBuilder(HideCursor);
            ClearArea(output);
            output.Append(Esc + "[?1049h" + Esc + "[H" + Esc + "[2J");
            device.Write(output.ToString());
            appShown = true;
        }
        // Drawn once the app has started, which happens just after it is shown.
        frameDirty = true;
        if (depth == 0) Post(Commit);
    }

    private void StopApp()
    {
        if (shownApp is { } app) app.Changed -= App_Changed;
        shownApp = null;
        appTimer?.Dispose();
        appTimer = null;
        filtering = false;
    }

    private void LeaveAlternate()
    {
        if (!appShown) return;
        appShown = false;
        frameDirty = fullFrame = false;
        // The main screen comes back as it was left, the cursor where the prompt area began; lines held meanwhile print under it now.
        device.Write(Esc + "[?1049l" + ShowCursor);
        areaDirty = true;
        Schedule();
    }

    private void App_Changed()
    {
        if (!OnLoop) { Post(App_Changed); return; }
        frameDirty = true;
        Schedule();
    }

    private void AppTick()
    {
        if (shownApp is not { } app || !appShown) return;
        app.Tick();
        frameDirty = true;
        Commit();
    }

    private void FilterRequested()
    {
        if (!OnLoop) { Post(FilterRequested); return; }
        if (shownApp is not { CanFilter: true } app) return;
        filtering = true;
        filterText = app.Filter;
        filterCaret = filterText.Length;
        frameDirty = true;
        Schedule();
    }

    private void FilterType(string typed)
    {
        if (typed == "\n") return;
        filterText = filterText[..filterCaret] + typed + filterText[filterCaret..];
        filterCaret += typed.Length;
        shownApp?.SetFilter(filterText);
        frameDirty = true;
    }

    private void AppKey(ConsoleKeyInfo key)
    {
        if (shownApp is not { } app || session is null) return;
        var letter = ControlLetter(key);
        if (letter == 'C') { session.CloseApp(); return; }
        if (letter == 'L') { fullFrame = frameDirty = true; return; }
        frameDirty = true;
        if (filtering)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter: filtering = false; return;
                case ConsoleKey.Escape:
                    filtering = false;
                    filterText = "";
                    app.SetFilter("");
                    return;
                case ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown:
                    session.AppKey(new ConsoleKeyPress(Kind(key)!.Value));
                    return;
                case ConsoleKey.Backspace:
                    if (filterCaret == 0) return;
                    var from = Previous(filterText, filterCaret);
                    filterText = filterText[..from] + filterText[filterCaret..];
                    filterCaret = from;
                    app.SetFilter(filterText);
                    return;
                case ConsoleKey.LeftArrow: filterCaret = Previous(filterText, filterCaret); return;
                case ConsoleKey.RightArrow: filterCaret = Next(filterText, filterCaret); return;
                case ConsoleKey.Home: filterCaret = 0; return;
                case ConsoleKey.End: filterCaret = filterText.Length; return;
            }
            if (letter == '\0' && IsText(key.KeyChar)) FilterType(key.KeyChar.ToString());
            return;
        }
        if (Kind(key) is { } kind) session.AppKey(new ConsoleKeyPress(kind));
        else if (letter == '\0' && IsText(key.KeyChar)) session.AppKey(ConsoleKeyPress.Of(key.KeyChar));
    }

    private static ConsoleKeyKind? Kind(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.UpArrow => ConsoleKeyKind.Up,
        ConsoleKey.DownArrow => ConsoleKeyKind.Down,
        ConsoleKey.PageUp => ConsoleKeyKind.PageUp,
        ConsoleKey.PageDown => ConsoleKeyKind.PageDown,
        ConsoleKey.Home => ConsoleKeyKind.Home,
        ConsoleKey.End => ConsoleKeyKind.End,
        ConsoleKey.LeftArrow => ConsoleKeyKind.Left,
        ConsoleKey.RightArrow => ConsoleKeyKind.Right,
        ConsoleKey.Enter => ConsoleKeyKind.Enter,
        ConsoleKey.Tab => Has(key, ConsoleModifiers.Shift) ? ConsoleKeyKind.BackTab : ConsoleKeyKind.Tab,
        ConsoleKey.Escape => ConsoleKeyKind.Escape,
        _ => null,
    };

    // One frame, written at once from the top-left: every row, each ended by erasing what is left of the old one, so nothing flickers.
    private void DrawApp()
    {
        frameDirty = false;
        if (shownApp is not { } app || session is null) return;
        var width = Math.Max(1, columns);
        var body = Math.Max(1, rows - 2);
        ConsoleScreen screen;
        try { screen = app.Render(width, body); }
        catch (Exception error)
        {
            log($"console app render failed name={app.Name} error={error.GetType().Name}");
            session.CloseApp();
            return;
        }
        var bar = ink.Theme.Bar;
        var barSpace = ink.Style(ConsoleTone.Default, background: bar);
        var output = new StringBuilder(HideCursor);
        if (fullFrame) { output.Append(Esc + "[2J"); fullFrame = false; }
        output.Append(Esc + "[H");

        var clock = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var title = new List<VtCell>();
        foreach (var span in screen.Title)
            VtText.Add(title, span.Text, ink.Style(span.Tone, span.Family, bar, bold: span.Tone is ConsoleTone.Accent or ConsoleTone.Strong));
        var clockCells = new List<VtCell>();
        VtText.Add(clockCells, clock, ink.Style(ConsoleTone.Dim, background: bar));
        var room = width - VtText.Width(clockCells) - 1;
        List<VtCell> titleRow = room >= 4 ? [.. VtText.Pad(title, room, barSpace), new VtCell(" ", 1, barSpace), .. clockCells] : VtText.Pad(title, width, barSpace);
        Row(output, titleRow, width, last: false);

        var selection = ink.Style(ConsoleTone.Default, background: ink.Theme.Selection);
        for (var index = 0; index < body; index++)
        {
            var data = index < screen.Rows.Count ? screen.Rows[index] : ConsoleRow.Blank;
            var cells = new List<VtCell>();
            foreach (var span in data.Spans)
            {
                var tone = data.Selected && span.Tone is ConsoleTone.Default or ConsoleTone.Dim ? ConsoleTone.Strong : span.Tone;
                VtText.Add(cells, span.Text, ink.Style(tone, span.Family, data.Selected ? ink.Theme.Selection : null));
            }
            Row(output, data.Selected ? VtText.Pad(cells, width, selection) : VtText.Fit(cells, width), width, last: false);
        }

        var bottom = new List<VtCell>();
        var caretColumn = -1;
        if (filtering)
        {
            VtText.Add(bottom, app.Name + "/", ink.Style(ConsoleTone.Accent, background: bar));
            VtText.Add(bottom, filterText[..filterCaret], ink.Style(ConsoleTone.Strong, background: bar));
            caretColumn = VtText.Width(bottom);
            VtText.Add(bottom, filterText[filterCaret..], ink.Style(ConsoleTone.Strong, background: bar));
        }
        else if (screen.Message is { } message) VtText.Add(bottom, message, ink.Style(ConsoleTone.Warn, background: bar));
        else
            foreach (var (name, label) in screen.Keys.Where(key => key.Key.Length > 0))
            {
                VtText.Add(bottom, name, ink.Style(ConsoleTone.Strong, background: bar));
                VtText.Add(bottom, " " + label + "   ", ink.Style(ConsoleTone.Dim, background: bar));
            }
        Row(output, VtText.Pad(bottom, width, barSpace), width, last: true);
        if (caretColumn >= 0)
            output.Append(Esc + "[").Append((body + 2).ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append((Math.Min(caretColumn, width - 1) + 1).ToString(CultureInfo.InvariantCulture)).Append('H').Append(ShowCursor);
        device.Write(output.ToString());
    }

    private static void Row(StringBuilder output, List<VtCell> row, int width, bool last)
    {
        VtText.Draw(output, row);
        // A full row ends with the cursor waiting to wrap, where an erase would take its last cell.
        if (VtText.Width(row) < width) output.Append(Esc + "[K");
        if (!last) output.Append("\r\n");
    }
}
