namespace MessageLink.App.Services;

// Starts a run's log, keeping the run before it beside the new one as "<log>.previous". Replacing each log outright at
// start-up meant a restart erased the only account of what had just happened: twice on 2026-09-23, once for the Google
// disconnect and once for a burst of messages. One previous run only, so the space stays bounded by the caps.
internal static class LogRotation
{
    public static string PreviousPath(string path) => path + ".previous";

    public static void StartFresh(string path, string opening = "app started")
    {
        // Keeping the old run is worth having, never worth failing over: a locked or missing file just starts afresh.
        try { if (File.Exists(path)) File.Move(path, PreviousPath(path), true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        File.WriteAllText(path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {opening}\n");
    }
}
