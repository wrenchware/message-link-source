using System.Diagnostics;
using System.Text.Json;
using MessageLink.App;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.Core.Backend;
using MessageLink.Core.Services;

namespace MessageLink.Server.Linux;

/// <summary>
/// <see cref="IGoogleBrowser"/> on Linux (SWITCHBOARD.md 4.3): what the Windows renewal does with an invisible WebView2
/// (<c>GoogleBrowserRecovery</c>), done with a Chromium (headless, or headed on a virtual display, <see cref="Headed"/>) driven over its DevTools pipe (<see cref="ChromiumLauncher"/>,
/// <see cref="CdpConnection"/>). The same answers in the same words, so <see cref="GoogleSignInKeeper"/> and Voice's recovery cannot tell the two apart:
/// <list type="bullet">
/// <item>one browser at a time, each on its own profile, started for the one page and ended after it (never kept running between renewals);</item>
/// <item>the one fixed page, with every main-frame document request checked against the target's allowed navigations, and anything else refused
/// (<c>sign_in_needed</c>): an account chooser or password page is never filled in here, the user is asked instead, as on Windows;</item>
/// <item>new windows and downloads refused; the cookies read back only from the target's addresses and only those its rules accept, never logged.</item>
/// </list>
/// A profile is a working Google sign-in, so it lives where <see cref="GoogleSignInProfile"/> is pointed (the keys volume, 0700), and nothing about
/// a run but its service, a fixed word and its time is written anywhere.
/// </summary>
internal sealed class ChromiumGoogleBrowser(IChromiumLauncher launcher, Action<string> log, Func<string, ChromiumRenewalTarget>? targets = null) : IGoogleBrowser
{
    private readonly Func<string, ChromiumRenewalTarget> targets = targets ?? ChromiumRenewalTarget.For;
    // One browser at a time, as on Windows: a home server's memory, and Google seeing one renewal at a time from this address.
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The page's budget once this run has the browser, and the wait for another run to let go of it: the Windows renewal's.</summary>
    public TimeSpan PageBudget { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan QueueBudget { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long the browser gets to close by itself (and write its cookies to the profile) before it is ended.</summary>
    public TimeSpan CloseBudget { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Whether the launcher runs the browser headed (said in each run's line, <c>headed=</c>; it changes nothing else here).</summary>
    public bool Headed { get; init; }

    public Task<GoogleRefreshResult> RefreshAsync(string service, string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = targets(service);
        if (!GoogleSignInProfile.TryOpen(profileId, out var profile, target.Service) || profile is null) return Task.FromResult(new GoogleRefreshResult(null, "no_profile"));
        return RunAsync(target, profile, null, deleteOnFailure: false, cancellationToken);
    }

    public async Task<GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken) =>
        (await CreateProfileWithReasonAsync(GoogleSignInProfile.VoiceService, cookies, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// A new profile for <paramref name="service"/>, seeded from <paramref name="cookies"/> alone and loaded once, so it holds what Google sets on
    /// that page; null (with the reason) when the load did not end signed in, and then the new profile is deleted. This is how a server gets a
    /// profile for a sign-in made in a client's window (<see cref="ProfileKeepingGoogleAccount"/>): whether Google renews a profile seeded on another
    /// machine is what SWITCHBOARD.md 5.4 tests live.
    /// </summary>
    /// <param name="profileCookies">Every Google cookie the client's sign-in browser held, when the client sent them (SWITCHBOARD.md 5.4): the profile is then seeded with
    /// all of them as that browser had them, and with <paramref name="cookies"/> only for what they do not cover. Null: <paramref name="cookies"/> alone, as before.</param>
    public async Task<(GoogleSignInResult? Result, string Reason)> CreateProfileWithReasonAsync(string service, IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken, IReadOnlyList<GoogleProfileCookie>? profileCookies = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = targets(service);
        var profile = GoogleSignInProfile.CreateAttempt(target.Service);
        var refreshed = await RunAsync(target, profile, cookies, deleteOnFailure: true, cancellationToken, profileCookies).ConfigureAwait(false);
        return refreshed.Cookies is { } kept ? (new GoogleSignInResult(kept, profile), "") : (null, refreshed.Reason);
    }

    /// <summary>
    /// The one-browser-at-a-time gate, held by something else that runs a browser of its own (the sign-in in the server's browser, <see cref="RemoteGoogleSignIn"/>): a renewal behind it
    /// waits as it would behind another renewal. <c>sign_in_busy</c> (an <see cref="InvalidOperationException"/>) when it is not free within <paramref name="wait"/>.
    /// </summary>
    internal async Task<IDisposable> HoldAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(wait);
        try { await gate.WaitAsync(bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("sign_in_busy"); }
        return new Hold(gate);
    }

    private sealed class Hold(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }

    public bool HasProfile(string profileId, string service = GoogleSignInProfile.VoiceService) => GoogleSignInProfile.TryOpen(profileId, out _, service);

    public Task DeleteProfileAsync(string profileId, string service = GoogleSignInProfile.VoiceService) => GoogleSignInProfile.DeleteAsync(profileId, service);

    private async Task<GoogleRefreshResult> RunAsync(ChromiumRenewalTarget target, GoogleSignInProfile profile, IReadOnlyDictionary<string, string>? seed,
        bool deleteOnFailure, CancellationToken cancellationToken, IReadOnlyList<GoogleProfileCookie>? profileCookies = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(QueueBudget);
        var entered = false;
        var succeeded = false;
        var clock = Stopwatch.StartNew();
        string outcome = "unavailable", sidts = "none";
        var notes = new RunNotes();
        try
        {
            try
            {
                await gate.WaitAsync(bounded.Token).ConfigureAwait(false);
                entered = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Ended(new(null, "gate_timeout")); }
            // The browser is this run's now: its own budget starts here.
            bounded.CancelAfter(PageBudget);
            clock.Restart();
            IChromiumProcess? process = null;
            CdpConnection? cdp = null;
            GoogleRefreshResult? result = null;
            try
            {
                process = launcher.Launch(profile.Path);
                cdp = new CdpConnection(process.FromBrowser, process.ToBrowser);
                cdp.Start();
                result = await DriveAsync(target, cdp, seed, profileCookies, notes, bounded.Token, cancellationToken).ConfigureAwait(false);
            }
            // The browser did not start, or answer, within the budget: a slow or busy host, nothing about the sign-in.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { result = new(null, "browser_slow"); }
            catch (OperationCanceledException) { outcome = "cancelled"; throw; }
            catch (BackendException) { throw; }
            catch (Exception failure)
            {
                // The type only: a browser's or the system's message can name the profile's path or the page.
                log($"google browser failed service={target.Service} type={failure.GetType().Name}");
                throw new BackendException(BackendFailure.Unavailable, target.FailureMessage);
            }
            finally { notes.Saved = await ShutAsync(cdp, process).ConfigureAwait(false); }
            // A new profile counts only once the browser has closed by itself after being asked to, which is when its cookie store is on disk: one
            // ended at the budget may hold none of what it was seeded with, and keeping it would leave the keeper a profile that cannot renew. A
            // renewal's cookies are handed on either way (the helper can use them now), and the log says whether the profile kept them.
            if (seed is not null && result.Cookies is { } unsaved && !notes.Saved)
            {
                unsaved.Clear();
                result = new(null, "browser_slow", result.SidtsLifetime);
            }
            succeeded = result.Cookies is not null;
            return Ended(result);
        }
        finally
        {
            if (entered) gate.Release();
            // Only once the browser is gone: it holds the folder until it exits.
            if (deleteOnFailure && !succeeded) await profile.DeleteAsync().ConfigureAwait(false);
            // Fixed words and numbers: the service, whether it was seeded, how it ended, how long the browser ran, how long Google said to keep the
            // sign-in's timestamp cookie and whether the page changed it (the measures SWITCHBOARD.md 5.4 reads), whether the profile was written,
            // and how many loads from outside Google's hosts were refused.
            log($"google browser service={target.Service} seeded={(seed is not null ? "yes" : "no")} outcome={outcome} ms={clock.ElapsedMilliseconds} sidts={sidts}"
                + $" rotated={notes.Rotated} saved={(notes.Saved ? "yes" : "no")} blocked={notes.Blocked} headed={(Headed ? "yes" : "no")}");
        }

        GoogleRefreshResult Ended(GoogleRefreshResult result)
        {
            outcome = result.Cookies is not null ? "ok" : result.Reason;
            sidts = result.SidtsLifetime;
            return result;
        }
    }

    // What a run says about itself beside its result, in words and counts only.
    private sealed class RunNotes
    {
        // Whether the page changed __Secure-1PSIDTS from what the profile (or the seeding) held before it loaded: yes, no, or none when there was no
        // such cookie afterwards. Its lifetime alone cannot say: a seeded one and Google's are both long. The values are compared here and dropped.
        public string Rotated = "none";
        public bool Saved;
        public int Blocked;
    }

    private async Task<GoogleRefreshResult> DriveAsync(ChromiumRenewalTarget target, CdpConnection cdp, IReadOnlyDictionary<string, string>? seed,
        IReadOnlyList<GoogleProfileCookie>? profileCookies, RunNotes notes, CancellationToken bounded, CancellationToken caller)
    {
        var targetId = Page(await cdp.SendAsync("Target.getTargets", cancellationToken: bounded).ConfigureAwait(false))
            ?? (await cdp.SendAsync("Target.createTarget", new { url = "about:blank" }, cancellationToken: bounded).ConfigureAwait(false)).GetProperty("targetId").GetString()!;
        var session = (await cdp.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, cancellationToken: bounded).ConfigureAwait(false))
            .GetProperty("sessionId").GetString()!;
        await cdp.SendAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, cancellationToken: bounded).ConfigureAwait(false);
        await cdp.SendAsync("Page.enable", null, session, bounded).ConfigureAwait(false);
        var mainFrame = (await cdp.SendAsync("Page.getFrameTree", null, session, bounded).ConfigureAwait(false))
            .GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        // Every request is held until it is checked. The main frame's documents are judged as WebView2's NavigationStarting judges the page's own:
        // anything the target does not allow is a sign-in for someone to make. Everything else (Google's own account frames, scripts, images) may
        // come only from Google's hosts: this browser runs without its sandbox in the container, so nothing from elsewhere is loaded into it at all.
        await cdp.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, session, bounded).ConfigureAwait(false);
        if (seed is not null) await SeedAsync(target, cdp, session, seed, profileCookies, bounded).ConfigureAwait(false);
        // What the profile holds before the page loads, to say afterwards whether the page changed it. In memory for the run only.
        var before = await SidtsAsync(target, cdp, session, bounded).ConfigureAwait(false);

        // Answered only once the navigation has committed or failed, which needs its document request let through below, so it is not awaited first.
        var navigate = cdp.SendAsync("Page.navigate", new { url = target.Url }, session, bounded);
        _ = navigate.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        var navigateSeen = false;
        string? mainUrl = null;
        var unreachable = false;
        Task<bool>? waiting = null;
        try
        {
            while (true)
            {
                waiting ??= cdp.Events.WaitToReadAsync(bounded).AsTask();
                if (!navigateSeen)
                {
                    await Task.WhenAny(waiting, navigate).ConfigureAwait(false);
                    if (navigate.IsCompleted)
                    {
                        navigateSeen = true;
                        // A navigation that failed outright (the address could not be reached). A request refused here ends the run where it is refused.
                        if (navigate.IsFaulted && navigate.Exception?.InnerException is CdpException) return new(null, "load_failed");
                        if (navigate.IsFaulted) throw new BackendException(BackendFailure.Unavailable, target.FailureMessage);
                        if (navigate.IsCompletedSuccessfully && navigate.Result.ValueKind == JsonValueKind.Object && navigate.Result.TryGetProperty("errorText", out var error)
                            && error.GetString() is { Length: > 0 } text && text != "net::ERR_BLOCKED_BY_CLIENT") return new(null, "load_failed");
                        if (!waiting.IsCompleted) continue;
                    }
                }
                // The browser's end closed: it exited or crashed under the page.
                if (!await waiting.ConfigureAwait(false)) throw new BackendException(BackendFailure.Unavailable, target.FailureMessage);
                waiting = null;
                while (cdp.Events.TryRead(out var message))
                {
                    if (message.SessionId is { } from && from != session) continue;
                    switch (message.Method)
                    {
                        case "Fetch.requestPaused":
                            var requestId = message.Params.GetProperty("requestId").GetString();
                            var url = message.Params.GetProperty("request").GetProperty("url").GetString();
                            var frame = message.Params.TryGetProperty("frameId", out var frameId) ? frameId.GetString() : null;
                            var document = message.Params.TryGetProperty("resourceType", out var kind) && kind.GetString() == "Document";
                            if (document && (frame == mainFrame || frame == targetId))
                            {
                                // The browser's own error page for an address it could not reach is not a page Google sent it to: let through, and
                                // ended below as load_failed when it has loaded (WebView2's failed NavigationCompleted).
                                if (!ErrorPage(url) && !target.AllowNavigation(url))
                                {
                                    // Any other document is a request for an interactive sign-in: refused, and the user is asked, as on Windows.
                                    await cdp.SendAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, session, bounded).ConfigureAwait(false);
                                    return new(null, "sign_in_needed");
                                }
                            }
                            else if (!target.AllowLoad(url))
                            {
                                notes.Blocked++;
                                await cdp.SendAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, session, bounded).ConfigureAwait(false);
                                break;
                            }
                            await cdp.SendAsync("Fetch.continueRequest", new { requestId }, session, bounded).ConfigureAwait(false);
                            break;
                        case "Page.frameNavigated":
                            var navigated = message.Params.GetProperty("frame");
                            if (navigated.TryGetProperty("parentId", out _)) break;
                            var address = navigated.TryGetProperty("url", out var given) ? given.GetString() : null;
                            // The blank page the browser starts on is not this run's navigation.
                            if (address is null or "about:blank") break;
                            mainUrl = address;
                            unreachable = navigated.TryGetProperty("unreachableUrl", out _) || ErrorPage(address);
                            break;
                        case "Page.loadEventFired":
                            if (mainUrl is null) break;
                            if (unreachable) return new(null, "load_failed");
                            if (!target.IsDone(mainUrl))
                            {
                                // A page the target allows on its way may hand on to the next by itself; the page budget ends one that does not.
                                if (target.AllowNavigation(mainUrl)) break;
                                return new(null, "sign_in_needed");
                            }
                            var read = await ReadCookiesAsync(target, cdp, session, bounded).ConfigureAwait(false);
                            var after = await SidtsAsync(target, cdp, session, bounded).ConfigureAwait(false);
                            notes.Rotated = after is null ? "none" : after == before ? "no" : "yes";
                            return read;
                        case "Page.windowOpen":
                            return new(null, "new_window");
                        case "Inspector.targetCrashed":
                        case "Inspector.detached":
                            throw new BackendException(BackendFailure.Unavailable, target.FailureMessage);
                    }
                }
            }
        }
        // Still on Google's pages when the time ran out: the chooser or a password page, waiting for someone.
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return new(null, "incomplete"); }
        catch (IOException) { throw new BackendException(BackendFailure.Unavailable, target.FailureMessage); }
    }

    // Where Chromium shows a page it could not load (chrome-error://chromewebdata/).
    internal static bool ErrorPage(string? url) => url is not null && url.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase);

    internal static string? Page(JsonElement targets)
    {
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
            if (info.GetProperty("type").GetString() == "page") return info.GetProperty("targetId").GetString();
        return null;
    }

    /// <summary>How long a seeded cookie is kept. The client hands over values only, not Google's expiry, and a cookie seeded without one would be a
    /// session cookie, which the browser drops when it closes, leaving the profile nothing to renew from at the two hours. 400 days, the most Chromium
    /// keeps any cookie, and not the year Google gave the renewed <c>__Secure-1PSIDTS</c> (PROVIDERS.md): a <c>sidts=</c> above 525600 minutes in the
    /// log is the seeded cookie untouched, not one Google issued (with <c>rotated=</c> beside it, which compares the values). The whole jar of a client's browser
    /// (<c>profileCookies</c>) carries each cookie's own expiry, session cookies included, and is seeded with this all the same: the seeded profile is only
    /// ever read back by a browser that must find the cookies after a close, and a session cookie would not be there.</summary>
    internal static readonly TimeSpan SeededLifetime = TimeSpan.FromDays(400);

    // Only into a new, empty profile, and only the cookies the target's rules would read back, each where they would read it from. Google's account
    // cookies go on the whole domain; a service's own (OSID, COMPASS) on its host alone, as Google sets them. Values never leave this method.
    //
    // When the client sent the whole of its sign-in browser's Google cookies (profileCookies, SWITCHBOARD.md 5.4) those are seeded first, each as that browser held it
    // (host-only ones, accounts.google.com's LSID and every __Host- cookie among them, on their host alone), and the helper's cookies only where those do not already
    // cover the name: seeding SID again without its HttpOnly flag would undo the browser's own.
    private async Task SeedAsync(ChromiumRenewalTarget target, CdpConnection cdp, string session, IReadOnlyDictionary<string, string> cookies,
        IReadOnlyList<GoogleProfileCookie>? profileCookies, CancellationToken bounded)
    {
        var expires = (DateTimeOffset.UtcNow + SeededLifetime).ToUnixTimeSeconds();
        HashSet<string>? covered = null;
        if (profileCookies is not null)
        {
            // The rules again, whatever the client did: a peer that sends more than it should gets no further than the rules.
            var accepted = GoogleProfileCookieRules.Select(profileCookies);
            // Only the names that were set count as covered: one the browser refused is still seeded from the helper's cookies.
            covered = await SeedProfileCookiesAsync(cdp, session, accepted, profileCookies.Count - accepted.Count, expires, bounded).ConfigureAwait(false);
        }
        foreach (var (name, value) in cookies)
        {
            if (covered is not null && covered.Contains(name)) continue;
            if (target.CookieDomain(name) is not { } domain || target.ReadCookie(name, domain, value) is null) continue;
            // Not HttpOnly, as the Windows seeding is not: Google's pages read some of these themselves (SAPISID signs their own requests).
            object cookie = domain == "google.com"
                ? new { name, value, domain = ".google.com", path = "/", secure = true, expires }
                : new { name, value, url = "https://" + domain + "/", path = "/", secure = true, expires };
            await cdp.SendAsync("Network.setCookie", cookie, session, bounded).ConfigureAwait(false);
        }
    }

    // The client's browser cookies, each as it held it: a host-only cookie on its host's address with no domain (a __Host- cookie is refused by the browser with
    // one, and accounts.google.com's own LSID would otherwise become visible to every google.com host), a domain cookie on the whole domain, with its path, Secure,
    // HttpOnly and SameSite. Every one gets the seeded lifetime whatever it had (SeededLifetime says why), so the profile holds them after the browser closes.
    // Logged: counts and the cookies' names (Google's fixed ones; anything else is only counted, as a name could carry data), never a value. Returns the names set.
    private async Task<HashSet<string>> SeedProfileCookiesAsync(CdpConnection cdp, string session, IReadOnlyList<GoogleProfileCookie> cookies, int refusedByRules,
        long expires, CancellationToken bounded)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int seeded = 0, hostOnly = 0, rejected = refusedByRules, other = 0;
        foreach (var cookie in cookies)
        {
            var parameters = new Dictionary<string, object>
            {
                ["name"] = cookie.Name,
                ["value"] = cookie.Value,
                ["path"] = cookie.Path,
                ["secure"] = cookie.Secure,
                ["httpOnly"] = cookie.HttpOnly,
                ["expires"] = expires,
            };
            if (cookie.HostOnly) parameters["url"] = "https://" + cookie.Domain + "/";
            else parameters["domain"] = "." + cookie.Domain;
            if (cookie.SameSite is { } sameSite) parameters["sameSite"] = sameSite;
            try
            {
                var answer = await cdp.SendAsync("Network.setCookie", parameters, session, bounded).ConfigureAwait(false);
                // Current Chromium answers an error for a cookie it will not hold and always success=true otherwise; older versions answered success=false.
                if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("success", out var done) && done.ValueKind == JsonValueKind.False) { rejected++; continue; }
            }
            // The code only (CdpException carries nothing else): one cookie the browser would not take must not end the seeding of the rest.
            catch (CdpException) { rejected++; continue; }
            seeded++;
            if (cookie.HostOnly) hostOnly++;
            if (!GoogleProfileCookieRules.IsLoggableName(cookie.Name)) other++;
            set.Add(cookie.Name);
        }
        var names = set.Where(GoogleProfileCookieRules.IsLoggableName).Order(StringComparer.Ordinal);
        log($"google browser seeded_cookies n={seeded} host_only={hostOnly} rejected={rejected} other={other} names=[{string.Join(",", names)}]");
        return set;
    }

    // The cookies the browser would send to the target's addresses, filtered at once by its rules, with how long the browser was told to keep
    // __Secure-1PSIDTS (GoogleRefreshResult.Lifetime: a bound sign-in gets a short one, an ordinary one months).
    internal static async Task<GoogleRefreshResult> ReadCookiesAsync(ChromiumRenewalTarget target, CdpConnection cdp, string session, CancellationToken bounded)
    {
        var answer = await cdp.SendAsync("Network.getCookies", new { urls = target.CookieUrls }, session, bounded).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var sidtsLifetime = "none";
        foreach (var cookie in answer.GetProperty("cookies").EnumerateArray())
        {
            var name = cookie.GetProperty("name").GetString() ?? "";
            var domain = cookie.GetProperty("domain").GetString() ?? "";
            if (target.ReadCookie(name, domain, cookie.GetProperty("value").GetString() ?? "") is { } value) result[name] = value;
            if (name == "__Secure-1PSIDTS")
                sidtsLifetime = GoogleRefreshResult.Lifetime(cookie.TryGetProperty("session", out var browserSession) && browserSession.GetBoolean(),
                    cookie.TryGetProperty("expires", out var expires) && expires.TryGetDouble(out var seconds) ? seconds : -1, DateTimeOffset.UtcNow);
        }
        if (!target.IsComplete(result)) result.Clear();
        return result.Count == 0 ? new(null, "missing_cookies", sidtsLifetime) : new(result, "", sidtsLifetime);
    }

    // The value of __Secure-1PSIDTS the browser would send to the target's addresses, or null. Never logged; compared and dropped.
    private static async Task<string?> SidtsAsync(ChromiumRenewalTarget target, CdpConnection cdp, string session, CancellationToken bounded)
    {
        var answer = await cdp.SendAsync("Network.getCookies", new { urls = target.CookieUrls }, session, bounded).ConfigureAwait(false);
        foreach (var cookie in answer.GetProperty("cookies").EnumerateArray())
            if (cookie.GetProperty("name").GetString() == "__Secure-1PSIDTS") return cookie.GetProperty("value").GetString();
        return null;
    }

    // Asked to close first, so the profile's cookie store is written as a browser writes it on closing; ended if it has not gone by the budget. The
    // run's own budget may be spent by now, so this has its own. True when the browser was asked to close and exited by itself within the budget,
    // which is when what it held is on disk. Whatever happened, everything it started is ended before this returns (IChromiumProcess.DisposeAsync).
    private async Task<bool> ShutAsync(CdpConnection? cdp, IChromiumProcess? process)
    {
        var asked = false;
        if (cdp is not null && !cdp.Closed.IsCompleted)
        {
            asked = true;
            using var closing = new CancellationTokenSource(CloseBudget);
            try { await cdp.SendAsync("Browser.close", cancellationToken: closing.Token).ConfigureAwait(false); }
            catch (Exception) { /* It may close before it answers. */ }
        }
        var exited = false;
        if (process is not null)
        {
            try
            {
                await process.Exited.WaitAsync(CloseBudget).ConfigureAwait(false);
                exited = true;
            }
            catch (Exception) { /* Ended below. */ }
        }
        if (cdp is not null) await cdp.DisposeAsync().ConfigureAwait(false);
        if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
        return asked && exited;
    }
}
