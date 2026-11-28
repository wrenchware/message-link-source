using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.WindowsPlatform;

namespace MessageLink.App;

// IGoogleBrowser on WebView2 (GoogleBrowserRecovery). WebView2 runs only on the host's interface thread (the window's, or the server's STA
// thread: IStaQueue), so a call from anywhere else is moved there; one already on it runs as it is, as Google Messages' renewals in
// MainWindow always did.
internal sealed class WebViewGoogleBrowser(IStaQueue queue) : IGoogleBrowser
{
    public Task<GoogleRefreshResult> RefreshAsync(string service, string profileId, CancellationToken cancellationToken)
    {
        var target = service == GoogleSignInProfile.MessagesService ? GoogleBrowserRecovery.Messages : GoogleBrowserRecovery.Voice;
        return OnInterfaceAsync(() => GoogleBrowserRecovery.RefreshWithReasonAsync(target, profileId, cancellationToken));
    }

    public Task<GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken) =>
        OnInterfaceAsync(() => GoogleBrowserRecovery.CreateProfileAsync(cookies, cancellationToken));

    public bool HasProfile(string profileId, string service = GoogleSignInProfile.VoiceService) =>
        GoogleSignInProfile.TryOpen(profileId, out _, service);

    public Task DeleteProfileAsync(string profileId, string service = GoogleSignInProfile.VoiceService) =>
        GoogleSignInProfile.DeleteAsync(profileId, service);

    // A queue that will not take the work (the app is closing) cancels it, as UiDispatcher does.
    private Task<T> OnInterfaceAsync<T>(Func<Task<T>> action) => queue.RunAsync(action);
}
