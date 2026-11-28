using Microsoft.Web.WebView2.Core;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.Core.Backend;
using System.Collections.Concurrent;
using Windows.Foundation;

namespace MessageLink.App;

// What one service's recovery loads and keeps: the single page it opens, the navigations it allows on the way, the page
// that means signed in, and the cookies it reads back. Voice's is the original; Google Messages' was added in 0.1.290.
internal sealed record GoogleRecoveryTarget(string Service, string Url, Func<string?, bool> AllowNavigation, Func<string?, bool> IsDone,
    string[] CookieUrls, Func<string, string, string, string?> ReadCookie, Func<IReadOnlyDictionary<string, string>, bool> IsComplete,
    string FailureMessage);

// A deliberately non-interactive recovery path for a profile created by a Google sign-in window (Voice's or Messages').
// The message-only HWND is documented by WebView2 as an invisible controller host: no XAML Window is created, activated,
// or shown. This does not replace interactive sign-in; redirects to account selection or sign-in are refused.
internal static class GoogleBrowserRecovery
{
    public static readonly GoogleRecoveryTarget Voice = new(GoogleSignInProfile.VoiceService, GoogleVoiceSignInRules.VoiceUrl,
        GoogleVoiceSignInRules.IsRecoveryNavigation, GoogleVoiceSignInRules.IsVoicePage,
        [GoogleVoiceSignInRules.VoiceUrl, GoogleVoiceSignInRules.ApiUrl], GoogleVoiceSignInRules.ReadCookie, GoogleVoiceSignInRules.IsComplete,
        "Google Voice couldn't refresh its sign-in. Try again later.");

    // Messages for web's configuration address, never the web app itself: loading that could start a pairing of its own
    // or take over as the active session. It is the address the sign-in window already ends on.
    public static readonly GoogleRecoveryTarget Messages = new(GoogleSignInProfile.MessagesService, GoogleSignInRules.RecoveryUrl,
        GoogleSignInRules.IsRecoveryNavigation, GoogleSignInRules.IsCompletionUrl, [GoogleSignInRules.RecoveryUrl],
        GoogleSignInRules.ReadCookie, cookies => GoogleSignInRules.RequiredCookies.All(cookies.ContainsKey),
        "Google Messages sign-in refresh could not start. Try again later.");

    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, Task> deferredCleanups = new(StringComparer.Ordinal);
    // The page's own budget, from the moment this refresh has the browser; waiting for the other service's refresh to let
    // go of it has its own. Both came out of the one 20 s, so a refresh queued behind Voice's at launch had less.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20), Queue = TimeSpan.FromSeconds(60);

    // The caller owns a non-null result and must clear it after it has handed the refreshed values to the protected
    // session store. A missing/expired profile, failed page load, or absent required cookies is simply not recoverable.
    // Browser/runtime failures use a fixed error so no address, account, or browser detail can escape to UI or logs.
    public static async Task<Dictionary<string, string>?> RefreshAsync(GoogleRecoveryTarget target, string profileId, CancellationToken cancellationToken)
        => (await RefreshWithReasonAsync(target, profileId, cancellationToken)).Cookies;

    // The same, saying why nothing came back.
    public static async Task<GoogleRefreshResult> RefreshWithReasonAsync(GoogleRecoveryTarget target, string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GoogleSignInProfile.TryOpen(profileId, out var profile, target.Service) || profile is null) return new(null, "no_profile");

        return await RefreshAsync(target, profile, null, cancellationToken, deleteProfileOnFailure: false);
    }

    // One-time migration for cookie-only sessions created by older releases. Seeding happens only into a brand-new,
    // empty profile; an existing browser jar is never overwritten with the helper's attribute-free snapshot.
    public static async Task<GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = GoogleSignInProfile.CreateAttempt();
        var refreshed = (await RefreshAsync(Voice, profile, cookies, cancellationToken, deleteProfileOnFailure: true)).Cookies;
        return refreshed is null ? null : new GoogleSignInResult(refreshed, profile);
    }

    private static async Task<GoogleRefreshResult> RefreshAsync(GoogleRecoveryTarget target, GoogleSignInProfile profile,
        IReadOnlyDictionary<string, string>? seedCookies, CancellationToken cancellationToken,
        bool deleteProfileOnFailure)
    {

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(Queue);
        var token = bounded.Token;
        var entered = false;
        var succeeded = false;
        var cleanupDeferred = false;
        try
        {
            while (true)
            {
                try
                {
                    await gate.WaitAsync(token);
                    entered = true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "gate_timeout"); }

                // Check only after admission: the preceding recovery may have registered a late WebView cleanup while
                // this caller was waiting for the global gate. Other profiles can proceed while this one is quarantined.
                if (!deferredCleanups.TryGetValue(profile.Id, out var priorCleanup)) break;
                gate.Release();
                entered = false;
                try { await priorCleanup.WaitAsync(token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "gate_timeout"); }
                if (deferredCleanups.TryGetValue(profile.Id, out var current) && ReferenceEquals(current, priorCleanup))
                    deferredCleanups.TryRemove(profile.Id, out _);
            }
            // The browser is this refresh's now: its own budget starts here.
            bounded.CancelAfter(Timeout);

            CoreWebView2Controller? controller = null;
            try
            {
                var environmentTask = AwaitAsync(CoreWebView2Environment.CreateWithOptionsAsync(null, profile.Path,
                    new CoreWebView2EnvironmentOptions()));
                CoreWebView2Environment environment;
                try { environment = await environmentTask.WaitAsync(token); }
                catch
                {
                    // Environment creation can outlive WaitAsync cancellation and touch the user-data folder later.
                    // A new migration profile therefore transfers cleanup to the late operation before we return.
                    cleanupDeferred = deleteProfileOnFailure;
                    TrackDeferredCleanup(profile.Id,
                        CleanupEnvironmentAsync(environmentTask, deleteProfileOnFailure ? profile : null));
                    throw;
                }
                var options = environment.CreateCoreWebView2ControllerOptions();
                var host = CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)-3)); // HWND_MESSAGE
                var controllerTask = AwaitAsync(environment.CreateCoreWebView2ControllerAsync(host, options));
                try { controller = await controllerTask.WaitAsync(token); }
                catch
                {
                    // A cancellation can win while WinRT is still constructing the controller. Cleanup retains the
                    // new profile lease, closes the late controller, and only then removes the abandoned profile.
                    cleanupDeferred = deleteProfileOnFailure;
                    TrackDeferredCleanup(profile.Id,
                        CleanupControllerAsync(controllerTask, deleteProfileOnFailure ? profile : null));
                    throw;
                }
                controller.IsVisible = false;

                var core = controller.CoreWebView2;
                Configure(core);
                if (seedCookies is not null) SeedCookies(core, seedCookies);
                var result = await LoadAsync(target, core, token, cancellationToken);
                succeeded = result.Cookies is not null;
                return result;
            }
            // The browser did not start within the budget: a slow or busy runtime, nothing about the sign-in.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(null, "browser_slow");
            }
            catch (OperationCanceledException) { throw; }
            catch (BackendException) { throw; }
            catch (Exception)
            {
                throw new BackendException(BackendFailure.Unavailable, target.FailureMessage);
            }
            finally
            {
                try { controller?.Close(); } catch (Exception) { /* A failed browser is already unusable. */ }
            }
        }
        finally
        {
            if (entered) gate.Release();
            if (deleteProfileOnFailure && !succeeded && !cleanupDeferred) await profile.DeleteAsync();
        }
    }

    private static void Configure(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
    }

    private static void SeedCookies(CoreWebView2 core, IReadOnlyDictionary<string, string> cookies)
    {
        foreach (var (name, value) in cookies)
        {
            if (GoogleVoiceSignInRules.CookieDomain(name) is not { } domain ||
                GoogleVoiceSignInRules.ReadCookie(name, domain, value) is null) continue;
            var cookieDomain = domain == "google.com" ? ".google.com" : domain;
            var cookie = core.CookieManager.CreateCookie(name, value, cookieDomain, "/");
            cookie.IsSecure = true;
            core.CookieManager.AddOrUpdateCookie(cookie);
        }
    }

    private static async Task<GoogleRefreshResult> LoadAsync(GoogleRecoveryTarget target, CoreWebView2 core,
        CancellationToken boundedToken, CancellationToken callerToken)
    {
        var completion = new TaskCompletionSource<GoogleRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? starting = null;
        TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs>? newWindow = null;
        TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs>? download = null;
        TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? completed = null;
        TypedEventHandler<CoreWebView2, CoreWebView2ProcessFailedEventArgs>? failed = null;

        starting = (_, args) =>
        {
            // This component starts exactly one fixed URL. Any other document redirect is a request for interactive sign-in.
            if (!target.AllowNavigation(args.Uri))
            {
                args.Cancel = true;
                completion.TrySetResult(new(null, "sign_in_needed"));
            }
        };
        newWindow = (_, args) => { args.Handled = true; completion.TrySetResult(new(null, "new_window")); };
        download = (_, args) => args.Cancel = true;
        failed = (_, _) => completion.TrySetException(new BackendException(BackendFailure.Unavailable, target.FailureMessage));
        completed = async (_, args) =>
        {
            if (!args.IsSuccess || !target.IsDone(core.Source))
            {
                // A page the target allows on its way (Google's account host, for Messages) may hand on to the next by
                // itself; the bounded timeout ends one that does not. Voice allows only its own page, so for Voice any
                // other page still ends the recovery at once.
                if (args.IsSuccess && target.AllowNavigation(core.Source)) return;
                completion.TrySetResult(new(null, args.IsSuccess ? "sign_in_needed" : "load_failed"));
                return;
            }
            try
            {
                var (cookies, sidtsLifetime) = await ReadCookiesAsync(target, core);
                if (!target.IsComplete(cookies)) cookies.Clear();
                completion.TrySetResult(cookies.Count == 0 ? new(null, "missing_cookies", sidtsLifetime) : new(cookies, "", sidtsLifetime));
            }
            catch (Exception)
            {
                completion.TrySetException(new BackendException(BackendFailure.Unavailable, target.FailureMessage));
            }
        };

        core.NavigationStarting += starting;
        core.NewWindowRequested += newWindow;
        core.DownloadStarting += download;
        core.NavigationCompleted += completed;
        core.ProcessFailed += failed;
        try
        {
            core.Navigate(target.Url);
            try { return await completion.Task.WaitAsync(boundedToken); }
            // Still on Google's account pages when the time ran out: the chooser or a password page, waiting for someone.
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return new(null, "incomplete"); }
        }
        finally
        {
            core.NavigationStarting -= starting;
            core.NewWindowRequested -= newWindow;
            core.DownloadStarting -= download;
            core.NavigationCompleted -= completed;
            core.ProcessFailed -= failed;
        }
    }

    // Cookie retrieval is not navigation. Values are filtered immediately and are never logged. Beside them, how long the
    // browser was told to keep __Secure-1PSIDTS, as a fixed word or whole minutes: a sign-in bound to the device (Device
    // Bound Session Credentials) gets a short-lived one the browser renews by proving it holds the key, an ordinary one a
    // cookie kept for months, so this says which kind Google gave, without signing in again to find out.
    private static async Task<(Dictionary<string, string> Cookies, string SidtsLifetime)> ReadCookiesAsync(GoogleRecoveryTarget target, CoreWebView2 core)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var sidtsLifetime = "none";
        foreach (var url in target.CookieUrls)
            foreach (var cookie in await core.CookieManager.GetCookiesAsync(url))
            {
                if (target.ReadCookie(cookie.Name, cookie.Domain, cookie.Value) is { } value)
                    result[cookie.Name] = value;
                if (cookie.Name == "__Secure-1PSIDTS") sidtsLifetime = GoogleRefreshResult.Lifetime(cookie.IsSession, cookie.Expires, DateTimeOffset.UtcNow);
            }
        return (result, sidtsLifetime);
    }

    // WebView2 exposes environment creation as a WinRT operation rather than a Task. Converting it explicitly lets the
    // same cancellation budget cover startup as well as navigation, without blocking the UI thread.
    private static Task<T> AwaitAsync<T>(IAsyncOperation<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Completed = (current, status) =>
        {
            try
            {
                switch (status)
                {
                    case AsyncStatus.Completed: completion.TrySetResult(current.GetResults()); break;
                    case AsyncStatus.Canceled: completion.TrySetCanceled(); break;
                    default: completion.TrySetException(current.ErrorCode); break;
                }
            }
            catch (Exception failure) { completion.TrySetException(failure); }
        };
        return completion.Task;
    }

    private static async Task CleanupEnvironmentAsync(Task<CoreWebView2Environment> operation,
        GoogleSignInProfile? abandonedProfile)
    {
        try { await operation; }
        catch (Exception) { }
        finally
        {
            if (abandonedProfile is not null)
                try { await abandonedProfile.DeleteAsync(); } catch (Exception) { }
        }
    }

    private static async Task CleanupControllerAsync(Task<CoreWebView2Controller> operation,
        GoogleSignInProfile? abandonedProfile)
    {
        try
        {
            var lateController = await operation;
            try { lateController.Close(); } catch (Exception) { }
        }
        catch (Exception) { }
        finally
        {
            if (abandonedProfile is not null)
                try { await abandonedProfile.DeleteAsync(); } catch (Exception) { }
        }
    }

    private static void TrackDeferredCleanup(string profileId, Task cleanup)
    {
        deferredCleanups[profileId] = cleanup;
        _ = RemoveDeferredCleanupAsync(profileId, cleanup);
    }

    private static async Task RemoveDeferredCleanupAsync(string profileId, Task cleanup)
    {
        try { await cleanup; }
        finally
        {
            if (deferredCleanups.TryGetValue(profileId, out var current) && ReferenceEquals(current, cleanup))
                deferredCleanups.TryRemove(profileId, out _);
        }
    }
}
