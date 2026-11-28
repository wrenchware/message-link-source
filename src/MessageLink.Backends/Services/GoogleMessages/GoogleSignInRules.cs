using System.Text.Json;

namespace MessageLink.App.Services.GoogleMessages;

internal static class GoogleSignInRules
{
    public const string LoginUrl = "https://accounts.google.com/AccountChooser?continue=https://messages.google.com/web/config";
    // Where the sign-in ends, and all that silent recovery loads (GoogleBrowserRecovery.Messages).
    public const string RecoveryUrl = "https://messages.google.com/web/config";
    public static readonly string[] RequiredCookies = ["SID","HSID","SSID","OSID","APISID","SAPISID"];
    // Beyond upstream's six, the __Secure- cookies a signed-in browser also sends. They were taken in 0.1.290 so the helper
    // could renew __Secure-1PSIDTS as Chrome does; for this client that never renewed it (retired in 0.1.307), and a copied
    // sign-in is refused two hours after it was made whichever cookies it carries. Kept: Google's answers use them.
    public static readonly string[] CookieNames = [..RequiredCookies,"__Secure-1PSIDTS","__Secure-1PSID","__Secure-3PSID",
        "__Secure-3PSIDTS","__Secure-1PAPISID","__Secure-3PAPISID"];
    // The pairing dialog's first two choices (MainWindow.GoogleSignIn). A pairing the phone removed cannot be renewed, so
    // renewing is not offered. Every other saved pairing leads with renewing it: pairing as a new device is the one choice
    // that abandons the pairing the phone has, and a new one may not even be possible (a phone that requires client
    // attestation refuses it, mautrix/gmessages#91). It led whenever nothing was known about the pairing, which is the
    // usual state, since every renewal clears what was known (2026-09-24 audit). expired is kept for the wording.
    public static (string Primary, string Secondary) DialogChoices(bool firstPair, bool lost, bool expired) =>
        firstPair ? ("Continue", "")
        : lost ? ("Pair as a new device", "")
        : ("Renew sign-in", "Pair as a new device");
    public static bool IsLoginNavigation(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    public static string? ReadCookie(string name, string domain, string value)
    {
        if (!CookieNames.Contains(name, StringComparer.Ordinal)) return null;
        if (domain.TrimStart('.') != (name == "OSID" ? "messages.google.com" : "google.com")) return null;
        return value is { Length: > 0 and < 32768 } ? value : null;
    }
    // Silent recovery may pass through Google's own account host on its way back to the configuration address, and
    // nothing else; an account chooser or password page there never completes, so recovery gives up and asks.
    public static bool IsRecoveryNavigation(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "messages.google.com" or "accounts.google.com";
    public static bool IsCompletionUrl(string? value) => Uri.TryCreate(value,UriKind.Absolute,out var uri) &&
        uri.Scheme == "https" && uri.Host == "messages.google.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.TrimEnd('/') == "/web/config";
    public static string? ReadCookie(JsonElement cookie,string name)
    {
        if (cookie.GetProperty("name").GetString() != name) return null;
        var domain = cookie.GetProperty("domain").GetString()?.TrimStart('.');
        if (domain != (name == "OSID" ? "messages.google.com" : "google.com")) return null;
        var value = cookie.GetProperty("value");
        if (value.GetProperty("type").GetString() != "string") return null;
        return value.GetProperty("value").GetString() is { Length: > 0 and < 32768 } text ? text : null;
    }
}
