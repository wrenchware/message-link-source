using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// What the keeper needs from whatever hosts it: the app's window, or a server. Every decision below is made on the
// host's own thread (Post), as the window made them on its interface thread before this moved (SWITCHBOARD.md 1.7b).
internal interface IGoogleSignInHost
{
    bool Closed { get; }
    // A dialog is open, which may be the sign-in dialog itself: nothing renews alongside it.
    bool UserBusy { get; }
    // The launch's own connect and load, which a sign-in refreshed at launch waits for.
    Task StartupLoad { get; }
    void Post(Action action);
    Task PauseSyncAsync();
    void ResumeSync();
    // Reconnects Google Messages only, moving nothing on screen: a Messenger or Instagram restore that launch gave up on,
    // after a checkpoint or an account check, must not log in again behind it.
    Task ReconnectAsync();
    // The launch's own load, run again from the connect it skipped (ReleaseGoogleSignInNotice).
    void ReloadAfterRelease();
}

// Google refused the Google Messages sign-in while the phone keeps the pairing. Before asking the user to renew it, the
// sign-in is refreshed once from the browser profile of the last accepted sign-in, invisibly, as Google Voice recovers
// its sessions (GoogleVoiceAccounts.RecoverAsync). The user renewed by hand twice on 2026-09-23, and Voice recovered this
// way in two seconds the same evening. If the profile is gone, or Google wants the password again, nothing changes: the
// app asks as before.
//
// Google refuses every sign-in this app makes about two hours after it was made (GoogleSignInClock), so the same refresh
// also runs before a connect that would be refused (the backend's presumed expiry); ten minutes before the two hours the
// profile is loaded and its new cookies handed to the running helper, so Google has nothing to refuse
// (EarlyGoogleRefreshAsync); and one read just after the two hours hears a refusal at once if it comes all the same.
//
// Google says so once per expiry, so a refusal that lands while a dialog is open, while a refresh runs, or inside the
// spacing is put off and looked at again (GoogleRecoveryGate), not dropped without a line in the log.
//
// Here rather than in the window so that every host renews the same way (SWITCHBOARD.md 1.7b, 1.7c); the window keeps
// only what it draws (MainWindow.GoogleRecovery).
internal sealed class GoogleSignInKeeper(GoogleMessagesBackend backend, IGoogleBrowser browser,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] IAccessTokenStore tokens,
    IClientBusy? clientBusy = null) : IDisposable
{
    private readonly GoogleRecoveryGate googleRecovery = new();
    private bool googleRecovering, googleRecoveryDeferred, googleRecoveryStopped;
    private string? googleRecoveryDeferredReason;
    private IGoogleSignInHost host = null!;
    private Timer? googleEarlyTimer;

    // Whether a dialog is open. A host that is told by its clients (client.state, SWITCHBOARD.md 3.0b) answers from what they said; the one that
    // hosts the window itself asks the window.
    private bool UserBusy => clientBusy?.Busy ?? host.UserBusy;

    // A renewal is under way: Connection health's restart waits for it rather than racing it.
    public bool Recovering => googleRecovering;

    public void Start(IGoogleSignInHost host)
    {
        this.host = host;
        // Whether an old sign-in can be refreshed before connecting: only from a kept profile.
        backend.SilentRefreshAvailable = () => browser.RefreshBeforeConnect && backend.Files.BrowserProfile.Id is not null;
        backend.SignInRefused += Refused;
        // A refusal from before this host listened would otherwise wait for Google to say it again, which it does not.
        if (backend.SignInExpired) Refused(backend, EventArgs.Empty);
        googleEarlyTimer = new(_ => host.Post(CheckGoogleSignInAge), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        backend.SignInRefused -= Refused;
        googleEarlyTimer?.Dispose();
    }

    // A new sign-in by hand, with a new profile: the silent refresh may use it at the next refusal, whatever the old one
    // had failed.
    public void SignedIn()
    {
        void Reset()
        {
            googleRecovery.NoteSignedIn(DateTimeOffset.UtcNow); googleRecoveryStopped = false; googleSchedule.SignedIn();
        }
        // The state is the host's thread's, as every other decision here: a host that took a client's sign-in on a request thread (the server)
        // hands it over. Before the keeper is started nothing else touches it, and the reset is made here.
        if ((object?)host is null) Reset();
        else host.Post(Reset);
    }

    private void Refused(object? sender, EventArgs args) => host.Post(() =>
    {
        googleRecovery.NoteRefused(DateTimeOffset.UtcNow);
        // At the two hours of a sign-in the early renewal replaced: renewing early did not help, however this was heard.
        if (googleSchedule.Refused(DateTimeOffset.UtcNow)) GoogleMessagesLog.Write("browser early=off reason=refused_after_adopt");
        googleRecoveryStopped = false;
        _ = RecoverGoogleSignInAsync(backend);
    });

    // Runs on the host's thread, from a refusal, a deferral coming due, or the end of an attempt.
    private async Task RecoverGoogleSignInAsync(GoogleMessagesBackend backend)
    {
        // No browser is ever started for a QR pairing: it has no sign-in to renew.
        if (backend.QrSession) return;
        var (verdict, reason, wait) = googleRecovery.Decide(DateTimeOffset.UtcNow, backend.SignInExpired, googleRecovering, UserBusy, host.Closed);
        if (verdict == GoogleRecoveryVerdict.Stop && !googleRecoveryStopped)
        {
            // A profile whose cookies Google keeps refusing must not turn into a loop of invisible browsers.
            googleRecoveryStopped = true;
            GoogleMessagesLog.Write($"browser recovery=stopped reason={reason}");
        }
        // No refresh is coming soon, so the notice held for one is given now.
        if (GoogleRecoveryGate.EndsNoticeHold(verdict, reason)) ReleaseGoogleSignInNotice(backend, reason);
        if (verdict == GoogleRecoveryVerdict.Defer) DeferGoogleRecovery(backend, reason, wait);
        if (verdict != GoogleRecoveryVerdict.Run) return;
        if (backend.Files.BrowserProfile.Id is not { } profileId)
        {
            GoogleMessagesLog.Write("browser recovery=unavailable reason=no_profile");
            ReleaseGoogleSignInNotice(backend, "no_profile");
            return;
        }
        googleRecovering = true;
        googleRecoveryDeferredReason = null;
        googleRecovery.NoteAttempt(DateTimeOffset.UtcNow);
        await AttemptGoogleRecoveryAsync(backend, profileId);
        // Still refused after this attempt: the gate allows one more after the spacing, or stops.
        if (!host.Closed) _ = RecoverGoogleSignInAsync(backend);
    }

    private async Task AttemptGoogleRecoveryAsync(GoogleMessagesBackend backend, string profileId)
    {
        Dictionary<string, string>? cookies = null;
        var stopped = false;
        // Presumed from the sign-in's age, before Google refused anything (GoogleSignInClock). Such an attempt counts only
        // if it renewed something: otherwise a refusal that follows within the spacing is refreshed at once, rather than
        // told to the user as an expiry the refresh cannot help with. Read again at each decision: a refusal meanwhile
        // makes it an ordinary expiry.
        bool Presumed() => backend.SignInPresumed;
        try
        {
            GoogleMessagesLog.Write($"browser recovery=starting cause={(Presumed() ? "age" : "refused")}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var refreshed = await browser.RefreshAsync(GoogleSignInProfile.MessagesService, profileId, timeout.Token);
            cookies = refreshed.Cookies;
            if (cookies is null)
            {
                // Only a page that wanted the account or the password, or a sign-in without its cookies, says the profile
                // cannot renew it. Waiting for the other service's refresh, or a page that did not load, says nothing about
                // it, and counting those stopped the refresh for the rest of the run.
                GoogleMessagesLog.Write($"browser recovery=failed reason={refreshed.Reason}");
                if (refreshed.SignInNeeded) googleRecovery.NoteFailed();
                else if (Presumed()) googleRecovery.Withdraw();
                ReleaseGoogleSignInNotice(backend, "failed");
                return;
            }
            // Minutes the browser was told to keep the short-lived cookie: a few for a device-bound sign-in, months otherwise.
            GoogleMessagesLog.Write($"browser cookies sidts_lifetime_min={refreshed.SidtsLifetime}");
            if (Presumed() && (await backend.ChangedFromSavedAsync(cookies)).Length == 0)
            {
                // Google handed back the sign-in it already had: nothing to renew, so the saved one is simply tried.
                googleRecovery.Withdraw();
                GoogleMessagesLog.Write("browser recovery=unused reason=same");
                ReleaseGoogleSignInNotice(backend, "same");
                return;
            }
            // At launch the refusal comes from the launch's own connect, which is still loading the other services'
            // conversations. Stopping synchronization under it, and loading again beside it, would do both twice.
            try { await host.StartupLoad; } catch (Exception) { }
            // Never alongside a dialog, which may be the sign-in dialog itself: the fresh cookies wait up to two minutes for
            // it to close rather than being thrown away.
            if (UserBusy && !host.Closed)
            {
                GoogleMessagesLog.Write("browser recovery=waiting reason=dialog");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (UserBusy && !host.Closed && waited.Elapsed < TimeSpan.FromMinutes(2)) await Task.Delay(TimeSpan.FromSeconds(1));
            }
            if (host.Closed) return;
            if (UserBusy || !backend.SignInExpired)
            {
                // Not tried, so not counted: a dialog still open is deferred like any other.
                googleRecovery.Withdraw();
                GoogleMessagesLog.Write($"browser recovery=unused reason={(UserBusy ? "dialog" : "not_expired")}");
                return;
            }
            await host.PauseSyncAsync();
            stopped = true;
            // The same renewal the dialog's Renew sign-in runs: the saved pairing is kept, only the sign-in is new. Never a
            // new pairing: with nothing saved to renew it fails instead of showing an emoji nobody is looking at.
            using var signIn = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await backend.SignInAsync(cookies, new Progress<string>(_ => { }), signIn.Token, asNewDevice: false, renewOnly: true);
            if (tokens is GoogleSessionProfile profile) profile.Invalidate();
            googleRecovery.NoteConnected(DateTimeOffset.UtcNow);
            GoogleMessagesLog.Write("browser recovery=connected");
            // stopped stays set: the reconnect restarts synchronization, and if it returns early (a connect already
            // running) the finally still does, rather than leaving every realtime feed off. A second start is a no-op.
            // Invisible: whatever the user has open stays open, Settings included. Only Google Messages reconnects; the
            // other services keep what their own restore decided.
            if (!host.Closed) await host.ReconnectAsync();
        }
        catch (Exception failure)
        {
            GoogleMessagesLog.Write($"browser recovery=failed reason={GoogleMessagesLog.Reason(failure)}");
            // A presumed renewal that failed (the phone slow to answer, say) proves nothing about the sign-in either; a
            // renewal that found the pairing gone has ended the expiry itself.
            if (Presumed()) googleRecovery.Withdraw();
            else googleRecovery.NoteFailed();
            // Before synchronization restarts (finally), so the restarted loop says it on its first pass.
            ReleaseGoogleSignInNotice(backend, "failed");
        }
        finally
        {
            cookies?.Clear();
            googleRecovering = false;
            if (stopped && !host.Closed) host.ResumeSync();
        }
    }

    // Every 15 seconds, two things about the sign-in's age.
    // - Ten minutes before the two hours, the kept profile is loaded while the sign-in still works. Google hands it new
    //   short-lived cookies then (2026-09-25 13:28), and they go to the running helper in place (AdoptCookiesAsync), so
    //   the sign-in is renewed before Google refuses it and nothing reconnects; the clock starts again, and so, 110 minutes
    //   on, does the next look (EarlyGoogleRefreshAsync).
    // - Just after the two hours, one read, so a refusal is heard at once rather than at the next ping, up to twenty
    //   minutes later (CheckSignInAsync); if Google still accepts it, once more ten minutes on. A sign-in renewed early
    //   keeps the checks of the one it replaced, in case Google refuses at the old mark all the same.
    private readonly GoogleSignInSchedule googleSchedule = new();
    private void CheckGoogleSignInAge()
    {
        // A QR pairing has no Google-account sign-in to age, renew or check: nothing here is for it (GoogleMessagesBackend.QrPairing.cs).
        if (backend.QrSession) return;
        if (googleRecovering || UserBusy || host.Closed || !backend.CanReceive || backend.SignInExpired || backend.PairingLost) return;
        if (backend.Files.Clock.MadeAt is not { } made) return;
        var now = DateTimeOffset.UtcNow;
        var (check, checkFor, early) = googleSchedule.Tick(now, made);
        if (check) _ = CheckGoogleExpiryAsync(backend, checkFor, (int)(now - checkFor).TotalMinutes);
        else if (early) _ = EarlyGoogleRefreshAsync(backend, made);
    }

    private async Task CheckGoogleExpiryAsync(GoogleMessagesBackend backend, DateTimeOffset checkFor, int ageMinutes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var outcome = await backend.CheckSignInAsync(timeout.Token);
        GoogleMessagesLog.Write($"sign_in check age_min={ageMinutes} outcome={outcome}");
        if (googleSchedule.CheckAnswered(checkFor, outcome)) GoogleMessagesLog.Write("browser early=off reason=refused_at_old_mark");
    }

    // The kept profile, loaded while the sign-in still works. When Google hands it new sign-in cookies they are adopted in
    // place, and the helper is tried with them 15 s later. How each try ends is a fixed word the schedule reads: only an
    // answer that would come again every time stops early renewal for the run.
    private async Task EarlyGoogleRefreshAsync(GoogleMessagesBackend backend, DateTimeOffset made)
    {
        void Ended(string outcome)
        {
            if (googleSchedule.EarlyEnded(outcome)) GoogleMessagesLog.Write($"browser early=off reason={outcome}");
        }
        if (backend.Files.BrowserProfile.Id is not { } profileId) { GoogleMessagesLog.Write("browser early=unavailable reason=no_profile"); Ended("no_profile"); return; }
        Dictionary<string, string>? cookies = null;
        googleRecovering = true;
        var holding = true;
        try
        {
            GoogleMessagesLog.Write($"browser early=starting age_min={backend.Files.Clock.AgeMinutes(DateTimeOffset.UtcNow)?.ToString() ?? "unknown"}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var refreshed = await browser.RefreshAsync(GoogleSignInProfile.MessagesService, profileId, timeout.Token);
            cookies = refreshed.Cookies;
            if (cookies is null) { GoogleMessagesLog.Write($"browser early=failed reason={refreshed.Reason}"); Ended(refreshed.Reason); return; }
            var changed = await backend.ChangedFromSavedAsync(cookies);
            GoogleMessagesLog.Write((changed.Length == 0 ? "browser early=same" : $"browser early=renewed changed=[{string.Join(",", changed)}]") +
                $" sidts_lifetime_min={refreshed.SidtsLifetime}");
            if (changed.Length == 0) { Ended("same"); return; }
            // The sign-in this look was for may have been replaced while the page loaded (a renewal by hand, say): then these
            // cookies are older than the ones in use, and nothing is handed over.
            var adopted = host.Closed ? "closed" : UserBusy ? "dialog"
                : backend.Files.Clock.MadeAt != made || backend.Files.BrowserProfile.Id != profileId ? "stale" : null;
            if (adopted is null)
            {
                // Longer than the helper's own 40 s for its check, so the answer always comes and is never unknown.
                using var adopt = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                adopted = await backend.AdoptCookiesAsync(cookies, adopt.Token);
            }
            GoogleMessagesLog.Write($"browser early=adopt outcome={adopted}");
            if (adopted == "adopted") googleSchedule.Adopted(made);
            else Ended(adopted);
            // The helper is tried a moment later either way. Adopted: the running connection must take the new cookies as
            // Google's configuration page did. Not taken: loading the profile may have cost the helper the cookies it
            // kept (two holders of one sign-in is how other clients lost theirs). A refusal is renewed within seconds by
            // the ordinary path; one right after an adoption also means adopting does not help, and it stops.
            var expiry = backend.SignInGeneration;
            googleRecovering = false; holding = false;
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (host.Closed) return;
            // Refused (and perhaps renewed) meanwhile: that is the answer, and a read now would go to a different helper. Only
            // disconnected (asleep, a reconnect): no answer either way, and nothing is concluded from it.
            var outcome = backend.SignInGeneration != expiry || backend.SignInExpired ? "superseded" : !backend.CanReceive ? "skipped" : null;
            if (outcome is null)
            {
                using var check = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                outcome = await backend.CheckSignInAsync(check.Token);
            }
            GoogleMessagesLog.Write($"browser early=check outcome={outcome}");
            if (adopted == "adopted" && outcome is "refused" or "superseded") Ended("adopted_refused");
        }
        catch (Exception failure) { GoogleMessagesLog.Write($"browser early=failed reason={GoogleMessagesLog.Reason(failure)}"); }
        finally
        {
            cookies?.Clear();
            // Once let go for the check, the flag may belong to a renewal that check started.
            if (holding) googleRecovering = false;
        }
    }

    // The refresh cannot help now, so the notice it held back is given (the window draws it). A presumed expiry has nothing
    // to give: the backend clears it, and the launch's own load, which stopped at the presumption, is run again from the
    // connect it skipped, so the conversations, a click on a notification that waited for them, and the check for sends a
    // previous run left open all happen as at any launch.
    private void ReleaseGoogleSignInNotice(GoogleMessagesBackend backend, string reason)
    {
        var released = backend.EndSignInRecovery(reason);
        if (host.Closed || released || backend.PairingLost || backend.SignInExpired) return;
        host.ReloadAfterRelease();
    }

    // One deferral at a time, looked at again when it comes due. Logged when its reason changes, not every 30 seconds.
    private void DeferGoogleRecovery(GoogleMessagesBackend backend, string reason, TimeSpan wait)
    {
        if (googleRecoveryDeferred) return;
        googleRecoveryDeferred = true;
        if (googleRecoveryDeferredReason != reason) GoogleMessagesLog.Write($"browser recovery=deferred reason={reason} s={(int)wait.TotalSeconds}");
        googleRecoveryDeferredReason = reason;
        _ = Task.Delay(wait).ContinueWith(_ => host.Post(() =>
        {
            googleRecoveryDeferred = false;
            if (!host.Closed) _ = RecoverGoogleSignInAsync(backend);
        }), TaskScheduler.Default);
    }
}
