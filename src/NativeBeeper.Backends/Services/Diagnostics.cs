namespace NativeBeeper.App.Services;

// Whether this app records anything about itself: the five sanitized logs and the timer that samples the interface
// thread. A plain flag rather than a call into preferences, because the logs are shared with the offline harness,
// which has no preferences, no profile and no files to read them from. The app sets it at startup and whenever the
// setting changes; everything else only reads it.
internal static class Diagnostics
{
    public static volatile bool Recording = true;

    // The file each provider's log writes in the provider's own folder. The window keeps app.log; MessagesServer.exe sets its own name before its first
    // line (SWITCHBOARD.md 3.1), so a server run never rotates or writes the logs of the window. The Google Messages history is named after it.
    public const string WindowProviderLog = "app.log";
    public static string ProviderLogFile { get; set; } = WindowProviderLog;
    public static string SignInHistoryFile => ProviderLogFile == WindowProviderLog ? "sign-in-history.log" : "sign-in-history." + ProviderLogFile;
}
