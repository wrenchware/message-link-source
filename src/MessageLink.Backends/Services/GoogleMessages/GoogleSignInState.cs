namespace MessageLink.App.Services.GoogleMessages;

internal enum GoogleConnectRefusal { Other, SignInExpired, PairingLost }

// What Google has said about this computer's sign-in and pairing, and whether the "sign in" notice is being kept back
// while the window refreshes the sign-in invisibly (MainWindow.GoogleRecovery).
//
// A refused sign-in used to be recognised only when Google refused a poll mid-session. Refused at launch — on 2026-09-24,
// after six hours with Messages closed — the same refusal came back from connect as a bare authentication_required: the
// sign-in was never marked expired, nothing refreshed it, and the pairing dialog led with "Pair as a new device" for a
// pairing the phone still had. And where the refresh did run, the notice it was about to make unnecessary was already on
// screen, to vanish a few seconds later. The notice now waits for the refresh to be over, or to be unable to run, and
// for no longer than Hold in any case.
internal sealed class GoogleSignInState
{
    // Long enough for a browser refresh, a dialog it waits out, and the renewal after it; what is held longer is said.
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);
    private readonly object sync = new();
    private bool pairingLost, signInExpired, presumed, announced;
    private DateTimeOffset holdUntil = DateTimeOffset.MinValue;
    // Which expiry a hold belongs to, so a timer set for one cannot end the next.
    private int generation;

    public bool PairingLost { get { lock (sync) return pairingLost; } }
    // Only the sign-in: renewing brings it back, and pairing afresh is not needed.
    public bool SignInExpired { get { lock (sync) return signInExpired && !pairingLost; } }
    // Expired by its age rather than by Google: nothing has been refused yet (GoogleSignInClock).
    public bool Presumed { get { lock (sync) return signInExpired && presumed && !pairingLost; } }
    public int Generation { get { lock (sync) return generation; } }
    public bool Held(DateTimeOffset now) { lock (sync) return IsHeld(now); }
    private bool IsHeld(DateTimeOffset now) => signInExpired && !pairingLost && now < holdUntil;

    // What a connect the helper refused proves (the helper's connect_errors.go). A 401 there, with a saved pairing, is the
    // Google-account sign-in; the pairing stands. pairing_required means only that the helper holds no client, and every
    // other code is about the network or Google's service, so none of them says anything about either.
    public static GoogleConnectRefusal ConnectRefusal(string? code) => code switch
    {
        "pairing_lost" => GoogleConnectRefusal.PairingLost,
        "authentication_required" => GoogleConnectRefusal.SignInExpired,
        _ => GoogleConnectRefusal.Other,
    };

    // Google refused the sign-in, or (presumed) it is old enough that Google is about to. True the first time per expiry,
    // and never once the pairing is known lost, so the refusal is said once. With a refresh listening the notice is held
    // while it runs. A refusal that arrives during a presumed expiry is that expiry confirmed, not a second one.
    public bool Expire(DateTimeOffset now, bool refreshListening, out bool held, bool presumed = false)
    {
        lock (sync)
        {
            held = false;
            if (pairingLost) return false;
            if (signInExpired)
            {
                if (!presumed) this.presumed = false;
                return false;
            }
            signInExpired = true;
            this.presumed = presumed;
            generation++;
            if (refreshListening) { holdUntil = now + Hold; held = true; }
            return true;
        }
    }

    // A presumed expiry the refresh could not settle: Google refused nothing, so nothing is said, and the next connect
    // tries the saved sign-in as it always did. True when there was one to clear. A given generation must be the current.
    public bool ClearPresumed(int? expiry = null)
    {
        lock (sync)
        {
            if (!signInExpired || !presumed || pairingLost || expiry is { } g && g != generation) return false;
            signInExpired = false; presumed = false; holdUntil = DateTimeOffset.MinValue;
            return true;
        }
    }

    // The phone removed this computer: only pairing again helps, and there is nothing to wait for. True the first time.
    public bool LosePairing()
    {
        lock (sync)
        {
            if (pairingLost) return false;
            pairingLost = true; presumed = false; holdUntil = DateTimeOffset.MinValue;
            return true;
        }
    }

    // The refresh will not bring the sign-in back now. True when a notice was being held, which the caller then gives. A
    // given generation must be the current one, so a hold's own timer never ends a later expiry's hold.
    public bool Release(int? expiry = null)
    {
        lock (sync)
        {
            if (holdUntil == DateTimeOffset.MinValue || expiry is { } g && g != generation) return false;
            holdUntil = DateTimeOffset.MinValue;
            return true;
        }
    }

    // Whether a connect that just failed leaves the sign-in refused: its own refusal, or one already known. A connect the
    // phone's silence ended (backend_unavailable) says nothing about either, and must not clear them.
    public bool StillRefused(bool refusedNow) { lock (sync) return refusedNow || signInExpired || pairingLost; }

    public void SignedIn() { lock (sync) { pairingLost = false; signInExpired = false; presumed = false; holdUntil = DateTimeOffset.MinValue; announced = false; } }

    // The realtime loop's own "sign in" notice: once per run of the loop, and not while the refresh may still make it
    // unnecessary.
    public bool Announce(DateTimeOffset now)
    {
        lock (sync)
        {
            if (announced || IsHeld(now)) return false;
            announced = true;
            return true;
        }
    }

    // A new run of the realtime loop, or a connection: the next refusal is said again.
    public void ResetAnnouncement() { lock (sync) announced = false; }
}
