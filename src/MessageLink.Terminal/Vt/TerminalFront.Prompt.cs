using System.Diagnostics;
using System.Globalization;
using System.Text;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Vt;

// The prompt: editing keys as readline has them, history, Tab's choices, fish's gray suggestion, bash's reverse-i-search, and the area under the
// scroll it is drawn in (a status line for typing and hints, the prompt, then Tab's choices or the command's synopsis).
public sealed partial class TerminalFront
{
    private const int ChoicesShown = 40;

    private string text = "";
    private int caret;
    private bool cycling;
    private char? highHalf;

    // Reverse-i-search: what is searched for, which match, the line it replaced, and whether the first key typed replaces the query.
    private bool searching, searchFresh;
    private int searchIndex;
    private string searchQuery = "", searchDraft = "";
    private string? searchMatch;

    // ---- Reading keys ----

    /// <summary>
    /// How long a batch of more than one key stays open for more after the last one came: a paste that arrives in pieces (over SSH, through
    /// ConPTY) is still one paste, and a newline at the end of a piece is still part of it. Every key that comes within it extends it.
    /// </summary>
    public TimeSpan PasteGrace { get; set; } = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// How long a bracketed paste whose end mark has not come yet still counts as open after its last key: a terminal that lost the end mark
    /// must not leave every later Enter a new line.
    /// </summary>
    public TimeSpan PasteFrameLimit { get; set; } = TimeSpan.FromSeconds(1);

    private const string PasteStart = Esc + "[200~", PasteEnd = Esc + "[201~";

    private void StartKeys()
    {
        var thread = new Thread(() =>
        {
            var framed = false;
            var sinceKey = Stopwatch.StartNew();
            while (running)
            {
                var first = device.ReadKey();
                if (first is null) { Post(() => End(0)); return; }
                // Whatever is already waiting comes as one batch: a paste is drawn once, and every Enter in it is a new line, never a send (a Tab
                // a space, not a completion). The person reads it and presses Enter.
                var keys = new List<ConsoleKeyInfo> { first.Value };
                TakeWaiting(keys);
                if (keys.Count > 1) Linger(keys);
                if (framed && sinceKey.Elapsed > PasteFrameLimit) { framed = false; log("terminal paste outcome=end_mark_missing"); }
                sinceKey.Restart();
                // A terminal that marks its pastes says where one ends, so a piece that comes late (a lone newline) is still part of it.
                var pasted = keys.Count > 1 || framed;
                if (framesPastes) keys = Unframe(keys, ref framed, ref pasted);
                if (keys.Count > 0) Post(() => Keys(keys, pasted));
            }
        }) { IsBackground = true, Name = "mlink keys" };
        thread.Start();
    }

    // Takes out the bracketed-paste marks (.NET hands them over as the keys Esc [ 2 0 0 ~ and Esc [ 2 0 1 ~); keys from a start mark on are pasted.
    private static List<ConsoleKeyInfo> Unframe(List<ConsoleKeyInfo> keys, ref bool framed, ref bool pasted)
    {
        var kept = new List<ConsoleKeyInfo>(keys.Count);
        for (var index = 0; index < keys.Count; index++)
        {
            if (MarkAt(keys, index, PasteStart)) { framed = pasted = true; index += PasteStart.Length - 1; continue; }
            if (MarkAt(keys, index, PasteEnd)) { framed = false; pasted = true; index += PasteEnd.Length - 1; continue; }
            kept.Add(keys[index]);
        }
        return kept;
    }

    private static bool MarkAt(List<ConsoleKeyInfo> keys, int index, string mark)
    {
        if (index + mark.Length > keys.Count) return false;
        for (var offset = 0; offset < mark.Length; offset++)
            if (keys[index + offset].KeyChar != mark[offset] || keys[index + offset].Modifiers != 0) return false;
        return true;
    }

    private void TakeWaiting(List<ConsoleKeyInfo> keys)
    {
        while (device.KeyAvailable && device.ReadKey() is { } next) keys.Add(next);
    }

    private void Linger(List<ConsoleKeyInfo> keys)
    {
        var quiet = Stopwatch.StartNew();
        while (running && quiet.Elapsed < PasteGrace)
        {
            if (!device.KeyAvailable) { Thread.Sleep(1); continue; }
            var before = keys.Count;
            TakeWaiting(keys);
            // Available but nothing came (the input ended): the batch is done, and the next read says so.
            if (keys.Count == before) return;
            quiet.Restart();
        }
    }

    // Where a paste may go is decided here, once: the prompt and an app's filter line take its text, and an app's own keys take only keys typed
    // one at a time, so nothing pasted can move, open, mute or mark read, or answer an app's question.
    private void Keys(List<ConsoleKeyInfo> keys, bool pasting) => Batch(() =>
    {
        // A paste's plain text goes into the prompt in runs, not a key at a time: a long paste costs one edit per run.
        var run = new StringBuilder();
        var refused = false;
        for (var index = 0; index < keys.Count && running; index++)
        {
            var key = keys[index];
            if (pasting && session is not null && appShown)
            {
                if (!filtering) { refused = true; continue; }
                if (PlainText(key)) Collect(run, key.KeyChar);
                else if (key.Key == ConsoleKey.Tab) run.Append(' ');
                continue;
            }
            if (pasting && session is not null && PlainText(key)) { Collect(run, key.KeyChar); continue; }
            TypeRun(run);
            Key(key, pasting);
        }
        TypeRun(run);
        if (refused && session is not null && session.AppPasted()) frameDirty = true;
    });

    // A key that does nothing but type its character, in Key's own terms.
    private static bool PlainText(ConsoleKeyInfo key) =>
        (IsText(key.KeyChar) || char.IsSurrogate(key.KeyChar)) && ControlLetter(key) == '\0' && !Has(key, ConsoleModifiers.Alt) &&
        key.Key is not (ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.Backspace or ConsoleKey.Delete or ConsoleKey.LeftArrow or ConsoleKey.RightArrow
            or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Escape or >= ConsoleKey.F1 and <= ConsoleKey.F24
            or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Insert);

    // The two halves of a character beyond the first plane are joined as Key joins them; a half without its other half is dropped.
    private void Collect(StringBuilder run, char c)
    {
        if (char.IsHighSurrogate(c)) { highHalf = c; return; }
        if (char.IsLowSurrogate(c))
        {
            if (highHalf is { } high) run.Append(high).Append(c);
            highHalf = null;
            return;
        }
        highHalf = null;
        run.Append(c);
    }

    private void TypeRun(StringBuilder run)
    {
        if (run.Length == 0) return;
        if (!searching && cycling) { cycling = false; areaDirty = true; }
        Type(run.ToString());
        run.Clear();
    }

    private static bool Has(ConsoleKeyInfo key, ConsoleModifiers modifier) => (key.Modifiers & modifier) != 0;

    private static bool IsText(char c) => c >= ' ' && !char.IsControl(c);

    // Ctrl and a letter, as Windows reports it (the letter's key with Control) or as a terminal sends it (the control character).
    private static char ControlLetter(ConsoleKeyInfo key)
    {
        if (key.KeyChar is >= '\u0001' and <= '\u001a' && key.Key is not (ConsoleKey.Tab or ConsoleKey.Enter or ConsoleKey.Backspace or ConsoleKey.Escape))
            return (char)('A' + key.KeyChar - 1);
        if (Has(key, ConsoleModifiers.Control) && !Has(key, ConsoleModifiers.Alt) && key.Key is >= ConsoleKey.A and <= ConsoleKey.Z && !IsText(key.KeyChar))
            return (char)('A' + (key.Key - ConsoleKey.A));
        return '\0';
    }

    private void Key(ConsoleKeyInfo key, bool pasting)
    {
        if (session is null) return;
        // A character beyond the first plane arrives in two halves.
        if (char.IsHighSurrogate(key.KeyChar)) { highHalf = key.KeyChar; return; }
        if (char.IsLowSurrogate(key.KeyChar))
        {
            if (highHalf is { } high) Type(new string([high, key.KeyChar]));
            highHalf = null;
            return;
        }
        highHalf = null;
        if (appShown) { AppKey(key); return; }
        if (searching) { SearchKey(key); return; }
        if (key.Key != ConsoleKey.Tab && cycling) { cycling = false; areaDirty = true; }
        var control = Has(key, ConsoleModifiers.Control);
        var alt = Has(key, ConsoleModifiers.Alt);
        var shift = Has(key, ConsoleModifiers.Shift);
        switch (key.Key)
        {
            case ConsoleKey.Enter when shift || alt || pasting: Type("\n"); return;
            case ConsoleKey.Enter: Submit(); return;
            case ConsoleKey.Tab when pasting: Type(" "); return;
            case ConsoleKey.Tab: Complete(shift); return;
            case ConsoleKey.Backspace: if (control || alt) DeleteWordBefore(); else DeleteBefore(); return;
            case ConsoleKey.Delete: DeleteAfter(); return;
            case ConsoleKey.LeftArrow: Move(control || alt ? WordLeft(text, caret) : Previous(text, caret)); return;
            case ConsoleKey.RightArrow: if (!AcceptGhost()) Move(control || alt ? WordRight(text, caret) : Next(text, caret)); return;
            case ConsoleKey.Home: Move(0); return;
            case ConsoleKey.End: if (!AcceptGhost()) Move(text.Length); return;
            case ConsoleKey.UpArrow: Recall(session.HistoryUp(text)); return;
            case ConsoleKey.DownArrow: Recall(session.HistoryDown(text)); return;
            case ConsoleKey.Escape: SetInput(session.Escape(text), 0); return;
            case ConsoleKey.F1: session.OpenGuide("the-chat-console"); return;
            case >= ConsoleKey.F2 and <= ConsoleKey.F24 or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Insert: return;
        }
        switch (ControlLetter(key))
        {
            case '\0': break;
            case 'A': Move(0); return;
            case 'B': Move(Previous(text, caret)); return;
            case 'C': Interrupt(); return;
            case 'D':
                if (text.Length == 0) { log("terminal quit via=eof"); End(0); }
                else DeleteAfter();
                return;
            case 'E': if (!AcceptGhost()) Move(text.Length); return;
            case 'F': if (!AcceptGhost()) Move(Next(text, caret)); return;
            case 'H': DeleteBefore(); return;
            case 'I': Complete(false); return;
            case 'J' or 'M': if (pasting) Type("\n"); else Submit(); return;
            case 'K': SetInput(text[..caret], caret); return;
            case 'L': ClearScreen(); return;
            case 'N': Recall(session.HistoryDown(text)); return;
            case 'P': Recall(session.HistoryUp(text)); return;
            case 'R': BeginSearch(); return;
            case 'U': SetInput(text[caret..], 0); return;
            case 'W': DeleteWordBefore(); return;
            default: return;
        }
        if (alt && !control)
            switch (key.Key)
            {
                case ConsoleKey.B: Move(WordLeft(text, caret)); return;
                case ConsoleKey.F: Move(WordRight(text, caret)); return;
                case ConsoleKey.D: SetInput(text[..caret] + text[WordRight(text, caret)..], caret); return;
            }
        if (IsText(key.KeyChar)) Type(key.KeyChar.ToString());
    }

    // Typed text goes where the keys go now: the search, an app's filter, or the prompt.
    private void Type(string typed)
    {
        if (appShown) { if (filtering) FilterType(typed); return; }
        if (searching)
        {
            if (typed == "\n") return;
            if (searchFresh) searchQuery = "";
            searchFresh = false;
            searchQuery += typed;
            searchIndex = 0;
            UpdateSearch();
            return;
        }
        SetInput(text[..caret] + typed + text[caret..], caret + typed.Length);
    }

    // Every edit, typed or put there, is told to the session: it fixes or releases the /r reply target.
    private void SetInput(string value, int at)
    {
        text = value;
        caret = Math.Clamp(at, 0, text.Length);
        session?.InputChanged(text);
        areaDirty = true;
    }

    private void Move(int at)
    {
        caret = Math.Clamp(at, 0, text.Length);
        areaDirty = true;
    }

    private void Recall(string? line)
    {
        if (line is not null) SetInput(line, line.Length);
    }

    private void DeleteBefore()
    {
        if (caret == 0) return;
        var from = Previous(text, caret);
        SetInput(text[..from] + text[caret..], from);
    }

    private void DeleteAfter()
    {
        if (caret >= text.Length) return;
        SetInput(text[..caret] + text[Next(text, caret)..], caret);
    }

    private void DeleteWordBefore()
    {
        var from = WordLeft(text, caret);
        SetInput(text[..from] + text[caret..], from);
    }

    private static int Previous(string value, int at)
    {
        var last = 0;
        for (var index = 0; index < at && index < value.Length; index += Math.Max(1, StringInfo.GetNextTextElementLength(value, index))) last = index;
        return Math.Min(last, at);
    }

    private static int Next(string value, int at) =>
        at >= value.Length ? value.Length : Math.Min(value.Length, at + Math.Max(1, StringInfo.GetNextTextElementLength(value, at)));

    private static int WordLeft(string value, int at)
    {
        while (at > 0 && char.IsWhiteSpace(value[at - 1])) at--;
        while (at > 0 && !char.IsWhiteSpace(value[at - 1])) at--;
        return at;
    }

    private static int WordRight(string value, int at)
    {
        while (at < value.Length && char.IsWhiteSpace(value[at])) at++;
        while (at < value.Length && !char.IsWhiteSpace(value[at])) at++;
        return at;
    }

    // fish's gray suggestion after the caret: the rest of the newest earlier command that starts with the line.
    private string? Ghost() =>
        session is not null && !searching && !appShown && caret == text.Length && !text.Contains('\n') ? session.Suggestion(text) : null;

    private bool AcceptGhost()
    {
        if (Ghost() is not { } rest) return false;
        SetInput(text + rest, text.Length + rest.Length);
        return true;
    }

    private void Complete(bool backwards)
    {
        if (session is null) return;
        var (completed, at) = session.Complete(text, caret, backwards);
        if (completed == text && at == caret) return;
        cycling = true;
        SetInput(completed, at);
    }

    // Enter. The session takes the line (and fixes a /r target) before the prompt is emptied, so emptying it cannot let a reply target go between.
    private void Submit()
    {
        if (session is null) return;
        var line = text;
        if (IsExit(line)) { log("terminal quit via=command"); End(0); return; }
        var submitted = session.SubmitAsync(line);
        SetInput("", 0);
        _ = ObserveAsync(submitted);
    }

    private async Task ObserveAsync(Task submitted)
    {
        try { await submitted; }
        catch (Exception error) { log($"console submit failed error={error.GetType().Name}"); }
    }

    // ^C: the line is dropped and left in the scroll with ^C after it, as a shell leaves it.
    private void Interrupt()
    {
        if (session is null) return;
        if (text.Length > 0) session.Interrupt(text);
        SetInput("", 0);
    }

    // ^L: /clear, which clears the screen and the scrollback with the session's list.
    private void ClearScreen()
    {
        if (session is null || session.ActiveApp is not null) return;
        clearQueued = true;
        _ = ObserveAsync(session.SubmitAsync("/clear"));
    }

    // ---- Reverse-i-search, in the prompt as bash has it ----

    private void BeginSearch()
    {
        if (session is null || appShown) return;
        if (searching)
        {
            searchIndex++;
            if (session.FindInputHistory(searchQuery, searchIndex) is null) searchIndex = 0;
            UpdateSearch();
            return;
        }
        searching = true;
        searchDraft = text;
        searchQuery = text;
        searchFresh = text.Length > 0;
        searchIndex = 0;
        UpdateSearch();
    }

    private void UpdateSearch()
    {
        searchMatch = session?.FindInputHistory(searchQuery, searchIndex);
        areaDirty = true;
    }

    private void SearchKey(ConsoleKeyInfo key)
    {
        var letter = ControlLetter(key);
        if (key.Key is ConsoleKey.Enter or ConsoleKey.RightArrow || letter is 'J' or 'M') { EndSearch(searchMatch ?? searchDraft); return; }
        if (key.Key == ConsoleKey.Escape || letter is 'C' or 'G') { EndSearch(searchDraft); return; }
        if (letter == 'R') { BeginSearch(); return; }
        if (key.Key == ConsoleKey.Backspace || letter == 'H')
        {
            searchQuery = searchFresh ? "" : searchQuery[..Previous(searchQuery, searchQuery.Length)];
            searchFresh = false;
            searchIndex = 0;
            UpdateSearch();
            return;
        }
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.UpArrow or ConsoleKey.DownArrow || letter is 'A' or 'E')
        {
            // A movement key takes the match and then moves in it, as bash does.
            EndSearch(searchMatch ?? searchDraft);
            Key(key, pasting: false);
            return;
        }
        if (letter == '\0' && IsText(key.KeyChar)) Type(key.KeyChar.ToString());
    }

    /// <param name="value">What the prompt then holds; null leaves it as it was.</param>
    private void EndSearch(string? value)
    {
        if (!searching) return;
        searching = false;
        searchMatch = null;
        areaDirty = true;
        if (value is not null) SetInput(value, value.Length);
    }

    // ---- The prompt area ----

    private void ClearArea(StringBuilder output)
    {
        if (!areaShown) return;
        output.Append('\r');
        if (areaRow > 0) output.Append(Esc + "[").Append(areaRow.ToString(CultureInfo.InvariantCulture)).Append('A');
        output.Append(Esc + "[J");
        areaShown = false;
        areaRow = 0;
    }

    private void DrawArea(StringBuilder output)
    {
        if (!interactive || appShown || session is null) return;
        var width = Math.Max(1, columns);
        var area = new List<List<VtCell>>();
        var hint = session.Hint;
        var newer = hint is not null && hint.StartsWith("Newer: ", StringComparison.Ordinal);
        if ((newer ? hint : session.Typing.Text ?? hint) is { } note)
        {
            var status = new List<VtCell>();
            VtText.Add(status, note, ink.Style(newer ? ConsoleTone.Warn : ConsoleTone.Dim));
            area.Add(VtText.Fit(status, width));
        }
        var (prompt, caretRow, caretColumn) = PromptRows(width);
        var below = BelowRows(width);
        // Never taller than the window less a row: an area taller than the screen could not be gone back over to clear it, and every redraw would
        // push its top rows into the scrollback again. Tab's choices give way first, then the status line in a tiny window, then the input shows
        // the rows around the caret.
        var budget = Math.Max(1, rows - 1);
        if (area.Count + prompt.Count + below.Count > budget)
        {
            below = below.GetRange(0, Math.Clamp(budget - area.Count - prompt.Count, 0, below.Count));
            if (area.Count + prompt.Count > budget && budget < 3) area.Clear();
            if (prompt.Count > budget - area.Count) (prompt, caretRow) = PromptWindow(prompt, caretRow, budget - area.Count);
        }
        var top = area.Count;
        area.AddRange(prompt);
        area.AddRange(below);
        for (var index = 0; index < area.Count; index++)
        {
            VtText.Draw(output, area[index]);
            if (index < area.Count - 1) output.Append("\r\n");
        }
        var target = top + caretRow;
        output.Append('\r');
        if (area.Count - 1 - target > 0) output.Append(Esc + "[").Append((area.Count - 1 - target).ToString(CultureInfo.InvariantCulture)).Append('A');
        if (caretColumn > 0) output.Append(Esc + "[").Append(caretColumn.ToString(CultureInfo.InvariantCulture)).Append('C');
        areaShown = true;
        areaRow = target;
        areaHeight = area.Count;
    }

    // "messages>" in the accent, or "messages:" and the target's name in its service's colour; the input after it, wrapped to the width, with the
    // gray suggestion after the caret. Returns the rows and where the caret is in them.
    private (List<List<VtCell>> Rows, int CaretRow, int CaretColumn) PromptRows(int width)
    {
        var cells = new List<VtCell>();
        var accent = ink.Style(ConsoleTone.Accent);
        var strong = ink.Style(ConsoleTone.Strong);
        var dim = ink.Style(ConsoleTone.Dim);
        int caretCell;
        if (searching)
        {
            VtText.Add(cells, searchMatch is null && searchQuery.Length > 0 ? "(failed reverse-i-search)'" : "(reverse-i-search)'", accent);
            VtText.Add(cells, searchQuery, strong);
            caretCell = cells.Count;
            VtText.Add(cells, "': ", accent);
            VtText.Add(cells, searchMatch ?? "", ink.Style(ConsoleTone.Default));
        }
        else
        {
            var prompt = session!.Prompt;
            if (session.Target is { } target && prompt.StartsWith("messages:", StringComparison.Ordinal) && prompt.EndsWith('>'))
            {
                VtText.Add(cells, "messages:", accent);
                VtText.Add(cells, prompt[9..^1], ink.Style(ConsoleTone.Service, BubbleColours.TagFamily(target.Network, null), bold: true));
                VtText.Add(cells, ">", accent);
            }
            else VtText.Add(cells, prompt, accent);
            cells.Add(new VtCell(" ", 1, ""));
            caretCell = -1;
            var at = 0;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                if (at == caret) caretCell = cells.Count;
                var element = (string)elements.Current;
                at += element.Length;
                if (element is "\n" or "\r\n") cells.Add(new VtCell("↵", 1, dim));
                else VtText.Add(cells, element, strong);
            }
            if (caretCell < 0) caretCell = cells.Count;
            if (Ghost() is { } ghost) VtText.Add(cells, ghost, dim);
        }
        var rows = new List<List<VtCell>> { new() };
        int row = 0, column = 0, caretRow = -1, caretColumn = 0;
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            if (column + cell.Width > width && column > 0) { rows.Add([]); row++; column = 0; }
            if (index == caretCell) (caretRow, caretColumn) = (row, column);
            rows[row].Add(cell);
            column += cell.Width;
        }
        if (caretRow < 0)
        {
            (caretRow, caretColumn) = (row, column);
            // A caret just past a full row sits at the start of the next.
            if (caretColumn >= width) { caretRow++; caretColumn = 0; }
        }
        while (rows.Count <= caretRow) rows.Add([]);
        return (rows, caretRow, caretColumn);
    }

    // The input's rows that fit in <paramref name="room"/>, the caret's among them; a dim … row stands for the rows above or below that are not
    // shown, when there is room for it.
    private (List<List<VtCell>> Rows, int CaretRow) PromptWindow(List<List<VtCell>> all, int caretRow, int room)
    {
        room = Math.Max(1, room);
        var marks = room >= 3;
        var start = Math.Clamp(caretRow - room + (marks ? 2 : 1), 0, all.Count - room);
        var shown = all.GetRange(start, room);
        List<VtCell> Mark() => [new VtCell("…", 1, ink.Style(ConsoleTone.Dim))];
        if (marks && start > 0) shown[0] = Mark();
        if (marks && start + room < all.Count) shown[^1] = Mark();
        return (shown, caretRow - start);
    }

    // Under the prompt: Tab's choices as zsh lists them, the current one picked out; else the synopsis of the command being typed.
    private List<List<VtCell>> BelowRows(int width)
    {
        if (session is null || searching) return [];
        if (cycling)
        {
            var choices = session.CompletionChoices;
            if (choices.Count is < 2 or > ChoicesShown) return [];
            var current = session.CompletionIndex;
            var cells = new List<VtCell>();
            for (var index = 0; index < choices.Count; index++)
            {
                if (index > 0) VtText.Add(cells, "  ", "");
                VtText.Add(cells, choices[index], index == current ? ink.Style(ConsoleTone.Strong, underline: true) : ink.Style(ConsoleTone.Dim));
            }
            var rows = VtText.Wrap(cells, width);
            if (rows.Count > 4) { rows = rows.GetRange(0, 4); rows[3] =VtText.Fit([.. rows[3], new VtCell(" ", 1, ""), new VtCell("…", 1, "")], width); }
            return rows;
        }
        if (text.Length == 0 || text.Contains('\n') || session.Synopsis(text) is not { } hint) return [];
        var line = new List<VtCell>();
        VtText.Add(line, hint.Synopsis + "  " + hint.Summary, ink.Style(ConsoleTone.Dim));
        return [VtText.Fit(line, width)];
    }
}
