using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// A Google Messages sign-in made BY HAND in the server's own Chrome and shown on the server's page (SWITCHBOARD.md 5.4; chosen 2026-10-10: run official Chrome persistently, sign in by hand,
/// never automate it, and let it renew Google's sign-in itself as the PC's WebView2 profile does). Nothing is automated while a Google page is loaded and nothing is attached to the browser: the picture is
/// the X display it sits on (<see cref="ChromeScreenStream"/>), and what the person does goes in as real X input (<see cref="XInputTranslator"/>, <see cref="XInputRunner"/>). The server's part is
/// to read Chrome's cookie store from disk (<see cref="ChromeCookieStore"/>), which fills in as Chrome writes it, and when a NEW complete set of Google's account cookies is there, to hand it to the
/// pairing exactly as the sign-in of a client's own window does (the emoji reach the client as progress; a renewal stays a renewal).
/// <para>
/// The kept profile is never changed before the phone has accepted. The person signs in on a COPY of it (made while Chrome is closed, without its caches), or on a new profile when none is kept. The copy
/// replaces the kept profile only after the pairing succeeded (<see cref="GoogleMessagesBrowserProfile.KeepAsync"/>, with Chrome closed meanwhile); a cancel, a timeout, a refusal or a failure deletes the copy and
/// starts Chrome again on the kept profile, which is exactly as it was.
/// </para>
/// <para>
/// A sign-in is taken only when it is new. A set of account cookies that was already complete when the window opened is never taken, however long the window stays open or whatever the person is typing: it
/// is taken when the cookies that name the session (<see cref="ChromeRenewalLoop.SessionCookies"/>; not the timestamp cookies, which Google rotates by itself) are different from what was there. That holds for a
/// renewal and for a pairing as a new device alike. A person who finds Chrome already signed in therefore signs out and in again in the window. Chrome writes its cookie store every 30 seconds, so the
/// window can stay up that long after the sign-in.
/// </para>
/// <para>
/// One at a time (<see cref="SignInGuard"/>, and the one use of Chrome's display). Ended by the caller's cancel, 10 minutes without input, 20 minutes in all, or the display going away. Log lines are fixed words
/// and counts: never a count of what was typed (its length would be a password's), never a picture, an address or a cookie. Input is routed to the connection that started the sign-in and no other.
/// </para>
/// <para>
/// Google Voice (<see cref="ForVoice"/>, protocol 15) uses the same window, display and input, and the same one-at-a-time, for a sign-in on a NEW Chrome profile of that Voice account's own: never the Messages
/// profile, whose re-sign-in swaps and deletes it, even when it is the same Google account (the helper takes cookies only, for one Google account, so one account per profile). When the person is signed in
/// to Voice the account is added with the cookies and the profile; on any failure the new profile is deleted. Chrome then returns to the Messages profile, if there is one.
/// </para>
/// </summary>
internal sealed class ChromeRemoteSignIn(PersistentChrome chrome, IChromeCookieSource cookies, IFrameEncoder encoder, IGoogleMessagesAccount account, Action<string> log, SignInGuard? guard = null,
    ChromeBuild? build = null, Func<DateTimeOffset>? clock = null) : IRemoteSignInHost
{
    internal GoogleAccountBrowserProfile Profile { get; init; } = GoogleMessagesBrowserProfile.Default;
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TotalLimit { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan GateWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollGap { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan KeyGap { get; init; } = TimeSpan.FromMilliseconds(4);
    public TimeSpan RemapGap { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <inheritdoc/>
    public bool BrowserOutOfDate => build?.OutOfDate((clock ?? (() => DateTimeOffset.UtcNow))()) == true;

    private readonly object gate = new();
    private readonly SignInGuard signInGuard = guard ?? new SignInGuard();
    private Session? current;

    private sealed class Session(object owner)
    {
        public object Owner { get; } = owner;
        public volatile IXDisplay? Display;
        public volatile XInputRunner? Runner;
        public readonly SemaphoreSlim InputLock = new(1, 1);
        // Cancelled when the display is about to be let go, so input in flight stops where it is.
        public readonly CancellationTokenSource Aborted = new();
        // The person said they are finished (protocol 27's Done): what a Messenger or Instagram sign-in waits for. A Google one ends by itself and never looks.
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action Touch = () => { };
        public int MouseSeen, MouseApplied;
    }

    /// <summary>The limits, the outcome and what was found of one run of a sign-in, Messages' or Voice's.</summary>
    private sealed class Run : IDisposable
    {
        private int timedOut;
        private readonly Timer total, idle;

        public Run(ChromeRemoteSignIn owner, Session session, CancellationToken cancellationToken)
        {
            Limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            total = new Timer(_ => TimeOut(), null, owner.TotalLimit, Timeout.InfiniteTimeSpan);
            idle = new Timer(_ => TimeOut(), null, owner.IdleLimit, Timeout.InfiniteTimeSpan);
            session.Touch = () => { try { idle.Change(owner.IdleLimit, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } };
        }

        public CancellationTokenSource Limit { get; }
        public Stopwatch Watch { get; } = Stopwatch.StartNew();
        public string Outcome = "failed";
        public ChromeScreenStream? Screen;
        public Dictionary<string, string>? Cookies;
        public string Lifetime = "";
        public bool TimedOut => Volatile.Read(ref timedOut) != 0;

        private void TimeOut()
        {
            Interlocked.Exchange(ref timedOut, 1);
            try { Limit.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            idle.Dispose();
            total.Dispose();
            Limit.Dispose();
        }
    }

    public Task SignInAsync(RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken) =>
        EnterAsync(request, "google remote_signin busy", session => RunAsync(session, request, output, pairingEmoji, cancellationToken));

    /// <summary>
    /// The same window and display for a Google Voice account (protocol 15, <c>accounts.voice.remoteSignIn</c>): its sign-ins and its input go through this one object, so that a Messages sign-in and a Voice one can
    /// never run at once and a person's input reaches whichever is under way.
    /// </summary>
    public IRemoteSignInHost ForVoice(IGoogleVoiceAccounts voice) => new VoiceHost(this, voice);

    private sealed class VoiceHost(ChromeRemoteSignIn owner, IGoogleVoiceAccounts voice) : IRemoteSignInHost
    {
        public Task SignInAsync(RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken) =>
            owner.EnterAsync(request, "google voice remote_signin busy", session => owner.RunVoiceAsync(session, voice, output, cancellationToken));
        public Task InputAsync(object inputOwner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken) => owner.InputAsync(inputOwner, events, cancellationToken);
        public bool BrowserOutOfDate => owner.BrowserOutOfDate;
    }

    /// <summary>
    /// The same window, display, input and one-at-a-time for Messenger and Instagram (protocol 27, SWITCHBOARD.md 10.5), each on a throwaway Chrome profile under <paramref name="profilesRoot"/>, like the PC's
    /// InPrivate window. See <see cref="RunMetaAsync"/>.
    /// </summary>
    public IRemoteCookieSignInHost ForMeta(string profilesRoot) => new MetaHost(this, profilesRoot);

    private sealed class MetaHost(ChromeRemoteSignIn owner, string profilesRoot) : IRemoteCookieSignInHost
    {
        public bool Offers(string service) => MetaSignInTarget.For(service) is not null;

        public async Task<Dictionary<string, string>> SignInAsync(string service, RemoteSignInRequest request, IRemoteSignInOutput output, CancellationToken cancellationToken)
        {
            var target = MetaSignInTarget.For(service) ?? throw new NotSupportedException();
            Dictionary<string, string>? cookies = null;
            await owner.EnterAsync(request, RemoteSignInFinish.Line(service, "refused", "busy"),
                async session => cookies = await owner.RunMetaAsync(session, target, profilesRoot, output, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            return cookies!;
        }

        public Task InputAsync(object inputOwner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken) => owner.InputAsync(inputOwner, events, cancellationToken);
    }

    /// <summary>
    /// A Messenger or Instagram sign-in in the server's Chrome (SWITCHBOARD.md 10.5): Chrome (closed first, off the Messages profile) is started on a NEW throwaway profile at the service's login page; the
    /// person signs in by hand and chooses Done; Chrome is then closed gently, which is when it writes its cookie file whole (it writes late otherwise); the cookies the PC's own window would have kept are
    /// read from that file (<see cref="MetaSignInTarget"/>, the same rules), and refused with <c>missing_cookies</c> unless the required ones are all there. Whatever happens, the profile is deleted and
    /// Chrome goes back to the Messages profile, as after a Voice sign-in. Nothing is retried: a refusal or a failure ends the sign-in, and the person starts it again.
    /// <para>
    /// Nothing is ever put into the page and nothing about the browser is disguised: the profile is new and empty (its preferences turn password saving off and keep session cookies on disk, as a
    /// browser that restores its last session does), and Instagram's inbox is kept out by the browser's own policy file, not by anything run in the page.
    /// </para>
    /// </summary>
    private async Task<Dictionary<string, string>> RunMetaAsync(Session session, MetaSignInTarget target, string profilesRoot, IRemoteSignInOutput output, CancellationToken cancellationToken)
    {
        var service = target.Service;
        using var run = new Run(this, session, cancellationToken);
        log(RemoteSignInFinish.Line(service, "started", "none"));
        IDisposable hold;
        try { hold = await chrome.HoldAsync(GateWait, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { log(RemoteSignInFinish.Line(service, "refused", "busy")); throw; }
        using var _hold = hold;

        var profile = ThrowawayChromeProfile.Create(profilesRoot);
        var browserReason = "browser";
        try
        {
            ThrowawayChromeProfile.PreparePreferences(profile.Path);
            // A new profile, nothing copied: Chrome is closed first, so the display and the profile folder are the new ones alone.
            await InteractAsync(session, run, output, cancellationToken, profile.Path, target.LoginUrl, token => session.Done.Task.WaitAsync(token), () => chrome.StopAsync(),
                failure => browserReason = "browser_" + failure.GetType().Name).ConfigureAwait(false);

            // The person chose Done. Chrome closed gently, so the cookie file on disk holds everything it had; only then is it read.
            await chrome.StopAsync().ConfigureAwait(false);
            var read = await cookies.ReadAsync(profile.Path, target.Cookies, CancellationToken.None).ConfigureAwait(false);
            var found = read?.Cookies ?? new Dictionary<string, string>(StringComparer.Ordinal);
            if (!target.Required.All(found.ContainsKey))
            {
                found.Clear();
                run.Outcome = "missing";
                throw new InvalidOperationException("missing_cookies");
            }
            run.Outcome = "ok";
            log(RemoteSignInFinish.Line(service, "signed_in", "none"));
            // The browser is finished and closed; the window goes, and the server connects the account.
            output.SignedIn();
            return found;
        }
        finally
        {
            switch (run.Outcome)
            {
                case "cancelled": log(RemoteSignInFinish.Line(service, "cancelled", "client")); break;
                case "timeout": log(RemoteSignInFinish.Line(service, "timeout", "limit")); break;
                case "missing": log(RemoteSignInFinish.Line(service, "refused", "missing_cookies")); break;
                case "ok": break;
                default: log(RemoteSignInFinish.Line(service, "failed", browserReason)); break;
            }
            // The Chrome that wrote the profile goes first, then the profile with everything it held. Then Chrome is where it was: on the Messages profile, if one is kept.
            await chrome.StopAsync().ConfigureAwait(false);
            await profile.DeleteAsync(log).ConfigureAwait(false);
            await MessagesChrome.ResumeAsync(chrome, log).ConfigureAwait(false);
        }
    }

    private async Task EnterAsync(RemoteSignInRequest request, string busyLine, Func<Session, Task> run)
    {
        var session = new Session(request.Owner);
        using var lease = signInGuard.TryEnter();
        lock (gate)
        {
            if (lease is null || current is not null) { log(busyLine); throw new InvalidOperationException("sign_in_busy"); }
            current = session;
        }
        try { await run(session).ConfigureAwait(false); }
        finally { lock (gate) current = null; }
    }

    public async Task InputAsync(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken)
    {
        Session? session;
        lock (gate) session = current;
        // Another connection's sign-in, or none: refused with a word, and nothing in the events is looked at.
        if (session is null || !ReferenceEquals(session.Owner, owner)) throw new InvalidOperationException("no_sign_in");
        session.Touch();
        // Done comes after whatever the person did before it in the same batch, which is applied first.
        var done = events.Any(item => item.Kind == RemoteSignIn.DoneKind);
        try { await ApplyAsync(session, events, cancellationToken).ConfigureAwait(false); }
        finally { if (done) session.Done.TrySetResult(); }
    }

    private async Task ApplyAsync(Session session, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken)
    {
        if (session.Display is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Aborted.Token);
        try { await session.InputLock.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (session.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return; }
        try
        {
            // Read again under the lock: the display may have been let go while this waited for its turn.
            if (session.Display is not { } display || session.Runner is not { } runner) return;
            foreach (var item in events)
            {
                if (item.Kind is RemoteSignIn.Resize or RemoteSignIn.DoneKind) continue;
                var actions = XInputTranslator.Translate(item, display.Width, display.Height);
                if (item.Kind == RemoteSignIn.Mouse)
                {
                    Interlocked.Increment(ref session.MouseSeen);
                    if (actions.Count > 0) Interlocked.Increment(ref session.MouseApplied);
                }
                if (actions.Count == 0) continue;
                try { await runner.RunAsync(actions, linked.Token).ConfigureAwait(false); }
                // The display went away under the person: the sign-in ends on its own (the capture finds it gone). The type is not worth saying.
                catch (IOException) { return; }
                catch (OperationCanceledException) when (session.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return; }
            }
        }
        finally { session.InputLock.Release(); }
    }

    private async Task RunAsync(Session session, RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken)
    {
        using var run = new Run(this, session, cancellationToken);

        log("google remote_signin started");
        IDisposable hold;
        try { hold = await chrome.HoldAsync(GateWait, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { log("google remote_signin busy"); throw; }
        using var _hold = hold;

        // The person signs in on a profile of its own: a copy of the kept one (so the sign-in they have is untouched until the phone accepts the new), or a new one when none is kept.
        GoogleSignInProfile? kept = null;
        if (Profile.Id is { } keptId && GoogleSignInProfile.TryOpen(keptId, out var found, GoogleSignInProfile.MessagesService)) kept = found;
        var working = GoogleSignInProfile.CreateAttempt(GoogleSignInProfile.MessagesService);
        var swapped = false;

        try
        {
            // On Google's account chooser, not the configuration page: that page alone offers no way to sign in.
            await InteractAsync(session, run, output, cancellationToken, working.Path, ChromeArguments.SignInPage, ChromiumRenewalTarget.Messages, _ => true,
                kept is null ? null : async () =>
                {
                    // Closed gently, so the copy has everything Chrome wrote.
                    await chrome.StopAsync().ConfigureAwait(false);
                    ChromeProfileCopy.Copy(kept.Path, working.Path);
                }, "google remote_signin").ConfigureAwait(false);

            // Out of the browser's hands: the person is done. The window goes, and the phone is asked. Chrome stays up on the copy until the phone has said.
            run.Outcome = "signed_in";
            output.SignedIn();
            await account.SignInAsync(run.Cookies!, pairingEmoji, cancellationToken, request.AsNewDevice).ConfigureAwait(false);

            // Accepted. Chrome is closed, so the profile it wrote is whole and nothing runs on a folder that is about to be replaced; the working profile becomes the kept one (the old one is deleted
            // after the pointer is moved); Chrome starts again on it. If the pointer cannot be written, the kept profile stands and Chrome returns to it below.
            await chrome.StopAsync().ConfigureAwait(false);
            await using (var keep = new GoogleSignInResult(new Dictionary<string, string>(run.Cookies!, StringComparer.Ordinal), working))
                await Profile.KeepAsync(keep).ConfigureAwait(false);
            swapped = GoogleMessagesBrowserProfile.Id == working.Id;
            if (swapped) await chrome.EnsureAsync(working.Path, CancellationToken.None).ConfigureAwait(false);
            run.Outcome = "ok";
            log($"google remote_signin completed chrome=yes kept={(swapped ? "yes" : "no")} sidts={run.Lifetime} shown={(run.Screen?.Frames > 0 ? "yes" : "no")} frames={run.Screen?.Frames ?? 0} mouse={session.MouseSeen} applied={session.MouseApplied} ms={run.Watch.ElapsedMilliseconds}");
        }
        finally
        {
            if (run.Outcome != "ok")
            {
                var reason = run.Outcome switch
                {
                    "cancelled" => "cancelled reason=client",
                    "timeout" => "timeout",
                    "signed_in" => "failed reason=pairing",
                    _ => null,
                };
                if (reason is not null) log($"google remote_signin {reason} shown={(run.Screen?.Frames > 0 ? "yes" : "no")} mouse={session.MouseSeen} applied={session.MouseApplied}");
            }
            run.Cookies?.Clear();
            if (!swapped)
            {
                // Not accepted (or not kept): the working profile goes with the Chrome that wrote it, and the kept one, which was never touched, is what Chrome runs on again.
                await chrome.StopAsync().ConfigureAwait(false);
                await working.DeleteAsync().ConfigureAwait(false);
                if (kept is not null && GoogleSignInProfile.TryOpen(kept.Id, out _, GoogleSignInProfile.MessagesService))
                {
                    try { await chrome.EnsureAsync(kept.Path, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception failure) { log($"google chrome start=failed type={failure.GetType().Name}"); }
                }
            }
        }
    }

    /// <summary>
    /// A Google Voice sign-in (protocol 15): Chrome started on a NEW profile of this account's own at Voice's sign-up page; the person signs in by hand; when the cookie store holds a new complete Voice jar
    /// with Voice's own <c>OSID</c> (set by Voice's hand-over, so Voice has loaded), the account is added with those cookies and that profile, which the account then keeps for its renewals. Whatever happens,
    /// Chrome ends up closed on the new profile and back on the Messages one (if there is one), and a profile the account did not keep is deleted. Failures are fixed code words: the sign-in's own, or
    /// the add's (<c>auth_required</c>, <c>no_voice_number</c>, a helper's start failure).
    /// </summary>
    private async Task RunVoiceAsync(Session session, IGoogleVoiceAccounts voice, IRemoteSignInOutput output, CancellationToken cancellationToken)
    {
        const string Prefix = "google voice remote_signin";
        using var run = new Run(this, session, cancellationToken);

        log($"{Prefix} started");
        IDisposable hold;
        try { hold = await chrome.HoldAsync(GateWait, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { log($"{Prefix} busy"); throw; }
        using var _hold = hold;

        var working = GoogleSignInProfile.CreateAttempt(GoogleSignInProfile.VoiceService);
        var added = false;
        var addReason = "unknown";
        try
        {
            // A new profile, nothing copied: Chrome is closed first, so the display and the profile folder are the new ones alone.
            try
            {
                await InteractAsync(session, run, output, cancellationToken, working.Path, GoogleVoiceSignInRules.LoginUrl, ChromiumRenewalTarget.Voice,
                    read => read.Cookies.ContainsKey("OSID"), () => chrome.StopAsync(), Prefix, ChromeVisitedPages.GoogleVerification).ConfigureAwait(false);
            }
            catch (InvalidOperationException failure) when (failure.Message == ChromeVisitedPages.GoogleVerificationWord) { run.Outcome = "verification"; throw; }

            run.Outcome = "signed_in";
            output.SignedIn();
            // Chrome closed gently, so the profile on disk is whole before the account keeps it.
            await chrome.StopAsync().ConfigureAwait(false);
            try
            {
                // The add clears the dictionary it is given.
                await voice.AddAsync(new Dictionary<string, string>(run.Cookies!, StringComparer.Ordinal), working.Id, cancellationToken).ConfigureAwait(false);
                added = true;
            }
            catch (OperationCanceledException) { run.Outcome = "cancelled"; throw; }
            catch (Exception failure) { addReason = GoogleVoiceLog.Reason(failure); throw; }
            run.Outcome = "ok";
            log($"{Prefix} completed kept=yes shown={(run.Screen?.Frames > 0 ? "yes" : "no")} frames={run.Screen?.Frames ?? 0} mouse={session.MouseSeen} applied={session.MouseApplied} ms={run.Watch.ElapsedMilliseconds}");
        }
        finally
        {
            if (run.Outcome != "ok")
            {
                var reason = run.Outcome switch
                {
                    "cancelled" => "cancelled reason=client",
                    "timeout" => "timeout",
                    "signed_in" => $"failed reason=add code={addReason}",
                    "verification" => $"refused reason={ChromeVisitedPages.GoogleVerificationWord}",
                    _ => null,
                };
                if (reason is not null) log($"{Prefix} {reason} shown={(run.Screen?.Frames > 0 ? "yes" : "no")} mouse={session.MouseSeen} applied={session.MouseApplied}");
            }
            run.Cookies?.Clear();
            // The Chrome that wrote the profile goes first; a profile the account did not keep goes with it. Then Chrome is where it was: on the Messages profile.
            await chrome.StopAsync().ConfigureAwait(false);
            if (!added) await working.DeleteAsync().ConfigureAwait(false);
            await MessagesChrome.ResumeAsync(chrome, log).ConfigureAwait(false);
        }
    }

    // Google's part: until the cookie store holds a NEW complete sign-in (and `ready` says it is the one wanted), which it leaves in run.Cookies. When `deadEnd` is given and Chrome has opened a
    // page starting with it (one the policy refuses, so nobody can get past it here), the sign-in ends with that page's word instead.
    private Task InteractAsync(Session session, Run run, IRemoteSignInOutput output, CancellationToken cancellationToken, string workingPath, string page, ChromiumRenewalTarget target,
        Func<ChromeCookieRead, bool> ready, Func<Task>? prepare, string logPrefix, string? deadEnd = null) =>
        InteractAsync(session, run, output, cancellationToken, workingPath, page, async token =>
        {
            var finished = await WaitForSignInAsync(workingPath, target, ready, deadEnd, token).ConfigureAwait(false);
            run.Lifetime = finished.SidtsLifetime;
            run.Cookies = new Dictionary<string, string>(finished.Cookies, StringComparer.Ordinal);
            finished.Cookies.Clear();
        }, prepare, failure => log($"{logPrefix} failed type={failure.GetType().Name}"));

    // The person's part, the same for every service: Chrome on a profile at a page, its display shown and driven, until `wait` ends (Google's: a new sign-in in the cookie store; Messenger's and
    // Instagram's: the person's Done). What happened to the run is left in run.Outcome (timeout, cancelled); the browser's display and input are let go before this returns, however it ends.
    private async Task InteractAsync(Session session, Run run, IRemoteSignInOutput output, CancellationToken cancellationToken, string workingPath, string page,
        Func<CancellationToken, Task> wait, Func<Task>? prepare, Action<Exception> browserFailed)
    {
        var limit = run.Limit;
        Task? screenTask = null;
        CancellationTokenSource? screenStop = null;
        IXDisplay? capture = null, input = null;
        try
        {
            if (prepare is not null) await prepare().ConfigureAwait(false);
            if (!await chrome.EnsureAsync(workingPath, limit.Token, page).ConfigureAwait(false)) throw new IOException("browser_slow");
            capture = chrome.Open();
            input = chrome.Open();
            if (capture is null || input is null) throw new IOException("browser_slow");
            // There is no window manager to give the browser the keyboard.
            input.FocusBrowser();
            session.Runner = new XInputRunner(input) { KeyGap = KeyGap, RemapGap = RemapGap };
            session.Display = input;
            run.Screen = new ChromeScreenStream(capture, encoder, output) { Interval = FrameInterval };
            screenStop = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            var screen = run.Screen;
            screenTask = Task.Run(() => screen.RunAsync(screenStop.Token));
            using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            var waiting = wait(waitStop.Token);
            var first = await Task.WhenAny(waiting, screenTask).ConfigureAwait(false);
            if (first != waiting && !limit.IsCancellationRequested)
            {
                // The picture stopped because the display is gone (Chrome or Xvfb died under the person): nobody can sign in any more, and the window must not sit frozen.
                waitStop.Cancel();
                try { await waiting.ConfigureAwait(false); } catch (Exception) { }
                throw new IOException("x_unavailable");
            }
            await waiting.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.TimedOut && !cancellationToken.IsCancellationRequested)
        {
            run.Outcome = "timeout";
            throw new InvalidOperationException("sign_in_timeout");
        }
        catch (OperationCanceledException)
        {
            run.Outcome = "cancelled";
            throw;
        }
        // The browser did not come up, or its display went away. The type only.
        catch (Exception failure) when (failure is not InvalidOperationException)
        {
            browserFailed(failure);
            throw new InvalidOperationException("sign_in_browser");
        }
        finally
        {
            // No more input is taken; what is in flight is stopped and waited for (the display is not let go under it), the keyboard is put back as it was, and then the connections close.
            var runner = session.Runner;
            session.Display = null;
            session.Runner = null;
            session.Aborted.Cancel();
            screenStop?.Cancel();
            if (screenTask is not null) { try { await screenTask.ConfigureAwait(false); } catch (Exception) { } }
            screenStop?.Dispose();
            var locked = false;
            try { locked = await session.InputLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            try { if (locked) runner?.Release(); }
            finally
            {
                capture?.Dispose();
                input?.Dispose();
                if (locked) session.InputLock.Release();
            }
        }
    }

    // Polls Chrome's cookie store until a NEW complete Google sign-in is there: one whose session cookies are not those the store held when the window opened (or any, when it held none), and that `ready`
    // accepts (Voice's own cookie, which only a Voice page that loaded sets).
    private async Task<ChromeCookieRead> WaitForSignInAsync(string folder, ChromiumRenewalTarget target, Func<ChromeCookieRead, bool> ready, string? deadEnd, CancellationToken cancellationToken)
    {
        var first = await cookies.ReadAsync(folder, target, cancellationToken).ConfigureAwait(false);
        var started = first is { Complete: true } ? Signature(first) : null;
        while (true)
        {
            await Task.Delay(PollGap, cancellationToken).ConfigureAwait(false);
            if (deadEnd is not null && ChromeVisitedPages.Opened(folder, deadEnd)) throw new InvalidOperationException(ChromeVisitedPages.GoogleVerificationWord);
            var read = await cookies.ReadAsync(folder, target, cancellationToken).ConfigureAwait(false);
            if (read is not { Complete: true } || !ready(read)) continue;
            if (started is null || Signature(read) != started) return read;
        }
    }

    // What identifies the signed-in session, as a hash of the cookies that name it; compared in memory and dropped. The timestamp cookies are left out: Google rotates them by itself while a person is typing.
    private static string Signature(ChromeCookieRead read)
    {
        var text = string.Join("|", ChromeRenewalLoop.SessionCookies.Select(name => read.Cookies.TryGetValue(name, out var value) ? value : ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
