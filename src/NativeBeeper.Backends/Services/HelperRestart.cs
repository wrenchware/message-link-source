using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

/// <summary>
/// One service's helper as Connection health's Restart sees it. Each provider answers these from its own state, and its
/// restart is the stop-and-resume it already makes when its helper stops by itself: the saved session, never a pairing
/// or a new sign-in.
/// </summary>
internal interface IHelperRestartTarget
{
    /// <summary>The fixed word the log names this helper by: google_messages, messenger, instagram or gvoice.</summary>
    string Name { get; }

    /// <summary>Why a restart must not start now, as a fixed code, or null when it may.</summary>
    string? Refusal();

    /// <summary>Contact-visible calls on this helper still waiting for their answer.</summary>
    int MutationsInFlight { get; }

    /// <summary>
    /// Opaque: which helper this is and how many contact-visible calls have ever begun on it. It differs from an earlier reading
    /// once a send has begun since, or the helper was replaced; null when there is no helper to read it from.
    /// </summary>
    string? Mark { get; }

    /// <summary>
    /// Stops the helper and resumes from the saved session. Null when it reconnected; <see cref="HelperRestarts.Busy"/>
    /// when a call someone would see began before the helper could be stopped, in which case nothing was stopped; any
    /// other fixed code when it failed or was refused. With force the helper is stopped whatever is in flight: those
    /// calls' outcomes are then unknown, and they are reported as such and never sent again. With a <paramref name="through"/>
    /// mark as well, only while no call has begun since that mark was read, and Busy otherwise: the yes covered the sends it
    /// was asked about, not one that came after.
    /// </summary>
    Task<string?> RestartAsync(bool force, string? through = null);
}

internal sealed class HelperRestartTarget(string name, Func<string?> refusal, Func<int> mutations, Func<string?> mark, Func<bool, string?, Task<string?>> restart)
    : IHelperRestartTarget
{
    public string Name => name;
    public string? Refusal() => refusal();
    public int MutationsInFlight => mutations();
    public string? Mark => mark();
    public Task<string?> RestartAsync(bool force, string? through = null) => restart(force, through);
}

internal enum HelperRestartStep { Waiting, Asking, Restarting }

/// <summary>
/// Restarts one helper at a time per service, and never under a send: it first waits for the calls someone else would
/// see to be answered, and past <see cref="Patience"/> it asks before going on. A helper that takes a new one between the
/// wait and the stop is waited for again, since the provider refuses to stop it (<see cref="Busy"/>).
/// </summary>
/// <remarks>
/// Polled, as shutdown's wait is: this runs when someone presses a button, and a wait that every reply had to wake would
/// cost every reply something for the sake of that moment.
/// </remarks>
internal sealed class HelperRestarts(TimeProvider time, Action<string> log)
{
    public const string Busy = "busy";
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    // A provider that keeps answering Busy with nothing in flight is not waited on forever.
    private const int BusyLimit = 20;
    private readonly HashSet<string> running = new(StringComparer.Ordinal);

    /// <summary>Whether the helper serving this scope is being restarted now.</summary>
    public bool IsRunning(string scope) { lock (running) return running.Contains(scope); }

    /// <summary>
    /// Restarts the helper serving a scope. Ask is called at most once, when sends are still in flight after
    /// <see cref="Patience"/>; true goes on regardless, false cancels, and its token is cancelled when the sends settled
    /// while it was being asked. Step says what it is doing, from whichever thread is doing it.
    /// </summary>
    public async Task<HelperRestartResult> RunAsync(string scope, IHelperRestartTarget target,
        Func<CancellationToken, Task<bool>> ask, Action<HelperRestartStep>? step = null)
    {
        lock (running)
            if (!running.Add(scope)) return Finish(target, new("refused", "already_restarting", 0));
        var began = time.GetTimestamp();
        try
        {
            if (Safe(target.Refusal) is { } refused) return Finish(target, new("refused", refused, 0));
            var force = false;
            for (var busy = 0; ; busy++)
            {
                if (target.MutationsInFlight > 0)
                {
                    var answer = await SettleAsync(target, ask, step, began);
                    if (answer == Answer.Cancelled) return Finish(target, new("cancelled", null, Waited(began)));
                    force = answer == Answer.Forced;
                    // Anything could have changed while it waited: a sign-in begun, a helper that stopped and is already
                    // reconnecting by itself.
                    if (Safe(target.Refusal) is { } later) return Finish(target, new("refused", later, Waited(began)));
                }
                var waited = Waited(began);
                step?.Invoke(HelperRestartStep.Restarting);
                string? code;
                try { code = await target.RestartAsync(force); }
                catch (Exception failure) { code = failure is OperationCanceledException ? "cancelled" : failure.GetType().Name; }
                if (code == Busy && !force && busy < BusyLimit)
                {
                    // Busy with no send in flight is something else holding the helper, such as a connect starting: said as the
                    // refusal it is, or tried again after a pause, never in a loop that spends every retry at once.
                    if (target.MutationsInFlight == 0)
                    {
                        if (Safe(target.Refusal) is { } holding) return Finish(target, new("refused", holding, Waited(began)));
                        await Task.Delay(Poll, time);
                    }
                    continue;
                }
                return Finish(target, new(code is null ? "reconnected" : "failed", code, waited, force));
            }
        }
        finally { lock (running) running.Remove(scope); }
    }

    /// <summary>
    /// The restart for a caller that cannot be asked in the middle of it and knows things at the moment of the stop that this host cannot
    /// (a client of the host, <see cref="IHelperRestarter"/>), in two kinds of call. Settle waits for the sends on the helper and stops
    /// nothing: <c>settled</c> once none is in flight, or <c>asking</c> past <see cref="Patience"/>, with the mark of what is. The stop
    /// call never waits, so the caller can hold its own controls for exactly that long and state again whether it is signing in: it stops
    /// the helper when nothing is in flight, and answers <c>waiting</c> when a send has begun since the settle. Its force is the person's
    /// yes to <c>asking</c>, and counts only with that answer's token: it stops the helper under those sends and no others. A send that
    /// began after the question makes the answer <c>asking</c> again, with a new token; with none left in flight a yes is an ordinary
    /// restart.
    /// </summary>
    public async Task<HelperRestartResult> RunAsync(string scope, IHelperRestartTarget target, bool settle, bool force, string? token)
    {
        lock (running)
            if (!running.Add(scope)) return Finish(target, new("refused", "already_restarting", 0));
        var began = time.GetTimestamp();
        try
        {
            if (Safe(target.Refusal) is { } refused) return Finish(target, new("refused", refused, 0));
            if (settle)
            {
                while (target.MutationsInFlight > 0)
                {
                    if (time.GetElapsedTime(began) >= Patience) return Finish(target, new("asking", null, Waited(began), Token: target.Mark));
                    await Task.Delay(Poll, time);
                }
                // Anything could have changed while it waited, such as a helper that stopped and is already reconnecting by itself.
                if (Safe(target.Refusal) is { } later) return Finish(target, new("refused", later, Waited(began)));
                return Finish(target, new("settled", null, Waited(began)));
            }
            for (var busy = 0; ; busy++)
            {
                // Only the provider's own seal says authoritatively that nothing began since the token; this look is the quick answer.
                string? through = null;
                if (target.MutationsInFlight > 0)
                {
                    if (!force || token is null) return Finish(target, new("waiting", null, 0));
                    var mark = target.Mark;
                    if (mark is null || mark != token) return Finish(target, new("asking", null, 0, Token: mark));
                    through = token;
                }
                string? code;
                try { code = await target.RestartAsync(through is not null, through); }
                catch (Exception failure) { code = failure is OperationCanceledException ? "cancelled" : failure.GetType().Name; }
                if (code == Busy && busy < BusyLimit)
                {
                    // A send that began since the look above (the provider could not stop the helper under it) is answered by the look at
                    // the top of the next round. Busy with none in flight is something else holding the helper, such as a connect starting.
                    if (target.MutationsInFlight == 0 && Safe(target.Refusal) is { } holding) return Finish(target, new("refused", holding, 0));
                    await Task.Delay(Poll, time);
                    continue;
                }
                return Finish(target, new(code is null ? "reconnected" : "failed", code, 0, through is not null));
            }
        }
        finally { lock (running) running.Remove(scope); }
    }

    private enum Answer { Settled, Forced, Cancelled }

    private async Task<Answer> SettleAsync(IHelperRestartTarget target, Func<CancellationToken, Task<bool>> ask,
        Action<HelperRestartStep>? step, long began)
    {
        step?.Invoke(HelperRestartStep.Waiting);
        while (target.MutationsInFlight > 0)
        {
            if (time.GetElapsedTime(began) >= Patience) return await AskAsync(target, ask, step);
            await Task.Delay(Poll, time);
        }
        return Answer.Settled;
    }

    // Asked once. The question stands until it is answered or the sends settle, which answers it for the person.
    private async Task<Answer> AskAsync(IHelperRestartTarget target, Func<CancellationToken, Task<bool>> ask, Action<HelperRestartStep>? step)
    {
        step?.Invoke(HelperRestartStep.Asking);
        using var retract = new CancellationTokenSource();
        Task<bool> question;
        try { question = ask(retract.Token); }
        catch (Exception) { return Answer.Cancelled; }
        while (!question.IsCompleted)
        {
            if (target.MutationsInFlight == 0)
            {
                retract.Cancel();
                return Answer.Settled;
            }
            await Task.WhenAny(question, Task.Delay(Poll, time));
        }
        try { return await question ? Answer.Forced : Answer.Cancelled; }
        catch (Exception) { return Answer.Cancelled; }
    }

    private long Waited(long began) => (long)time.GetElapsedTime(began).TotalMilliseconds;

    private static string? Safe(Func<string?> refusal)
    {
        try { return refusal(); }
        catch (Exception failure) { return failure.GetType().Name; }
    }

    // Fixed words only: the helper's name and a code, both reduced to what a code can be made of, in the line and in the answer
    // that a caller (possibly in another process) is given.
    private HelperRestartResult Finish(IHelperRestartTarget target, HelperRestartResult result)
    {
        if (result.Code is not null) result = result with { Code = Word(result.Code) };
        var code = result.Code is null ? "" : $" code={result.Code}";
        var forced = result.Forced ? " forced=true" : "";
        log($"helper restart name={Word(target.Name)} outcome={result.Outcome} waited_ms={result.WaitedMs}{code}{forced}");
        return result;
    }

    internal static string Word(string value) =>
        value.Length is > 0 and <= 48 && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            ? value : "other";

    /// <summary>What a row says while a restart runs.</summary>
    public static string Describe(HelperRestartStep step) => step switch
    {
        HelperRestartStep.Waiting => "Waiting for a message to finish sending…",
        HelperRestartStep.Asking => "A message is still sending. Restarting now leaves its outcome unknown; it is never sent again.",
        _ => "Restarting…",
    };

    /// <summary>What a row says once a restart has ended.</summary>
    public static string Describe(HelperRestartResult result) => result.Outcome switch
    {
        "reconnected" => "Restarted and reconnected.",
        "cancelled" => "Restart cancelled.",
        "asking" => Describe(HelperRestartStep.Asking),
        "settled" => Describe(HelperRestartStep.Restarting),
        "waiting" => Describe(HelperRestartStep.Waiting),
        // The code is for the log (helper restart outcome=), never the row.
        "failed" => "The restart didn't work. Try again in a minute.",
        _ => Refused(result.Code),
    };

    private static string Refused(string? code) => code switch
    {
        "already_restarting" => "Already restarting.",
        "signing_in" => "Signing in or connecting now. Restart once that finishes.",
        Busy => "Connecting now. Try again in a moment.",
        "reconnecting" => "Already reconnecting by itself.",
        "needs_sign_in" => "Needs signing in again in Settings. A restart can't fix that.",
        "account_check" => "Instagram wants an account check. Complete it in Instagram, then sign out and connect again in Settings.",
        "budget_spent" => "Instagram restarted 3 times without staying connected for 2 minutes. To protect the account, it isn't restarted again yet.",
        "not_signed_in" => "Not signed in on this PC.",
        "not_started" => "Not running here. Reconnect it in Settings.",
        "closing" => "Messages is closing.",
        _ => "Can't restart now. Try again in a minute.",
    };
}

/// <summary>
/// A client's side of a restart asked of the host in two kinds of call (<see cref="IHelperRestarter"/>): the wait for sends, then the
/// stop. What only the client knows is read afresh for the stop call, so a sign-in begun during the wait is refused just before the
/// helper would be stopped, and the client's controls are held for the stop call alone, not for the wait before it. Everything the
/// client is given is called on the context the restart was started on (no ConfigureAwait), since it is the client's window state.
/// </summary>
internal static class HelperRestartStages
{
    /// <summary>How often a question is checked against the sends it is about.</summary>
    public static readonly TimeSpan Watch = TimeSpan.FromMilliseconds(250);
    // A restart that keeps finding a new send between its calls is not run forever.
    private const int Rounds = 20;

    private enum Verdict { No, Yes, Withdrawn }

    /// <param name="signingIn">Whether the client is signing in to that service itself, read at every call.</param>
    /// <param name="hold">Makes the client's own Connect and Sign out do nothing until the returned handle is disposed; taken for the stop call only.</param>
    /// <param name="sending">Whether the client can see a send of its own to that service in flight: what the question is watched against.</param>
    /// <param name="ask">Puts the question to the person; true goes on regardless. Its token is cancelled when the sends it is about were answered meanwhile.</param>
    /// <param name="step">What the restart is doing now (the client's line).</param>
    public static async Task<HelperRestartResult> RunAsync(IHelperRestarter restarter, string scope, Func<bool> signingIn, Func<IDisposable> hold,
        Func<bool> sending, Func<CancellationToken, Task<bool>> ask, Action<HelperRestartStep> step, TimeProvider time)
    {
        async Task<HelperRestartResult> Call(bool settle, bool force = false, string? token = null)
        {
            var signing = signingIn();
            // Not over a sign-in already under way, whose flag the release would clear.
            using var held = settle || signing ? null : hold();
            return await restarter.RestartAsync(scope, signing, force, token, settle);
        }

        var answer = await Call(settle: true);
        for (var round = 0; round < Rounds; round++)
        {
            switch (answer.Outcome)
            {
                case "settled":
                    step(HelperRestartStep.Restarting);
                    answer = await Call(settle: false);
                    break;
                // A send began between the two calls: waited for as the first was.
                case "waiting":
                    step(HelperRestartStep.Waiting);
                    answer = await Call(settle: true);
                    break;
                // Sends are still in flight and nothing was stopped: whether to go on is the person's to say. A yes goes back with the
                // token of the sends it was asked about, and if others began since, the host asks again rather than cut those off. A
                // question withdrawn because they were answered is an ordinary restart, which finds that out for itself.
                case "asking":
                    step(HelperRestartStep.Asking);
                    var verdict = await AskAsync(ask, sending, time);
                    if (verdict == Verdict.No) return new("cancelled", null, answer.WaitedMs);
                    step(HelperRestartStep.Restarting);
                    answer = verdict == Verdict.Yes ? await Call(settle: false, force: true, token: answer.Token) : await Call(settle: true);
                    break;
                default:
                    return answer;
            }
        }
        return new("failed", HelperRestarts.Busy, 0);
    }

    // Watched against the sends the client can see: when the last of them has been answered the question is withdrawn. A question about
    // sends it never saw stays until it is answered, and the host's token is what keeps a yes from covering a send it was not asked about.
    private static async Task<Verdict> AskAsync(Func<CancellationToken, Task<bool>> ask, Func<bool> sending, TimeProvider time)
    {
        using var retract = new CancellationTokenSource();
        Task<bool> question;
        try { question = ask(retract.Token); }
        catch (Exception) { return Verdict.No; }
        var seen = sending();
        while (!question.IsCompleted)
        {
            await Task.WhenAny(question, Task.Delay(Watch, time));
            if (question.IsCompleted) break;
            var now = sending();
            if (seen && !now)
            {
                retract.Cancel();
                return Verdict.Withdrawn;
            }
            seen = now;
        }
        try { return await question ? Verdict.Yes : Verdict.No; }
        catch (Exception) { return Verdict.No; }
    }
}
