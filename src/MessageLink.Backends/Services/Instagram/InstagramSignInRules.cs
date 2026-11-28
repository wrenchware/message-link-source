namespace MessageLink.Instagram.Spike;

internal static class InstagramSignInRules
{
    public const string LoginUrl = "https://www.instagram.com/accounts/login/";
    public const string CookieUrl = "https://www.instagram.com/";
    public static readonly string[] RequiredCookies = ["sessionid", "csrftoken", "ds_user_id", "mid", "ig_did"];

    public static bool IsInstagramHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && IsAllowedHost(uri.Host);

    public static bool IsLoginNavigation(string? value) => IsInstagramHttps(value) &&
        // Even an inbox landing page is unnecessary for cookie capture. Block the
        // whole messaging UI, including escaped routes that the server decodes.
        !Uri.UnescapeDataString(new Uri(value!).AbsolutePath).StartsWith("/direct", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedHost(string host) =>
        host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);

    // Instagram's "Log in with Facebook" leaves for Facebook, which this window never follows: the allowlist above stays
    // exactly as it is. This only recognises where a refused navigation was going, so the window can say why nothing
    // happened instead of cancelling it silently.
    public static bool IsFacebook(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && (
            uri.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("fb.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".fb.com", StringComparison.OrdinalIgnoreCase));

    public static string? ReadCookie(string name, string domain, string value)
    {
        // Keep Instagram's first-party additions as well as the required five;
        // never enumerate another browser profile or copy third-party cookies.
        if (!IsAllowedHost(domain.TrimStart('.')) || name.Length is 0 or > 256 ||
            name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c))) return null;
        return value is { Length: > 0 and < 32768 } ? value : null;
    }
}
