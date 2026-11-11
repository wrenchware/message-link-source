using System.Globalization;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// /top: a live monitor in five pages — overview, workers (helper processes), queues (sends in flight and scheduled),
/// cache (what is held in memory and on disk) and perf (the process). Everything comes from snapshots the host already
/// keeps; refreshing asks no service for anything. Message text never appears except the user's own queued sends.
/// </summary>
public sealed class TopApp(IConsoleAppHost shell, string page = "overview") : ConsoleApp(shell)
{
    public static readonly IReadOnlyList<string> Pages = ["overview", "workers", "queues", "cache", "perf"];

    private int pageIndex = Math.Max(0, Pages.ToList().IndexOf(page));
    private bool paused;
    private ConsoleSystemStatus status = new("dev", []);
    private ConsolePerf? perf, previousPerf;
    private readonly Dictionary<int, (DateTimeOffset At, TimeSpan Cpu)> helperCpu = [];
    private readonly Dictionary<int, double> helperRate = [];
    private IReadOnlyList<ConsoleStorageItem>? storage;
    private bool measuring;
    private CancellationTokenSource? measure;

    public override string Name => "top";
    public string Page => Pages[pageIndex];

    public override void Start() { Sample(); if (Page == "cache") Measure(); }
    public override void Stop() => measure?.Cancel();

    public override void Tick()
    {
        if (!paused) Sample();
    }

    private void Sample()
    {
        status = Shell.Host.SystemStatus;
        previousPerf = perf;
        perf = Shell.Host.Perf();
        foreach (var process in status.Processes)
        {
            if (process is not { ProcessId: { } pid, CpuTime: { } cpu }) continue;
            var now = Shell.Clock.GetUtcNow();
            if (helperCpu.TryGetValue(pid, out var last) && now > last.At)
                helperRate[pid] = Math.Max(0, (cpu - last.Cpu).TotalSeconds / (now - last.At).TotalSeconds / Math.Max(1, perf?.Processors ?? 1) * 100);
            helperCpu[pid] = (now, cpu);
        }
    }

    /// <summary>The Messages process's processor use over the last interval, as Task Manager shows it (all cores = 100%).</summary>
    public double? CpuPercent => perf is { } now && previousPerf is { } then && now.At > then.At
        ? Math.Max(0, (now.CpuTime - then.CpuTime).TotalSeconds / (now.At - then.At).TotalSeconds / Math.Max(1, now.Processors) * 100) : null;

    private void Measure()
    {
        if (measuring) return;
        measuring = true;
        measure?.Cancel();
        measure = new CancellationTokenSource();
        var token = measure.Token;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<ConsoleStorageItem> items;
            try { items = await Shell.Host.StorageAsync(token); }
            catch (Exception) { items = []; }
            if (token.IsCancellationRequested) return;
            storage = items;
            measuring = false;
            Invalidate();
        });
    }

    public override bool Key(ConsoleKeyPress key)
    {
        Message = null;
        var page = key.Kind switch
        {
            ConsoleKeyKind.Tab or ConsoleKeyKind.Right => (pageIndex + 1) % Pages.Count,
            ConsoleKeyKind.BackTab or ConsoleKeyKind.Left => (pageIndex + Pages.Count - 1) % Pages.Count,
            ConsoleKeyKind.Char when key.Char is >= '1' and <= '5' => key.Char - '1',
            _ => -1,
        };
        if (page >= 0)
        {
            pageIndex = page;
            if (Page == "cache" && storage is null) Measure();
            return true;
        }
        if (key.Is('p')) { paused = !paused; Message = paused ? "Paused. p resumes." : null; return true; }
        if (key.Is('r') && Page == "cache") { storage = null; Measure(); return true; }
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
            "cache" => Cache(width),
            "perf" => Perf(width),
            _ => Overview(width, height),
        };
        return Screen(title, Fill(rows, height), ("Tab", "page"), ("1-5", "jump"), ("p", paused ? "resume" : "pause"),
            Page == "cache" ? ("r", "measure") : ("", ""), ("q", "quit"));
    }

    // ---- Pages ----

    private IEnumerable<ConsoleRow> Overview(int width, int height)
    {
        var now = Shell.Clock.GetUtcNow();
        var rows = new List<ConsoleRow>();
        var head = new List<ConsoleSpan> { new("Message Link " + status.Version, ConsoleTone.Strong) };
        if (perf is { } p)
        {
            head.Add(new(" · up " + ConsoleCells.Uptime(p.Uptime), ConsoleTone.Dim));
            head.Add(new(" · mem ", ConsoleTone.Dim)); head.Add(new(ConsoleCells.Bytes(p.WorkingSet)));
            head.Add(new(" · gc ", ConsoleTone.Dim)); head.Add(new(string.Join("/", p.Collections)));
            head.Add(new(" · threads ", ConsoleTone.Dim)); head.Add(new(p.Threads.ToString(CultureInfo.InvariantCulture)));
        }
        rows.Add(new ConsoleRow(head));
        var cpu = CpuPercent ?? 0;
        var barWidth = Math.Clamp(width - 18, 10, 40);
        var (filled, empty) = ConsoleCells.Bar(cpu / 100, barWidth);
        rows.Add(new ConsoleRow([new("cpu  [", ConsoleTone.Dim), new(filled, cpu > 50 ? ConsoleTone.Warn : ConsoleTone.Ok), new(empty, ConsoleTone.Dim),
            new("] ", ConsoleTone.Dim), new(cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%")]));
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

    private IEnumerable<ConsoleRow> Cache(int width)
    {
        var rows = new List<ConsoleRow> { ConsoleRow.Of("IN MEMORY", ConsoleTone.Dim) };
        if (perf is { Caches.Count: > 0 } p)
        {
            var table = new ConsoleTable(new("HOLDER", 0, Flex: true), new("COUNT", 10, Right: true), new("SIZE", 8, Right: true));
            foreach (var cache in p.Caches)
                table.Add(new(cache.Name), new(ConsoleCells.Count(cache.Count)), new(cache.Bytes is { } b ? ConsoleCells.Bytes(b) : "—", ConsoleTone.Dim));
            rows.AddRange(table.Render(Math.Min(width, 60)).Select(row => new ConsoleRow(row.Spans)));
        }
        else rows.Add(ConsoleRow.Of("Nothing to report.", ConsoleTone.Dim));
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("ON DISK", ConsoleTone.Dim));
        if (storage is null) { rows.Add(ConsoleRow.Of("measuring…", ConsoleTone.Dim)); return rows; }
        if (storage.Count == 0) { rows.Add(ConsoleRow.Of("Nothing measured.", ConsoleTone.Dim)); return rows; }
        var total = Math.Max(1, storage.Sum(s => s.Bytes));
        var bar = Math.Clamp(width - 48, 6, 30);
        foreach (var item in storage)
        {
            var (filled, empty) = ConsoleCells.Bar(item.Bytes / (double)total, bar);
            rows.Add(new ConsoleRow([new(ConsoleCells.Pad(item.Name, 22)), new(ConsoleCells.Pad(ConsoleCells.Bytes(item.Bytes), 8, right: true)), new("  "),
                new(filled, ConsoleTone.Accent), new(empty, ConsoleTone.Dim),
                new(item.Reclaimable > 0 ? "  " + ConsoleCells.Bytes(item.Reclaimable) + " reclaimable" : "", ConsoleTone.Dim)]));
        }
        rows.Add(new ConsoleRow([new(ConsoleCells.Pad("total", 22), ConsoleTone.Strong), new(ConsoleCells.Pad(ConsoleCells.Bytes(total), 8, right: true), ConsoleTone.Strong)]));
        return rows;
    }

    private IEnumerable<ConsoleRow> Perf(int width)
    {
        if (perf is not { } p) return [ConsoleRow.Of("The process can't be read here.", ConsoleTone.Dim)];
        var rows = new List<ConsoleRow>();
        var bar = Math.Clamp(width - 30, 10, 40);
        ConsoleRow Gauge(string name, long bytes, long of)
        {
            var (filled, empty) = ConsoleCells.Bar(of <= 0 ? 0 : bytes / (double)of, bar);
            return new ConsoleRow([new(ConsoleCells.Pad(name, 12), ConsoleTone.Dim), new(ConsoleCells.Pad(ConsoleCells.Bytes(bytes), 8, right: true)), new("  "),
                new(filled, ConsoleTone.Accent), new(empty, ConsoleTone.Dim)]);
        }
        var top = Math.Max(p.WorkingSet, p.PrivateBytes);
        rows.Add(ConsoleRow.Of("MEMORY (bars against the larger of working set and private)", ConsoleTone.Dim));
        rows.Add(Gauge("working set", p.WorkingSet, top));
        rows.Add(Gauge("private", p.PrivateBytes, top));
        rows.Add(Gauge("committed", p.Committed, top));
        rows.Add(Gauge("gc heap", p.Heap, top));
        rows.Add(Gauge("managed", p.Managed, top));
        rows.Add(Gauge("fragmented", p.Fragmented, top));
        rows.Add(ConsoleRow.Of($"native ≈ {ConsoleCells.Bytes(Math.Max(0, p.PrivateBytes - p.Committed))} (private less the GC's committed: XAML, composition, pictures)", ConsoleTone.Dim));
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("GARBAGE COLLECTOR", ConsoleTone.Dim));
        string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
        rows.Add(new ConsoleRow([.. p.Generations.Select((size, i) => new ConsoleSpan($"{(i < names.Length ? names[i] : "gen" + i)} {ConsoleCells.Bytes(size)}   "))]));
        rows.Add(new ConsoleRow([new("collections ", ConsoleTone.Dim), new(string.Join(" / ", p.Collections)), new("  (gen0 / gen1 / gen2)", ConsoleTone.Dim)]));
        rows.Add(ConsoleRow.Blank);
        rows.Add(ConsoleRow.Of("PROCESS", ConsoleTone.Dim));
        rows.Add(new ConsoleRow([new("handles ", ConsoleTone.Dim), new(ConsoleCells.Count(p.Handles)), new("   gdi ", ConsoleTone.Dim), new(ConsoleCells.Count(p.Gdi)),
            new("   user ", ConsoleTone.Dim), new(ConsoleCells.Count(p.User)), new("   threads ", ConsoleTone.Dim), new(ConsoleCells.Count(p.Threads))]));
        rows.Add(new ConsoleRow([new("cpu time ", ConsoleTone.Dim), new(ConsoleCells.Uptime(p.CpuTime)), new("   cores ", ConsoleTone.Dim),
            new(p.Processors.ToString(CultureInfo.InvariantCulture)), new("   uptime ", ConsoleTone.Dim), new(ConsoleCells.Uptime(p.Uptime))]));
        return rows;
    }
}
