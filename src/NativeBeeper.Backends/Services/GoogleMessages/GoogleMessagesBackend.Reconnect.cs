using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// One connection attempt at a time, a pause after a failed one, and a lost pairing that stays lost until the user pairs
// again. Every Google read calls ConnectAsync. Each used to queue behind the lock and then make an attempt of its own,
// so with a phone that had removed this computer, attempt after attempt ran its whole minute back to back for as long
// as the app was open — 45 minutes in the case that found it — and anything else needing the lock, Pair again
// included, waited behind the queue.
internal sealed partial class GoogleMessagesBackend
{
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private int connectFailures;
    // The attempt in progress, so signing in can end it rather than wait it out.
    private CancellationTokenSource? attempt;
    private volatile bool signingIn;
    // Whether the phone removed the pairing, or Google stopped accepting only this computer's Google-account sign-in
    // while the phone keeps the pairing (connection_state.go, signed_out), and whether the notice for that is held.
    private readonly GoogleSignInState signIn = new();
    public bool PairingLost => signIn.PairingLost;
    public bool SignInExpired => signIn.SignInExpired;
    // The window's invisible refresh may still bring the sign-in back, so nothing says it needs renewing yet.
    public bool SignInHeld => signIn.Held(DateTimeOffset.UtcNow);
    // The expiry is the sign-in's age, not a refusal: the refresh runs before a connect Google would refuse.
    public bool SignInPresumed => signIn.Presumed;
    // Counts expiries: a caller that waited can tell whether the sign-in it knew was refused meanwhile.
    internal int SignInGeneration => signIn.Generation;
    // A renewal is under way or has just finished and its reconnect has not: the conversation header waits through this
    // rather than saying "Phone unavailable" for the second between the renewal and the connection coming back.
    public bool SignInSettling => signingIn || signIn.Held(DateTimeOffset.UtcNow) ||
        !connected && DateTimeOffset.UtcNow - lastSignedIn < TimeSpan.FromSeconds(15);
    private DateTimeOffset lastSignedIn = DateTimeOffset.MinValue;
    // Raised, from whatever thread noticed, each time Google refuses the sign-in; the window tries a silent refresh.
    public event EventHandler? SignInRefused;
    // Raised, from whatever thread, whenever what the sign-in state says to the user may have changed: expired, held,
    // released, cleared, lost or renewed. What is drawn from it is looked at again then.
    public event EventHandler? SignInStateChanged;
    private void NoteSignInState() => SignInStateChanged?.Invoke(this, EventArgs.Empty);
    // Whether the window has a kept browser profile to refresh from (MainWindow.GoogleRecovery). Without one, an old
    // sign-in is still tried as it is, since nothing could refresh it first.
    internal Func<bool>? SilentRefreshAvailable { get; set; }
    // The sign-in a presumed expiry was last raised for, so an old sign-in is refreshed first once, not on every connect.
    private DateTimeOffset? presumedFor;
    // Why the last sign-in failed, as the helper's fixed code, so the dialog can offer what can still help.
    internal string? LastSignInFailure { get; private set; }

    // Nothing to try: the pairing is gone, sign-in has the connection, or the last attempt failed a moment ago. Callers
    // learn at once instead of queuing an attempt of their own.
    private void ThrowIfNotConnectable()
    {
        if (signIn.PairingLost) throw GoogleReadFailure.Map("pairing_lost");
        // While the window renews the sign-in nothing tells anyone to renew it: a read said "Open Settings and choose Renew
        // sign-in" over the conversation while the invisible refresh that made it unnecessary was still running.
        if (authenticationRequired && signIn.Held(DateTimeOffset.UtcNow)) throw GoogleReadFailure.Renewing();
        // An expired sign-in is renewed; anything else that refused the saved session is paired again.
        if (authenticationRequired) throw GoogleReadFailure.Map(signIn.SignInExpired ? "authentication_required" : "pairing_required");
        if (signingIn) throw new BackendException(BackendFailure.NotReady, "Google Messages is signing in. It connects when that finishes.");
        if (DateTimeOffset.UtcNow < retryAfter) throw GoogleReadFailure.Map("connect_backoff");
    }

    // Never paired on this PC: Google Messages is not set up, which is nobody's failure and nothing to retry. A first run
    // logged "connect starting" and "connect failed" every three seconds for the whole session and said a service needed
    // authorization (2026-10-02 clean-PC run). Now nothing is attempted, retried or announced, and it is logged once a run.
    // Pairing writes the session and connects as it always did; while it runs, the "signing in" answer stands. Callers
    // other than launch's Connect rarely get here: the list and the realtime loop leave an unpaired Google Messages out
    // (MessagingRouter.GoogleMessagesExpected).
    internal const string NotSetUp = "Google Messages isn't set up. Pair it in Settings.";
    private int notPairedConnectLogged, notPairedRealtimeLogged;
    private bool NeverPaired => !Files.SessionSaved;
    private void ThrowIfNotSetUp()
    {
        if (signingIn || !NeverPaired) return;
        if (Interlocked.Exchange(ref notPairedConnectLogged, 1) == 0) GoogleMessagesLog.Write("connect skipped reason=not_paired");
        throw new BackendException(BackendFailure.Unavailable, NotSetUp);
    }

    private void NoteConnectFailed()
    {
        connectFailures++;
        var pause = GoogleReconnect.Pause(connectFailures);
        retryAfter = DateTimeOffset.UtcNow + pause;
        GoogleMessagesLog.Write($"connect paused s={(int)pause.TotalSeconds} failures={connectFailures}");
    }

    private void NoteConnected() { connectFailures = 0; retryAfter = DateTimeOffset.MinValue; signIn.ResetAnnouncement(); }

    // Launch and Reconnect are someone asking now, so they try at once rather than waiting out the pause.
    private void ClearPause() => retryAfter = DateTimeOffset.MinValue;

    // The helper reports a removed pairing as an event, and a request it ended for that reason as pairing_lost. Either
    // marks the account for pairing again, once. The event published here reaches the app only if the realtime loop is
    // reading; when a connect attempt is what ended, the loop says it itself on its next pass (RunAsync).
    private void MarkPairingLost(string reason)
    {
        if (!signIn.LosePairing()) return;
        authenticationRequired = true; connected = false;
        lock (typingLock) typing.Clear();
        GoogleMessagesLog.Write($"pairing lost reason={reason}");
        Publish(new(RealtimeKind.AuthenticationRequired));
        NoteSignInState();
    }

    // Once per expiry, whether Google refused a poll mid-session or the connect at launch, or the sign-in is old enough
    // that Google is about to (presumed). While the window refreshes the sign-in from its browser profile, nothing says it
    // needs renewing: the refresh ends the hold when it cannot help (EndSignInRecovery), a renewal ends it by succeeding,
    // and a timer ends it after GoogleSignInState.Hold regardless, which before said nothing at all mid-session.
    private void MarkSignInExpired(string reason, bool presumed = false)
    {
        // A QR pairing has no Google-account sign-in to expire (GoogleMessagesBackend.QrPairing.cs): a refusal of its credentials is the pairing itself being refused, which
        // nothing can renew and only pairing again brings back. Said as that, so no renewal is tried and the keeper is never woken.
        if (qrSession) { MarkPairingLost("qr_" + GoogleReconnect.Code(reason)); return; }
        if (!signIn.Expire(DateTimeOffset.UtcNow, SignInRefused is not null, out var held, presumed)) return;
        authenticationRequired = true; connected = false;
        lock (typingLock) typing.Clear();
        var age = Files.Clock.AgeMinutes(DateTimeOffset.UtcNow);
        GoogleMessagesLog.Write($"sign_in expired reason={reason} notice={(held ? "held" : "shown")} age_min={age?.ToString() ?? "unknown"}");
        if (!held) Publish(new(RealtimeKind.AuthenticationRequired, SignInOnly: signIn.SignInExpired));
        else
        {
            var expiry = signIn.Generation;
            _ = Task.Delay(GoogleSignInState.Hold).ContinueWith(_ => EndSignInRecovery("hold_expired", expiry), TaskScheduler.Default);
        }
        NoteSignInState();
        SignInRefused?.Invoke(this, EventArgs.Empty);
    }

    // A sign-in older than Files.Clock.Stale, with a kept profile to refresh it from and the window listening: the
    // connect is not tried, since Google refuses these at two hours, and the refresh that follows a refusal runs now
    // instead. Once per sign-in: if the refresh cannot help, the connect goes ahead as it always did.
    private bool PresumeExpired()
    {
        if (qrSession || Files.Clock.MadeAt is not { } made || DateTimeOffset.UtcNow - made < Files.Clock.Stale || presumedFor == made) return false;
        if (SignInRefused is null || SilentRefreshAvailable?.Invoke() != true) return false;
        presumedFor = made;
        MarkSignInExpired("stale", presumed: true);
        return signIn.Presumed;
    }

    // The refresh cannot bring the sign-in back now: it failed, has nothing to refresh from, or must wait out its spacing.
    // The notice held for it is given. The event wakes a realtime loop that was waiting for events when Google refused
    // mid-session; at launch the loop is between attempts and says it itself on its next pass (RunAsync). The reason is a
    // fixed log word. True when a notice had been held. A presumed expiry has nothing to say: Google refused nothing, so
    // it is cleared and the next connect tries the saved sign-in. An expiry, when given, must still be the current one.
    internal bool EndSignInRecovery(string reason, int? expiry = null)
    {
        if (signIn.Presumed)
        {
            // Cleared first, so no connect in between reads a sign-in that is neither expired nor connectable (it would say
            // "needs pairing again"); put back if a refusal confirmed the expiry meanwhile.
            authenticationRequired = false;
            if (signIn.ClearPresumed(expiry))
            {
                ClearPause();
                // The connect that follows is the one the presumption held back: the header waits for it as for a renewal.
                lastSignedIn = DateTimeOffset.UtcNow;
                GoogleMessagesLog.Write($"sign_in presumed=cleared reason={GoogleReconnect.Code(reason)}");
                NoteSignInState();
                return false;
            }
            authenticationRequired = signIn.SignInExpired || signIn.PairingLost;
        }
        if (!signIn.Release(expiry)) return false;
        GoogleMessagesLog.Write($"sign_in notice=released reason={GoogleReconnect.Code(reason)}");
        Publish(new(RealtimeKind.AuthenticationRequired, SignInOnly: signIn.SignInExpired));
        NoteSignInState();
        _ = RetireRefusedHelperAsync();
        return true;
    }

    // Nothing is going to renew the sign-in by itself now, so the helper Google refused is ended: the library kept pinging
    // and retrying its acknowledgements with the refused cookies every five seconds for as long as the app ran, and each
    // retry was a line in the log. Its last session is saved first; the next connect after a renewal starts a new one.
    private async Task RetireRefusedHelperAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (helper is not { } refused || !authenticationRequired || connected) return;
            if (latestSession is { } last) await PersistSessionAsync(refused, last, "retired");
            helper = null;
            latestSession = null;
            await refused.DisposeAsync();
            GoogleMessagesLog.Write("helper retired reason=refused");
        }
        catch (Exception failure) { GoogleMessagesLog.Write($"helper retire=failed reason={GoogleMessagesLog.Reason(failure)}"); }
        finally { gate.Release(); }
    }

    // Google's logout marker: reconnect, and doubt the pairing only if it comes again (GoogleReconnect.LogoutConfirmed).
    // A second one is not proof either: it is treated as a refused sign-in, whose renewal asks Google about the pairing
    // itself and marks it lost only if Google no longer knows it (reauth.go). It used to mark the pairing lost outright,
    // which left only Pair as a new device on offer for a pairing that might stand (2026-09-24 audit).
    private DateTimeOffset? lastLogoutMarker;
    private void NoteLogoutMarker()
    {
        var now = DateTimeOffset.UtcNow;
        if (GoogleReconnect.LogoutConfirmed(lastLogoutMarker, now)) { MarkSignInExpired("logged_out_twice"); return; }
        lastLogoutMarker = now;
        GoogleMessagesLog.Write("logout marker action=reconnect");
        connected = false;
        Publish(new(RealtimeKind.Disconnected));
    }

    // Ends an attempt in progress and holds new ones off while the user pairs.
    private void BeginSignIn()
    {
        signingIn = true;
        try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void NoteSignedIn()
    {
        signIn.SignedIn(); authenticationRequired = false; lastLogoutMarker = null;
        lastSignedIn = DateTimeOffset.UtcNow;
        NoteConnected();
        NoteSignInState();
    }

    // One read at the moment Google is expected to refuse the sign-in (GoogleSignInClock): a refusal is then heard within
    // seconds and renewed, rather than whenever the next ping or acknowledgement happens to go out, up to twenty minutes
    // later, with the app showing Google Messages as connected meanwhile. A read, so it sends and marks nothing; if Google
    // still accepts the sign-in, that too is worth knowing. The outcome is a fixed log word.
    internal async Task<string> CheckSignInAsync(CancellationToken token)
    {
        if (!connected || helper is null) return "skipped";
        try { await ReadAsync("ui.chatList", new { count = 1 }, token); return "accepted"; }
        catch (Exception) when (signIn.SignInExpired) { return "refused"; }
        catch (Exception) { return "failed"; }
    }

    // When the realtime loop may try again: at once after a success or a sign-in, after the pause after a failure.
    private TimeSpan NextAttemptDelay()
    {
        var wait = retryAfter - DateTimeOffset.UtcNow;
        return wait > TimeSpan.FromSeconds(3) ? wait : TimeSpan.FromSeconds(3);
    }
}

