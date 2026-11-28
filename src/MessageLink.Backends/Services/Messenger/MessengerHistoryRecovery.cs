namespace MessageLink.App.Services.Messenger;

// A helper can remain connected while its ordinary-history replies stop arriving. Its confirmed stall gets one
// saved-session restart, never while a mutation is in flight. The budget is independent of crash recovery: a socket
// staying apparently healthy for two minutes must not buy an unlimited sequence of new Facebook logins.
internal sealed class MessengerHistoryRecovery(TimeProvider time)
{
    internal static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
    internal const int AttemptsPerWindow = 2;
    private readonly object sync = new();
    private readonly Queue<long> attempts = new();
    private object? lastGeneration;
    private bool running;

    public Task<string> RunAsync(object generation, Func<bool> isCurrent, Func<int> mutationsInFlight,
        Func<Task<string?>> restart)
    {
        lock (sync)
        {
            if (running) return Task.FromResult("already_recovering");
            if (ReferenceEquals(lastGeneration, generation)) return Task.FromResult("already_attempted");
            if (!isCurrent()) return Task.FromResult("superseded");
            lastGeneration = generation;
            running = true;
        }
        return RunCoreAsync(isCurrent, mutationsInFlight, restart);
    }

    private async Task<string> RunCoreAsync(Func<bool> isCurrent, Func<int> mutationsInFlight, Func<Task<string?>> restart)
    {
        try
        {
            while (isCurrent())
            {
                if (mutationsInFlight() > 0)
                {
                    // No timeout can authorize killing a send. Shutdown, a user reconnect or the send settling ends
                    // this wait; waiting itself makes no network requests and spends no attempt.
                    await Task.Delay(Poll, time);
                    continue;
                }
                lock (sync)
                {
                    var now = time.GetTimestamp();
                    while (attempts.TryPeek(out var oldest) && time.GetElapsedTime(oldest, now) >= Window)
                        attempts.Dequeue();
                    if (attempts.Count >= AttemptsPerWindow || attempts.Count > 0 &&
                        time.GetElapsedTime(attempts.Last(), now) < MinimumInterval) return "budget_spent";
                }
                // The restart still checks generation and atomically seals only an idle helper under the backend's
                // connect gate. A send winning the race after the earlier count makes it Busy, with nothing stopped.
                string? code;
                try { code = await restart(); }
                catch (Exception) { code = "failed"; }
                if (code != "busy")
                {
                    // These refusals precede any helper stop or Facebook call. A shutdown racing the last idle
                    // check must not use the next connection's recovery allowance.
                    if (code is not ("superseded" or "closing" or "not_signed_in" or "reconnecting" or
                        "saved_session_unreadable" or "helper_missing"))
                        lock (sync) attempts.Enqueue(time.GetTimestamp());
                    return code ?? "reconnected";
                }
                await Task.Delay(Poll, time);
            }
            return "superseded";
        }
        finally { lock (sync) running = false; }
    }
}
