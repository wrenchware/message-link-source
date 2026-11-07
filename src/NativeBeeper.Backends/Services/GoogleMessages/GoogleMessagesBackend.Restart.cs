using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Connection health's Restart: the helper is let go as one that stopped by itself is — Disconnected, then the connect
// every read makes, which saves what the old helper last handed over, closes it and starts one from the saved pairing.
// It is never a pairing, a renewal or a sign-in, and it is refused while one of those is under way or needed.
internal sealed partial class GoogleMessagesBackend
{
    /// <summary>Contact-visible calls on the running helper still waiting for their answer.</summary>
    internal int MutationsInFlight => helper?.OwnMutationsInFlight ?? 0;

    /// <summary>Which helper this is and how many contact-visible calls have begun on it: see IHelperRestartTarget.Mark.</summary>
    internal string? MutationMark => helper?.OwnMutationMark;

    /// <summary>Why a restart may not run now, as a fixed word, or null when it may.</summary>
    internal string? RestartRefusal()
    {
        if (stopping) return "closing";
        if (!Files.SessionSaved) return "not_signed_in";
        // Pairing, a renewal, or the invisible refresh the window runs after a refusal.
        if (signingIn || signIn.Held(DateTimeOffset.UtcNow)) return "signing_in";
        if (signIn.PairingLost || signIn.SignInExpired || authenticationRequired) return "needs_sign_in";
        // A connect already under way, which a restart would only queue behind.
        if (attempt is not null) return HelperRestarts.Busy;
        return null;
    }

    /// <summary>
    /// Stops the helper and connects again from the saved pairing. Null once reconnected, HelperRestarts.Busy when a send
    /// began before the helper could be stopped (nothing was stopped), or the fixed code it failed with.
    /// </summary>
    internal async Task<string?> RestartAsync(bool force, string? through = null)
    {
        if (RestartRefusal() is { } refusal) return refusal;
        // Held for a whole connect; anything longer than a moment is one, and the restart stands aside for it.
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2))) return HelperRestarts.Busy;
        try
        {
            if (RestartRefusal() is { } later && later != HelperRestarts.Busy) return later;
            if (helper is { } current && !(force ? through is null || current.SealThrough(through) : current.SealIfIdle())) return HelperRestarts.Busy;
            connected = false;
            lock (typingLock) typing.Clear();
            GoogleMessagesLog.Write($"helper restart by=user forced={force}");
        }
        finally { gate.Release(); }
        // What a helper that stops by itself publishes; the realtime loop says Connected again once it sees the new one.
        Publish(new(RealtimeKind.Disconnected));
        ClearPause();
        try
        {
            await ConnectAsync(CancellationToken.None);
            return connected ? null : "not_connected";
        }
        catch (Exception failure) { return GoogleMessagesLog.Reason(failure); }
    }
}
