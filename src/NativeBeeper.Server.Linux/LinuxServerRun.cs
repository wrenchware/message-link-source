using System.Diagnostics;
using NativeBeeper.App.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Host;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// One run of the Linux host: compose, open the door, restore when asked, and the one stop every ending goes through (SIGTERM, SIGINT, or the
/// door closing by itself). The door is a hook, so the transport is plugged in by whoever builds it and this file does not change.
/// </summary>
/// <param name="previousExit">The run marker's word for how the last run ended (<c>clean</c>, <c>unclean</c>, ...), which paces the restore (<see cref="RestoreLedger"/>).</param>
/// <param name="google">Google Messages, when the host runs it (<see cref="LinuxGoogle.Prepare"/>).</param>
internal sealed class LinuxServerRun(string root, HostKeys keys, LinuxServerOptions options, Action<string> log, string previousExit, LinuxGoogle? google = null)
{
    /// <summary>
    /// Serves clients until <c>stopping</c> is cancelled, then stops taking requests, gives the requests already taken up to
    /// <see cref="ShutdownBudget.Sends"/> to be answered, closes every connection and returns. It dispatches over <see cref="ProtocolHost.Services"/>
    /// and <see cref="ProtocolHost.Journal"/> (one journal for every connection), and logs <c>server listening</c> once it accepts. A hook that
    /// returns or throws before the stop ends the run.
    /// </summary>
    public delegate Task Serve(ProtocolHost host, CancellationToken stopping);

    /// <summary>
    /// A Tailscale auth key for a headless setup (SWITCHBOARD.md 10.8): read like the page's password, taken out of the environment before any helper starts and
    /// never kept in the options, and handed to the tailnet door alone, on its input (<see cref="TailnetDoor"/>).
    /// </summary>
    public const string TailnetAuthKeyVariable = "MESSAGES_TAILNET_AUTHKEY";

    private readonly CancellationTokenSource stopping = new();
    private RestoreLedger? ledger;
    private RestoreLedger Ledger => ledger ??= RestoreLedger.For(root, new FileKeyStateProtector(keys.StateKey), TimeProvider.System, log);
    private string reason = "none";
    private int stopRequested;

    /// <summary>
    /// The home network (SWITCHBOARD.md stage 4): TLS with a self-signed certificate the client pins, a one-time pairing code for a new device and a
    /// token per device, sealed with the host's own key. On the stop, new requests are refused and those already taken get the sends' bound. The server's own page is on
    /// when <c>MESSAGES_ADMIN_PASSWORD</c> is set (read here and handed on, never kept in the options, whose text would print it).
    /// </summary>
    /// <param name="restart">Stops the run so it starts again in this process (a restore or a reset from the page, applied before anything opens); false when it cannot.</param>
    public static Serve Network(LinuxServerOptions options, HostKeys keys, Action<string> log, Func<string, string?>? environment = null, Func<string, bool>? restart = null) => async (host, stop) =>
    {
        // The owner's tailnet, when asked for (SWITCHBOARD.md 4.7): the host also listens on a relay socket that only the door beside it reaches.
        var relay = options.Tailnet ? TailnetDoor.RelaySocket : null;
        // What the door reports, for the server's page: made before both, since the page exists before the door starts. Without the door, the page says how to turn it on.
        var tailnet = new NativeBeeper.Host.Network.TailnetBoard(options.Tailnet, options.Port);
        // Notifications to phones (SWITCHBOARD.md 7.5): the owner's Firebase project, from two files. The server's page keeps them in the keys volume (one is the
        // project's private key, and nothing on the data volume is left readable); files put by hand in the data volume's push folder are read when the keys
        // volume has none. MESSAGES_PUSH names one folder for both.
        var named = (environment ?? Environment.GetEnvironmentVariable)("MESSAGES_PUSH") is { Length: > 0 } chosen ? chosen : null;
        var kept = named ?? Path.Combine(keys.Folder, "push");
        var pushFrom = named ?? (File.Exists(Path.Combine(kept, NativeBeeper.Host.Network.FirebaseTransport.ServiceAccountFile))
            && File.Exists(Path.Combine(kept, NativeBeeper.Host.Network.FirebaseTransport.AppSettingsFile)) ? kept : Path.Combine(options.DataRoot, "push"));
        var protector = new FileKeyStateProtector(keys.StateKey);
        // The page's Backups card, restore and reset (stage 10), and the record of the restore this server was made from, if it was.
        var backups = host.Services.Backup is NativeBeeper.Host.Backup.ServerBackupService backup && host.Services.Admin is { } admin
            ? new NativeBeeper.Host.Network.ServerPageBackups(backup, async ct => (await admin.AccountsAsync(ct).ConfigureAwait(false)).Count, ct => SignOutAllAsync(admin, log, ct),
                restart ?? (_ => false))
            : null;
        var restored = await NativeBeeper.Host.Backup.RestoredMark.LoadAsync(Path.Combine(options.DataRoot, NativeBeeper.Host.Backup.RestoredMark.FileName), protector).ConfigureAwait(false);
        var network = await NativeBeeper.Host.Network.NetworkHost.StartAsync(host,
            new NativeBeeper.Host.Network.NetworkHostOptions(options.DataRoot, protector, log)
            {
                Backups = backups, Restored = restored,
                Port = options.Port, HostNames = options.HostNames, Pair = options.Pair,
                // The certificate's key and the devices' generation live with the keys, off the data volume.
                KeysFolder = keys.Folder,
                // A live code goes to the container's output (docker logs) and never into server.log on the data volume.
                ShowCode = Show,
                PagePassword = (environment ?? Environment.GetEnvironmentVariable)("MESSAGES_ADMIN_PASSWORD"),
                // Without that password, the page sets itself up with a setup code printed to the container's output (SWITCHBOARD.md 10.4).
                FirstRun = true,
                RelaySocket = relay,
                Tailnet = tailnet,
                Push = NativeBeeper.Host.Network.FirebaseTransport.TryLoad(pushFrom, log),
                PushFolder = kept,
            }, stop).ConfigureAwait(false);
        await using (network.ConfigureAwait(false))
        {
            log("server listening transport=network");
            // Its sign-in link and Tailnet Lock key are shown as a code is; its node's state is kept with the keys.
            var door = relay is null ? null
                : TailnetDoor.Start(Path.Combine(AppContext.BaseDirectory, TailnetDoor.Executable), Path.Combine(keys.Folder, TailnetDoor.StateFolder), relay,
                    options.TailnetName, options.Port, log, Show, board: tailnet,
                    authKey: (environment ?? Environment.GetEnvironmentVariable)(TailnetAuthKeyVariable), defaultName: options.TailnetNameGiven is null);
            try { await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await network.StopAsync(ShutdownBudget.Normal.Sends).ConfigureAwait(false);
            // After the host's drain, whose answers to tailnet devices go out through the door.
            if (door is not null) await door.StopAsync().ConfigureAwait(false);
        }
    };

    private static void Show(string line) => Console.Out.Write($"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n");

    // Reset server (stage 10): every account the server can remove is removed, as the page removes one (each through its own sign-out); Google Messages, which is unpaired on
    // the phone, and an account in the middle of a move are left for the reset itself, which deletes what the server keeps of them. A removal that fails stops the reset.
    private static async Task<(int SignedOut, int Left, bool Failed)> SignOutAllAsync(NativeBeeper.Protocol.IServerAdmin admin, Action<string> log, CancellationToken cancellationToken)
    {
        int signedOut = 0, left = 0;
        foreach (var account in await admin.AccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!account.Removable) { left++; continue; }
            try { if ((await admin.RemoveAccountAsync(account.Provider, account.Key, cancellationToken).ConfigureAwait(false)).Removed) signedOut++; }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                log($"server reset sign_out failed provider={account.Provider} type={failure.GetType().Name}");
                return (signedOut, left, true);
            }
        }
        return (signedOut, left, false);
    }

    /// <summary>No transport: the host is composed and restored, and nothing can reach it (development, and the harness).</summary>
    public static Serve NoTransport(Action<string> log) => async (_, stop) =>
    {
        log("server listening transport=none");
        try { await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    };

    /// <summary>A backup did not stop within <see cref="BackupStopWait"/> as this run stopped: a restore or a reset waits for a later start.</summary>
    public bool BackupStillRunning { get; private set; }
    internal static readonly TimeSpan BackupStopWait = TimeSpan.FromSeconds(60);

    /// <summary>Whether this run ended to start again in the same process (a restore or a reset chosen on the page), rather than to stop.</summary>
    public bool RestartRequested => Volatile.Read(ref restartRequested) == 1;
    private int restartRequested;

    /// <summary>Stops this run so that the process composes the server again, applying a restore or a reset before anything opens. True: the stop has begun.</summary>
    public bool RequestRestart(string why)
    {
        if (Volatile.Read(ref stopRequested) == 1) return false;
        Interlocked.Exchange(ref restartRequested, 1);
        RequestStop(why);
        return true;
    }

    /// <summary>Begins the stop, once; a second request (a second Ctrl+C) is only logged, since the stop is already bounded.</summary>
    public void RequestStop(string why)
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 1) { log($"server stop already_running reason={why}"); return; }
        Volatile.Write(ref reason, why);
        stopping.Cancel();
    }

    /// <summary>Runs until stopped. 0 for a stop that was asked for, 1 when the start or the door failed.</summary>
    public async Task<int> RunAsync(Serve serve)
    {
        LinuxHostStartup? composed = null;
        Task restoring = Task.CompletedTask, serving = Task.CompletedTask, backingUp = Task.CompletedTask;
        var code = 0;
        try
        {
            var clock = Stopwatch.StartNew();
            composed = LinuxHostStartup.Compose(root, keys, log, options.Restore, google, options.Backups(Directory.Exists));
            // One ledger for the start's restore and for the first login of an account that moved here.
            composed.UsePacing(Ledger.PermitAsync);
            log($"server ready ms={clock.ElapsedMilliseconds}");
            if (!stopping.IsCancellationRequested)
            {
                // The door opens before the providers connect, so a client sees the state change as the restore goes.
                serving = Task.Run(() => serve(composed.Host, stopping.Token));
                if (options.Restore) restoring = PacedRestoreAsync(composed, stopping.Token);
                else composed.EndRestores();
                // The daily backup, beside the history walk: its first look comes minutes after the start, at below-normal priority.
                backingUp = Task.Run(() => composed.Backup.RunScheduleAsync(stopping.Token));
                try
                {
                    await serving.WaitAsync(stopping.Token).ConfigureAwait(false);
                    // The door closed without being asked to.
                    code = 1;
                    RequestStop("transport_ended");
                }
                catch (Exception failure) when (!stopping.IsCancellationRequested)
                {
                    log($"server transport failed type={failure.GetType().Name}");
                    code = 1;
                    RequestStop("transport_failed");
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception failure)
        {
            // The type alone: an exception's message can carry what was being opened.
            log($"server start failed type={failure.GetType().Name}");
            code = 1;
            RequestStop("start_failed");
        }
        await StopAsync(composed, restoring, serving, backingUp).ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// The start's restore, held as long as <see cref="RestoreLedger"/> says (each restore logs Messenger and Instagram in again, and Docker restarts a crashing container
    /// within seconds). While it is held, a client asking for the restore is answered skipped rather than left waiting or let past the pacing; the feed and
    /// the restore follow when the wait is over, if the host is still running.
    /// </summary>
    private async Task PacedRestoreAsync(LinuxHostStartup composed, CancellationToken stop)
    {
        var ledger = Ledger;
        // Only a restore that logs Messenger or Instagram in is counted against the hour; any start still pays the unclean back-off.
        var counted = composed.RestoresAccounts;
        RestoreLedger.Decision decision;
        try { decision = await ledger.StartAsync(previousExit, counted).ConfigureAwait(false); }
        catch (Exception failure)
        {
            // Not knowing is not permission: no restore this run, and nobody left waiting for one.
            log($"server restore skipped reason=paced error={failure.GetType().Name}");
            composed.EndRestores();
            return;
        }
        // A count that cannot be written (a full disk, a read-only folder) is no limit at all: the next start would find the cap and the back-off gone. Fail
        // closed: no restore this run. The host still serves, and a person can sign in or reconnect from a client.
        if (!decision.Saved)
        {
            log("server restore skipped reason=ledger_not_saved");
            composed.EndRestores();
            return;
        }
        if (decision.Wait > TimeSpan.Zero)
        {
            log($"server restore held reason=paced wait_s={(long)Math.Ceiling(decision.Wait.TotalSeconds)} unclean={decision.Unclean} recent={decision.Recent}");
            composed.EndRestores();
            try { await Task.Delay(decision.Wait, stop).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                foreach (var provider in composed.Restoring) log($"server restore provider={provider} outcome=stopped");
                return;
            }
        }
        if (!counted) log("server restore counted=no reason=no_meta_account");
        else if (!await ledger.RecordAsync().ConfigureAwait(false))
        {
            log("server restore skipped reason=ledger_not_saved");
            composed.EndRestores();
            return;
        }
        await composed.RestoreAsync(stop).ConfigureAwait(false);
    }

    // The Windows server's stop, in its order: refuse new helpers and stop the restore, close the door (the transport drains its clients on the
    // sends bound), then close the providers within their bounds (BackendShutdown: a send already on the wire gets its own bound, and is never resent).
    private async Task StopAsync(LinuxHostStartup? composed, Task restoring, Task serving, Task backingUp)
    {
        var clock = Stopwatch.StartNew();
        var budget = ShutdownBudget.Normal;
        log($"server stopping reason={Volatile.Read(ref reason)}");
        HelperConnection.RefuseStarts = true;
        try { await restoring.WaitAsync(budget.Sends).ConfigureAwait(false); }
        catch (TimeoutException) { log("server stop restore_still_running"); }
        catch (Exception) { }
        // The hook's own drain is the sends bound; past twice that it is not waited for, so a stuck transport cannot hold the providers open.
        try { await serving.WaitAsync(budget.Sends + budget.Sends).ConfigureAwait(false); }
        catch (TimeoutException) { log("server stop transport_still_running"); }
        catch (Exception failure) when (failure is not OperationCanceledException) { log($"server stop transport_error={failure.GetType().Name}"); }
        catch (OperationCanceledException) { }
        // A backup being written stops with the run and leaves no file behind (its temporary is removed); its end is waited for, on the sends bound.
        // And one started by hand (Back up now, Check latest): stopped and waited for, so a restore or a reset applied after this run never runs beside it.
        composed?.Backup.Stop();
        try { await backingUp.WaitAsync(budget.Sends).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (Exception) { }
        if (composed is not null && !await composed.Backup.IdleAsync(BackupStopWait).ConfigureAwait(false))
        {
            log("server stop backup_still_running");
            BackupStillRunning = true;
        }
        if (composed is not null)
        {
            // Chrome (when the server runs one) closes first and gently, so its cookies and state are on disk; it gets its own bound.
            await composed.StopGoogleBrowserAsync().ConfigureAwait(false);
            try
            {
                composed.Host.StopPublishing();
                await BackendShutdown.StopAsync(composed.Services, budget).ConfigureAwait(false);
            }
            catch (Exception failure) { log($"server stop error={failure.GetType().Name}"); }
            try { composed.Dispose(); } catch (Exception) { }
        }
        log($"server stopped ms={clock.ElapsedMilliseconds}");
    }
}
