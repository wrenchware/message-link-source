using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services;
using MessageLink.Core.Services;
using MessageLink.GoogleMessages.Spike;
using MessageLink.Server;
using MessageLink.Protocol;

namespace MessageLink.Server.Windows;

/// <summary>One run of the server: the composition it starts once the window is pumping, and the stop every path ends in (a quit, Restart Manager, the end of the session).</summary>
/// <param name="previousExit">The run marker's word for how the last run ended (<c>clean</c>, <c>unclean</c>, ...), which paces the restore (<see cref="RestoreLedger"/>).</param>
internal sealed class ServerRun(ServerOptions options, string previousExit = "clean") : IServerLifecycle
{
    private HostStartup? startup;
    private ServerPipe? pipe;
    private ServerNetwork? network;
    private OwnerWatch? owner;
    private PackageWatch? packageWatch;
    private Task starting = Task.CompletedTask, networking = Task.CompletedTask, backingUp = Task.CompletedTask;
    private readonly CancellationTokenSource stop = new();
    private int exitCode;

    /// <summary>How long a server whose owner has gone waits for a client to come back before it stops. Set before <see cref="Start"/>.</summary>
    public TimeSpan OwnerGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Non-zero when the run ended because it could not start (the pipe was taken); the process returns it.</summary>
    public int ExitCode => Volatile.Read(ref exitCode);

    private ServerShell? shell;
    private int restartRequested, sessionQueried, sessionEnded;

    /// <summary>Whether the run ended for a reset or a restore chosen on the page, which the next start applies before anything opens (PcServerRestore); never while Windows is ending the session.</summary>
    public bool RestartRequested => Volatile.Read(ref restartRequested) == 1 && Volatile.Read(ref sessionQueried) == 0 && Volatile.Read(ref sessionEnded) == 0;

    // The page's Restart: this server stops, and the window open on this root launches again, or with none open this process starts the next server (Program).
    private bool RequestRestart(string why)
    {
        if (Volatile.Read(ref shell) is not { } running || stop.IsCancellationRequested) return false;
        Interlocked.Exchange(ref restartRequested, 1);
        running.Stop("restart_" + why);
        return true;
    }

    /// <summary>Composes the host off the shell's thread, so the window answers a quit while a slow start is under way.</summary>
    public void Start(ServerShell shell)
    {
        Volatile.Write(ref this.shell, shell);
        packageWatch = PackageWatch.Start(shell.StopForPackage, AppLog.Write);
        // Before the composition, so an owner that is already gone is found at once; the grace is what waits for a client.
        if (options.OwnerPid is { } ownerPid)
        {
            owner = new OwnerWatch(OwnerWatch.WatchProcess(ownerPid, AppLog.Write), () => pipe?.Connections ?? 0, shell.Stop, AppLog.Write) { Grace = OwnerGrace };
            owner.Start();
        }
        var queued = new StepClock(AppLog.Write);
        starting = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                queued.Took("start");
                var composed = HostStartup.Compose(AppLog.Write, shell.Sta ?? throw new InvalidOperationException("no_sta"), options.Connects);
                Volatile.Write(ref startup, composed);
                AppLog.Write($"server ready ms={clock.ElapsedMilliseconds}");
                // A stop that came while composing opens nothing.
                if (stop.IsCancellationRequested) return;
                if (!await ServeAsync(composed, shell)) return;
                if (options.Connects && !stop.IsCancellationRequested)
                {
                    // The page and the phones' door (SWITCHBOARD.md 10.7), beside the restore rather than ahead of it: this PC's own window is on the pipe already.
                    networking = Task.Run(() => StartNetworkAsync(composed));
                    // The daily backup into the person's folder, its first look minutes after the start, at below-normal priority.
                    if (composed.Backup is { } backup) backingUp = Task.Run(() => backup.RunScheduleAsync(stop.Token));
                }
                // A stop that came while composing starts nothing: only a restore connects the providers, and none begins once stopping has.
                if (options.Connects && !stop.IsCancellationRequested) await PacedRestoreAsync(composed, stop.Token);
                // A restore that will not run: a client that asked for one while the door was open is answered, not left waiting.
                else composed.EndRestores();
            }
            catch (Exception failure)
            {
                // The type alone: an exception's message can carry what was being opened.
                AppLog.Write($"server start failed type={failure.GetType().Name}");
                shell.Stop("start_failed");
            }
        });
    }

    /// <summary>What a backup being written gets to stop when the server does: inside the slack of <see cref="ServerStopBounds.WorstCase"/>.</summary>
    internal static readonly TimeSpan BackupStopWait = TimeSpan.FromSeconds(3);

    private static async Task StopPipeAsync(ServerPipe served, TimeSpan drain)
    {
        try { await served.StopAsync(drain); }
        catch (Exception failure) { AppLog.Write($"server stop pipe_error={failure.GetType().Name}"); }
    }

    // A start of the network still under way is waited for first, within the drain, then its clients get the same drain and the tailnet door closes its node.
    private async Task StopNetworkAsync(TimeSpan drain)
    {
        try { await networking.WaitAsync(drain); } catch (Exception) { }
        if (Volatile.Read(ref network) is not { } opened) return;
        try { await opened.StopAsync(drain); }
        catch (Exception failure) { AppLog.Write($"server stop network_error={failure.GetType().Name}"); }
    }

    // The network door and page; a failure leaves the pipe serving (ServerNetwork says why). A stop that began meanwhile closes what opened.
    private async Task StartNetworkAsync(HostStartup composed)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var opened = await ServerNetwork.StartAsync(composed.Host, composed.Backup, AppPaths.DataRoot, composed.Services.GetRequiredService<IStateProtector>(), AppLog.Write, stop.Token,
                RequestRestart);
            Volatile.Write(ref network, opened);
            AppLog.Write($"server network ms={clock.ElapsedMilliseconds} outcome={(opened is null ? "off" : "on")}");
            if (opened is not null && stop.IsCancellationRequested) await opened.StopAsync(TimeSpan.Zero);
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { AppLog.Write($"server network failed type={failure.GetType().Name}"); }
    }

    /// <summary>
    /// The start's restore, held as long as <see cref="RestoreLedger"/> says (SWITCHBOARD.md 3.3: Instagram's pacing persisted, so a server that keeps crashing, or a PC that
    /// signs in and out, never logs Messenger and Instagram in faster than the Linux host may). The door is open meanwhile; a client asking for the restore is answered
    /// skipped. Only a restore that would log Messenger or Instagram in is counted against the hour; a ledger that cannot be read or written means no restore this run.
    /// </summary>
    private async Task PacedRestoreAsync(HostStartup composed, CancellationToken stopping)
    {
        // This root's own, beside the Linux host's name for it (<root>\Server\restores.protected), sealed with DPAPI like the server's other files.
        var ledger = RestoreLedger.For(AppPaths.DataRoot, composed.Services.GetRequiredService<IStateProtector>(), TimeProvider.System, AppLog.Write);
        var counted = composed.RestoresAccounts;
        RestoreLedger.Decision decision;
        try { decision = await ledger.StartAsync(previousExit, counted); }
        catch (Exception failure)
        {
            AppLog.Write($"server restore skipped reason=paced error={failure.GetType().Name}");
            composed.EndRestores();
            composed.OpenPassiveFeed();
            return;
        }
        if (!decision.Saved)
        {
            AppLog.Write("server restore skipped reason=ledger_not_saved");
            composed.EndRestores();
            composed.OpenPassiveFeed();
            return;
        }
        if (decision.Wait > TimeSpan.Zero)
        {
            AppLog.Write($"server restore held reason=paced wait_s={(long)Math.Ceiling(decision.Wait.TotalSeconds)} unclean={decision.Unclean} recent={decision.Recent}");
            composed.EndRestores();
            // Every feed but Google's goes meanwhile, so an account a client connects is heard; the restore lets Google's go after the wait.
            composed.OpenPassiveFeed();
            try { await Task.Delay(decision.Wait, stopping); }
            catch (OperationCanceledException) { return; }
        }
        if (!counted) AppLog.Write("server restore counted=no reason=no_meta_account");
        else if (!await ledger.RecordAsync())
        {
            AppLog.Write("server restore skipped reason=ledger_not_saved");
            composed.EndRestores();
            composed.OpenPassiveFeed();
            return;
        }
        await composed.RestoreAsync(stopping);
    }

    // The clients' door, open before the providers connect so a client sees the state as the restore changes it. False when the pipe's name was
    // taken, which has stopped the server.
    private async Task<bool> ServeAsync(HostStartup composed, ServerShell shell)
    {
        var step = new StepClock(AppLog.Write);
        var shutdown = new ServerShutdown(options.OwnerPid is not null, RefuseQuit, shell.Stop, AppLog.Write);
        var token = new ClientToken(AppPaths.Combine("Server"), composed.Services.GetRequiredService<IStateProtector>());
        step.Took("token");
        // The stop is the pipe's own question too: a connection accepted after the stop began is never served (it is told the server is closing).
        var served = new ServerPipe(PipeEndpoint.PipeNameForCurrentUser(AppPaths.InstanceSuffix), token, composed.Host.Services with { Shutdown = shutdown },
            composed.Host.Journal, composed.Host.Version, AppLog.Write, () => stop.IsCancellationRequested);
        served.ConnectionsChanged += () => owner?.ConnectionsChanged();
        var opened = await served.StartAsync();
        step.Took("pipe");
        if (!opened)
        {
            Volatile.Write(ref exitCode, 3);
            shell.Stop("pipe_taken");
            return false;
        }
        Volatile.Write(ref pipe, served);
        // A stop that began while the door was opening: the stop looked for the pipe before it was there, so it is told now.
        if (stop.IsCancellationRequested) served.BeginDraining();
        return true;
    }

    // Mutations the providers have made and not yet heard back from, and requests the host has begun and not yet answered (a send, a sign-in, a
    // restore a client asked for): a quit now would leave their outcome unknown. The journal's count is what the host itself is waiting on; the
    // helpers' is what has reached a provider.
    public string? RefuseQuit() => HelperConnection.MutationsInFlight > 0 || Volatile.Read(ref startup)?.Host.Journal.RunningCount > 0 ? "busy" : null;

    // The scopes the last published state says are connected: one per account (each Google Messages and Voice account, Messenger, Instagram, Hermes).
    public int? ConnectedAccounts() => Volatile.Read(ref startup)?.Host.StatePublisher.Latest?.State.Scopes.Count(scope => scope.Connected);

    public bool PageReady() => Volatile.Read(ref network) is not null && !stop.IsCancellationRequested;

    public void SessionQuery()
    {
        Interlocked.Exchange(ref sessionQueried, 1);
        AppLog.Write("session end query");
        RunMarker.MarkSessionEnding(RunMarkerPath);
    }

    public void SessionCancel()
    {
        Interlocked.Exchange(ref sessionQueried, 0);
        RunMarker.MarkRunning(RunMarkerPath);
        AppLog.Write("session end cancelled");
    }

    public void SessionEnd() => Interlocked.Exchange(ref sessionEnded, 1);

    public async Task StopAsync(string reason, ShutdownBudget budget)
    {
        var clock = Stopwatch.StartNew();
        AppLog.Write($"server stopping reason={reason}");
        // Before anything is waited for: a restore under way starts no more helpers and ends at its next check, so the wait below is short and
        // the shutdown never races a provider that is still starting. The providers are closed after the restore, not under it.
        stop.Cancel();
        // The very first act: from here a request on any connection is refused (unavailable, before the journal sees it), so nothing new starts while the
        // restore is waited for below. The ones already accepted go on to their answers. A connection accepted after this is told the same.
        Volatile.Read(ref pipe)?.BeginDraining();
        HelperConnection.RefuseStarts = true;
        owner?.Dispose();
        packageWatch?.Dispose();
        try { await starting.WaitAsync(budget.Sends); }
        catch (Exception failure) when (failure is TimeoutException or OperationCanceledException) { AppLog.Write("server stop restore_still_running"); }
        catch (Exception) { }
        // After the start has ended, so a pipe still being opened is not missed. No new client; then the ones connected are given the time sends get
        // to have the answers to what they already asked written (the request that began this stop is one of them); and only then are the providers
        // closed, so nothing a client sent is cut off by the providers going first.
        // The network door (SWITCHBOARD.md 10.7) closes beside the pipe and within the pipe's own bound (its drain, then the tailnet door), so the stop's worst case
        // (ServerStopBounds.WorstCase) is unchanged by it.
        var pipeStop = Volatile.Read(ref pipe) is { } served ? StopPipeAsync(served, budget.Sends) : Task.CompletedTask;
        var networkStop = StopNetworkAsync(budget.Sends);
        try { await Task.WhenAll(pipeStop, networkStop).WaitAsync(budget.Sends + PipeHost.WorstStop); }
        catch (TimeoutException) { AppLog.Write("server stop doors_still_closing"); }
        catch (Exception) { }
        // A backup being written stops with the run and leaves no file behind (its temporary is removed), within the stop's slack.
        if (Volatile.Read(ref startup)?.Backup is { } backup)
        {
            backup.Stop();
            try { await backingUp.WaitAsync(BackupStopWait); } catch (Exception) { }
            if (!await backup.IdleAsync(TimeSpan.Zero)) AppLog.Write("server stop backup_still_running");
        }
        if (Volatile.Read(ref startup) is { } composed)
        {
            try
            {
                composed.Host.StopPublishing();
                await BackendShutdown.StopAsync(composed.Services, budget);
            }
            catch (Exception failure) { AppLog.Write($"server stop error={failure.GetType().Name}"); }
            try { composed.Dispose(); } catch (Exception) { }
        }
        RunMarker.End(RunMarkerPath);
        AppLog.Write($"server stopped ms={clock.ElapsedMilliseconds}");
        Program.FlushLogs();
    }

    private static string RunMarkerPath => AppPaths.Combine("server-run.marker");
}
