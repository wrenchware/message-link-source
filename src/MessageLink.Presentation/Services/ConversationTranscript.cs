using System.Globalization;
using System.Net;
using System.Text;
using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// One exported message: when, who, what it said (with its attachments, edit mark and reactions already written in), the
// service it came through when the conversation is merged, and the line quoting what it replied to.
public sealed record TranscriptLine(DateTimeOffset At, string Who, string Text, string? Service, bool Outgoing, string? ReplyPrefix);

// A conversation written out as a text file or a self-contained web page. Pure: messages in, text out. Only what the
// caller hands it is written, so an export holds exactly the messages that window had loaded.
//
// Addresses never leave the app this way. An attachment's SourceUrl and a participant's AvatarUrl are CDN addresses that
// can carry access tokens, and a shared link's address can carry tracking or signed parameters, so none of them is read
// here at all. The page has no script, no image and nothing fetched from anywhere: it is readable offline, forever.
public static class ConversationTranscript
{
    private const int ReplyQuoteLength = 60;

    public static IReadOnlyList<TranscriptLine> Lines(IEnumerable<ChatMessage> messages, Func<ChatMessage, string> service,
        bool showService, TimeZoneInfo zone) => Lines(messages, service, showService, zone, null);

    // who: the name the conversation shows for a sender, when the caller knows better than the message itself (a
    // participant's saved name). Outgoing messages are always "You".
    public static IReadOnlyList<TranscriptLine> Lines(IEnumerable<ChatMessage> messages, Func<ChatMessage, string> service,
        bool showService, TimeZoneInfo zone, Func<ChatMessage, string>? who)
    {
        _ = zone; // Times stay instants here; the zone decides days and clock times when they are written.
        var shown = messages.Where(message => !message.IsHidden)
            .OrderBy(message => message.Timestamp).ThenBy(message => message.SortKey, StringComparer.Ordinal).ToArray();
        // Replies are resolved from this list only: nothing is fetched to fill in a quote.
        var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var message in shown) byId.TryAdd(message.Id, message);
        string Who(ChatMessage message) => message.IsOutgoing ? "You"
            : who?.Invoke(message) is { Length: > 0 } name ? name
            : string.IsNullOrWhiteSpace(message.SenderName) ? "Unknown sender" : message.SenderName!;
        var lines = new List<TranscriptLine>(shown.Length);
        foreach (var message in shown)
        {
            string? reply = null;
            if (message.ReplyToId is { Length: > 0 } replyId && !message.IsDeleted)
                reply = byId.TryGetValue(replyId, out var original)
                    ? $"↪ {Who(original)}: {Quote(original)}"
                    : "↪ (earlier message)";
            var system = message.Type == "system";
            lines.Add(new(message.Timestamp, system ? "" : Who(message), Body(message), showService ? service(message) : null,
                message.IsOutgoing, reply));
        }
        return lines;
    }

    public static string ToText(string header, IReadOnlyList<TranscriptLine> lines, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.Append(header.Trim()).Append("\r\n");
        DateTime? day = null;
        const string Indent = "        ";
        foreach (var line in lines)
        {
            var local = TimeZoneInfo.ConvertTime(line.At, zone);
            if (day != local.Date)
            {
                day = local.Date;
                text.Append("\r\n").Append("— ").Append(DayHeading(local)).Append(" —").Append("\r\n");
            }
            if (line.ReplyPrefix is { } reply) text.Append(Indent).Append(OneLine(reply)).Append("\r\n");
            text.Append('[').Append(local.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ");
            if (line.Service is { Length: > 0 } service) text.Append('[').Append(service).Append("] ");
            if (line.Who.Length > 0) text.Append(line.Who).Append(": ");
            var body = line.Text.Replace("\r\n", "\n").Split('\n');
            text.Append(body[0]).Append("\r\n");
            foreach (var more in body.Skip(1)) text.Append(Indent).Append(more).Append("\r\n");
        }
        return text.ToString();
    }

    public static string ToHtml(string title, string header, IReadOnlyList<TranscriptLine> lines, TimeZoneInfo zone)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var incomingLight = BubbleColours.Incoming(false);
        var incomingDark = BubbleColours.Incoming(true);
        var accent = BubbleColours.Swatch(BubbleColours.DefaultSwatch(BubbleFamily.GoogleRcs))!;
        var page = new StringBuilder();
        page.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(E(title)).Append("</title>\n<style>\n")
            .Append(":root{color-scheme:light dark;")
            .Append("--page:#FAFAFA;--text:#1B1B1B;--muted:#5F6368;")
            .Append("--in:").Append(BubbleColours.Hex(incomingLight)).Append(";--in-ink:").Append(BubbleColours.Hex(BubbleColours.Ink(incomingLight))).Append(';')
            .Append("--out:").Append(BubbleColours.Hex(accent.Light)).Append(";--out-ink:").Append(BubbleColours.Hex(BubbleColours.Ink(accent.Light))).Append(";}\n")
            .Append("@media (prefers-color-scheme: dark){:root{--page:#202020;--text:#F3F3F3;--muted:#A8ABB0;")
            .Append("--in:").Append(BubbleColours.Hex(incomingDark)).Append(";--in-ink:").Append(BubbleColours.Hex(BubbleColours.Ink(incomingDark))).Append(';')
            .Append("--out:").Append(BubbleColours.Hex(accent.Dark)).Append(";--out-ink:").Append(BubbleColours.Hex(BubbleColours.Ink(accent.Dark))).Append(";}}\n")
            .Append("body{margin:0;background:var(--page);color:var(--text);font:15px/1.4 \"Segoe UI Variable Text\",\"Segoe UI\",system-ui,sans-serif;}\n")
            .Append("main{max-width:760px;margin:0 auto;padding:24px 16px 48px;}\n")
            .Append("h1{font-size:22px;font-weight:600;margin:0 0 6px;}\n")
            .Append(".note{color:var(--muted);font-size:13px;margin:0 0 20px;}\n")
            .Append("h2{color:var(--muted);font-size:12px;font-weight:600;text-align:center;margin:22px 0 10px;}\n")
            .Append(".row{display:flex;flex-direction:column;margin:6px 0;}\n")
            .Append(".row.out{align-items:flex-end;}.row.in{align-items:flex-start;}\n")
            .Append(".meta{color:var(--muted);font-size:12px;margin:0 6px 2px;}\n")
            .Append(".reply{color:var(--muted);font-size:12px;border-left:2px solid var(--muted);padding-left:6px;margin:0 6px 3px;max-width:75%;overflow-wrap:anywhere;}\n")
            .Append(".bubble{max-width:75%;padding:7px 12px;border-radius:16px;white-space:pre-wrap;overflow-wrap:anywhere;}\n")
            .Append(".in .bubble{background:var(--in);color:var(--in-ink);}.out .bubble{background:var(--out);color:var(--out-ink);}\n")
            .Append(".system{color:var(--muted);font-size:12px;text-align:center;margin:8px 0;white-space:pre-wrap;}\n")
            .Append("@media (forced-colors: active){.bubble{border:1px solid CanvasText;}}\n")
            .Append("</style>\n</head>\n<body>\n<main>\n")
            .Append("<h1>").Append(E(title)).Append("</h1>\n")
            .Append("<p class=\"note\">").Append(E(header.Trim())).Append("</p>\n");
        DateTime? day = null;
        foreach (var line in lines)
        {
            var local = TimeZoneInfo.ConvertTime(line.At, zone);
            if (day != local.Date)
            {
                day = local.Date;
                page.Append("<h2>").Append(E(DayHeading(local))).Append("</h2>\n");
            }
            var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (line.Who.Length == 0)
            {
                page.Append("<p class=\"system\">").Append(E(time)).Append(" · ").Append(E(line.Text)).Append("</p>\n");
                continue;
            }
            var meta = string.Join(" · ", new[] { time, line.Service, line.Who }.Where(part => !string.IsNullOrEmpty(part)));
            page.Append("<div class=\"row ").Append(line.Outgoing ? "out" : "in").Append("\">")
                .Append("<div class=\"meta\">").Append(E(meta)).Append("</div>");
            if (line.ReplyPrefix is { } reply) page.Append("<div class=\"reply\">").Append(E(OneLine(reply))).Append("</div>");
            page.Append("<div class=\"bubble\">").Append(E(line.Text)).Append("</div></div>\n");
        }
        page.Append("</main>\n</body>\n</html>\n");
        return page.ToString();
    }

    // The words a message is written with: its text, what it carried, whether it was edited, and how people reacted.
    private static string Body(ChatMessage message)
    {
        if (message.IsDeleted) return "(message removed)";
        if (message.CallKind is { } call) return Call(call, message);
        var text = Content(message);
        if (message.EditedAt is not null) text += " (edited)";
        var reactions = message.Reactions.GroupBy(reaction => reaction.Key, StringComparer.Ordinal)
            .Select((group, order) => (group.Key, Count: group.Select(reaction => reaction.ParticipantId).Distinct(StringComparer.Ordinal).Count(), order))
            .OrderByDescending(group => group.Count).ThenBy(group => group.order)
            .Select(group => $"{group.Key} {group.Count}").ToArray();
        if (reactions.Length > 0) text += $" ({string.Join(", ", reactions)})";
        return text;
    }

    private static string Content(ChatMessage message)
    {
        var text = MessageText.PlainText(MetaAiMessages.Text(message) ?? "").Trim();
        var captions = PhotoPresentation.Captions(message);
        if (captions.Length > 0) captions = MessageText.PlainText(captions).Trim();
        var labels = string.Join(" ", message.Attachments.Select(Attachment));
        var parts = new[] { labels, text, captions }.Where(part => part.Length > 0).ToArray();
        return parts.Length > 0 ? string.Join(" ", parts) : $"[{message.Type ?? "Message"}]";
    }

    // The call card's own words (CallEventCard.Headline). A voicemail's transcript follows it; its recording does not.
    private static string Call(string kind, ChatMessage message)
    {
        var text = MessageText.PlainText(message.Text ?? "").Trim();
        switch (kind)
        {
            case "voicemail":
                const string prefix = "Voicemail:";
                var spoken = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..].Trim() : text;
                return spoken.Length > 0 ? $"Voicemail: {spoken}" : "Voicemail";
            case "missed": return "Missed voice call";
            case "unanswered": return "Unanswered voice call";
            case "incoming": return "Incoming voice call";
            case "outgoing": return "Outgoing voice call";
            default: return text.Length > 0 ? text : "Call history";
        }
    }

    private static string Attachment(MessageAttachment attachment)
    {
        if (attachment.IsVoiceNote) return "[Voice message]";
        if (attachment.IsGif) return "[GIF]";
        if (attachment.IsSticker) return "[Sticker]";
        // A shared post or link: its title, never its address.
        if (attachment.Link is not null) return SafeName(attachment.Name) is { } title ? $"[Link: {title}]" : "[Link]";
        if (PhotoPresentation.IsPhoto(attachment)) return "[Photo]";
        if (attachment.Type == "video" || attachment.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return "[Video]";
        return SafeName(attachment.Name) is { } name ? $"[File: {name}]" : "[File]";
    }

    // A file's name, never anything that is really an address.
    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("://", StringComparison.Ordinal)) return null;
        var single = OneLine(name).Trim();
        return single.Length == 0 ? null : single;
    }

    private static string Quote(ChatMessage original)
    {
        var text = OneLine(original.IsDeleted ? "(message removed)" : original.CallKind is { } call ? Call(call, original) : Content(original)).Trim();
        if (text.Length <= ReplyQuoteLength) return text;
        // Never half an emoji: a cut through a surrogate pair leaves a character no file can hold.
        var cut = char.IsHighSurrogate(text[ReplyQuoteLength - 1]) ? ReplyQuoteLength - 1 : ReplyQuoteLength;
        return text[..cut].TrimEnd() + "…";
    }

    private static string OneLine(string value) => value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static string DayHeading(DateTimeOffset local) => local.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
}
