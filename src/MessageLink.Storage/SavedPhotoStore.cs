using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;

namespace MessageLink.Storage;

// The full-size photos of saved messages, in a database of their own (saved-photos.db) beside the media cache rather than
// inside it: the cache evicts by use and Clear pictures empties it, and neither may take a photo somebody chose to keep.
// Bytes are protected as the cache protects them (IStateProtector); keys are digests the caller makes; the owner, the
// conversation a reference came from, is protected too, and is read back only when an account signs out. 2026-10-04.
public sealed partial class SavedPhotoStore(string path, IStateProtector protector, long capacity = SavedPhotoStore.DefaultCapacity) : ISavedPhotoStore
{
    public const long DefaultCapacity = 1024L * 1024 * 1024;
    // As the media cache: a photo larger than this is not something a conversation draws either.
    private const int ItemLimit = 50 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    // Every photo key held, read once, so asking whether a photo is kept costs no query: the media reader asks for every
    // picture it draws. Null until first read; changed only under the gate, after the database has.
    private HashSet<string>? held;

    public long Capacity => capacity;

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
            if (version > 1) throw new InvalidDataException("Saved photos require a newer app.");
            if (version == 0)
            {
                command.CommandText = """
                    PRAGMA auto_vacuum=INCREMENTAL;
                    CREATE TABLE IF NOT EXISTS photos(key TEXT PRIMARY KEY, payload BLOB NOT NULL, bytes INTEGER NOT NULL, kept INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS photos_kept ON photos(kept);
                    CREATE TABLE IF NOT EXISTS refs(ref TEXT NOT NULL, photo TEXT NOT NULL, owner BLOB NOT NULL, PRIMARY KEY(ref, photo));
                    CREATE INDEX IF NOT EXISTS refs_photo ON refs(photo);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    // Inside the gate. A store never written has no file, and reading it does not make one.
    private HashSet<string> Held()
    {
        if (held is not null) return held;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM photos";
            using var rows = command.ExecuteReader();
            while (rows.Read()) keys.Add(rows.GetString(0));
        }
        return held = keys;
    }

    public async Task<bool> HoldsAsync(string photo, CancellationToken token = default)
    {
        if (held is { } known) lock (known) return known.Contains(photo);
        await gate.WaitAsync(token);
        try
        {
            var keys = await Task.Run(Held, token);
            lock (keys) return keys.Contains(photo);
        }
        catch (Exception e) when (e is SqliteException or InvalidDataException or IOException) { return false; }
        finally { gate.Release(); }
    }

    public async Task<byte[]?> ReadAsync(string photo, CancellationToken token = default)
    {
        if (!await HoldsAsync(photo, token)) return null;
        try
        {
            byte[]? encrypted;
            await gate.WaitAsync(token);
            try
            {
                encrypted = await Task.Run(() =>
                {
                    using var connection = Open(); using var command = connection.CreateCommand();
                    command.CommandText = "SELECT payload FROM photos WHERE key=$key";
                    command.Parameters.AddWithValue("$key", photo);
                    return command.ExecuteScalar() as byte[];
                }, token);
            }
            finally { gate.Release(); }
            if (encrypted is null) return null;
            var bytes = await protector.UnprotectAsync(encrypted);
            token.ThrowIfCancellationRequested();
            return bytes.Length is > 0 and <= ItemLimit ? bytes : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<bool> ReferAsync(string reference, string photo, string owner, CancellationToken token = default)
    {
        var sealedOwner = await SealAsync(owner);
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                var keys = Held();
                lock (keys) if (!keys.Contains(photo)) return false;
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE photos SET kept=$now WHERE key=$photo";
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$photo", photo);
                if (command.ExecuteNonQuery() == 0) return false;
                command.CommandText = "INSERT OR REPLACE INTO refs VALUES($ref,$photo,$owner)";
                command.Parameters.AddWithValue("$ref", reference);
                command.Parameters.AddWithValue("$owner", sealedOwner);
                command.ExecuteNonQuery();
                transaction.Commit();
                return true;
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<SavedPhotoKeep> KeepAsync(string reference, string photo, string owner, byte[] bytes, CancellationToken token = default)
    {
        if (bytes.Length == 0 || bytes.Length > ItemLimit) return new(false, SavedPhotoChange.None);
        var encrypted = await protector.ProtectAsync(bytes);
        if (encrypted.LongLength > capacity) return new(false, SavedPhotoChange.None);
        var sealedOwner = await SealAsync(owner);
        token.ThrowIfCancellationRequested();
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                var keys = Held();
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO photos VALUES($key,$data,$bytes,$now) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload,bytes=excluded.bytes,kept=excluded.kept";
                command.Parameters.AddWithValue("$key", photo);
                command.Parameters.AddWithValue("$data", encrypted);
                command.Parameters.AddWithValue("$bytes", encrypted.LongLength);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "INSERT OR REPLACE INTO refs VALUES($ref,$photo,$owner)";
                command.Parameters.AddWithValue("$ref", reference);
                command.Parameters.AddWithValue("$photo", photo);
                command.Parameters.AddWithValue("$owner", sealedOwner);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "SELECT COALESCE(SUM(bytes),0) FROM photos";
                var excess = Convert.ToInt64(command.ExecuteScalar()) - capacity;
                var evict = new List<(string Key, long Bytes)>();
                if (excess > 0)
                {
                    // Saved longest ago first, by the photos_kept index, and only as many as must go.
                    command.CommandText = "SELECT key,bytes FROM photos WHERE key<>$photo ORDER BY kept,rowid";
                    command.Parameters.AddWithValue("$photo", photo);
                    using (var rows = command.ExecuteReader())
                        while (excess > 0 && rows.Read()) { evict.Add((rows.GetString(0), rows.GetInt64(1))); excess -= rows.GetInt64(1); }
                    command.Parameters.Clear();
                }
                var references = Remove(command, evict.Select(entry => entry.Key));
                transaction.Commit();
                if (evict.Count > 0) Vacuum(command);
                lock (keys) { keys.Add(photo); foreach (var (key, _) in evict) keys.Remove(key); }
                return new SavedPhotoKeep(true, new(references, evict.Count, evict.Sum(entry => entry.Bytes)));
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<SavedPhotoChange> ReleaseAsync(string reference, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(path)) return SavedPhotoChange.None;
                var keys = Held();
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM refs WHERE ref=$ref";
                command.Parameters.AddWithValue("$ref", reference);
                var references = command.ExecuteNonQuery();
                command.Parameters.Clear();
                var (photos, bytes) = references == 0 ? (new List<string>(), 0L) : Orphans(command);
                transaction.Commit();
                Forget(keys, command, photos);
                return new SavedPhotoChange(references, photos.Count, bytes);
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<SavedPhotoChange> ForgetAsync(Func<string, bool> owner, CancellationToken token = default)
    {
        // The owners are read and opened outside the gate's long hold: there are as many as references, a few hundred.
        List<(string Ref, string Photo, byte[] Owner)> all;
        await gate.WaitAsync(token);
        try
        {
            all = await Task.Run(() =>
            {
                var found = new List<(string, string, byte[])>();
                if (!File.Exists(path)) return found;
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT ref,photo,owner FROM refs";
                using var rows = command.ExecuteReader();
                while (rows.Read()) found.Add((rows.GetString(0), rows.GetString(1), (byte[])rows[2]));
                return found;
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
        var matching = new List<(string Ref, string Photo)>();
        foreach (var (reference, photo, sealedOwner) in all)
        {
            string? opened;
            try { opened = Encoding.UTF8.GetString(await protector.UnprotectAsync(sealedOwner)); }
            catch (Exception) { opened = null; } // One that cannot be opened cannot be told apart, and stays.
            if (opened is not null && owner(opened)) matching.Add((reference, photo));
        }
        if (matching.Count == 0) return SavedPhotoChange.None;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            return await Task.Run(() =>
            {
                var keys = Held();
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM refs WHERE ref=$ref AND photo=$photo";
                var refParameter = command.Parameters.Add("$ref", SqliteType.Text);
                var photoParameter = command.Parameters.Add("$photo", SqliteType.Text);
                var references = 0;
                foreach (var (reference, photo) in matching)
                { refParameter.Value = reference; photoParameter.Value = photo; references += command.ExecuteNonQuery(); }
                command.Parameters.Clear();
                var (photos, bytes) = Orphans(command);
                transaction.Commit();
                Forget(keys, command, photos);
                return new SavedPhotoChange(references, photos.Count, bytes);
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<SavedPhotoChange> ClearAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(path)) { held = new(StringComparer.Ordinal); return SavedPhotoChange.None; }
                var keys = Held();
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE(SUM(bytes),0) FROM photos";
                var bytes = Convert.ToInt64(command.ExecuteScalar());
                command.CommandText = "DELETE FROM refs";
                var references = command.ExecuteNonQuery();
                command.CommandText = "DELETE FROM photos";
                var photos = command.ExecuteNonQuery();
                transaction.Commit();
                lock (keys) keys.Clear();
                command.Transaction = null;
                command.CommandText = "PRAGMA incremental_vacuum";
                command.ExecuteNonQuery();
                return new SavedPhotoChange(references, photos, bytes);
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task<(int Photos, long Bytes)> UsageAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(path)) return (0, 0L);
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes),0) FROM photos";
                using var row = command.ExecuteReader();
                row.Read();
                return (row.GetInt32(0), row.GetInt64(1));
            }, token);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> SealAsync(string owner)
    {
        var plain = Encoding.UTF8.GetBytes(owner);
        try { return await protector.ProtectAsync(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // The photos no reference names any more, with the bytes they hold, removed inside the caller's transaction.
    private static (List<string> Photos, long Bytes) Orphans(SqliteCommand command)
    {
        var orphans = new List<string>();
        long bytes = 0;
        command.CommandText = "SELECT key,bytes FROM photos WHERE key NOT IN (SELECT photo FROM refs)";
        using (var rows = command.ExecuteReader())
            while (rows.Read()) { orphans.Add(rows.GetString(0)); bytes += rows.GetInt64(1); }
        command.CommandText = "DELETE FROM photos WHERE key=$key";
        var key = command.Parameters.Add("$key", SqliteType.Text);
        foreach (var value in orphans) { key.Value = value; command.ExecuteNonQuery(); }
        command.Parameters.Clear();
        return (orphans, bytes);
    }

    // Photos taken out with every reference to them, inside the caller's transaction; how many references went with them.
    private static int Remove(SqliteCommand command, IEnumerable<string> photos)
    {
        var references = 0;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        foreach (var value in photos)
        {
            key.Value = value;
            command.CommandText = "DELETE FROM refs WHERE photo=$key"; references += command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM photos WHERE key=$key"; command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        return references;
    }

    private static void Forget(HashSet<string> keys, SqliteCommand command, List<string> photos)
    {
        if (photos.Count == 0) return;
        lock (keys) foreach (var photo in photos) keys.Remove(photo);
        Vacuum(command);
    }

    // The space a removal frees goes back to the disk at once, as the media cache gives back what it evicts.
    private static void Vacuum(SqliteCommand command)
    {
        command.Transaction = null; command.Parameters.Clear();
        command.CommandText = "PRAGMA incremental_vacuum";
        command.ExecuteNonQuery();
    }
}
