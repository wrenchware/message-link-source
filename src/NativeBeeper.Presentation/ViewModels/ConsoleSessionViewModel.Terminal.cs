using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// The terminal's own commands that read state: /status, /ls, /to, /route, /commands, /help and completion.
public sealed partial class ConsoleSessionViewModel
{
    /// <summary>Every command word Tab completes, aliases included.</summary>
    public static IReadOnlyList<string> TerminalCommands => ConsoleCatalog.Words;

    private static readonly string[] Fortunes =
    [
        "There is no place like 127.0.0.1.", "Your social daemon is running. Your chores daemon is not.",
        "The most productive command is sometimes /pause.", "A watched inbox never pings. An unwatched one has 47 messages.",
        "No carrier pigeons were harmed in the delivery of this message.", "Life is too short for an uncompleted command.",
        "It works on my machine. Your machine is not my machine.", "Read receipts are a social contract. /pause is a loophole.",
        "Any sufficiently advanced group chat is indistinguishable from a meeting.", "rm -rf /regrets: permission denied.",
        "Have you tried turning it off and on again? (Don't. The helpers resume themselves.)", "Inbox zero is a state of mind, not a number.",
        "The quickest reply is a well-chosen silence.", "In the beginning there was the command line. Then someone added emoji.",
        "You are not behind on messages. The messages are ahead of you.", "Today's lucky alias: /alias calm=/pause morning",
        "640K ought to be enough for anybody. Your Messenger cache disagrees.", "There are 10 kinds of people: those who read receipts, and those who don't.",
        "A clean scroll is a happy scroll. Ctrl+L.", "SELECT * FROM friends WHERE replied = false; -- 0 rows. Liar.",
    ];

    /// <summary>Window actions with no app of their own: "latest" scrolls to the end and follows.</summary>
    public event Action<string>? ViewRequested;
    public ConsoleSystemStatus SystemStatus => host.SystemStatus;
    public string WelcomeStatus
    {
        get
        {
            var chats = targets.Entries();
            var unread = chats.Count(c => c.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread));
            return $"{chats.Count.ToString(CultureInfo.CurrentCulture)} chats · {unread.ToString(CultureInfo.CurrentCulture)} unread · {host.SystemStatus.PendingSends.ToString(CultureInfo.CurrentCulture)} pending sends";
        }
    }

    /// <summary>Conversations with something unread, for the status line.</summary>
    public int UnreadChats => targets.Entries().Count(c => c.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread));

    /// <summary>Tab's choices while it cycles, and which is in the line now; empty when Tab is not cycling.</summary>
    public IReadOnlyList<string> CompletionChoices { get { lock (gate) return completion?.Choices ?? []; } }
    public int CompletionIndex { get { lock (gate) return completion?.Index ?? -1; } }

    /// <summary>Newest matching input first; no I/O, execution or target changes. History stays in this session.</summary>
    public string? FindInputHistory(string text, int skip = 0)
    {
        lock (gate) return history.AsEnumerable().Reverse().Where(h => h.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Skip(Math.Max(0, skip)).FirstOrDefault();
    }

    /// <summary>The newest earlier input that begins with what is typed, for the prompt's grey suggestion (fish's
    /// autosuggestion): the rest of it, or null. Only a command is suggested, never a message.</summary>
    public string? Suggestion(string typed)
    {
        if (typed.Length < 2 || !typed.StartsWith('/') || typed.Contains('\n')) return null;
        lock (gate)
            return history.AsEnumerable().Reverse()
                .FirstOrDefault(h => h.Length > typed.Length && h.StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                    !ConsoleCatalog.StartsWithSend(h) && !ConsoleCatalog.StartsWithAct(h))?[typed.Length..];
    }

    /// <summary>The synopsis and summary of the command being typed, for the line under the prompt; null when there is none.</summary>
    public (string Synopsis, string Summary)? Synopsis(string typed)
    {
        var text = typed.TrimStart();
        // With no one selected, a command typed without its slash runs, so its synopsis shows too.
        if (!text.StartsWith('/') && text.Length > 0 && Target is null && CommandWord(text) is not null) text = "/" + text;
        if (!text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal)) return null;
        var (word, _) = SplitWord(text.Split('|', ';')[^1].Trim() is { Length: > 0 } last && last.StartsWith('/') ? last : text);
        if (ConsoleCatalog.Find(word) is { } info) return (info.Synopsis, info.Summary);
        if (Aliases.TryGetValue(word, out var expansion)) return ("/" + word, "alias for " + expansion);
        return null;
    }

    private (int Start, IReadOnlyList<string> Choices)? TerminalCompletion(string before)
    {
        if (before.StartsWith('/') && !before.StartsWith("//", StringComparison.Ordinal) && !before.Any(char.IsWhiteSpace))
            return (1, [.. TerminalCommands.Concat(Aliases.Keys).Where(c => c.StartsWith(before[1..], StringComparison.OrdinalIgnoreCase)).Distinct().Order(StringComparer.Ordinal)]);
        // After a pipe, the filters.
        var pipe = before.LastIndexOf('|');
        if (pipe >= 0 && before[(pipe + 1)..].TrimStart() is var filter && !filter.Any(char.IsWhiteSpace))
            return (before.Length - filter.Length, [.. ConsoleShell.FilterNames.Where(f => f.StartsWith(filter, StringComparison.OrdinalIgnoreCase))]);
        foreach (var (prefix, words) in new (string, IReadOnlyList<string>)[]
        {
            ("/ls chats ", ["all", "unread", "pinned", "muted", "archived"]),
            ("/help ", [.. ConsoleCatalog.Categories.Concat(TerminalCommands).Distinct()]), ("/man ", TerminalCommands), ("/unalias ", [.. Aliases.Keys]),
            ("/logs ", host.LogSources.Count > 0 ? [.. host.LogSources.Select(s => s.Split(' ')[0].ToLowerInvariant()).Distinct()] : ConsoleCatalog.Find("logs")!.Options!),
        })
        {
            if (!before.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var typed = before[prefix.Length..];
            if (typed.Any(char.IsWhiteSpace)) return null;
            return (prefix.Length, [.. words.Where(w => w.StartsWith(typed, StringComparison.OrdinalIgnoreCase))]);
        }
        // A message command: its message (^, ^2, the last /msgs numbers), then for /react a reaction.
        var parts = before.Split(' ');
        if (parts.Length is 2 or 3 && parts[0].StartsWith('/') && parts[0][1..].ToLowerInvariant() is var verb &&
            verb is "react" or "unreact" or "like" or "edit" or "unsend" or "copy" or "save" or "remind" or "reply")
        {
            if (parts.Length == 2)
                return (before.Length - parts[1].Length, [.. MessageRefWords().Where(w => w.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase))]);
            if (verb is "react" or "unreact") return (before.Length - parts[2].Length, [.. ConsoleCatalog.Reactions.Where(r => r.StartsWith(parts[2], StringComparison.Ordinal))]);
            if (verb == "remind") return (before.Length - parts[2].Length, [.. new[] { "1h", "3h", "tonight", "tomorrow" }.Where(r => r.StartsWith(parts[2], StringComparison.OrdinalIgnoreCase))]);
            return null;
        }
        // A command's own options, from the catalogue.
        var space = before.IndexOf(' ');
        if (space > 1 && ConsoleCatalog.Find(before[1..space]) is { Options: { } options })
        {
            var typed = before[(space + 1)..];
            if (typed.Any(char.IsWhiteSpace)) return null;
            return (space + 1, [.. options.Where(o => o.StartsWith(typed, StringComparison.OrdinalIgnoreCase))]);
        }
        return null;
    }

    // ---- /status ----

    private void PrintStatus()
    {
        Command("status");
        var status = host.SystemStatus;
        var now = clock.GetUtcNow();
        Out(new ConsoleSpan("Messages " + status.Version, ConsoleTone.Strong), new ConsoleSpan(" · " + WelcomeStatus, ConsoleTone.Dim));
        if (status.Processes.Count > 0)
        {
            var chats = targets.Entries();
            var table = new ConsoleTable(new("SERVICE", 15), new("STATE", 15), new("HELPER", 0), new("EVENT", 5, Right: true),
                new("SENT", 5, Right: true), new("CHATS", 5, Right: true), new("UNREAD", 6, Right: true));
            foreach (var process in status.Processes)
            {
                var network = process.Network.Length > 0 ? process.Network : TopApp.NetworkOf(process.Name);
                var mine = chats.Where(c => c.Networks.Contains(network)).ToArray();
                var unread = mine.Count(c => c.Members.Any(m => m.Network == network && (m.UnreadCount > 0 || m.IsMarkedUnread)));
                var helper = process.Running ? "up " + (process.Uptime is { } up ? ConsoleCells.Uptime(up) : "?") : "stopped";
                if (process.Restarts > 0) helper += $" · {process.Restarts.ToString(CultureInfo.InvariantCulture)} restart" + (process.Restarts == 1 ? "" : "s");
                if (!process.Running && process.LastExit is { } exit) helper += " · exit " + exit.ToString(CultureInfo.InvariantCulture);
                table.Add(ConsoleSpan.For(process.Name, BubbleColours.TagFamily(network, null)), TopApp.State(process),
                    new(helper, process.Restarts > 0 ? ConsoleTone.Warn : ConsoleTone.Dim), new(ConsoleCells.Age(process.LastEvent, now)),
                    new(ConsoleCells.Age(process.LastSend, now), ConsoleTone.Dim), new(mine.Length.ToString(CultureInfo.InvariantCulture), ConsoleTone.Dim),
                    new(unread.ToString(CultureInfo.InvariantCulture), unread > 0 ? ConsoleTone.Strong : ConsoleTone.Dim));
            }
            Rows(table.Render(Columns));
        }
        var label = new Func<string, ConsoleSpan>(text => new ConsoleSpan(text.PadRight(9), ConsoleTone.Dim));
        Out(label("target"), Target is { } to ? ConsoleSpan.For(to.Title, BubbleColours.TagFamily(to.Network, null)) : new ConsoleSpan("none", ConsoleTone.Dim),
            new ConsoleSpan(Target is { } t ? " · " + t.Service : " · /to name chooses one", ConsoleTone.Dim));
        Out(label("notify"), new ConsoleSpan(host.PauseText ?? (host.NotificationsOn ? "Notifications: on" : "Notifications: off"),
            host.PauseText is not null ? ConsoleTone.Warn : ConsoleTone.Default));
        Out(label("outbox"), new ConsoleSpan($"{status.PendingSends.ToString(CultureInfo.CurrentCulture)} in flight · {status.Scheduled.ToString(CultureInfo.CurrentCulture)} scheduled",
            status.PendingSends > 0 ? ConsoleTone.Warn : ConsoleTone.Default));
        int kept;
        lock (gate) kept = history.Count;
        Out(label("console"), new ConsoleSpan("Events: " + Verbosity.ToString().ToLowerInvariant() + $" · filter {(Hint ?? "none")} · {kept.ToString(CultureInfo.CurrentCulture)} inputs kept in memory", ConsoleTone.Dim));
    }

    // ---- /ls ----

    private void ListChats(string filter)
    {
        Command("chats");
        var word = filter.Trim().ToLowerInvariant();
        var network = ConversationTargets.ServiceOf(word) is { } label ? label.Split(" · ")[0] : null;
        var rows = targets.Entries().Where(c => word switch
        {
            "" or "all" or "*" => !c.Members.All(m => m.IsArchived),
            "unread" => c.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread),
            "pinned" => c.Members.Any(m => m.IsPinned),
            "muted" => mutedHere.GetValueOrDefault(c.DisplayId, c.Members.Any(m => m.IsMuted)),
            "archive" or "archived" => c.Members.Any(m => m.IsArchived),
            _ when network is not null => c.Networks.Contains(network),
            _ => c.Title.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase),
        }).OrderByDescending(c => c.Members.Any(m => m.IsPinned)).ThenByDescending(c => c.LastActivity ?? DateTimeOffset.MinValue).ToArray();
        var shown = Capturing ? rows : rows.Take(50).ToArray();
        lock (gate) refs = [.. shown.Select(chat => new ConsoleRef(chat, null))];
        if (rows.Length == 0) { Out("No matching chats on this PC.", ConsoleTone.Dim); return; }
        var now = clock.GetUtcNow();
        var table = new ConsoleTable(new("#", 3, Right: true), new("FLAGS", 4), new("SERVICE", 0), new("NAME", 0, Flex: true),
            new("NEW", 3, Right: true), new("LAST", 4, Right: true));
        for (var i = 0; i < shown.Length; i++)
        {
            var chat = shown[i];
            var unread = chat.Members.Sum(m => m.UnreadCount);
            var marked = chat.Members.Any(m => m.IsMarkedUnread);
            var flags = (chat.Members.Any(m => m.IsPinned) ? "*" : " ") + (unread > 0 || marked ? "u" : " ") +
                (mutedHere.GetValueOrDefault(chat.DisplayId, chat.Members.Any(m => m.IsMuted)) ? "m" : " ") + (chat.Members.Any(m => m.IsArchived) ? "a" : " ");
            var tags = string.Join("/", chat.Networks.Select(ConsoleLineFormatter.Tag).Where(t => t.Length > 0).Distinct());
            table.Add(new((i + 1).ToString(CultureInfo.InvariantCulture), ConsoleTone.Accent), new(flags, ConsoleTone.Dim),
                ConsoleSpan.For(tags, chat.Networks.Count == 1 ? BubbleColours.TagFamily(chat.Networks[0], null) : null),
                new(Choice(chat), unread > 0 || marked ? ConsoleTone.Strong : ConsoleTone.Default),
                new(unread > 0 ? unread.ToString(CultureInfo.InvariantCulture) : marked ? "•" : "", ConsoleTone.Accent),
                new(ConsoleCells.Age(chat.LastActivity, now), ConsoleTone.Dim));
        }
        Rows(table.Render(Columns, header: false));
        Out($"{rows.Length.ToString(CultureInfo.CurrentCulture)} chats" + (shown.Length < rows.Length ? $", first {shown.Length.ToString(CultureInfo.CurrentCulture)} shown" : "") +
            " · * pinned · u unread · m muted · a archived · /to N, /open N, /thread N", ConsoleTone.Dim);
    }

    // ---- /to and /route ----

    private void ChooseTarget(string? name)
    {
        Command("to");
        if (name is null) { ShowRoute(null); return; }
        if (name.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            lock (gate) { query = null; replyLock = null; newerHint = null; UpdatePrompt(); UpdateHint(); }
            Say(ConsoleLineKind.Info, "Recipient cleared."); return;
        }
        var (_, service) = ConsoleCommandParser.SplitName(name);
        if (Named(name) is not { } target) return;
        Choose(target, service);
    }

    private ConsoleRecipient? Choose(ChatTarget target, string? service)
    {
        ConsoleRoute route;
        lock (gate) route = sender.Route(target, service, NewestByDisplay);
        if (route is ConsoleRoute.To { Recipient: var to })
        {
            lock (gate) SetQuery(to, "query");
            Out(new ConsoleSpan("Recipient: ", ConsoleTone.Dim), ConsoleSpan.For(to.Title, BubbleColours.TagFamily(to.Network, null)),
                new ConsoleSpan(" · " + to.Service + ". Plain text sends here; /to off clears it.", ConsoleTone.Dim));
            return to;
        }
        if (route is ConsoleRoute.Refused { Message: var why }) Say(ConsoleLineKind.Error, why);
        return null;
    }

    private void ShowRoute(string? name)
    {
        Command("route");
        ChatTarget? target;
        string? service = null;
        if (name is null)
        {
            if (Target is not { } current) { Say(ConsoleLineKind.Info, "No recipient selected. Use /to name, or /route name."); return; }
            target = current.Target ?? targets.Entries().FirstOrDefault(e => e.DisplayId == current.DisplayChatId);
            service = current.Network.Length > 0 ? ConsoleSender.ServiceWord(current.Service) : null;
            if (target is null) { Out("Route: " + current.Title + " → " + current.Service); return; }
        }
        else
        {
            service = ConsoleCommandParser.SplitName(name).Service;
            target = Named(name);
            if (target is null) return;
        }
        Trace(target, service);
    }

    // traceroute for a message: each hop from the name typed to the outbox.
    private void Trace(ChatTarget target, string? service)
    {
        var now = clock.GetUtcNow();
        ConsoleRoute route;
        lock (gate) route = sender.Route(target, service, NewestByDisplay);
        Out(new ConsoleSpan("route to ", ConsoleTone.Dim), new ConsoleSpan(target.Title, ConsoleTone.Strong),
            new ConsoleSpan(target.Members.Count > 1 ? $" · linked across {target.Members.Count.ToString(CultureInfo.CurrentCulture)} conversations" : "", ConsoleTone.Dim));
        void Hop(int n, string what, params ConsoleSpan[] detail) =>
            Out([new ConsoleSpan($" {n.ToString(CultureInfo.InvariantCulture),2}  ", ConsoleTone.Accent), new ConsoleSpan(what.PadRight(9), ConsoleTone.Dim), .. detail]);
        var services = string.Join(", ", target.Networks.Where(n => n.Length > 0).Distinct());
        Hop(1, "name", new ConsoleSpan(target.Label), new ConsoleSpan($" · {(target.IsGroup ? "group" : "one-to-one")} · {services}", ConsoleTone.Dim));
        if (route is ConsoleRoute.Refused { Message: var why })
        {
            Hop(2, "choice", new ConsoleSpan("✗ " + why, ConsoleTone.Error));
            Out(" ⇒ nothing would be sent", ConsoleTone.Error);
            return;
        }
        var to = ((ConsoleRoute.To)route).Recipient;
        var source = target.Members.FirstOrDefault(m => m.Id == to.SourceChatId);
        var newest = NewestByDisplay(target.DisplayId);
        var reason = service is not null ? "as asked (@" + service + ")"
            : target.Members.Count(m => !m.IsReadOnly && !m.IsCallHistory) <= 1 ? "its only conversation"
            : newest is not null && newest.ChatId == to.SourceChatId ? "they last wrote here " + ConsoleCells.Age(now - newest.Timestamp) + " ago"
            : "the linked person's sending choice";
        var family = BubbleColours.TagFamily(to.Network, source?.ExpectedTransport);
        Hop(2, "choice", ConsoleSpan.For(to.Service, family), new ConsoleSpan(" · " + reason, ConsoleTone.Dim));
        var caps = source?.Capabilities;
        Hop(3, "chat", new ConsoleSpan(Tag(to.SourceChatId), ConsoleTone.Strong),
            new ConsoleSpan((source?.ExpectedTransport is { Length: > 0 } transport ? " · expects " + transport.ToUpperInvariant() : "") +
                (caps?.MaxTextLength is { } max ? " · up to " + max.ToString("N0", CultureInfo.CurrentCulture) + " characters" : "") +
                (source?.IsEncrypted == true ? " · end-to-end encrypted" : ""), ConsoleTone.Dim));
        var process = host.SystemStatus.Processes.FirstOrDefault(p => (p.Network.Length > 0 ? p.Network : TopApp.NetworkOf(p.Name)) == to.Network);
        if (process is not null)
            Hop(4, "service", TopApp.State(process), new ConsoleSpan((process.Running ? " · helper" + (process.ProcessId is { } pid ? " pid " + pid.ToString(CultureInfo.InvariantCulture) : "") +
                (process.Uptime is { } up ? " up " + ConsoleCells.Uptime(up) : "") : " · helper stopped") +
                (process.LastSend is { } sent ? " · last confirmed send " + ConsoleCells.Age(now - sent) + " ago" : ""), ConsoleTone.Dim));
        else Hop(4, "service", new ConsoleSpan(to.Network));
        var pending = host.Queue.Count(q => q.ChatId == to.SourceChatId && q.State is "sending" or "pending" or "unknown" or "held");
        Hop(5, "outbox", new ConsoleSpan($"{pending.ToString(CultureInfo.CurrentCulture)} in flight here", pending > 0 ? ConsoleTone.Warn : ConsoleTone.Default),
            new ConsoleSpan(" · one submission per Enter, never retried", ConsoleTone.Dim));
        var connected = process is null || process.State == "connected";
        Out([new ConsoleSpan(" ⇒ ", connected ? ConsoleTone.Ok : ConsoleTone.Warn), new ConsoleSpan("plain text to " + to.Title + " would go by "),
            ConsoleSpan.For(to.Service, family), new ConsoleSpan(connected ? "" : ", once it is connected again", ConsoleTone.Warn)]);
    }

    /// <summary>"messenger#a3f9": a conversation by service and a one-way tag, so routes can be compared without its id.</summary>
    public static string Tag(string chatId)
    {
        var colon = chatId.IndexOf(':');
        var scheme = colon > 0 ? chatId[..colon] : "chat";
        uint hash = 2166136261;
        foreach (var c in chatId) { hash ^= c; hash *= 16777619; }
        return scheme + "#" + (hash & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
    }

    // ---- /commands ----

    private void PrintCommands(string query)
    {
        Command("commands");
        (int Number, string Text)[] inputs;
        // The line that asked is left out; numbers are what !n runs.
        lock (gate) inputs = [.. history.SkipLast(1).Select((h, i) => (i + 1, h)).Where(h => h.h.Contains(query, StringComparison.OrdinalIgnoreCase))];
        if (inputs.Length == 0) { Out("No matching input history.", ConsoleTone.Dim); return; }
        foreach (var (number, text) in inputs)
            Out(new ConsoleSpan($"{number,5}  ", ConsoleTone.Dim), new ConsoleSpan(text.Replace("\n", " ↵ ")));
        Out("Kept in memory while Messages runs · !n runs one again · Ctrl+R searches", ConsoleTone.Dim);
    }

    // ---- /help ----

    private void PrintHelp(string? topic)
    {
        var wanted = topic?.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(wanted)) { HelpIndex(); return; }
        // A group of the index first: /apps is itself only the index, so "apps" means the group either way.
        if (ConsoleCatalog.Categories.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } category) { HelpCategory(category); return; }
        if (ConsoleCatalog.Find(wanted) is { } info) { Manual(info); return; }
        var found = ConsoleCatalog.Apropos(wanted);
        if (found.Count == 0) { Say(ConsoleLineKind.Error, $"Nothing in the manual about “{wanted}”. /help lists every command."); return; }
        foreach (var match in found)
            Out(new ConsoleSpan(ConsoleCells.Pad("/" + match.Name, 12), ConsoleTone.Accent), new ConsoleSpan(match.Summary));
    }

    // /help talk: one group of the index, each command's synopsis and what it does.
    private void HelpCategory(string category)
    {
        Out(new ConsoleSpan(ConsoleCatalog.CategoryTitle(category), ConsoleTone.Accent), new ConsoleSpan(" · /help command for its manual page", ConsoleTone.Dim));
        var commands = ConsoleCatalog.All.Where(c => c.Category == category).ToArray();
        var width = Math.Min(26, commands.Max(c => c.Synopsis.Length) + 2);
        foreach (var command in commands)
        {
            // A synopsis too long for the column has a line of its own, and the summary goes under it, at the column.
            if (command.Synopsis.Length + 2 > width)
            {
                Out(new ConsoleSpan("  "), new ConsoleSpan(command.Synopsis, ConsoleTone.Strong));
                Out(new string(' ', width + 2) + command.Summary);
            }
            else Out(new ConsoleSpan("  "), new ConsoleSpan(ConsoleCells.Pad(command.Synopsis, width), ConsoleTone.Strong), new ConsoleSpan(command.Summary));
        }
    }

    private void HelpIndex()
    {
        Out(new ConsoleSpan("Messages console", ConsoleTone.Strong), new ConsoleSpan(" · /help talk lists a group · /help command its manual · /help word searches", ConsoleTone.Dim));
        foreach (var category in ConsoleCatalog.Categories)
        {
            var names = ConsoleCatalog.All.Where(c => c.Category == category).Select(c => c.Name).ToArray();
            var lines = ConsoleShell.Wrap(string.Join("  ", names), Math.Max(20, Columns - 10));
            for (var i = 0; i < lines.Count; i++)
                Out([new ConsoleSpan(i == 0 ? ConsoleCells.Pad(ConsoleCatalog.CategoryTitle(category), 9) : new string(' ', 9), ConsoleTone.Accent),
                    .. lines[i].Split("  ").SelectMany((name, j) => new[] { new ConsoleSpan(j == 0 ? "" : "  "), new ConsoleSpan(name, ConsoleTone.Strong) })]);
        }
        Blank();
        (string Key, string Text)[] keys =
        [
            ("KEYS", "Tab complete · → take the grey suggestion · ↑↓ history · Ctrl+R search history · Ctrl+C drop the line"),
            ("", "Ctrl+L clear · PgUp/PgDn scroll · Esc clear the line, then the target · Ctrl+Plus/Minus zoom"),
            ("SHELL", "a ; b  both · a && b  b if a worked · cmd | grep x | head 5 | copy | less"),
            ("", "!! last command · !n command n · !word the last starting with word · /alias name=command"),
            ("NAMES", "a name, \"a name\", name@service, N from the last list, or . for what Messages has open"),
            ("MESSAGES", "m3 from the last /msgs, N from /search, ^ the newest from the person the prompt shows (^2 before it)"),
            ("", "cmd --help shows a command's manual without running it · no one selected: commands work without /"),
        ];
        foreach (var (key, text) in keys)
            Out(new ConsoleSpan(ConsoleCells.Pad(key, 9), ConsoleTone.Accent), new ConsoleSpan(text, ConsoleTone.Dim));
    }

    private void Manual(ConsoleCommandInfo info)
    {
        var head = info.Name.ToUpperInvariant() + "(1)";
        Out(new ConsoleSpan(head, ConsoleTone.Strong), new ConsoleSpan(new string(' ', Math.Max(2, Math.Min(Columns, 80) - head.Length * 2 - 16)) + "Messages console", ConsoleTone.Dim),
            new ConsoleSpan(new string(' ', 4) + head, ConsoleTone.Strong));
        void Section(string name) => Out(name, ConsoleTone.Accent);
        Section("NAME");
        Out("    " + info.Name + (info.Aliases is { Count: > 0 } a ? ", " + string.Join(", ", a) : "") + " — " + info.Summary);
        Section("SYNOPSIS");
        Out(new ConsoleSpan("    "), new ConsoleSpan(info.Synopsis, ConsoleTone.Strong));
        if (info.Description.Count > 0)
        {
            Section("DESCRIPTION");
            foreach (var line in info.Description) Out("    " + line);
        }
        if (info.Sends || info.Acts || info.Seen is not null)
        {
            Section("SAFETY");
            if (info.Seen is { } seen) Out("    " + seen, ConsoleTone.Warn);
            if (info.Sends) Out("    This sends a real message. It is never chained, piped, aliased or repeated by !!.", ConsoleTone.Warn);
            else if (info.Acts) Out("    It runs only typed on a line of its own: never chained, piped, aliased or repeated by !!.", ConsoleTone.Warn);
        }
        if (info.Examples.Count > 0)
        {
            Section("EXAMPLES");
            foreach (var example in info.Examples) Out(new ConsoleSpan("    "), new ConsoleSpan(example, ConsoleTone.Strong));
        }
        if (info.SeeAlso is { Count: > 0 } see)
        {
            Section("SEE ALSO");
            Out("    " + string.Join(", ", see.Select(s => "/" + s)), ConsoleTone.Dim);
        }
    }
}
