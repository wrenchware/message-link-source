using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.Messenger;

// Connection health's Restart: what recovery does after a helper stopped by itself — the saved Facebook session
// connected again, as launch does — once, when somebody asks. It never opens a sign-in window.
internal sealed partial class MessengerBackend
{
    // Set while a recovery after a helper stop is waiting or connecting, so a restart does not start a second one.
    private volatile bool recovering;
    // Set from just before the old helper is closed off until the new one is ready or the restart has failed. A send in
    // that window is refused as not sent: the closed-off helper would refuse it unwritten, and the transport failure that
    // reaches the send path cannot tell that from one whose outcome is unknown.
    private volatile bool restarting;
    private void ThrowIfRestarting()
    {
        if (restarting) throw new NativeBeeper.Core.Services.SendException(false, "Not sent. Messenger is reconnecting; send it again in a moment.");
    }

    /// <summary>Contact-visible calls on the running helper still waiting for their answer.</summary>
    internal int MutationsInFlight => helper?.OwnMutationsInFlight ?? 0;

    /// <summary>Which helper this is and how many contact-visible calls have begun on it: see IHelperRestartTarget.Mark.</summary>
    internal string? MutationMark => helper?.OwnMutationMark;

    /// <summary>Why a restart may not run now, as a fixed word, or null when it may.</summary>
    internal string? RestartRefusal()
    {
        if (!SessionSaved) return "not_signed_in";
        if (closing) return "closing";
        // A connect already under way holds the gate for the whole sign-in.
        if (gate.CurrentCount == 0) return HelperRestarts.Busy;
        if (recovering || restarting) return "reconnecting";
        return null;
    }

    /// <summary>
    /// Stops the helper and connects again from the saved session. Null once reconnected, HelperRestarts.Busy when a
    /// send began before the helper could be stopped (nothing was stopped), or the fixed code it failed with.
    /// </summary>
    internal Task<string?> RestartAsync(bool force, string? through = null) => RestartSavedAsync(force, expected: null, through);

    // Automatic recovery names the exact helper that reported the stall. A user connect, sign-out or other recovery
    // superseding it wins, including when that happens while the saved session is being read.
    private async Task<string?> RestartSavedAsync(bool force, HelperConnection? expected, string? through = null)
    {
        if (RestartRefusal() is { } refusal) return refusal;
        if (!File.Exists(HelperExecutables.PathFor("messenger-helper"))) return "helper_missing";
        if (!await gate.WaitAsync(0)) return HelperRestarts.Busy;
        Dictionary<string, string>? cookies = null;
        try
        {
            if (closing) return "closing";
            if (recovering || expected is not null && !ReferenceEquals(helper, expected)) return "superseded";
            try { cookies = await ReadSavedCookiesAsync(); }
            catch (Exception) { return "saved_session_unreadable"; }
            if (cookies is null) return "saved_session_unreadable";
            if (closing) return "closing";
            if (expected is not null && !ReferenceEquals(helper, expected)) return "superseded";
            restarting = true;
            if (helper is { } current)
            {
                if (!(force ? through is null || current.SealThrough(through) : current.SealIfIdle())) return HelperRestarts.Busy;
                // Nothing more goes to it: a send sees Messenger reconnecting until the new helper is ready.
                ready = false;
            }
            MessengerLog.Write(expected is null ? $"helper restart by=user forced={force}" : "helper restart reason=history_stalled forced=false");
            // Closes the old helper first, which is not a stop to recover from; adoptLegacy as every saved-session connect.
            await ConnectLockedAsync(cookies, adoptLegacy: true);
            MessengerLog.Write($"helper restarted; {chats.Length} conversations");
        }
        catch (Exception failure)
        {
            var code = MessengerFailure.Code(failure);
            MessengerLog.Write($"helper restart failed: {code}");
            return code;
        }
        finally { restarting = false; cookies?.Clear(); gate.Release(); }
        // The new helper must be asked for the open page again; announcing only Connected would leave the old rows.
        AnnounceConnected();
        return null;
    }

    private readonly MessengerHistoryRecovery historyRecovery = new(TimeProvider.System);

    private void HistoryStalled(HelperConnection started)
    {
        if (closing || !ReferenceEquals(helper, started)) return;
        // Events arrive on the helper's own reader. Reconnecting disposes that reader, so it must run elsewhere.
        _ = Task.Run(async () =>
        {
            var outcome = await historyRecovery.RunAsync(started,
                () => !closing && !recovering && ReferenceEquals(helper, started),
                () => started.OwnMutationsInFlight,
                () => RestartSavedAsync(force: false, expected: started));
            if (outcome is not ("already_recovering" or "already_attempted" or "superseded"))
                MessengerLog.Write($"helper history recovery outcome={HelperRestarts.Word(outcome)}");
        });
    }
}
