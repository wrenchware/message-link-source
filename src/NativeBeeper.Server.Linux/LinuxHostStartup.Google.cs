using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Protocol;
using NativeBeeper.Server;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// The Linux host with Google Messages (<c>MESSAGES_GOOGLE=1</c>, <see cref="LinuxGoogle"/>, SWITCHBOARD.md 4.3): the Windows server's Google wiring
/// (<c>src/NativeBeeper.Server/HostStartup.cs</c>) on Linux. Its feed beside Messenger's, its restore and restart, and the sign-in keeper started by
/// the restore and only by it. A host not started to restore never gets here: <see cref="LinuxGoogle.Prepare"/> refuses Google Messages then
/// (<c>no_restore</c>), since a client's sign-in would seed a browser profile that no keeper renews. Instagram runs as it does without it; Google Voice stays refused.
/// </summary>
internal sealed partial class LinuxHostStartup
{
    private SerialQueue? signInQueue;

    // Google's feed is gated on a saved pairing, as the Windows server's is: let go, it connects that pairing by itself. Google Voice's joins it when Voice runs
    // here (its own Chrome): left out, from moving day (2026-10-12) to 2026-10-29 no Voice arrival reached a client, a push or the whole history as it happened,
    // only when something read the conversation again.
    private static GatedRealtimeFeed GoogleFeed(GoogleMessagesBackend google, MessengerBackend messenger, InstagramBackend instagram, GoogleVoiceBackend voice, bool voiceRuns) =>
        new(new CompositeRealtimeFeed(() => MessagingRouter.GoogleMessagesExpected, voiceRuns
            ? [(ProviderScopes.Google, google), (ProviderScopes.Messenger, messenger), (ProviderScopes.VoicePrefix, voice), (ProviderScopes.Instagram, instagram)]
            : [(ProviderScopes.Google, google), (ProviderScopes.Messenger, messenger), (ProviderScopes.Instagram, instagram)]));

    // Google Voice's slot too when it runs here (beside the server's own Chrome): a client's restore of it is then the host's own, as on the Windows server.
    private static RestoreOnce GoogleRestores(bool voiceRuns) => new(voiceRuns ? [RestoreOnce.Messenger, RestoreOnce.Instagram, RestoreOnce.Google, RestoreOnce.Voice]
        : [RestoreOnce.Messenger, RestoreOnce.Instagram, RestoreOnce.Google]);

    // The real restarter with Google's backend in it; the account keeps a browser profile of this host's own at every sign-in a client hands over. With Google Voice running (the server's own
    // Chrome), its accounts are the real ones, behind a port that refuses the hand-over of cookies from a client.
    private static HostUnavailablePorts GooglePorts(LinuxGoogle withGoogle, IGoogleMessagesAccount account, GoogleMessagesBackend google, GoogleSignInKeeper keeper,
        MessengerBackend messenger, InstagramBackend instagram, GoogleVoiceAccounts voice, IGoogleVoiceAccounts voicePort, Action<string> log) =>
        UnavailableProviders.PortsWithGoogle(new HostHelperRestarter(google, keeper, messenger, instagram, voice, log),
            new ProfileKeepingGoogleAccount(account, withGoogle.Browser, withGoogle.Guard, refuseHandOver: withGoogle.RefusesHandOver), withGoogle.VoiceRuns ? voicePort : null);

    // A sign-in in the server's own browser (SWITCHBOARD.md 5.4), only with a headed browser: it pairs through the backend's own account, since it keeps the profile it signed in with
    // itself and a profile seeded from cookies (ProfileKeepingGoogleAccount) would replace it. Null when the host has no headed browser; the client then signs in as it did.
    // With the server's own Chrome the same window signs a Google Voice account in too (protocol 15): one object serves both, so only one sign-in runs at a time, and the Voice one adds the account
    // to the real accounts (voicePort), never through the port that refuses a client's cookies.
    // Messenger and Instagram too (protocol 27, SWITCHBOARD.md 10.5), in the same Chrome and window on a throwaway profile each time: the same object again, so a Meta sign-in and a Google one never
    // run at once. Only where the server's own Chrome offers its sign-in at all; elsewhere a PC signs them in in its own window, as before.
    private static (IRemoteSignInHost? Messages, IRemoteSignInHost? Voice, IRemoteCookieSignInHost? Meta) RemoteSignIn(LinuxGoogle? withGoogle, IGoogleMessagesAccount? account,
        IGoogleVoiceAccounts voicePort, Action<string> log)
    {
        if (account is null || withGoogle is null) return (null, null, null);
        // Signed in by hand in the server's own Chrome, which stays running (SWITCHBOARD.md 5.4, MESSAGES_GOOGLE_CHROME=1).
        if (withGoogle.Chrome is { } chrome)
        {
            var signIn = chrome.SignIn(account, log, withGoogle.Guard);
            return (signIn, signIn?.ForVoice(voicePort), signIn?.ForMeta(withGoogle.SignInProfilesRoot));
        }
        return (withGoogle.SignIn is { } launcher ? new RemoteGoogleSignIn(launcher, withGoogle.Browser, account, log, withGoogle.Guard) : null, null, null);
    }

    private CancellationTokenSource? chromeStop;

    // Chrome up on its kept profile (when it has one), and the 100-minute reload that renews its sign-in in place. Neither waits for anything: a sign-in that is not there yet starts Chrome itself.
    private void StartChrome(CancellationToken stop)
    {
        if (google?.Chrome is not { } chrome) return;
        chromeStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var token = chromeStop.Token;
        _ = Task.Run(async () =>
        {
            try { await chrome.StartKeptAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception failure) { log($"google chrome start=failed type={failure.GetType().Name}"); }
        });
        var loop = new ChromeRenewalLoop(chrome.Browser, new BackendRenewalTarget(Services.GetRequiredService<GoogleMessagesBackend>()), log);
        _ = Task.Run(() => loop.RunAsync(token));
    }

    /// <summary>Closes the server's Chrome gently (it writes its cookies and state) and stops the reload loop; bounded. Safe when Chrome was never started.</summary>
    public async Task StopGoogleBrowserAsync()
    {
        try { chromeStop?.Cancel(); } catch (ObjectDisposedException) { }
        if (google?.Chrome is not { } chrome) return;
        try { await chrome.Chrome.StopAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false); }
        catch (TimeoutException) { log("google chrome stop=still_running"); }
        catch (Exception failure) { log($"google chrome stop=failed type={failure.GetType().Name}"); }
    }

    /// <summary>
    /// The Windows server's restore order: the keeper first, so a refusal the launch's connect hears is renewed; Messenger and Instagram alongside and not awaited
    /// before Google, so a stale Google sign-in never holds it up; each once (<see cref="RestoreOnce"/>); then the feed is let go.
    /// </summary>
    private async Task RestoreWithGoogleAsync(CancellationToken stop)
    {
        try
        {
            var signIn = StartSignInKeeper(stop);
            StartChrome(stop);
            if (stop.IsCancellationRequested)
            {
                foreach (var provider in Restoring) log($"server restore provider={provider} outcome=stopped");
                signIn.EndStartupLoad();
                return;
            }
            var others = RestoreOthersAsync(stop);
            // Google Voice's saved accounts alongside, when it runs here, as the Windows server restores them: each through its own helper, a stale sign-in first renewed from its own Chrome profile.
            var voices = google?.VoiceRuns == true ? RestoreVoiceAsync(stop) : Task.CompletedTask;
            try
            {
                await Restored(RestoreOnce.Google, stop, () => restores.RunAsync(RestoreOnce.Google, async () =>
                {
                    if (!MessagingRouter.GoogleMessagesExpected) return "not_saved";
                    // Held for the connect, so a renewal that ends meanwhile does not connect again beside it.
                    using var connecting = signIn.BeginConnect();
                    var backend = Services.GetRequiredService<IBeeperBackend>();
                    await backend.DiscoverAsync(stop);
                    return $"accounts={(await backend.GetAccountsAsync(stop)).Count}";
                }));
            }
            // However it ended, or never began: a renewal that waited for the launch's connect goes on.
            finally { signIn.EndStartupLoad(); }
            if (!stop.IsCancellationRequested) feed.Open();
            await others;
            await voices;
        }
        finally { restores.End(); }
    }

    private Task RestoreVoiceAsync(CancellationToken stop) =>
        Restored(VoiceProvider, stop, async () => $"failed={(await restores.RunAsync(RestoreOnce.Voice, () => Services.GetRequiredService<GoogleVoiceAccounts>().RestoreAsync(stop))).Count}");

    /// <summary>The word Google Voice's restore is logged with, as on the Windows server.</summary>
    internal const string VoiceProvider = "gvoice";

    // The keeper (age timer, early renewal, the checks after two hours, the refusal-driven refresh) with this host as its host, on a thread of its own.
    private LinuxSignInHost StartSignInKeeper(CancellationToken stop)
    {
        signInQueue = new SerialQueue("Google sign-in", log);
        // A renewal's end tells every client to re-read Google Messages, in the host's own event order.
        var signIn = new LinuxSignInHost(signInQueue, Services.GetRequiredService<HostClientState>(), ConnectGoogleAsync, stop, log,
            () => Host.EventLog.Append(new RealtimeEvent(RealtimeKind.ResyncRequired, Scope: ProviderScopes.Google)));
        Services.GetRequiredService<GoogleSignInKeeper>().Start(signIn);
        log("server sign-in keeper started");
        return signIn;
    }

    private async Task ConnectGoogleAsync(CancellationToken stop)
    {
        var backend = Services.GetRequiredService<IBeeperBackend>();
        await backend.DiscoverAsync(stop);
        await backend.GetAccountsAsync(stop);
    }
}
