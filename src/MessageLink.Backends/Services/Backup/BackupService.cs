using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.Backup;

internal sealed record BackupRun(bool Succeeded, string? Failure, long Bytes, BackupManifest? Manifest);

/// <summary>
/// Backs up what only this PC holds into an encrypted file in a folder the user picks, once a day while Messages
/// runs, and restores one without replacing anything already here.
/// </summary>
/// <remarks>
/// The password is kept sealed to this Windows user so scheduled backups run unattended; it is never logged and never
/// written in the clear. The heavy part — deriving the key, compressing and encrypting — runs on a thread of its own
/// at below-normal priority, and never before the launch has settled. Restore reaches Messenger only through the
/// helper's own import: it never registers a device, pairs, or touches a session.
/// </remarks>
internal sealed class BackupService(IPrivateStateStore state, IAccessTokenStore tokens, IStateProtector protector,
    MessengerBackend messenger, Func<Task<IReadOnlyList<ChatSummary>>> loadedChats, Func<Task> localRestored,
    Func<System.Text.Json.Nodes.JsonObject?> readAppPreferences, Func<System.Text.Json.Nodes.JsonObject, (int Added, int Skipped)> restoreAppPreferences)
{
    public const int MinPasswordLength = 8;
    private static string SettingsPath => AppPaths.Combine("backup-settings.json");
    private static string PasswordPath => AppPaths.Combine("backup-password.protected");
    // Well clear of the launch, and then often enough that a day's backup is not late by more than a few minutes.
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5), Poll = TimeSpan.FromMinutes(10);
    // A scheduled backup waits this long for a Messenger that is signed in but not connected, rather than leaving its
    // history out; then it goes ahead without it, so one broken connection cannot stop every backup.
    private static readonly TimeSpan MessengerPatience = TimeSpan.FromHours(12);
    private static readonly TimeSpan TemporaryAge = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim running = new(1, 1);
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private BackupSettings settings = BackupSettings.Load(SettingsPath);
    private DateTimeOffset? waitingForMessenger;
    // The app's lifetime, from the schedule; a backup started by hand stops with the app too, and is not then recorded as failed.
    private CancellationToken lifetime;

    /// <summary>Raised on the thread that created the service whenever the settings or the running state change.</summary>
    public event EventHandler? Changed;
    public BackupSettings Settings => settings;
    public bool IsBusy { get; private set; }
    public bool IsRestoring { get; private set; }
    public bool HasPassword => File.Exists(PasswordPath);
    public string Describe() => settings.Describe(DateTimeOffset.UtcNow, IsBusy, IsRestoring);

    private LocalStores Stores() => new(state, protector, LocalChatPreferenceStore.DefaultDirectory, LocalContactNameStore.DefaultDirectory,
        readAppPreferences, restoreAppPreferences);

    public void SetEnabled(bool enabled)
    {
        if (settings.Enabled == enabled) return;
        Update(settings with { Enabled = enabled });
        AppLog.Write($"backup schedule enabled={enabled}");
    }

    public void SetFolder(string folder)
    {
        Update(settings with { Folder = folder });
        AppLog.Write("backup folder chosen");
    }

    public async Task SetPasswordAsync(string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException("The password is too short.", nameof(password));
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            var sealedPassword = await protector.ProtectAsync(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(PasswordPath)!);
            var temporary = PasswordPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, sealedPassword);
            File.Move(temporary, PasswordPath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        // A failure that was about the password is no longer the latest word on it. Backups older than this are sealed
        // under the previous password, which a check says rather than calling them unreadable.
        var next = settings with { PasswordSetAt = DateTimeOffset.UtcNow };
        Update(next.LastFailure == "password" ? next with { LastFailure = null, LastFailureAt = null } : next);
        AppLog.Write("backup password set");
    }

    private async Task<string> ReadPasswordAsync()
    {
        byte[] plain;
        try { plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(PasswordPath)); }
        catch (Exception e) { throw new BackupFailedException("password", e); }
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task<BackupRun> BackUpNowAsync() => RunAsync(scheduled: false, lifetime);

    /// <summary>Reads the latest backup back now, without restoring anything.</summary>
    public Task<BackupCheckResult> VerifyNowAsync() => VerifyAsync(scheduled: false, lifetime);

    // A check holds the semaphore only to read one file back, and never overlaps a backup or a restore. A backup or
    // restore asked for while one runs waits for it rather than being refused as if another backup were running.
    private async Task<bool> EnterAsync()
    {
        if (await running.WaitAsync(0)) return true;
        return checking && await running.WaitAsync(CheckPatience);
    }

    private static readonly TimeSpan CheckPatience = TimeSpan.FromMinutes(2);
    private volatile bool checking;

    private async Task<BackupCheckResult> VerifyAsync(bool scheduled, CancellationToken token)
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
                    var current = settings;
                    if (current.Folder is not { Length: > 0 } folder || !BackupFolder.IsInstallTag(current.Install)) return new("missing", null, 0, 0, 0);
                    if (!HasPassword) { skipped = "password"; return BackupCheckResult.Busy; }
                    string password;
                    // Read exactly as a backup reads it.
                    try { password = await ReadPasswordAsync(); }
                    catch (BackupFailedException) { skipped = "password"; return BackupCheckResult.Busy; }
                    return await OnQuietThread(() => BackupCheck.Run(folder, current.Install!, current.LastName, password, File.GetAttributes, token, current.PasswordSetAt));
                }
                finally { checking = false; }
            });
        }
        catch (OperationCanceledException)
        {
            AppLog.Write($"backup verify cancelled trigger={trigger}");
            return BackupCheckResult.Busy;
        }
        if (skipped is not null)
        {
            // The password saved here could not be read, so nothing was checked; the next poll tries again.
            AppLog.Write($"backup verify skipped reason={skipped} trigger={trigger}");
            return new(skipped, null, 0, 0, 0);
        }
        // A busy folder is tried again at the next poll and records nothing. Anything else is a finished check; an
        // unreadable backup makes the next one due within the retry interval (BackupSettings.NextDue).
        if (result.Result != "busy") Update(settings.Verified(DateTimeOffset.UtcNow, result.Result));
        AppLog.Write($"backup verify result={result.Result} pages={result.Pages} local={result.LocalItems} bytes={result.Bytes} " +
            $"ms={watch.ElapsedMilliseconds} trigger={trigger}");
        return result;
    }

    /// <summary>Checks a few minutes after launch, then whenever the next backup is due, until the app closes.</summary>
    public async Task RunScheduleAsync(CancellationToken token)
    {
        lifetime = token;
        try
        {
            await Task.Delay(FirstCheck, token);
            if (settings is { Folder: { Length: > 0 } folder, Install: var install } && BackupFolder.IsInstallTag(install))
                await Task.Run(() =>
                {
                    try { if (Directory.Exists(folder) && BackupFolder.SweepTemporary(folder, install!, TemporaryAge, DateTime.UtcNow) is > 0 and var swept) AppLog.Write($"backup swept temporary={swept}"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }, token);
            // In memory only: a check that found the folder busy, or the password unreadable, is tried again after
            // VerifyRetry rather than at every wake.
            DateTimeOffset? verifyNotBefore = null;
            while (!token.IsCancellationRequested)
            {
                // The latest backup is read back a day after it was written and weekly after that, before this round's
                // backup (BackupSettings.VerifyIsDue): that is when the day-old file is still the latest one.
                if (settings.VerifyIsDue(DateTimeOffset.UtcNow, HasPassword, verifyNotBefore))
                {
                    var check = await VerifyAsync(scheduled: true, token);
                    verifyNotBefore = check.Result is "busy" or "password" ? DateTimeOffset.UtcNow + BackupSettings.VerifyRetry : null;
                }
                var now = DateTimeOffset.UtcNow;
                var deferred = false;
                if (settings.NextDue(now, HasPassword) is { } due && due <= now)
                {
                    if (MessengerBackend.HasSavedSession && !messenger.IsConnected)
                    {
                        if (waitingForMessenger is null) AppLog.Write("backup deferred reason=messenger_offline");
                        waitingForMessenger ??= now;
                        deferred = waitingForMessenger.Value + MessengerPatience > now;
                    }
                    if (!deferred)
                    {
                        waitingForMessenger = null;
                        await RunAsync(scheduled: true, token);
                    }
                }
                var next = settings.NextScheduled(DateTimeOffset.UtcNow, HasPassword, verifyNotBefore);
                var wait = !deferred && next is { } at && at - DateTimeOffset.UtcNow is var until && until < Poll ? until : Poll;
                await Task.Delay(wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<BackupRun> RunAsync(bool scheduled, CancellationToken token)
    {
        if (!await EnterAsync()) return new(false, "busy", 0, null);
        IsBusy = true; Raise();
        var trigger = scheduled ? "schedule" : "manual";
        var watch = Stopwatch.StartNew();
        try
        {
            var folder = settings.Folder is { Length: > 0 } chosen ? chosen : throw new BackupFailedException("folder");
            if (!HasPassword) throw new BackupFailedException("password");
            // The tag this PC's backup names carry, made once, so its pruning never reaches another PC's backups.
            if (!BackupFolder.IsInstallTag(settings.Install)) Update(settings with { Install = BackupFolder.NewInstallTag() });
            var current = settings;
            var passwordRead = DateTimeOffset.UtcNow;
            var password = await ReadPasswordAsync();
            var profile = await NotificationIdentity.ProfileAsync(tokens, token);
            var chats = await loadedChats();
            var includeMessenger = messenger.IsConnected;
            var leftOut = includeMessenger ? null : MessengerBackend.HasSavedSession ? "not_connected" : "not_signed_in";
            var version = typeof(BackupService).Assembly.GetName().Version;
            var app = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
            var (manifest, name, bytes, pruned) = await OnQuietThread(() => Write(folder, current, password, profile, chats, includeMessenger, leftOut, app, token));
            var succeeded = settings.Succeeded(DateTimeOffset.UtcNow, bytes, name, manifest);
            // The password changed while this backup ran, after it had read the old one: the file just written is sealed
            // under that, and a check says so rather than calling it unreadable. The next backup uses the new one.
            if (succeeded.PasswordSetAt > passwordRead) succeeded = succeeded with { PasswordSetAt = DateTimeOffset.UtcNow };
            Update(succeeded);
            AppLog.Write($"backup written entries={BackupArchive.EntryCount(manifest)} local={manifest.LocalItems} pages={manifest.Messenger?.Pages ?? 0} " +
                $"messenger={manifest.LeftOut ?? "included"} bytes={bytes} pruned={pruned.Removed.Count} spared={pruned.Spared} ms={watch.ElapsedMilliseconds} trigger={trigger}");
            return new(true, null, bytes, manifest);
        }
        // The app closing mid-backup can fail it in any number of ways as services go; none of them is the folder's fault.
        catch (Exception) when (token.IsCancellationRequested)
        {
            AppLog.Write($"backup cancelled trigger={trigger}");
            return new(false, "cancelled", 0, null);
        }
        catch (Exception e)
        {
            var reason = e is BackupFailedException failed ? failed.Reason : "write";
            Update(settings with { LastFailure = reason, LastFailureAt = DateTimeOffset.UtcNow });
            AppLog.Write($"backup failed reason={reason} trigger={trigger} ms={watch.ElapsedMilliseconds}");
            return new(false, reason, 0, null);
        }
        finally
        {
            IsBusy = false;
            running.Release();
            Raise();
        }
    }

    // Everything here runs on the quiet thread, and blocks on the helper rather than hopping back to the interface.
    private (BackupManifest Manifest, string Name, long Bytes, PruneResult Pruned) Write(string folder, BackupSettings current, string password,
        string? profile, IReadOnlyList<ChatSummary> chats, bool includeMessenger, string? leftOut, string app, CancellationToken token)
    {
        var install = current.Install!;
        try
        {
            Directory.CreateDirectory(folder);
            BackupFolder.SweepTemporary(folder, install, TemporaryAge, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new BackupFailedException(NewBackupFile.Classify(e, "folder"), e); }

        LocalStateSnapshot local;
        try { local = LocalStateBackup.CollectAsync(Stores(), profile, LocalStateBackup.CandidateIds(chats), token).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new BackupFailedException("local", e); }
        if (local.Unreadable > 0) AppLog.Write($"backup local unreadable={local.Unreadable}");

        var exporting = false;
        try
        {
            // Messenger's history failing to read leaves it out rather than failing the rest (NewBackupFile).
            var written = NewBackupFile.Write(folder, install, password, local, includeMessenger ? Pages() : null, leftOut, app,
                DateTimeOffset.UtcNow, DateTime.Now, failure =>
                {
                    EndExport();
                    AppLog.Write($"backup messenger left out reason={Code(failure)}");
                });
            var name = Path.GetFileName(written.Path);
            var pruned = new PruneResult([], 0);
            // The newest backup of each Messenger account's history stays, however many newer ones lack it.
            try { pruned = BackupFolder.Prune(folder, install, current.KeptAfter(written.Manifest, name)?.Values); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (written.Manifest, name, written.Bytes, pruned);
        }
        catch (Exception e) when (e is not (BackupFailedException or OperationCanceledException))
        {
            throw new BackupFailedException(NewBackupFile.Classify(e, "write"), e);
        }
        finally { EndExport(); }

        // Lets the helper drop its snapshot now rather than when the export goes idle.
        void EndExport()
        {
            if (!exporting) return;
            exporting = false;
            try { messenger.EndHistoryExportAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
        }

        IEnumerable<MessengerHistoryPage> Pages()
        {
            string? cursor = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                MessengerHistoryPage page;
                exporting = true;
                try { page = messenger.ExportHistoryPageAsync(cursor, token).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is not OperationCanceledException) { throw new BackupFailedException("messenger", e); }
                // The helper drops the export itself once it has handed out the last page.
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

    // A thread of its own at below-normal priority: key derivation, compression and encryption are the costly part,
    // and a backup is never more urgent than the conversation in front of the user.
    private static Task<T> OnQuietThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Messages backup" }.Start();
        return done.Task;
    }

    /// <summary>Opens a backup with its password, checking every part of it before anything is shown.</summary>
    public Task<OpenedBackup> OpenAsync(string path, string password) => Task.Run(() => OpenedBackup.Open(path, password));

    /// <summary>
    /// Adds a backup's contents to this PC. Messenger's history goes through the helper's import, only for the account
    /// signed in here; local rows are added only where this PC has nothing under the same key.
    /// </summary>
    public async Task<BackupRestore> RestoreAsync(OpenedBackup opened, IProgress<(int Done, int Total)>? progress = null)
    {
        if (!await EnterAsync()) throw new InvalidOperationException("busy");
        IsBusy = IsRestoring = true; Raise();
        var watch = Stopwatch.StartNew();
        try
        {
            // Rows of every kind, for the log; Messenger's messages alone, for the result the user reads.
            int added = 0, skipped = 0, refused = 0, removed = 0, imported = 0;
            int messagesAdded = 0, messagesSkipped = 0, messagesKeptOut = 0;
            string? note = null;
            var partial = false;
            if (opened.Manifest.Messenger is { } history)
            {
                if (!messenger.IsConnected) note = MessengerBackend.HasSavedSession ? "not_connected" : "not_signed_in";
                else if (messenger.HistoryAccountKey is { } here && here != history.Account) note = "other_account";
                else
                {
                    using var pages = opened.Contents.ReadPages().GetEnumerator();
                    try
                    {
                        while (await Task.Run(pages.MoveNext))
                        {
                            progress?.Report((imported, history.Pages));
                            var result = await messenger.ImportHistoryPageAsync(pages.Current);
                            added += result.Added; skipped += result.Skipped; refused += result.Refused; removed += result.Removed;
                            if (result.Sections.GetValueOrDefault("messages") is { } messages)
                            {
                                messagesAdded += messages.Added; messagesSkipped += messages.Skipped; messagesKeptOut += messages.Refused;
                            }
                            imported++;
                        }
                        progress?.Report((imported, history.Pages));
                    }
                    // Each page is its own transaction and a second import of it changes nothing, so stopping partway
                    // leaves a consistent store that restoring again completes.
                    catch (InvalidOperationException e) when (e.Message == "backup_other_account") { note = "other_account"; }
                    catch (InvalidOperationException e) when (e.Message == "login_required") { note = "not_connected"; partial = imported > 0; }
                    catch (Exception e) when (e is InvalidOperationException or BackupFileException or TimeoutException or IOException or System.Text.Json.JsonException)
                    { note = "failed"; partial = imported > 0; }
                    if (imported > 0) messenger.HistoryImported();
                }
            }
            var profile = await NotificationIdentity.ProfileAsync(tokens);
            var snapshot = await Task.Run(opened.Contents.ReadLocal);
            var local = await LocalStateBackup.RestoreAsync(Stores(), snapshot, profile);
            added += local.Added; skipped += local.Skipped; refused += local.Refused;
            // Labels, merged conversations and the pinned order are held in memory once loaded, and the next change to
            // one writes that copy over what was just added. They are read again before the dialog lets anyone make one.
            if (local.Added > 0)
            {
                try { await localRestored(); }
                catch (Exception e) { AppLog.Write($"backup restore reload failed reason={e.GetType().Name}"); }
            }
            AppLog.Write($"backup restore added={added} skipped={skipped} refused={refused} removed={removed} held={local.Held} pages={imported} " +
                $"messenger={note ?? (opened.Manifest.Messenger is null ? "none" : "restored")} rekeyed={local.Rekeyed} " +
                $"same_profile={local.SameProfile} ms={watch.ElapsedMilliseconds}");
            return new(imported, messagesAdded, messagesSkipped, messagesKeptOut, removed, local, profile is null, note, partial);
        }
        catch (Exception e)
        {
            AppLog.Write($"backup restore failed reason={e.GetType().Name} ms={watch.ElapsedMilliseconds}");
            throw;
        }
        finally
        {
            IsBusy = IsRestoring = false;
            running.Release();
            Raise();
        }
    }

    private void Update(BackupSettings next)
    {
        settings = next;
        try { next.Save(SettingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Raise();
    }

    private void Raise()
    {
        if (context is null) Changed?.Invoke(this, EventArgs.Empty);
        else context.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
    }
}
