namespace MessageLink.App.Services.Messenger;

// What the Facebook sign-in window is allowed to navigate to, and which cookies it may keep.
internal static class MessengerSignInRules
{
    // Facebook's own login page. The user signs in exactly as they would in a browser, passkey included.
    public const string LoginUrl = "https://www.facebook.com/login/";
    public const string CookieUrl = "https://www.facebook.com/";
    // mautrix-meta needs these three; the rest are kept when Facebook sets them.
    public static readonly string[] RequiredCookies = ["xs", "c_user", "datr"];
    public static readonly string[] CookieNames = [.. RequiredCookies, "sb", "fr", "wd", "presence", "oo", "dpr"];

    private static readonly string[] Hosts = ["facebook.com", "messenger.com"];

    public static bool IsLoginNavigation(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && IsAllowedHost(uri.Host);

    private static bool IsAllowedHost(string host) => Hosts.Any(allowed =>
        host.Equals(allowed, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));

    public static string? ReadCookie(string name, string domain, string value)
    {
        if (!CookieNames.Contains(name, StringComparer.Ordinal)) return null;
        var host = domain.TrimStart('.');
        if (host != "facebook.com" && host != "messenger.com") return null;
        return value is { Length: > 0 and < 32768 } ? value : null;
    }
}
