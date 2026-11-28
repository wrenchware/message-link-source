using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Terminal.Vt;

/// <summary>
/// The terminal mlink draws in: VT output (lines into the normal scrollback, the prompt at the bottom, full-screen apps on the alternate screen) and
/// keys in. Made and run on the <see cref="TerminalLoop"/>'s thread.
/// </summary>
/// <remarks>
/// Lines go into the terminal's own scrollback, so its find, copy and scrolling work on them; the prompt area (a status line, the prompt, Tab's
/// choices) is drawn under the newest line and redrawn around each line that arrives, readline-style, with the input and caret kept. Only appended
/// lines are printed: when the session's filter rebuilds its list the lines already printed stay, and new ones keep coming under them. A cleared
/// list (/clear, Ctrl+L, a new account) clears the screen and the scrollback. Everything here runs on the loop's thread; keys are read on a thread
/// of their own and posted to it, and every change made between two writes leaves as one write.
/// </remarks>
public sealed partial class TerminalFront : IDisposable
{
    private const string Esc = "\u001b";
    private const string HideCursor = Esc + "[?25l", ShowCursor = Esc + "[?25h";
    private const string BracketedOn = Esc + "[?2004h", BracketedOff = Esc + "[?2004l";

    private readonly ITerminalDevice device;
    private readonly Action<string> log;
    private readonly LineLayout layout = new();
    private SynchronizationContext? loop;
    private ConsoleSessionViewModel? session;
    private VtPalette ink = new(null);
    private TaskCompletionSource<int>? done;
    private CancellationTokenSource? lifetime;
    private Timer? typingTick;
    private volatile bool running;
    private bool interactive, disposed;
    // Bracketed paste is on: the terminal marks where each paste starts and ends.
    private volatile bool framesPastes;
    private int columns = 80, rows = 24;

    // What is waiting to be written: the next write takes all of it at once.
    private int depth;
    private bool commitPosted, areaDirty, clearQueued;
    private readonly List<ConsoleLineViewModel> queued = [];
    private readonly List<ConsoleLineViewModel> stateChanged = [];
    private long printedSeq = long.MinValue;

    // The prompt area on screen: whether it is drawn, the row the cursor is on counted from its top, and how many rows it took (kept once it is
    // cleared, to know whether the line above it is still on screen).
    private bool areaShown;
    private int areaRow, areaHeight;

    // The last line printed and how many rows it took, while nothing has been printed under it: a send's line can be redrawn in place until then.
    private ConsoleLineViewModel? lastLine;
    private int lastRows;
    private readonly HashSet<ConsoleLineViewModel> watched = [];

    private TerminalFront(ITerminalDevice device, Action<string> log)
    {
        this.device = device;
        this.log = log;
    }

    /// <summary>The terminal this process was started in. Turns on VT input and output on Windows.</summary>
    public static TerminalFront OpenSystem(Action<string> log) => new(new SystemTerminal(log), log);

    /// <summary>A front over any device: the checks' fake terminal.</summary>
    public static TerminalFront Create(ITerminalDevice device, Action<string> log) => new(device, log);

    /// <summary>OSC 52: the sequence that asks the terminal to put <paramref name="text"/> on its clipboard.</summary>
    public static string Osc52(string text) => Esc + "]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\a";

    /// <summary>Puts text on the clipboard of the terminal the person is looking at (OSC 52), even over SSH.</summary>
    public void Copy(string text)
    {
        if (text.Length == 0) return;
        if (device.OutputIsTerminal) device.Write(Osc52(text));
        // Windows' own console ignores OSC 52; on this PC's desktop the clipboard is put right here as well.
        var local = device.SetLocalClipboard(text);
        log($"terminal copy chars={text.Length.ToString(CultureInfo.InvariantCulture)} local={(local ? 1 : 0)}");
    }

    /// <summary>What is in the prompt now, and where the caret is (for the checks).</summary>
    public string Input => text;
    public int Caret => caret;

    /// <summary>Runs the console until the person quits (/exit, Ctrl+D on an empty line) or <paramref name="cancellationToken"/> ends; returns the exit code.</summary>
    public async Task<int> RunAsync(ConsoleSessionViewModel session, ITypingService? typing, CancellationToken cancellationToken)
    {
        if (running) throw new InvalidOperationException("The terminal is already running a console.");
        loop = SynchronizationContext.Current ?? throw new InvalidOperationException("RunAsync needs the terminal loop.");
        this.session = session;
        done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Started first: whether the output takes VT is known only once it has been asked to.
        device.Start();
        interactive = device.InputIsTerminal && device.OutputIsTerminal;
        running = true;
        ReadTheme();
        (columns, rows) = (device.Columns, device.Rows);
        if (device.OutputIsTerminal) session.Columns = Math.Max(30, columns);
        session.Motion = interactive;
        log($"terminal start interactive={(interactive ? 1 : 0)} columns={columns.ToString(CultureInfo.InvariantCulture)} rows={rows.ToString(CultureInfo.InvariantCulture)}");

        session.Lines.CollectionChanged += Lines_Changed;
        session.PropertyChanged += Session_PropertyChanged;
        session.Typing.Changed += Typing_Changed;
        session.InputSuggested += InputSuggested;
        session.FilterRequested += FilterRequested;
        device.Resized += Device_Resized;
        using var cancel = lifetime.Token.Register(() => Post(() => End(0)));
        try
        {
            foreach (var line in session.Lines) Queue(line);
            framesPastes = interactive && device.FramesPastes;
            if (interactive) device.Write("\r" + Esc + "[K" + (framesPastes ? BracketedOn : ""));
            areaDirty = true;
            Commit();
            if (session.ActiveApp is { } app) ShowApp(app);
            if (typing is not null) _ = WatchTypingAsync(typing, lifetime.Token);
            typingTick = new Timer(_ => Post(TypingTick), null, 1000, 1000);
            if (interactive) StartKeys(); else StartLines();
            return await done.Task;
        }
        finally
        {
            running = false;
            lifetime.Cancel();
            typingTick?.Dispose();
            StopApp();
            session.Lines.CollectionChanged -= Lines_Changed;
            session.PropertyChanged -= Session_PropertyChanged;
            session.Typing.Changed -= Typing_Changed;
            session.InputSuggested -= InputSuggested;
            session.FilterRequested -= FilterRequested;
            device.Resized -= Device_Resized;
            foreach (var line in watched) line.PropertyChanged -= Line_PropertyChanged;
            watched.Clear();
            if (interactive)
            {
                var output = new StringBuilder();
                if (appShown) output.Append(Esc + "[?1049l");
                appShown = false;
                ClearArea(output);
                output.Append(VtPalette.Reset).Append(ShowCursor);
                if (framesPastes) output.Append(BracketedOff);
                framesPastes = false;
                device.Write(output.ToString());
            }
            device.Restore();
        }
    }

    private void End(int code)
    {
        if (!running) return;
        log($"terminal end code={code.ToString(CultureInfo.InvariantCulture)}");
        done?.TrySetResult(code);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime?.Cancel();
        lifetime?.Dispose();
        typingTick?.Dispose();
        appTimer?.Dispose();
        device.Restore();
    }

    // ---- The loop ----

    private bool OnLoop => loop is not null && SynchronizationContext.Current == loop;

    private void Post(Action action)
    {
        if (loop is null) return;
        loop.Post(_ =>
        {
            if (!running) return;
            try { action(); }
            catch (Exception failure) { log($"terminal failed error={failure.GetType().Name}"); }
        }, null);
    }

    /// <summary>Runs <paramref name="action"/> with every write it causes held back, then writes once.</summary>
    private void Batch(Action action)
    {
        depth++;
        try { action(); }
        finally
        {
            depth--;
            if (depth == 0) Commit();
        }
    }

    // Something changed outside a key: one write for everything that changes before the loop comes round again.
    private void Schedule()
    {
        if (depth > 0 || commitPosted || !running) return;
        commitPosted = true;
        Post(Commit);
    }

    private void Commit()
    {
        commitPosted = false;
        if (!running) return;
        if (appShown) { if (frameDirty) DrawApp(); return; }
        if (!clearQueued && queued.Count == 0 && stateChanged.Count == 0 && !areaDirty) return;
        var output = new StringBuilder();
        if (interactive) output.Append(HideCursor);
        ClearArea(output);
        if (clearQueued)
        {
            clearQueued = false;
            if (device.OutputIsTerminal) output.Append(Esc + "[H" + Esc + "[2J" + Esc + "[3J");
            lastLine = null;
            foreach (var line in watched) line.PropertyChanged -= Line_PropertyChanged;
            watched.Clear();
        }
        foreach (var line in stateChanged) Restate(output, line);
        stateChanged.Clear();
        foreach (var line in queued) Print(output, line);
        queued.Clear();
        areaDirty = false;
        DrawArea(output);
        if (interactive) output.Append(ShowCursor);
        device.Write(output.ToString());
    }

    // ---- Lines ----

    private void Lines_Changed(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!OnLoop) { Post(() => Lines_Changed(sender, args)); return; }
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewItems is { } added:
                foreach (ConsoleLineViewModel line in added) Queue(line);
                break;
            case NotifyCollectionChangedAction.Reset when session?.Lines.Count == 0:
                queued.Clear();
                stateChanged.Clear();
                clearQueued = true;
                break;
        }
        Schedule();
    }

    // Only lines newer than every line printed: a line the filter lets back in is history, and is not printed again.
    private void Queue(ConsoleLineViewModel line)
    {
        if (line.Line.Seq <= printedSeq) return;
        printedSeq = line.Line.Seq;
        queued.Add(line);
    }

    private int PrintWidth => device.OutputIsTerminal ? Math.Max(1, columns) : int.MaxValue / 4;

    private void Print(StringBuilder output, ConsoleLineViewModel line)
    {
        var state = line.SendState;
        var lineRows = layout.Rows(line.Line, state, PrintWidth, ink);
        foreach (var row in lineRows) { VtText.Draw(output, row); output.Append("\r\n"); }
        lastLine = line;
        lastRows = lineRows.Count;
        if (state != ConsoleSendState.None && !Final(state) && watched.Add(line)) line.PropertyChanged += Line_PropertyChanged;
    }

    private static bool Final(ConsoleSendState state) => state is ConsoleSendState.Sent or ConsoleSendState.NotSent;

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ConsoleLineViewModel.SendState) || sender is not ConsoleLineViewModel line) return;
        if (!OnLoop) { Post(() => Line_PropertyChanged(sender, args)); return; }
        if (!stateChanged.Contains(line)) stateChanged.Add(line);
        Schedule();
    }

    // A send's state changed after its line was printed. While nothing is under it the line is drawn again in place; once scrolled past, a sent
    // line gets a short follow-up. Not sent, outcome unknown and not confirmed have the session's own notice line, so they get nothing more here.
    private void Restate(StringBuilder output, ConsoleLineViewModel line)
    {
        if (!watched.Contains(line)) return;
        var state = line.SendState;
        if (Final(state))
        {
            watched.Remove(line);
            line.PropertyChanged -= Line_PropertyChanged;
        }
        if (interactive && ReferenceEquals(line, lastLine) && lastRows + areaHeight <= rows)
        {
            // The area is cleared, so the cursor is on the row under the line.
            output.Append(Esc + "[").Append(lastRows.ToString(CultureInfo.InvariantCulture)).Append("A\r" + Esc + "[J");
            Print(output, line);
            return;
        }
        if (state != ConsoleSendState.Sent) return;
        VtText.Draw(output, layout.FollowUp(line.Line, "sent", PrintWidth, ink));
        output.Append("\r\n");
        lastLine = null;
    }

    // ---- Session ----

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!OnLoop) { Post(() => Session_PropertyChanged(sender, args)); return; }
        switch (args.PropertyName)
        {
            case nameof(ConsoleSessionViewModel.Prompt) or nameof(ConsoleSessionViewModel.PromptName) or nameof(ConsoleSessionViewModel.Hint):
                areaDirty = true;
                Schedule();
                break;
            case nameof(ConsoleSessionViewModel.Theme):
                // New output takes the scheme; what is already in the scrollback keeps the inks it was printed in.
                ReadTheme();
                areaDirty = frameDirty = true;
                Schedule();
                break;
            case nameof(ConsoleSessionViewModel.ActiveApp):
                ShowApp(session?.ActiveApp);
                break;
        }
    }

    private void ReadTheme()
    {
        var styles = device.OutputIsTerminal;
        ink = new VtPalette(session?.Theme, styles, device.Colors);
    }

    private void Typing_Changed(object? sender, EventArgs args)
    {
        if (!OnLoop) { Post(() => Typing_Changed(sender, args)); return; }
        areaDirty = true;
        Schedule();
    }

    private void TypingTick()
    {
        session?.Typing.Tick();
        // The app's title bar carries a clock.
        if (appShown) { frameDirty = true; Schedule(); }
    }

    // Receives only: the console never tells anyone it is typing.
    private async Task WatchTypingAsync(ITypingService service, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in service.WatchAsync(cancellationToken))
            {
                if (!running) break;
                if (OnLoop) session?.Typing.Update(update);
                else Post(() => session?.Typing.Update(update));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { log($"console typing watch ended error={error.GetType().Name}"); }
    }

    // A recalled send or a held look-alike: put in the prompt after the line that asked has been taken and the prompt emptied.
    private void InputSuggested(string suggested) => Post(() => Batch(() =>
    {
        if (appShown) return;
        EndSearch(null);
        SetInput(suggested, suggested.Length);
    }));

    private void Device_Resized() => Post(Resize);

    private void Resize()
    {
        var (nextColumns, nextRows) = (device.Columns, device.Rows);
        if (nextColumns == columns && nextRows == rows) return;
        (columns, rows) = (nextColumns, nextRows);
        if (session is not null && device.OutputIsTerminal) session.Columns = Math.Max(30, columns);
        // A terminal that reflows has moved what was printed; the last line cannot be found again to redraw it. The prompt area is cleared from
        // where it was drawn: a terminal that rewrapped its rows narrower may keep a stray piece of the old prompt above the new one.
        lastLine = null;
        log($"terminal resize columns={columns.ToString(CultureInfo.InvariantCulture)} rows={rows.ToString(CultureInfo.InvariantCulture)}");
        if (appShown) { frameDirty = true; fullFrame = true; }
        else areaDirty = true;
        Schedule();
    }

    // ---- Redirected input: one line at a time, each run to its end before the next ----

    private void StartLines()
    {
        var thread = new Thread(() =>
        {
            using var taken = new SemaphoreSlim(0);
            while (running)
            {
                var line = device.ReadLine();
                if (line is null) { Post(() => End(0)); return; }
                Post(() => _ = SubmitLineAsync(line, taken));
                taken.Wait();
            }
        }) { IsBackground = true, Name = "mlink input" };
        thread.Start();
    }

    private async Task SubmitLineAsync(string line, SemaphoreSlim taken)
    {
        try
        {
            if (session is null || !running) return;
            if (IsExit(line)) { End(0); return; }
            await session.SubmitAsync(line);
        }
        catch (Exception error) { log($"console submit failed error={error.GetType().Name}"); }
        finally { taken.Release(); }
    }

    private bool IsExit(string line)
    {
        var word = line.Trim();
        // Without a slash only while no one is selected, when a plain word is a command and could never be a message.
        return word.Equals("/exit", StringComparison.OrdinalIgnoreCase) || word.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
            session?.Target is null && (word.Equals("exit", StringComparison.OrdinalIgnoreCase) || word.Equals("quit", StringComparison.OrdinalIgnoreCase));
    }
}
