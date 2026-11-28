namespace MessageLink.App.Services;

// A run leaves this file in its data folder until its shutdown has finished, so finding it at launch means the last run
// never got there. "running" is a crash, a fail-fast, Task Manager or the forced stop before an install: no shutdown ran
// to the end. "session" is Windows ending the session (a restart, a sign-out, the installer's close) after Messages
// had agreed to it and while its bounded close was still under way, so that close did not finish in time.
//
// Beside it, "<marker>.version" names the build that wrote it. The marker's own text stays exactly "running" or
// "session" (tools\Probe-SessionEnd.ps1 reads it), so the build has a file of its own.
//
// No WinUI types, so the contract harness links this file. Every call is guarded: a marker that cannot be written
// leaves the next launch saying "unknown" or "clean", never stops this one.
internal static class RunMarker
{
    private const string Running = "running", Session = "session";

    // This build, as the version file records it.
    public static string CurrentVersion { get; } = typeof(RunMarker).Assembly.GetName().Version?.ToString() ?? "unknown";

    // The build the previous run was, from its version file; null when there was none (a first run, a clean exit, or a
    // build from before the file). Set by Begin.
    public static string? PreviousVersion { get; private set; }

    public static string VersionPath(string path) => path + ".version";

    // What the previous run left behind (clean | unclean | session_unfinished | unknown), then marks this one running.
    public static string Begin(string path) => Begin(path, CurrentVersion);

    public static string Begin(string path, string version)
    {
        try
        {
            var previous = Classify(File.Exists(path) ? File.ReadAllText(path) : null);
            PreviousVersion = ReadVersion(path);
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            File.WriteAllText(path, Running + "\n");
            try { File.WriteAllText(VersionPath(path), version + "\n"); } catch (Exception) { }
            return previous;
        }
        catch (Exception) { return "unknown"; }
    }

    // Windows asked to end the session and Messages agreed.
    public static void MarkSessionEnding(string path) => Write(path, Session);

    // Another application refused the session end, so this run carries on as an ordinary one.
    public static void MarkRunning(string path) => Write(path, Running);

    public static void End(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
        try { File.Delete(VersionPath(path)); } catch (Exception) { }
    }

    public static string Classify(string? text) => text?.Trim() switch
    {
        null => "clean",
        Running => "unclean",
        Session => "session_unfinished",
        _ => "unclean",
    };

    // Whether this launch says "Messages closed unexpectedly last time" (MainWindow.FirstRun): show | upgraded | none.
    // Only an unclean exit of this same build. Windows ending the session leaves "session" (session_unfinished) and a
    // finished close leaves nothing, so a restart, a sign-out, Quit and the installer's Restart Manager close never say
    // it. A different build, or none recorded, means an install came between: the forced stop before one (Release.ps1,
    // or tools\Stop-MessageLink.ps1 before Setup) is not a crash the person needs telling about.
    public static string CrashNotice(string previousExit, string? previousVersion, string version) =>
        previousExit != "unclean" ? "none" : string.Equals(previousVersion, version, StringComparison.Ordinal) ? "show" : "upgraded";

    private static string? ReadVersion(string path)
    {
        try
        {
            var file = VersionPath(path);
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 and <= 64 } text ? text : null;
        }
        catch (Exception) { return null; }
    }

    private static void Write(string path, string state)
    {
        try { File.WriteAllText(path, state + "\n"); } catch (Exception) { }
    }
}
