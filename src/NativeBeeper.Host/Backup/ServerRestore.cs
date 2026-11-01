using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Storage;

namespace NativeBeeper.Host.Backup;

/// <summary>A backup file in the backups folder, as the restore lists it: its name, size and when it was written.</summary>
internal sealed record BackupListing(string Name, long Bytes, DateTimeOffset Written);

/// <summary>
/// What a backup holds, read before anything is restored: a server's (<c>server</c>) or a PC's (<c>pc</c>), when it was made, the devices it would bring back (a server's
/// only) with when each was last seen, and counts. The devices' names are the owner's own, shown only on the server's page.
/// </summary>
internal sealed record RestorePreview(string Kind, DateTimeOffset Created, string App, IReadOnlyList<BackupDevice> Devices, int Conversations, int Messages,
    int MessengerPages, int Scheduled, int Photos);

/// <summary>A restore the person has chosen, kept sealed until the server applies it at its next start: which file, its password, and the devices to forget.</summary>
internal sealed record RestorePlan(int Version, string File, string Password, List<string> Forget, DateTimeOffset At);

/// <summary>How the last restore ended, for the page to say once (<c>Restore/outcome.json</c>): fixed words and counts only.</summary>
internal sealed record RestoreOutcome(string Outcome, string? Kind, DateTimeOffset At, int Devices = 0, int Forgotten = 0, int Held = 0, int Rows = 0, int Messages = 0,
    int MessengerPages = 0, int PcRows = 0);

/// <summary>The words <see cref="RestoreOutcome.Outcome"/> takes.</summary>
internal static class RestoreOutcomes
{
    public const string Restored = "restored", Unreadable = "unreadable", Unsupported = "unsupported", Missing = "missing", InUse = "in_use", Failed = "failed",
        PlanUnreadable = "plan_unreadable", Reset = "reset", ResetInUse = "reset_in_use";
}

/// <summary>A restore's moves failed part way (SWITCHBOARD.md stage 10): the list of moves stays, the server must not open, and the next start finishes them.</summary>
internal sealed class RestoreUnfinishedException(Exception inner) : Exception("The restore did not finish its moves.", inner);

/// <summary>
/// Restore (SWITCHBOARD.md stage 10, step 10.3), applied only at a start of the server, before any store is open, and only onto a server with no service signed in.
/// </summary>
/// <remarks>
/// <para><b>Plan, stage, commit.</b> The page checks the file and its password and writes the person's choices as a plan, sealed with the server's own key, then the
/// server stops and starts again. At the start, the restore builds every new store beside the old ones (<c>Restore/staged</c>), each sealed with this server's keys
/// (never the backup's: a master key is never replaced), and only when all are whole writes the list of moves (<c>Restore/commit.json</c>) and makes them. A stop
/// before that list leaves the old stores as they were and the staged ones swept at the next start, which starts the restore again from its plan; a stop after it
/// finishes the moves at the next start. The server opens nothing in between.</para>
/// <para><b>Every copy starts over.</b> The shared state and the whole history are made anew under new epochs, and the list's revisions are not carried: every client's
/// copy from before the restore names an epoch the server no longer has, so it is read again whole (stage 9's start-over). Every scheduled send that has not gone
/// is held as not sent for the person to send or drop (owner, 2026-11-01); the outbox is never carried, so no send is repeated.</para>
/// <para><b>A PC's backup</b> brings no identity: its Messenger history and its choices wait (<c>Restore/pending</c>) until the accounts they belong to are signed in
/// again, and are then taken in once (<see cref="PendingImports"/>).</para>
/// <para>Logged as <c>server restore ...</c> with fixed words and counts; never a file name, a password or a device's name.</para>
/// </remarks>
internal static class ServerRestore
{
    private const string PlanFile = "plan.protected", CommitFile = "commit.json", OutcomeFile = "outcome.json", StagedFolder = "staged";
    public const string PendingFolder = "pending";

    // Sealed: the staged file holds the target's bytes sealed with the server's key (a secret staged on the data volume, the certificate's key and the Firebase key), unsealed
    // as it is put in place.
    private sealed record Move(string From, string To, bool Store, bool Sealed = false);
    private sealed record Commit(int Version, List<Move> Moves, List<string> Deletes, RestoreOutcome Outcome);

    /// <summary>The backup files in <paramref name="folder"/>, newest first; none when it cannot be read.</summary>
    public static IReadOnlyList<BackupListing> List(string? folder)
    {
        if (folder is null) return [];
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles("*" + BackupFile.Extension, SearchOption.TopDirectoryOnly)
                .Where(file => file.Name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase))
                .Select(file => new BackupListing(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
                .OrderByDescending(file => file.Written)];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
    }

    /// <summary>
    /// The path of <paramref name="name"/> in <paramref name="folder"/>, or null when it is not a plain name of a backup file there: a name from a form never reaches
    /// another folder.
    /// </summary>
    public static string? PathOf(string? folder, string? name)
    {
        if (folder is null || name is not { Length: > 0 and <= 255 } || name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal)
            || !name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Opens a backup with its password and reads every part of it, as a restore would, without writing anything. Throws <see cref="BackupFileException"/> for a file
    /// that is not a backup, one this build cannot read, a wrong password or damage (those two are one answer, as for a PC backup).
    /// </summary>
    public static RestorePreview Preview(string path, string password, CancellationToken cancellationToken = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = BackupFile.Open(file, password);
        switch (ServerBackupContents.FormatOf(reader))
        {
            case ServerBackupArchive.Format:
            {
                using var contents = ServerBackupContents.Open(reader);
                contents.Verify(cancellationToken);
                var identity = contents.ReadIdentity();
                var scheduled = contents.ReadPart<PlainRow>(ServerBackupArchive.Parts.Shared).Count(row => row.Key.StartsWith(Protocol.SharedCategories.Scheduled + ":", StringComparison.Ordinal));
                var manifest = contents.Manifest;
                return new("server", manifest.Created, manifest.App, identity.Devices, manifest.Count(ServerBackupArchive.Parts.HistoryChats),
                    manifest.Count(ServerBackupArchive.Parts.HistoryMessages), manifest.Messenger?.Pages ?? 0, scheduled, manifest.Count(ServerBackupArchive.Parts.Photos));
            }
            case BackupArchive.Format:
            {
                using var contents = BackupContents.Open(reader);
                _ = contents.ReadLocal();
                var pages = 0;
                foreach (var _ in contents.ReadPages()) { pages++; cancellationToken.ThrowIfCancellationRequested(); }
                if (pages != (contents.Manifest.Messenger?.Pages ?? 0)) throw new BackupFileException(BackupFileFailure.Unreadable);
                return new("pc", contents.Manifest.Created, contents.Manifest.App, [], contents.Manifest.MessengerConversations, contents.Manifest.MessengerMessages,
                    pages, 0, 0);
            }
            default:
                throw new BackupFileException(BackupFileFailure.Unreadable);
        }
    }

    /// <summary>Keeps the person's choice of restore, sealed, for the next start (<see cref="ApplyPendingAsync"/>). Replaces an earlier one not yet applied.</summary>
    public static async Task StageAsync(ServerBackupPaths paths, IStateProtector protector, RestorePlan plan)
    {
        Directory.CreateDirectory(paths.Restore);
        var path = Path.Combine(paths.Restore, PlanFile);
        var plain = JsonSerializer.SerializeToUtf8Bytes(plan);
        try { await WriteWholeAsync(path, await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Whether a restore is chosen and waits for the next start.</summary>
    public static bool Planned(ServerBackupPaths paths) => File.Exists(Path.Combine(paths.Restore, PlanFile)) || File.Exists(Path.Combine(paths.Restore, CommitFile));

    /// <summary>How the last restore ended, once it has (the page says so until a backup or another restore replaces it); null when none is recorded.</summary>
    public static RestoreOutcome? LastOutcome(ServerBackupPaths paths)
    {
        try
        {
            var path = Path.Combine(paths.Restore, OutcomeFile);
            return File.Exists(path) && new FileInfo(path).Length < 64 << 10 ? JsonSerializer.Deserialize<RestoreOutcome>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Forgets the recorded outcome (the page's notice, once read).</summary>
    public static void ClearOutcome(ServerBackupPaths paths)
    {
        try { File.Delete(Path.Combine(paths.Restore, OutcomeFile)); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The restore chosen before this start, if any, applied before anything opens a store: a commit a stop left is finished, staged files a stop left are swept, and a plan
    /// is applied from the beginning. <paramref name="signedIn"/> says whether any service is signed in on this server, which refuses the restore. Returns how it ended,
    /// or null when there was nothing to do. Anything that fails before the list of moves is written ends the plan as failed, with nothing changed; a failure while the moves
    /// are made is thrown (<see cref="RestoreUnfinishedException"/>), the list stays, and the caller must not open the server: the next start finishes the moves.
    /// <paramref name="signedInForReset"/> says whether an account Reset server signs out (Messenger, Instagram, Google Voice) is still signed in at the reset's start, which
    /// refuses the reset; Google Messages is unpaired here by the reset itself.
    /// </summary>
    public static async Task<RestoreOutcome?> ApplyPendingAsync(ServerBackupPaths paths, IStateProtector protector, string? folder, Func<bool> signedIn, Action<string> log,
        TimeProvider? time = null, IReadOnlySet<string>? keep = null, Func<bool>? signedInForReset = null, bool restorePagePassword = true, CancellationToken cancellationToken = default)
    {
        time ??= TimeProvider.System;
        if (File.Exists(Path.Combine(paths.Restore, ResetFile)))
        {
            if (signedInForReset?.Invoke() == true)
            {
                File.Delete(Path.Combine(paths.Restore, ResetFile));
                var refused = new RestoreOutcome(RestoreOutcomes.ResetInUse, null, time.GetUtcNow());
                WriteOutcome(paths, refused);
                log("server reset refused outcome=in_use");
                return refused;
            }
            return Reset(paths, keep ?? new HashSet<string>(StringComparer.Ordinal), time, log);
        }
        var commitPath = Path.Combine(paths.Restore, CommitFile);
        if (File.Exists(commitPath))
        {
            Commit? left = null;
            try { left = JsonSerializer.Deserialize<Commit>(await File.ReadAllBytesAsync(commitPath, cancellationToken).ConfigureAwait(false)); }
            catch (JsonException) { }
            if (left is not null)
            {
                try { Finish(paths, protector, left); }
                catch (Exception failure) { throw Unfinished(log, failure); }
                log($"server restore finished resumed=yes moves={left.Moves.Count}");
                return left.Outcome;
            }
            // Written whole before any move, so one that cannot be read was never begun: nothing has moved.
            File.Delete(commitPath);
        }
        var staged = Path.Combine(paths.Restore, StagedFolder);
        var planPath = Path.Combine(paths.Restore, PlanFile);
        if (Directory.Exists(staged))
        {
            try { Directory.Delete(staged, recursive: true); }
            // Left as it is, and the plan ends: a sweep that keeps failing must not make every start try the restore again.
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log($"server restore sweep failed type={failure.GetType().Name}");
                return File.Exists(planPath) ? Ended(paths, planPath, new(RestoreOutcomes.Failed, null, time.GetUtcNow()), log) : null;
            }
            log("server restore swept staged=yes");
        }
        if (!File.Exists(planPath)) return null;
        var clock = Stopwatch.StartNew();
        RestorePlan? plan = null;
        try { plan = JsonSerializer.Deserialize<RestorePlan>(await protector.UnprotectAsync(await File.ReadAllBytesAsync(planPath, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false)); }
        catch (Exception failure) when (failure is IOException or JsonException or System.Security.Cryptography.CryptographicException or InvalidDataException) { }
        if (plan is not { Version: 1, File.Length: > 0, Password.Length: > 0 }) return Ended(paths, planPath, new(RestoreOutcomes.PlanUnreadable, null, time.GetUtcNow()), log);
        if (signedIn()) return Ended(paths, planPath, new(RestoreOutcomes.InUse, null, time.GetUtcNow()), log);
        if (PathOf(folder, plan.File) is not { } file) return Ended(paths, planPath, new(RestoreOutcomes.Missing, null, time.GetUtcNow()), log);
        Commit built;
        try
        {
            built = await BuildAsync(paths, protector, file, plan, staged, time, restorePagePassword, cancellationToken).ConfigureAwait(false);
            await WriteWholeAsync(commitPath, JsonSerializer.SerializeToUtf8Bytes(built)).ConfigureAwait(false);
        }
        catch (BackupFileException failure)
        {
            TrySweep(staged);
            return Ended(paths, planPath, new(failure.Failure == BackupFileFailure.Unsupported ? RestoreOutcomes.Unsupported : RestoreOutcomes.Unreadable, null, time.GetUtcNow()), log);
        }
        // Whatever it was: nothing has moved (the list of moves was not written), so the old stores stand, the staged ones go and the plan ends, said on the page as failed.
        // Never left to be tried again at every start.
        catch (Exception failure)
        {
            TrySweep(staged);
            try { File.Delete(commitPath); } catch (Exception) { }
            log($"server restore failed type={failure.GetType().Name}");
            return Ended(paths, planPath, new(RestoreOutcomes.Failed, null, time.GetUtcNow()), log);
        }
        try { Finish(paths, protector, built); }
        catch (Exception failure) { throw Unfinished(log, failure); }
        var o = built.Outcome;
        log($"server restore applied kind={o.Kind} devices={o.Devices} forgotten={o.Forgotten} rows={o.Rows} messages={o.Messages} held={o.Held} " +
            $"messenger_pages={o.MessengerPages} pc_rows={o.PcRows} ms={clock.ElapsedMilliseconds}");
        return o;
    }

    private static RestoreUnfinishedException Unfinished(Action<string> log, Exception failure)
    {
        log($"server restore unfinished type={failure.GetType().Name} action=refuse_to_open");
        return new RestoreUnfinishedException(failure);
    }

    /// <summary>Whether a restore's moves were begun and not finished: the server must not open until a start finishes them.</summary>
    public static bool IsUnfinished(ServerBackupPaths paths) => File.Exists(Path.Combine(paths.Restore, CommitFile));

    /// <summary>Asks the next start to reset the server (<see cref="ResetAsync"/>): the page's Reset server, once every account it could sign out is signed out.</summary>
    public static async Task StageResetAsync(ServerBackupPaths paths)
    {
        Directory.CreateDirectory(paths.Restore);
        await WriteWholeAsync(Path.Combine(paths.Restore, ResetFile), "reset"u8.ToArray()).ConfigureAwait(false);
    }

    private const string ResetFile = "reset.plan";

    /// <summary>Whether a reset waits for the next start.</summary>
    public static bool ResetPlanned(ServerBackupPaths paths) => File.Exists(Path.Combine(paths.Restore, ResetFile));

    /// <summary>
    /// Drops a reset or a restore that waits for the next start (the page's Cancel, when the server could not start again by itself). False when a restore's moves have begun:
    /// those are only ever finished.
    /// </summary>
    public static bool CancelPending(ServerBackupPaths paths)
    {
        if (IsUnfinished(paths)) return false;
        foreach (var name in new[] { ResetFile, PlanFile })
            if (File.Exists(Path.Combine(paths.Restore, name))) File.Delete(Path.Combine(paths.Restore, name));
        return true;
    }

    /// <summary>
    /// Resets the server, keeping nothing of what it held: every store, sign-in and paired device in the data folder, and in the keys folder Messenger's helper store,
    /// Google's browser profiles, the certificate, the Firebase files and the join pause. Kept: the keys themselves and their id (a master key is never replaced), the
    /// devices' generation (a list from before the reset must never be taken back), the page's own key, the tailnet door's identity, and whatever <paramref name="keep"/>
    /// names in the data folder (the log, the lease, the run marker). A plan to restore waiting beside it is dropped: a reset is a fresh start.
    /// </summary>
    private static RestoreOutcome Reset(ServerBackupPaths paths, IReadOnlySet<string> keep, TimeProvider time, Action<string> log)
    {
        int removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.Data).ToList())
        {
            var name = Path.GetFileName(entry);
            if (keep.Contains(name) || name == HostKeysMarker || name == Path.GetFileName(paths.Restore)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
            removed++;
        }
        if (!string.Equals(Path.GetFullPath(paths.Keys), Path.GetFullPath(paths.Data), StringComparison.Ordinal))
            foreach (var entry in new[] { "Messenger", "Google", "tls", "push", "join-requests-paused" }.Select(name => Path.Combine(paths.Keys, name)))
            {
                if (Directory.Exists(entry)) { Directory.Delete(entry, recursive: true); removed++; }
                else if (File.Exists(entry)) { File.Delete(entry); removed++; }
            }
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.Restore).ToList())
            if (Path.GetFileName(entry) is not (OutcomeFile or ResetFile)) { if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry); }
        var outcome = new RestoreOutcome(RestoreOutcomes.Reset, null, time.GetUtcNow());
        WriteOutcome(paths, outcome);
        // Last: a stop anywhere above leaves the plan, and the next start resets again from where it was.
        File.Delete(Path.Combine(paths.Restore, ResetFile));
        log($"server reset applied removed={removed}");
        return outcome;
    }

    // The data folder's copy of the keys' id (HostKeys.MarkerFile): kept, so the keys still meet their data after a reset.
    private const string HostKeysMarker = "key.id";

    private static RestoreOutcome Ended(ServerBackupPaths paths, string planPath, RestoreOutcome outcome, Action<string> log)
    {
        try { File.Delete(planPath); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        WriteOutcome(paths, outcome);
        log($"server restore refused outcome={outcome.Outcome}");
        return outcome;
    }

    private static void TrySweep(string staged)
    {
        try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    // Every new file in the staged folder, sealed with this server's protector; nothing outside it is touched. Returns the moves that put them in place.
    private static async Task<Commit> BuildAsync(ServerBackupPaths paths, IStateProtector protector, string file, RestorePlan plan, string staged, TimeProvider time,
        bool restorePagePassword, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(staged);
        var moves = new List<Move>();
        var deletes = new List<string>();
        string Stage(string final, bool store = false, bool sealedFile = false)
        {
            var at = Path.Combine(staged, moves.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "-" + Path.GetFileName(final));
            moves.Add(new(at, final, store, sealedFile));
            return at;
        }
        byte[] Seal(byte[] plain) => protector.ProtectAsync(plain).GetAwaiter().GetResult();
        var now = time.GetUtcNow();

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = BackupFile.Open(stream, plan.Password);
        var format = ServerBackupContents.FormatOf(reader);
        if (format == BackupArchive.Format) return await BuildFromPcAsync(paths, protector, reader, staged, moves, deletes, now, cancellationToken).ConfigureAwait(false);
        if (format != ServerBackupArchive.Format) throw new BackupFileException(BackupFileFailure.Unreadable);
        using var contents = ServerBackupContents.Open(reader);
        contents.Verify(cancellationToken);
        var identity = contents.ReadIdentity();

        // The certificate and its key: every device that pinned it reconnects by itself.
        if (identity.Certificate is { Length: > 0 } certificate && identity.CertificateKey is { Length: > 0 } key)
        {
            // The key first, as the certificate's own writer does.
            // Unsealed into its place, except where the host keeps it sealed (a Windows server, TlsKeySealed): there the staged, sealed bytes are the file.
            await WritePrivateAsync(Stage(paths.TlsKey, sealedFile: !paths.TlsKeySealed), Seal(Encoding.UTF8.GetBytes(key))).ConfigureAwait(false);
            await WritePrivateAsync(Stage(paths.TlsCertificate), Encoding.UTF8.GetBytes(certificate)).ConfigureAwait(false);
        }

        // The devices, less those the person chose to forget, numbered past whatever this server's keys folder last recorded so the list is never taken for a rollback.
        var forget = plan.Forget.ToHashSet(StringComparer.Ordinal);
        var kept = identity.Devices.Where(device => !forget.Contains(device.Id) && !forget.Contains("*")).ToList();
        var recorded = 0L;
        try { if (File.Exists(paths.DevicesGeneration)) _ = long.TryParse(File.ReadAllText(paths.DevicesGeneration).Trim(), out recorded); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        var generation = Math.Max(recorded, identity.DevicesGeneration) + 1;
        var list = new JsonObject
        {
            ["generation"] = generation,
            ["devices"] = JsonSerializer.SerializeToNode(kept, ServerBackupArchive.Json),
        };
        await WritePrivateAsync(Stage(paths.Devices), Seal(Encoding.UTF8.GetBytes(list.ToJsonString()))).ConfigureAwait(false);
        await WritePrivateAsync(Stage(paths.DevicesGeneration), Encoding.ASCII.GetBytes(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
        // Each phone's push address: the network host lets go of a forgotten device's at its start.
        if (identity.Push is { } push) await WritePrivateAsync(Stage(paths.Push), Seal(Encoding.UTF8.GetBytes(push.GetRawText()))).ConfigureAwait(false);
        else deletes.Add(paths.Push);
        if (identity.FirebaseSettings is { } settingsFile && identity.FirebaseAccount is { } accountFile)
        {
            await WritePrivateAsync(Stage(Path.Combine(paths.PushProject, ServerBackupPaths.FirebaseSettings)), Encoding.UTF8.GetBytes(settingsFile)).ConfigureAwait(false);
            await WritePrivateAsync(Stage(Path.Combine(paths.PushProject, ServerBackupPaths.FirebaseAccount), sealedFile: true), Seal(Encoding.UTF8.GetBytes(accountFile))).ConfigureAwait(false);
        }
        if (identity.JoinPaused) await WritePrivateAsync(Stage(paths.JoinPause), "paused"u8.ToArray()).ConfigureAwait(false);
        else deletes.Add(paths.JoinPause);
        // The key the shared state is filed under travels with its rows: without it every row would be orphaned by the next key served.
        if (identity.SpaceKey is { Length: > 0 } spaceKey)
            await WritePrivateAsync(Stage(paths.Space), Seal(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, LocalKey = spaceKey }))).ConfigureAwait(false);
        else deletes.Add(paths.Space);
        // The page's password as the old server's first run chose it (a hash), staged sealed as the other secrets are. Not when this server's password is in its
        // environment, which wins anyway; and a backup without one leaves this server's as it is, so nobody is locked out of the page.
        if (restorePagePassword && identity.PagePassword is { Length: > 0 } pagePassword)
            await WritePrivateAsync(Stage(paths.PagePassword, sealedFile: true), Seal(Encoding.UTF8.GetBytes(pagePassword))).ConfigureAwait(false);

        // state.db: the allowlisted rows, into a new file.
        var stateFile = Stage(paths.State, store: true);
        var state = new SqlitePrivateStateStore(stateFile, protector);
        var rows = 0;
        foreach (var row in contents.ReadPart<PlainRow>(ServerBackupArchive.Parts.State))
            if (LocalStateBackup.Categories.ContainsKey(row.Key.Split(':')[0]) && await state.AddAsync(row.Key, row.Value, cancellationToken).ConfigureAwait(false)) rows++;

        // The shared state, under a new epoch, every scheduled send held for the person.
        var held = 0;
        var sharedRows = new List<SharedStateStore.SealedRow>();
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in contents.ReadPart<PlainRow>(ServerBackupArchive.Parts.Shared))
        {
            if (ServerBackupExport.SharedLeftOut.Any(prefix => row.Key.StartsWith(prefix, StringComparison.Ordinal))) continue;
            var value = row.Value;
            if (row.Key.StartsWith(Protocol.SharedCategories.Scheduled + ":", StringComparison.Ordinal) && HoldScheduled(value, now) is { } heldValue) { value = heldValue; held++; }
            sharedRows.Add(new(row.Key, Seal(value), row.Since));
            sizes[row.Key] = value.Length;
            rows++;
        }
        var retired = contents.ReadPart<RetiredRow>(ServerBackupArchive.Parts.Retired).Select(row => (row.Id, row.At)).ToList();
        SharedStateStore.CreateRestored(Stage(paths.Shared, store: true), sharedRows, row => sizes[row.Key], retired, now);

        // The whole history: its rows as they were, its key sealed with this server's, under a new epoch.
        var messages = 0;
        if (contents.ReadHistory() is { } history)
        {
            using var import = HistoryDatabase.ImportRaw(Stage(paths.History, store: true), Seal(history.Key));
            foreach (var chat in contents.ReadPart<HistoryRawChat>(ServerBackupArchive.Parts.HistoryChats)) import.Chat(chat);
            foreach (var message in contents.ReadPart<HistoryRawMessage>(ServerBackupArchive.Parts.HistoryMessages)) { import.Message(message); messages++; }
            foreach (var row in contents.ReadPart<HistoryRawRow>(ServerBackupArchive.Parts.HistoryRows)) import.Row(row);
            import.Complete(history.Seq);
        }
        else deletes.Add(paths.History);

        // The search archive and the saved photos.
        using (var archive = MessageArchive.ImportRaw(Stage(paths.Archive, store: true)))
        {
            foreach (var profile in contents.ReadArchiveKeys()) archive.Profile(profile.Profile, Seal(profile.Key));
            foreach (var record in contents.ReadPart<ArchiveRawRecord>(ServerBackupArchive.Parts.Archive)) archive.Record(record);
            archive.Complete();
        }
        using (var photos = SavedPhotoStore.ImportRaw(Stage(paths.Photos, store: true)))
        {
            foreach (var photo in contents.ReadPart<BackupPhoto>(ServerBackupArchive.Parts.Photos)) photos.Photo(photo.Key, Seal(photo.Bytes), photo.Bytes.Length, photo.Kept);
            foreach (var reference in contents.ReadPart<BackupPhotoRef>(ServerBackupArchive.Parts.PhotoRefs)) photos.Reference(reference.Ref, reference.Photo, Seal(Encoding.UTF8.GetBytes(reference.Owner)));
            photos.Complete();
        }

        // Messenger's history waits for Messenger to be signed in again.
        var pages = await StageMessengerAsync(paths, protector, contents.Manifest.Messenger, contents.ReadMessengerPages(), (final, store) => Stage(final, store)).ConfigureAwait(false);

        // The backup's own settings, and the password just used, so the restored server goes on backing up under the tag it had.
        if (identity.Backup is { } backup)
        {
            var settings = new BackupSettings(Enabled: backup.Enabled, Install: BackupFolder.IsInstallTag(backup.Install) ? backup.Install : null, MessengerKept: backup.MessengerKept,
                PasswordSetAt: now);
            await WriteWholeAsync(Stage(paths.BackupSettings), JsonSerializer.SerializeToUtf8Bytes(settings)).ConfigureAwait(false);
        }
        var password = Encoding.UTF8.GetBytes(plan.Password);
        try { await WritePrivateAsync(Stage(paths.BackupPassword), Seal(password)).ConfigureAwait(false); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(password); }

        // Read by the network host: an unknown token from now on may be a device that joined after this backup.
        var mark = Stage(paths.Restored);
        await RestoredMark.WriteAsync(mark, protector, now, contents.Manifest.Created).ConfigureAwait(false);
        // Whatever an earlier restore left waiting belongs to the server that was; this one's own is staged above.
        deletes.Add(Path.Combine(paths.Restore, PendingFolder));
        return new Commit(1, moves, deletes, new(RestoreOutcomes.Restored, "server", now, kept.Count, identity.Devices.Count - kept.Count, held, rows, messages, pages));
    }

    // A PC's backup: no identity. Its Messenger pages and its choices wait for the accounts they belong to.
    private static async Task<Commit> BuildFromPcAsync(ServerBackupPaths paths, IStateProtector protector, Stream reader, string staged, List<Move> moves, List<string> deletes,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        string Stage(string final, bool store = false)
        {
            var at = Path.Combine(staged, moves.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "-" + Path.GetFileName(final));
            moves.Add(new(at, final, store));
            return at;
        }
        using var contents = BackupContents.Open(reader);
        var local = contents.ReadLocal();
        var pages = await StageMessengerAsync(paths, protector, contents.Manifest.Messenger, contents.ReadPages(), Stage).ConfigureAwait(false);
        var plain = JsonSerializer.SerializeToUtf8Bytes(local, BackupArchive.Json);
        try { await WritePrivateAsync(Stage(PendingImports.PcLocalPath(paths)), await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        cancellationToken.ThrowIfCancellationRequested();
        var count = local.State.Length + local.ChatPreferences.Length + local.ContactNames.Length;
        return new Commit(1, moves, deletes, new(RestoreOutcomes.Restored, "pc", now, MessengerPages: pages, PcRows: count));
    }

    // Each page sealed into its own file, with the account it belongs to: taken in once Messenger is signed in again (PendingImports).
    private static async Task<int> StageMessengerAsync(ServerBackupPaths paths, IStateProtector protector, BackupMessengerSummary? summary, IEnumerable<JsonElement> pages,
        Func<string, bool, string> stage)
    {
        if (summary is null) return 0;
        var number = 0;
        foreach (var page in pages)
        {
            var plain = Encoding.UTF8.GetBytes(page.GetRawText());
            await WritePrivateAsync(stage(PendingImports.MessengerPagePath(paths, number++), false), await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false);
        }
        await WritePrivateAsync(stage(PendingImports.MessengerAccountPath(paths), false),
            JsonSerializer.SerializeToUtf8Bytes(new PendingImports.MessengerPending(summary.Account, number))).ConfigureAwait(false);
        return number;
    }

    /// <summary>
    /// A scheduled message's row, held as not sent, never sent by the restored server by itself; null when it is held already or is not a message of the schedule. A message
    /// still to come is held too (owner, 2026-11-01): the backup cannot know it was cancelled since, which sent it anyway, or sent early, which sent it twice. It has no reason
    /// of its own, as a client of an older version must know any word a row holds, so the clients word no reason as this one (ScheduledSendRows.Describe).
    /// </summary>
    internal static byte[]? HoldScheduled(byte[] value, DateTimeOffset now)
    {
        ScheduledSend? item;
        try { item = JsonSerializer.Deserialize<ScheduledSend>(value); }
        catch (JsonException) { return null; }
        if (item is not { Id.Length: > 0, ChatId.Length: > 0 } || item.State is not (ScheduledSendState.Sending or ScheduledSendState.Waiting)) return null;
        var reason = item.State == ScheduledSendState.Sending || item.Due <= now ? ScheduledSendReason.Closed : ScheduledSendReason.None;
        return JsonSerializer.SerializeToUtf8Bytes(item with { State = ScheduledSendState.NotSent, Reason = reason, SendingAt = null });
    }

    // The deletes first, then the commit list again without them (so a resumed finish never deletes what a move has since put there), then the moves, each made once: a
    // file already moved (its staged copy gone, the target there) is done. A store's old log goes only once the new store is in place, and again on a resumed finish: nothing
    // opens a store between the two (the server refuses to open while the list is there), so a log beside a moved store is always the old one's. A failure here is thrown,
    // never swept or called "nothing was changed": the list stays, and the next start finishes it.
    private static void Finish(ServerBackupPaths paths, IStateProtector protector, Commit commit)
    {
        if (commit.Deletes.Count > 0)
        {
            foreach (var path in commit.Deletes)
            {
                foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
                    if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            var commitPath = Path.Combine(paths.Restore, CommitFile);
            File.WriteAllBytes(commitPath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(commit with { Deletes = [] }));
            File.Move(commitPath + ".tmp", commitPath, overwrite: true);
        }
        foreach (var move in commit.Moves)
        {
            if (File.Exists(move.From))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                if (move.Sealed)
                {
                    // Unsealed only into its place: written whole beside the target, then moved over it, then the staged copy goes.
                    var plain = protector.UnprotectAsync(File.ReadAllBytes(move.From)).GetAwaiter().GetResult();
                    try { WritePrivateAsync(move.To, plain).GetAwaiter().GetResult(); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
                    File.Delete(move.From);
                }
                else File.Move(move.From, move.To, overwrite: true);
            }
            if (move.Store)
                foreach (var leftover in new[] { move.To + "-wal", move.To + "-shm", move.To + "-journal" })
                    if (File.Exists(leftover)) File.Delete(leftover);
        }
        WriteOutcome(paths, commit.Outcome);
        TrySweep(Path.Combine(paths.Restore, StagedFolder));
        foreach (var name in new[] { PlanFile, CommitFile })
            if (File.Exists(Path.Combine(paths.Restore, name))) File.Delete(Path.Combine(paths.Restore, name));
    }

    private static void WriteOutcome(ServerBackupPaths paths, RestoreOutcome outcome)
    {
        try
        {
            Directory.CreateDirectory(paths.Restore);
            var path = Path.Combine(paths.Restore, OutcomeFile);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(outcome));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    // Written whole, flushed, and moved into place, private to the server's account off Windows.
    internal static async Task WritePrivateAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var file = new FileStream(temporary, options))
        {
            await file.WriteAsync(bytes).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static Task WriteWholeAsync(string path, byte[] bytes) => WritePrivateAsync(path, bytes);
}
