namespace MessageLink.App.Services.Instagram;

/// <summary>Which list read a poll makes.</summary>
internal enum InstagramPollRead
{
    /// <summary>The inbox's first page: one request, where every new message is.</summary>
    FirstPage,
    /// <summary>Every page of every folder shown: what finds a change below the first page, and what removes a row.</summary>
    Sweep,
}

/// <summary>
/// When Instagram's poll asks, and what. Every twenty seconds it reads the inbox's first page, which is where a
/// conversation with a new message rises to, so a new message keeps its latency; every ten minutes it reads every page,
/// so a read or a change further down is caught within ten. While nobody could be looking — every Messages window
/// hidden or minimized, or the session locked — it asks every minute instead, and when somebody comes back it asks at
/// once (never within twenty seconds of the last poll) and then every twenty seconds again.
/// </summary>
/// <remarks>
/// Every poll used to read every page: 45 polls, 450 pages and 45 mailbox requests in fifteen minutes on the user's
/// real account, where being throttled risks a checkpoint. Nothing here adds a request that was not made before; it only
/// decides which of the old ones to leave out. A failing session keeps its back-off whoever comes back.
/// Thread-safe: presence is reported from window threads while the poll waits on its own.
/// </remarks>
internal sealed class InstagramPollSchedule(TimeProvider? clock = null, bool present = true)
{
    public static readonly TimeSpan PresentInterval = InstagramPollFailure.Interval;
    public static readonly TimeSpan AwayInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);
    // How often the request ledger is written, by the clock rather than by counting polls, whose pace now varies.
    public static readonly TimeSpan LedgerEvery = TimeSpan.FromMinutes(15);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private bool present = present;
    // The wait under way, which somebody coming back ends.
    private TaskCompletionSource? returned;
    private DateTimeOffset? sweptAt;
    private DateTimeOffset? ledgerAt;

    public bool Present { get { lock (gate) return present; } }

    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>
    /// Whether somebody could be looking. True when this is somebody coming back, which ends a wait under way. Coming back
    /// while the poll is reading needs nothing more: the wait after that read is already the usual twenty seconds.
    /// </summary>
    public bool SetPresent(bool value)
    {
        TaskCompletionSource? wake;
        lock (gate)
        {
            if (present == value) return false;
            present = value;
            if (!value) return false;
            wake = returned;
        }
        wake?.TrySetResult();
        return true;
    }

    /// <summary>How long until the next poll: the back-off after failures, and never less than a minute while away.</summary>
    public TimeSpan Interval(int failures)
    {
        var usual = Present ? PresentInterval : AwayInterval;
        if (failures <= 0) return usual;
        var backOff = InstagramPollFailure.Delay(failures);
        return backOff > usual ? backOff : usual;
    }

    /// <summary>
    /// Waits for the next poll, started straight after the last one. Ends early, answering true, when somebody comes back
    /// while it waits — but never sooner than the usual twenty seconds after that poll, so showing and hiding a window
    /// over and over cannot make the poll ask more often than it does for somebody sitting there; and not at all while
    /// the poll is backing off from failures, which coming back does not hurry.
    /// </summary>
    public async Task<bool> WaitAsync(int failures, CancellationToken cancellationToken)
    {
        TaskCompletionSource wake;
        TimeSpan interval;
        var started = Now;
        lock (gate)
        {
            wake = returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            interval = Interval(failures);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var timer = Task.Delay(interval, clock, stop.Token);
            var first = failures > 0 ? timer : await Task.WhenAny(timer, wake.Task).ConfigureAwait(false);
            if (first == wake.Task)
            {
                stop.Cancel();
                var soonest = started + PresentInterval - Now;
                if (soonest > TimeSpan.Zero) await Task.Delay(soonest, clock, cancellationToken).ConfigureAwait(false);
                return true;
            }
            await timer.ConfigureAwait(false);
            return false;
        }
        finally
        {
            lock (gate) if (ReferenceEquals(returned, wake)) returned = null;
        }
    }

    /// <summary>Which read this poll makes: a sweep when none has been made in ten minutes, the first page otherwise.</summary>
    public InstagramPollRead Next()
    {
        lock (gate) return sweptAt is not { } at || Now - at >= SweepEvery ? InstagramPollRead.Sweep : InstagramPollRead.FirstPage;
    }

    /// <summary>A whole read succeeded: at connect, or by a poll. The next sweep is ten minutes from now.</summary>
    public void Swept() { lock (gate) sweptAt = Now; }

    /// <summary>Whether a quarter of an hour has passed since the last ledger line. The first call starts the count.</summary>
    public bool LedgerDue()
    {
        lock (gate)
        {
            var now = Now;
            if (ledgerAt is not { } at) { ledgerAt = now; return false; }
            if (now - at < LedgerEvery) return false;
            ledgerAt = now;
            return true;
        }
    }
}

/// <summary>
/// What the poll did between two lines of the request ledger. Counts only. ThreadsReused is the newest-page reads answered without a request to Instagram
/// (shared with one already asking, or the page just read: InstagramNewestReads), beside <c>threads=</c>, which counts the ones that were requests.
/// </summary>
internal sealed record InstagramPollTally(int Polls, int Sweeps, int Away, int Arrivals, long ThreadsReused = 0)
{
    public static InstagramPollTally None { get; } = new(0, 0, 0, 0);
}
