using MessageLink.App;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;

namespace MessageLink.Server.Linux;

/// <summary>
/// What one service's renewal loads and keeps, as the Windows renewal's targets (<c>GoogleBrowserRecovery.Voice</c> and <c>.Messages</c> in
/// MessageLink.Windows) say it, from the same rules: the one page it opens, the main-frame navigations it allows on the way, the page that
/// means signed in, the addresses whose cookies it reads back and which cookies those may be, and where a seeded cookie belongs.
/// </summary>
internal sealed record ChromiumRenewalTarget(string Service, string Url, Func<string?, bool> AllowNavigation, Func<string?, bool> IsDone,
    string[] CookieUrls, Func<string, string, string, string?> ReadCookie, Func<IReadOnlyDictionary<string, string>, bool> IsComplete,
    Func<string, string?> CookieDomain, string FailureMessage)
{
    /// <summary>
    /// The hosts Chrome's cookie store may hold this service's cookies under (<c>host_key</c>, each with and without the leading dot). Voice's two cookies that are not Google's own sit on the
    /// Voice page's host (<c>OSID</c>) and its API host (<c>COMPASS</c>).
    /// </summary>
    public string[] StoreHosts { get; init; } = ["google.com"];

    public static readonly ChromiumRenewalTarget Voice = new(GoogleSignInProfile.VoiceService, GoogleVoiceSignInRules.VoiceUrl,
        GoogleVoiceSignInRules.IsRecoveryNavigation, GoogleVoiceSignInRules.IsVoicePage,
        [GoogleVoiceSignInRules.VoiceUrl, GoogleVoiceSignInRules.ApiUrl], GoogleVoiceSignInRules.ReadCookie, GoogleVoiceSignInRules.IsComplete,
        GoogleVoiceSignInRules.CookieDomain, "Google Voice couldn't refresh its sign-in. Try again later.")
    { StoreHosts = ["google.com", "voice.google.com", "clients6.google.com"] };

    // Messages for web's configuration address, never the web app itself: loading that could start a pairing of its own or take over as the active
    // session. It is the address the sign-in window ends on.
    public static readonly ChromiumRenewalTarget Messages = new(GoogleSignInProfile.MessagesService, GoogleSignInRules.RecoveryUrl,
        GoogleSignInRules.IsRecoveryNavigation, GoogleSignInRules.IsCompletionUrl, [GoogleSignInRules.RecoveryUrl],
        GoogleSignInRules.ReadCookie, cookies => GoogleSignInRules.RequiredCookies.All(cookies.ContainsKey), MessagesCookieDomain,
        "Google Messages sign-in refresh could not start. Try again later.")
    { StoreHosts = ["google.com", "messages.google.com"] };

    /// <summary>
    /// What any load but the page's own main-frame documents (frames, scripts, styles, images, requests) may come from: Google's hosts, over HTTPS on
    /// the default port. Anything else is refused and counted (<c>blocked=</c> in the run's line), since the browser runs without its own sandbox in
    /// the container. The list is what Google's sign-in pages are known to use; a host missing from it shows up as a count, never as a failure.
    /// </summary>
    public Func<string?, bool> AllowLoad { get; init; } = GoogleHost;

    public static bool GoogleHost(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && (uri.Host is "google.com" or "gstatic.com" or "accounts.youtube.com"
            || new[] { ".google.com", ".gstatic.com", ".googleapis.com", ".googleusercontent.com" }.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.Ordinal)));

    /// <summary>
    /// What the sign-in's page itself may be (a main-frame document, shown with no address bar): Google's own pages, <c>google.com</c> and everything under <c>.google.com</c> (the
    /// account, recovery, passkey and two-step pages are all on <c>accounts.google.com</c>), and the one address on <c>accounts.youtube.com</c> the sign-in passes through. Narrower
    /// than <see cref="GoogleHost"/>, which also admits hosts that serve anyone's content (<c>*.googleusercontent.com</c>, <c>*.googleapis.com</c>, <c>*.gstatic.com</c>): fine for a
    /// script or an image, not for what a person is asked to type a password into.
    /// </summary>
    public static bool GoogleMainFrame(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host == "google.com" || uri.Host.EndsWith(".google.com", StringComparison.Ordinal)
            || uri.Host == "accounts.youtube.com" && uri.AbsolutePath.StartsWith("/accounts/SetSID", StringComparison.Ordinal));

    /// <summary>Messages' when the service says so, else Voice's, as the Windows browser chooses.</summary>
    public static ChromiumRenewalTarget For(string service) => service == GoogleSignInProfile.MessagesService ? Messages : Voice;

    // The domain GoogleSignInRules.ReadCookie accepts each Messages cookie from.
    private static string? MessagesCookieDomain(string name) => GoogleSignInRules.CookieNames.Contains(name, StringComparer.Ordinal)
        ? name == "OSID" ? "messages.google.com" : "google.com"
        : null;
}
