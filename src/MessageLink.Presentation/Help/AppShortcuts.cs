namespace MessageLink.Presentation.Help;

/// <summary>
/// The keyboard shortcuts the Ctrl+/ sheet lists. Kept here, outside the window, so the guide's checks can hold the guide
/// to them: every shortcut on the sheet must be in the guide too.
/// </summary>
public static class AppShortcuts
{
    public static readonly IReadOnlyList<(string Keys, string Action)> Main =
    [
        ("F1", "Help guide"),
        ("Ctrl+N", "New chat"),
        ("Ctrl+Shift+N", "Open this conversation in its own window"),
        ("Ctrl+W", "Close a conversation window"),
        ("Ctrl+E", "Search"),
        ("Ctrl+U", "Show only unread conversations, or all again"),
        ("Ctrl+K", "Commands"),
        ("Ctrl+`", "Chat console"),
        ("Alt+Up / Alt+Down", "Previous or next chat"),
        ("Alt+Left / Alt+Right", "Back or forward through opened conversations"),
        ("Alt+Shift+Up / Alt+Shift+Down", "Previous or next unread conversation"),
        ("Ctrl+Tab / Ctrl+Shift+Tab", "Switch between recent conversations"),
        ("Ctrl+F", "Search in this conversation"),
        ("Ctrl+Shift+M", "Mute or unmute this conversation"),
        ("Ctrl+Shift+P", "Pin or unpin this conversation"),
        ("Ctrl+1 … Ctrl+9", "Pinned chats, in list order"),
        ("Ctrl+Shift+I", "Show or hide service badges"),
        ("Ctrl+,", "Settings"),
        ("Ctrl+/", "Keyboard shortcuts"),
        ("Enter / Shift+Enter", "Send / new line"),
        ("Up", "Edit your last message, when the message box is empty"),
        ("Alt+Down", "Change service, while writing in a linked conversation"),
        ("Ctrl+V", "Paste a photo"),
        ("Ctrl+Shift+R", "Record a voice message, or stop recording"),
        ("Win+.", "Windows emoji panel"),
        ("Ctrl+Shift+Enter", "Send later"),
        ("Esc", "Cancel a reply, an edit or a recording"),
    ];

    /// <summary>The photo viewer is its own window with its own keys.</summary>
    public static readonly IReadOnlyList<(string Keys, string Action)> Photo =
    [
        ("Left / Right", "Previous or next photo"),
        ("F11", "Full screen"),
        ("Esc", "Close the photo"),
    ];

    /// <summary>Things done with the mouse that nothing on screen points to, listed under the shortcuts.</summary>
    public static readonly IReadOnlyList<(string Gesture, string Action)> Tips =
    [
        ("Drag a pinned chat", "Reorder your pins"),
        ("Drop a photo or file on a conversation", "Attach it"),
        ("Right-click a conversation", "Pin, mute, remind me later, archive"),
    ];
}
