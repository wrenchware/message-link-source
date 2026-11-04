using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// What the person did on the server's page, as the DevTools commands that do it in the server's browser (SWITCHBOARD.md 5.4). The person may be typing a Google password, so
/// this is the one place their input is read, and it is read to be handed on and forgotten: nothing here is logged, stored or put in an exception, a bad event is dropped without
/// saying which, and nothing outside this table reaches the browser. A pointer is kept inside the page, a wheel turn and a click count are bounded, a key is one of a short list
/// of control keys (characters arrive as text, so a layout or an input method never matters), and a text event is capped.
/// </summary>
internal static class SignInInput
{
    /// <summary>A DevTools command: the method and its parameters.</summary>
    public readonly record struct Command(string Method, object Parameters)
    {
        public override string ToString() => nameof(Command);
    }

    // DOM key name -> (windowsVirtualKeyCode, DOM code). Only these keys are sent as keys.
    private static readonly Dictionary<string, (int Vk, string Code, bool Text)> ControlKeys = new(StringComparer.Ordinal)
    {
        ["Enter"] = (13, "Enter", true),
        ["Tab"] = (9, "Tab", false),
        ["Backspace"] = (8, "Backspace", false),
        ["Delete"] = (46, "Delete", false),
        ["Escape"] = (27, "Escape", false),
        ["ArrowLeft"] = (37, "ArrowLeft", false),
        ["ArrowUp"] = (38, "ArrowUp", false),
        ["ArrowRight"] = (39, "ArrowRight", false),
        ["ArrowDown"] = (40, "ArrowDown", false),
        ["Home"] = (36, "Home", false),
        ["End"] = (35, "End", false),
        ["PageUp"] = (33, "PageUp", false),
        ["PageDown"] = (34, "PageDown", false),
    };

    private const int Alt = 1, Ctrl = 2, Meta = 4, Shift = 8;

    /// <param name="viewWidth">The page's width in its own pixels, from the newest picture (the default before there is one).</param>
    /// <param name="viewHeight">The same for the height.</param>
    /// <returns>The command, or null for an event that is not one of the above or is out of range.</returns>
    public static Command? Translate(RemoteSignInInput input, int viewWidth, int viewHeight)
    {
        switch (input.Kind)
        {
            case RemoteSignIn.Mouse: return Mouse(input, viewWidth, viewHeight);
            case RemoteSignIn.Key: return KeyEvent(input);
            case RemoteSignIn.Text:
                // One call inserts the whole run, so an input method's text and a paste arrive as they were made. Control characters (a pasted tab or line break among them)
                // are not text for a one-line field; they are dropped, never turned into a key.
                if (input.Text is not { Length: > 0 and <= RemoteSignIn.MaxTextCharacters } text) return null;
                var clean = new string(text.Where(character => character >= ' ' && character != '\u007f' && !(character is >= '\u0080' and <= '\u009f')).ToArray());
                return clean.Length == 0 ? null : new Command("Input.insertText", new { text = clean });
            default: return null;
        }
    }

    private static Command? Mouse(RemoteSignInInput input, int viewWidth, int viewHeight)
    {
        var type = input.Type switch
        {
            "mouseMoved" or "mousePressed" or "mouseReleased" or "mouseWheel" => input.Type,
            _ => null,
        };
        if (type is null || !double.IsFinite(input.X) || !double.IsFinite(input.Y) || !double.IsFinite(input.DeltaX) || !double.IsFinite(input.DeltaY)) return null;
        var button = input.Button switch { "left" or "middle" or "right" => input.Button, _ => "none" };
        // A press or release names a button; a move or a wheel turn names none.
        if (type is "mousePressed" or "mouseReleased" && button == "none") return null;
        var x = Math.Clamp(input.X, 0, Math.Max(0, viewWidth - 1));
        var y = Math.Clamp(input.Y, 0, Math.Max(0, viewHeight - 1));
        var parameters = new Dictionary<string, object>
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["modifiers"] = input.Modifiers & (Alt | Ctrl | Meta | Shift),
            ["button"] = button,
            ["buttons"] = input.Buttons & 7,
            ["clickCount"] = Math.Clamp(input.ClickCount, 0, 3),
        };
        if (type == "mouseWheel")
        {
            parameters["deltaX"] = Math.Clamp(input.DeltaX, -2000, 2000);
            parameters["deltaY"] = Math.Clamp(input.DeltaY, -2000, 2000);
        }
        return new Command("Input.dispatchMouseEvent", parameters);
    }

    private static Command? KeyEvent(RemoteSignInInput input)
    {
        var type = input.Type is "keyDown" or "keyUp" ? input.Type : null;
        if (type is null || input.Key is not { Length: > 0 } key) return null;
        var modifiers = input.Modifiers & (Alt | Ctrl | Meta | Shift);
        // Control keys, with Shift where the person held it (Shift+Tab). Ctrl+A is the one chord: select all, as the browser's own command, so a field can be replaced.
        if (ControlKeys.TryGetValue(key, out var control) && (modifiers & (Ctrl | Alt | Meta)) == 0)
        {
            var parameters = new Dictionary<string, object>
            {
                ["type"] = type == "keyUp" ? "keyUp" : control.Text ? "keyDown" : "rawKeyDown",
                ["modifiers"] = modifiers,
                ["key"] = key,
                ["code"] = control.Code,
                ["windowsVirtualKeyCode"] = control.Vk,
                ["nativeVirtualKeyCode"] = control.Vk,
            };
            if (type == "keyDown" && control.Text) parameters["text"] = "\r";
            return new Command("Input.dispatchKeyEvent", parameters);
        }
        if (key == "a" && modifiers == Ctrl)
        {
            var parameters = new Dictionary<string, object>
            {
                ["type"] = type == "keyUp" ? "keyUp" : "rawKeyDown",
                ["modifiers"] = modifiers,
                ["key"] = "a",
                ["code"] = "KeyA",
                ["windowsVirtualKeyCode"] = 65,
                ["nativeVirtualKeyCode"] = 65,
            };
            if (type == "keyDown") parameters["commands"] = new[] { "selectAll" };
            return new Command("Input.dispatchKeyEvent", parameters);
        }
        return null;
    }

    /// <summary>The picture size to ask for, inside the limits (0 or less means the default).</summary>
    public static (int Width, int Height) Size(int width, int height) => (
        width <= 0 ? RemoteSignIn.DefaultWidth : Math.Clamp(width, RemoteSignIn.MinWidth, RemoteSignIn.MaxWidth),
        height <= 0 ? RemoteSignIn.DefaultHeight : Math.Clamp(height, RemoteSignIn.MinHeight, RemoteSignIn.MaxHeight));
}
