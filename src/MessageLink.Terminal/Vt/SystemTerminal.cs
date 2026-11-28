using System.Runtime.InteropServices;
using System.Text;

namespace MessageLink.Terminal.Vt;

/// <summary>
/// This process's own console. On Windows it turns on VT processing for the output and gives the console its modes back afterwards, also when the
/// program ends by an exception or a signal; on Linux .NET restores the terminal's settings itself. Ended by a signal (SIGTERM, SIGHUP, Ctrl+Break)
/// while an app had the alternate screen or the cursor was hidden, it leaves the alternate screen and shows the cursor. The size is polled, and on
/// Linux SIGWINCH says so at once.
/// </summary>
public sealed class SystemTerminal : ITerminalDevice
{
    private const uint EnableProcessedOutput = 0x1, EnableVirtualTerminalProcessing = 0x4;
    private const int StdInput = -10, StdOutput = -11;

    private readonly Stream output = Console.OpenStandardOutput();
    private readonly Lock writeGate = new(), startGate = new();
    private readonly Action<string> log;
    private readonly VtScreenState screen = new();
    private readonly List<IDisposable> signals = [];
    private Timer? poll;
    private (int Columns, int Rows) size;
    private uint? inputMode, outputMode;
    private bool? controlC;
    private bool started, vtRefused;

    public SystemTerminal(Action<string> log) => this.log = log;

    public bool InputIsTerminal => !Console.IsInputRedirected;
    // A console that will not take VT (the legacy console) is written to as if it were a file: plain lines, no prompt drawing.
    public bool OutputIsTerminal => !Console.IsOutputRedirected && !vtRefused;
    // On Linux .NET reads the terminal's bytes and passes the marks through as the keys Esc [ 2 0 0 ~ (seen under a pty, 2026-11-25).
    public bool FramesPastes => !OperatingSystem.IsWindows();
    public int Columns => Measure().Columns;
    public int Rows => Measure().Rows;
    public bool KeyAvailable { get { try { return Console.KeyAvailable; } catch (InvalidOperationException) { return false; } } }

    public event Action? Resized;

    /// <summary>
    /// Whether Ctrl+C is read as a key: only while keys are read one at a time (both ends a terminal). Read a line at a time, Ctrl+C keeps its
    /// default and stops the program, and no "^C" can end up in a line.
    /// </summary>
    public static bool ControlCAsInput(bool inputIsTerminal, bool outputIsTerminal) => inputIsTerminal && outputIsTerminal;

    private static (int Columns, int Rows) Measure()
    {
        try
        {
            var columns = Console.WindowWidth;
            var rows = Console.WindowHeight;
            return (columns > 0 ? columns : 80, rows > 0 ? rows : 24);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PlatformNotSupportedException) { return (80, 24); }
    }

    public void Start()
    {
        lock (startGate)
        {
            if (started) return;
            started = true;
        }
        if (OperatingSystem.IsWindows())
        {
            if (OutputIsTerminal && GetConsoleMode(GetStdHandle(StdOutput), out var mode))
            {
                outputMode = mode;
                if (!SetConsoleMode(GetStdHandle(StdOutput), mode | EnableProcessedOutput | EnableVirtualTerminalProcessing))
                {
                    vtRefused = true;
                    log("terminal vt outcome=refused");
                }
            }
            if (InputIsTerminal && GetConsoleMode(GetStdHandle(StdInput), out var input)) inputMode = input;
        }
        // Asked after the output was: a console that refused VT is read a line at a time.
        if (ControlCAsInput(InputIsTerminal, OutputIsTerminal))
        {
            try { controlC = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; }
            catch (IOException) { controlC = null; }
        }
        AppDomain.CurrentDomain.ProcessExit += ProcessExit;
        Console.CancelKeyPress += CancelKeyPress;
        size = Measure();
        poll = new Timer(_ => Poll(), null, 250, 250);
        if (!OperatingSystem.IsWindows())
        {
            Register(PosixSignal.SIGWINCH, _ => Poll());
            // Not canceled: the signal goes on to end the program as it would have.
            Register(PosixSignal.SIGTERM, _ => Restore(signalled: true));
            Register(PosixSignal.SIGHUP, _ => Restore(signalled: true));
        }
    }

    private void Register(PosixSignal signal, Action<PosixSignalContext> handler)
    {
        try { lock (signals) signals.Add(PosixSignalRegistration.Create(signal, handler)); }
        catch (PlatformNotSupportedException) { }
    }

    private void Poll()
    {
        var now = Measure();
        if (now == size) return;
        size = now;
        Resized?.Invoke();
    }

    private void ProcessExit(object? sender, EventArgs e) => Restore();

    // Ctrl+Break ends the program even while Ctrl+C is read as a key.
    private void CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (e.SpecialKey == ConsoleSpecialKey.ControlBreak) Restore();
    }

    public void Restore() => Restore(signalled: false);

    // From a signal's own handler its registration is left alone: the program is ending.
    private void Restore(bool signalled)
    {
        lock (startGate)
        {
            if (!started) return;
            started = false;
        }
        AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
        Console.CancelKeyPress -= CancelKeyPress;
        poll?.Dispose();
        if (!signalled)
            lock (signals)
            {
                foreach (var signal in signals) signal.Dispose();
                signals.Clear();
            }
        // The front has normally left the alternate screen and shown the cursor already; ended by a signal, it has not.
        string leave;
        lock (writeGate) leave = screen.Leave();
        if (OutputIsTerminal) Write(leave);
        if (controlC is { } before)
        {
            try { Console.TreatControlCAsInput = before; } catch (IOException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            if (outputMode is { } mode) SetConsoleMode(GetStdHandle(StdOutput), mode);
            if (inputMode is { } input) SetConsoleMode(GetStdHandle(StdInput), input);
        }
    }

    public void Write(string text)
    {
        if (text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (writeGate)
        {
            screen.Observe(text);
            try { output.Write(bytes); output.Flush(); }
            // The terminal went away (a closed SSH session): there is no one left to draw for.
            catch (IOException) { }
        }
    }

    public ConsoleKeyInfo? ReadKey()
    {
        try { return Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { return null; }
        catch (IOException) { return null; }
    }

    public string? ReadLine()
    {
        try { return Console.In.ReadLine(); }
        catch (IOException) { return null; }
    }

    public bool SetLocalClipboard(string text)
    {
        // Only a console on this PC's own desktop: over SSH the clipboard here is not the one the person is looking at, and OSC 52 reaches theirs.
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("SSH_TTY") is { Length: > 0 }) return false;
        return WindowsClipboard.Set(text);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
}

/// <summary>
/// Whether the alternate screen is on, the cursor hidden and bracketed paste on, as read from what was written, and the sequence that undoes them:
/// what a program ended by a signal must still write so the shell it goes back to is usable.
/// </summary>
public sealed class VtScreenState
{
    private const string AlternateOn = "\u001b[?1049h", AlternateOff = "\u001b[?1049l", CursorOff = "\u001b[?25l", CursorOn = "\u001b[?25h";
    private const string BracketedOn = "\u001b[?2004h", BracketedOff = "\u001b[?2004l";

    public bool Alternate { get; private set; }
    public bool CursorHidden { get; private set; }
    public bool Bracketed { get; private set; }

    public void Observe(string text)
    {
        if (!text.Contains("\u001b[?", StringComparison.Ordinal)) return;
        Alternate = Later(text, AlternateOn, AlternateOff, Alternate);
        CursorHidden = Later(text, CursorOff, CursorOn, CursorHidden);
        Bracketed = Later(text, BracketedOn, BracketedOff, Bracketed);
    }

    // The later of the two sequences wins; with neither it stays as it was.
    private static bool Later(string text, string on, string off, bool was)
    {
        var at = text.LastIndexOf(on, StringComparison.Ordinal);
        var until = text.LastIndexOf(off, StringComparison.Ordinal);
        return at < 0 && until < 0 ? was : at > until;
    }

    /// <summary>What undoes the alternate screen, the hidden cursor and bracketed paste; "" when none is on. Asking for it counts as having written it.</summary>
    public string Leave()
    {
        if (!Alternate && !CursorHidden && !Bracketed) return "";
        var text = (Alternate ? AlternateOff : "") + VtPalette.Reset + (CursorHidden ? CursorOn : "") + (Bracketed ? BracketedOff : "");
        Alternate = CursorHidden = Bracketed = false;
        return text;
    }
}

/// <summary>The Windows clipboard as plain Unicode text, for a console that ignores OSC 52.</summary>
internal static class WindowsClipboard
{
    private const uint UnicodeText = 13, Moveable = 0x0002;

    public static bool Set(string text)
    {
        // Another program may hold the clipboard for a moment.
        var opened = false;
        for (var attempt = 0; attempt < 5 && !(opened = OpenClipboard(0)); attempt++) Thread.Sleep(10);
        if (!opened) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(Moveable, (nuint)bytes);
            if (memory == 0) return false;
            var target = GlobalLock(memory);
            if (target == 0) { GlobalFree(memory); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(UnicodeText, memory) != 0) return true;
            GlobalFree(memory);
            return false;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);
}
