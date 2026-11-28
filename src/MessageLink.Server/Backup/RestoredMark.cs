using System.Security.Cryptography;
using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Server.Backup;

/// <summary>
/// The record a restore leaves (<c>&lt;data&gt;/restored.protected</c>, sealed with the server's own key), so a device that joined after the backup was made is told
/// what happened rather than only that its token is unknown ("Restore: every case").
/// </summary>
/// <remarks>
/// A device's token is checked against the list the backup brought back. One the list does not know was never in it: the device joined after the backup, or was forgotten
/// in the restore's review, or was removed before the backup. The server cannot tell those apart (it keeps only the hashes of tokens it holds), and all of them have one
/// way back, which is to ask to join again; so for <see cref="Window"/> after a restore an unknown token is refused with the word <c>restored</c>. A device removed
/// after the restore is told only that it was removed: each removal from then on puts its token's hash here (<see cref="NoteRevokedAsync"/>), which is no more than
/// devices.json held. Nothing here names a device or a person.
/// </remarks>
internal sealed class RestoredMark
{
    public const string FileName = "restored.protected";
    /// <summary>How long an unknown token is answered as a device from before the restore. A device left off for longer is told only that its token is unknown.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(365);
    private const int MaxRevoked = 256;

    private sealed record Stored(int Version, DateTimeOffset RestoredAt, DateTimeOffset BackupCreated, List<string> Revoked);

    private readonly string path;
    private readonly IStateProtector protector;
    private readonly object gate = new();
    // One write at a time, each of the newest record: two removals at once shared the temporary file, so one failed or an older list was left on the disk.
    private readonly SemaphoreSlim writing = new(1, 1);
    private Stored stored;

    private RestoredMark(string path, IStateProtector protector, Stored stored) { this.path = path; this.protector = protector; this.stored = stored; }

    public DateTimeOffset RestoredAt => stored.RestoredAt;
    public DateTimeOffset BackupCreated => stored.BackupCreated;

    /// <summary>Writes a new record (a restore just finished), replacing any earlier one.</summary>
    public static async Task WriteAsync(string path, IStateProtector protector, DateTimeOffset restoredAt, DateTimeOffset backupCreated) =>
        await SaveAsync(path, protector, new Stored(1, restoredAt, backupCreated, [])).ConfigureAwait(false);

    /// <summary>The record, or null when there is none or it cannot be read (which then answers nothing specially).</summary>
    public static async Task<RestoredMark?> LoadAsync(string path, IStateProtector protector)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 << 10) return null;
            var stored = JsonSerializer.Deserialize<Stored>(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false));
            return stored is { Version: 1, Revoked: not null } ? new RestoredMark(path, protector, stored) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException) { return null; }
    }

    /// <summary>Whether a token the device list does not know (by its SHA-256) is answered as a device from before the restore.</summary>
    public bool RefusesAsRestored(byte[] tokenHash, DateTimeOffset now)
    {
        lock (gate)
        {
            if (now - stored.RestoredAt > Window || now < stored.RestoredAt - TimeSpan.FromDays(1)) return false;
            var hash = Convert.ToBase64String(tokenHash);
            return !stored.Revoked.Contains(hash, StringComparer.Ordinal);
        }
    }

    /// <summary>Notes a device removed since the restore, by its token's hash, so it is told it was removed. The oldest notes go past <see cref="MaxRevoked"/>.</summary>
    public async Task NoteRevokedAsync(byte[] tokenHash)
    {
        lock (gate)
        {
            var revoked = stored.Revoked.Append(Convert.ToBase64String(tokenHash)).Distinct(StringComparer.Ordinal).TakeLast(MaxRevoked).ToList();
            stored = stored with { Revoked = revoked };
        }
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            Stored latest;
            lock (gate) latest = stored;
            await SaveAsync(path, protector, latest).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { /* Kept in memory for this run; the device is removed either way. */ }
        finally { writing.Release(); }
    }

    private static async Task SaveAsync(string path, IStateProtector protector, Stored stored) =>
        await ServerRestore.WritePrivateAsync(path, await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(stored)).ConfigureAwait(false)).ConfigureAwait(false);
}
