using System.Text.Json;
using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// A Google Messages sign-in made in the server's own browser and shown on the server's page (SWITCHBOARD.md 5.4; chosen 2026-10-10: Google binds a sign-in to the browser that
/// made it, so a sign-in made on the PC could be used here for two hours and renewed only by the PC). The server's headed Chromium, on a display of its own and a profile that is new for
/// the run, is the browser the person signs in with; the profile it signed in with becomes the kept renewal profile (<see cref="GoogleMessagesBrowserProfile.KeepAsync"/>), nothing seeded;
/// and the phone is paired exactly as for a sign-in handed up (<see cref="IGoogleMessagesAccount.SignInAsync(IReadOnlyDictionary{string, string}, IProgress{string}, CancellationToken, bool)"/>),
/// the emoji reaching the client as progress.
/// <para>
/// One at a time, and at most one browser (the renewal's gate, <see cref="ChromiumGoogleBrowser.HoldAsync"/>). Ended by the caller's cancel (the client's, or its connection closing), 10
/// minutes without input, or 20 minutes in all (<see cref="IdleLimit"/>, <see cref="TotalLimit"/>); whatever ends it, the browser and its display are killed and the profile deleted unless
/// the sign-in was accepted. The pairing that follows the browser is the caller's to cancel and has no limit of its own here (the account's own waits apply).
/// </para>
/// <para>
/// Log lines are fixed words and counts: <c>google remote_signin started</c>, <c>completed</c> (with <c>sidts=</c>, <c>rts=</c>, <c>saved=</c>, <c>frames=</c>, <c>blocked=</c>, <c>ms=</c>),
/// <c>rejected</c>, <c>cancelled reason=</c>, <c>timeout</c>, <c>failed type=</c>. Never a count of what was typed or clicked (its length would be a password's), never a picture, an address or a
/// cookie. Input is routed to the sign-in of the connection that started it and no other.
/// </para>
/// </summary>
internal sealed class RemoteGoogleSignIn(IChromiumSignInLauncher launcher, ChromiumGoogleBrowser browser, IGoogleMessagesAccount account, Action<string> log, SignInGuard? guard = null) : IRemoteSignInHost
{
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TotalLimit { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>How long the browser gets to close by itself, which is when its cookie store is written, before it is ended.</summary>
    public TimeSpan CloseBudget { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a sign-in waits for a renewal (or nothing else) to give up the browser before it says it is busy.</summary>
    public TimeSpan GateWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long a browser gets to come up (its DevTools port written and connected).</summary>
    public TimeSpan StartBudget { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(66);
    public TimeSpan RejectedGrace { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Where the sign-in goes: Google's real pages. Only the harness's checks against local pages give others.</summary>
    internal SignInPages Pages { get; init; } = SignInPages.Google;

    private readonly object gate = new();
    private readonly SignInGuard signInGuard = guard ?? new SignInGuard();
    private Session? current;

    private sealed class Session(object owner)
    {
        public object Owner { get; } = owner;
        public volatile ChromiumSignInDriver? Driver;
        public Action Touch = () => { };
    }

    public async Task SignInAsync(RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken)
    {
        var session = new Session(request.Owner);
        // One Google sign-in at a time on this server, remote or the legacy hand-over: held from the browser to the kept profile and the pairing it commits.
        using var lease = signInGuard.TryEnter();
        lock (gate)
        {
            if (lease is null || current is not null) { log("google remote_signin busy"); throw new InvalidOperationException("sign_in_busy"); }
            current = session;
        }
        try { await RunAsync(session, request, output, pairingEmoji, cancellationToken).ConfigureAwait(false); }
        finally { lock (gate) current = null; }
    }

    public Task InputAsync(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken)
    {
        Session? session;
        lock (gate) session = current;
        // Another connection's sign-in, or none: refused with a word, and nothing in the events is looked at.
        if (session is null || !ReferenceEquals(session.Owner, owner)) throw new InvalidOperationException("no_sign_in");
        session.Touch();
        return session.Driver is { } driver ? driver.InputAsync(events, cancellationToken) : Task.CompletedTask;
    }

    private async Task RunAsync(Session session, RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void TimeOut()
        {
            Interlocked.Exchange(ref timedOut, 1);
            try { limit.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        using var total = new Timer(_ => TimeOut(), null, TotalLimit, Timeout.InfiniteTimeSpan);
        using var idle = new Timer(_ => TimeOut(), null, IdleLimit, Timeout.InfiniteTimeSpan);
        session.Touch = () => { try { idle.Change(IdleLimit, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } };

        log("google remote_signin started");
        using var hold = await HoldBrowserAsync(cancellationToken).ConfigureAwait(false);
        var profile = GoogleSignInProfile.CreateAttempt(GoogleSignInProfile.MessagesService);
        GoogleSignInResult? result = null;
        IChromiumSignInProcess? process = null;
        CdpConnection? cdp = null;
        ChromiumSignInDriver? driver = null;
        string outcome = "failed";
        var saved = false;
        var scrubbed = false;
        try
        {
            GoogleRefreshResult signedIn;
            try
            {
                PreparePreferences(profile.Path);
                process = launcher.LaunchSignIn(profile.Path);
                using var starting = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                starting.CancelAfter(StartBudget);
                try { cdp = await process.ConnectAsync(starting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!limit.IsCancellationRequested) { throw new IOException("browser_slow"); }
                driver = new ChromiumSignInDriver(cdp, Pages) { FrameInterval = FrameInterval, RejectedGrace = RejectedGrace };
                session.Driver = driver;
                signedIn = await driver.RunAsync(output, request.Width, request.Height, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref timedOut) != 0 && !cancellationToken.IsCancellationRequested)
            {
                outcome = "timeout";
                throw new InvalidOperationException("sign_in_timeout");
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch (InvalidOperationException) when (driver?.Rejected == true)
            {
                outcome = "rejected";
                throw;
            }
            catch (BackendException)
            {
                outcome = "no_cookies";
                throw;
            }
            // The browser did not come up, went away under the page, or answered something unexpected. The type only: a browser's or the system's message can name the profile's path or the page.
            catch (Exception failure)
            {
                log($"google remote_signin failed type={failure.GetType().Name}");
                throw new InvalidOperationException("sign_in_browser");
            }
            finally
            {
                // The browser is finished with, one way or another, before the pairing or the delete: asked to close first so its cookie store is on disk, then ended with its display.
                saved = await ShutAsync(cdp, process).ConfigureAwait(false);
                session.Driver = null;
                // The display's key is for the run alone; it does not stay in a profile that is kept.
                XAuthority.Delete(Path.Combine(profile.Path, ChromiumLauncher.XAuthorityFile));
                // And what the browser kept of the page it showed (a saved session holds the text typed into a field): the profile keeps what a renewal loads, not that.
                scrubbed = ProfileScrub.Run(profile.Path);
            }

            // Before the phone is asked and before anything is kept: a profile the browser did not close by itself may not hold the sign-in (its cookie store may never have been written), and one
            // that still holds typed form state must not be kept. Either way the attempt is deleted below and the working profile and session are untouched, so the person can try again.
            if (!saved) { outcome = "unsaved"; throw new InvalidOperationException("sign_in_unsaved"); }
            if (!scrubbed) { outcome = "unscrubbed"; throw new InvalidOperationException("sign_in_profile"); }

            // Out of the browser's hands: the person is done. The window goes, and the phone is asked.
            outcome = "signed_in";
            output.SignedIn();
            result = new GoogleSignInResult(signedIn.Cookies!, profile);
            await account.SignInAsync(result.Cookies, pairingEmoji, cancellationToken, request.AsNewDevice).ConfigureAwait(false);
            // Accepted: this profile is what the keeper renews from; the one it replaces is deleted.
            await GoogleMessagesBrowserProfile.KeepAsync(result).ConfigureAwait(false);
            outcome = "ok";
            log($"google remote_signin completed {driver!.CookieWords} sidts={signedIn.SidtsLifetime} saved={(saved ? "yes" : "no")} shown={(driver.Frames > 0 ? "yes" : "no")} blocked={driver.Blocked} ms={clock.ElapsedMilliseconds}");
        }
        finally
        {
            if (outcome != "ok")
            {
                var reason = outcome switch
                {
                    "cancelled" => "cancelled reason=client",
                    "timeout" => "timeout",
                    "rejected" => "rejected",
                    "no_cookies" => "cancelled reason=missing_cookies",
                    "unsaved" => "failed reason=not_saved",
                    "unscrubbed" => "failed reason=not_scrubbed",
                    "signed_in" => "failed reason=pairing",
                    _ => null,
                };
                if (reason is not null) log("google remote_signin " + reason + (driver is null ? "" : $" shown={(driver.Frames > 0 ? "yes" : "no")} blocked={driver.Blocked}"));
            }
            // A sign-in that was not accepted leaves nothing: the result clears its cookies and deletes the profile; with no result yet, the profile goes by itself (the browser is gone).
            if (result is not null) await result.DisposeAsync().ConfigureAwait(false);
            else await profile.DeleteAsync().ConfigureAwait(false);
        }
    }

    // The one browser, shared with the renewals; busy (said once) when a renewal holds it past the wait.
    private async Task<IDisposable> HoldBrowserAsync(CancellationToken cancellationToken)
    {
        try { return await browser.HoldAsync(GateWait, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            log("google remote_signin busy");
            throw;
        }
    }

    // The sign-in window turns off password saving and general autofill (GoogleSignInWindow); this browser's profile is new for the run, so the same is written into it before it starts.
    // Nothing here changes what the browser says it is.
    internal static void PreparePreferences(string profileFolder)
    {
        var folder = Path.Combine(profileFolder, "Default");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(folder, "Preferences"), PreferencesJson);
    }

    internal const string PreferencesJson = "{\"credentials_enable_service\":false,\"credentials_enable_autosignin\":false,\"profile\":{\"password_manager_enabled\":false},"
        + "\"autofill\":{\"profile_enabled\":false,\"credit_card_enabled\":false}}";

    // Asked to close, then ended: true when it closed by itself within the budget, which is when its cookie store is on disk. Everything it started is ended before this returns.
    private async Task<bool> ShutAsync(CdpConnection? cdp, IChromiumSignInProcess? process)
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
        // Never asked to close (no connection came up, or it was already gone): nothing will end it by itself, so it is ended at once, not after the budget.
        if (!asked) process?.Kill();
        if (process is not null && asked)
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
