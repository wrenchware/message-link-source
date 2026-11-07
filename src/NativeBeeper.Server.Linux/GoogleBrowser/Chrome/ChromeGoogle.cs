using NativeBeeper.App;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Everything the host needs to run Google Messages through its own Chrome (SWITCHBOARD.md 5.4, Chrome on the server): the one persistent Chrome, the browser the keeper renews through, the
/// cookie store, the picture encoder, which Chrome this is and whether the sign-in window is offered here (<see cref="LinuxGoogle.NetworkAllowsSignIn"/>).
/// </summary>
internal sealed record ChromeGoogle(PersistentChrome Chrome, ChromeKeptBrowser Browser, IChromeCookieSource Cookies, IFrameEncoder Encoder, ChromeBuild Build, bool SignInOffered)
{
    /// <summary>The remote sign-in host, or null when this host does not offer it.</summary>
    public ChromeRemoteSignIn? SignIn(IGoogleMessagesAccount account, Action<string> log, SignInGuard guard, GoogleAccountBrowserProfile? profile = null) =>
        SignInOffered ? new ChromeRemoteSignIn(Chrome, Cookies, Encoder, account, log, guard, Build) { Profile = profile ?? GoogleMessagesBrowserProfile.Default } : null;

    /// <summary>
    /// At start: Chrome up on the kept profile, when there is one (a server that has never been signed in has no Chrome running; the first sign-in starts it). The profile is the one the keeper's
    /// pointer names, so a sign-in that was never accepted leaves nothing behind to start.
    /// </summary>
    public async Task StartKeptAsync(CancellationToken cancellationToken)
    {
        if (GoogleMessagesBrowserProfile.Id is not { } id || !GoogleSignInProfile.TryOpen(id, out var profile, GoogleSignInProfile.MessagesService) || profile is null) return;
        // Copies a cut-short read left behind are removed first.
        ChromeCookieStore.Sweep(profile.Path);
        await Chrome.EnsureAsync(profile.Path, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Where the server's one Chrome belongs when nothing else is using it: on the Messages profile, if one is kept.</summary>
internal static class MessagesChrome
{
    /// <summary>
    /// Chrome closed gently and started again on the Messages profile, when one is kept: where a Voice sign-in or renewal leaves it (Voice's profiles are loaded for those alone). With no Messages profile it
    /// is left closed. Never throws.
    /// </summary>
    public static async Task ResumeAsync(PersistentChrome chrome, Action<string> log)
    {
        try
        {
            await chrome.StopAsync().ConfigureAwait(false);
            if (GoogleMessagesBrowserProfile.Id is { } id && GoogleSignInProfile.TryOpen(id, out var kept, GoogleSignInProfile.MessagesService) && kept is not null)
                await chrome.EnsureAsync(kept.Path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) { log($"google chrome start=failed type={failure.GetType().Name}"); }
    }
}

/// <summary><see cref="IChromeRenewalTarget"/> over the Google Messages backend (the same calls the keeper's early renewal makes).</summary>
internal sealed class BackendRenewalTarget(GoogleMessagesBackend backend) : IChromeRenewalTarget
{
    public DateTimeOffset? MadeAt => backend.Files.Clock.MadeAt;
    public bool Ready => backend.CanReceive && !backend.SignInExpired && !backend.PairingLost;
    public string? ProfileId => backend.Files.BrowserProfile.Id;
    public Task<string[]> ChangedFromSavedAsync(IReadOnlyDictionary<string, string> cookies) => backend.ChangedFromSavedAsync(cookies);
    public Task<string> AdoptAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken) => backend.AdoptCookiesAsync(cookies, cancellationToken);
}
