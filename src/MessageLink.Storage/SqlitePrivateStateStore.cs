using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;
namespace MessageLink.Storage;

public sealed class SqlitePrivateStateStore(string path, IStateProtector protector) : IPrivateStateStore, ICompactableStore
{
    private readonly SemaphoreSlim gate = new(1);
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
            if (version > 1) throw new InvalidDataException("Local database requires a newer application.");
            if (version == 0)
            {
                // Two processes open this file (the app and the server, in the local mode), and the first launch of both can find it empty together. The
                // creator takes the write lock first (BEGIN IMMEDIATE: it waits for the other, as the busy timeout does for any write, rather than
                // reading the version and then failing to upgrade), looks at the version again, and creates only what is not there, so whichever comes
                // second finds the first one's table and does nothing. (A read-then-write deferred transaction is what would fail here.)
                using var transaction = connection.BeginTransaction(deferred: false);
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS private_state (key TEXT PRIMARY KEY, payload BLOB NOT NULL, updated INTEGER NOT NULL); PRAGMA user_version=1;";
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                command.Transaction = null;
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var encrypted = await Task.Run(() =>
            {
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload FROM private_state WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
                return command.ExecuteScalar() as byte[];
            }, cancellationToken);
            return encrypted is null ? null : await protector.UnprotectAsync(encrypted);
        }
        finally { gate.Release(); }
    }
    // Every write since launch and how long each held the store's one gate, for the responsiveness log: a draft restored
    // on a conversation switch waits behind whatever write holds it. Counts and milliseconds only.
    private static long writes, writeHeldTicks;
    public static (long Writes, long HeldMs) WriteTotals =>
        (Interlocked.Read(ref writes), Interlocked.Read(ref writeHeldTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency);
    /// <summary>
    /// A write of the protected state made by the store that holds it in place of this one (the client modes' replica, ReplicatedStateStore), counted in
    /// <see cref="WriteTotals"/> with the time it held its own gate, so the responsiveness log reads the same in every mode.
    /// </summary>
    public static void CountWrite(long heldTicks)
    {
        Interlocked.Increment(ref writes);
        Interlocked.Add(ref writeHeldTicks, heldTicks);
    }
    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) => WriteAsync(key, value, keepPlace: false, cancellationToken);
    // The row keeps its `updated`, which is what the trim below orders by; a row not there yet is written as new.
    public Task WriteInPlaceAsync(string key, byte[] value, CancellationToken cancellationToken = default) => WriteAsync(key, value, keepPlace: true, cancellationToken);
    private async Task WriteAsync(string key, byte[] value, bool keepPlace, CancellationToken cancellationToken)
    {
        if (value.Length > 16 * 1024 * 1024) throw new InvalidDataException("Local snapshot exceeds the storage limit.");
        await gate.WaitAsync(cancellationToken);
        var held = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var encrypted = await protector.ProtectAsync(value);
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = keepPlace
                    ? "INSERT INTO private_state VALUES($key,$payload,$updated) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload"
                    : "INSERT INTO private_state VALUES($key,$payload,$updated) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload,updated=excluded.updated";
                command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$payload", encrypted); command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
                // Cache snapshots are expendable; drafts are retained independently. Counted first, because the trim has
                // to read `updated` for every cache row, and `updated` is stored after each row's payload: reaching it
                // walks the snapshot's whole overflow chain, with no page cache on a fresh connection. That was every
                // byte of up to 64 history snapshots re-read on every draft save. The count reads only the keys. With 64
                // or fewer rows the trim removes nothing, so skipping it then changes nothing.
                command.Parameters.Clear();
                command.CommandText = "SELECT COUNT(*) FROM private_state WHERE key LIKE 'cache:%'";
                if (Convert.ToInt64(command.ExecuteScalar()) > CacheLimit)
                {
                    command.CommandText = $"DELETE FROM private_state WHERE key LIKE 'cache:%' AND key NOT IN (SELECT key FROM private_state WHERE key LIKE 'cache:%' ORDER BY updated DESC LIMIT {CacheLimit})";
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }, cancellationToken);
        }
        finally
        {
            Interlocked.Increment(ref writes);
            Interlocked.Add(ref writeHeldTicks, System.Diagnostics.Stopwatch.GetTimestamp() - held);
            gate.Release();
        }
    }
    private const int CacheLimit = 64;
    // One statement, so a value the app writes while a restore runs is never replaced by the backup's.
    public async Task<bool> AddAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        if (value.Length > 16 * 1024 * 1024) throw new InvalidDataException("Local snapshot exceeds the storage limit.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var encrypted = await protector.ProtectAsync(value);
            return await Task.Run(() =>
            {
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO private_state VALUES($key,$payload,$updated) ON CONFLICT(key) DO NOTHING";
                command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$payload", encrypted);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return command.ExecuteNonQuery() == 1;
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM private_state WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }
    // Every key under a category. A draft row names its conversation only as a keyed hash, so a sweep for drafts the
    // app no longer has any conversation for has to start from the rows themselves.
    public async Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<IReadOnlyList<string>>(() =>
            {
                using var connection = Open(); using var command = connection.CreateCommand();
                // Matched by length rather than LIKE: a prefix is an exact start, with no pattern characters to escape.
                command.CommandText = "SELECT key FROM private_state WHERE substr(key, 1, $length) = $prefix ORDER BY key";
                command.Parameters.AddWithValue("$length", prefix.Length); command.Parameters.AddWithValue("$prefix", prefix);
                using var reader = command.ExecuteReader();
                var keys = new List<string>();
                while (reader.Read()) keys.Add(reader.GetString(0));
                return keys;
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// Every row whose key starts with one of <paramref name="prefixes"/>, as it is kept (its payload still sealed), read in one transaction from a connection of
    /// its own: the server's backup (stage 10), which unseals each with the protector that sealed it. None when the file is not there. Nothing is written.
    /// </summary>
    public static IReadOnlyList<(string Key, byte[] Sealed)> ReadSealed(string path, IReadOnlyList<string> prefixes)
    {
        if (!File.Exists(path)) return [];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key, payload FROM private_state ORDER BY key";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, byte[])>();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))) rows.Add((key, (byte[])reader[1]));
        }
        return rows;
    }
    /// <summary>
    /// Rewrites the database without its free pages (VACUUM), inside the store's own gate: a draft saved meanwhile waits
    /// for it, then lands in the compacted file. Every row is kept exactly as it was.
    /// </summary>
    public async Task<StoreCompaction> CompactAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => StoreCompaction.Run(path, Open, "VACUUM"), CancellationToken.None); }
        finally { gate.Release(); }
    }
}
