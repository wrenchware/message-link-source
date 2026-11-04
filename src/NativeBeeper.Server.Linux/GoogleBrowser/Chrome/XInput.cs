using System.Text;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

internal enum XActionKind { Move, Button, Key, Text }

/// <summary>
/// One thing to do on the X display. <see cref="Text"/> may be a password: the type prints only its name, and nothing outside <see cref="XInputRunner"/> reads it.
/// </summary>
internal readonly record struct XAction(XActionKind Kind, int A = 0, int B = 0, string? Text = null)
{
    public override string ToString() => nameof(XAction);
}

/// <summary>
/// What the person did on the server's page, as real input on the server's display (SWITCHBOARD.md 5.4, Chrome on the server). The person may be typing a Google password, so this is the one
/// place their input is read, and it is read to be handed on and forgotten: nothing here is logged, stored or put in an exception, a bad event is dropped without saying which, and nothing
/// outside this table reaches the display. A pointer is held inside the display, a wheel turn and a text event are bounded, a key is one of a short list of control keys (characters arrive as
/// text, so a layout or an input method never matters), and Ctrl+A is the one chord.
/// <para>
/// The picture the client shows is the whole display, so a pointer position is already a pixel of it (<c>ViewWidth</c> by <c>ViewHeight</c> of the frame is the display's size): no browser viewport,
/// toolbar or device scale lies between what the person sees and where the click lands. A press and a release first move the pointer to their own position, so a click lands where it was made
/// even if the moves before it were dropped or coalesced. A click count is not sent: X has none, and the browser counts double and triple clicks from the timing of the presses, as it does for
/// a real mouse.
/// </para>
/// </summary>
internal static class XInputTranslator
{
    // The keysyms of the control keys (X11/keysymdef.h).
    private const int Return = 0xff0d, Tab = 0xff09, BackSpace = 0xff08, Delete = 0xffff, Escape = 0xff1b, Left = 0xff51, Up = 0xff52, Right = 0xff53, Down = 0xff54,
        Home = 0xff50, End = 0xff57, PageUp = 0xff55, PageDown = 0xff56, ShiftL = 0xffe1, ControlL = 0xffe3, LowerA = 0x61;

    /// <summary>The keysym the shift key makes (used to type a shifted character).</summary>
    public const int ShiftKeysym = ShiftL;

    private static readonly Dictionary<string, int> ControlKeys = new(StringComparer.Ordinal)
    {
        ["Enter"] = Return, ["Tab"] = Tab, ["Backspace"] = BackSpace, ["Delete"] = Delete, ["Escape"] = Escape,
        ["ArrowLeft"] = Left, ["ArrowUp"] = Up, ["ArrowRight"] = Right, ["ArrowDown"] = Down,
        ["Home"] = Home, ["End"] = End, ["PageUp"] = PageUp, ["PageDown"] = PageDown,
    };

    private const int Ctrl = 2, Shift = 8;
    private const int MaxWheelClicks = 10;

    /// <summary>The actions for one event on a display <paramref name="width"/> by <paramref name="height"/>; empty for an event that is not one of the above or is out of range.</summary>
    public static IReadOnlyList<XAction> Translate(RemoteSignInInput input, int width, int height)
    {
        switch (input.Kind)
        {
            case RemoteSignIn.Mouse: return Mouse(input, width, height);
            case RemoteSignIn.Key: return KeyEvent(input);
            case RemoteSignIn.Text:
                if (input.Text is not { Length: > 0 and <= RemoteSignIn.MaxTextCharacters } text) return [];
                // Control characters (a pasted tab or line break among them) are not text for a one-line field; they are dropped, never turned into a key.
                var clean = new string(text.Where(character => character >= ' ' && character != '\u007f' && !(character is >= '\u0080' and <= '\u009f')).ToArray());
                return clean.Length == 0 ? [] : [new XAction(XActionKind.Text, Text: clean)];
            default: return [];
        }
    }

    /// <summary>The display pixel a pointer position falls in.</summary>
    public static (int X, int Y) Pixel(double x, double y, int width, int height) => (ToPixel(x, width), ToPixel(y, height));

    // Positions are the left or top edge of a pixel, so 0.9 and 0.0 are pixel 0, and the far edge (size) is the last pixel.
    private static int ToPixel(double position, int size) => size <= 0 ? 0 : (int)Math.Clamp(Math.Floor(position), 0, size - 1);

    private static IReadOnlyList<XAction> Mouse(RemoteSignInInput input, int width, int height)
    {
        if (input.Type is not ("mouseMoved" or "mousePressed" or "mouseReleased" or "mouseWheel")) return [];
        if (!double.IsFinite(input.X) || !double.IsFinite(input.Y) || !double.IsFinite(input.DeltaX) || !double.IsFinite(input.DeltaY)) return [];
        var (x, y) = Pixel(input.X, input.Y, width, height);
        var move = new XAction(XActionKind.Move, x, y);
        switch (input.Type)
        {
            case "mouseMoved": return [move];
            case "mousePressed" or "mouseReleased":
                var button = input.Button switch { "left" => 1, "middle" => 2, "right" => 3, _ => 0 };
                return button == 0 ? [] : [move, new XAction(XActionKind.Button, button, input.Type == "mousePressed" ? 1 : 0)];
            default:
                // The client sends a turn down as a positive deltaY (the page scrolls down for it), as a browser's own wheel event does. One notch is 120.
                var actions = new List<XAction> { move };
                AddWheel(actions, input.DeltaY, up: 4, down: 5);
                AddWheel(actions, input.DeltaX, up: 6, down: 7);
                return actions.Count == 1 ? [] : actions;
        }
    }

    private static void AddWheel(List<XAction> actions, double delta, int up, int down)
    {
        if (delta == 0) return;
        var clicks = (int)Math.Clamp(Math.Ceiling(Math.Abs(delta) / 120), 1, MaxWheelClicks);
        var button = delta < 0 ? up : down;
        for (var i = 0; i < clicks; i++)
        {
            actions.Add(new XAction(XActionKind.Button, button, 1));
            actions.Add(new XAction(XActionKind.Button, button, 0));
        }
    }

    private static IReadOnlyList<XAction> KeyEvent(RemoteSignInInput input)
    {
        if (input.Type is not ("keyDown" or "keyUp") || input.Key is not { Length: > 0 } key) return [];
        var down = input.Type == "keyDown";
        var modifiers = input.Modifiers & 15;
        int keysym, modifier = 0;
        if (ControlKeys.TryGetValue(key, out keysym) && (modifiers & (1 | 4 | Ctrl)) == 0)
        {
            if ((modifiers & Shift) != 0) modifier = ShiftL;
        }
        else if (key == "a" && modifiers == Ctrl) { keysym = LowerA; modifier = ControlL; }
        else return [];
        // The modifier goes down before the key and up after it.
        var key_ = new XAction(XActionKind.Key, keysym, down ? 1 : 0);
        if (modifier == 0) return [key_];
        var modifier_ = new XAction(XActionKind.Key, modifier, down ? 1 : 0);
        return down ? [modifier_, key_] : [key_, modifier_];
    }

    /// <summary>The keysym for a character: Latin-1 keysyms are the code points; everything else is the Unicode keysym range.</summary>
    public static long KeysymFor(int codePoint) => codePoint is >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff ? codePoint : 0x01000000L + codePoint;
}

/// <summary>
/// Carries <see cref="XAction"/>s out on a display. Typed text is pressed key by key: a character with a key of its own is that key (with Shift where it is the shifted symbol); a character with
/// none (an accent, an emoji) borrows a spare keycode, as xdotool does, which is given the character's keysym, pressed, and emptied again. Nothing typed ever reaches a command line, a log or
/// an exception: it goes from the event to the X server and nowhere else.
/// </summary>
internal sealed class XInputRunner(IXDisplay display)
{
    /// <summary>The pause after a key's press and release, and after a borrowed keycode is given its symbol (the browser must hear of the new mapping before the key).</summary>
    public TimeSpan KeyGap { get; init; } = TimeSpan.FromMilliseconds(4);
    public TimeSpan RemapGap { get; init; } = TimeSpan.FromMilliseconds(25);

    // What this runner has done to the display that it must undo before the connection goes: a borrowed keycode that still holds a character, and keys and buttons it pressed and did not release.
    private int borrowed;
    private readonly HashSet<int> keysDown = [], buttonsDown = [];

    /// <summary>
    /// Undoes what the runner left on the display (a borrowed keycode still holding a character, a key or button still down), so that a connection that is closed leaves the keyboard as it found it. Best
    /// effort: a display that is already gone has nothing to restore. Call it with no input running (the sign-in holds its input lock).
    /// </summary>
    public void Release()
    {
        try
        {
            foreach (var key in keysDown) display.Key(key, false);
            foreach (var button in buttonsDown) display.Button(button, false);
            keysDown.Clear(); buttonsDown.Clear();
            if (borrowed != 0) { var keycode = borrowed; borrowed = 0; display.SetKeysym(keycode, 0); }
            display.Sync();
        }
        catch (IOException) { }
    }

    public async Task RunAsync(IReadOnlyList<XAction> actions, CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (action.Kind)
            {
                case XActionKind.Move: display.MotionTo(action.A, action.B); break;
                case XActionKind.Button:
                    display.Button(action.A, action.B != 0);
                    if (action.B != 0) buttonsDown.Add(action.A); else buttonsDown.Remove(action.A);
                    break;
                case XActionKind.Key:
                    if (display.Keyboard.Find(action.A, 0) is > 0 and var keycode)
                    {
                        display.Key(keycode, action.B != 0);
                        if (action.B != 0) keysDown.Add(keycode); else keysDown.Remove(keycode);
                    }
                    break;
                case XActionKind.Text:
                    if (action.Text is { } text) await TypeAsync(text, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        display.Sync();
    }

    private async Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keysym = XInputTranslator.KeysymFor(rune.Value);
            var keyboard = display.Keyboard;
            if (keyboard.Find(keysym, 0) is > 0 and var plain) await TapKeyAsync(plain, 0, cancellationToken).ConfigureAwait(false);
            else if (keyboard.Find(keysym, 1) is > 0 and var shifted && keyboard.Find(XInputTranslator.ShiftKeysym, 0) is > 0 and var shift) await TapKeyAsync(shifted, shift, cancellationToken).ConfigureAwait(false);
            else if (keyboard.Spare() is > 0 and var spare)
            {
                borrowed = spare;
                display.SetKeysym(spare, keysym);
                display.Sync();
                await Task.Delay(RemapGap, cancellationToken).ConfigureAwait(false);
                try { await TapKeyAsync(spare, 0, cancellationToken).ConfigureAwait(false); }
                finally
                {
                    // If this fails (the display went), the mapping stays marked as borrowed and Release() tries again.
                    display.SetKeysym(spare, 0);
                    borrowed = 0;
                    display.Sync();
                }
            }
        }
    }

    private async Task TapKeyAsync(int keycode, int modifierKeycode, CancellationToken cancellationToken)
    {
        if (modifierKeycode != 0) { display.Key(modifierKeycode, true); keysDown.Add(modifierKeycode); }
        display.Key(keycode, true);
        keysDown.Add(keycode);
        display.Sync();
        await Task.Delay(KeyGap, cancellationToken).ConfigureAwait(false);
        display.Key(keycode, false);
        keysDown.Remove(keycode);
        if (modifierKeycode != 0) { display.Key(modifierKeycode, false); keysDown.Remove(modifierKeycode); }
        display.Sync();
        await Task.Delay(KeyGap, cancellationToken).ConfigureAwait(false);
    }
}
