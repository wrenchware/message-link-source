namespace NativeBeeper.Presentation.Services;

/// <summary>One typed console line, read. Command words are case-insensitive.</summary>
public abstract record ConsoleCommand
{
    /// <summary>Plain text for the query target. Empty for a blank line.</summary>
    public sealed record Say(string Text) : ConsoleCommand;
    /// <summary>/r or /reply. Text may be empty.</summary>
    public sealed record Reply(string Text) : ConsoleCommand;
    /// <summary>
    /// /w, /msg or /query. With <paramref name="Exact"/> the name was quoted or was one word with @service attached, and Text is the rest of the
    /// line (null when there is none). Otherwise Name is the whole rest of the line, Text is null, and
    /// <see cref="ConversationTargets.Lead"/> decides where the name ends.
    /// </summary>
    public sealed record Whisper(string Name, string? Service, string? Text, bool Exact = true) : ConsoleCommand;
    public sealed record Read(string? Name) : ConsoleCommand;
    public sealed record Mute(string Name) : ConsoleCommand;
    public sealed record Unmute(string Name) : ConsoleCommand;
    /// <summary>The words after /filter, a quoted name kept whole and unquoted.</summary>
    public sealed record Filter(IReadOnlyList<string> Args) : ConsoleCommand;
    public sealed record Events(ConsoleVerbosity Level) : ConsoleCommand;
    public sealed record Search(string Words) : ConsoleCommand;
    public sealed record Help(string? Topic) : ConsoleCommand;
    public sealed record Clear : ConsoleCommand;
    /// <summary>/history or /history N: redraw with the last N hours the server has sent (null: the default day).</summary>
    public sealed record History(int? Hours) : ConsoleCommand;
    public sealed record Chats(string Selector) : ConsoleCommand;
    public sealed record To(string? Name) : ConsoleCommand;
    public sealed record Route(string? Name) : ConsoleCommand;
    public sealed record Status : ConsoleCommand;
    public sealed record Commands(string Query) : ConsoleCommand;
    public sealed record Fortune : ConsoleCommand;
    /// <summary>A full-screen console app (top, chats, logs, matrix), with its argument.</summary>
    public sealed record View(string App, string Argument = "") : ConsoleCommand;
    /// <summary>A console tool that prints into the scroll: thread, diag, whoami, uptime, theme, alias and the rest. Rest is
    /// the line after the command word, trimmed; Name is the catalogue's canonical word.</summary>
    public sealed record Tool(string Name, string Rest) : ConsoleCommand;
    /// <summary>
    /// A command that acts on a conversation or a message, or reads a conversation's messages: archive, unarchive, pin,
    /// unpin, unread, delete, unsend, react, unreact, like, edit, copy, save, remind, label, unlabel, snooze and msgs.
    /// Verb is the catalogue's word. Ref names the conversation or message as typed (quotes kept, so a name keeps its
    /// spaces); null when none was typed. Rest is what follows it: a reaction, text, a time, a label or a count.
    /// Confirmed: the line ended in "yes", the second step of /delete and /unsend.
    /// </summary>
    public sealed record Act(string Verb, string? Ref, string Rest = "", bool Confirmed = false) : ConsoleCommand;
    /// <summary>/reply mN text: a quoted reply to a message. Ref is the message reference, Text what to send.</summary>
    public sealed record QuotedReply(string Ref, string Text) : ConsoleCommand;
    /// <summary>/exit or /quit. The terminal ends the console before the line reaches the session; there it does nothing.</summary>
    public sealed record Exit : ConsoleCommand;
    /// <summary>A command word the console does not have, without its slash.</summary>
    public sealed record Unknown(string Word) : ConsoleCommand;
    /// <summary>A known command used wrongly. Command is its canonical word, for the log.</summary>
    public sealed record Invalid(string Message, string Command = "") : ConsoleCommand;
}

/// <summary>Reads a console line into a command. Pure: no lookups, no state.</summary>
public static class ConsoleCommandParser
{
    public static ConsoleCommand Parse(string? input)
    {
        var line = (input ?? "").Trim();
        if (line.Length == 0) return new ConsoleCommand.Say("");
        // "//" sends a line that starts with a slash as it is, less the first slash.
        if (line.StartsWith("//", StringComparison.Ordinal)) return new ConsoleCommand.Say(line[1..]);
        if (line[0] != '/') return new ConsoleCommand.Say(line);
        var end = 1;
        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
        var word = line[1..end];
        var rest = line[end..].Trim();
        switch (word.ToLowerInvariant())
        {
            case "r":
                return new ConsoleCommand.Reply(rest);
            case "reply":
                // "/reply m3 text" quotes a message; "/reply text" is /r, as it always was. Only a word shaped like a
                // message reference starts a quote (a bare number does not: "/reply 5 minutes" stays a reply).
                if (Head(rest) is { First: var quoteRef, After: var quoteText } && IsMessageRef(quoteRef, numbers: false))
                    return quoteText.Length == 0 ? new ConsoleCommand.Invalid($"Type the reply after the message: /reply {quoteRef} text", "reply")
                        : new ConsoleCommand.QuotedReply(quoteRef, quoteText);
                return new ConsoleCommand.Reply(rest);
            case "archive" or "unarchive" or "pin" or "unpin" or "unread":
                return rest.Length == 0 ? new ConsoleCommand.Invalid($"Say which conversation: /{word.ToLowerInvariant()} name, N or .", word.ToLowerInvariant())
                    : new ConsoleCommand.Act(word.ToLowerInvariant(), rest);
            case "delete" or "unsend":
            {
                var verb = word.ToLowerInvariant();
                var yes = rest.EndsWith(" yes", StringComparison.OrdinalIgnoreCase) || rest.Equals("yes", StringComparison.OrdinalIgnoreCase);
                var what = yes ? rest[..^3].Trim() : rest;
                if (what.Length == 0)
                    return new ConsoleCommand.Invalid(verb == "delete" ? "Say what to delete: /delete name, N, . or a message (m3, ^)" : "Say which of your messages: /unsend m3", verb);
                return new ConsoleCommand.Act(verb, what, "", yes);
            }
            case "react":
            {
                var (first, emoji) = Head(rest);
                return first.Length == 0 || emoji.Length == 0 ? new ConsoleCommand.Invalid("Type a message and a reaction: /react m3 👍", "react")
                    : new ConsoleCommand.Act("react", first, emoji);
            }
            case "unreact" or "like" or "copy" or "save":
            {
                var verb = word.ToLowerInvariant();
                var (first, more) = Head(rest);
                if (first.Length == 0) return new ConsoleCommand.Invalid($"Say which message: /{verb} m3, /{verb} ^ or a /search number", verb);
                if (verb != "unreact" && more.Length > 0) return new ConsoleCommand.Invalid($"/{verb} takes one message: /{verb} m3", verb);
                return new ConsoleCommand.Act(verb, first, more);
            }
            case "edit":
            {
                var (first, text) = Head(rest);
                return first.Length == 0 || text.Length == 0 ? new ConsoleCommand.Invalid("Type your message and its new words: /edit m3 new text", "edit")
                    : new ConsoleCommand.Act("edit", first, text);
            }
            case "remind":
            {
                var (first, when) = Head(rest);
                return first.Length == 0 || when.Length == 0 ? new ConsoleCommand.Invalid("Say which message and when: /remind m3 1h, tonight, tomorrow or 17:30", "remind")
                    : new ConsoleCommand.Act("remind", first, when);
            }
            case "label" or "unlabel":
            {
                var verb = word.ToLowerInvariant();
                var (first, label) = Head(rest);
                return first.Length == 0 || label.Length == 0 ? new ConsoleCommand.Invalid($"Say which conversation and label: /{verb} name Family (quote a name with spaces)", verb)
                    : new ConsoleCommand.Act(verb, first, label.Trim('"'));
            }
            case "snooze":
            {
                // The time is the last word, so the name before it may have spaces.
                var space = rest.LastIndexOf(' ');
                return space <= 0 ? new ConsoleCommand.Invalid("Say which conversation and when: /snooze name 1h, tonight, tomorrow, 17:30 or off", "snooze")
                    : new ConsoleCommand.Act("snooze", rest[..space].Trim(), rest[(space + 1)..]);
            }
            case "msgs" or "messages":
            {
                // "/msgs name 20": a trailing number after a name is a count; a number alone is a row of the last list.
                if (rest.Length == 0) return new ConsoleCommand.Act("msgs", null);
                var space = rest.LastIndexOf(' ');
                if (space > 0 && rest[(space + 1)..] is var tail && tail.Length <= 3 && tail.All(char.IsAsciiDigit))
                    return new ConsoleCommand.Act("msgs", rest[..space].Trim(), tail);
                return new ConsoleCommand.Act("msgs", rest);
            }
            case "grep":
                return rest.Length == 0 ? new ConsoleCommand.Invalid("Type what to look for in the scroll: /grep words", "grep") : new ConsoleCommand.Tool("grep", rest);
            case "w" or "msg" or "query":
                return Whisper(rest);
            case "read":
                return new ConsoleCommand.Read(rest.Length == 0 ? null : rest);
            case "mute":
                return rest.Length == 0 ? new ConsoleCommand.Invalid("Type a name: /mute name", "mute") : new ConsoleCommand.Mute(rest);
            case "unmute":
                return rest.Length == 0 ? new ConsoleCommand.Invalid("Type a name: /unmute name", "unmute") : new ConsoleCommand.Unmute(rest);
            case "filter":
                var args = Words(rest);
                return args.Count == 0
                    ? new ConsoleCommand.Invalid("Type what to show: /filter messenger, /filter person name, /filter muted or /filter all", "filter")
                    : new ConsoleCommand.Filter(args);
            case "events":
                return rest.ToLowerInvariant() switch
                {
                    "quiet" => new ConsoleCommand.Events(ConsoleVerbosity.Quiet),
                    "normal" => new ConsoleCommand.Events(ConsoleVerbosity.Normal),
                    "all" => new ConsoleCommand.Events(ConsoleVerbosity.All),
                    _ => new ConsoleCommand.Invalid("Use /events quiet, normal or all.", "events")
                };
            case "search":
                return rest.Length == 0 ? new ConsoleCommand.Invalid("Type what to search for: /search words", "search") : new ConsoleCommand.Search(rest);
            case "help" or "man" or "?":
                return new ConsoleCommand.Help(rest.Length == 0 ? null : rest);
            case "history":
                if (rest.Equals("inputs", StringComparison.OrdinalIgnoreCase)) return new ConsoleCommand.Commands("");
                if (rest.Length == 0) return new ConsoleCommand.History(null);
                return rest.Length <= 3 && rest.All(char.IsAsciiDigit) && int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture) is var hours && hours is >= 1 and <= 168
                    ? new ConsoleCommand.History(hours)
                    : new ConsoleCommand.Invalid("Use /history, or /history 48 for more hours (up to 168).", "history");
            case "ls":
                if (rest.Equals("chats", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("chats ", StringComparison.OrdinalIgnoreCase))
                    rest = rest.Length > 5 ? rest[6..].Trim() : "";
                return new ConsoleCommand.Chats(rest);
            case "chats":
                return new ConsoleCommand.View("chats", rest);
            case "to": return new ConsoleCommand.To(rest.Length == 0 ? null : rest);
            case "route": return new ConsoleCommand.Route(rest.Length == 0 ? null : rest);
            case "status": return rest.Length == 0 ? new ConsoleCommand.Status() : new ConsoleCommand.Invalid("Use /status without arguments.", "status");
            case "commands": return new ConsoleCommand.Commands(rest);
            case "fortune": return rest.Length == 0 ? new ConsoleCommand.Fortune() : new ConsoleCommand.Invalid("Use /fortune without arguments.", "fortune");
            case "apps":
                return new ConsoleCommand.Help(null);
            case "top":
                var page = rest.ToLowerInvariant();
                return page.Length == 0 || TopApp.Pages.Contains(page) ? new ConsoleCommand.View("top", page)
                    : new ConsoleCommand.Invalid("Use /top, or /top " + string.Join(", ", TopApp.Pages) + ".", "top");
            case "workers" or "cache" or "perf" or "queues" or "jobs" or "outbox":
                return rest.Length == 0 ? new ConsoleCommand.View("top", word.ToLowerInvariant() is "jobs" or "outbox" ? "queues" : word.ToLowerInvariant())
                    : new ConsoleCommand.Invalid("Use /" + word.ToLowerInvariant() + " without arguments.", "top");
            case "logs" or "log" or "tail":
                return new ConsoleCommand.View("logs", rest);
            case "matrix":
                return new ConsoleCommand.View("matrix");
            case "exit" or "quit":
                return new ConsoleCommand.Exit();
            case "banner":
                return rest.Length == 0 ? new ConsoleCommand.Tool("banner", "") : new ConsoleCommand.Invalid("Use /banner without arguments.", "banner");
            case "clear" or "cls":
                return new ConsoleCommand.Clear();
            case "thread" or "diag" or "doctor" or "whoami" or "uptime" or "neofetch" or "fetch" or "cowsay" or "date" or "echo" or "theme" or "guide" or "manual"
                or "alias" or "unalias" or "watch" or "mirror":
                return new ConsoleCommand.Tool(ConsoleCatalog.Find(word)?.Name ?? word.ToLowerInvariant(), rest);
            default:
                return new ConsoleCommand.Unknown(word);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex MessageRefShape =
        new(@"^(m\d{1,5}|\^\d{0,3}|#\d{1,4})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a word names a message: m3 (from the last /msgs), ^ or ^2 (the newest, or second newest, message from
    /// someone else in the target), #4 (search result 4), and with <paramref name="numbers"/> a bare 4 as well.
    /// </summary>
    public static bool IsMessageRef(string word, bool numbers = true) =>
        MessageRefShape.IsMatch(word) || numbers && word.Length is > 0 and <= 4 && word.All(char.IsAsciiDigit);

    private static readonly System.Text.RegularExpressions.Regex Span =
        new(@"^(\d{1,3})\s*(m|min|mins|h|hr|hrs|d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ClockTime =
        new(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// When a reminder or snooze is for: 30m, 1h, 2d; tonight (8 pm, as Remind me's "This evening"); tomorrow or morning
    /// (8 am, as its "Tomorrow morning"); or a clock time, 17:30 or 5:30pm, today if still ahead and otherwise tomorrow.
    /// Null when the words are none of these or the moment has passed.
    /// </summary>
    public static DateTimeOffset? When(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        var value = text.Trim().ToLowerInvariant();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        if (Span.Match(value) is { Success: true } span)
        {
            var count = int.Parse(span.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (count == 0) return null;
            return span.Groups[2].Value[0] switch
            {
                'm' => count <= 24 * 60 ? now.AddMinutes(count) : null,
                'h' => count <= 24 * 7 ? now.AddHours(count) : null,
                _ => count <= 30 ? now.AddDays(count) : null,
            };
        }
        switch (value)
        {
            case "tonight" or "evening":
                return LaterTimes.Picked(local.Date, LaterTimes.Evening, now, zone);
            case "tomorrow":
                return LaterTimes.Picked(local.Date.AddDays(1), LaterTimes.Morning, now, zone);
            case "morning":
                return LaterTimes.Picked(local.Date, LaterTimes.Morning, now, zone) ?? LaterTimes.Picked(local.Date.AddDays(1), LaterTimes.Morning, now, zone);
        }
        if (ClockTime.Match(value) is not { Success: true } clock) return null;
        var hour = int.Parse(clock.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minute = clock.Groups[2].Success ? int.Parse(clock.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        // A bare number is only a time with am or pm after it; "17" alone could be anything.
        if (!clock.Groups[2].Success && !clock.Groups[3].Success) return null;
        if (clock.Groups[3].Success)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (clock.Groups[3].Value == "pm" ? 12 : 0);
        }
        if (hour > 23 || minute > 59) return null;
        var time = new TimeSpan(hour, minute, 0);
        return LaterTimes.Picked(local.Date, time, now, zone) ?? LaterTimes.Picked(local.Date.AddDays(1), time, now, zone);
    }

    /// <summary>The first word (a quoted run kept whole, with its quotes) and the rest of the line, trimmed.</summary>
    public static (string First, string After) Head(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return ("", "");
        int end;
        if (value[0] == '"' && value.IndexOf('"', 1) is var close && close > 0)
        {
            end = close + 1;
            // A service may be attached to the quoted name: "Alex Smith"@messenger.
            while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        }
        else
        {
            end = 0;
            while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        }
        return (value[..end], value[end..].Trim());
    }

    // "/w name text": a quoted name, or one word with @service attached, is exact; otherwise the whole line goes to Lead,
    // which knows the names and so where one ends.
    private static ConsoleCommand Whisper(string rest)
    {
        if (rest.Length == 0) return new ConsoleCommand.Invalid("Type a name: /w name message", "w");
        if (rest[0] == '"')
        {
            var close = rest.IndexOf('"', 1);
            if (close < 0) return new ConsoleCommand.Invalid("Close the quoted name: /w \"name\" message", "w");
            var name = rest[1..close].Trim();
            if (name.Length == 0) return new ConsoleCommand.Invalid("Type a name: /w name message", "w");
            var after = rest[(close + 1)..];
            string? service = null;
            // "\"Alex\"@ig" names a service however it is spelt; "\"Alex\" @ig" does when @ig is one, and is text otherwise.
            var spaced = after.Length > 0 && char.IsWhiteSpace(after[0]);
            var trimmed = after.TrimStart();
            if (trimmed.StartsWith('@'))
            {
                var stop = 1;
                while (stop < trimmed.Length && !char.IsWhiteSpace(trimmed[stop])) stop++;
                var word = trimmed[1..stop];
                if (ConversationTargets.ServiceOf(word) is not null) { service = word; after = trimmed[stop..]; }
                else if (!spaced) return NoService(word);
            }
            var text = after.Trim();
            return new ConsoleCommand.Whisper(name, service, text.Length == 0 ? null : text);
        }
        // A one-word name with its service attached: "Alex@ig hi". Only the first word is looked at: an @ig further on
        // may be the message ("/w Alex follow me @ig"), and a longer name followed by a service ("Alex Smith @fb hi")
        // is Lead's to find, because only the names say where a name ends.
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        var token = rest[..end];
        var at = token.LastIndexOf('@');
        if (at >= 0 && ConversationTargets.ServiceOf(token[(at + 1)..]) is not null)
        {
            var name = token[..at].Trim();
            if (name.Length == 0) return new ConsoleCommand.Invalid("Type a name before @" + token[(at + 1)..] + ".", "w");
            var text = rest[end..].Trim();
            return new ConsoleCommand.Whisper(name, token[(at + 1)..], text.Length == 0 ? null : text);
        }
        return new ConsoleCommand.Whisper(rest, null, null, Exact: false);
    }

    private static ConsoleCommand.Invalid NoService(string word) =>
        new($"There's no service called “{word}”. Use @google, @messenger, @instagram or @voice.", "w");

    /// <summary>A name as typed after a command, split from a trailing @service: "\"Alex Smith\"@fb", "Alex@ig", "Alex".
    /// The name comes back unquoted; the service word is null when none was given or it names no service.</summary>
    public static (string Name, string? Service) SplitName(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            if (close > 0)
            {
                var after = value[(close + 1)..].Trim();
                var service = after.StartsWith('@') && ConversationTargets.ServiceOf(after[1..]) is not null ? after[1..] : null;
                return (value[1..close].Trim(), service);
            }
            return (value.Trim('"').Trim(), null);
        }
        var at = value.LastIndexOf('@');
        if (at > 0 && ConversationTargets.ServiceOf(value[(at + 1)..]) is not null) return (value[..at].Trim(), value[(at + 1)..]);
        return (value, null);
    }

    /// <summary>Whitespace-separated words, a quoted run kept as one word without its quotes.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) break;
            if (text[index] == '"')
            {
                var close = text.IndexOf('"', index + 1);
                var end = close < 0 ? text.Length : close;
                var quoted = text[(index + 1)..end].Trim();
                if (quoted.Length > 0) words.Add(quoted);
                index = close < 0 ? text.Length : close + 1;
                continue;
            }
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            words.Add(text[start..index]);
        }
        return words;
    }
}
