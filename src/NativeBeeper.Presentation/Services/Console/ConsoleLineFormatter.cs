using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.ViewModels;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Turns what happened into console lines: three columns (time, service tag, body) and the words a screen reader says.
/// Pure: the same input gives the same line, and nothing is looked up beyond the merges this PC already holds.
/// Message text is the chat list's own preview wording (photos, voice messages, stickers and files read as the list
/// reads them), except that the newlines of a text are kept.
/// </summary>
/// <param name="merges">Tells a Voice account's label apart when there is more than one; optional.</param>
/// <param name="zone">The local time zone dates and day lines are read in; the machine's when null.</param>
public sealed class ConsoleLineFormatter(MergedConversations? merges = null, TimeZoneInfo? zone = null)
{
    private const string Dot = " · ";
    private const string Chevron = " › ";
    public const int QuotedElements = 40;

    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Local;

    private DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).DateTime;
    public DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(Local(at));

    /// <summary>The time column: "12:04" or "12:04 PM" as the culture writes it, with "23 Sep" before it when asked.</summary>
    public string Time(DateTimeOffset at, bool withDate)
    {
        var local = Local(at);
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return withDate ? local.ToString("d MMM", CultureInfo.CurrentCulture) + " " + time : time;
    }

    /// <summary>"— Thursday 24 September —".</summary>
    public string DayText(DateTimeOffset at) => "— " + Local(at).ToString("dddd d MMMM", CultureInfo.CurrentCulture) + " —";

    /// <summary>The tag column's word for a service.</summary>
    public static string Tag(string? network) => network switch
    {
        "Google Messages" => "Google",
        "Messenger" => "Messenger",
        "Instagram" => "Instagram",
        "Google Voice" => "Voice",
        _ => ""
    };

    /// <summary>A service as the logs name it: fixed words only.</summary>
    public static string Provider(string? network) => network switch
    {
        "Google Messages" => "google",
        "Messenger" => "messenger",
        "Instagram" => "instagram",
        "Google Voice" => "voice",
        _ => "unknown"
    };

    /// <summary>The least verbosity that shows an entry of this kind.</summary>
    public static ConsoleVerbosity Level(ActivityKind kind) => kind switch
    {
        ActivityKind.Message or ActivityKind.Call or ActivityKind.Voicemail => ConsoleVerbosity.Quiet,
        ActivityKind.Read or ActivityKind.Typing => ConsoleVerbosity.All,
        _ => ConsoleVerbosity.Normal
    };

    /// <summary>
    /// A message's words as the chat list's preview has them (MessageItemViewModel.PreviewText and ChatPreviewText), but
    /// with a text's own newlines kept and nothing cut off: the console wraps rather than shortens.
    /// </summary>
    public static string Words(ChatMessage message)
    {
        if (message.IsDeleted || message.IsHidden) return MessageItemViewModel.PreviewText(message);
        var readable = MetaAiMessages.Text(message);
        if (string.IsNullOrWhiteSpace(readable)) return MessageItemViewModel.PreviewText(message);
        var text = MessageText.PlainText(readable).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return text.Length == 0 ? MessageItemViewModel.PreviewText(message) : ChatPreviewText.Format(false, text);
    }

    /// <summary>The first 40 text elements of a message's words on one line, in curly quotes, with … when cut.</summary>
    public static string Quote(string text, int elements = QuotedElements)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var info = new StringInfo(flat);
        return "“" + (info.LengthInTextElements > elements ? info.SubstringByTextElements(0, elements).TrimEnd() + "…" : flat) + "”";
    }

    /// <summary>
    /// A conversation's service as a route: "Messenger", or "Google Voice · Work" where there is more than one account on
    /// the service. The merges' own rule, so the console and the merged conversation's route picker agree.
    /// </summary>
    public string RouteLabel(string? chatId, string network, string? accountId)
    {
        if (merges is null || network.Length == 0) return network;
        var chat = (chatId is null ? null : merges.Source(chatId)) ?? Stub(chatId ?? "", network, accountId ?? "");
        return merges.ServiceRouteLabel(chat);
    }

    // " · Work" for a Voice conversation when more than one Voice account is signed in; empty otherwise.
    private string AccountSuffix(string? chatId, string network, string? accountId)
    {
        if (network != "Google Voice") return "";
        var label = RouteLabel(chatId, network, accountId);
        return label.Length > network.Length + Dot.Length && label.StartsWith(network + Dot, StringComparison.Ordinal)
            ? Dot + label[(network.Length + Dot.Length)..] : "";
    }

    /// <summary>The conversation an entry belongs to, as it is addressed: its title (the merged title for a merged
    /// person), carrying the Voice account where that tells two apart.</summary>
    public string Conversation(ActivityEntry entry) =>
        Name(entry.ConversationTitle, entry.SenderName) + AccountSuffix(entry.ChatId, entry.Network, entry.AccountId);

    // Whoever did it: in a one-to-one conversation the conversation itself (its title carries the local contact name);
    // in a group the person.
    private string Actor(ActivityEntry entry) =>
        entry.IsOutgoing ? "You" : entry.IsGroup ? Name(entry.SenderName, null) : Conversation(entry);

    /// <summary>The conversation's own title, as the prompt and a hint name it (no account label: the service says it).</summary>
    public static string TitleOf(ActivityEntry entry) => Name(entry.ConversationTitle, entry.SenderName);

    /// <summary>"23 Sep", the date a search result is listed under.</summary>
    public string ShortDate(DateTimeOffset at) => Local(at).ToString("d MMM", CultureInfo.CurrentCulture);

    private static string Name(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : "Someone";

    // How the name is typed back: quoted when it has a space, so /msgs reads it whole.
    public static string Typed(string name) => name.Contains(' ') ? "\"" + name + "\"" : name;

    /// <summary>The line for an entry, or null for one the console has no line for.</summary>
    public ConsoleLine? Line(ActivityEntry entry, long seq, bool withDate)
    {
        var body = Body(entry, out var kind);
        if (body is null) return null;
        return Build(seq, entry.At, kind, entry.Network, entry.Message?.Transport, body, entry.ChatId, entry.DisplayChatId, entry.Message?.Id,
            entry.IsMuted, entry.IsArchived, null, withDate);
    }

    private string? Body(ActivityEntry entry, out ConsoleLineKind kind)
    {
        kind = ConsoleLineKind.Event;
        switch (entry.Kind)
        {
            case ActivityKind.Message:
                if (entry.IsOutgoing)
                {
                    kind = ConsoleLineKind.Outgoing;
                    return "You → " + Conversation(entry) + ": " + (entry.Message is { } sent ? Words(sent) : "new message");
                }
                kind = ConsoleLineKind.Incoming;
                var from = entry.IsGroup ? Conversation(entry) + Chevron + Name(entry.SenderName, null) : Conversation(entry);
                return entry.Message is { } message ? from + ": " + Words(message)
                    : from + ": new message · /msgs " + Typed(Name(entry.ConversationTitle, entry.SenderName)) + " to read";
            case ActivityKind.Edited:
                return "· " + Actor(entry) + " edited" + (entry.Message is { } edited ? ": " + Words(edited) : "");
            case ActivityKind.Unsent:
                return "· " + Actor(entry) + " unsent a message";
            case ActivityKind.Reaction:
                if (entry.Detail is not { Length: > 1 } detail) return null;
                var emoji = detail[1..];
                if (detail[0] == '-') return "· " + Actor(entry) + " removed " + emoji;
                var target = entry.Message is { } reacted ? Words(reacted) : "";
                return "· " + Actor(entry) + " reacted " + emoji + (target.Length > 0 ? " to " + Quote(target) : "");
            case ActivityKind.Read:
                return "· " + (entry.IsGroup ? Name(entry.SenderName, null) : Conversation(entry)) + " read your message";
            case ActivityKind.Typing:
                return "· " + (entry.IsGroup ? Name(entry.SenderName, null) : Conversation(entry)) + " is typing…";
            case ActivityKind.Call:
                if (entry.IsOutgoing) return "· Call to " + Conversation(entry);
                return entry.Detail?.Contains("missed", StringComparison.OrdinalIgnoreCase) == true
                    ? "· Missed call from " + Caller(entry) : "· Call from " + Caller(entry);
            case ActivityKind.Voicemail:
                var transcript = string.IsNullOrWhiteSpace(entry.Detail) ? "" : MessageText.PlainText(entry.Detail).Trim();
                return "· Voicemail from " + Caller(entry) + (transcript.Length > 0 ? ": " + transcript : "");
            case ActivityKind.Connected:
                kind = ConsoleLineKind.Connection;
                return Service(entry.Network) + " connected again";
            case ActivityKind.Disconnected:
                kind = ConsoleLineKind.Connection;
                return Service(entry.Network) + " disconnected";
            case ActivityKind.SignInNeeded:
                kind = ConsoleLineKind.Connection;
                if (entry.Network == "Google Messages")
                    return entry.Detail == ActivityEntry.SignInOnly ? "Google Messages needs its sign-in renewed in Message Link" : "Google Messages needs pairing again in Message Link";
                return Service(entry.Network) + " needs you to sign in again in Message Link";
            default:
                return null;
        }
    }

    private string Caller(ActivityEntry entry) => entry.IsGroup ? Name(entry.SenderName, entry.ConversationTitle) : Conversation(entry);
    private static string Service(string network) => network.Length == 0 ? "Every service" : network;

    /// <summary>"· 3 new messages from Alex while disconnected: newest text", for entries a reconnect replayed.</summary>
    public ConsoleLine CatchUp(IReadOnlyList<ActivityEntry> entries, long seq, bool withDate)
    {
        var newest = entries.OrderBy(e => e.At).ThenBy(e => e.Sequence).Last();
        var count = entries.Count;
        var body = "· " + count.ToString(CultureInfo.CurrentCulture) + (count == 1 ? " new message from " : " new messages from ") +
            Conversation(newest) + " while disconnected" + (newest.Message is { } message ? ": " + Words(message) : "");
        return Build(seq, newest.At, ConsoleLineKind.Event, newest.Network, newest.Message?.Transport, body, newest.ChatId, newest.DisplayChatId,
            newest.Message?.Id, entries.Any(e => e.IsMuted), entries.Any(e => e.IsArchived), null, withDate);
    }

    /// <summary>"You → Alex: on my way", for a text the console itself sent.</summary>
    public ConsoleLine Sent(ConsoleRecipient to, string text, DateTimeOffset at, long seq, string localId, bool withDate)
    {
        var body = "You → " + to.Title + AccountSuffix(to.SourceChatId, to.Network, to.AccountId) + ": " + text;
        return Build(seq, at, ConsoleLineKind.Outgoing, to.Network, null, body, to.SourceChatId, to.DisplayChatId, null, false, false, localId, withDate);
    }

    /// <summary>An Info or Error line: the console's own words, with no service.</summary>
    public ConsoleLine Notice(ConsoleLineKind kind, string text, DateTimeOffset at, long seq, bool withDate) =>
        Build(seq, at, kind, "", null, text, null, null, null, false, false, null, withDate);

    /// <summary>A line of command output: its inks kept, wide (no time or tag column) when a command printed it.</summary>
    public ConsoleLine Output(IReadOnlyList<ConsoleSpan> spans, bool error, DateTimeOffset at, long seq, bool withDate, bool wide)
    {
        var body = string.Concat(spans.Select(span => span.Text));
        var plain = spans.Count == 0 || spans.Count == 1 && spans[0].Tone is ConsoleTone.Default or ConsoleTone.Error && spans[0].Family is null;
        var kind = error ? ConsoleLineKind.Error : ConsoleLineKind.Info;
        var time = wide ? "" : Time(at, withDate);
        return new ConsoleLine(seq, at, kind, "", null, body, Spoken(time, "", body.Trim()), null, null, null, null, false, false, null, time,
            plain ? null : spans, wide);
    }

    /// <summary>"messages:Lauren&gt; /route Lauren": a command line echoed after the prompt it was typed at.</summary>
    public ConsoleLine Command(string prompt, string input, DateTimeOffset at, long seq, bool withDate)
    {
        var flat = input.Replace('\n', ' ');
        return new ConsoleLine(seq, at, ConsoleLineKind.Command, "", null, prompt + " " + flat, "Command " + flat, null, null, null, null,
            false, false, null, "", [new ConsoleSpan(prompt, ConsoleTone.Accent), new ConsoleSpan(" "), new ConsoleSpan(flat, ConsoleTone.Strong)], Wide: true);
    }

    /// <summary>The line that heads a new day.</summary>
    public ConsoleLine Day(DateTimeOffset at, long seq)
    {
        var text = DayText(at);
        return new ConsoleLine(seq, at, ConsoleLineKind.Day, "", null, text, text.Trim('—', ' '), null, null, null, null, false, false);
    }

    private ConsoleLine Build(long seq, DateTimeOffset at, ConsoleLineKind kind, string network, string? transport, string body, string? source,
        string? display, string? messageId, bool muted, bool archived, string? localId, bool withDate)
    {
        var tag = Tag(network);
        var time = Time(at, withDate);
        return new ConsoleLine(seq, at, kind, tag, tag.Length == 0 ? null : BubbleColours.TagFamily(network, transport), body,
            Spoken(time, tag, body), source, display, messageId, network.Length == 0 ? null : network, muted, archived, localId, time);
    }

    /// <summary>"12:04, Messenger, Alex: hey": the columns in order, the dim dot of an event line left out.</summary>
    public static string Spoken(string time, string tag, string body)
    {
        var said = body.StartsWith("· ", StringComparison.Ordinal) ? body[2..] : body;
        return string.Join(", ", new[] { time, tag, said.Replace('\n', ' ') }.Where(part => part.Length > 0));
    }

    private static ChatSummary Stub(string id, string network, string accountId) =>
        new(id, accountId, "", network, "single", null, 0, false, false, false, false, null, null, [], null, null,
            ChatCapabilities.Unknown, [], 0);
}
