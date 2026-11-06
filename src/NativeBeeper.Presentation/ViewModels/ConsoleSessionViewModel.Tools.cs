using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// Console tools that print (/diag, /whoami, /uptime, /neofetch, /theme ...), the login banner, and full-screen apps.
public sealed partial class ConsoleSessionViewModel : IConsoleAppHost
{
    public static readonly string[] Logo =
    [
        @" __  __ ___ ___ ___   _   ___ ___ ___ ",
        @"|  \/  | __/ __/ __| /_\ / __| __/ __|",
        @"| |\/| | _|\__ \__ \/ _ \ (_ | _|\__ \",
        @"|_|  |_|___|___/___/_/ \_\___|___|___/",
    ];

    /// <summary>The colour scheme in use (ConsoleThemes); the window draws with it.</summary>
    [ObservableProperty] private string theme = ConsoleThemes.Default;

    /// <summary>The full-screen app running on the alternate screen, or null while the scroll is shown.</summary>
    [ObservableProperty] private ConsoleApp? activeApp;

    /// <summary>The prompt as an app shows it while it runs: "top&gt;".</summary>
    public string AppPrompt => ActiveApp is { } app ? app.Name + ">" : Prompt;

    /// <summary>The window should hand the prompt to the app as its filter line.</summary>
    public event Action? FilterRequested;

    /// <summary>Whether moving pictures are welcome; the window sets it from Windows' animation effects.</summary>
    public bool Motion { get; set; } = true;

    /// <summary>"messages:Lauren&gt;": the prompt with a target, its name cut to fit a narrow window.</summary>
    public static string PromptFor(string title)
    {
        var name = title.Replace('\n', ' ').Trim();
        return "messages:" + (ConsoleCells.Width(name) > 24 ? ConsoleCells.Fit(name, 24) : name) + ">";
    }

    // ---- The login banner ----

    // Above the first line of a new console, as a login shell has it: when it was last opened. /neofetch follows it with
    // the version, services and chats.
    private void Motd()
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? last = null;
        try { last = host.LastOpened; host.LastOpened = now; } catch (Exception) { }
        if (last is not { } then || then >= now) return;
        IReadOnlyList<ConsoleSpan> spans = [new ConsoleSpan("Last login: " + TimeZoneInfo.ConvertTime(then, clock.LocalTimeZone).ToString("ddd MMM d HH:mm", CultureInfo.CurrentCulture) + " on console", ConsoleTone.Dim)];
        Add(now, (n, withDate) => formatter.Output(spans, false, now, n, withDate, wide: true), ConsoleVerbosity.Quiet, null);
    }

    // ---- Tools ----

    private async Task ToolAsync(string name, string rest)
    {
        switch (name)
        {
            case "thread": await ThreadAsync(rest); return;
            case "diag": Diag(); return;
            case "mirror": await MirrorAsync(rest); return;
            case "whoami": WhoAmI(); return;
            case "uptime": Uptime(); return;
            case "neofetch": Neofetch(); return;
            case "banner": foreach (var line in Logo) Out(line, ConsoleTone.Accent); return;
            case "cowsay": foreach (var line in ConsoleShell.Cow(rest)) Out(line); return;
            case "date": Out(clock.GetLocalNow().ToString("dddd d MMMM yyyy  HH:mm:ss  zzz", CultureInfo.CurrentCulture)); return;
            case "echo": Out(rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"' ? rest[1..^1] : rest); return;
            case "theme": ChooseTheme(rest); return;
            case "alias": await AliasAsync(rest); return;
            case "unalias": await UnaliasAsync(rest); return;
            case "watch": Watch(rest); return;
            case "next": await NextAsync(); return;
            case "grep": GrepScroll(rest); return;
            case "guide":
                try { host.OpenGuide(rest.Length == 0 ? null : rest); } catch (Exception) { Fail("The guide couldn't be opened."); return; }
                Out(rest.Length == 0 ? "Opened the Messages guide. F1 opens it from here too." : $"Opened the Messages guide at “{rest}”.", ConsoleTone.Dim);
                return;
            case "tour":
                try { host.StartTour(); } catch (Exception) { Fail("The tour couldn't start."); return; }
                Out("The tour is running in the main window.", ConsoleTone.Dim);
                return;
            default: Fail($"/{name} isn't available here."); return;
        }
    }

    private void Diag()
    {
        var checks = host.Checks();
        if (checks.Count == 0) { Out("This copy has nothing to check.", ConsoleTone.Dim); return; }
        Out("messages doctor", ConsoleTone.Strong);
        foreach (var check in checks)
        {
            var (mark, tone) = check.Level switch
            {
                ConsoleCheckLevel.Ok => ("✓", ConsoleTone.Ok),
                ConsoleCheckLevel.Info => ("·", ConsoleTone.Dim),
                ConsoleCheckLevel.Warn => ("!", ConsoleTone.Warn),
                _ => ("✗", ConsoleTone.Error),
            };
            Emit(new ConsoleOutLine([new ConsoleSpan(" " + mark + " ", tone), new ConsoleSpan(ConsoleCells.Pad(check.Name, 22), check.Level == ConsoleCheckLevel.Ok ? ConsoleTone.Default : tone),
                new ConsoleSpan(check.Detail, ConsoleTone.Dim)], check.Level == ConsoleCheckLevel.Fail));
        }
        var problems = checks.Count(c => c.Level is ConsoleCheckLevel.Warn or ConsoleCheckLevel.Fail);
        Out(problems == 0 ? "No problems found." : $"{problems.ToString(CultureInfo.CurrentCulture)} to look at. Settings → Troubleshooting has Connection health and the logs (/logs).",
            problems == 0 ? ConsoleTone.Ok : ConsoleTone.Warn);
    }

    // The server's check of its copies against the services (SWITCHBOARD.md stage 9): its newest results, and with "now" a check started.
    private async Task MirrorAsync(string rest)
    {
        var start = rest.Trim();
        if (start.Length > 0 && !start.Equals("now", StringComparison.OrdinalIgnoreCase)) { Fail("Use /mirror or /mirror now."); return; }
        ConsoleMirror? mirror;
        try { mirror = await host.MirrorAsync(start.Length > 0); }
        catch (Exception) { Fail("The server couldn't say just now."); return; }
        if (mirror is null) { Out("No server keeps copies for this PC: /mirror is for a PC that uses a Messages Server.", ConsoleTone.Dim); return; }
        if (start.Length > 0) Out("Checking now: each service takes about a second per conversation. /mirror again shows what it found.", ConsoleTone.Dim);
        if (mirror.Services.Count == 0) { Out(mirror.Running ? "The first check is under way." : "Not checked yet: the server checks every half hour.", ConsoleTone.Dim); return; }
        foreach (var service in mirror.Services)
        {
            var found = service.Differed == 0 ? "all matched"
                : $"{service.Differed} differed: {service.Missing} missing, {service.Extra} gone, {service.Changed} changed, read again";
            var tone = service.Differed == 0 ? ConsoleTone.Ok : ConsoleTone.Warn;
            Emit(new ConsoleOutLine([new ConsoleSpan(service.Differed == 0 ? " ✓ " : " ! ", tone), new ConsoleSpan(ConsoleCells.Pad(service.Name, 18)),
                new ConsoleSpan($"{service.Checked} checked · {found}" + (service.Skipped > 0 ? $" · {service.Skipped} not read" : "") + " · "
                    + TimeZoneInfo.ConvertTime(service.At, clock.LocalTimeZone).ToString("HH:mm", CultureInfo.CurrentCulture), ConsoleTone.Dim)], false));
        }
        if (mirror.Running && start.Length == 0) Out("Another check is under way.", ConsoleTone.Dim);
    }

    private void WhoAmI()
    {
        var env = host.Environment;
        var services = host.SystemStatus.Processes.Where(p => p.State != "unconfigured").ToArray();
        Out(new ConsoleSpan(env is null ? "you" : env.User + "@" + env.Machine, ConsoleTone.Strong),
            new ConsoleSpan(env is null ? "" : $" · Messages {env.Version} ({env.Install})", ConsoleTone.Dim));
        if (services.Length == 0) { Out("Signed in to nothing yet.", ConsoleTone.Dim); return; }
        Out([new ConsoleSpan("signed in: ", ConsoleTone.Dim), .. services.SelectMany((p, i) => new[]
        {
            new ConsoleSpan(i == 0 ? "" : ", "),
            ConsoleSpan.For(p.Name, BubbleColours.TagFamily(p.Network.Length > 0 ? p.Network : TopApp.NetworkOf(p.Name), null)),
        })]);
        if (Target is { } to) Out(new ConsoleSpan("talking to: ", ConsoleTone.Dim), ConsoleSpan.For(to.Title, BubbleColours.TagFamily(to.Network, null)));
    }

    private void Uptime()
    {
        var now = clock.GetUtcNow();
        var up = host.Perf()?.Uptime ?? now - stream.Launched;
        var entries = stream.Snapshot();
        int Messages(TimeSpan span) => entries.Count(e => e.Kind == ActivityKind.Message && !e.IsBackfill && now - e.At <= span);
        var connected = host.SystemStatus.Processes.Count(p => p.State == "connected");
        Out($" {clock.GetLocalNow():HH:mm:ss} up {ConsoleCells.Duration(up)},  {connected.ToString(CultureInfo.CurrentCulture)} service{(connected == 1 ? "" : "s")} connected,  " +
            $"messages: {Messages(TimeSpan.FromMinutes(1))}, {Messages(TimeSpan.FromMinutes(5))}, {Messages(TimeSpan.FromMinutes(15))}");
    }

    private void Neofetch()
    {
        var env = host.Environment;
        var status = host.SystemStatus;
        var perf = host.Perf();
        var chats = targets.Entries();
        var title = env is null ? "you@messages" : env.User + "@" + env.Machine;
        var info = new List<(string Key, ConsoleSpan Value)>
        {
            ("", new ConsoleSpan(title, ConsoleTone.Strong)),
            ("", new ConsoleSpan(new string('-', title.Length), ConsoleTone.Dim)),
            ("App", new ConsoleSpan("Messages " + status.Version + (env is null ? "" : " (" + env.Install + ")"))),
        };
        if (env is not null)
        {
            info.Add(("OS", new ConsoleSpan(env.Os + " " + env.Architecture)));
            info.Add(("Runtime", new ConsoleSpan(env.Runtime)));
            info.Add(("UI", new ConsoleSpan("WinUI 3 · " + env.AppSdk)));
        }
        if (perf is not null)
        {
            info.Add(("Uptime", new ConsoleSpan(ConsoleCells.Duration(perf.Uptime))));
            info.Add(("Memory", new ConsoleSpan(ConsoleCells.Bytes(perf.WorkingSet) + " working set · " + ConsoleCells.Bytes(perf.Managed) + " managed")));
        }
        info.Add(("Services", new ConsoleSpan($"{status.Processes.Count(p => p.State == "connected")} of {status.Processes.Count(p => p.State != "unconfigured")} connected")));
        info.Add(("Chats", new ConsoleSpan($"{chats.Count} · {chats.Count(c => c.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread))} unread")));
        info.Add(("Shell", new ConsoleSpan("messages console · " + Aliases.Count + " aliases")));
        info.Add(("Theme", new ConsoleSpan(Theme)));
        info.Add(("Font", new ConsoleSpan("Cascadia Mono")));
        // The picture only where it fits beside the facts; a narrow window gets the facts alone.
        var art = Columns < 70 ? [] : new[]
        {
            "   .--------------.   ", "   |  ..........  |   ", "   |  :  ____  :  |   ", "   |  : |    | :  |   ", "   |  : |____| :  |   ",
            "   |  :........:  |   ", "   '-------.  .---'   ", "           | /        ", "           |/         ", "                      ",
        };
        var rows = Math.Max(art.Length, info.Count + 2);
        for (var i = 0; i < rows; i++)
        {
            var spans = new List<ConsoleSpan>();
            if (art.Length > 0) spans.Add(new ConsoleSpan(i < art.Length ? art[i] : new string(' ', art[0].Length), ConsoleTone.Accent));
            if (i < info.Count)
            {
                if (info[i].Key.Length > 0) spans.Add(new ConsoleSpan(info[i].Key + ": ", ConsoleTone.Accent));
                spans.Add(info[i].Value);
            }
            else if (i == info.Count + 1)
            {
                // The palette strip neofetch ends with: here, the four services' own colours.
                foreach (var family in new[] { BubbleFamily.GoogleRcs, BubbleFamily.GoogleSms, BubbleFamily.Messenger, BubbleFamily.Instagram, BubbleFamily.GoogleVoice })
                    spans.Add(new ConsoleSpan("███", ConsoleTone.Service, family));
            }
            Out([.. spans]);
        }
    }

    private void ChooseTheme(string rest)
    {
        var wanted = rest.Trim();
        if (wanted.Length == 0)
        {
            foreach (var scheme in ConsoleThemes.All)
                Out(new ConsoleSpan(scheme.Name == Theme ? "● " : "  ", ConsoleTone.Accent), new ConsoleSpan(ConsoleCells.Pad(scheme.Name, 10), scheme.Name == Theme ? ConsoleTone.Strong : ConsoleTone.Default),
                    new ConsoleSpan(scheme.Description, ConsoleTone.Dim));
            Out("/theme name switches. Service colours stay their own in every theme.", ConsoleTone.Dim);
            return;
        }
        if (!ConsoleThemes.Exists(wanted)) { Fail($"There's no theme called “{wanted}”. Try {string.Join(", ", ConsoleThemes.All.Select(t => t.Name))}."); return; }
        var chosen = ConsoleThemes.Find(wanted).Name;
        Theme = chosen;
        try { host.Theme = chosen; } catch (Exception) { }
        log("console theme name=" + chosen);
        Out($"Theme: {chosen}.", ConsoleTone.Dim);
    }

    private void Watch(string rest)
    {
        if (rest.Equals("off", StringComparison.OrdinalIgnoreCase) || rest.Length == 0)
        {
            lock (gate) { personFilter = null; Rebuild(); UpdateHint(); }
            Out(rest.Length == 0 ? "Type a name: /watch name, or /watch off." : "Watching everyone again.", ConsoleTone.Dim);
            return;
        }
        if (Named(rest) is not { } target) return;
        lock (gate)
        {
            personFilter = (target.DisplayId, target.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal), target.Title);
            Rebuild(); UpdateHint();
        }
        Out(new ConsoleSpan("Watching ", ConsoleTone.Dim), new ConsoleSpan(target.Title, ConsoleTone.Strong), new ConsoleSpan(". /watch off shows everyone.", ConsoleTone.Dim));
    }

    // /grep: the lines the scroll shows now, through the pipe's own grep (smart case, -v, -i), leaving out this command's
    // own echo. Reads nothing but the scroll.
    private void GrepScroll(string rest)
    {
        List<ConsoleOutLine> lines;
        lock (gate)
        {
            var shown = buffer.Where(h => h.Visible && h.Line.Kind != ConsoleLineKind.Day).ToList();
            var echo = shown.FindLastIndex(h => h.Line.Kind == ConsoleLineKind.Command);
            if (echo >= 0) shown.RemoveAt(echo);
            lines = [.. shown.Select(h => h.Line.Wide
                ? new ConsoleOutLine(h.Line.Spans ?? [new ConsoleSpan(h.Line.Body)])
                : new ConsoleOutLine([new ConsoleSpan(h.Line.Time + "  ", ConsoleTone.Dim),
                    .. h.Line.ServiceTag.Length > 0 ? [ConsoleSpan.For(h.Line.ServiceTag, h.Line.Family), new ConsoleSpan("  ")] : Array.Empty<ConsoleSpan>(),
                    .. h.Line.Spans ?? [new ConsoleSpan(h.Line.Body)]]))];
        }
        var found = ConsoleShell.Apply(lines, [new ConsoleFilter("grep", ConsoleCommandParser.Words(rest))], _ => { }, _ => { }, out var problem);
        if (problem is not null) { Fail(problem); return; }
        log("console grep lines=" + lines.Count.ToString(CultureInfo.InvariantCulture) + " found=" + (found?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        if (found is not { Count: > 0 }) { Out("No lines in the scroll match. /search looks in messages kept on this PC; /msgs * | grep in recent ones.", ConsoleTone.Dim); return; }
        foreach (var line in found) Emit(line);
    }

    private async Task NextAsync()
    {
        var next = targets.Entries().Where(c => !c.Members.All(m => m.IsArchived) && c.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread))
            .OrderByDescending(c => c.LastActivity ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (next is null) { Out("Nothing unread. Inbox zero.", ConsoleTone.Ok); return; }
        if (await Safely(() => host.OpenAsync(next.DisplayId))) Out(new ConsoleSpan("Opened ", ConsoleTone.Dim), new ConsoleSpan(next.Title, ConsoleTone.Strong), new ConsoleSpan(" in Messages.", ConsoleTone.Dim));
        else Fail("Couldn't open that conversation.");
    }

    // ---- Named conversations ----

    private ChatTarget? OpenTarget() => host.OpenChatId is { } open ? TargetOf(open) : null;

    private ChatTarget? RefTarget(ConsoleRef item) => item.Target ?? (item.Hit is { } hit ? TargetOf(hit.Chat.Id) : null);

    // ---- Full-screen apps ----

    private void LaunchApp(string app, string argument)
    {
        ConsoleApp? next = app switch
        {
            "top" => new TopApp(this, argument.Length == 0 ? "overview" : argument),
            "chats" => new ChatsApp(this, argument),
            "logs" => host.LogSources.Count == 0 ? null : new PagerApp(this, argument),
            "matrix" => new MatrixApp(this),
            _ => null,
        };
        if (next is null) { Fail(app == "logs" ? "No logs are available here." : $"/{app} isn't available here."); return; }
        if (Capturing)
        {
            // In a pipe an app prints one screen, as top -b does, rather than taking the window over.
            next.Start();
            next.Tick();
            var screen = next.Render(Columns, 60);
            foreach (var row in screen.Rows.Reverse().SkipWhile(r => r.Text.Trim().Length == 0).Reverse()) Emit(new ConsoleOutLine(row.Spans));
            next.Stop();
            return;
        }
        Show(next);
    }

    private void Show(ConsoleApp app)
    {
        if (ActiveApp is { } old) old.Stop();
        ActiveApp = app;
        OnPropertyChanged(nameof(AppPrompt));
        app.Start();
        log("console app name=" + app.Name);
    }

    // "| less": the pipe's output in the pager.
    private void OpenPager(IReadOnlyList<ConsoleOutLine> lines) => Show(new PagerApp(this, lines));

    /// <summary>F1 in the console: the guide at its console chapter.</summary>
    public void OpenGuide(string? topic)
    {
        try { host.OpenGuide(topic); } catch (Exception) { }
        log("console guide via=key");
    }

    /// <summary>Closes the app and shows the scroll as it was left.</summary>
    public void CloseApp()
    {
        if (ActiveApp is not { } app) return;
        app.Stop();
        ActiveApp = null;
        OnPropertyChanged(nameof(AppPrompt));
    }

    /// <summary>A key for the running app. q and Esc close it when the app does not use them.</summary>
    public bool AppKey(ConsoleKeyPress key)
    {
        if (ActiveApp is not { } app) return false;
        if (app.Key(key)) { app.Redraw(); return true; }
        if (key.Kind == ConsoleKeyKind.Escape || key.Is('q')) { CloseApp(); return true; }
        return false;
    }

    // ---- What apps may ask (IConsoleAppHost) ----

    IConsoleHost IConsoleAppHost.Host => host;
    ConversationTargets IConsoleAppHost.Targets => targets;
    ActivityStream IConsoleAppHost.Stream => stream;
    TimeProvider IConsoleAppHost.Clock => clock;
    ConsoleLineFormatter IConsoleAppHost.Formatter => formatter;
    void IConsoleAppHost.Close() => CloseApp();
    void IConsoleAppHost.BeginFilter() => FilterRequested?.Invoke();

    async Task IConsoleAppHost.RunAsync(string line)
    {
        CloseApp();
        await RunCommandLineAsync(line, Prompt);
    }

    async Task<bool> IConsoleAppHost.OpenAsync(ChatTarget target)
    {
        var opened = false;
        await RunEchoed(Prompt, "/open " + ConsoleLineFormatter.Typed(target.Title), async () =>
        {
            Command("open");
            opened = await Safely(() => host.OpenAsync(target.DisplayId));
            if (!opened) Fail("Couldn't open that conversation.");
        });
        return opened;
    }

    void IConsoleAppHost.SetTarget(ChatTarget target)
    {
        CloseApp();
        _ = RunEchoed(Prompt, "/to " + ConsoleLineFormatter.Typed(target.Title), () => { Command("to"); Choose(target, null); return Task.CompletedTask; });
    }

    async Task<string> IConsoleAppHost.ReadAsync(ChatTarget target)
    {
        var said = "";
        await RunEchoed(Prompt, "/read " + ConsoleLineFormatter.Typed(target.Title), async () =>
        {
            Command("read");
            var capture = new RunState { Capture = [] };
            var outer = running.Value;
            running.Value = capture;
            try { await MarkReadAsync(target); }
            finally { running.Value = outer; }
            foreach (var line in capture.Capture!) Emit(line);
            said = capture.Capture!.LastOrDefault()?.Text ?? "";
        });
        return said;
    }

    async Task<string> IConsoleAppHost.MuteAsync(ChatTarget target, bool muted)
    {
        var said = "";
        await RunEchoed(Prompt, (muted ? "/mute " : "/unmute ") + ConsoleLineFormatter.Typed(target.Title), async () =>
        {
            Command(muted ? "mute" : "unmute");
            var capture = new RunState { Capture = [] };
            var outer = running.Value;
            running.Value = capture;
            try { await MuteTargetAsync(target, muted); }
            finally { running.Value = outer; }
            foreach (var line in capture.Capture!) Emit(line);
            said = capture.Capture!.LastOrDefault()?.Text ?? "";
        });
        return said;
    }

    bool IConsoleAppHost.IsMuted(ChatTarget target)
    {
        lock (gate) return mutedHere.TryGetValue(target.DisplayId, out var here) ? here : target.Members.Any(m => m.IsMuted);
    }
}
