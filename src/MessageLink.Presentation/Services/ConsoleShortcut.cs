namespace MessageLink.Presentation.Services;

/// <summary>
/// Ctrl+`: the chat console. Read from each window's PreviewKeyDown rather than as a KeyboardAccelerator: the accelerator
/// (on the grave-accent key, VK_OEM_3) never fired in normal use — app.log had the console opened from the tray but never
/// by the shortcut, beside working Ctrl+F and Ctrl+Shift+M accelerators — and a key-down that a focused text box
/// (the composer, search) takes as its own never reaches an accelerator at all. The tunnelling preview sees it first.
/// </summary>
/// <remarks>
/// The key is the one left of 1 and above Tab, matched by its scan code (0x29) so the shortcut stays on that physical
/// key on every layout. VK_OEM_3 alone is not enough: other layouts give that code to another key (Ö on German, ' on
/// UK), which would then open the console too. It counts only when no scan code was reported (synthetic input).
/// Shift is allowed (Ctrl+Shift+` is the same key with the Shift a quick hand adds); Alt is not, because Ctrl+Alt is
/// AltGr on many layouts and types a character there.
/// </remarks>
public static class ConsoleShortcut
{
    public const int GraveAccentKey = 0xC0;
    public const uint GraveAccentScanCode = 0x29;

    public static bool Matches(int virtualKey, uint scanCode, bool control, bool alt) =>
        control && !alt && (scanCode == GraveAccentScanCode || (scanCode == 0 && virtualKey == GraveAccentKey));
}
