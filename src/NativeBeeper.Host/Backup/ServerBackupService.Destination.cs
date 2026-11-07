using System.Security.Cryptography;
using System.Text.Json;
using NativeBeeper.App.Services.Backup;

namespace NativeBeeper.Host.Backup;

internal sealed partial class ServerBackupService
{
    private BackupDestination? destination;
    private bool destinationUnreadable;
    private int remoteKept;
    private DateTimeOffset? destinationNotBefore;
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

    private async Task<string> ChooseDestinationAsync(BackupDestination chosen, CancellationToken token)
    {
        if (!await running.WaitAsync(0, token).ConfigureAwait(false)) return "busy";
        try
        {
            if (destinationNotBefore > time.GetUtcNow()) return "wait";
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
                return e is BackupDestinationException failure ? failure.Code : e is UnauthorizedAccessException ? "denied" :
                    e is System.Net.Sockets.SocketException or TimeoutException ? "unavailable" : "unsaved";
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
        finally { running.Release(); }
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
            if (!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(name))) throw new IOException();
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
        using var smb = OpenSmb(destination!, token);
        var files = smb.List();
        foreach (var old in files.Where(f => BackupFolder.IsOwnTemporaryName(f.Name, current.Install!) && time.GetUtcNow() - f.Written > TemporaryAge))
            smb.Delete(old.Name);
        var scratch = ScratchFolder();
        try
        {
            Written written;
            try { written = Write(scratch, current, password, includeMessenger, leftOut, token); }
            catch (BackupFailedException e) when (e.Reason == "space") { throw new BackupFailedException("local_space", e); }
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
