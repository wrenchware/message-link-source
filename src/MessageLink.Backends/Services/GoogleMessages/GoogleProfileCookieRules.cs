using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

/// <summary>
/// Which of the sign-in browser's cookies travel to a server, and which a server will seed (SWITCHBOARD.md 5.4). The client applies it as it collects and the
/// server applies it again as it receives, so a peer that sends more than it should gets no further than the rules: only Google's own domains, no more than
/// <see cref="MaxCount"/> cookies and <see cref="MaxBytes"/> of them. The values are credentials; nothing here logs or throws with one.
/// </summary>
internal static class GoogleProfileCookieRules
{
    public const int MaxCount = 200;
    public const int MaxBytes = 64 * 1024;
    // A browser keeps no cookie whose name and value together pass 4096 bytes, so a bigger one is not a cookie of this sign-in.
    private const int MaxCookieBytes = 4096;

    /// <summary>A host that is google.com or under it. Not <c>notgoogle.com</c>, and not google.com as a prefix of another name.</summary>
    public static bool IsGoogleHost(string? host) => host is { Length: > 0 }
        && (host.Equals("google.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase))
        && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    /// <summary>
    /// One cookie as the sign-in browser reports it, or null when it is not one to hand over. <paramref name="domain"/> is the browser's own spelling, where a
    /// leading dot means the domain and its subdomains and no dot means that host alone (WebView2's <c>Domain</c>); a <c>__Host-</c> cookie is host-only whatever
    /// it says (<see cref="Accept"/>).
    /// </summary>
    public static GoogleProfileCookie? FromBrowser(string? name, string? value, string? domain, string? path, bool secure, bool httpOnly, string? sameSite, long? expires)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(domain)) return null;
        var hostOnly = !domain.StartsWith('.') || name.StartsWith("__Host-", StringComparison.Ordinal);
        return Accept(new GoogleProfileCookie(name, value, domain.TrimStart('.'), hostOnly, path ?? "/", secure, httpOnly, sameSite, expires));
    }

    /// <summary>The cookie, made safe to seed, or null when it must not be: a foreign domain, a name or value the browser could not hold, a path that is not a path.</summary>
    public static GoogleProfileCookie? Accept(GoogleProfileCookie? cookie)
    {
        if (cookie is not { Name: { Length: > 0 } name, Value: { Length: > 0 } value, Domain: { } rawDomain }) return null;
        var domain = rawDomain.TrimStart('.');
        if (!IsGoogleHost(domain)) return null;
        if (name.Length + value.Length > MaxCookieBytes || HasControl(name) || HasControl(value) || name.Any(c => c is ';' or '=' or ' ' or ',') || value.Any(c => c is ';')) return null;
        var path = cookie.Path is { Length: > 0 } given ? given : "/";
        if (path[0] != '/' || path.Length > 512 || HasControl(path) || path.Any(c => c is ';')) return null;
        // A __Host- cookie is for the host that set it, on every path, over HTTPS, and a __Secure- one over HTTPS: a browser refuses either otherwise, so
        // they are made so here rather than left to fail when seeded.
        var prefixedHost = name.StartsWith("__Host-", StringComparison.Ordinal);
        var prefixedSecure = prefixedHost || name.StartsWith("__Secure-", StringComparison.Ordinal);
        var sameSite = cookie.SameSite switch
        {
            { } text when text.Equals("none", StringComparison.OrdinalIgnoreCase) => "None",
            { } text when text.Equals("lax", StringComparison.OrdinalIgnoreCase) => "Lax",
            { } text when text.Equals("strict", StringComparison.OrdinalIgnoreCase) => "Strict",
            _ => null,
        };
        return new GoogleProfileCookie(name, value, domain.ToLowerInvariant(), cookie.HostOnly || prefixedHost, prefixedHost ? "/" : path, cookie.Secure || prefixedSecure,
            cookie.HttpOnly, sameSite, cookie.Expires);
    }

    /// <summary>
    /// Every acceptable cookie of <paramref name="cookies"/>, once each (the same name, domain, host-only-ness and path is one cookie, the later one standing), within
    /// <see cref="MaxCount"/> and <see cref="MaxBytes"/>. When they do not all fit, the cookies the helper itself reads
    /// (<see cref="GoogleSignInRules.CookieNames"/>) go first and the rest follow in the order given, so a cap never costs the sign-in its own cookies.
    /// </summary>
    public static IReadOnlyList<GoogleProfileCookie> Select(IEnumerable<GoogleProfileCookie?>? cookies)
    {
        if (cookies is null) return [];
        var unique = new Dictionary<(string Name, string Domain, bool HostOnly, string Path), GoogleProfileCookie>();
        var order = new List<(string, string, bool, string)>();
        foreach (var raw in cookies)
        {
            if (Accept(raw) is not { } cookie) continue;
            var key = (cookie.Name, cookie.Domain, cookie.HostOnly, cookie.Path);
            if (!unique.ContainsKey(key)) order.Add(key);
            unique[key] = cookie;
        }
        var kept = new List<GoogleProfileCookie>();
        var bytes = 0;
        foreach (var key in order.OrderBy(key => GoogleSignInRules.CookieNames.Contains(key.Item1, StringComparer.Ordinal) ? 0 : 1))
        {
            var cookie = unique[key];
            var size = cookie.Name.Length + cookie.Value.Length + cookie.Domain.Length + cookie.Path.Length;
            if (kept.Count >= MaxCount) break;
            if (bytes + size > MaxBytes) continue;
            kept.Add(cookie);
            bytes += size;
        }
        return kept;
    }

    // Names are the only thing logged; a name with anything but letters, digits and a few marks is counted, not shown.
    public static bool IsLoggableName(string name) => name.Length is > 0 and <= 48 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool HasControl(string text) => text.Any(char.IsControl);
}
