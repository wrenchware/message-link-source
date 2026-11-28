using System.Diagnostics;
using System.Text.Json;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.Core.Backend;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// Where the sign-in goes and what it may touch: the page it opens, which main-frame documents and other loads are allowed, which page ends it (and where its cookies are read) and which
/// page is Google's refusal. <see cref="Google"/> is the real one; the harness's smoke run against a real Chrome gives local pages, and nothing else ever does.
/// </summary>
internal sealed record SignInPages(string LoginUrl, Func<string?, bool> AllowMain, Func<string?, bool> AllowLoad, Func<string?, bool> IsCompletion, Func<string?, bool> IsRejected,
    ChromiumRenewalTarget Cookies)
{
    public static SignInPages Google { get; } = new(GoogleSignInRules.LoginUrl, url => GoogleSignInRules.IsLoginNavigation(url) && ChromiumRenewalTarget.GoogleMainFrame(url),
        ChromiumRenewalTarget.GoogleHost, GoogleSignInRules.IsCompletionUrl, ChromiumSignInDriver.IsRejectedPage, ChromiumRenewalTarget.Messages);
}

/// <summary>
/// The server's browser while a person signs in to Google Messages through it (chosen 2026-10-10): one page, on Google's real sign-in address, shown to the
/// client as a picture and driven by what the person does there. Google binds a sign-in to the browser that made it, so this is the browser the sign-in is made in, and the one whose
/// profile is kept to renew it. Nothing is injected into the page, no user agent is changed and nothing hides that the browser is driven: the page is the one Google serves, and only
/// DevTools's own screencast and input commands touch it.
/// <para>
/// The Windows sign-in window's rules (<c>GoogleSignInWindow</c>): the page's own main-frame documents must be HTTPS to a default port with no user name
/// (<see cref="GoogleSignInRules.IsLoginNavigation"/>), a new window is not opened (one to a login address is loaded in the page instead), downloads are refused, and the sign-in is
/// done when the page's load ends on <see cref="GoogleSignInRules.IsCompletionUrl"/>, which is when the cookies are read (<see cref="GoogleSignInRules.ReadCookie"/>, the same
/// required set). Stricter than the window, because this browser is not the sandboxed WebView2 and shows no address bar: every main-frame document must be on google.com (<see
/// cref="ChromiumRenewalTarget.GoogleMainFrame"/>) and every other load on one of Google's hosts (<see cref="ChromiumRenewalTarget.GoogleHost"/>), and what is refused is counted (<see cref="Blocked"/>) and never named.
/// </para>
/// <para>
/// Privacy. The person may be typing a Google password and the picture may show a code. Nothing the person types, no key, no picture and no address is logged or kept here; the log gets
/// fixed words and the counts below, and no exception message from the browser is passed on (the type only). The picture goes to the output as the browser gave it (base64 JPEG), is
/// never decoded or written, and the person's input is translated by <see cref="SignInInput"/> and forgotten.
/// </para>
/// </summary>
internal sealed class ChromiumSignInDriver(CdpConnection cdp, SignInPages? given = null)
{
    private readonly SignInPages pages = given ?? SignInPages.Google;

    /// <summary>The least time between two pictures: the browser sends the next one only when this one is acknowledged, so waiting to acknowledge is the frame rate.</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(66);
    /// <summary>How long Google's "can't sign you in" page stays in front of the person before the sign-in ends (<see cref="Rejected"/>).</summary>
    public TimeSpan RejectedGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Loads from outside Google's hosts that were refused, for the run's one log line.</summary>
    public int Blocked => Volatile.Read(ref blocked);
    /// <summary>Pictures sent on, for the run's one log line. A count; it says nothing of what was shown.</summary>
    public int Frames => Volatile.Read(ref frames);
    /// <summary>Google showed its "Couldn't sign you in" page (<see cref="GoogleSignInNotices"/> in the window).</summary>
    public bool Rejected => Volatile.Read(ref rejected) != 0;
    /// <summary>The sign-in finished: <c>__Secure-1PSIDRTS</c> and the timestamp cookie are among what Google set (fixed words, never values).</summary>
    public string CookieWords { get; private set; } = "";

    private int blocked, frames, rejected;
    private volatile string? session;
    private string? targetId;
    private int viewWidth = RemoteSignIn.DefaultWidth, viewHeight = RemoteSignIn.DefaultHeight;
    private readonly SemaphoreSlim screencast = new(1, 1);
    private long nextAckAt;
    private CancellationToken stop;

    /// <summary>
    /// Opens Google's sign-in and runs until it is done: the cookies of the finished sign-in (a <see cref="GoogleRefreshResult"/> with <c>Cookies</c>), or an exception. Pictures go to
    /// <paramref name="output"/> as they are made. Ends with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> does, with <see cref="IOException"/> when the browser
    /// goes away under the page, with <see cref="InvalidOperationException"/> <c>sign_in_rejected</c> after Google's refusal page, and with the backend's own failure when the sign-in ends on the
    /// completion page without the cookies.
    /// </summary>
    public async Task<GoogleRefreshResult> RunAsync(IRemoteSignInOutput output, int width, int height, CancellationToken cancellationToken)
    {
        stop = cancellationToken;
        var target = pages.Cookies;
        var picked = Page(await cdp.SendAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false))
            ?? (await cdp.SendAsync("Target.createTarget", new { url = "about:blank" }, cancellationToken: cancellationToken).ConfigureAwait(false)).GetProperty("targetId").GetString()!;
        targetId = picked;
        var page = (await cdp.SendAsync("Target.attachToTarget", new { targetId = picked, flatten = true }, cancellationToken: cancellationToken).ConfigureAwait(false))
            .GetProperty("sessionId").GetString()!;
        await cdp.SendAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Any other page the browser opens (a window the sign-in page asks for) is closed as it appears: it would be outside the checks below.
        await cdp.SendAsync("Target.setDiscoverTargets", new { discover = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await cdp.SendAsync("Page.enable", null, page, cancellationToken).ConfigureAwait(false);
        var mainFrame = (await cdp.SendAsync("Page.getFrameTree", null, page, cancellationToken).ConfigureAwait(false))
            .GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        await cdp.SendAsync("Fetch.enable", FetchPatterns, page, cancellationToken).ConfigureAwait(false);
        // Everything the browser starts is held at its first instruction until it has been given the same rules (AttachedAsync): top-level windows and workers (asked of the browser),
        // frames from other sites and the workers of the page (asked of the page).
        await cdp.SendAsync("Target.setAutoAttach", AutoAttach, cancellationToken: cancellationToken).ConfigureAwait(false);
        await cdp.SendAsync("Target.setAutoAttach", AutoAttach, page, cancellationToken).ConfigureAwait(false);
        var (pictureWidth, pictureHeight) = SignInInput.Size(width, height);
        session = page;
        await StartScreencastAsync(pictureWidth, pictureHeight, cancellationToken).ConfigureAwait(false);
        // Answered once the navigation commits or fails, which needs its document request let through below, so it is not awaited here.
        var navigate = cdp.SendAsync("Page.navigate", new { url = pages.LoginUrl }, page, cancellationToken);
        _ = navigate.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        string? mainUrl = null;
        var unreachable = false;
        long rejectedAt = 0;
        while (true)
        {
            // A refusal page that has been showing long enough ends the sign-in: the person has read it, and there is nothing further on it to do.
            if (Rejected && Stopwatch.GetElapsedTime(rejectedAt) >= RejectedGrace) throw new InvalidOperationException("sign_in_rejected");
            bool more;
            if (!Rejected) more = await cdp.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            else
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(RejectedGrace);
                try { more = await cdp.Events.WaitToReadAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; }
            }
            // The browser's end closed: it exited or crashed under the page.
            if (!more) throw new IOException("browser_exited");
            while (cdp.Events.TryRead(out var message))
            {
                if (message.Method == "Target.targetCreated")
                {
                    await CloseOtherPageAsync(message.Params, picked, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // A target the browser attached to us before it ran anything: a window the page opened, a frame from another site, a worker.
                if (message.Method == "Target.attachedToTarget")
                {
                    await AttachedAsync(message.Params, picked, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (message.Method == "Target.detachedFromTarget")
                {
                    if (message.Params.TryGetProperty("sessionId", out var gone) && gone.GetString() is { } goneSession) attached.Remove(goneSession);
                    continue;
                }
                if (message.SessionId is { } from && from != page && !attached.Contains(from)) continue;
                // Only the page itself speaks of pages and pictures; what an attached frame or worker sends is its requests, judged below.
                if (message.SessionId is { } sender && sender != page && message.Method != "Fetch.requestPaused") continue;
                switch (message.Method)
                {
                    case "Fetch.requestPaused":
                        await JudgeRequestAsync(message.Params, message.SessionId ?? page, mainFrame, picked, cancellationToken).ConfigureAwait(false);
                        break;
                    case "Page.screencastFrame":
                        Forward(message.Params, page, output);
                        break;
                    case "Page.frameNavigated":
                        var navigated = message.Params.GetProperty("frame");
                        if (navigated.TryGetProperty("parentId", out _)) break;
                        var address = navigated.TryGetProperty("url", out var given) ? given.GetString() : null;
                        if (address is null or "about:blank") break;
                        mainUrl = address;
                        unreachable = navigated.TryGetProperty("unreachableUrl", out _) || ChromiumGoogleBrowser.ErrorPage(address);
                        if (pages.IsRejected(address) && Interlocked.Exchange(ref rejected, 1) == 0) rejectedAt = Stopwatch.GetTimestamp();
                        break;
                    case "Page.navigatedWithinDocument":
                        // Google's sign-in moves between its steps inside one page as well, which starts no navigation (the window watches the source for the same reason): only the
                        // refusal page is looked for here, never the end, which is a load.
                        if (message.Params.TryGetProperty("frameId", out var inFrame) && inFrame.GetString() == mainFrame && message.Params.TryGetProperty("url", out var inUrl)
                            && inUrl.GetString() is { } inAddress)
                        {
                            mainUrl = inAddress;
                            if (pages.IsRejected(inAddress) && Interlocked.Exchange(ref rejected, 1) == 0) rejectedAt = Stopwatch.GetTimestamp();
                        }
                        break;
                    case "Page.windowOpen":
                        // As the window does: a new window for a login address is the same page; anything else is not opened at all (its target is closed above).
                        if (message.Params.TryGetProperty("url", out var opened) && opened.GetString() is { } openedUrl && pages.AllowMain(openedUrl))
                            _ = ObserveAsync(cdp.SendAsync("Page.navigate", new { url = openedUrl }, page, cancellationToken));
                        break;
                    case "Page.loadEventFired":
                        if (mainUrl is null || unreachable || !pages.IsCompletion(mainUrl)) break;
                        var read = await ChromiumGoogleBrowser.ReadCookiesAsync(target, cdp, page, cancellationToken).ConfigureAwait(false);
                        if (read.Cookies is null)
                            throw new BackendException(BackendFailure.AuthenticationRequired, "Google sign-in didn't finish for Google Messages. Please try again.");
                        CookieWords = await CookieWordsAsync(page, cancellationToken).ConfigureAwait(false);
                        try { await cdp.SendAsync("Page.stopScreencast", null, page, cancellationToken).ConfigureAwait(false); }
                        catch (Exception failure) when (failure is CdpException or IOException) { }
                        return read;
                    case "Inspector.targetCrashed":
                    case "Inspector.detached":
                        throw new IOException("browser_crashed");
                }
            }
        }
    }

    // Every request is held until judged, as the renewal does: a main-frame document by the window's rule and Google's hosts, anything else by Google's hosts alone.
    private async Task JudgeRequestAsync(JsonElement request, string page, string mainFrame, string target, CancellationToken cancellationToken)
    {
        var requestId = request.GetProperty("requestId").GetString();
        var url = request.GetProperty("request").GetProperty("url").GetString();
        var frame = request.TryGetProperty("frameId", out var frameId) ? frameId.GetString() : null;
        var document = request.TryGetProperty("resourceType", out var kind) && kind.GetString() == "Document";
        var main = document && (frame == mainFrame || frame == target);
        // The browser's own error page for an address it could not reach is let through (it is not a page Google sent it to).
        var allowed = main ? ChromiumGoogleBrowser.ErrorPage(url) || pages.AllowMain(url)
            : pages.AllowLoad(url);
        try
        {
            if (allowed) await cdp.SendAsync("Fetch.continueRequest", new { requestId }, page, cancellationToken).ConfigureAwait(false);
            else
            {
                Interlocked.Increment(ref blocked);
                await cdp.SendAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, page, cancellationToken).ConfigureAwait(false);
            }
        }
        // A request that ended while it was judged (the page moved on) is nothing to report.
        catch (CdpException) { }
    }

    private static readonly object AutoAttach = new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true };
    private static readonly object FetchPatterns = new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } };

    // The sessions of the targets attached so far (besides the page's): touched only by the one loop that reads the browser's events.
    private readonly HashSet<string> attached = new(StringComparer.Ordinal);

    // A page the page opened is closed before it has run anything; the browser's own page attached a second time is let go; a frame or worker gets the same request rules (and its own
    // auto-attach, for what it starts) before it is let run. Nothing else is done to any of them, and nothing is injected.
    private async Task AttachedAsync(JsonElement attachedTo, string ours, CancellationToken cancellationToken)
    {
        if (!attachedTo.TryGetProperty("sessionId", out var sessionElement) || sessionElement.GetString() is not { } child || !attachedTo.TryGetProperty("targetInfo", out var info)) return;
        var type = info.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        var id = info.TryGetProperty("targetId", out var target) ? target.GetString() : null;
        var waiting = attachedTo.TryGetProperty("waitingForDebugger", out var wait) && wait.ValueKind == JsonValueKind.True;
        try
        {
            // The session we attached with ourselves is reported too; it is the page's and stays.
            if (child == session) return;
            if (id == ours)
            {
                await cdp.SendAsync("Target.detachFromTarget", new { sessionId = child }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            if (type == "page")
            {
                if (id is not null) await cdp.SendAsync("Target.closeTarget", new { targetId = id }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            // The browser's own parts (its interface, and the built-in extensions of a desktop Chrome) are not the page's content: let go, which also lets them run.
            if (type is not ("iframe" or "worker" or "shared_worker" or "service_worker" or "worklet"))
            {
                await cdp.SendAsync("Target.detachFromTarget", new { sessionId = child }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            attached.Add(child);
            await cdp.SendAsync("Fetch.enable", FetchPatterns, child, cancellationToken).ConfigureAwait(false);
            await cdp.SendAsync("Target.setAutoAttach", AutoAttach, child, cancellationToken).ConfigureAwait(false);
            if (waiting) await cdp.SendAsync("Runtime.runIfWaitingForDebugger", null, child, cancellationToken).ConfigureAwait(false);
        }
        // A target that ended while it was being set up is nothing to report.
        catch (CdpException) { }
    }

    private async Task CloseOtherPageAsync(JsonElement created, string ours, CancellationToken cancellationToken)
    {
        if (!created.TryGetProperty("targetInfo", out var info) || info.GetProperty("type").GetString() != "page") return;
        var id = info.GetProperty("targetId").GetString();
        if (id is null || id == ours) return;
        try { await cdp.SendAsync("Target.closeTarget", new { targetId = id }, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (CdpException) { }
    }

    // The picture goes on as it came; it is acknowledged later, which paces the browser (see FrameInterval). A picture too large for the protocol is dropped, still acknowledged.
    private void Forward(JsonElement frame, string page, IRemoteSignInOutput output)
    {
        if (!frame.TryGetProperty("sessionId", out var ackId) || !ackId.TryGetInt32(out var ack)) return;
        var now = Stopwatch.GetTimestamp();
        var wait = TimeSpan.Zero;
        var due = Volatile.Read(ref nextAckAt);
        if (due > now) wait = Stopwatch.GetElapsedTime(now, due);
        Volatile.Write(ref nextAckAt, Math.Max(due, now) + (long)(FrameInterval.TotalSeconds * Stopwatch.Frequency));
        _ = AcknowledgeAsync(ack, page, wait);
        if (!frame.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return;
        var jpeg = data.GetString();
        if (jpeg is null || jpeg.Length == 0 || jpeg.Length > RemoteSignIn.MaxFrameCharacters) return;
        if (frame.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.TryGetProperty("deviceWidth", out var w) && w.TryGetDouble(out var viewW) && viewW >= 1 && viewW <= 8192) Volatile.Write(ref viewWidth, (int)Math.Round(viewW));
            if (metadata.TryGetProperty("deviceHeight", out var h) && h.TryGetDouble(out var viewH) && viewH >= 1 && viewH <= 8192) Volatile.Write(ref viewHeight, (int)Math.Round(viewH));
        }
        Interlocked.Increment(ref frames);
        output.Frame(new RemoteSignInFrame(jpeg, Volatile.Read(ref viewWidth), Volatile.Read(ref viewHeight)));
    }

    private async Task AcknowledgeAsync(int ack, string page, TimeSpan wait)
    {
        try
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stop).ConfigureAwait(false);
            await cdp.SendAsync("Page.screencastFrameAck", new { sessionId = ack }, page, stop).ConfigureAwait(false);
        }
        // The run ended or the browser went; nothing waits for an acknowledgement.
        catch (Exception) { }
    }

    private static Task ObserveAsync(Task task) => task.ContinueWith(failed => failed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task StartScreencastAsync(int width, int height, CancellationToken cancellationToken)
    {
        await screencast.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await cdp.SendAsync("Page.startScreencast", new { format = "jpeg", quality = 70, maxWidth = width, maxHeight = height, everyNthFrame = 1 }, session, cancellationToken).ConfigureAwait(false);
        }
        finally { screencast.Release(); }
    }

    /// <summary>
    /// What the person did, in order (<see cref="SignInInput"/>). Ignored before the page is attached. An event the browser refuses is dropped without a word; a browser that has gone ends
    /// the run itself.
    /// </summary>
    public async Task InputAsync(IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken)
    {
        if (session is not { } page) return;
        foreach (var item in events)
        {
            try
            {
                if (item.Kind == RemoteSignIn.Resize)
                {
                    var (width, height) = SignInInput.Size(item.Width, item.Height);
                    await screencast.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await cdp.SendAsync("Page.stopScreencast", null, page, cancellationToken).ConfigureAwait(false);
                        await cdp.SendAsync("Page.startScreencast", new { format = "jpeg", quality = 70, maxWidth = width, maxHeight = height, everyNthFrame = 1 }, page, cancellationToken).ConfigureAwait(false);
                    }
                    finally { screencast.Release(); }
                    continue;
                }
                if (SignInInput.Translate(item, Volatile.Read(ref viewWidth), Volatile.Read(ref viewHeight)) is not { } command) continue;
                await cdp.SendAsync(command.Method, command.Parameters, page, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException) { }
            catch (IOException) { return; }
        }
    }

    // Whether Google set the cookies that mark a bound or a renewable sign-in, as fixed words: names are compared in memory and no value is read into anything kept.
    private async Task<string> CookieWordsAsync(string page, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await cdp.SendAsync("Network.getAllCookies", null, page, cancellationToken).ConfigureAwait(false);
            bool timestamp = false, restricted = false;
            foreach (var cookie in answer.GetProperty("cookies").EnumerateArray())
            {
                var name = cookie.GetProperty("name").GetString();
                if (name == "__Secure-1PSIDTS") timestamp = true;
                else if (name == "__Secure-1PSIDRTS") restricted = true;
            }
            return $"sidts_set={(timestamp ? "yes" : "no")} rts_set={(restricted ? "yes" : "no")}";
        }
        catch (Exception failure) when (failure is CdpException or IOException) { return "sidts_set=unknown rts_set=unknown"; }
    }

    // As GoogleSignInNotices.IsRejectedPage (the window's own, in the Windows app): Google's "Couldn't sign you in" page.
    internal static bool IsRejectedPage(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/v3/signin/rejected", StringComparison.OrdinalIgnoreCase) || path.Equals("/signin/v2/deniedsigninrejected", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Page(JsonElement targets) => ChromiumGoogleBrowser.Page(targets);
}
