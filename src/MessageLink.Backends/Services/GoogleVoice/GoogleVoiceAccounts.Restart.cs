namespace MessageLink.App.Services.GoogleVoice;

// Connection health's Restart for one Voice account: what the paced restart does once a helper has stopped by itself,
// run at once — the saved session started again, and when Google refuses it, the kept browser profile's refresh. It owns
// the account's recovery throughout, as that restart does, so no other replacement starts beside it; and if it fails,
// that paced restart takes over, exactly as it would after a crash.
internal sealed partial class GoogleVoiceAccounts
{
    private GoogleVoiceAccount? Listed(string key) => Accounts.FirstOrDefault(account => account.Key == key);

    /// <summary>Contact-visible calls on this account's helper still waiting for their answer.</summary>
    internal int MutationsInFlight(string key) => Listed(key)?.MutationsInFlight ?? 0;

    /// <summary>Which helper this account's is and how many contact-visible calls have begun on it: see IHelperRestartTarget.Mark.</summary>
    internal string? MutationMark(string key) => Listed(key)?.MutationMark;

    /// <summary>Why a restart of this account may not run now, as a fixed word, or null when it may.</summary>
    internal string? RestartRefusal(string key) => Listed(key) is not { } account ? "not_started"
        : account.IsDisposed ? "not_started"
        : account.NeedsSignIn ? "needs_sign_in"
        // A browser recovery, or the paced restart after a stop: already bringing it back.
        : account.Recovery.IsOwned ? "reconnecting"
        : null;

    /// <summary>
    /// Stops the account's helper and starts it again from the saved session. Null once reconnected,
    /// HelperRestarts.Busy when a send began before the helper could be stopped (nothing was stopped), or the fixed code
    /// it failed or was refused with.
    /// </summary>
    internal async Task<string?> RestartAsync(string key, bool force, string? through = null)
    {
        if (RestartRefusal(key) is { } refusal) return refusal;
        var account = Listed(key)!;
        if (!account.Recovery.TryBegin()) return "reconnecting";
        var tag = GoogleVoiceLog.Tag(key);
        var handedOn = false;
        try
        {
            CancellationToken token;
            try { token = account.RecoveryToken; }
            catch (ObjectDisposedException) { return "not_started"; }
            if (!await account.StopForRestartAsync(force, through)) return HelperRestarts.Busy;
            GoogleVoiceLog.Write($"restart account={tag} reason=user forced={force}");
            try
            {
                var saved = await ReadAsync(Path.Combine(Root, account.Key, "session.protected"));
                if (saved?.Cookies is not { Count: > 0 }) throw new InvalidOperationException("unreadable");
                await StartAsync(saved with { ProfileId = account.ProfileId }, account.Key, account, token);
                GoogleVoiceLog.Write($"restart account={tag} connected reason=user");
                return null;
            }
            catch (InvalidOperationException failure) when (failure.Message == "auth_required")
            {
                // Google's two hours ran out on the saved sign-in; the browser profile renews it, as after a crash.
                GoogleVoiceLog.Write($"restart account={tag} refused reason=user");
                await RecoverOwnedAsync(account);
                return Listed(key) is { IsConnected: true } ? null : "auth_required";
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                var reason = GoogleVoiceLog.Reason(failure);
                GoogleVoiceLog.Write($"restart account={tag} failed reason={reason} by=user");
                // The helper is gone and this one did not come back: the paced restart a stopped helper gets takes over.
                account.Recovery.End();
                handedOn = true;
                _ = Restart(account, "user_restart_failed");
                return reason;
            }
        }
        catch (OperationCanceledException) { return "cancelled"; }
        finally { if (!handedOn) account.Recovery.End(); }
    }
}
