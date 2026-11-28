using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

/// <summary>
/// What a hand-over sign-in's cookie jar says about how Google bound the session. The experiment of switching the sign-in browser's own binding off for a server failed
/// (run 4: Google then issued no timestamp cookie, and Messages refused the sign-in), so nothing here changes how any sign-in browser is created.
/// </summary>
internal static class GoogleSignInBinding
{
    /// <summary>Whether the jar holds <c>__Secure-1PSIDRTS</c>, which may mark a bound session: a fixed word, never a value.</summary>
    public static string RtsWord(IEnumerable<GoogleProfileCookie> jar) =>
        jar.Any(cookie => cookie.Name == "__Secure-1PSIDRTS") ? "rts=yes" : "rts=no";
}
