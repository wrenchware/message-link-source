namespace MessageLink.App.Services.Instagram;

// Connection health's Restart. It is the recovery a helper that stopped by itself gets — the saved session resumed, as
// launch does — and it spends that recovery's budget: three sign-ins, back only once a helper has stayed connected two
// minutes. A session Instagram refused, after an account check above all, is never put to it again from here.
internal sealed partial class InstagramBackend
{
    // Why the last start failed, when only the user can change that answer. Cleared by a connection that succeeds.
    private volatile string? startRefused;

    /// <summary>Contact-visible calls on the running helper still waiting for their answer.</summary>
    internal int MutationsInFlight => helper?.OwnMutationsInFlight ?? 0;

    /// <summary>Which helper this is and how many contact-visible calls have begun on it: see IHelperRestartTarget.Mark.</summary>
    internal string? MutationMark => helper?.OwnMutationMark;

    /// <summary>Why a restart may not run now, as a fixed word, or null when it may. Takes nothing from the budget.</summary>
    internal string? RestartRefusal()
    {
        var (reached, uptime) = Uptime();
        return Refusal() ?? Budget(recovery => recovery.RefuseUser(reached, uptime, RefusedWith()));
    }

    /// <summary>
    /// Stops the helper and resumes the saved session. Null once reconnected, HelperRestarts.Busy when a send began before
    /// the helper could be stopped (nothing stopped, nothing spent), or the fixed code it failed or was refused with.
    /// </summary>
    internal async Task<string?> RestartAsync(bool force, string? through = null)
    {
        if (Refusal() is { } refusal) return refusal;
        var current = helper;
        var (reached, uptime) = Uptime();
        if (Budget(recovery => recovery.TakeForUser(reached, uptime, RefusedWith())) is { } spent) return spent;
        int used;
        lock (recovery) used = recovery.Used;
        if (current is not null)
        {
            // Where the next connection resumes from, as the helper holds it now: the save quitting makes, for the same
            // reason. A read, answered from memory.
            if (IsConnected)
            {
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                if (!await ResaveSessionAsync(current, "restart", bound.Token)) InstagramLog.Write("instagram session not saved after=restart");
            }
            // The session the new helper resumes from must be readable before the old one is closed off: otherwise the
            // resume gives up before starting anything, and the closed-off helper would be left refusing every call.
            var (probe, problem) = await store.ReadSessionAsync();
            if (probe is null)
            {
                lock (recovery) recovery.Refund();
                return "saved_session_" + (problem ?? "unreadable");
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(probe);
            // Not ready before it is closed off, so a send from here on waits for the new helper ("reconnecting") instead of
            // reaching one that refuses it unwritten and reads as a send whose outcome is unknown.
            var wasReady = ready;
            ready = false;
            if (!(force ? through is null || current.SealThrough(through) : current.SealIfIdle()))
            {
                ready = wasReady;
                lock (recovery) recovery.Refund();
                return HelperRestarts.Busy;
            }
        }
        ExpectReconnect(true);
        InstagramLog.Write($"instagram helper restart by=user attempt={used} forced={force}");
        try
        {
            // automatic: a close somebody asked for (signing out, quitting) still wins over this.
            var shown = await ResumeAsync(automatic: true);
            if (shown < 0)
            {
                // It changed between the look above and the resume. The closed-off helper is closed rather than left behind.
                if (current is not null) await CloseSealedAsync(current);
                return "saved_session_" + (SavedSessionProblem ?? "unreadable");
            }
            InstagramLog.Write($"instagram helper restarted conversations={shown}");
            return null;
        }
        catch (Exception failure)
        {
            var code = InstagramConnectionFailure.Code(failure);
            InstagramLog.Write($"instagram helper restart failed code={code}");
            // A resume that failed before its start closed anything (the helper file gone, an account check, a held refusal) left the closed-off
            // helper in place, refusing every call while Settings said reconnecting. Closed only if it is still the current one.
            if (current is not null) try { await CloseSealedAsync(current); } catch (Exception) { }
            return code;
        }
        finally { if (!IsConnected) ExpectReconnect(false); }
    }

    // The closed-off helper of a restart whose resume gave up before starting anything: closed as a start would close it, and
    // only if it is still the current one, so a start that came in meanwhile is left alone.
    private async Task CloseSealedAsync(object sealedHelper)
    {
        await gate.WaitAsync();
        try { if (ReferenceEquals(helper, sealedHelper)) await CloseCoreAsync(); }
        finally { gate.Release(); }
    }

    private string? Refusal()
    {
        if (!store.HasSession) return "not_signed_in";
        if (disposed || closing) return "closing";
        // A start already under way: a sign-in, the launch restore, a Reconnect or a recovery.
        if (gate.CurrentCount == 0) return HelperRestarts.Busy;
        if (helper is null && reconnectComing) return "reconnecting";
        return null;
    }

    // What stopped the last connection, when only the user can change it: the poll's refusal, the realtime connection's,
    // or a start's.
    private string? RefusedWith()
    {
        string? state;
        lock (realtimeGate) state = realtimeState;
        return refused ?? startRefused ?? HeldCode ?? (InstagramConnectionFailure.NeedsUser(state) ? state : null);
    }

    private (bool Reached, TimeSpan Uptime) Uptime()
    {
        var since = readySince;
        var reached = IsConnected && since is not null && ReferenceEquals(since.Helper, helper);
        return (reached, reached ? DateTimeOffset.UtcNow - since!.At : TimeSpan.Zero);
    }

    private string? Budget(Func<InstagramRecovery, string?> decide)
    {
        lock (recovery) return decide(recovery);
    }
}
