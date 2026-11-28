namespace MessageLink.Presentation.Services;

// How long something is allowed to take before the interface admits it is taking time.
//
// Most of the work these indicators stand for finishes in well under a second: a conversation row reads its summary
// back after a receipt, an open conversation waits for a provider that is already nearly connected. A swirl that
// appears and vanishes inside that time does not read as progress — it reads as a flicker, or as something wrong
// with the row it appeared on, and it is the only part of the app that moves while nothing is happening.
//
// So progress waits. Work that finishes first is never announced at all, and work that is genuinely slow still says
// so, a little later, which is the only case where saying it was ever useful. Two seconds because that is roughly
// where waiting stops feeling instant and starts feeling like waiting — and because the alternative, tuning it per
// indicator, would give the same app several different ideas about how long is too long.
//
// Kept here rather than as a number in each view model so both of them mean the same thing by it, and so a check can
// read it.
public static class ProgressIndicator
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    // For the first seconds after launch every provider is restoring at once, and a recovery or reconnection notice
    // there announces the start-up itself rather than anything slow about one conversation. The user asked for none
    // within five seconds of starting. A recovery that begins inside it never shows its pill; the offline notice
    // still appears once the period has passed if the connection is still missing.
    public static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(5);
    private static readonly DateTime launched = LaunchTime();
    public static TimeSpan SinceLaunch => DateTime.UtcNow - launched;
    // How much longer a notice waits so that it cannot appear inside the quiet period; zero once it has passed.
    public static TimeSpan QuietRemaining(TimeSpan quiet) => quiet - SinceLaunch is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
    private static DateTime LaunchTime()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch (Exception) { return DateTime.UtcNow; }
    }
}
