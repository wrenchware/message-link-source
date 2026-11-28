namespace MessageLink.Terminal.Vt;

/// <summary>
/// The terminal <see cref="TerminalFront"/> draws on and reads from: this process's console (<see cref="SystemTerminal"/>), or a fake the checks
/// drive. Output is VT text; input is keys as <see cref="Console.ReadKey(bool)"/> reports them, or whole lines when input is not a terminal.
/// </summary>
public interface ITerminalDevice
{
    /// <summary>Keys can be read one at a time (standard input is a terminal).</summary>
    bool InputIsTerminal { get; }
    /// <summary>VT can be written (standard output is a terminal).</summary>
    bool OutputIsTerminal { get; }
    /// <summary>Whether color may be written: off when the person set NO_COLOR (https://no-color.org). Bold, underline and reverse stay.</summary>
    bool Colors => Environment.GetEnvironmentVariable("NO_COLOR") is not { Length: > 0 };
    /// <summary>
    /// Whether keys come as the terminal's own bytes, so turning on bracketed paste brings its start and end marks as keys. Not Windows' console:
    /// it hands over key events, with no marks.
    /// </summary>
    bool FramesPastes => false;
    int Columns { get; }
    int Rows { get; }

    /// <summary>Raised, on any thread, when the size may have changed.</summary>
    event Action? Resized;

    /// <summary>Turns on what drawing and reading keys need (VT output, Ctrl+C as a key). <see cref="Restore"/> puts back what was there.</summary>
    void Start();
    /// <summary>Puts the terminal back as <see cref="Start"/> found it. Safe to call more than once.</summary>
    void Restore();

    void Write(string text);

    /// <summary>The next key, waiting for one; null at the end of input.</summary>
    ConsoleKeyInfo? ReadKey();
    /// <summary>Whether a key is waiting, so a paste can be taken as one batch.</summary>
    bool KeyAvailable { get; }
    /// <summary>The next line when input is not a terminal; null at its end.</summary>
    string? ReadLine();

    /// <summary>
    /// Puts text on this machine's own clipboard where the terminal itself cannot (a local Windows console, which ignores OSC 52). False when it
    /// does not apply or did not work.
    /// </summary>
    bool SetLocalClipboard(string text);
}
