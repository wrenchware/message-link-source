using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Backup;

/// <summary>
/// The server's own backup (SWITCHBOARD.md stage 10, step 10.2, D7 and D8): one encrypted file a day into the folder its host maps (<c>/backups</c> in Docker), with
/// the server's data and identity and never a service's sign-in, on the PC backup's rules (<see cref="BackupSettings"/>): every 24 hours, an hour after a failure, the
/// newest read back a day after it was written and weekly after that, five kept under the server's own tag (the newest holding each Messenger account's history never
/// pruned), at below-normal priority and never in the first minutes after a start.
/// </summary>
/// <remarks>
/// The password is sealed with the server's own key (<c>Backup/password.protected</c>) so the schedule runs unattended; it is never logged, and where it is set the page
/// says that a lost password means the backups are lost. A scheduled backup waits up to <see cref="MessengerPatience"/> for a Messenger that is signed in but not
/// connected, then goes without its history. Each change in how the backup stands raises <see cref="Changed"/> (the host's <c>backup</c> signal), and two days without a
/// good backup while backups are on raises <see cref="StaleNotice"/> once (a push to the phones).
/// Logged: <c>server backup written|failed|verify|cancelled|schedule ...</c> with fixed words, counts and milliseconds only.
/// </remarks>
internal sealed class ServerBackupService : IServerBackup
{
    /// <summary>How long without a good backup, while backups are on, before the phones are told.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);
    private static readonly TimeSpan TemporaryAge = TimeSpan.FromHours(1);

    private readonly ServerBackupPaths paths;
    private readonly IStateProtector protector;
    // The host's mapping on Linux, fixed for the run; on Windows the person's choice, which ChooseFolder changes (volatile: read by the schedule and the page).
    private volatile string? folder;
    private readonly IServerBackupMessenger? messenger;
    private readonly string app;
    private readonly Action<string> log;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly object gate = new();
    private BackupSettings settings;
    private DateTimeOffset? waitingForMessenger;
    private ServerBackupStatus? lastRaised;
    // Every backup and check, the ones started by hand included, stops with the server (Stop, or the schedule's token).
    private readonly CancellationTokenSource stopped = new();
    private CancellationToken lifetime => stopped.Token;
    private volatile bool busy, checking;

    /// <param name="folder">Where backups go (<c>MESSAGES_BACKUPS</c>, or <c>/backups</c> when that is mapped); null leaves backups off, and the page says how to map one.</param>
    public ServerBackupService(ServerBackupPaths paths, IStateProtector protector, string? folder, IServerBackupMessenger? messenger, string app, Action<string> log,
        TimeProvider? time = null)
    {
        this.paths = paths; this.protector = protector; this.folder = folder; this.messenger = messenger; this.app = app; this.log = log; this.time = time ?? TimeProvider.System;
        // A server's backups are on unless its page turned them off: a mapped folder is the owner's request for them, and nothing runs before a password is set.
        settings = (File.Exists(paths.BackupSettings) ? BackupSettings.Load(paths.BackupSettings) : new BackupSettings(Enabled: true)) with { Folder = folder };
    }

    /// <summary>The wait before the schedule first looks (well clear of the start), and between looks. The harness makes them short.</summary>
    public TimeSpan FirstCheck { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long a scheduled backup waits for a Messenger signed in but not connected before it goes without that history.</summary>
    public TimeSpan MessengerPatience { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Raised (on a worker) with the new status whenever how the backup stands changes.</summary>
    public event Action<ServerBackupStatus>? Changed;

    /// <summary>Told once when two days have gone without a good backup while backups are on, and again only after a good one; it answers whether anyone was told
    /// (no phone set up for notifications is not telling), and is asked again at the next look until someone is.</summary>
    public Func<CancellationToken, Task<bool>>? StaleNotice { get; set; }

    /// <summary>The key the shared state is filed under now, and the shared state's import (a PC backup's choices wait for both, <see cref="PendingImports"/>).</summary>
    public Func<CancellationToken, Task<string?>>? CurrentKey { get; set; }
    public Func<MoveBundle, CancellationToken, Task<(string Status, int Added, int Skipped, int Refused)>>? ImportBundle { get; set; }

    public string? Folder => folder;
    public ServerBackupPaths Paths => paths;

    /// <summary>
    /// Where the person's choice of folder is kept, on a host whose folder is that choice (the Windows server, SWITCHBOARD.md 10.7, D7: any folder its user can write
    /// to, a mapped drive, <c>\\nas\share</c> or OneDrive included); null on a host whose folder is its mapping (Docker's <c>/backups</c>), which the page cannot change.
    /// </summary>
    public string? FolderChoiceFile { get; init; }

    /// <summary>The folder kept in <paramref name="file"/>, or null when there is none (or it cannot be read).</summary>
    public static string? ReadFolderChoice(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 4096) return null;
            var text = File.ReadAllText(file).Trim();
            return text.Length > 0 && Path.IsPathFullyQualified(text) ? text : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Backups go to <paramref name="chosen"/> from the next one on: a full path, on a drive or share that is there now. A folder that is not there yet is made (only here: a Windows
    /// path names its drive or share, so one that is not mounted fails rather than filling the PC's own disk, and a Docker server's folder is its mapping), and is checked by writing to it.
    /// Kept for the next start. Answers <c>chosen</c>, or with nothing changed why not: <c>fixed</c> (this host's folder is its mapping), <c>not_full</c>, <c>missing</c>, <c>denied</c> or <c>unsaved</c>.
    /// </summary>
    public string ChooseFolder(string? chosen)
    {
        if (FolderChoiceFile is not { } file) return "fixed";
        chosen = chosen?.Trim().Trim('"');
        if (string.IsNullOrEmpty(chosen) || chosen.Length > 1024 || !Path.IsPathFullyQualified(chosen)) return "not_full";
        try
        {
            chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
            if (Path.GetPathRoot(chosen) is not { Length: > 0 } root || !Directory.Exists(root)) return "missing";
            Directory.CreateDirectory(chosen);
            var probe = Path.Combine(chosen, ".messages-write-check");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, chosen);
            File.Move(temporary, file, overwrite: true);
        }
        catch (UnauthorizedAccessException) { return "denied"; }
        catch (Exception failure) when (failure is IOException or ArgumentException or NotSupportedException) { return "unsaved"; }
        folder = chosen;
        Update(current => current);
        log("server backup folder chosen");
        return "chosen";
    }
    public bool HasPassword => File.Exists(paths.BackupPassword);
    public bool IsBusy => busy;
    public BackupSettings Settings { get { lock (gate) return settings; } }

    public Task<ServerBackupStatus> StatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status());

    /// <summary>How the backup stands now (<see cref="ServerBackupStatus"/>).</summary>
    public ServerBackupStatus Status()
    {
        var current = Settings;
        var now = time.GetUtcNow();
        var hasFolder = folder is not null;
        var hasPassword = HasPassword;
        var state = busy ? ServerBackupStates.Running
            : !hasFolder ? ServerBackupStates.NoFolder
            : !current.Enabled ? ServerBackupStates.Off
            : !hasPassword ? ServerBackupStates.NoPassword
            : current.LastFailed ? ServerBackupStates.Failing
            : ServerBackupStates.On;
        int kept = 0;
        if (hasFolder && BackupFolder.IsInstallTag(current.Install))
            try { kept = Directory.GetFiles(folder!).Count(file => BackupFolder.IsOwnBackupName(Path.GetFileName(file), current.Install!)); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        return new ServerBackupStatus(state, current.Enabled, hasFolder, hasPassword, current.LastSuccess, current.NextDue(now, hasPassword), current.LastBytes,
            current.LastFailed ? current.LastFailure : null, current.LastFailed ? current.LastFailureAt : null, current.LastVerifyResult, current.LastVerifiedAt,
            IsStale(current, now, hasFolder, hasPassword), kept);
    }

    // On, with somewhere to write and a password, and nothing good for two days: since the last good backup, or since the password was set when there has been none.
    private static bool IsStale(BackupSettings current, DateTimeOffset now, bool hasFolder, bool hasPassword) =>
        current.Enabled && hasFolder && hasPassword && (current.LastSuccess ?? current.PasswordSetAt) is { } since && now - since > StaleAfter;

    public Task<ServerBackupStarted> RunAsync(CancellationToken cancellationToken = default)
    {
        if (folder is null) return Task.FromResult(new ServerBackupStarted(false, ServerBackupFailures.NoFolder));
        if (!HasPassword) return Task.FromResult(new ServerBackupStarted(false, ServerBackupFailures.NoPassword));
        if (busy || running.CurrentCount == 0) return Task.FromResult(new ServerBackupStarted(false, ServerBackupFailures.Busy));
        // Answered at once; the backup's end is told by the status changing.
        _ = Task.Run(() => BackUpAsync(scheduled: false, lifetime));
        return Task.FromResult(new ServerBackupStarted(true));
    }

    /// <summary>A backup now, awaited (the page's Back up now and the harness).</summary>
    public Task<BackupRun> BackUpNowAsync() => BackUpAsync(scheduled: false, lifetime);

    /// <summary>Reads the newest backup back now, restoring nothing.</summary>
    public Task<string> CheckNowAsync() => VerifyAsync(scheduled: false, lifetime);

    public async Task SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        if (password is not { Length: >= ServerBackupLimits.MinPassword and <= ServerBackupLimits.MaxPassword }) throw new ArgumentException("The password must be 8 to 256 characters.", nameof(password));
        var plain = Encoding.UTF8.GetBytes(password);
        try { await ServerRestore.WritePrivateAsync(paths.BackupPassword, await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        // As the PC's: a failure that was about the password is no longer the latest word, and backups older than this are sealed under the previous one.
        Update(current =>
        {
            var next = current with { PasswordSetAt = time.GetUtcNow() };
            return next.LastFailure == "password" ? next with { LastFailure = null, LastFailureAt = null } : next;
        });
        log("server backup password set");
    }

    /// <summary>Stops every backup and check, the ones started by hand included; a backup stopped part way leaves no file behind.</summary>
    public void Stop() => stopped.Cancel();

    /// <summary>Whether no backup or check is running, waiting up to <paramref name="wait"/> for one to end: a restore or a reset never runs beside one.</summary>
    public async Task<bool> IdleAsync(TimeSpan wait)
    {
        if (!await running.WaitAsync(wait).ConfigureAwait(false)) return false;
        running.Release();
        return true;
    }

    /// <summary>Keeps a restore the person chose, sealed with this server's key, for the next start (<see cref="ServerRestore.StageAsync"/>).</summary>
    public Task StageRestoreAsync(RestorePlan plan) => ServerRestore.StageAsync(paths, protector, plan);

    public void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled) return;
        Update(current => current with { Enabled = enabled });
        log($"server backup schedule enabled={enabled}");
    }

    private async Task<string> ReadPasswordAsync()
    {
        byte[] plain;
        try { plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(paths.BackupPassword).ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception e) { throw new BackupFailedException("password", e); }
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// Looks a few minutes after the start, then whenever something is due, until the server stops: what a restore left to take in, the newest backup's check, the
    /// day's backup, and the phones told when two days pass without one.
    /// </summary>
    public async Task RunScheduleAsync(CancellationToken token)
    {
        using var stopping = token.Register(stopped.Cancel);
        try
        {
            await Task.Delay(FirstCheck, time, token).ConfigureAwait(false);
            if (folder is not null && BackupFolder.IsInstallTag(Settings.Install))
                try { if (BackupFolder.SweepTemporary(folder, Settings.Install!, TemporaryAge, time.GetUtcNow().UtcDateTime) is > 0 and var swept) log($"server backup swept temporary={swept}"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            DateTimeOffset? verifyNotBefore = null;
            while (!token.IsCancellationRequested)
            {
                await PendingAsync(token).ConfigureAwait(false);
                if (Settings.VerifyIsDue(time.GetUtcNow(), HasPassword, verifyNotBefore))
                {
                    var result = await VerifyAsync(scheduled: true, token).ConfigureAwait(false);
                    verifyNotBefore = result is "busy" or "password" ? time.GetUtcNow() + BackupSettings.VerifyRetry : null;
                }
                var now = time.GetUtcNow();
                var deferred = false;
                if (Settings.NextDue(now, HasPassword) is { } due && due <= now)
                {
                    if (messenger is { Saved: true, Connected: false })
                    {
                        if (waitingForMessenger is null) log("server backup deferred reason=messenger_offline");
                        waitingForMessenger ??= now;
                        deferred = waitingForMessenger.Value + MessengerPatience > now;
                    }
                    if (!deferred)
                    {
                        waitingForMessenger = null;
                        await BackUpAsync(scheduled: true, token).ConfigureAwait(false);
                    }
                }
                await TellIfStaleAsync(token).ConfigureAwait(false);
                Raise();
                var next = Settings.NextScheduled(time.GetUtcNow(), HasPassword, verifyNotBefore);
                var wait = !deferred && next is { } at && at - time.GetUtcNow() is var until && until < Poll ? until : Poll;
                var least = Poll < TimeSpan.FromMinutes(1) ? Poll : TimeSpan.FromMinutes(1);
                await Task.Delay(wait < least ? least : wait, time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What a restore left to take in, tried at every look: each is taken in once its account is signed in again.</summary>
    public async Task PendingAsync(CancellationToken token)
    {
        try
        {
            await PendingImports.TryMessengerAsync(paths, protector, messenger, log, token).ConfigureAwait(false);
            if (CurrentKey is { } key && ImportBundle is { } import) await PendingImports.TryPcLocalAsync(paths, protector, key, import, log, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { log($"server restore pending failed type={failure.GetType().Name}"); }
    }

    private async Task TellIfStaleAsync(CancellationToken token)
    {
        var current = Settings;
        if (!IsStale(current, time.GetUtcNow(), folder is not null, HasPassword) || StaleNotice is not { } notice) return;
        // Once per stretch without a good backup: the mark names the last good one (or none), and a good one ends the stretch.
        var stretch = current.LastSuccess?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        var markPath = Path.Combine(paths.Backup, "stale.told");
        try { if (File.Exists(markPath) && File.ReadAllText(markPath) == stretch) return; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return; }
        try
        {
            if (!await notice(token).ConfigureAwait(false)) return;
            Directory.CreateDirectory(paths.Backup);
            File.WriteAllText(markPath, stretch);
            log("server backup stale told=yes");
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { log($"server backup stale told=failed type={failure.GetType().Name}"); }
    }

    private async Task<string> VerifyAsync(bool scheduled, CancellationToken token)
    {
        var trigger = scheduled ? "schedule" : "manual";
        var watch = Stopwatch.StartNew();
        string? skipped = null;
        BackupCheckResult result;
        try
        {
            result = await BackupCheck.GatedAsync(running, async () =>
            {
                checking = true;
                try
                {
                    var current = Settings;
                    if (folder is null || !BackupFolder.IsInstallTag(current.Install)) return new("missing", null, 0, 0, 0);
                    if (!HasPassword) { skipped = "password"; return BackupCheckResult.Busy; }
                    string password;
                    try { password = await ReadPasswordAsync().ConfigureAwait(false); }
                    catch (BackupFailedException) { skipped = "password"; return BackupCheckResult.Busy; }
                    return await OnQuietThread(() => ServerBackupCheck.Run(folder, current.Install!, current.LastName, password, token, current.PasswordSetAt)).ConfigureAwait(false);
                }
                finally { checking = false; }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log($"server backup verify cancelled trigger={trigger}");
            return "busy";
        }
        if (skipped is not null)
        {
            log($"server backup verify skipped reason={skipped} trigger={trigger}");
            return skipped;
        }
        if (result.Result != "busy") Update(current => current.Verified(time.GetUtcNow(), result.Result));
        log($"server backup verify result={result.Result} bytes={result.Bytes} ms={watch.ElapsedMilliseconds} trigger={trigger}");
        return result.Result;
    }

    private async Task<BackupRun> BackUpAsync(bool scheduled, CancellationToken token)
    {
        if (!await running.WaitAsync(checking ? TimeSpan.FromMinutes(2) : TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false)) return new(false, "busy", 0, null);
        busy = true;
        Raise();
        var trigger = scheduled ? "schedule" : "manual";
        var watch = Stopwatch.StartNew();
        try
        {
            var target = folder ?? throw new BackupFailedException("folder");
            if (!HasPassword) throw new BackupFailedException("password");
            // The tag this server's backup names carry, made once, so its pruning never reaches another install's backups in the same folder.
            if (!BackupFolder.IsInstallTag(Settings.Install)) Update(current => current with { Install = BackupFolder.NewInstallTag() });
            var current = Settings;
            var passwordRead = time.GetUtcNow();
            var password = await ReadPasswordAsync().ConfigureAwait(false);
            var includeMessenger = messenger is { Connected: true };
            var leftOut = includeMessenger ? null : messenger is { Saved: true } ? "not_connected" : "not_signed_in";
            var written = await OnQuietThread(() => Write(target, current, password, includeMessenger, leftOut, token)).ConfigureAwait(false);
            Update(settingsNow =>
            {
                var succeeded = settingsNow.Succeeded(time.GetUtcNow(), written.Bytes, written.Name, Summary(written.Manifest));
                return succeeded.PasswordSetAt > passwordRead ? succeeded with { PasswordSetAt = time.GetUtcNow() } : succeeded;
            });
            var m = written.Manifest;
            log($"server backup written rows={m.Count(ServerBackupArchive.Parts.Shared) + m.Count(ServerBackupArchive.Parts.State)} " +
                $"messages={m.Count(ServerBackupArchive.Parts.HistoryMessages)} photos={m.Count(ServerBackupArchive.Parts.Photos)} devices={m.Count("devices")} " +
                $"pages={m.Messenger?.Pages ?? 0} messenger={m.LeftOut ?? "included"} bytes={written.Bytes} pruned={written.Pruned.Removed.Count} spared={written.Pruned.Spared} " +
                $"ms={watch.ElapsedMilliseconds} trigger={trigger}");
            return new(true, null, written.Bytes, null);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            log($"server backup cancelled trigger={trigger}");
            return new(false, "cancelled", 0, null);
        }
        catch (Exception e)
        {
            var reason = e is BackupFailedException failed ? failed.Reason : "write";
            Update(current => current with { LastFailure = reason, LastFailureAt = time.GetUtcNow() });
            log($"server backup failed reason={reason} type={(e is BackupFailedException { InnerException: { } inner } ? inner.GetType().Name : e.GetType().Name)} trigger={trigger} ms={watch.ElapsedMilliseconds}");
            return new(false, reason, 0, null);
        }
        finally
        {
            busy = false;
            running.Release();
            Raise();
        }
    }

    // What BackupSettings keeps of a manifest: Messenger's part, by the same record the PC's uses.
    private static BackupManifest Summary(ServerBackupManifest manifest) =>
        new(manifest.Format, manifest.Version, manifest.Created, manifest.App, manifest.Messenger, manifest.LeftOut, []);

    private sealed record Written(ServerBackupManifest Manifest, string Name, long Bytes, PruneResult Pruned);

    // On the quiet thread. Under a temporary name beside the final one, then moved into place, as a PC backup is; Messenger's history failing to read leaves it out
    // rather than failing the rest.
    private Written Write(string target, BackupSettings current, string password, bool includeMessenger, string? leftOut, CancellationToken token)
    {
        var install = current.Install!;
        try
        {
            // Never made here: a folder that is not there is a mapping that went missing, and one made inside the container would vanish with it.
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException();
            BackupFolder.SweepTemporary(target, install, TemporaryAge, time.GetUtcNow().UtcDateTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new BackupFailedException(NewBackupFile.Classify(e, "folder"), e); }
        var part = new BackupSettingsPart(current.Enabled, install, current.MessengerKept);
        var exporting = false;
        try
        {
            (ServerBackupManifest Manifest, string Path, long Bytes) written;
            try { written = Once(includeMessenger ? Pages() : null, leftOut); }
            catch (Exception e) when (includeMessenger && e is BackupFailedException { Reason: "messenger" } or InvalidDataException)
            {
                EndExport();
                log($"server backup messenger left out reason={Code(e is BackupFailedException { InnerException: { } inner } ? inner : e)}");
                written = Once(null, "failed");
            }
            var name = Path.GetFileName(written.Path);
            var pruned = new PruneResult([], 0);
            // The newest backup of each Messenger account's history stays, however many newer ones lack it.
            var kept = written.Manifest.Messenger is { } summary
                ? new Dictionary<string, string>(current.MessengerKept ?? [], StringComparer.Ordinal) { [summary.Account] = name } : current.MessengerKept;
            try { pruned = BackupFolder.Prune(target, install, kept?.Values); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new(written.Manifest, name, written.Bytes, pruned);
        }
        catch (Exception e) when (e is not (BackupFailedException or OperationCanceledException)) { throw new BackupFailedException(NewBackupFile.Classify(e, "write"), e); }
        finally { EndExport(); }

        (ServerBackupManifest, string, long) Once(IEnumerable<MessengerHistoryPage>? pages, string? without)
        {
            string final, temporary;
            try { final = BackupFolder.NextPath(target, time.GetUtcNow().ToLocalTime().DateTime, install); temporary = BackupFolder.TemporaryPath(final); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new BackupFailedException(NewBackupFile.Classify(e, "folder"), e); }
            try
            {
                ServerBackupManifest manifest;
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    using var sealedPayload = BackupFile.Create(file, password);
                    try { manifest = ServerBackupExport.Write(sealedPayload, paths, protector, part, pages, without, app, time.GetUtcNow(), token); }
                    catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or CryptographicException or JsonException) { throw new BackupFailedException("local", e); }
                    sealedPayload.Complete();
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, final, overwrite: false);
                return (manifest, final, new FileInfo(final).Length);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
                throw;
            }
        }

        void EndExport()
        {
            if (!exporting) return;
            exporting = false;
            try { messenger!.EndExportAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
        }

        IEnumerable<MessengerHistoryPage> Pages()
        {
            string? cursor = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                MessengerHistoryPage page;
                exporting = true;
                try { page = messenger!.ExportPageAsync(cursor, token).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is not OperationCanceledException) { throw new BackupFailedException("messenger", e); }
                exporting = !page.IsLast;
                yield return page;
                if (page.IsLast) yield break;
                cursor = page.Next;
            }
        }
    }

    // The helper's error codes are fixed words; anything else is named by its type, never by its message.
    private static string Code(Exception e) =>
        e.Message is { Length: > 0 and <= 40 } code && code.All(c => c is (>= 'a' and <= 'z') or '_') ? code : e.GetType().Name;

    // A thread of its own at below-normal priority: key derivation, compression and encryption are the costly part, and a backup is never more urgent than a client.
    private static Task<T> OnQuietThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Messages server backup" }.Start();
        return done.Task;
    }

    private void Update(Func<BackupSettings, BackupSettings> change)
    {
        BackupSettings next;
        lock (gate)
        {
            next = change(settings) with { Folder = folder };
            settings = next;
        }
        try
        {
            Directory.CreateDirectory(paths.Backup);
            // The folder is the host's to say at each start, never kept: a server moved to another mapping writes where it is told.
            (next with { Folder = null }).Save(paths.BackupSettings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Raise();
    }

    // The status, to whoever listens, when any of it changed.
    private void Raise()
    {
        var status = Status();
        lock (gate)
        {
            if (lastRaised == status) return;
            lastRaised = status;
        }
        try { Changed?.Invoke(status); } catch (Exception) { /* A listener that fails changes nothing here. */ }
    }
}

/// <summary>
/// Reads the server's newest backup back the way a restore would, without restoring anything (<see cref="BackupCheck"/> for a server's file): the same results, from
/// <c>ok</c> to <c>old_password</c>, and the folder is never written.
/// </summary>
internal static class ServerBackupCheck
{
    public static BackupCheckResult Run(string folder, string install, string? lastName, string password, CancellationToken ct, DateTimeOffset? passwordSetAt = null)
    {
        ct.ThrowIfCancellationRequested();
        string? path;
        try
        {
            path = BackupFolder.IsOwnBackupName(lastName, install) && Path.Combine(folder, lastName!) is var last && File.Exists(last) ? last : BackupFolder.Newest(folder, install);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { path = null; }
        if (path is null) return new("missing", null, 0, 0, 0);
        var name = Path.GetFileName(path);
        long bytes;
        try
        {
            var info = new FileInfo(path);
            bytes = info.Length;
            if (passwordSetAt is { } changed && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < changed) return new("old_password", name, 0, 0, bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new("missing", name, 0, 0, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new("unreadable", name, 0, 0, 0); }
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = BackupFile.Open(file, password);
            using var contents = ServerBackupContents.Open(reader);
            contents.Verify(ct);
            return new("ok", name, contents.Manifest.Messenger?.Pages ?? 0, contents.Manifest.Count(ServerBackupArchive.Parts.Shared), bytes);
        }
        catch (BackupFileException failure) { return new(failure.Failure == BackupFileFailure.Unsupported ? "unsupported" : "unreadable", name, 0, 0, bytes); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new("missing", name, 0, 0, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException) { return new("unreadable", name, 0, 0, bytes); }
    }
}
