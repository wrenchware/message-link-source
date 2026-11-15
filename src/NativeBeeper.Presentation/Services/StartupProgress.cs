namespace NativeBeeper.Presentation.Services;

/// <summary>What one service is still doing at launch, in the order a launch goes through them.</summary>
public enum StartupPhase { Connecting, CatchingUp, Syncing }

/// <summary>One service still loading: its phase and its user-facing name.</summary>
public sealed record StartupPending(StartupPhase Phase, string Service);

/// <summary>
/// The moment the launch settled, once: since process start, why (all, or the cap), and how many services were still
/// pending then, by phase.
/// </summary>
public sealed record StartupSettle(TimeSpan At, string Reason, int Pending, int Services, int Connecting, int CatchingUp, int Syncing);

/// <summary>
/// Whether the launch has settled: every service set up on this PC has connected, finished reading what came while the app
/// was closed where it does that (Google Messages' priority catch-up, Instagram's catch-up), and been read into the list by
/// a full resync that began after it connected — or thirty seconds have passed since the process started. A service that
/// could not connect, or needs signing in again, is settled for this purpose: it is not loading, and its own notice says
/// what is wrong. Settles once; nothing that happens later in the run (a reconnect, a wake, another resync) unsettles it.
/// </summary>
/// <remarks>
/// Pure and single-threaded: MainWindow feeds it on the interface thread and asks it on a short timer. Times are
/// ProgressIndicator.SinceLaunch, so a connection and a resync are compared on one clock whichever thread saw them.
/// </remarks>
public sealed class StartupProgress
{
    // Past this, the launch is over whatever is still pending. The owner's two launches on 2026-10-02 had every service
    // connected by 4.8 s and the first full resync applied by 10.3 s; a service still connecting at thirty is a failure its
    // own notice reports, not a launch.
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    // Shares of the taskbar's fill. Connecting is most of a launch's wait; a catch-up and the full resync follow it.
    private const double ConnectShare = .5, CatchUpShare = .3, SyncShare = .2;

    private sealed class Service(string scope)
    {
        public string Scope { get; } = scope;
        public TimeSpan? ConnectedAt;
        public bool Synced, Unavailable;
    }

    private readonly Dictionary<string, Service> services = new(StringComparer.Ordinal);
    private readonly Func<string, bool> catchingUp;
    // The start of the latest full resync that finished. One that began after a service connected has read that service.
    private TimeSpan? resyncBegan;
    private double reported;

    /// <param name="scopes">The connection scopes set up on this PC as the launch begins.</param>
    /// <param name="catchingUp">Whether a scope's catch-up is running now (CatchUpRuns.IsRunning); none when null.</param>
    public StartupProgress(IEnumerable<string> scopes, Func<string, bool>? catchingUp = null)
    {
        foreach (var scope in scopes) services.TryAdd(scope, new(scope));
        this.catchingUp = catchingUp ?? (_ => false);
    }

    public IReadOnlyCollection<string> Scopes => services.Keys;
    public StartupSettle? Settled { get; private set; }
    public bool IsConnected(string scope) => services.TryGetValue(scope, out var service) && service.ConnectedAt is not null;

    /// <summary>A service reported itself connected at this moment. A later connection counts from its own moment: the list
    /// has to be read again after it, which the resync that every connection asks for does.</summary>
    public void Connected(string scope, TimeSpan at)
    {
        if (!services.TryGetValue(scope, out var service)) return;
        service.Unavailable = false;
        if (service.ConnectedAt is { } before && before >= at) return;
        service.ConnectedAt = at;
        // A resync that began after this moment may already have finished: the interface thread hears of a connection
        // through a queue, and the resync can get there first.
        service.Synced = resyncBegan >= at;
    }

    /// <summary>The service could not connect, or waits on the user to sign in again: not loading. A later connection
    /// undoes this.</summary>
    public void Unavailable(string scope)
    {
        if (services.TryGetValue(scope, out var service)) service.Unavailable = true;
    }

    /// <summary>A full resync that began at this moment has been applied to the list.</summary>
    public void Resynced(TimeSpan began)
    {
        if (resyncBegan is null || began > resyncBegan) resyncBegan = began;
        foreach (var service in services.Values)
            if (service.ConnectedAt <= began) service.Synced = true;
    }

    /// <summary>What is still loading, in phase order and then service order. Empty once everything has settled.</summary>
    public IReadOnlyList<StartupPending> Pending()
    {
        var pending = new List<StartupPending>();
        foreach (var service in services.Values.OrderBy(service => Rank(service.Scope)).ThenBy(service => service.Scope, StringComparer.Ordinal))
            if (PhaseOf(service) is { } phase) pending.Add(new(phase, ServiceScopes.Name(service.Scope)));
        return [.. pending.OrderBy(item => item.Phase)];
    }

    private StartupPhase? PhaseOf(Service service) =>
        service.Unavailable ? null
        : service.ConnectedAt is null ? StartupPhase.Connecting
        : catchingUp(service.Scope) ? StartupPhase.CatchingUp
        : !service.Synced ? StartupPhase.Syncing : null;

    /// <summary>
    /// Settles the launch when nothing is pending, or when the cap has passed. Returns the settle the one time it happens,
    /// and null on every other call.
    /// </summary>
    public StartupSettle? Evaluate(TimeSpan now)
    {
        if (Settled is not null) return null;
        var pending = Pending();
        if (pending.Count > 0 && now < Cap) return null;
        // Counted by service, not by name: two Voice accounts still connecting are two.
        var phases = services.Values.Select(PhaseOf).Where(phase => phase is not null).ToArray();
        Settled = new(now, pending.Count == 0 ? "all" : "cap", phases.Length, services.Count,
            phases.Count(phase => phase == StartupPhase.Connecting), phases.Count(phase => phase == StartupPhase.CatchingUp),
            phases.Count(phase => phase == StartupPhase.Syncing));
        return Settled;
    }

    /// <summary>How far along the launch is, from 0 to 1, for the taskbar. Never goes back: a catch-up that starts after its
    /// connection counted it as done holds the fill where it was rather than taking it down.</summary>
    public double Fraction()
    {
        if (Settled is not null || services.Count == 0) return reported = 1;
        var sum = 0d;
        foreach (var service in services.Values)
        {
            if (service.Unavailable) { sum += 1; continue; }
            var connected = service.ConnectedAt is not null;
            sum += (connected ? ConnectShare : 0) + (connected && !catchingUp(service.Scope) ? CatchUpShare : 0) + (service.Synced ? SyncShare : 0);
        }
        return reported = Math.Max(reported, Math.Clamp(sum / services.Count, 0, 1));
    }

    /// <summary>
    /// In plain words, one line per phase: "Connecting · Instagram", "Catching up · Google Messages, Instagram". A name
    /// appears once a line, however many accounts of it are pending.
    /// </summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<StartupPending> pending) =>
        [.. pending.GroupBy(item => item.Phase).OrderBy(group => group.Key)
            .Select(group => $"{Word(group.Key)} · {string.Join(", ", group.Select(item => item.Service).Distinct())}")];

    public static string Word(StartupPhase phase) => phase switch
    {
        StartupPhase.Connecting => "Connecting",
        StartupPhase.CatchingUp => "Catching up",
        _ => "Syncing",
    };

    // The order the app names its services everywhere else (the Services filter, Settings).
    private static int Rank(string scope) => ServiceScopes.Name(scope) switch
    {
        "Google Messages" => 0,
        "Messenger" => 1,
        "Instagram" => 2,
        "Hermes" => 4,
        _ => 3,
    };
}

/// <summary>
/// Which services are reading what came while the app was closed right now (Google Messages' priority catch-up,
/// Instagram's catch-up), so the launch is not called settled in the middle of one. A backend wraps the task it starts in
/// Track; nothing else is asked of it. Thread-safe: catch-ups start and end on background threads, and the launch asks
/// from the interface thread.
/// </summary>
public sealed class CatchUpRuns
{
    public static CatchUpRuns Shared { get; } = new();
    private readonly Dictionary<string, int> running = new(StringComparer.Ordinal);

    public bool IsRunning(string scope) { lock (running) return running.GetValueOrDefault(scope) > 0; }

    /// <summary>Counts the catch-up as running from now until the task ends, however it ends, and returns the same task.</summary>
    public Task Track(string scope, Task work)
    {
        lock (running) running[scope] = running.GetValueOrDefault(scope) + 1;
        work.ContinueWith(_ =>
        {
            lock (running) { if (running.TryGetValue(scope, out var count) && count > 1) running[scope] = count - 1; else running.Remove(scope); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }
}
