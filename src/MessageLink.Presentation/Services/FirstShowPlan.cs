namespace MessageLink.Presentation.Services;

// How the shell brings the main window forward (MainWindow.OpenFromShellAsync).
//   ShowAfterReveal: still drawing for its launch; the launch reveal shows it once it has drawn.
//   ShowNow: it has been on screen before, so showing it again costs nothing unfinished.
//   PrepareThenShow: started in the background and never shown. Its first show is the one Windows plays the open
//   animation for, so the target is opened while hidden and the window shown only after it has drawn.
public enum FirstShowStep { ShowNow, ShowAfterReveal, PrepareThenShow }

public static class FirstShowPlan
{
    public static FirstShowStep For(bool everShown, bool heldForLaunch) =>
        heldForLaunch ? FirstShowStep.ShowAfterReveal : everShown ? FirstShowStep.ShowNow : FirstShowStep.PrepareThenShow;

    // Whether the first show of a background window opens the conversation an ordinary launch would. Only a request
    // with no target does, and never a notification click: one left with nothing to open (expired, another connection's,
    // a test) asked only for the window, and opening the top row would have the show read a conversation nobody picked.
    // Nor the console: all it opens is what the user named, and a search hit it opens follows the show.
    public static bool OpensFirstConversation(string origin, bool hasTarget) => !hasTarget && origin is not ("notification" or "console");
}
