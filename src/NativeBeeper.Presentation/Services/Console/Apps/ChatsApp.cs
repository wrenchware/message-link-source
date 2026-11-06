using System.Globalization;
using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// /chats: the conversation list as a terminal browser. Reads the list this PC already holds; opening, choosing a target,
/// muting and marking read are the same explicit actions as their commands, and marking read asks first, since one stray
/// key would otherwise tell a real person you had read them.
/// </summary>
public sealed class ChatsApp : ConsoleApp
{
    public static readonly IReadOnlyList<string> Views = ["all", "unread", "pinned", "muted", "archived"];
    private static readonly (string Network, string Word)[] Services =
        [("Google Messages", "google"), ("Messenger", "messenger"), ("Instagram", "instagram"), ("Google Voice", "voice")];

    private int view;
    private string? service;
    private int selected, top, page = 10;
    private IReadOnlyList<ChatTarget> rows = [];
    private ChatTarget? confirmRead;

    public ChatsApp(IConsoleAppHost shell, string argument = "") : base(shell)
    {
        var word = argument.Trim().ToLowerInvariant();
        if (Views.ToList().IndexOf(word) is var found and >= 0) view = found;
        else if (Services.FirstOrDefault(s => s.Word == word || ConversationTargets.ServiceOf(word) == s.Network) is { Network: { } network }) service = network;
        else if (word.Length > 0) SetFilter(argument.Trim());
        Reload();
    }

    public override string Name => "chats";
    public override bool CanFilter => true;
    public override TimeSpan Refresh => TimeSpan.FromSeconds(2);
    public ChatTarget? Selected => selected < rows.Count ? rows[selected] : null;
    public IReadOnlyList<ChatTarget> Rows => rows;

    public override void Tick() => Reload();
    protected override void FilterChanged() { selected = 0; top = 0; Reload(); }

    private static bool Unread(ChatTarget chat) => chat.Members.Any(m => m.UnreadCount > 0 || m.IsMarkedUnread);

    private void Reload()
    {
        var keep = Selected?.DisplayId;
        rows = [.. Shell.Targets.Entries().Where(chat => Views[view] switch
            {
                "unread" => Unread(chat),
                "pinned" => chat.Members.Any(m => m.IsPinned),
                "muted" => Shell.IsMuted(chat),
                "archived" => chat.Members.Any(m => m.IsArchived),
                _ => !chat.Members.All(m => m.IsArchived),
            })
            .Where(chat => service is null || chat.Networks.Contains(service))
            .Where(chat => Filter.Length == 0 || chat.Title.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) ||
                chat.Label.Contains(Filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(chat => chat.Members.Any(m => m.IsPinned))
            .ThenByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue)];
        if (keep is not null && rows.ToList().FindIndex(r => r.DisplayId == keep) is var at and >= 0) selected = at;
        selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
    }

    public override bool Key(ConsoleKeyPress key)
    {
        Message = null;
        if (confirmRead is { } pending)
        {
            confirmRead = null;
            if (key.Is('y')) { _ = ReadAsync(pending); return true; }
            Message = "Not marked read.";
            return true;
        }
        if (Move(key, selected, rows.Count, page) is { } next) { selected = next; return true; }
        switch (key.Kind)
        {
            case ConsoleKeyKind.Tab: view = (view + 1) % Views.Count; selected = 0; Reload(); return true;
            case ConsoleKeyKind.BackTab: view = (view + Views.Count - 1) % Views.Count; selected = 0; Reload(); return true;
            case ConsoleKeyKind.Enter when Selected is { } chat: _ = OpenAsync(chat); return true;
        }
        if (key.Kind != ConsoleKeyKind.Char) return false;
        switch (key.Char)
        {
            case >= '1' and <= '4':
                var picked = Services[key.Char - '1'].Network;
                service = service == picked ? null : picked;
                selected = 0; Reload();
                return true;
            case '0': service = null; Reload(); return true;
            case '/': Shell.BeginFilter(); return true;
            case 't' when Selected is { } chat: Shell.SetTarget(chat); return true;
            case 'i' when Selected is { } chat: _ = Shell.RunAsync("/thread info " + Reference(chat)); return true;
            case 's' when Selected is { } chat: _ = Shell.RunAsync("/thread stats " + Reference(chat)); return true;
            case 'R' or 'r' when Selected is { } chat:
                if (!Unread(chat)) { Message = $"Nothing unread in {chat.Title}."; return true; }
                confirmRead = chat;
                Message = $"Mark {chat.Title} read on {string.Join(" and ", chat.Networks.Select(ConsoleLineFormatter.Tag).Where(t => t.Length > 0))}? It tells them. y/n";
                return true;
            case 'm' when Selected is { } chat: _ = MuteAsync(chat); return true;
        }
        return false;
    }

    public override void Click(int row, bool twice)
    {
        var index = top + row - 1;
        if (index < 0 || index >= rows.Count) return;
        selected = index;
        if (twice) _ = OpenAsync(rows[index]);
    }

    // The command a row stands for, typed back by number so a name two conversations share still means this one.
    private string Reference(ChatTarget chat) => ConsoleLineFormatter.Typed(chat.Title) +
        (Shell.Targets.Entries().Count(t => t.Title.Equals(chat.Title, StringComparison.CurrentCultureIgnoreCase)) > 1 && chat.Networks.Count == 1
            ? "@" + ConsoleSender.ServiceWord(chat.Networks[0]) : "");

    private async Task OpenAsync(ChatTarget chat)
    {
        Message = await Shell.OpenAsync(chat) ? $"Opened {chat.Title} in Messages." : $"Couldn't open {chat.Title}.";
        Invalidate();
    }

    private async Task ReadAsync(ChatTarget chat)
    {
        Message = await Shell.ReadAsync(chat);
        Reload();
        Invalidate();
    }

    private async Task MuteAsync(ChatTarget chat)
    {
        Message = await Shell.MuteAsync(chat, !Shell.IsMuted(chat));
        Reload();
        Invalidate();
    }

    public override ConsoleScreen Render(int width, int height)
    {
        var now = Shell.Clock.GetUtcNow();
        var title = new List<ConsoleSpan> { new("chats", ConsoleTone.Accent), new("  ") };
        foreach (var (name, i) in Views.Select((v, i) => (v, i)))
            title.Add(new ConsoleSpan(name + " ", i == view ? ConsoleTone.Strong : ConsoleTone.Dim));
        title.Add(new ConsoleSpan(" · " + (service is null ? "every service" : ConsoleLineFormatter.Tag(service)), ConsoleTone.Dim));
        if (Filter.Length > 0) title.Add(new ConsoleSpan(" · /" + Filter, ConsoleTone.Match));
        title.Add(new ConsoleSpan($" · {rows.Count.ToString(CultureInfo.CurrentCulture)}", ConsoleTone.Dim));

        page = Math.Max(1, height - 1);
        top = Scroll(top, selected, rows.Count, page);
        var preview = width >= 100;
        var table = preview
            ? new ConsoleTable(new("#", 4, Right: true), new("SERVICE", 10), new("NAME", 26), new("NEW", 3, Right: true), new("LAST", 4, Right: true),
                new("FLAGS", 5), new("PREVIEW", 0, Flex: true))
            : new ConsoleTable(new("#", 4, Right: true), new("SERVICE", 10), new("NAME", 0, Flex: true), new("NEW", 3, Right: true),
                new("LAST", 4, Right: true), new("FLAGS", 5));
        for (var index = top; index < Math.Min(rows.Count, top + page); index++)
        {
            var chat = rows[index];
            var first = chat.Members.FirstOrDefault();
            var tags = string.Join("/", chat.Networks.Select(ConsoleLineFormatter.Tag).Where(t => t.Length > 0).Distinct());
            var unread = chat.Members.Sum(m => m.UnreadCount);
            var marked = chat.Members.Any(m => m.IsMarkedUnread);
            var flags = (chat.Members.Any(m => m.IsPinned) ? "*" : "·") + (Shell.IsMuted(chat) ? "m" : "·") + (chat.Members.Any(m => m.IsArchived) ? "a" : "·") +
                (chat.IsGroup ? "g" : "·") + (chat.Members.Count > 1 ? "l" : "·");
            var cells = new List<ConsoleSpan>
            {
                new((index + 1).ToString(CultureInfo.InvariantCulture), ConsoleTone.Dim),
                ConsoleSpan.For(tags.Length == 0 ? "—" : tags, chat.Networks.Count == 1 ? BubbleColours.TagFamily(chat.Networks[0], first?.ExpectedTransport) : null),
                new(chat.Title, unread > 0 || marked ? ConsoleTone.Strong : ConsoleTone.Default),
                new(unread > 0 ? unread.ToString(CultureInfo.InvariantCulture) : marked ? "•" : "", ConsoleTone.Accent),
                new(ConsoleCells.Age(chat.LastActivity, now), ConsoleTone.Dim),
                new(flags, ConsoleTone.Dim),
            };
            if (preview)
            {
                var newest = chat.Members.Where(m => m.Preview is not null).OrderByDescending(m => m.Preview!.Timestamp).FirstOrDefault()?.Preview;
                cells.Add(new(newest is null ? "" : (newest.IsOutgoing ? "You: " : "") + ConsoleLineFormatter.Words(newest), ConsoleTone.Dim));
            }
            table.AddRow(index == selected, [.. cells]);
        }
        var body = table.Render(width).Select(row => new ConsoleRow(row.Spans, row.Selected)).ToList();
        if (rows.Count == 0) body.Add(ConsoleRow.Of(Filter.Length > 0 ? $"No conversation matches “{Filter}”." : "Nothing here.", ConsoleTone.Dim));
        return Screen(title, Fill(body, height), ("↑↓", "move"), ("⏎", "open"), ("t", "target"), ("i", "info"), ("s", "stats"), ("r", "read"),
            ("m", "mute"), ("/", "filter"), ("Tab", "view"), ("1-4", "service"), ("q", "quit"));
    }
}
