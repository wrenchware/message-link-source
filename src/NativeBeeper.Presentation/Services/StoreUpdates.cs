namespace NativeBeeper.Presentation.Services;

// Microsoft Store updates for the packaged app (owner, 2026-11-14). A copy that starts with Windows and lives in the tray
// never exits by itself, and an MSIX update cannot replace a package in use, so without this it sat on its old version
// until the person signed out. The Store still finds, downloads and signs everything; this only asks it, and picks the
// moment: about once a day, a silent download, then an install once Message Link is in the tray and nothing is waiting
// to be sent, saved, signed in or backed up. The Windows side (StoreContext, the quit and the restart) is the App's
// WindowsStoreUpdates and App.StoreUpdates.cs; everything here runs in the harness against a fake store.

/// <summary>What the Store answered when asked for updates. <c>Outcome</c> is a log word: none, available, failed.</summary>
public sealed record StoreUpdateCheck(string Outcome, int Available = 0, int Mandatory = 0)
{
    public static readonly StoreUpdateCheck None = new("none");
    public static StoreUpdateCheck Failed(string why) => new("failed_" + why);
    public bool HasUpdates => Available > 0;
}

/// <summary>The Store's side, behind a port. Unknown or failed is never an update.</summary>
public interface IStoreUpdatePort
{
    Task<StoreUpdateCheck> CheckAsync(CancellationToken cancellationToken);
    /// <summary>A silent download of what <see cref="CheckAsync"/> found; a state word, <c>completed</c> when it is ready to install.</summary>
    Task<string> DownloadAsync(CancellationToken cancellationToken);
}

/// <summary>What the app is doing right now, as far as an update's restart is concerned. Read on the interface thread.</summary>
public sealed record UpdateQuietness(
    bool WindowShown,
    TimeSpan HiddenFor,
    bool Busy,
    bool SigningIn,
    bool Sending,
    bool UnsavedDrafts,
    bool BackupRunning,
    bool ShuttingDown);

public enum StoreUpdateStage { Idle, Checking, Downloading, Ready, Installing }

/// <summary>The timers, short in the harness.</summary>
public sealed record StoreUpdateTiming(TimeSpan FirstCheck, TimeSpan Interval, TimeSpan AfterFailure, TimeSpan QuietPoll, TimeSpan HiddenLongEnough)
{
    // The first check waits well past the launch, which is busy enough (owner: shortly after startup, not during it).
    // A failure waits hours, never seconds: the Store is asked at most a few times a day whatever it answers.
    public static readonly StoreUpdateTiming Default = new(
        FirstCheck: TimeSpan.FromMinutes(3), Interval: TimeSpan.FromHours(24), AfterFailure: TimeSpan.FromHours(6),
        QuietPoll: TimeSpan.FromMinutes(5), HiddenLongEnough: TimeSpan.FromMinutes(10));

    // An install that did not happen (it failed, or something of the package still ran) relaunches the app, whose fresh
    // run would otherwise try again within minutes, all day (code review, 2026-11-14). The App keeps the moment the failure
    // allows the next try in the data folder, and the next run's first check waits for it.
    public StoreUpdateTiming HoldingOff(TimeSpan remaining) => remaining > FirstCheck ? this with { FirstCheck = remaining } : this;
}

public static class StoreUpdatePolicy
{
    // What keeps an update's restart from happening now, as a log word, or null when nothing does. Asked by the schedule
    // (byPerson false), which also waits for the window to have been in the tray a while, so a person who just hid it is
    // not surprised by it coming back in a new version; and by Settings' Restart to update (byPerson true), whose window is
    // open by definition. Nothing here is ever overridden: a mandatory update waits for the same moment.
    public static string? Blocker(UpdateQuietness now, bool byPerson, TimeSpan hiddenLongEnough)
    {
        if (now.ShuttingDown) return "shutting_down";
        if (now.Sending) return "sending";
        if (now.BackupRunning) return "backup";
        if (now.SigningIn) return "signing_in";
        if (now.Busy) return "busy";
        if (now.UnsavedDrafts) return "unsaved";
        if (byPerson) return null;
        if (now.WindowShown) return "window_shown";
        if (now.HiddenFor < hiddenLongEnough) return "hidden_recently";
        return null;
    }

    // Settings' words for a Restart to update that has to wait.
    public static string Explain(string reason) => reason switch
    {
        "sending" => "Message Link is still sending. Try again when your messages have gone.",
        "backup" => "A backup or restore is running. Try again when it finishes.",
        "signing_in" => "A sign-in is in progress. Try again when it finishes.",
        "unsaved" => "A message or photo you haven't sent can't be kept over a restart. Send or remove it, then try again.",
        "busy" or "in_progress" => "Message Link is busy. Try again in a moment.",
        _ => "The update can't be installed right now. Try again later.",
    };
}

// The schedule: check, download, wait for a quiet moment, then hand over to the install, which closes the app.
public sealed class StoreUpdates
{
    private readonly IStoreUpdatePort port;
    private readonly Func<Task<UpdateQuietness>> quiet;
    // The App's install: null when the quit is under way (this run is ending), otherwise why it was refused.
    private readonly Func<string, Task<string?>> install;
    private readonly Action<string> log;
    private readonly StoreUpdateTiming timing;
    private string? lastDeferral;

    public StoreUpdates(IStoreUpdatePort port, Func<Task<UpdateQuietness>> quiet, Func<string, Task<string?>> install, Action<string> log, StoreUpdateTiming? timing = null)
    {
        this.port = port;
        this.quiet = quiet;
        this.install = install;
        this.log = log;
        this.timing = timing ?? StoreUpdateTiming.Default;
    }

    public StoreUpdateStage Stage { get; private set; }
    public int Available { get; private set; }
    public int Mandatory { get; private set; }
    /// <summary>Downloaded and waiting for its moment: what Settings offers Restart to update for.</summary>
    public bool IsReady => Stage == StoreUpdateStage.Ready;
    public event Action? Changed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timing.FirstCheck, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var wait = await CheckAndDownloadAsync(cancellationToken);
                if (wait is null) { await WaitForQuietAsync(cancellationToken); return; }
                await Task.Delay(wait.Value, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Null when an update is downloaded and ready; otherwise how long until the Store is asked again.
    private async Task<TimeSpan?> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        Set(StoreUpdateStage.Checking);
        StoreUpdateCheck check;
        try { check = await port.CheckAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) { check = StoreUpdateCheck.Failed(failure.GetType().Name); }
        log($"update check outcome={check.Outcome} available={check.Available} mandatory={check.Mandatory}");
        Available = check.Available;
        Mandatory = check.Mandatory;
        if (!check.HasUpdates)
        {
            Set(StoreUpdateStage.Idle);
            return check.Outcome.StartsWith("failed", StringComparison.Ordinal) ? timing.AfterFailure : timing.Interval;
        }
        Set(StoreUpdateStage.Downloading);
        string state;
        try { state = await port.DownloadAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) { state = "failed_" + failure.GetType().Name; }
        log($"update download outcome={state} available={check.Available}");
        if (state == "completed") { Set(StoreUpdateStage.Ready); return null; }
        Set(StoreUpdateStage.Idle);
        return timing.AfterFailure;
    }

    private async Task WaitForQuietAsync(CancellationToken cancellationToken)
    {
        // Until the install is under way, whether this loop or the person's Restart to update started it.
        while (!cancellationToken.IsCancellationRequested && Stage == StoreUpdateStage.Ready)
        {
            if (await TryInstallAsync(byPerson: false) is null) return;
            await Task.Delay(timing.QuietPoll, cancellationToken);
        }
    }

    /// <summary>Settings' Restart to update. Null when the restart is under way; otherwise the reason, for <see cref="StoreUpdatePolicy.Explain"/>.</summary>
    public Task<string?> RestartNowAsync() => IsReady ? TryInstallAsync(byPerson: true) : Task.FromResult<string?>("not_ready");

    // One attempt at a time: the schedule and the person's button could otherwise both pass the Ready check, each await
    // the quietness read, and both start a quit (code review, 2026-11-14). The second is told an attempt is under way.
    private int attempting;

    private async Task<string?> TryInstallAsync(bool byPerson)
    {
        if (Stage != StoreUpdateStage.Ready) return "not_ready";
        if (Interlocked.CompareExchange(ref attempting, 1, 0) != 0) return "in_progress";
        var origin = byPerson ? "person" : "quiet";
        string? reason;
        try
        {
            try { reason = StoreUpdatePolicy.Blocker(await quiet(), byPerson, timing.HiddenLongEnough); }
            catch (Exception failure) { reason = "unknown_" + failure.GetType().Name; }
            if (reason is null)
            {
                Set(StoreUpdateStage.Installing);
                try { reason = await install(origin); }
                catch (Exception failure) { reason = "failed_" + failure.GetType().Name; }
                // Refused by the quit itself (a draft it could not save, a window still busy): ready again for the next moment.
                if (reason is not null) Set(StoreUpdateStage.Ready);
            }
        }
        finally { Volatile.Write(ref attempting, 0); }
        // Said once per change, not every poll: a tray copy waiting all day for its window to close writes one line.
        if (reason is not null && (byPerson || reason != lastDeferral))
            log($"update install deferred reason={reason} origin={origin} mandatory={Mandatory}");
        if (!byPerson) lastDeferral = reason;
        return reason;
    }

    private void Set(StoreUpdateStage stage)
    {
        if (Stage == stage) return;
        Stage = stage;
        Changed?.Invoke();
    }
}
