using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MessageLink.Presentation.Services;

public sealed record MessageTextRun(string Text, Uri? Link = null, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false);

// Parse data only: no HTML execution, stylesheets, images or external resources.
public static class MessageText
{
    // Bare domains need a path or www prefix so dotted prose and file names stay plain text.
    // Capture the separator rather than using lookbehind, keeping the non-backtracking matcher.
    private const string Host = @"(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}";
    // An email address opens a new mail to it, as Messages for Web links one (2026-10-01); it is never a web link.
    private static readonly Regex Urls = new(
        "(?<url>https?://[^\\s<>\"']+)|(?:^|[\\s(\\[{:;,!?])(?:(?<mail>[a-z0-9][a-z0-9._%+-]{0,63}@" + Host + ")|(?<url>(?:www\\." + Host + "(?:/[^\\s<>\"']*)?|" + Host + "/[^\\s<>\"']*)))",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    // The host must be a name or an address Windows takes too: .NET accepts "http://…:65001" (a model's elided address) with "…" as its
    // host, Windows' own Uri refuses it, and the Hyperlink given it threw, leaving the rest of a Hermes reply undrawn (2026-11-19).
    public static Uri? WebLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && RealHost(uri) && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    // IdnHost itself throws on a host IDN refuses.
    private static bool RealHost(Uri uri)
    {
        try { return Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Unknown; }
        catch (UriFormatException) { return false; }
    }
    public static bool IsWeb(Uri link) => link.Scheme is "http" or "https";
    private static Uri? MailLink(string address) =>
        Uri.TryCreate("mailto:" + address, UriKind.Absolute, out var uri) && uri.Scheme == "mailto" ? uri : null;
    private static readonly Regex BareHost = new("^" + Host + "$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly Regex Entity = new("&(?:#[0-9]+|#x[0-9a-f]+|[a-z][a-z0-9]*);", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    // A sender's own text is parsed as HTML too, so an entity decodes to whatever it names. A control character or a bidi
    // override or isolate (which turns "Invoice[RLO]fdp.exe" round to read as a PDF) is nothing anyone types: where an
    // entity produced one it is dropped, and never reaches the bubble, search, a copy or a notification.
    private static string Decode(string raw) => raw.Contains('&') ? Entity.Replace(raw, entity => Spoken(WebUtility.HtmlDecode(entity.Value))) : raw;
    internal static string Spoken(string decoded) => decoded.Any(Unspoken) ? string.Concat(decoded.Where(c => !Unspoken(c))) : decoded;
    private static bool Unspoken(char c) =>
        char.IsControl(c) && c is not ('\t' or '\n' or '\r') || (int)c is >= 0x202A and <= 0x202E || (int)c is >= 0x2066 and <= 0x2069;

    // An anchor's words stand for its link only when they do not name a different site. Words that read as an address
    // ("https://www.usps.com/track") over a link to somewhere else are the oldest phishing trick; they are shown as the
    // text they are, with an address in them linked to itself. Hosts compare without case or a leading "www.".
    internal static bool Misleading(string shown, Uri link)
    {
        var target = Site(link.IdnHost);
        foreach (Match match in Urls.Matches(shown))
        {
            if (match.Groups["mail"] is { Success: true } mail)
            { if (Site(mail.Value[(mail.Value.LastIndexOf('@') + 1)..]) != target) return true; continue; }
            var address = match.Groups["url"].Value;
            var explicitScheme = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            // An address that does not even make a link (credentials in it, say) cannot be checked, so it never stands for one.
            if (WebLink(explicitScheme ? address : "https://" + address) is not { } named || Site(named.IdnHost) != target) return true;
        }
        // A bare site name ("usps.com") on its own is an address too, though in running text it stays plain (Urls).
        var whole = shown.Trim().TrimEnd('.');
        return BareHost.IsMatch(whole) && Site(whole) != target;
    }
    private static string Site(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    // Walk never shows these, so they are never part of what an anchor's words say either.
    private static bool Unshown(HtmlNode node) => node.NodeType == HtmlNodeType.Comment ||
        node.Name.ToLowerInvariant() is "script" or "style" or "iframe" or "object" or "template" or "mx-reply";
    private static bool Block(string name) => name is "p" or "div" or "blockquote" or "li" or "pre" or "h1" or "h2" or "h3";

    // An anchor is judged on exactly the words Walk shows for it. InnerText also holds a hidden reply quote's or frame's text,
    // so an address hidden there would vouch for the link while the bubble showed another site's name over it.
    private static void Shown(HtmlNode node, int depth, StringBuilder text)
    {
        if (depth > 64) return;
        if (node.NodeType == HtmlNodeType.Text) { text.Append(Decode(((HtmlTextNode)node).Text)); return; }
        if (Unshown(node)) return;
        var name = node.Name.ToLowerInvariant();
        if (name == "br") { text.Append('\n'); return; }
        var block = Block(name);
        if (block) text.Append('\n');
        if (name == "li") text.Append("• ");
        foreach (var child in node.ChildNodes) Shown(child, depth + 1, text);
        if (block) text.Append('\n');
    }

    public static IReadOnlyList<MessageTextRun> Parse(string text)
    {
        if (text.Length > 65536) text = text[..65536] + "…";
        var document = new HtmlDocument();
        document.LoadHtml(text);
        var runs = new List<MessageTextRun>();
        Walk(document.DocumentNode, null, false, false, false, 0);
        return runs;

        void AddText(string value, Uri? link, bool bold, bool italic, bool code, bool autoLink = true)
        {
            if (value.Length == 0) return;
            if (link is not null || code || !autoLink) { runs.Add(new(value, link, bold, italic, code)); return; }
            Linked(value, runs, bold, italic, code, strike: false);
        }
        void Break()
        { if (runs.Count > 0 && !runs[^1].Text.EndsWith('\n')) runs.Add(new("\n")); }
        void Walk(HtmlNode node, Uri? link, bool bold, bool italic, bool code, int depth, bool autoLink = true)
        {
            if (depth > 64) return;
            if (node.NodeType == HtmlNodeType.Text)
            { AddText(Decode(((HtmlTextNode)node).Text), link, bold, italic, code, autoLink); return; }
            if (Unshown(node)) return;
            var name = node.Name.ToLowerInvariant();
            if (name == "br") { runs.Add(new("\n")); return; }
            var block = Block(name);
            if (block) Break();
            if (name == "li") runs.Add(new("• "));
            if (name == "a")
            {
                link = WebLink(WebUtility.HtmlDecode(node.GetAttributeValue("href", "")));
                // A link whose words name another site is shown as its words, linked only where they are an address.
                var words = new StringBuilder();
                foreach (var child in node.ChildNodes) Shown(child, depth + 1, words);
                if (link is not null && Misleading(words.ToString(), link)) { link = null; autoLink = true; }
                else autoLink = false;
            }
            bold |= name is "b" or "strong";
            italic |= name is "i" or "em";
            code |= name is "code" or "pre";
            foreach (var child in node.ChildNodes) Walk(child, link, bold, italic, code, depth + 1, autoLink);
            if (block) Break();
        }
    }
    // Words as runs in one style, each address and mail address in them a link of its own (Parse, HermesMarkdown).
    internal static void Linked(string value, List<MessageTextRun> runs, bool bold, bool italic, bool code, bool strike)
    {
        var position = 0;
        foreach (Match match in Urls.Matches(value))
        {
            if (match.Groups["mail"] is { Success: true } mail)
            {
                // Followed by a path it is not an address anyone writes to; it stays plain, as it always was.
                if (mail.Index + mail.Length < value.Length && value[mail.Index + mail.Length] == '/') continue;
                var mailbox = mail.Value.TrimEnd('.');
                if (mail.Index > position) runs.Add(new(value[position..mail.Index], null, bold, italic, code, strike));
                runs.Add(new(mailbox, MailLink(mailbox), bold, italic, code, strike));
                position = mail.Index + mailbox.Length;
                continue;
            }
            var address = match.Groups["url"];
            var explicitScheme = address.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                address.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            // Do not turn a suffix inside another URI scheme into a separate web link.
            if (!explicitScheme && address.Index > 0 && value[address.Index - 1] == ':')
            {
                var start = address.Index - 2;
                while (start >= 0 && char.IsLetter(value[start])) start--;
                var prefix = value[(start + 1)..(address.Index - 1)].ToLowerInvariant();
                if (prefix is "javascript" or "data" or "mailto" or "file" or "ftp" or "tel") continue;
            }
            var target = address.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            while (target.EndsWith(')') && target.Count(c => c == ')') > target.Count(c => c == '(')) target = target[..^1];
            while (target.EndsWith(']') && target.Count(c => c == ']') > target.Count(c => c == '[')) target = target[..^1];
            if (address.Index > position) runs.Add(new(value[position..address.Index], null, bold, italic, code, strike));
            runs.Add(new(target, WebLink(explicitScheme ? target : "https://" + target), bold, italic, code, strike));
            position = address.Index + target.Length;
        }
        if (position < value.Length) runs.Add(new(value[position..], null, bold, italic, code, strike));
    }
    public static string PlainText(string text) => string.Concat(Parse(text).Select(run => run.Text)).TrimEnd('\n');

    // A Hermes thread holds text as Hermes (or a report posted to it) wrote it: Markdown or plain words, never HTML. Every other body is parsed as HTML, so
    // a reply or report reading "<a href=...>" would become a link and "&amp;" an ampersand. Its rows are escaped first and shown exactly as typed; a bare
    // address is still linked, never previewed or opened by itself. Escaping on the way in would not do: Android shows text as it arrives.
    public static bool IsLiteral(string? chatId) => MessageLink.Core.Services.HermesIds.Owns(chatId);
    public static string Literal(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    /// <summary>A message body as <see cref="Parse"/> reads it: literal in a Hermes thread, as sent everywhere else.</summary>
    public static string Markup(string? chatId, string? text) => text is null ? "" : IsLiteral(chatId) ? Literal(text) : text;
    public static string Markup(MessageLink.Core.Messaging.ChatMessage message) => Markup(message.ChatId, message.Text);
    /// <summary>A message's words as a copy, a search result or the console shows them.</summary>
    public static string PlainText(MessageLink.Core.Messaging.ChatMessage message) => PlainText(Markup(message));
    /// <summary>A reply Hermes wrote, which the bubble draws from its Markdown (HermesMarkdown).</summary>
    public static bool IsMarkdown(MessageLink.Core.Messaging.ChatMessage message) =>
        !message.IsOutgoing && !message.IsDeleted && !message.IsHidden && IsLiteral(message.ChatId) && !string.IsNullOrWhiteSpace(message.Text);
    /// <summary>
    /// A message's words to read at a glance, in the list's preview line and a notification: a Hermes reply without its Markdown marks.
    /// A copy, search and mlink keep the words as written (PlainText).
    /// </summary>
    public static string Readable(MessageLink.Core.Messaging.ChatMessage message) => IsMarkdown(message) ? HermesMarkdown.PlainText(message.Text) : PlainText(message);
}
