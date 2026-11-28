using System.Security.Cryptography;
using System.Text.Json;
using MessageLink.App.Services.Backup;

namespace MessageLink.Server.Backup;

internal sealed partial class ServerBackupService
{
    private BackupDestination? destination;
    private bool destinationUnreadable;
    private int remoteKept;
    private DateTimeOffset? destinationNotBefore;
    // A share browsed or chosen that refused the sign-in or didn't answer: holds the next sign-in to any share, browsed or chosen, but not a local folder.
    private DateTimeOffset? shareNotBefore;
    // One sign-in to a file server at a time from the page (browse, new folder, choice, test), the hold read after the one before has finished: two requests at
    // once can't both get past it to the file server.
    private readonly SemaphoreSlim shareSignIns = new(1, 1);
    private DateTimeOffset? remoteNotBefore;
    private string DestinationFile => Path.Combine(paths.Backup, "destination.protected");
    internal int SmbPort { get; init; } = 445; // Only the isolated fixture changes the port.
    public bool UsesSmb => destination?.Kind == "smb";
    public bool DestinationUnreadable => destinationUnreadable;

    private void LoadDestination()
    {
        if (!File.Exists(DestinationFile)) return;
        try
        {
            if (new FileInfo(DestinationFile).Length > 16384) throw new InvalidDataException();
            var plain = protector.UnprotectAsync(File.ReadAllBytes(DestinationFile)).GetAwaiter().GetResult();
            try { destination = JsonSerializer.Deserialize<BackupDestination>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (destination is null || (destination.Kind == "smb" ? !BackupDestination.ValidSmb(destination) :
                destination.Kind != "local" || !Path.IsPathFullyQualified(destination.Local!))) throw new InvalidDataException();
            folder = destination.Display;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or ArgumentException)
        {
            // Never fall back to a different disk when a saved share cannot be read.
            destination = null;
            folder = null;
            destinationUnreadable = true;
            log("server backup destination outcome=unreadable");
        }
    }

    public Task<string> ChooseSmbAsync(string server, string share, string subfolder, string user, string domain, string password,
        CancellationToken token) => ChooseDestinationAsync(new("smb", Server: server.Trim(), Share: share.Trim(),
            Subfolder: subfolder.Trim().Replace('/', '\\'), User: user, Domain: domain, Password: password), token);

    /// <summary>
    /// The sign-in of the share in use, for the page to hold while a person browses that file server again without typing its password (ServerPage.BackupBrowse.cs);
    /// null when the location isn't a share. Never put into a page, an address or a log.
    /// </summary>
    public BackupDestination? SavedShare => destination is { Kind: "smb" } saved ? saved with { Id = null } : null;

    /// <summary>
    /// For the backup location's browser: a file server's shares (no share named) or the folders in a share's subfolder, with "ok" or why not. A sign-in that is
    /// refused or a server that doesn't answer holds every browse and choice of a share for 30 seconds, as a failed choice of a share does: never a sign-in loop.
    /// </summary>
    public async Task<ShareListing> BrowseSmbAsync(BackupDestination signIn, CancellationToken token)
    {
        signIn = signIn with { Server = signIn.Server?.Trim(), Share = signIn.Share?.Trim() ?? "", Subfolder = signIn.Subfolder?.Trim().Replace('/', '\\').Trim('\\') ?? "" };
        if (!BackupDestination.ValidSignIn(signIn) || signIn.Share!.Length > 0 && !BackupDestination.ValidSmb(signIn)) return new("path", []);
        await shareSignIns.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (shareNotBefore > time.GetUtcNow()) return new("wait", []);
            try { return await Task.Run(() => SmbBackupDestination.Browse(signIn, token, SmbPort), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                var code = ShareFailed(e);
                log($"server backup browse share outcome={code}");
                return new(code, []);
            }
        }
        finally { shareSignIns.Release(); }
    }

    /// <summary>
    /// For the browser's New folder on a share: a folder by a plain name in the share's subfolder, with the browser's own sign-in and its 30-second hold. "made",
    /// "exists", "name", "path", "wait", or why the share refused.
    /// </summary>
    public async Task<string> MakeSmbFolderAsync(BackupDestination place, string? name, CancellationToken token)
    {
        name = name?.Trim();
        if (!BackupDestination.Segment(name)) return "name";
        place = place with { Server = place.Server?.Trim(), Share = place.Share?.Trim(), Subfolder = place.Subfolder?.Trim().Replace('/', '\\').Trim('\\') ?? "" };
        if (!BackupDestination.ValidSmb(place)) return "path";
        await shareSignIns.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (shareNotBefore > time.GetUtcNow()) return "wait";
            var made = await Task.Run(() => SmbBackupDestination.MakeFolder(place, name!, token, SmbPort), token).ConfigureAwait(false);
            log($"server backup browse share_folder outcome={made}");
            return made;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            var code = ShareFailed(e);
            log($"server backup browse share_folder outcome={code}");
            return code;
        }
        finally { shareSignIns.Release(); }
    }

    // Why a share refused, holding the next sign-in for 30 seconds when it was the sign-in or the server: a share or folder that isn't there is the person's own
    // choice, said at once.
    private string ShareFailed(Exception e)
    {
        var code = e is BackupDestinationException failure ? failure.Code : "unavailable";
        if (code is "auth" or "unavailable") shareNotBefore = time.GetUtcNow() + TimeSpan.FromSeconds(30);
        return code;
    }

    private async Task<string> ChooseDestinationAsync(BackupDestination chosen, CancellationToken token)
    {
        if (!await running.WaitAsync(0, token).ConfigureAwait(false)) return "busy";
        var oneSignIn = false;
        try
        {
            if (chosen.Kind == "smb") { await shareSignIns.WaitAsync(token).ConfigureAwait(false); oneSignIn = true; }
            if (destinationNotBefore > time.GetUtcNow() || chosen.Kind == "smb" && shareNotBefore > time.GetUtcNow()) return "wait";
            if (chosen.Kind == "smb" && !BackupDestination.ValidSmb(chosen)) return "path";
            var same = SameLocation(chosen, destination ?? (folder is null ? null : new("local", Local: folder)));
            chosen = chosen with { Id = same ? destination?.Id ?? Settings.DestinationId ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N") };
            try
            {
                if (chosen.Kind == "smb") await Task.Run(() => { using var smb = new SmbBackupDestination(chosen, token, SmbPort); smb.Probe(); }, token).ConfigureAwait(false);
                else ProbeLocal(chosen.Local!);
                var plain = JsonSerializer.SerializeToUtf8Bytes(chosen);
                try { await ServerRestore.WritePrivateAsync(DestinationFile, await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // Applies to page submissions too, so a failing share cannot become a tight authentication loop.
                destinationNotBefore = time.GetUtcNow() + TimeSpan.FromSeconds(30);
                var code = e is BackupDestinationException failure ? failure.Code : e is UnauthorizedAccessException ? "denied" :
                    e is System.Net.Sockets.SocketException or TimeoutException ? "unavailable" : "unsaved";
                // A share that refused the sign-in or didn't answer holds the browser too; one that signed in and refused the folder doesn't, so its list
                // can open again at once to choose another folder.
                if (chosen.Kind == "smb" && code is "auth" or "unavailable") shareNotBefore = time.GetUtcNow() + TimeSpan.FromSeconds(30);
                return code;
            }
            destination = chosen;
            lock (gate) remoteNotBefore = null;
            destinationUnreadable = false;
            folder = chosen.Display;
            if (!same) remoteKept = 0;
            // A different destination has not received a good backup yet. Retain the per-account retention anchors.
            BackupSettings Selected(BackupSettings current) => same ? current with { DestinationId = chosen.Id,
                LastFailure = current.LastFailure == "auth" ? null : current.LastFailure, LastFailureAt = current.LastFailure == "auth" ? null : current.LastFailureAt } : ForDestination(current, chosen.Id);
            try { Update(Selected); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log("server backup settings outcome=unsaved");
                lock (gate) settings = Selected(settings) with { Folder = folder, LastFailure = "settings", LastFailureAt = time.GetUtcNow() };
                return "chosen_settings";
            }
            log("server backup destination outcome=chosen");
            return "chosen";
        }
        finally
        {
            if (oneSignIn) shareSignIns.Release();
            running.Release();
        }
    }

    /// <summary>
    /// Test, on the page's Backup location screen: the location in use gets the check a new choice gets (a test file written, read back, renamed and removed),
    /// with its saved sign-in, and nothing is changed but what the check proves: a share whose refused sign-in paused backups works again, so they resume, and
    /// one that refuses it now pauses them, as a refused backup would. "ok", "none" (no location), "busy", "wait" (a share's 30-second hold after a refused
    /// sign-in or a server that didn't answer, which a failure here sets too), or why it failed.
    /// </summary>
    public async Task<string> TestDestinationAsync(CancellationToken token)
    {
        if (!await running.WaitAsync(0, token).ConfigureAwait(false)) return "busy";
        var oneSignIn = false;
        try
        {
            // Read under the lock a choice takes, so the location tested is the one the page then names.
            var selected = destination;
            var local = folder;
            if (selected is null && local is null) return "none";
            var smb = selected?.Kind == "smb";
            if (smb) { await shareSignIns.WaitAsync(token).ConfigureAwait(false); oneSignIn = true; }
            if (smb && (shareNotBefore > time.GetUtcNow() || destinationNotBefore > time.GetUtcNow())) return "wait";
            try
            {
                if (smb) await Task.Run(() => { using var share = new SmbBackupDestination(selected!, token, SmbPort); share.Probe(); }, token).ConfigureAwait(false);
                else await Task.Run(() => ProbeLocal(local!), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // "write" only when the file read back differently; a disk that filled up, or one that failed under the test, says so.
                var code = e is BackupDestinationException failure ? failure.Code : e is UnauthorizedAccessException ? "denied"
                    : smb ? "unavailable" : e is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } ? "space" : "io";
                if (smb && code is "auth" or "unavailable")
                {
                    shareNotBefore = time.GetUtcNow() + TimeSpan.FromSeconds(30);
                    // The saved sign-in refused: the scheduled backup would only be refused too, so backups wait for it as they do when a backup is refused
                    // (OpenSmb); a share that didn't answer holds the backup's next try as a failed backup does.
                    if (code == "auth")
                        try { Update(current => current with { LastFailure = "auth", LastFailureAt = time.GetUtcNow() }); }
                        catch (Exception save) when (save is IOException or UnauthorizedAccessException) { lock (gate) settings = settings with { LastFailure = "auth", LastFailureAt = time.GetUtcNow() }; }
                    else RemoteFailed();
                }
                log($"server backup test outcome={code}");
                return code;
            }
            lock (gate) remoteNotBefore = null;
            if (smb && Settings.LastFailure == "auth")
            {
                try { Update(current => current with { LastFailure = null, LastFailureAt = null }); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lock (gate) settings = settings with { LastFailure = null, LastFailureAt = null }; }
            }
            log("server backup test outcome=ok");
            return "ok";
        }
        finally
        {
            if (oneSignIn) shareSignIns.Release();
            running.Release();
        }
    }

    private static bool SameLocation(BackupDestination chosen, BackupDestination? previous) => previous is not null && chosen.Kind == previous.Kind &&
        (chosen.Kind == "smb" ? string.Equals(chosen.Server, previous.Server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(chosen.Share, previous.Share, StringComparison.OrdinalIgnoreCase) && string.Equals(chosen.Subfolder, previous.Subfolder, StringComparison.Ordinal) :
            string.Equals(chosen.Local, previous.Local, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private bool AuthenticationBlocked => UsesSmb && Settings.LastFailure == "auth";

    // The protected destination is the commit point. If its settings save was interrupted, a new process must not
    // attribute the previous location's successful backup to this one.
    private static BackupSettings ForDestination(BackupSettings current, string? id) => current with
    {
        // Keep history anchors if the person switches back; absent names protect nothing at the new destination.
        DestinationId = id, LastSuccess = null, LastWrittenAt = null, LastName = null, LastBytes = 0, LastFailure = null, LastFailureAt = null,
        LastVerifiedAt = null, LastVerifyResult = null, MessengerLeftOut = null,
    };

    private SmbBackupDestination OpenSmb(BackupDestination selected, CancellationToken token)
    {
        if (AuthenticationBlocked) throw new BackupDestinationException("auth");
        lock (gate) if (remoteNotBefore > time.GetUtcNow()) throw new BackupDestinationException("unavailable");
        try { return new SmbBackupDestination(selected, token, SmbPort); }
        catch (BackupDestinationException e) when (e.Code == "auth")
        {
            try { Update(current => current with { LastFailure = "auth", LastFailureAt = time.GetUtcNow() }); }
            catch (Exception save) when (save is IOException or UnauthorizedAccessException)
            { lock (gate) settings = settings with { LastFailure = "auth", LastFailureAt = time.GetUtcNow() }; }
            throw;
        }
        catch (IOException) { RemoteFailed(); throw; }
    }

    private void RemoteFailed() { lock (gate) remoteNotBefore = time.GetUtcNow() + TimeSpan.FromSeconds(30); }

    private static void ProbeLocal(string chosen)
    {
        if (!Directory.Exists(chosen)) throw new BackupDestinationException("missing");
        var name = Path.Combine(chosen, ".message-link-check-" + Guid.NewGuid().ToString("N"));
        var renamed = name + "-done";
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            using (var file = new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(name))) throw new BackupDestinationException("write");
            File.Move(name, renamed, false);
            File.Delete(renamed);
            _ = Directory.GetFiles(chosen);
        }
        finally { if (File.Exists(name)) File.Delete(name); if (File.Exists(renamed)) File.Delete(renamed); }
    }

    private string ScratchFolder()
    {
        var scratch = Path.Combine(paths.Backup, "transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static void RemoveScratch(string scratch)
    {
        // Contains only ciphertext we created, with no child directories or links.
        try { foreach (var file in Directory.GetFiles(scratch)) File.Delete(file); Directory.Delete(scratch); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private Written WriteDestination(string target, BackupSettings current, string password, bool includeMessenger, string? leftOut, CancellationToken token)
    {
        if (!UsesSmb) return Write(target, current, password, includeMessenger, leftOut, token);
        // A session only to reach the share (so an unreachable one fails before the archive is written) and sweep its leftovers. Then the archive is written
        // with no session open, and a new one publishes: the client sends no keep-alives, and a share that drops idle sessions (Windows after 15 minutes)
        // failed every long backup at its publish.
        using (var first = OpenSmb(destination!, token))
            foreach (var old in first.List().Where(f => BackupFolder.IsOwnTemporaryName(f.Name, current.Install!) && time.GetUtcNow() - f.Written > TemporaryAge))
                first.Delete(old.Name);
        var scratch = ScratchFolder();
        try
        {
            Written written;
            try { written = Write(scratch, current, password, includeMessenger, leftOut, token); }
            catch (BackupFailedException e) when (e.Reason == "space") { throw new BackupFailedException("local_space", e); }
            progress?.Enter(ServerBackupSteps.Copying);
            using var smb = OpenSmb(destination!, token);
            var files = smb.List();
            var occupied = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? name = null;
            for (var copy = 1; copy < 1000; copy++)
            {
                var candidate = BackupFolder.FileName(time.GetUtcNow().ToLocalTime().DateTime, current.Install!, copy);
                if (!occupied.Contains(candidate) && !occupied.Contains("Messages" + candidate["Message Link".Length..])) { name = candidate; break; }
            }
            if (name is null) throw new IOException();
            smb.Publish(Path.Combine(scratch, written.Name), name);
            var kept = written.Manifest.RetentionNames(current, name);
            var pruned = new List<string>();
            var spared = 0;
            foreach (var old in BackupFolder.OrderOwnNames(files.Select(f => f.Name).Append(name), current.Install!).Skip(BackupFolder.Keep))
            {
                if (kept.Contains(old)) { spared++; continue; }
                try { smb.Delete(old); pruned.Add(old); } catch (IOException) { log("server backup retention outcome=failed"); break; }
            }
            remoteKept = files.Count(f => BackupFolder.IsOwnBackupName(f.Name, current.Install!)) + 1 - pruned.Count;
            return written with { Name = name, Pruned = new(pruned, spared) };
        }
        finally { RemoveScratch(scratch); }
    }

    private BackupCheckResult CheckDestination(BackupSettings current, string password, CancellationToken token)
    {
        if (!UsesSmb) return ServerBackupCheck.Run(folder!, current.Install!, current.LastName, password, token, current.PasswordSetAt);
        var scratch = ScratchFolder();
        try
        {
            using var smb = OpenSmb(destination!, token);
            var files = smb.List();
            remoteKept = files.Count(f => BackupFolder.IsOwnBackupName(f.Name, current.Install!));
            var name = files.Any(f => f.Name == current.LastName && BackupFolder.IsOwnBackupName(f.Name, current.Install!)) ? current.LastName :
                BackupFolder.OrderOwnNames(files.Select(f => f.Name), current.Install!).FirstOrDefault();
            if (name is null) return new("missing", null, 0, 0, 0);
            var entry = files.Single(f => f.Name == name);
            var local = Path.Combine(scratch, name);
            using (var output = File.Create(local)) smb.Download(name, output);
            File.SetLastWriteTimeUtc(local, entry.Written.UtcDateTime);
            return ServerBackupCheck.Run(scratch, current.Install!, name, password, token, current.PasswordSetAt);
        }
        catch (Exception e) when (e is not OperationCanceledException) { RemoteFailed(); return new("unavailable", null, 0, 0, 0); }
        finally { RemoveScratch(scratch); }
    }

    public Task<IReadOnlyList<BackupListing>> ListRestoreAsync(CancellationToken token) => Task.Run<IReadOnlyList<BackupListing>>(() =>
    {
        var selected = destination;
        var localFolder = folder;
        if (selected?.Kind != "smb")
        {
            if (localFolder is null) return [];
            return new DirectoryInfo(localFolder).EnumerateFiles().Where(f => f.Name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase) &&
                (f.Attributes & FileAttributes.ReparsePoint) == 0).Select(f => new BackupListing(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc)))
                .OrderByDescending(f => f.Written).ToArray();
        }
        try
        {
            using var smb = OpenSmb(selected, token);
            return smb.List().Where(f => f.Name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Written).ToArray();
        }
        catch (IOException) { RemoteFailed(); throw; }
    }, token);

    // Review and restart use the same encrypted snapshot, even if retention or another writer changes the destination.
    public Task<string> PrepareRestoreAsync(string name, CancellationToken token) => Task.Run(() =>
    {
        if (!BackupDestination.Segment(name) || !name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase)) throw new IOException();
        var selected = destination;
        var localFolder = folder;
        var downloads = Path.Combine(paths.Restore, "downloads");
        Directory.CreateDirectory(downloads);
        var local = Path.Combine(downloads, Guid.NewGuid().ToString("N") + BackupFile.Extension);
        try
        {
            using var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (selected?.Kind == "smb") { using var smb = OpenSmb(selected, token); smb.Download(name, output); }
            else
            {
                var source = ServerRestore.PathOf(localFolder, name) ?? throw new IOException();
                using var input = File.OpenRead(source);
                input.CopyToAsync(output, token).GetAwaiter().GetResult();
            }
            output.Flush(true);
            return local;
        }
        catch { if (selected?.Kind == "smb") RemoteFailed(); try { File.Delete(local); } catch (IOException) { } throw; }
    }, token);

    public void DiscardPrepared(string name)
    {
        if (!name.EndsWith(BackupFile.Extension, StringComparison.Ordinal) || !Guid.TryParseExact(name[..^BackupFile.Extension.Length], "N", out _)) return;
        try { File.Delete(Path.Combine(paths.Restore, "downloads", name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SweepTransfers()
    {
        try
        {
            if (Directory.Exists(paths.Backup))
                foreach (var directory in Directory.GetDirectories(paths.Backup, "transfer-*"))
                    if (Guid.TryParseExact(Path.GetFileName(directory)["transfer-".Length..], "N", out _) &&
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && Directory.GetDirectories(directory).Length == 0)
                        RemoveScratch(directory);
            var downloads = Path.Combine(paths.Restore, "downloads");
            if (!ServerRestore.Planned(paths) && Directory.Exists(downloads))
                foreach (var file in Directory.GetFiles(downloads))
                    if (time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1)) DiscardPrepared(Path.GetFileName(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("server backup temporary outcome=unswept"); }
    }
}
