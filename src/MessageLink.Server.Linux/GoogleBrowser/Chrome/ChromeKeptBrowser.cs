using System.Diagnostics;
using MessageLink.App;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;

namespace MessageLink.Server.Linux;

/// <summary>
/// <see cref="IGoogleBrowser"/> for the server's own Chrome (Chrome on the server), so that <see cref="GoogleSignInKeeper"/> (its 110-minute renewal, and the renewal after a refusal) asks
/// the one browser that signed in and gets Chrome's own cookies. Chrome is the PC's WebView2 in this arrangement: signed in by a person, never automated, and left to renew Google's sign-in itself. What
/// the server does to it is close it and start it again, and read its files:
/// <list type="number">
/// <item>close it gently (SIGTERM, wait for it to exit, so its cookie store is written) and start it again on the profile with <c>--app=https://messages.google.com/web/config</c>: the page loads fresh
/// whatever Chrome was showing, and with it whatever Google rotates for a signed-in browser. No key is pressed and nothing is attached (no DevTools, no input on a page that might be showing anything);</item>
/// <item>wait: Chrome writes its cookie store in batches (every 30 seconds, <c>kCommitInterval</c>), so the store is read again until <c>__Secure-1PSIDTS</c> has changed or the wait is over;</item>
/// <item>hand the cookies on exactly as a person's sign-in does; whether the page changed the timestamp cookie is logged as <c>rotated=yes|no</c> (in memory: the values are compared and dropped).</item>
/// </list>
/// One use of Chrome's display at a time (<see cref="PersistentChrome.HoldAsync"/>). Logged as fixed words and counts: <c>google chrome renewed rotated= sidts=</c>.
/// </summary>
internal sealed class ChromeKeptBrowser(PersistentChrome chrome, IChromeCookieSource cookies, Action<string> log) : IGoogleBrowser
{
    public bool RefreshBeforeConnect => false;
    /// <summary>How long a renewal waits for another use of the display.</summary>
    public TimeSpan QueueBudget { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The first look at the store after Chrome is started again, and the gap between looks after that.</summary>
    public TimeSpan FirstLook { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollGap { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The longest the store is waited on: past Chrome's own 30-second batch.</summary>
    public TimeSpan PollBudget { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>
    /// How the last renewal ended for the timestamp cookie: <c>yes</c> (one there now that was not before, a first one included), <c>no</c> (the same one), or <c>none</c> (none
    /// after the renewal). Read by <see cref="ChromeRenewalLoop"/>, which adopts only a <c>yes</c>.
    /// </summary>
    public string LastRotated { get; private set; } = "none";

    public async Task<GoogleRefreshResult> RefreshAsync(string service, string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (service == GoogleSignInProfile.VoiceService) return await RefreshVoiceAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (service != GoogleSignInProfile.MessagesService || !GoogleSignInProfile.TryOpen(profileId, out var profile, service) || profile is null) return new(null, "no_profile");
        IDisposable hold;
        try { hold = await chrome.HoldAsync(QueueBudget, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return new(null, "gate_timeout"); }
        using (hold)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                // Closed gently first, so that what it holds is on disk (the "before" is then the whole truth), and started again on the one page it is for; no key is sent to a page that might be
                // showing anything else.
                await chrome.StopAsync().ConfigureAwait(false);
                var before = await cookies.ReadAsync(profile.Path, cancellationToken).ConfigureAwait(false);
                if (!await chrome.EnsureAsync(profile.Path, cancellationToken).ConfigureAwait(false)) return new(null, "browser_slow");
                var after = await WaitForStoreAsync(profile.Path, before?.Sidts, cancellationToken).ConfigureAwait(false);
                if (after is null) return new(null, "browser_slow");
                var rotated = after.Sidts is null ? "none" : after.Sidts != before?.Sidts ? "yes" : "no";
                LastRotated = rotated;
                log($"google chrome renewed rotated={rotated} sidts={after.SidtsLifetime} complete={(after.Complete ? "yes" : "no")} ms={clock.ElapsedMilliseconds}");
                return after.Complete ? new(after.Cookies, "", after.SidtsLifetime) : new(null, after.Cookies.Count == 0 ? "sign_in_needed" : "missing_cookies", after.SidtsLifetime);
            }
            catch (OperationCanceledException) { throw; }
            // The type only: a failure's message can name a path.
            catch (Exception failure)
            {
                log($"google chrome renewal=failed type={failure.GetType().Name}");
                return new(null, "browser_slow");
            }
        }
    }

    /// <summary>
    /// A Google Voice account's renewal and its recovery after a refusal (<see cref="GoogleVoiceAccounts"/> asks for both through this port): under the same hold, Chrome is closed and started on the account's
    /// own profile at <c>voice.google.com</c> (the one page, loaded as the PC's renewal loads it; nothing is attached, pressed or opened in it), the store is read until it holds a complete Voice
    /// jar, and Chrome is put back on the Messages profile, if there is one, whatever happened. A Voice profile is never the one Messages' Chrome runs on, and Voice's page is loaded here and
    /// nowhere else: not at start, and not while the account is merely connected.
    /// </summary>
    private async Task<GoogleRefreshResult> RefreshVoiceAsync(string profileId, CancellationToken cancellationToken)
    {
        if (!GoogleSignInProfile.TryOpen(profileId, out var profile, GoogleSignInProfile.VoiceService) || profile is null) return new(null, "no_profile");
        IDisposable hold;
        try { hold = await chrome.HoldAsync(QueueBudget, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return new(null, "gate_timeout"); }
        using (hold)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                await chrome.StopAsync().ConfigureAwait(false);
                var before = await cookies.ReadAsync(profile.Path, ChromiumRenewalTarget.Voice, cancellationToken).ConfigureAwait(false);
                if (!await chrome.EnsureAsync(profile.Path, cancellationToken, GoogleVoiceSignInRules.VoiceUrl).ConfigureAwait(false)) return new(null, "browser_slow");
                var after = await WaitForStoreAsync(profile.Path, before?.Sidts, cancellationToken, ChromiumRenewalTarget.Voice).ConfigureAwait(false);
                if (after is null) return new(null, "browser_slow");
                var rotated = after.Sidts is null ? "none" : after.Sidts != before?.Sidts ? "yes" : "no";
                log($"google chrome renewed service=voice rotated={rotated} sidts={after.SidtsLifetime} complete={(after.Complete ? "yes" : "no")} ms={clock.ElapsedMilliseconds}");
                return after.Complete ? new(after.Cookies, "", after.SidtsLifetime) : new(null, after.Cookies.Count == 0 ? "sign_in_needed" : "missing_cookies", after.SidtsLifetime);
            }
            catch (OperationCanceledException) { throw; }
            // The type only: a failure's message can name a path.
            catch (Exception failure)
            {
                log($"google chrome renewal=failed service=voice type={failure.GetType().Name}");
                return new(null, "browser_slow");
            }
            finally { await MessagesChrome.ResumeAsync(chrome, log).ConfigureAwait(false); }
        }
    }

    // Looks at the store after the first wait, and again until the timestamp cookie is not the one it was, or the budget is spent; the last look is the answer. Voice's jar from Chrome may have no
    // timestamp cookie at all (Google gave the server's Chrome none, the live run of 2026-10-11): a complete one then ends the wait at the first look, as there is nothing to wait for.
    private async Task<ChromeCookieRead?> WaitForStoreAsync(string profileFolder, string? before, CancellationToken cancellationToken, ChromiumRenewalTarget? target = null)
    {
        var clock = Stopwatch.StartNew();
        ChromeCookieRead? latest = null;
        await Task.Delay(FirstLook, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            latest = await cookies.ReadAsync(profileFolder, target ?? ChromiumRenewalTarget.Messages, cancellationToken).ConfigureAwait(false) ?? latest;
            if (latest is { Sidts: { } now } && (before is null || now != before)) return latest;
            if (target is not null && latest is { Complete: true, Sidts: null }) return latest;
            if (clock.Elapsed + PollGap > PollBudget) return latest;
            await Task.Delay(PollGap, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> seed, CancellationToken cancellationToken) => Task.FromResult<GoogleSignInResult?>(null);

    public bool HasProfile(string profileId, string service = GoogleSignInProfile.VoiceService) => GoogleSignInProfile.TryOpen(profileId, out _, service);

    public Task DeleteProfileAsync(string profileId, string service = GoogleSignInProfile.VoiceService) => GoogleSignInProfile.DeleteAsync(profileId, service);
}

/// <summary>What <see cref="ChromeRenewalLoop"/> needs from the Google Messages backend. Reads and the one in-place adoption; the harness gives a fake.</summary>
internal interface IChromeRenewalTarget
{
    /// <summary>When the sign-in in use was made or last renewed (<see cref="GoogleSignInClock"/>), or null.</summary>
    DateTimeOffset? MadeAt { get; }
    /// <summary>Connected, not refused and not unpaired: the state in which renewing in place makes sense.</summary>
    bool Ready { get; }
    /// <summary>The kept profile's id, or null.</summary>
    string? ProfileId { get; }
    Task<string[]> ChangedFromSavedAsync(IReadOnlyDictionary<string, string> cookies);
    /// <summary>Hands the cookies to the running helper in place: <c>adopted</c>, or the helper's word for why not.</summary>
    Task<string> AdoptAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken);
}

/// <summary>
/// The PC's 110-minute load, done for the server's Chrome (Chrome on the server): a sign-in that has run about 100 minutes gets Chrome reloaded (<see cref="ChromeKeptBrowser"/>), and
/// when Chrome's page rotated <c>__Secure-1PSIDTS</c> the new cookies go into the running helper in place, which restarts the clock and so this loop's count. Nothing reconnects. When it did not
/// rotate (<c>rotated=no</c>) it is tried again after <see cref="Retry"/>; twice running is logged as <c>renewal=stuck</c>, and nothing else is done: Google refuses the sign-in at two hours, and the keeper
/// (<see cref="GoogleSignInKeeper"/>) then reloads and re-reads Chrome once more before it asks the person, exactly as it does on the PC. Never more than one reload per <see cref="Retry"/>.
/// </summary>
internal sealed class ChromeRenewalLoop(ChromeKeptBrowser browser, IChromeRenewalTarget target, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    /// <summary>How old a sign-in is when it is renewed, and how long a try that rotated nothing waits before the next.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromMinutes(100);
    public TimeSpan Retry { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>The cookies that name the signed-in session itself and do not change when Google rotates the timestamps (see <see cref="ChromeRemoteSignIn"/>).</summary>
    internal static readonly string[] SessionCookies = ["SID", "__Secure-1PSID", "__Secure-3PSID"];
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Renewals in a row that rotated nothing.</summary>
    public int Streak { get; private set; }
    /// <summary>Raised (once per streak reaching two) when Chrome cannot renew: the person will be asked at the two hours.</summary>
    public event Action? Stuck;

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? lastTry;
    private DateTimeOffset? triedFor;

    public async Task RunAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try { await TickAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception failure) { log($"google chrome renewal=failed type={failure.GetType().Name}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One look: renews if it is time. True when a renewal was tried.</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        if (target.ProfileId is not { } profileId || target.MadeAt is not { } made || !target.Ready) return false;
        var at = now();
        // A sign-in that is new (made by hand, or renewed in place) starts the count again.
        if (triedFor != made) { triedFor = made; lastTry = null; Streak = 0; }
        if (at - made < Every || lastTry is { } last && at - last < Retry) return false;
        lastTry = at;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var refreshed = await browser.RefreshAsync(GoogleSignInProfile.MessagesService, profileId, timeout.Token).ConfigureAwait(false);
        if (refreshed.Cookies is not { } cookies) { log($"google chrome renewal=failed reason={refreshed.Reason}"); return true; }
        try
        {
            // A sign-in with no timestamp cookie at all: Google gave this Chrome a session it does not run on the two-hour clock (the live run of 2026-10-11: no __Secure-1PSIDTS, and
            // the checks at 120 and 130 minutes were accepted). There is nothing to renew, so it is not "stuck": looked at again after the full interval, never every few minutes
            // (that run restarted Chrome every five). The keeper's own checks still catch a refusal.
            if (browser.LastRotated == "none" && refreshed.Cookies is not null)
            {
                Streak = 0;
                lastTry = at + Every - Retry;
                log("google chrome renewal=not_needed reason=no_timestamp");
                return true;
            }
            if (browser.LastRotated != "yes")
            {
                Streak++;
                log($"google chrome renewal=unchanged streak={Streak}");
                if (Streak == 2)
                {
                    log("google chrome renewal=stuck ask=at_refusal");
                    Stuck?.Invoke();
                }
                return true;
            }
            var changed = await target.ChangedFromSavedAsync(cookies).ConfigureAwait(false);
            using var adopt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            adopt.CancelAfter(TimeSpan.FromSeconds(90));
            // Only a renewal of the sign-in that is paired: a Rotating timestamp changes the __Secure-*PSIDTS cookies and nothing of the session's own; if the session cookies themselves are not the
            // ones saved, Chrome is signed in as someone else (or signed in again by someone) and its cookies are never put into the helper.
            var outcome = changed.Length == 0 ? "same" : changed.Any(name => SessionCookies.Contains(name, StringComparer.Ordinal)) ? "other_account"
                : await target.AdoptAsync(cookies, adopt.Token).ConfigureAwait(false);
            log($"google chrome adopt outcome={outcome}");
            if (outcome == "adopted") Streak = 0;
        }
        finally { cookies.Clear(); }
        return true;
    }
}
