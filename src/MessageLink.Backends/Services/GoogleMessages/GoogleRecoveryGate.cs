namespace MessageLink.App.Services.GoogleMessages;

internal enum GoogleRecoveryVerdict { Run, Defer, Stop, Skip }

// When the window may refresh a refused Google Messages sign-in from the browser profile (MainWindow.GoogleRecovery).
// Google refuses once per expiry, so a refusal that arrives while a dialog is open, while a refresh is still running, or
// within the spacing left by an earlier refresh is put off and looked at again rather than dropped. What stays bounded is
// the number of invisible browsers: one refresh per ten minutes, and after one that failed, or whose session Google
// refused again within the hour, only one more. After that the user is asked, as before.
internal sealed class GoogleRecoveryGate
{
    public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(10), Recheck = TimeSpan.FromSeconds(30), Held = TimeSpan.FromHours(1);
    public const int Attempts = 2;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue, previousAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset? connectedAt;
    private int failures;

    // Google refused the sign-in. A refusal soon after a refresh that connected means that refresh did not hold; one long
    // after the last refresh is a new expiry and starts afresh.
    public void NoteRefused(DateTimeOffset now)
    {
        if (connectedAt is { } at) failures = now - at < Held ? failures + 1 : 0;
        else if (now - lastAttempt >= Held) failures = 0;
        connectedAt = null;
    }

    // What to do now, and when to look again after putting it off. The reason is a fixed log word.
    public (GoogleRecoveryVerdict Verdict, string Reason, TimeSpan Wait) Decide(DateTimeOffset now, bool expired, bool recovering, bool dialogOpen, bool closed)
    {
        if (closed || !expired) return (GoogleRecoveryVerdict.Skip, closed ? "closed" : "not_expired", TimeSpan.Zero);
        if (failures >= Attempts) return (GoogleRecoveryVerdict.Stop, "failed_again", TimeSpan.Zero);
        if (recovering) return (GoogleRecoveryVerdict.Defer, "busy", Recheck);
        if (dialogOpen) return (GoogleRecoveryVerdict.Defer, "dialog", Recheck);
        var left = lastAttempt == DateTimeOffset.MinValue ? TimeSpan.Zero : lastAttempt + Spacing - now;
        return left > TimeSpan.Zero ? (GoogleRecoveryVerdict.Defer, "spacing", left > Recheck ? left : Recheck) : (GoogleRecoveryVerdict.Run, "", TimeSpan.Zero);
    }

    // Whether a verdict means no refresh is coming soon, so the "renew sign-in" notice held back for one is given now: the
    // gate has stopped, or the next refresh must wait out the spacing. A refresh already running, or a dialog open, keeps
    // it: the first may still renew the sign-in, and the second is the user already in front of it. Skip is a sign-in that
    // came back or a pairing now known lost, each of which ends the hold itself, or a window closing.
    public static bool EndsNoticeHold(GoogleRecoveryVerdict verdict, string reason) =>
        verdict == GoogleRecoveryVerdict.Stop || verdict == GoogleRecoveryVerdict.Defer && reason == "spacing";

    public void NoteAttempt(DateTimeOffset now) { previousAttempt = lastAttempt; lastAttempt = now; }
    public void NoteFailed() => failures++;
    public void NoteConnected(DateTimeOffset now) => connectedAt = now;
    // The user renewed by hand, with a new profile: what the old one failed at no longer counts against the new one, which
    // is otherwise never tried again until the gate's hour runs out.
    public void NoteSignedIn(DateTimeOffset now) { failures = 0; connectedAt = now; }
    // The browser ran but the sign-in was never tried (a dialog opened meanwhile, or the sign-in came back some other way):
    // not an attempt, so it neither spaces nor counts against the next one.
    public void Withdraw() => lastAttempt = previousAttempt;
}

// What a refresh brought back, and when nothing, why, as a fixed log word (GoogleBrowserRecovery). Every empty refresh was
// logged as auth_required and counted as a sign-in Google wanted by hand, though most reasons (another refresh holding the
// browser, a page that did not load) say nothing about the sign-in. SignInNeeded is the one set that does: a page wanting
// the account or password, or a sign-in that ended without its cookies.
internal sealed record GoogleRefreshResult(Dictionary<string, string>? Cookies, string Reason, string SidtsLifetime = "none")
{
    public bool SignInNeeded => Cookies is null && Reason is "sign_in_needed" or "missing_cookies" or "incomplete" or "new_window";

    // How long a cookie was set to last, for the log: "session" (until the browser closes), whole minutes from now, or
    // "past". WebView2 gives the expiry in seconds since 1970.
    public static string Lifetime(bool session, double expires, DateTimeOffset now)
    {
        if (session || double.IsNaN(expires) || expires <= 0) return "session";
        var minutes = (expires - now.ToUnixTimeMilliseconds() / 1000.0) / 60;
        return minutes <= 0 ? "past" : minutes > 5_256_000 ? "5256000" : ((long)minutes).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
