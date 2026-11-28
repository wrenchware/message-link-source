using System.Globalization;
using System.Text;

namespace MessageLink.Presentation.Services;

public enum TrayCommand { Open, OpenChat, OpenUnread, NewChat, Console, PauseHour, PauseMorning, Resume, Settings, Quit, ServerPage, StopServer }

// One entry of the notification-area menu. Id 0 is never chosen: separators, the "Pause notifications" submenu's own
// row and the disabled line saying until when notifications are paused all carry 0, and their Command means nothing.
public sealed record TrayMenuItem(int Id, string Text, TrayCommand Command, string? ChatId = null, bool Enabled = true,
    bool IsDefault = false, bool IsSeparator = false, IReadOnlyList<TrayMenuItem>? Children = null);

// The tray menu, built from the state at the moment it opens. Pure: the window hands it the unread list's in-memory
// snapshot, and nothing here reads a provider or marks anything read. The list it returns is a snapshot too, so a
// conversation's row still names that conversation however the unread list changes while the menu is open.
public static class TrayMenuModel
{
    public const int OpenId = 1, QuitId = 2, NewChatId = 3, ConsoleId = 4, SettingsId = 5;
    public const int PauseHourId = 10, PauseMorningId = 11, ResumeId = 12, OpenUnreadId = 13, ServerPageId = 14, StopServerId = 15, FirstChatId = 100;
    public const int UnreadShown = 5, TitleLength = 40;

    public static IReadOnlyList<TrayMenuItem> Build(IReadOnlyList<(string ChatId, string Title, string Service)> unread,
        int unreadTotal, bool notificationsOn, DateTimeOffset? pausedUntil, bool consoleAvailable, DateTimeOffset nowLocal, bool ownServer = false)
        => Build(unread, unreadTotal, notificationsOn, pausedUntil, consoleAvailable, nowLocal, TimeZoneInfo.Local, ownServer);

    /// <param name="ownServer">This PC runs its own Message Link Server: the menu offers its page, "Close Message Link" and "Quit Message Link and server".</param>
    public static IReadOnlyList<TrayMenuItem> Build(IReadOnlyList<(string ChatId, string Title, string Service)> unread,
        int unreadTotal, bool notificationsOn, DateTimeOffset? pausedUntil, bool consoleAvailable, DateTimeOffset now, TimeZoneInfo zone, bool ownServer = false)
    {
        var items = new List<TrayMenuItem>
        {
            new(OpenId, "Open Message Link", TrayCommand.Open, IsDefault: true),
            Separator(),
        };
        var shown = Math.Min(unread.Count, UnreadShown);
        for (var i = 0; i < shown; i++)
            items.Add(new(FirstChatId + i, MenuText(unread[i].Title, unread[i].Service), TrayCommand.OpenChat, unread[i].ChatId));
        var more = MoreCount(unread.Count, unreadTotal);
        if (more > 0) items.Add(new(OpenUnreadId, $"{more} more unread…", TrayCommand.OpenUnread));
        if (shown > 0 || more > 0) items.Add(Separator());
        items.Add(new(NewChatId, "New chat", TrayCommand.NewChat));
        if (consoleAvailable) items.Add(new(ConsoleId, "Open console", TrayCommand.Console));
        if (notificationsOn)
        {
            if (pausedUntil is { } until)
            {
                var time = TimeZoneInfo.ConvertTime(until, zone).ToString("t", CultureInfo.CurrentCulture);
                items.Add(new(0, $"Notifications paused until {time}", TrayCommand.Resume, Enabled: false));
                items.Add(new(ResumeId, "Resume notifications", TrayCommand.Resume));
            }
            else
            {
                items.Add(new(0, "Pause notifications", TrayCommand.PauseHour, Children:
                [
                    new(PauseHourId, "For 1 hour", TrayCommand.PauseHour),
                    new(PauseMorningId, NotificationPause.MorningLabel(now, zone), TrayCommand.PauseMorning),
                ]));
            }
        }
        items.Add(new(SettingsId, "Settings", TrayCommand.Settings));
        // A PC that runs its own Message Link Server (owner, 2026-11-22, replacing "Quit" and "Stop Messages Server"): the words say what keeps running.
        // Close ends the app only, and the server goes on serving the phone and keeping Google signed in, under an icon of its own (ServerMenu); "Quit Message
        // Link and server" ends both, and the icon goes. The page is the server's own, on this PC.
        if (ownServer) items.Add(new(ServerPageId, "Manage server", TrayCommand.ServerPage));
        items.Add(Separator());
        items.Add(new(QuitId, QuitText(ownServer), TrayCommand.Quit));
        if (ownServer) items.Add(new(StopServerId, QuitBothText, TrayCommand.StopServer));
        return items;
    }

    /// <summary>The app's own quit: "Close Message Link" where this PC's server keeps running after it, "Quit Message Link" where there is none here.</summary>
    public static string QuitText(bool ownServer) => ownServer ? "Close Message Link" : "Quit Message Link";

    public const string QuitBothText = "Quit Message Link and server";

    /// <summary>
    /// The menu of the server's own icon, shown while it runs with no Message Link window open (owner, 2026-11-22): open the app, its page, or end the server.
    /// Manage server is greyed until the page is up, as it is for the first moments of a start. Ids are the app menu's, so a choice means the same thing in both.
    /// </summary>
    public static IReadOnlyList<TrayMenuItem> ServerMenuFor(bool pageReady) =>
    [
        new(OpenId, "Open Message Link", TrayCommand.Open, IsDefault: true),
        new(ServerPageId, "Manage server", TrayCommand.ServerPage, Enabled: pageReady),
        Separator(),
        new(StopServerId, "Quit Message Link Server", TrayCommand.StopServer),
    ];

    /// <summary>
    /// The server icon's tooltip: that it runs, and how many accounts are connected (a count, never a name). <paramref name="connected"/> is null until the
    /// server knows its accounts' state.
    /// </summary>
    public static string ServerTip(int? connected) => connected switch
    {
        null => "Message Link Server is running",
        0 => "Message Link Server is running · no accounts connected",
        1 => "Message Link Server is running · 1 account connected",
        var count => $"Message Link Server is running · {count} accounts connected",
    };

    // "N more unread…" counts the unread conversations the menu does not list, shown only past the five it does.
    public static int MoreCount(int listed, int unreadTotal)
        => unreadTotal > UnreadShown ? Math.Max(0, unreadTotal - Math.Min(listed, UnreadShown)) : 0;

    // The menu before anything else is known, and whenever building one fails: Open, and the app's own quit, named for what keeps running after it.
    public static IReadOnlyList<TrayMenuItem> Fallback { get; } = FallbackFor(ownServer: false);

    public static IReadOnlyList<TrayMenuItem> FallbackFor(bool ownServer) =>
    [
        new(OpenId, "Open Message Link", TrayCommand.Open, IsDefault: true),
        Separator(),
        new(QuitId, QuitText(ownServer), TrayCommand.Quit),
    ];

    // The chosen entry, from the list the menu was drawn from. Id 0 is never an answer.
    public static TrayMenuItem? Find(IReadOnlyList<TrayMenuItem> items, int id)
    {
        if (id == 0) return null;
        foreach (var item in items)
        {
            if (item.Children is { } children) { if (Find(children, id) is { } found) return found; continue; }
            if (!item.IsSeparator && item.Enabled && item.Id == id) return item;
        }
        return null;
    }

    // A conversation's row: its title, clipped, then a tab and its service, which Windows right-aligns. Every "&" is
    // doubled so a name never grows a keyboard mnemonic, and tabs and control characters are taken out (a run of them
    // becomes one space) so a title can neither move the service column nor break the line.
    public static string MenuText(string title, string service)
    {
        var clean = Clean(title);
        if (clean.Length == 0) clean = "Conversation";
        return Escape(Clip(clean, TitleLength)) + "\t" + Escape(Clean(service));
    }

    private static TrayMenuItem Separator() => new(0, "", TrayCommand.Open, IsSeparator: true);

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new StringBuilder(value.Length);
        var gap = false;
        foreach (var c in value)
        {
            if (char.IsControl(c)) { gap = true; continue; }
            if (gap && text.Length > 0) text.Append(' ');
            gap = false;
            text.Append(c);
        }
        return text.ToString().Trim();
    }

    // Counted in text elements, so a surrogate pair, a combining mark or a joined emoji is never cut (as
    // NotificationObserver.Clip does for a toast).
    private static string Clip(string value, int length)
    {
        var elements = StringInfo.ParseCombiningCharacters(value);
        return elements.Length <= length ? value : value[..elements[length]] + "…";
    }

    private static string Escape(string value) => value.Replace("&", "&&", StringComparison.Ordinal);
}
