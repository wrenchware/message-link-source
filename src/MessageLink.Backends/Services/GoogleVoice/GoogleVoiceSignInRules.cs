namespace MessageLink.App.Services.GoogleVoice;

internal static class GoogleVoiceSignInRules
{
    // Upstream's documented starting point: signing in continues to the Voice web app, which sets its own cookie.
    public const string LoginUrl = "https://voice.google.com/signup";
    public const string VoiceUrl = "https://voice.google.com/";
    public const string ApiUrl = "https://clients6.google.com/";
    // libgv needs the Google account cookies; OSID and COMPASS belong to Voice and its API host, and Google sometimes
    // requires __Secure-1PSIDTS as well. Voicemail audio is served from www.google.com, which refuses those alone: it
    // wants the secure first-party session cookies a browser also sends there.
    public static readonly string[] RequiredCookies = ["SID", "HSID", "SSID", "APISID", "SAPISID"];
    public static readonly string[] CookieNames = [.. RequiredCookies, "OSID", "COMPASS", "__Secure-1PSIDTS",
        "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PAPISID", "__Secure-3PAPISID", "__Secure-3PSIDTS"];

    public static bool IsLoginNavigation(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        if (uri.Host == "google.com" || uri.Host.EndsWith(".google.com", StringComparison.Ordinal)) return true;
        // Google may finish its own account SSO by briefly visiting this exact YouTube cookie handoff before returning
        // to Voice. Keep the exception narrower than the prior all-HTTPS rule: no other YouTube server or path is allowed.
        return uri.Host == "accounts.youtube.com" &&
            uri.AbsolutePath.Equals("/accounts/SetSID", StringComparison.OrdinalIgnoreCase);
    }

    // Background recovery is deliberately narrower than interactive sign-in. The Voice landing page normally redirects
    // into an account's /u/<n>/ route, so allow only Voice app pages and never account-picker, sign-in, or other hosts.
    public static bool IsRecoveryNavigation(string? value) => IsVoicePage(value);

    // Signed in means Google has let the Voice web app itself load, not merely that account cookies exist.
    public static bool IsVoicePage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host == "voice.google.com" &&
        !uri.AbsolutePath.StartsWith("/signup", StringComparison.OrdinalIgnoreCase);

    public static string? ReadCookie(string name, string domain, string value)
    {
        if (!CookieNames.Contains(name, StringComparer.Ordinal)) return null;
        var expected = CookieDomain(name);
        if (domain.TrimStart('.') != expected) return null;
        return value is { Length: > 0 and < 32768 } ? value : null;
    }

    internal static string? CookieDomain(string name) => CookieNames.Contains(name, StringComparer.Ordinal)
        ? name switch { "OSID" => "voice.google.com", "COMPASS" => "clients6.google.com", _ => "google.com" }
        : null;

    public static bool IsComplete(IReadOnlyDictionary<string, string> cookies) => RequiredCookies.All(cookies.ContainsKey);
}
