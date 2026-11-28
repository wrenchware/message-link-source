using System.Globalization;

namespace MessageLink.Presentation.Services;

/// <summary>
/// /top: a live monitor in three pages: overview, workers (the server's helper processes) and queues (sends in flight and
/// scheduled). Everything comes from snapshots the server already keeps; refreshing asks no service for anything. Message
/// text never appears except the user's own queued sends.
/// </summary>
public sealed class TopApp(IConsoleAppHost shell, string page = "overview") : ConsoleApp(shell)
{
    public static readonly IReadOnlyList<string> Pages = ["overview", "workers", "queues"];

    private int pageIndex = Math.Max(0, Pages.ToList().IndexOf(page));
    private bool paused;
    private ConsoleSystemStatus status = new("dev", []);
    private readonly Dictionary<int, (DateTimeOffset At, TimeSpan Cpu)> helperCpu = [];
    private readonly Dictionary<int, double> helperRate = [];

    public override string Name => "top";
    public string Page => Pages[pageIndex];

    public override void Start() => Sample();

    public override void Tick()
    {
        if (!paused) Sample();
    }

    private void Sample()
    {
        status = Shell.Host.SystemStatus;
        foreach (var process in status.Processes)
        {
            if (process is not { ProcessId: { } pid, CpuTime: { } cpu }) continue;
            var now = Shell.Clock.GetUtcNow();
            // Of one core: the server's core count is not known here.
            if (helperCpu.TryGetValue(pid, out var last) && now > last.At)
                helperRate[pid] = Math.Max(0, (cpu - last.Cpu).TotalSeconds / (now - last.At).TotalSeconds * 100);
            helperCpu[pid] = (now, cpu);
        }
    }

    public override bool Key(ConsoleKeyPress key)
    {
        Message = null;
        var page = key.Kind switch
        {
            ConsoleKeyKind.Tab or ConsoleKeyKind.Right => (pageIndex + 1) % Pages.Count,
            ConsoleKeyKind.BackTab or ConsoleKeyKind.Left => (pageIndex + Pages.Count - 1) % Pages.Count,
            ConsoleKeyKind.Char when key.Char is >= '1' and <= '3' => key.Char - '1',
            _ => -1,
        };
        if (page >= 0)
        {
            pageIndex = page;
            return true;
        }
        if (key.Is('p')) { paused = !paused; Message = paused ? "Paused. p resumes." : null; return true; }
        return false;
    }

    public override void Click(int row, bool twice) { }

    public override ConsoleScreen Render(int width, int height)
    {
        var title = new List<ConsoleSpan> { new("top", ConsoleTone.Accent), new("  ") };
        for (var i = 0; i < Pages.Count; i++)
        {
            title.Add(new ConsoleSpan($"{i + 1}:{Pages[i]}", i == pageIndex ? ConsoleTone.Strong : ConsoleTone.Dim));
            title.Add(new ConsoleSpan(" "));
        }
        title.Add(new ConsoleSpan(paused ? " paused" : "", ConsoleTone.Warn));
        var rows = Page switch
        {
            "workers" => Workers(width),
            "queues" => Queues(width),
            _ => Overview(width, height),
        };
        return Screen(title, Fill(rows, height), ("Tab", "page"), ("1-3", "jump"), ("p", paused ? "resume" : "pause"), ("q", "quit"));
    }

    // ---- Pages ----

    private IEnumerable<ConsoleRow> Overview(int width, int height)
    {
        var now = Shell.Clock.GetUtcNow();
        var rows = new List<ConsoleRow> { new([new("Message Link " + status.Version, ConsoleTone.Strong)]) };
        // Messages each minute of the last half hour (or as many minutes as there is room for), in and out.
        var minutes = Math.Clamp(width - 22, 10, 60);
        var (incoming, outgoing) = Activity(now, minutes);
        rows.Add(Spark("in ", incoming, minutes, ConsoleTone.Ok));
        rows.Add(Spark("out", outgoing, minutes, ConsoleTone.Accent));
        rows.Add(ConsoleRow.Blank);
        rows.AddRange(ServiceTable(width, now));
        rows.Add(ConsoleRow.Blank);
        var queue = Shell.Host.Queue;
        rows.Add(new ConsoleRow([new("queue ", ConsoleTone.Dim),
            new($"{queue.Count(q => q.State is "sending" or "pending" or "unknown" or "held")} in flight", queue.Any(q => q.State is "unknown") ? ConsoleTone.Warn : ConsoleTone.Default),
            new(" · ", ConsoleTone.Dim), new($"{queue.Count(q => q.State == "scheduled")} scheduled")]));
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("RECENT", ConsoleTone.Dim));
        var room = Math.Max(0, height - rows.Count);
        foreach (var entry in Shell.Stream.Snapshot().Where(e => e.Kind != ActivityKind.Typing).TakeLast(room).Reverse())
        {
            var family = entry.Network.Length == 0 ? (BubbleFamily?)null : BubbleColours.TagFamily(entry.Network, entry.Message?.Transport);
            rows.Add(new ConsoleRow([
                new(Shell.Formatter.Time(entry.At, false).PadRight(9), ConsoleTone.Dim),
                new(KindWord(entry).PadRight(10), entry.Kind is ActivityKind.Disconnected or ActivityKind.SignInNeeded ? ConsoleTone.Warn : ConsoleTone.Default),
                ConsoleSpan.For(ConsoleCells.Pad(ConsoleLineFormatter.Tag(entry.Network), 10), family),
                new(ConsoleCells.Fit(entry.ConversationTitle ?? entry.Network, Math.Max(4, width - 31))),
            ]));
        }
        return rows;
    }

    private static string KindWord(ActivityEntry entry) => entry.Kind switch
    {
        ActivityKind.Message => entry.IsOutgoing ? "sent" : "message",
        ActivityKind.Edited => "edit",
        ActivityKind.Unsent => "unsend",
        ActivityKind.Reaction => "reaction",
        ActivityKind.Read => "read",
        ActivityKind.Call => "call",
        ActivityKind.Voicemail => "voicemail",
        ActivityKind.Connected => "connected",
        ActivityKind.Disconnected => "offline",
        ActivityKind.SignInNeeded => "sign-in",
        _ => "event",
    };

    private ConsoleRow Spark(string label, IReadOnlyList<double> values, int minutes, ConsoleTone tone) =>
        new([new(label + "  ", ConsoleTone.Dim), new(ConsoleCells.Spark(values), tone),
            new($"  {values.Sum().ToString(CultureInfo.InvariantCulture),3} in {minutes}m", ConsoleTone.Dim)]);

    /// <summary>Messages each minute for the last <paramref name="minutes"/> minutes, oldest first, from the activity stream.</summary>
    public (IReadOnlyList<double> In, IReadOnlyList<double> Out) Activity(DateTimeOffset now, int minutes)
    {
        var incoming = new double[minutes];
        var outgoing = new double[minutes];
        foreach (var entry in Shell.Stream.Snapshot())
        {
            if (entry.Kind != ActivityKind.Message || entry.IsBackfill) continue;
            var ago = (int)Math.Floor((now - entry.At).TotalMinutes);
            if (ago < 0 || ago >= minutes) continue;
            (entry.IsOutgoing ? outgoing : incoming)[minutes - 1 - ago]++;
        }
        return (incoming, outgoing);
    }

    private IEnumerable<ConsoleRow> ServiceTable(int width, DateTimeOffset now)
    {
        var chats = Shell.Targets.Entries();
        var table = new ConsoleTable(new("SERVICE", 15), new("STATE", 13), new("PID", 6, Right: true), new("UPTIME", 9, Right: true),
            new("RST", 3, Right: true), new("MEM", 6, Right: true), new("CPU%", 5, Right: true), new("EVENT", 5, Right: true),
            new("SENT", 5, Right: true), new("UNREAD", 6, Right: true));
        foreach (var process in status.Processes)
        {
            var network = process.Network.Length > 0 ? process.Network : NetworkOf(process.Name);
            var unread = chats.Count(chat => chat.Members.Any(m => m.Network == network && (m.UnreadCount > 0 || m.IsMarkedUnread)));
            table.Add(ConsoleSpan.For(process.Name, BubbleColours.TagFamily(network, null)), State(process),
                new(process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—", ConsoleTone.Dim),
                new(process.Uptime is { } up ? ConsoleCells.Uptime(up) : "—"),
                new(process.Restarts.ToString(CultureInfo.InvariantCulture), process.Restarts > 0 ? ConsoleTone.Warn : ConsoleTone.Dim),
                new(process.MemoryBytes is { } bytes ? ConsoleCells.Bytes(bytes) : "—"),
                new(process.ProcessId is { } pid && helperRate.TryGetValue(pid, out var rate) ? rate.ToString("0.0", CultureInfo.InvariantCulture) : "—"),
                new(ConsoleCells.Age(process.LastEvent, now)), new(ConsoleCells.Age(process.LastSend, now), ConsoleTone.Dim),
                new(unread.ToString(CultureInfo.InvariantCulture), unread > 0 ? ConsoleTone.Strong : ConsoleTone.Dim));
        }
        if (status.Processes.Count == 0) return [ConsoleRow.Of("No services are set up yet. Message Link's Settings → Connections adds one.", ConsoleTone.Dim)];
        return table.Render(width).Select(row => new ConsoleRow(row.Spans));
    }

    /// <summary>"● connected" in green, "○ offline" in yellow, "· unconfigured" dim, "✗ sign-in needed" in red.</summary>
    public static ConsoleSpan State(ConsoleProcessStatus process) => process.State switch
    {
        "connected" => new("● connected", ConsoleTone.Ok),
        "offline" => new("○ offline", ConsoleTone.Warn),
        "unconfigured" => new("· not set up", ConsoleTone.Dim),
        "sign-in needed" => new("✗ sign in", ConsoleTone.Error),
        var other => new("? " + other, ConsoleTone.Dim),
    };

    public static string NetworkOf(string name) =>
        name.StartsWith("Google Voice", StringComparison.Ordinal) ? "Google Voice" : name;

    private IEnumerable<ConsoleRow> Workers(int width)
    {
        var now = Shell.Clock.GetUtcNow();
        var table = new ConsoleTable(new("HELPER", 15), new("STATE", 9), new("PID", 6, Right: true), new("UPTIME", 9, Right: true),
            new("RESTARTS", 8, Right: true), new("MEM", 6, Right: true), new("CPU%", 5, Right: true), new("LAST EXIT", 0, Flex: true));
        foreach (var process in status.Processes)
        {
            var exit = process.LastExit is { } code
                ? $"{process.ExitHow ?? "exited"} {code.ToString(CultureInfo.InvariantCulture)}" + (process.ExitAt is { } at ? " · " + ConsoleCells.Age(now - at) + " ago" : "")
                : process.ExitHow ?? "—";
            table.Add(ConsoleSpan.For(process.Name, BubbleColours.TagFamily(process.Network.Length > 0 ? process.Network : NetworkOf(process.Name), null)),
                new(process.Running ? "running" : "stopped", process.Running ? ConsoleTone.Ok : ConsoleTone.Dim),
                new(process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—", ConsoleTone.Dim),
                new(process.Uptime is { } up ? ConsoleCells.Uptime(up) : "—"),
                new(process.Restarts.ToString(CultureInfo.InvariantCulture), process.Restarts > 0 ? ConsoleTone.Warn : ConsoleTone.Dim),
                new(process.MemoryBytes is { } bytes ? ConsoleCells.Bytes(bytes) : "—"),
                new(process.ProcessId is { } pid && helperRate.TryGetValue(pid, out var rate) ? rate.ToString("0.0", CultureInfo.InvariantCulture) : "—"),
                new(exit, process.LastExit is { } c && c != 0 ? ConsoleTone.Warn : ConsoleTone.Dim));
        }
        var rows = table.Render(width).Select(row => new ConsoleRow(row.Spans)).ToList();
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("One helper process per service (one per Voice account). A helper that stops after connecting is", ConsoleTone.Dim));
        rows.Add(ConsoleRow.Of("resumed from its saved session by Message Link itself; nothing here starts or stops one.", ConsoleTone.Dim));
        return rows;
    }

    private IEnumerable<ConsoleRow> Queues(int width)
    {
        var now = Shell.Clock.GetUtcNow();
        var queue = Shell.Host.Queue;
        if (queue.Count == 0) return [ConsoleRow.Of("Nothing in flight. Nothing scheduled.", ConsoleTone.Dim)];
        var titles = Shell.Targets.Entries();
        var table = new ConsoleTable(new("STATE", 10), new("TO", 18), new("WHEN", 9, Right: true), new("TEXT", 0, Flex: true));
        foreach (var item in queue)
        {
            var title = titles.FirstOrDefault(t => t.DisplayId == item.ChatId || t.Members.Any(m => m.Id == item.ChatId))?.Title ?? "Someone";
            var when = item.Due is { } due ? (due > now ? "in " + ConsoleCells.Age(due - now) : "due") : ConsoleCells.Age(now - item.Created) + " ago";
            table.Add(new(item.State, item.State switch { "unknown" or "not sent" => ConsoleTone.Error, "held" or "pending" => ConsoleTone.Warn, "scheduled" => ConsoleTone.Accent, _ => ConsoleTone.Default }),
                new(title, ConsoleTone.Strong), new(when, ConsoleTone.Dim), new(item.Text.Replace('\n', ' ')));
        }
        var rows = table.Render(width).Select(row => new ConsoleRow(row.Spans)).ToList();
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("A send whose outcome is unknown is reported, never sent again. Scheduled sends go at their time.", ConsoleTone.Dim));
        return rows;
    }
}
