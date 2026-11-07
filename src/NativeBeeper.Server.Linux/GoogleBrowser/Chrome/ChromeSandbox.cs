using System.Diagnostics;
using System.Globalization;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Chrome's own sandbox in the container (an external security review, 2026-10-11). Chrome runs as the server's account, which can read the keys volume, so a page's code that broke out of a
/// renderer is the whole server's secrets unless the renderer is sandboxed. The sandbox needs unprivileged user namespaces, which Docker's default seccomp profile refuses a container with no
/// capability (<c>docker/messages-server/chrome-seccomp.json</c> allows them, and install-unraid.sh <c>--chrome-sandbox</c> uses it; the Community Applications template uses
/// <c>seccomp=unconfined</c>, as a template cannot place that file on the host). Two things are done here, neither of which can be a guess:
/// <list type="bullet">
/// <item>Before Chrome is started with its sandbox on, the container is asked whether it can make a user namespace at all (<see cref="Preflight"/>). If it cannot, Chrome is not started
/// (<c>server google refused reason=sandbox_unavailable</c>) rather than being started to fail, or started without a sandbox behind the owner's back.</item>
/// <item>After Chrome is up, what it did is read from <c>/proc</c> (<see cref="InspectAsync"/>): its renderers carry a seccomp filter (<c>Seccomp: 2</c>) only when the sandbox is on, since
/// <c>--no-sandbox</c> leaves them with none. The result is one line of fixed words and counts, <c>google chrome sandbox=active|inactive|unknown</c>. Nothing of Chrome is attached to.</item>
/// </list>
/// </summary>
internal static class ChromeSandbox
{
    /// <summary>
    /// Why a sandboxed Chrome could not start here, as one fixed word, or null when a user namespace can be made. <paramref name="read"/> answers a file's first line (or null),
    /// and <paramref name="unshareWorks"/> whether a process can really create a user namespace (what the kernel's switches and the seccomp profile together decide).
    /// </summary>
    public static string? Preflight(Func<string, string?> read, Func<bool> unshareWorks)
    {
        var maximum = read("/proc/sys/user/max_user_namespaces")?.Trim();
        if (maximum is null || !int.TryParse(maximum, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0) return "userns_disabled";
        // Debian's kernels and others have this switch; the file is simply absent where it does not exist.
        if (read("/proc/sys/kernel/unprivileged_userns_clone")?.Trim() == "0") return "userns_disabled";
        // Ubuntu 24.04's AppArmor rule that confines unprivileged user namespaces.
        if (read("/proc/sys/kernel/apparmor_restrict_unprivileged_userns")?.Trim() == "1") return "userns_restricted";
        return unshareWorks() ? null : "userns_denied";
    }

    /// <summary>The first line of a file, or null if it cannot be read.</summary>
    public static string? ReadFirstLine(string path)
    {
        try { return File.ReadLines(path).FirstOrDefault(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether a child process can create a user namespace in this container: <c>unshare --user true</c>, which is what Chrome's sandbox does first. A missing program or a timeout is "no".</summary>
    public static bool UnshareWorks()
    {
        try
        {
            var start = new ProcessStartInfo("unshare") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--user");
            start.ArgumentList.Add("true");
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    /// <summary>The process group a <c>/proc/&lt;pid&gt;/stat</c> line names (its fifth field; the second, the command, may hold spaces and parentheses), or null.</summary>
    public static int? GroupOf(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After the command: state, parent, group.
        return fields.Length >= 3 && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group) ? group : null;
    }

    /// <summary>The <c>Seccomp:</c> mode in a <c>/proc/&lt;pid&gt;/status</c> text (2 is a filter), or null.</summary>
    public static int? SeccompOf(string status)
    {
        foreach (var line in status.Split('\n'))
            if (line.StartsWith("Seccomp:", StringComparison.Ordinal) && int.TryParse(line["Seccomp:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mode)) return mode;
        return null;
    }

    /// <summary>One process of Chrome's group: its command line (NUL-separated) and its status text.</summary>
    internal sealed record Sample(string CommandLine, string Status);

    /// <summary>The <c>Seccomp_filters:</c> count in a <c>/proc/&lt;pid&gt;/status</c> text, or null where the kernel does not say it.</summary>
    public static int? FiltersOf(string status)
    {
        foreach (var line in status.Split('\n'))
            if (line.StartsWith("Seccomp_filters:", StringComparison.Ordinal) && int.TryParse(line["Seccomp_filters:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return count;
        return null;
    }

    // Chrome rewrites its children's command lines into one space-separated title, so the arguments are split on spaces as well as on NULs: split on
    // NULs alone, the server found 16 processes and no --type= among them (2026-10-12).
    private static string? TypeOf(Sample sample) =>
        sample.CommandLine.Split(['\0', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(argument => argument.StartsWith("--type=", StringComparison.Ordinal))?["--type=".Length..];

    /// <summary>
    /// The line for what the processes show: <c>active</c> when every renderer is filtered beyond the browser itself, <c>inactive</c> when some is not, <c>unknown</c>
    /// when there is no renderer yet. In a container every process carries the runtime's own seccomp filter (the server's 16 processes all read <c>Seccomp: 2</c>,
    /// 2026-10-12), so a renderer counts as sandboxed when it has more filters than the browser process; where the kernel gives no count, a filter at all. Counts only.
    /// </summary>
    public static string Describe(IEnumerable<Sample> samples)
    {
        var all = samples.ToList();
        var baseline = all.Where(sample => TypeOf(sample) is null).Select(sample => FiltersOf(sample.Status)).OfType<int>().DefaultIfEmpty(-1).Min();
        int renderers = 0, filtered = 0;
        foreach (var sample in all)
        {
            if (TypeOf(sample) != "renderer") continue;
            renderers++;
            if (baseline >= 0 && FiltersOf(sample.Status) is { } count ? count > baseline : SeccompOf(sample.Status) == 2) filtered++;
        }
        var word = renderers == 0 ? "unknown" : filtered == renderers ? "active" : "inactive";
        return $"google chrome sandbox={word} renderers={renderers} filtered={filtered}";
    }

    /// <summary>
    /// How many of the processes found are of each kind (Chrome's <c>--type=</c>; none is the browser itself or the launch shell), how many have a seccomp filter, and the
    /// range of their filter counts. Fixed words and counts.
    /// </summary>
    public static string Census(IEnumerable<Sample> samples)
    {
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var counts = new List<int>();
        int total = 0, filtered = 0;
        foreach (var sample in samples)
        {
            total++;
            if (SeccompOf(sample.Status) == 2) filtered++;
            if (FiltersOf(sample.Status) is { } count) counts.Add(count);
            var type = TypeOf(sample);
            var kind = type is "renderer" or "zygote" or "gpu-process" or "utility" or "broker" or "crashpad-handler" ? type : type is null ? "none" : "other";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        return $"google chrome processes total={total} filtered={filtered} filters={(counts.Count == 0 ? "none" : counts.Min() + "-" + counts.Max())} "
            + $"kinds={(kinds.Count == 0 ? "none" : string.Join(",", kinds.Select(pair => pair.Key + ":" + pair.Value)))}";
    }

    /// <summary>The parent process a <c>/proc/&lt;pid&gt;/stat</c> line names (its fourth field), or null.</summary>
    public static int? ParentOf(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) ? parent : null;
    }

    /// <summary>
    /// Chrome's processes, read from /proc: the process group's, and every process descended from one of them. The sandbox's zygote starts a session of its own, so its renderers are
    /// in another group, and reading the group alone found none (the server said <c>sandbox=unknown renderers=0</c> for a sandboxed Chrome, 2026-10-12). Whatever cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<Sample> SampleGroup(int group, string proc = "/proc")
    {
        var processes = new Dictionary<int, (int? Parent, int? Group)>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(proc))
            {
                if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(folder, "stat"));
                    processes[pid] = (ParentOf(stat), GroupOf(stat));
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        var members = processes.Where(entry => entry.Value.Group == group).Select(entry => entry.Key).ToHashSet();
        for (var added = true; added;)
        {
            added = false;
            foreach (var (pid, process) in processes)
                if (process.Parent is { } parent && members.Contains(parent) && members.Add(pid)) added = true;
        }
        var found = new List<Sample>();
        foreach (var pid in members.Order())
        {
            var folder = Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture));
            try { found.Add(new(File.ReadAllText(Path.Combine(folder, "cmdline")), File.ReadAllText(Path.Combine(folder, "status")))); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>
    /// After a Chrome started with its sandbox on has had time to show a page, says once whether its renderers are filtered; looks again later if it had none yet. A Chrome with no
    /// process group to read (the harness's) says nothing.
    /// </summary>
    public static async Task InspectAsync(IChromeRunning running, Action<string> log, CancellationToken stop, TimeSpan? first = null, TimeSpan? again = null, Func<int, IReadOnlyList<Sample>>? sample = null)
    {
        if (running.ProcessGroup is not { } group) return;
        try
        {
            string line = "";
            IReadOnlyList<Sample> samples = [];
            foreach (var wait in new[] { first ?? TimeSpan.FromSeconds(20), again ?? TimeSpan.FromSeconds(40) })
            {
                await Task.Delay(wait, stop).ConfigureAwait(false);
                if (running.Exited.IsCompleted) return;
                samples = (sample ?? (g => SampleGroup(g)))(group);
                line = Describe(samples);
                if (!line.Contains("sandbox=unknown", StringComparison.Ordinal)) break;
            }
            log(line);
            // Still no renderer found for a Chrome that was showing pages (2026-10-12, after following descendants too): what was found, by kind, says where they went.
            if (line.Contains("sandbox=unknown", StringComparison.Ordinal)) log(Census(samples));
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// The server's latest reading of its Chrome's sandbox, for its owner's <c>host.status</c> (SWITCHBOARD.md 4.6): <c>off</c> for a Chrome started without one, <c>unknown</c> until
/// <see cref="ChromeSandbox.InspectAsync"/> has said, then the word of its <c>google chrome sandbox=</c> line. Taken from that line as it is logged, so the two never disagree.
/// </summary>
internal sealed partial class ChromeSandboxReading(string initial)
{
    private volatile string word = initial;

    public string Word => word;

    /// <summary>The log, with each line also read for the sandbox's word.</summary>
    public Action<string> Tee(Action<string> log) => line =>
    {
        Note(line);
        log(line);
    };

    internal void Note(string line)
    {
        if (SandboxLine().Match(line) is { Success: true } found) word = found.Groups[1].Value;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^google chrome sandbox=(active|inactive|unknown) ")]
    private static partial System.Text.RegularExpressions.Regex SandboxLine();
}
