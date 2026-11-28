namespace MessageLink.Presentation.Services;

// "Is the app free to leave?" One snapshot of what the app is doing and one rule for every way of leaving it: the tray's Quit, closing the
// window, a restart the person asked for (sample mode, a theme, a server change), a tool's quit (--quit, tools\Stop-Messages.ps1), and a
// Store update's restart, from its schedule or Settings' Restart to update. The Windows app reads the facts (MainWindow.Leaving.cs); each
// caller shows the answer its own way.

/// <summary>What the app is doing right now, as far as leaving it is concerned. Read on the interface thread.</summary>
/// <param name="WindowShown">The main window, or a conversation window, is on screen.</param>
/// <param name="HiddenFor">How long the window has been in the tray; zero while it is shown.</param>
/// <param name="Busy">A chat being started, a dialog open, a photo or file on its way, or a conversation window that can't close yet.</param>
/// <param name="HandlingNotification">A notification's button is still being carried out.</param>
/// <param name="PhotoOpen">The photo viewer is open.</param>
/// <param name="SigningIn">A sign-in or connect is under way.</param>
/// <param name="Sending">A message the outbox has not settled.</param>
/// <param name="Holding">A message in its Undo send wait.</param>
/// <param name="Changing">An edit, delete or reaction still on its way to the service.</param>
/// <param name="UnsavedDrafts">Words, photos or an edit that would be lost.</param>
/// <param name="ShuttingDown">This run is already ending.</param>
public sealed record LeaveFacts(
    bool WindowShown,
    TimeSpan HiddenFor,
    bool Busy,
    bool HandlingNotification,
    bool PhotoOpen,
    bool SigningIn,
    bool Sending,
    bool Holding,
    bool Changing,
    bool UnsavedDrafts,
    bool ShuttingDown)
{
    /// <summary>Nothing going on, in the tray for <paramref name="hiddenFor"/>.</summary>
    public static LeaveFacts Quiet(TimeSpan hiddenFor) => new(false, hiddenFor, false, false, false, false, false, false, false, false, false);
}

/// <summary>A way of leaving.</summary>
/// <param name="Name">A log word.</param>
/// <param name="PersonAnswers">A person asked and is there to answer a question; otherwise whatever is unfinished keeps the app open.</param>
/// <param name="ReturnsHeldSends">A message in its Undo send wait goes back to its draft first (as Undo would) instead of keeping the app open.
/// Only a person's own leaving does this: they are there to see the draft, and it is saved with the others (owner, 2026-11-21).</param>
/// <param name="IsUpdate">A Store update's restart. The photo viewer keeps it waiting: a person is looking at it.</param>
/// <param name="WaitsForTray">The update's own schedule, which also waits for the window to have been in the tray a while, so a person who
/// just hid it is not surprised by it coming back in a new version.</param>
public sealed record LeaveWay(string Name, bool PersonAnswers, bool ReturnsHeldSends, bool IsUpdate, bool WaitsForTray)
{
    /// <summary>The tray's Quit, closing the window, or a restart the person asked for.</summary>
    public static readonly LeaveWay Quit = new("quit", PersonAnswers: true, ReturnsHeldSends: true, IsUpdate: false, WaitsForTray: false);
    /// <summary>A tool's quit: nothing is shown, so nothing can be asked.</summary>
    public static readonly LeaveWay ToolQuit = new("tool", PersonAnswers: false, ReturnsHeldSends: false, IsUpdate: false, WaitsForTray: false);
    /// <summary>Settings' Restart to update. Its window is open by definition; anything unfinished is explained rather than asked.</summary>
    public static readonly LeaveWay RestartToUpdate = new("update_person", PersonAnswers: false, ReturnsHeldSends: false, IsUpdate: true, WaitsForTray: false);
    /// <summary>The update schedule's own install.</summary>
    public static readonly LeaveWay Update = new("update", PersonAnswers: false, ReturnsHeldSends: false, IsUpdate: true, WaitsForTray: true);
}

public static class LeavePolicy
{
    /// <summary>Words a person is asked about (<see cref="LeaveWay.PersonAnswers"/>) rather than refused for: a yes lets the leaving go on.</summary>
    public const string Changing = "changing", Unsaved = "unsaved";

    /// <summary>
    /// What keeps the app from leaving this way now, as a log word, or null when nothing does. For a person, a word in <paramref name="answered"/>
    /// was asked about and accepted, so it is passed over. Nothing here is ever overridden: a mandatory update waits for the same moment.
    /// </summary>
    public static string? Blocker(LeaveFacts now, LeaveWay way, TimeSpan hiddenLongEnough = default, IReadOnlyCollection<string>? answered = null)
    {
        if (now.ShuttingDown) return "shutting_down";
        var held = now.Holding && !way.ReturnsHeldSends;
        if (way.PersonAnswers)
        {
            if (now.Busy) return "busy";
            if (now.HandlingNotification) return "notification";
            if (now.Changing && answered?.Contains(Changing) != true) return Changing;
            // The drafts question also covers a send that has not settled: it says such a message may still arrive.
            if ((now.UnsavedDrafts || now.Sending || held) && answered?.Contains(Unsaved) != true) return Unsaved;
            // A sign-in and the photo viewer are the person's to leave.
            return null;
        }
        if (now.Sending || held) return "sending";
        if (now.Changing) return Changing;
        if (now.SigningIn) return "signing_in";
        if (now.Busy || now.HandlingNotification) return "busy";
        if (way.IsUpdate && now.PhotoOpen) return "photo";
        if (now.UnsavedDrafts) return Unsaved;
        if (!way.WaitsForTray) return null;
        if (now.WindowShown) return "window_shown";
        if (now.HiddenFor < hiddenLongEnough) return "hidden_recently";
        return null;
    }

    /// <summary>Whether a person is asked about <paramref name="reason"/> rather than refused.</summary>
    public static bool Asks(string reason) => reason is Changing or Unsaved;
}
