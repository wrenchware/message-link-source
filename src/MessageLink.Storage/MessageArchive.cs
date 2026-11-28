using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;

namespace MessageLink.Storage;

// Expendable, rebuildable local archive for search. Each profile has one random key pair
// protected by IStateProtector; records are AES-GCM encrypted in process with their row id
// as associated data. Native chat/message identities are stored only as keyed hashes.
public sealed partial class MessageArchive(string path, IStateProtector protector)
{
    public enum Kind { Chat = 1, Message = 2, Contacts = 3, CrawlState = 4 }
    public sealed record Entry(Kind Kind, string Id, byte[] Payload);
    private const int RecordLimit = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, byte[]> keys = [];

    private static string ProfileHash(string profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)));
    private static string RowId(byte[] key, Kind kind, string id) =>
        Convert.ToHexString(HMACSHA256.HashData(key.AsSpan(32, 32), Encoding.UTF8.GetBytes((int)kind + "\n" + id)));

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > 1) throw new InvalidDataException("Search archive requires a newer app.");
            if (version == 0)
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS meta(profile TEXT PRIMARY KEY, key BLOB NOT NULL);" +
                    "CREATE TABLE IF NOT EXISTS records(id TEXT PRIMARY KEY, profile TEXT NOT NULL, kind INTEGER NOT NULL, payload BLOB NOT NULL);" +
                    "CREATE INDEX IF NOT EXISTS records_profile_kind ON records(profile, kind); PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    // Caller holds the gate.
    private async Task<byte[]> KeyAsync(string profileHash)
    {
        if (keys.TryGetValue(profileHash, out var cached)) return cached;
        var stored = await Task.Run(() =>
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM meta WHERE profile=$profile"; command.Parameters.AddWithValue("$profile", profileHash);
            return command.ExecuteScalar() as byte[];
        });
        byte[]? key = null;
        if (stored is not null)
        {
            try { key = await protector.UnprotectAsync(stored); } catch { key = null; }
            if (key is { Length: not 64 }) key = null;
        }
        if (key is null)
        {
            // Missing or unreadable key: this profile's archive is unrecoverable, so start over.
            key = RandomNumberGenerator.GetBytes(64);
            var protectedKey = await protector.ProtectAsync(key);
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM records WHERE profile=$profile; INSERT INTO meta VALUES($profile,$key) ON CONFLICT(profile) DO UPDATE SET key=excluded.key";
                command.Parameters.AddWithValue("$profile", profileHash); command.Parameters.AddWithValue("$key", protectedKey);
                command.ExecuteNonQuery(); transaction.Commit();
            });
        }
        keys[profileHash] = key;
        return key;
    }

    private static byte[] Encrypt(byte[] key, string rowId, byte[] plain)
    {
        var output = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key.AsSpan(0, 32), 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes(rowId));
        return output;
    }

    private static byte[]? Decrypt(byte[] key, string rowId, byte[] payload)
    {
        if (payload.Length < 28 || payload.Length - 28 > RecordLimit) return null;
        try
        {
            var plain = new byte[payload.Length - 28];
            using var aes = new AesGcm(key.AsSpan(0, 32), 16);
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(rowId));
            return plain;
        }
        catch (CryptographicException) { return null; } // Corrupt or swapped rows are misses.
    }

    public async Task UpsertAsync(string profile, IReadOnlyList<Entry> entries, CancellationToken token = default)
    {
        if (entries.Count == 0) return;
        await gate.WaitAsync(token);
        try
        {
            var profileHash = ProfileHash(profile);
            var key = await KeyAsync(profileHash);
            var rows = entries.Where(e => e.Payload.Length <= RecordLimit)
                .Select(e => { var id = RowId(key, e.Kind, e.Id); return (id, e.Kind, payload: Encrypt(key, id, e.Payload)); }).ToArray();
            token.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO records VALUES($id,$profile,$kind,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                var id = command.Parameters.Add("$id", SqliteType.Text); var owner = command.Parameters.Add("$profile", SqliteType.Text);
                var kind = command.Parameters.Add("$kind", SqliteType.Integer); var payload = command.Parameters.Add("$payload", SqliteType.Blob);
                owner.Value = profileHash;
                foreach (var row in rows) { id.Value = row.id; kind.Value = (int)row.Kind; payload.Value = row.payload; command.ExecuteNonQuery(); }
                transaction.Commit();
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<byte[]>> ReadAllAsync(string profile, Kind kind, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var profileHash = ProfileHash(profile);
            var key = await KeyAsync(profileHash);
            return await Task.Run(() =>
            {
                var result = new List<byte[]>();
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, payload FROM records WHERE profile=$profile AND kind=$kind";
                command.Parameters.AddWithValue("$profile", profileHash); command.Parameters.AddWithValue("$kind", (int)kind);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (Decrypt(key, reader.GetString(0), (byte[])reader[1]) is { } plain) result.Add(plain);
                }
                return (IReadOnlyList<byte[]>)result;
            }, token);
        }
        finally { gate.Release(); }
    }

    // Release the database gate between bounded batches. Startup must not retain every decrypted message
    // while building its search index, nor prevent live writes while the consumer yields to user input.
    // The existing (profile, kind) index includes rowid, so this seeks rather than rescanning an OFFSET prefix.
    public async IAsyncEnumerable<IReadOnlyList<byte[]>> ReadBatchesAsync(string profile, Kind kind,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        long after = 0;
        while (true)
        {
            await gate.WaitAsync(token);
            List<byte[]> batch;
            var previous = after;
            try
            {
                var profileHash = ProfileHash(profile);
                var key = await KeyAsync(profileHash);
                (batch, after) = await Task.Run(() =>
                {
                    var result = new List<byte[]>();
                    long last = after;
                    var bytes = 0;
                    using var connection = Open(); using var command = connection.CreateCommand();
                    command.CommandText = "SELECT rowid,id,payload FROM records WHERE profile=$profile AND kind=$kind AND rowid>$after ORDER BY rowid LIMIT 128";
                    command.Parameters.AddWithValue("$profile", profileHash); command.Parameters.AddWithValue("$kind", (int)kind);
                    command.Parameters.AddWithValue("$after", after);
                    using var reader = command.ExecuteReader();
                    while (bytes < 1024 * 1024 && reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        last = reader.GetInt64(0); // Corrupt rows must advance the cursor too.
                        var encrypted = (byte[])reader[2];
                        bytes += encrypted.Length;
                        if (Decrypt(key, reader.GetString(1), encrypted) is { } plain) result.Add(plain);
                    }
                    return (result, last);
                }, token);
            }
            finally { gate.Release(); }
            if (after == previous) yield break;
            yield return batch;
        }
    }

    public async Task<byte[]?> ReadAsync(string profile, Kind kind, string id, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var profileHash = ProfileHash(profile);
            var key = await KeyAsync(profileHash);
            var rowId = RowId(key, kind, id);
            var payload = await Task.Run(() =>
            {
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload FROM records WHERE id=$id AND profile=$profile";
                command.Parameters.AddWithValue("$id", rowId); command.Parameters.AddWithValue("$profile", profileHash);
                return command.ExecuteScalar() as byte[];
            }, token);
            return payload is null ? null : Decrypt(key, rowId, payload);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string profile, Kind kind, IReadOnlyList<string> ids, CancellationToken token = default)
    {
        if (ids.Count == 0) return;
        await gate.WaitAsync(token);
        try
        {
            var profileHash = ProfileHash(profile);
            var key = await KeyAsync(profileHash);
            var rows = ids.Select(id => RowId(key, kind, id)).ToArray();
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM records WHERE id=$id AND profile=$profile";
                var id = command.Parameters.Add("$id", SqliteType.Text); command.Parameters.AddWithValue("$profile", profileHash);
                foreach (var row in rows) { id.Value = row; command.ExecuteNonQuery(); }
                transaction.Commit();
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    // Removes the profile's records and key; a later write starts a fresh archive.
    public async Task ClearAsync(string profile, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var profileHash = ProfileHash(profile);
            keys.Remove(profileHash);
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM records WHERE profile=$profile; DELETE FROM meta WHERE profile=$profile";
                command.Parameters.AddWithValue("$profile", profileHash); command.ExecuteNonQuery(); transaction.Commit();
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Rewrites the archive without its free pages (VACUUM), inside the archive's own gate, so indexing and search wait
    /// for it rather than meeting it. Every record is kept exactly as it was.
    /// </summary>
    public async Task<StoreCompaction> CompactAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => StoreCompaction.Run(path, Open, "VACUUM"), CancellationToken.None); }
        finally { gate.Release(); }
    }
}
