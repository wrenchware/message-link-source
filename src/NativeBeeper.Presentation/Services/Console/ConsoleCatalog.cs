namespace NativeBeeper.Presentation.Services;

/// <summary>
/// One console command as help, completion and the prompt's hint describe it. The catalogue is the one place a command's
/// words live, so /help, /man, Tab and the synopsis shown while typing can never disagree.
/// </summary>
/// <param name="Sends">The command can send a message to a real person; such a command is never chained, piped or
/// aliased.</param>
/// <param name="Options">Words Tab offers as the command's first argument.</param>
/// <param name="Names">Tab offers conversation names as the argument.</param>
/// <param name="Acts">The command changes a conversation or a message, on the service or on this PC: it runs only typed
/// on a line of its own, never chained, piped, aliased or repeated by !!.</param>
/// <param name="Seen">What the command does reaches other people (a reaction, an edit, an unsend), or your account on
/// the service; the manual page says so.</param>
public sealed record ConsoleCommandInfo(string Name, string Category, string Synopsis, string Summary,
    IReadOnlyList<string> Description, IReadOnlyList<string> Examples, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Options = null, bool Names = false, bool Sends = false, IReadOnlyList<string>? SeeAlso = null,
    bool Acts = false, string? Seen = null);

public static class ConsoleCatalog
{
    public static readonly IReadOnlyList<string> Categories = ["talk", "manage", "look", "apps", "system", "fun"];

    /// <summary>The reactions Tab offers after /react m3: the ones the services' pickers start with.</summary>
    public static readonly IReadOnlyList<string> Reactions = ["👍", "❤️", "😂", "😮", "😢", "😡", "👎"];

    public static string CategoryTitle(string category) => category switch
    {
        "talk" => "TALK",
        "manage" => "MANAGE",
        "look" => "LOOK",
        "apps" => "APPS",
        "system" => "SYSTEM",
        _ => "FUN",
    };

    private static readonly string[] ChatViews = ["all", "unread", "pinned", "muted", "archived", "google", "messenger", "instagram", "voice"];
    private static readonly string[] TopPages = ["overview", "workers", "queues", "cache", "perf"];

    public static IReadOnlyList<ConsoleCommandInfo> All { get; } =
    [
        // ---- talk ----
        new("r", "talk", "/r text", "reply to whoever wrote last",
            ["The recipient is fixed the moment you type \"/r \": whoever writes while you are typing, the reply still goes to",
             "the person shown in the prompt. /r on its own makes them the target without sending."],
            ["/r on my way", "/r"], Sends: true, SeeAlso: ["reply", "w", "to"]),
        new("reply", "talk", "/reply [message] text", "quote a message in your reply; without one, the same as /r",
            ["message is m3 from the last /msgs, ^ for the newest message from the person the prompt shows (^2 the one",
             "before), or #4 for search result 4. The reply goes to that message's conversation, quoting it, where its",
             "service can reply to a message. Without a message, /reply text is /r text."],
            ["/reply m3 see you there", "/reply ^ yes!", "/reply #2 found it"], Sends: true, SeeAlso: ["r", "msgs", "react"],
            Seen: "This sends a real message, quoting theirs, to everyone in that conversation."),
        new("w", "talk", "/w name [text]", "message someone; without text, make them the target",
            ["name may end in @service (@google, @messenger, @instagram, @voice, @voice:label) when a linked person is on",
             "several. Quote names with spaces when text follows: /w \"Alex Smith\" hi. A name that only begins a name is",
             "offered back rather than sent to."],
            ["/w Lauren running late", "/w \"Alex Smith\"@messenger hi", "/w Mom"], Aliases: ["msg", "query"], Names: true, Sends: true,
            SeeAlso: ["to", "route"]),
        new("to", "talk", "/to [name|N|.|off]", "choose who plain text goes to, without sending or opening",
            ["N is a number from the last /chats, /ls or /search list. . is the conversation open in Message Link.",
             "off clears the target. The prompt shows the target: messages:Lauren>"],
            ["/to Lauren", "/to 3", "/to .", "/to off"], Names: true, Options: ["off", "."], SeeAlso: ["route", "w"]),
        new("open", "talk", "/open [name|N|.]", "open a conversation in Message Link",
            ["Brings Message Link forward with the conversation. Opening is an ordinary open: reading it there marks it read as",
             "usual. With no name: the target, else whoever wrote last."],
            ["/open Lauren", "/open 2"], Names: true, SeeAlso: ["next", "chats"]),
        new("next", "talk", "/next", "open the newest conversation with unread messages",
            ["The same as /open on the newest unread conversation in the list. Nothing is marked read until you read it."],
            ["/next"], SeeAlso: ["open", "chats"]),
        new("read", "talk", "/read [name|N]", "mark a conversation read without opening it",
            ["Tells the service, as reading it would. With no name: the target, else whoever wrote last. Never retried."],
            ["/read", "/read Lauren"], Names: true),
        new("mute", "talk", "/mute name|N", "mute a conversation in Message Link", ["Local only; /unmute undoes it. /filter muted shows muted lines."],
            ["/mute \"Soccer parents\""], Names: true, SeeAlso: ["unmute", "filter"]),
        new("unmute", "talk", "/unmute name|N", "unmute a conversation in Message Link", [], ["/unmute \"Soccer parents\""], Names: true),
        new("react", "talk", "/react message emoji", "react to a message",
            ["message is m3 from the last /msgs, ^ for the newest message from the person the prompt shows (^2 the one",
             "before), or a /search result number. emoji is one the service offers for it (Tab offers the usual ones), or",
             "its name: /react m3 heart. Where the service can't react, nothing is sent and the console says so."],
            ["/react m3 👍", "/react ^ ❤️", "/react 2 laugh"], SeeAlso: ["like", "unreact", "msgs"], Acts: true,
            Seen: "Everyone in the conversation sees your reaction."),
        new("unreact", "talk", "/unreact message [emoji]", "take back your reaction to a message",
            ["Without emoji, your only reaction on it. Where you reacted more than once, say which."],
            ["/unreact m3", "/unreact ^ 👍"], SeeAlso: ["react"], Acts: true, Seen: "Everyone in the conversation sees it go."),
        new("like", "talk", "/like message", "the service's like: 👍, or ❤️ on Instagram",
            ["The reaction the service's picker starts with. The same as /react with that emoji."],
            ["/like ^", "/like m4"], SeeAlso: ["react", "unreact"], Acts: true, Seen: "Everyone in the conversation sees your reaction."),
        new("edit", "talk", "/edit message text", "change the words of a message you sent",
            ["Only your own messages, where the service edits (the message menu's Edit message). The new words replace the",
             "old ones for everyone. An uncertain edit is never tried again by itself."],
            ["/edit m7 see you at 8, not 7"], SeeAlso: ["unsend", "msgs"], Acts: true, Seen: "Everyone in the conversation sees the new words."),
        new("unsend", "talk", "/unsend message [yes]", "remove a message you sent, for everyone",
            ["The message menu's Delete for everyone, where the service offers it. Two steps: /unsend m7 says what will",
             "happen; /unsend m7 yes, typed within 60 seconds, does it."],
            ["/unsend m7", "/unsend m7 yes"], SeeAlso: ["delete", "edit"], Acts: true,
            Seen: "It is taken back from everyone in the conversation; they may already have read it."),

        // ---- manage ----
        new("archive", "manage", "/archive name|N|.", "archive a conversation",
            ["On the service for Google Messages, Messenger and Google Voice, as the conversation menu's Archive does.",
             "Instagram can't archive from Message Link, so an Instagram conversation (or a linked one) is archived in Message Link."],
            ["/archive 3", "/archive ."], Names: true, SeeAlso: ["unarchive", "ls"], Acts: true, Seen: "Changes your account on the service, except in Message Link where it says so."),
        new("unarchive", "manage", "/unarchive name|N|.", "bring a conversation back from the archive",
            ["/ls archived numbers the archived ones."], ["/ls archived", "/unarchive 2"], Names: true, SeeAlso: ["archive"], Acts: true,
            Seen: "Changes your account on the service, except in Message Link where it says so."),
        new("pin", "manage", "/pin name|N|.", "pin a conversation to the top, in Message Link", [], ["/pin Lauren"], Names: true, SeeAlso: ["unpin"], Acts: true),
        new("unpin", "manage", "/unpin name|N|.", "unpin a conversation, in Message Link", [], ["/unpin Lauren"], Names: true, Acts: true),
        new("unread", "manage", "/unread name|N|.", "mark a conversation unread, in Message Link",
            ["The conversation menu's Mark as unread: the service isn't told. Reading it clears the mark."],
            ["/unread ."], Names: true, SeeAlso: ["read"], Acts: true),
        new("delete", "manage", "/delete name|N|.|message [yes]", "delete a conversation, or a message",
            ["Two steps. /delete 3 prints exactly what will happen, in the words of the app's own Delete; /delete 3 yes,",
             "typed within 60 seconds, does it. A conversation is deleted where the conversation menu offers Delete…",
             "(not Instagram or a linked conversation). A message (m3, ^, #2) is the message menu's Delete for me, or Delete",
             "for everyone where that is all its service offers."],
            ["/delete 3", "/delete 3 yes", "/delete m2"], Names: true, SeeAlso: ["unsend", "archive"], Acts: true,
            Seen: "It can't be undone. A conversation goes from your account on the service; a message deleted for everyone goes for them too."),
        new("label", "manage", "/label name|N|. label", "put one of your labels on a conversation",
            ["Labels are kept in Message Link (the conversation menu's Labels). Quote a name with spaces: /label \"Alex Smith\" Family."],
            ["/label 3 Family", "/label . Work"], Names: true, SeeAlso: ["unlabel"], Acts: true),
        new("unlabel", "manage", "/unlabel name|N|. label", "take a label off a conversation", [], ["/unlabel 3 Family"], Names: true, Acts: true),
        new("snooze", "manage", "/snooze name|N|. when|off", "remind me about a conversation later",
            ["The conversation menu's Remind me later, in Message Link: notifications wait, then one reminder comes. when is 30m,",
             "1h, 3h, tonight, tomorrow or a time like 17:30; off calls it off."],
            ["/snooze . 1h", "/snooze Lauren tomorrow", "/snooze 2 off"], Names: true, Options: ["."], SeeAlso: ["remind"], Acts: true),
        new("copy", "manage", "/copy message", "copy a message's words to the clipboard", [], ["/copy m3", "/copy ^"], SeeAlso: ["msgs"]),
        new("save", "manage", "/save message", "keep a message in Saved Messages", ["The message menu's Save."],
            ["/save m3"], SeeAlso: ["remind"], Acts: true),
        new("remind", "manage", "/remind message when", "a notification about a message later",
            ["The message menu's Remind me: kept in Saved Messages with a time. when is 30m, 1h, tonight,",
             "tomorrow or a time like 17:30."],
            ["/remind m3 tonight", "/remind ^ 2h"], SeeAlso: ["save", "snooze"], Acts: true),

        // ---- look ----
        new("ls", "look", "/ls [view|name]", "list conversations, numbered, in the scroll",
            ["view is all (or *), unread, pinned, muted, archived or a service. Numbers work with /to N, /open N, /thread N.",
             "| grep finds conversation names; for words in messages, use /msgs * | grep words."],
            ["/ls unread", "/ls messenger", "/ls * | grep Smith"], Options: ChatViews, SeeAlso: ["chats", "msgs"]),
        new("msgs", "look", "/msgs [name|N|.|*] [count]", "a conversation's recent messages, numbered m1, m2 …",
            ["Reads only what this PC already keeps: nothing is asked of the service and nothing is marked read. The",
             "newest is last. count is up to 50 (10 unless you say). * or all lists every conversation's, up to 200 each",
             "(20 unless you say), one line each, for | grep. The numbers work with /react, /reply, /copy and the rest."],
            ["/msgs", "/msgs Lauren 20", "/msgs * | grep dinner"], Aliases: ["messages"], Names: true, Options: ["*", "all", "."],
            SeeAlso: ["react", "reply", "grep", "search"]),
        new("grep", "look", "/grep words", "find lines already in the scroll",
            ["The same as | grep, over what the console shows: a capital letter makes it exact, -v shows the lines that",
             "don't match. /search looks through messages kept on this PC instead."],
            ["/grep dinner", "/grep -v Messenger"], SeeAlso: ["search", "msgs"]),
        new("search", "look", "/search words [@service]", "search messages kept on this PC",
            ["Results are numbered: /open N opens one, /thread N inspects its conversation. @service limits the search."],
            ["/search alternator", "/search dinner @messenger", "/search invoice | grep March"], SeeAlso: ["open", "thread"]),
        new("thread", "look", "/thread [info|members|media|stats|raw|route] [name|N|.]", "inspect a conversation",
            ["Reads only what this PC holds. With no name: the target, else the conversation open in Message Link, else whoever",
             "wrote last. stats draws when people write; media lists photos, files and voice notes; raw shows ids and flags."],
            ["/thread", "/thread stats Lauren", "/thread media .", "/thread raw 3"], Options: ["info", "members", "media", "stats", "raw", "route"],
            Names: true, SeeAlso: ["route", "search"]),
        new("route", "look", "/route [name|N]", "show, hop by hop, how a message would be sent",
            ["Traces a name to the conversation and service a text would use, the helper that carries it and the outbox,",
             "without sending or changing the target."],
            ["/route Lauren", "/route Lauren@instagram"], Names: true, SeeAlso: ["to", "thread"]),
        new("history", "look", "/history [hours]", "redraw with earlier messages kept on this PC",
            ["The last day unless you say; up to 168 hours. /history inputs lists what you typed."],
            ["/history", "/history 48"], Options: ["24", "48", "168", "inputs"], SeeAlso: ["commands"]),
        new("filter", "look", "/filter what", "show only some services or a person",
            ["what is one or more services, person name, muted or all."], ["/filter messenger instagram", "/filter person Lauren", "/filter all"],
            Options: ["all", "person", "muted", "google", "messenger", "instagram", "voice"], SeeAlso: ["watch", "events"]),
        new("watch", "look", "/watch name|off", "follow one person: the scroll shows only them", ["The same as /filter person name."],
            ["/watch Lauren", "/watch off"], Names: true, Options: ["off"], SeeAlso: ["filter"]),
        new("events", "look", "/events quiet|normal|all", "how chatty the scroll is",
            ["quiet: messages and calls. normal: also edits, reactions and connections. all: also read receipts and typing."],
            ["/events all"], Options: ["quiet", "normal", "all"]),

        // ---- apps ----
        new("top", "apps", "/top [page]", "live monitor: services, helpers, activity, queues, cache, perf",
            ["A full-screen app. Tab or 1-5 changes page, p pauses, q or Esc returns to the scroll exactly as you left it."],
            ["/top", "/top workers", "/top | grep Messenger"], Options: TopPages, SeeAlso: ["workers", "queues", "cache", "perf"]),
        new("workers", "apps", "/workers", "helper processes: pid, uptime, restarts, memory, cpu", ["/top on its workers page."], ["/workers"]),
        new("queues", "apps", "/queues", "sends in flight and scheduled sends", ["/top on its queues page. Nothing here sends or resends."],
            ["/queues"], Aliases: ["jobs", "outbox"]),
        new("cache", "apps", "/cache", "what Message Link holds in memory and on disk", ["/top on its cache page. r measures the disk again."], ["/cache"]),
        new("perf", "apps", "/perf", "memory, collections, handles and threads", ["/top on its perf page."], ["/perf"]),
        new("chats", "apps", "/chats [view|name]", "browse conversations full-screen",
            ["Up/Down move, Enter opens, t makes it the target, i inspects, r marks read (asks first), m mutes, / filters,",
             "Tab changes view, 1-4 picks a service, q returns."],
            ["/chats", "/chats unread"], Options: ChatViews, SeeAlso: ["ls", "thread"]),
        new("logs", "apps", "/logs [source]", "live tail of the diagnostic logs",
            ["The same sanitized logs as Settings → Troubleshooting. Tab changes log, f follows, / greps, g and G jump, c copies."],
            ["/logs", "/logs messenger", "/log instagram"], Aliases: ["log", "tail"],
            Options: ["messages", "google", "voice", "messenger", "helper", "instagram"]),

        // ---- system ----
        new("status", "system", "/status", "service health, unread, sends and notifications", [], ["/status"], SeeAlso: ["top", "diag"]),
        new("diag", "system", "/diag", "check this copy of Message Link and its services", ["Reads state only; fixes nothing by itself."],
            ["/diag", "/diag | grep -v ok"], Aliases: ["doctor"]),
        new("mirror", "system", "/mirror [now]", "how the server's copies compare with the services",
            ["The server compares each service's most recently active conversations with its own copy every half hour, and reads again what differs.",
             "/mirror shows what its newest check found; /mirror now starts one. Reads only: nothing is sent or marked read."],
            ["/mirror", "/mirror now"], Options: ["now"], SeeAlso: ["diag"]),
        new("whoami", "system", "/whoami", "this PC, this user and the services signed in", [], ["/whoami"]),
        new("uptime", "system", "/uptime", "how long Message Link has run, and how busy it has been", [], ["/uptime"]),
        new("alias", "system", "/alias [name[=command]]", "name a command line",
            ["Aliases run console commands only; they never send. /alias alone lists them, /unalias name removes one.",
             "Saved protected on this PC."],
            ["/alias u=/ls unread", "/alias mon=/top workers", "/alias"], SeeAlso: ["unalias"]),
        new("unalias", "system", "/unalias name", "remove an alias", [], ["/unalias u"]),
        new("theme", "system", "/theme [name]", "the terminal's colours", ["campbell, phosphor, amber, ice or mono. Service colours stay their own."],
            ["/theme amber", "/theme"], Options: [.. ConsoleThemes.All.Select(theme => theme.Name)]),
        new("pause", "system", "/pause [1h|morning]", "pause notifications; /resume turns them back on", [], ["/pause", "/pause morning"],
            Options: ["1h", "morning"]),
        new("resume", "system", "/resume", "turn notifications back on", [], ["/resume"]),
        new("commands", "system", "/commands [words]", "what you typed while Message Link has been running",
            ["Kept in memory only, never written to disk. !n runs number n again."], ["/commands", "/commands search"]),
        new("clear", "system", "/clear", "clear the scroll (nothing is deleted)", ["Ctrl+L does the same."], ["/clear"], Aliases: ["cls"]),
        new("latest", "system", "/latest", "jump to the newest line and follow again", [], ["/latest"]),
        new("guide", "system", "/guide [topic]", "open the full Message Link guide, at a topic if you name one",
            ["The guide covers all of Message Link, not only the console. F1 opens it too, at the console chapter."],
            ["/guide", "/guide send later", "/guide linked"], Aliases: ["manual"], SeeAlso: ["help", "tour"]),
        new("tour", "system", "/tour", "the two-minute tour of the main window", ["Points out what is easy to miss. Nothing is opened or sent."],
            ["/tour"], SeeAlso: ["guide"]),
        new("help", "system", "/help [group|command|word]", "this help; /help talk lists a group, /help command its manual",
            ["The groups are talk, manage, look, apps, system and fun. A word that is neither a group nor a command searches",
             "the manual. command --help (or -h) shows the same page without running the command."],
            ["/help", "/help talk", "/help route", "/delete --help"], Aliases: ["man", "?"]),

        // ---- fun ----
        new("neofetch", "fun", "/neofetch", "the obligatory system summary", [], ["/neofetch"], Aliases: ["fetch"]),
        new("banner", "fun", "/banner", "the Message Link logo", [], ["/banner"], Options: ["on", "off"]),
        new("fortune", "fun", "/fortune", "a small piece of terminal wisdom", [], ["/fortune", "/fortune | cowsay"]),
        new("cowsay", "fun", "/cowsay text", "a cow says it", [], ["/cowsay moo"]),
        new("matrix", "fun", "/matrix", "follow the white rabbit (any key returns)", [], ["/matrix"]),
        new("date", "fun", "/date", "the date and time", [], ["/date"]),
        new("echo", "fun", "/echo text", "print text in the scroll; never sends", [], ["/echo hello | cowsay"]),
    ];

    private static readonly Dictionary<string, ConsoleCommandInfo> ByWord = Index();

    private static Dictionary<string, ConsoleCommandInfo> Index()
    {
        var map = new Dictionary<string, ConsoleCommandInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in All)
        {
            map[info.Name] = info;
            foreach (var alias in info.Aliases ?? []) map[alias] = info;
        }
        return map;
    }

    /// <summary>The command a word names, alias or not; null for none.</summary>
    public static ConsoleCommandInfo? Find(string? word) =>
        word is null ? null : ByWord.GetValueOrDefault(word.Trim().TrimStart('/'));

    /// <summary>Every word Tab completes after a slash, names and aliases, sorted.</summary>
    public static IReadOnlyList<string> Words { get; } = [.. ByWord.Keys.Where(w => w != "?").Order(StringComparer.Ordinal)];

    /// <summary>Whether the command line starts with a command that can send. Plain text sends too.</summary>
    public static bool StartsWithSend(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0) return false;
        if (text[0] != '/' || text.StartsWith("//", StringComparison.Ordinal)) return true;
        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return Find(text[1..end])?.Sends == true;
    }

    /// <summary>Whether the command line starts with a command that changes a conversation or a message.</summary>
    public static bool StartsWithAct(string line)
    {
        var text = line.TrimStart();
        if (text.Length < 2 || text[0] != '/') return false;
        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return Find(text[1..end])?.Acts == true;
    }

    /// <summary>
    /// The command whose manual a line asks for with --help or -h: any word of a command that doesn't send, and only the
    /// last word of one that does (a message may say "-h" in the middle). Null when the line doesn't ask.
    /// </summary>
    public static ConsoleCommandInfo? HelpAsked(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('/')) text = text[1..];
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || Find(words[0]) is not { } info) return null;
        static bool Asks(string word) => word is "--help" or "-h" or "-?";
        return (info.Sends ? Asks(words[^1]) : words.Skip(1).Any(Asks)) ? info : null;
    }

    private static readonly System.Text.RegularExpressions.Regex RefWord =
        new(@"^(\d{1,4}|m\d{1,5}|\^\d{0,3}|#\d{1,4}|\.|\*|-h|--help)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Commands whose arguments are free words, so any short line after them reads as the command.
    private static readonly HashSet<string> FreeWords = new(StringComparer.Ordinal) { "grep", "search", "help", "echo", "cowsay", "commands", "guide", "alias" };

    /// <summary>
    /// Whether plain text typed to a target looks like a command the user meant to run rather than send: its first word is
    /// a command (not one that sends) or an alias, and either the line has a pipe (" | "), or it is four words or fewer
    /// and what follows the word reads as that command's arguments (nothing, one of its options, a number, m3, ^, ., *, a
    /// name it knows, or any words for /grep, /search and /help). Deliberately narrow: "Open the door when you get here"
    /// and "save me a seat" are sentences, and go.
    /// </summary>
    public static bool LooksLikeCommand(string line, Func<string, bool> isAlias, Func<string, bool> isName, out string word)
    {
        word = "";
        var text = line.Trim();
        if (text.Length == 0 || text[0] is '/' or '!' or '\\' || text.Contains('\n')) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var first = words[0].ToLowerInvariant();
        var info = Find(first);
        if (info is { Sends: true } || info is null && !isAlias(first)) return false;
        word = info?.Name ?? first;
        if (text.Contains(" | ", StringComparison.Ordinal)) return true;
        if (words.Length > 4) return false;
        if (words.Length == 1 || info is null) return true;
        var next = words[1];
        if (FreeWords.Contains(info.Name)) return true;
        if (info.Options?.Contains(next, StringComparer.OrdinalIgnoreCase) == true || RefWord.IsMatch(next)) return true;
        return info.Names && isName(next.Trim('"'));
    }

    /// <summary>The command a mistyped word most likely meant (one edit or one swap away), for "did you mean"; null for none.</summary>
    public static string? Suggest(string word)
    {
        var typed = word.ToLowerInvariant();
        if (typed.Length < 2) return null;
        var best = Words.Select(w => (Word: w, Cost: Distance(typed, w))).OrderBy(c => c.Cost).ThenBy(c => c.Word.Length).First();
        return best.Cost <= 1 ? best.Word : null;
    }

    // Optimal string alignment distance: insertions, deletions, substitutions and adjacent swaps ("stauts").
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Manual pages whose words mention <paramref name="word"/>, for /help word.</summary>
    public static IReadOnlyList<ConsoleCommandInfo> Apropos(string word) =>
        [.. All.Where(info => info.Summary.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            info.Description.Any(line => line.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
            info.Synopsis.Contains(word, StringComparison.OrdinalIgnoreCase))];
}
