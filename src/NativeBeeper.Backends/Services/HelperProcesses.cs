using System.Diagnostics;

namespace NativeBeeper.App.Services;

// Helper processes running from one install folder, found by file name and then by the folder their executable is in:
// the development copy runs from a folder of its own, and its helpers are never counted as this one's. Nothing here
// ends a process it did not start. A helper from this folder that predates this process may still belong to a live
// copy running on another data folder, so launch only counts them.
internal static class HelperProcesses
{
    // The four providers, then this PC's own Tailscale node (Services/Tailnet), which holds files in the data folder too.
    public static readonly string[] Names = ["gmessages-helper", "messenger-helper", "gvoice-helper", "instagram-helper", "tailnet-helper"];

    // Each helper found: the index of its name, whether it started before the cutoff, its process id and start time.
    // One whose path or start time cannot be read (it exited meanwhile, or access was refused) is counted, not guessed.
    public static List<(int Name, bool Before, int Id, DateTime Started)> Running(string folder, IReadOnlyList<string> names, DateTime cutoff, out int unreadable)
    {
        var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var found = new List<(int, bool, int, DateTime)>();
        unreadable = 0;
        for (var index = 0; index < names.Count; index++)
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(names[index]); }
            catch (Exception) { unreadable++; continue; }
            foreach (var process in candidates)
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), home, StringComparison.OrdinalIgnoreCase)) continue;
                        var started = process.StartTime;
                        found.Add((index, started < cutoff, process.Id, started));
                    }
                    catch (Exception) { unreadable++; }
                }
            }
        }
        return found;
    }

    // Waits for the given processes to end, up to the bound, then ends any that have not. Only for this process's own
    // helpers at exit, where it does no more than the kill-on-close job is about to. Returns how many had to be ended.
    public static int WaitThenEnd(IEnumerable<(int Id, DateTime Started)> helpers, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        var ended = 0;
        foreach (var (id, started) in helpers)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                // The id alone could by now name some other process that reused it.
                if (process.StartTime != started) continue;
                var left = bound - clock.Elapsed;
                if (process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero)) continue;
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(1));
                ended++;
            }
            catch (Exception) { } // Already gone.
        }
        return ended;
    }
}
