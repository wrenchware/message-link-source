using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class SharedLinkPresentation
{
    public static Uri? Destination(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        string.IsNullOrEmpty(uri.UserInfo) ? uri : null;

    public static bool IsInstagram(Uri? uri) => uri is { Scheme: "https", IsDefaultPort: true } &&
        (uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase));

    // Facebook's own addresses, held to the same rule as Instagram's: https on the default port, and the host itself or
    // one of its subdomains, so a look-alike host or an address carrying a port is an ordinary link.
    public static bool IsFacebook(Uri? uri) => uri is { Scheme: "https", IsDefaultPort: true } &&
        (uri.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("fb.watch", StringComparison.OrdinalIgnoreCase));

    // Only the forms Facebook itself uses for a reel. Its videos are named so they are not mistaken for one; posts, pages
    // and everything else keep the ordinary link card.
    public static string? FacebookKind(Uri? uri)
    {
        if (!IsFacebook(uri)) return null;
        if (uri!.Host.Equals("fb.watch", StringComparison.OrdinalIgnoreCase)) return "Video";
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part.ToLowerInvariant()).ToArray() switch
        {
            ["reel" or "reels", ..] or ["share", "r", ..] => "Reel",
            ["share", "v", ..] or ["watch", ..] => "Video",
            _ => null
        };
    }

    // A reel from either service is drawn the same way: its full portrait frame, then what it is and where it opens.
    public static bool IsReel(Uri? uri) => InstagramKind(uri) == "Reel" || FacebookKind(uri) == "Reel";
    // The line under a shared item saying what it is, where the service is known; null leaves the card's host.
    public static string? ShareLabel(Uri? uri) =>
        InstagramKind(uri) is { } kind ? kind == GenericInstagramKind ? "Instagram" : "Instagram · " + kind :
        FacebookKind(uri) == "Reel" ? "Facebook · Reel" : null;

    // Whether a message's words are only the address a reel card under them already stands for, so the bubble above the
    // card can go. Ordinary link previews keep their address (MessageBubble: a link you cannot read is worse than a
    // repeated one), but a reel card names what it is, where it opens and follows the link itself. Only a body that is
    // one address and nothing else goes, and only beside exactly one reel card from the same service: the address a
    // person pastes often differs from the card's (a share code, a /share/r/ address Facebook resolves to /reel/), so
    // the service and kind say they are the same reel rather than the characters. Any other words keep the whole body.
    public static bool CardReplacesText(string? text, IReadOnlyList<MessageAttachment> attachments)
    {
        var words = text?.Trim();
        if (string.IsNullOrEmpty(words) || words.Any(char.IsWhiteSpace) || Destination(words) is not { } typed || !IsReel(typed)) return false;
        var reels = attachments.Where(PhotoPresentation.IsPhoto).Select(item => Destination(item.Link)).Where(IsReel).ToArray();
        return reels.Length == 1 && IsInstagram(reels[0]) == IsInstagram(typed);
    }

    public static string OpenLabel(Uri? uri) => IsInstagram(uri) ? "Open in Instagram" : FacebookKind(uri) == "Reel" ? "Open in Facebook" : "Open link";

    // Reserve the footprint before decoding. Instagram previews and reels may be portrait or square;
    // ordinary webpage cards retain their compact height. The native control fills this
    // bounded slot with a centered crop, without changing its size when the image decodes.
    public static double PreviewHeight(Uri? destination, double? sourceWidth, double? sourceHeight)
    {
        const double cardWidth = 300;
        var instagram = IsInstagram(destination);
        var reel = IsReel(destination);
        // A portrait reel needs its full 9:16 frame above the separate caption.
        // The old 400px ceiling cropped embedded text near the bottom of the preview.
        var maximumHeight = reel ? cardWidth * 16 / 9 : instagram ? 400 : 220;
        var known = sourceWidth is > 0 && double.IsFinite(sourceWidth.Value) &&
            sourceHeight is > 0 && double.IsFinite(sourceHeight.Value);
        var height = known ? cardWidth * (sourceHeight!.Value / sourceWidth!.Value)
            : reel ? maximumHeight : 180;
        return Math.Clamp(height, 140, maximumHeight);
    }

    public static string? InstagramKind(Uri? uri) => !IsInstagram(uri) ? null :
        uri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() switch
        {
            "reel" or "reels" => "Reel",
            "p" => "Post",
            "stories" => "Story",
            "music" or "audio" => "Music",
            _ => GenericInstagramKind
        };

    private const string GenericInstagramKind = "Instagram share";

    // What the helpers call a preview that arrived without a title of its own. The conversation's card hides it too.
    public const string UntitledPreview = "Shared link";

    // A title worth a line of its own, or none. The host, the address itself and the helpers' placeholder all repeat
    // what the address line below already says, and a title that only repeats it pushes the address out of sight.
    public static string? Title(string? name, Uri link)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var title = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Equals(UntitledPreview, StringComparison.OrdinalIgnoreCase)) return null;
        var bare = Bare(title);
        var (host, rest) = Address(link);
        return bare.Equals(Bare(host), StringComparison.OrdinalIgnoreCase) ||
            bare.Equals(Bare(host + rest), StringComparison.OrdinalIgnoreCase) ||
            bare.Equals(Bare(link.AbsoluteUri), StringComparison.OrdinalIgnoreCase) ? null : title;

        static string Bare(string value)
        {
            var text = value.Trim();
            foreach (var scheme in new[] { "https://", "http://" })
                if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { text = text[scheme.Length..]; break; }
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
            return text.TrimEnd('/');
        }
    }

    // The whole address without its scheme, split where the host ends so the host can be drawn heavier than the path.
    // Unescaped where that is safe to read. Anything that draws nothing — controls, bidirectional marks, zero-width and
    // other ignorable characters, blank fillers, spaces — stays escaped, and so do letters of right-to-left scripts,
    // which carry the slashes and digits beside them into their own order; a host written in one is shown in its ASCII
    // form for the same reason. What is drawn is what the link says, in the order it says it. A path of just "/" says
    // nothing and is left off.
    public static (string Host, string Path) Address(Uri link)
    {
        var name = link.Host.EnumerateRunes().Any(RightToLeft) ? link.IdnHost : link.Host;
        var host = link.IsDefaultPort ? name : name + ":" + link.Port;
        var rest = link.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.SafeUnescaped);
        if (rest == "/") rest = "";
        return (Visible(host), Visible(rest));
    }

    private static string Visible(string value)
    {
        var text = new System.Text.StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (!Hidden(rune)) { text.Append(rune.ToString()); continue; }
            var count = rune.EncodeToUtf8(bytes);
            foreach (var b in bytes[..count]) text.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static bool Hidden(System.Text.Rune value) => System.Text.Rune.IsControl(value) || System.Text.Rune.IsWhiteSpace(value) ||
        RightToLeft(value) || System.Text.Rune.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned ||
        // Ignorable characters outside the format category, which draw nothing either: the combining grapheme joiner,
        // the Hangul fillers, Khmer's inherent vowels, the Mongolian and other variation selectors, and the braille blank.
        value.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x2800 or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFFA0 or (>= 0xE0100 and <= 0xE01EF);

    // Hebrew, Arabic and the other right-to-left scripts, with their presentation forms and numerals.
    private static bool RightToLeft(System.Text.Rune value) => value.Value is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF)
        or (>= 0xFE70 and <= 0xFEFE) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    // What a message's own conversation calls its service. Nothing when the conversation at hand is not the message's
    // own — a merged conversation stands in for a member it cannot find, and "Merged" is not a service.
    public static string? SourceService(string chatId, ChatSummary? source) =>
        source is { } chat && chat.Id == chatId && !MergedConversations.IsMerged(chat.Id) && !string.IsNullOrWhiteSpace(chat.Network)
            ? chat.Network.Trim() : null;

    // The service a message came through, named from its own conversation wherever the app knows it: the one at hand
    // when that is the message's own, otherwise whichever conversation the merged conversations know by that id, which
    // covers a merged conversation's members whether or not its history is on screen. Named with its account when more
    // than one account of that service is known, as a merged conversation's route is. The id's prefix is the last resort.
    public static string ServiceFor(string chatId, ChatSummary? known, MergedConversations? merges)
    {
        var source = SourceService(chatId, known) is not null ? known : merges?.Source(chatId);
        var own = SourceService(chatId, source);
        return Service(chatId, own is null ? null : merges?.ServiceRouteLabel(source!) ?? own);
    }

    // Which service a shared item came through. The source conversation says so outright where it can be reached; the
    // conversation id's prefix is the fallback, which cannot tell two accounts apart and calls anything it does not
    // recognise Google Messages.
    public static string Service(string chatId, string? source) =>
        !string.IsNullOrWhiteSpace(source) ? source.Trim() :
        chatId.StartsWith("instagram:", StringComparison.Ordinal) ? "Instagram" :
        chatId.StartsWith("messenger:", StringComparison.Ordinal) ? "Messenger" :
        chatId.StartsWith("gvoice:", StringComparison.Ordinal) ? "Google Voice" : "Google Messages";

    public static string Detail(DateTimeOffset at, string service) => $"{at.ToLocalTime():g} · {service}";
}
