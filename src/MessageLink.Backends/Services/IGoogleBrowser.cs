using MessageLink.App.Services.GoogleMessages;

namespace MessageLink.App.Services;

// The browser that keeps each Google sign-in's profile, so a sign-in Google is about to refuse can be renewed without the
// user. WebView2 in this app (WebViewGoogleBrowser); a server has no WebView2 and renews through its own browser instead,
// which is why the backends reach it only through this.
internal interface IGoogleBrowser
{
    // Copied WebView2 sign-ins expire at two hours; the server's own Chrome session survived days.
    // This changes only the age presumption, never recovery after Google actually refuses a request.
    bool RefreshBeforeConnect => true;
    // Loads the kept profile for one service (GoogleSignInProfile.VoiceService or MessagesService) and reads back its
    // cookies, or says why it could not. The caller owns and clears a non-null Cookies.
    Task<GoogleRefreshResult> RefreshAsync(string service, string profileId, CancellationToken cancellationToken);
    // A new Voice profile seeded from cookies alone (sessions saved before profiles were kept), or null.
    Task<GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken);
    bool HasProfile(string profileId, string service = GoogleSignInProfile.VoiceService);
    Task DeleteProfileAsync(string profileId, string service = GoogleSignInProfile.VoiceService);
}
