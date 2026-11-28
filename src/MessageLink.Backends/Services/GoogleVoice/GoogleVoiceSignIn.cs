using MessageLink.App.Services.GoogleMessages;

namespace MessageLink.App.Services.GoogleVoice;

// How old a Voice account's sign-in is. Google refuses a sign-in this app copied out of the browser two hours after
// Google made it (GoogleSignInClock), and only a new __Secure-1PSIDTS from the kept profile starts the two hours again,
// so the time is kept with the account's saved session and moves only when that cookie does. Only the time is kept.
internal static class GoogleVoiceSignIn
{
    public const string Timestamp = "__Secure-1PSIDTS";
    private static readonly string[] AccountCookies = ["SID", "HSID", "SSID", "APISID", "SAPISID", "__Secure-1PSID", "__Secure-3PSID",
        Timestamp, "__Secure-3PSIDTS"];

    // The time to keep with a session about to be saved: the one saved before while the timestamp cookie is unchanged,
    // now when Google issued a new one (or the account is new here), and unknown when the session has none.
    public static DateTimeOffset? MadeAt(IReadOnlyDictionary<string, string>? previous, DateTimeOffset? previousMade,
        IReadOnlyDictionary<string, string> next, DateTimeOffset now)
    {
        if (!next.TryGetValue(Timestamp, out var fresh) || fresh.Length == 0) return null;
        if (previous is not null && previous.TryGetValue(Timestamp, out var before) && before == fresh) return previousMade;
        return now;
    }

    // Which Google-account cookies differ between two sessions, by name, for the log. Values are compared in memory only.
    public static string[] Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        AccountCookies.Where(name => after.TryGetValue(name, out var fresh) && fresh.Length > 0 &&
            (!before.TryGetValue(name, out var old) || old != fresh)).ToArray();

    // Past this at launch the saved sign-in is refreshed from the kept profile before the helper starts, as Google
    // Messages does, instead of starting one that Google refuses now or within minutes.
    public static bool StaleAtLaunch(DateTimeOffset? made, DateTimeOffset now) => made is { } at && now - at >= GoogleSignInClock.Stale;
}

// When one Voice account's sign-in is renewed early: once per sign-in, between 110 and 117 minutes after Google made it
// (GoogleSignInSchedule's window), the kept profile is loaded and a new sign-in handed to the running helper in place.
// A try that fails for a passing reason costs that sign-in's try only, and Google's refusal at two hours is recovered the
// way it always was; an answer that would come again every time stops it for the account for the run.
internal sealed class GoogleVoiceEarlyRenewal
{
    private DateTimeOffset? triedFor;

    public bool Off { get; private set; }

    public bool Due(DateTimeOffset now, DateTimeOffset? made)
    {
        if (Off || made is not { } at || triedFor == at) return false;
        var age = now - at;
        if (age < GoogleSignInSchedule.EarlyFrom || age >= GoogleSignInSchedule.EarlyUntil) return false;
        triedFor = at;
        return true;
    }

    // How a try ended, as a fixed word. True when this answer is what stopped early renewal for the run.
    public bool Ended(string outcome)
    {
        if (Off || outcome is not ("same" or "no_profile" or "sign_in_needed" or "missing_cookies" or "new_window" or "wrong_account" or
            "refused")) return false;
        Off = true;
        return true;
    }
}
