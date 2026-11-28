using MessageLink.Core.Backend;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services;

/// <summary>
/// The rules for searching Messenger's or Instagram's directory (<see cref="RemoteSearchRules"/>), kept where the search is answered. They were the client's
/// alone: three characters, a pause before asking, 900 ms between calls, answers remembered for five minutes. A client that is not this app's window (a
/// device paired with a server) is not bound by the window's code, and each call is a real request on the user's own account, with Instagram's limits
/// unknown and a checkpoint the price of being throttled. So the backend holds them whoever asks, and the window's own pacing never trips them.
/// </summary>
/// <remarks>
/// A query under the minimum is answered empty without a call. A query asked within five minutes is answered from memory. A call comes at least
/// <see cref="RemoteSearchRules.PerProvider"/> after the last; a search that arrives early waits its turn rather than being dropped, but only one waits
/// at a time, so a flood is refused (rate limited, transient) instead of queued.
/// </remarks>
internal sealed class PacedDirectorySearch(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly RemoteContactCache cache = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim turn = new(1, 1);
    private DateTimeOffset lastCall = DateTimeOffset.MinValue;

    /// <summary>How many times the provider was actually asked: the harness counts it.</summary>
    internal int Calls { get; private set; }

    public async Task<IReadOnlyList<ContactPerson>> SearchAsync(string account, string query, Func<string, Task<IReadOnlyList<ContactPerson>>> ask,
        CancellationToken cancellationToken)
    {
        var text = query.Trim();
        if (!RemoteSearchRules.Allows(text)) return [];
        if (Remembered(account, text) is { } known) return known;
        if (!await turn.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new BackendException(BackendFailure.RateLimited, "A search is already under way. Try again in a moment.") { Transient = true };
        try
        {
            // The same words may have been answered while this waited for the turn.
            if (Remembered(account, text) is { } again) return again;
            var wait = lastCall + RemoteSearchRules.PerProvider - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            lastCall = time.GetUtcNow();
            Calls++;
            var answer = await ask(text).ConfigureAwait(false);
            lock (gate) cache.Remember(account, text, answer, time.GetUtcNow());
            return answer;
        }
        finally { turn.Release(); }
    }

    private IReadOnlyList<ContactPerson>? Remembered(string account, string text)
    {
        lock (gate) return cache.Lookup(account, text, time.GetUtcNow());
    }

    /// <summary>A sign-out makes what was answered somebody else's; the pace of calls stays.</summary>
    public void ForgetAnswers()
    {
        lock (gate) cache.ForgetAnswers();
    }
}
