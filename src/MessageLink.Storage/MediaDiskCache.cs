using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;

namespace MessageLink.Storage;

// Separate, expendable database: media eviction cannot remove drafts or account state.
public sealed class MediaDiskCache(string path, IStateProtector protector, long capacity = 512L * 1024 * 1024)
{
    private readonly SemaphoreSlim gate = new(1);
    // As large as anything a provider here lets be received (Google Messages, 100 MB): a bigger item was not kept, so each view of it downloaded it again.
    private const int ItemLimit = 100 * 1024 * 1024;
    private static string Key(string profile, string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "\n" + id)));
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
            if (version > 2) throw new InvalidDataException("Media cache requires a newer app.");
            if (version == 0)
            {
                command.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; CREATE TABLE IF NOT EXISTS media(key TEXT PRIMARY KEY, payload BLOB NOT NULL, expires INTEGER NOT NULL, accessed INTEGER NOT NULL); PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
            if (version < 2)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE INDEX IF NOT EXISTS media_expires ON media(expires);
                    CREATE INDEX IF NOT EXISTS media_accessed ON media(accessed);
                    CREATE TABLE IF NOT EXISTS media_usage(id INTEGER PRIMARY KEY CHECK(id=1), bytes INTEGER NOT NULL);
                    INSERT OR REPLACE INTO media_usage SELECT 1, COALESCE(SUM(length(payload)),0) FROM media;
                    CREATE TRIGGER IF NOT EXISTS media_insert AFTER INSERT ON media BEGIN
                        UPDATE media_usage SET bytes=bytes+length(new.payload) WHERE id=1;
                    END;
                    CREATE TRIGGER IF NOT EXISTS media_update AFTER UPDATE OF payload ON media BEGIN
                        UPDATE media_usage SET bytes=bytes+length(new.payload)-length(old.payload) WHERE id=1;
                    END;
                    CREATE TRIGGER IF NOT EXISTS media_delete AFTER DELETE ON media BEGIN
                        UPDATE media_usage SET bytes=bytes-length(old.payload) WHERE id=1;
                    END;
                    PRAGMA user_version=2;
                    """;
                command.ExecuteNonQuery(); transaction.Commit(); command.Transaction = null;
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    /// <summary>
    /// The entry's bytes, or null when it is not held. <paramref name="renewFor"/>, for a caller whose entries all live for
    /// that long, keeps a picture someone goes on looking at: each read pushes its expiry out to that long from now, at
    /// most once a day per entry (RenewSlack) and joined to the batched marks below, so a picture seen every week is not
    /// downloaded again because it was first saved thirty days ago. Never for short-lived markers, which a read must not
    /// keep alive.
    /// </summary>
    public async Task<byte[]?> ReadAsync(string profile, string id, CancellationToken token = default, TimeSpan? renewFor = null)
    {
        try
        {
            byte[]? encrypted;
            await gate.WaitAsync(token);
            try
            {
                encrypted = await Task.Run(() =>
                {
                    using var connection = Open(); using var command = connection.CreateCommand();
                    var key = Key(profile, id);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    command.CommandText = "SELECT payload, accessed, expires FROM media WHERE key=$key AND expires>$now";
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$now", now);
                    using var row = command.ExecuteReader();
                    if (!row.Read()) return null;
                    var bytes = (byte[])row[0];
                    var renewal = renewFor is { } span && span > RenewSlack && row.GetInt64(2) < now + (long)(span - RenewSlack).TotalMilliseconds
                        ? now + (long)span.TotalMilliseconds : (long?)null;
                    // Approximate LRU to the minute: repeated paints of one tile do not each write the database.
                    if (renewal is not null) Touch(key, renewal);
                    else if (row.GetInt64(1) < now - 60000) Touch(key);
                    return bytes;
                }, token);
            }
            finally { gate.Release(); }
            if (encrypted is null) return null;
            // The blob has been copied out of SQLite. Protection work no longer serializes independent media reads.
            var result = await protector.UnprotectAsync(encrypted);
            token.ThrowIfCancellationRequested();
            return result.Length <= ItemLimit ? result : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    // How far a renewing read lets an entry's expiry fall behind before it is pushed out again: once a day at most.
    private static readonly TimeSpan RenewSlack = TimeSpan.FromDays(1);

    /// <summary>Whether an entry is held, without reading it: asked before preparing something large ahead (a GIF's clip),
    /// so what is already here is neither decrypted nor fetched again.</summary>
    public async Task<bool> HoldsAsync(string profile, string id, CancellationToken token = default)
    {
        try
        {
            await gate.WaitAsync(token);
            try
            {
                return await Task.Run(() =>
                {
                    if (!File.Exists(path)) return false;
                    using var connection = Open(); using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1 FROM media WHERE key=$key AND expires>$now";
                    command.Parameters.AddWithValue("$key", Key(profile, id));
                    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return command.ExecuteScalar() is not null;
                }, token);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // Reading an entry marks it recently used, for eviction. That was a standalone UPDATE per read, and SQLite's journal
    // makes each about 6.5 ms against 0.6 ms for the read itself; with every read serialized behind the one gate, a
    // launch burst of 31 avatars took 204 ms instead of 16 (measured on a 370 MB synthetic cache). The marks are gathered
    // and written together a moment later in one transaction. A mark lost to the app closing only ages an entry.
    // The offline checks shorten it, to wait for those marks without sitting through two seconds each time.
    public TimeSpan TouchDelay { get; init; } = TimeSpan.FromSeconds(2);
    // Key to the new expiry a renewal asked for, or null for a plain read's mark.
    private readonly Dictionary<string, long?> touched = [];
    private bool touchQueued;
    private void Touch(string key, long? expires = null)
    {
        lock (touched)
        {
            // A read's mark never cancels a renewal already waiting for the same entry.
            if (expires is not null) touched[key] = expires; else touched.TryAdd(key, null);
            if (touchQueued) return;
            touchQueued = true;
        }
        _ = FlushTouchesAsync();
    }
    // Extends an entry that already holds these exact bytes, as rewriting them would, without rewriting them: a rewrite
    // is a protect, a purge, an upsert and a journaled commit of its own, and every avatar the app kept came back
    // unchanged after each launch's check. It joins the batched marks above. An update, not an insert: an entry evicted
    // or expired in the meantime stays gone, and the next launch simply fetches that picture again.
    public void Renew(string profile, string id, TimeSpan lifetime) =>
        Touch(Key(profile, id), DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeMilliseconds());
    private async Task FlushTouchesAsync()
    {
        await Task.Delay(TouchDelay);
        KeyValuePair<string, long?>[] keys;
        lock (touched) { keys = [.. touched]; touched.Clear(); touchQueued = false; }
        await gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE media SET accessed=$now, expires=COALESCE($expires, expires) WHERE key=$key";
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var expires = command.Parameters.Add("$expires", SqliteType.Integer);
                foreach (var (value, renewal) in keys) { key.Value = value; expires.Value = renewal is { } until ? until : DBNull.Value; command.ExecuteNonQuery(); }
                transaction.Commit();
            });
        }
        catch { /* Only eviction order and a renewal's expiry depend on this. */ }
        finally { gate.Release(); }
    }

    // Removes these entries in one transaction and returns how many were there. Keys are digests, so the ids are hashed
    // and compared with the keys already in the table: a long list of ids that were never cached costs hashing, not a
    // statement each.
    public async Task<int> DeleteAsync(string profile, IEnumerable<string> ids, CancellationToken token = default)
    {
        var wanted = await Task.Run(() => ids.Select(id => Key(profile, id)).ToHashSet(StringComparer.Ordinal), token);
        if (wanted.Count == 0) return 0;
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var present = new List<string>();
                command.CommandText = "SELECT key FROM media";
                using (var rows = command.ExecuteReader())
                    while (rows.Read()) if (wanted.Contains(rows.GetString(0))) present.Add(rows.GetString(0));
                command.CommandText = "DELETE FROM media WHERE key=$key";
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var removed = 0;
                foreach (var value in present) { key.Value = value; removed += command.ExecuteNonQuery(); }
                transaction.Commit();
                if (removed > 0)
                {
                    command.Transaction = null; command.Parameters.Clear();
                    command.CommandText = "PRAGMA incremental_vacuum(256)"; command.ExecuteNonQuery();
                }
                return removed;
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public async Task WriteAsync(string profile, string id, byte[] bytes, TimeSpan lifetime, CancellationToken token = default)
    {
        if (bytes.Length == 0 || bytes.Length > ItemLimit || bytes.Length > capacity) return;
        var encrypted = await protector.ProtectAsync(bytes);
        if (encrypted.LongLength > capacity) return;
        await gate.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                var now = DateTimeOffset.UtcNow;
                command.CommandText = "DELETE FROM media WHERE expires<=$now";
                command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds()); var removed = command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO media VALUES($key,$data,$expires,$now) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload,expires=excluded.expires,accessed=excluded.accessed";
                command.Parameters.AddWithValue("$key", Key(profile, id)); command.Parameters.AddWithValue("$data", encrypted);
                command.Parameters.AddWithValue("$expires", now.Add(lifetime).ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "SELECT bytes FROM media_usage WHERE id=1";
                var excess = Convert.ToInt64(command.ExecuteScalar()) - capacity;
                if (excess > 0)
                {
                    // Read only as many oldest entries as need to go, using the access-order index.
                    var evict = new List<string>();
                    command.CommandText = "SELECT key,length(payload) FROM media ORDER BY accessed,rowid";
                    using (var rows = command.ExecuteReader())
                        while (excess > 0 && rows.Read()) { evict.Add(rows.GetString(0)); excess -= rows.GetInt64(1); }
                    command.CommandText = "DELETE FROM media WHERE key=$key";
                    var key = command.Parameters.Add("$key", SqliteType.Text);
                    foreach (var id in evict) { key.Value = id; removed += command.ExecuteNonQuery(); }
                }
                transaction.Commit();
                if (removed > 0)
                {
                    command.Transaction = null; command.Parameters.Clear();
                    command.CommandText = "PRAGMA incremental_vacuum(256)"; command.ExecuteNonQuery();
                }
            }, token);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Empties the cache: every picture, video and avatar kept for quick display goes, and is fetched again when it is
    /// next shown. Marks still waiting to be written are dropped with them. Returns how many entries went and how many
    /// bytes they held. The space is given back to the disk at once, as eviction gives it back.
    /// </summary>
    public async Task<(int Entries, long Bytes)> ClearAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            lock (touched) touched.Clear();
            if (!File.Exists(path)) return (0, 0);
            return await Task.Run(() =>
            {
                using var connection = Open(); using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE((SELECT bytes FROM media_usage WHERE id=1),0)";
                var bytes = Convert.ToInt64(command.ExecuteScalar());
                // The delete trigger takes each entry's bytes off the running total as it goes; the total is then set to
                // exactly nothing, so a count that had drifted cannot outlive the entries it counted.
                command.CommandText = "DELETE FROM media";
                var entries = command.ExecuteNonQuery();
                command.CommandText = "UPDATE media_usage SET bytes=0 WHERE id=1";
                command.ExecuteNonQuery();
                transaction.Commit();
                command.Transaction = null;
                command.CommandText = "PRAGMA incremental_vacuum";
                command.ExecuteNonQuery();
                return (entries, bytes);
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    /// <summary>Gives the cache's free pages back to the disk (incremental vacuum), inside its gate. A cache made before
    /// it could vacuum incrementally is rewritten once so that it can.</summary>
    public async Task<StoreCompaction> CompactAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(path)) return StoreCompaction.Nothing;
                long mode;
                try
                {
                    using var connection = Open(); using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA auto_vacuum";
                    mode = Convert.ToInt64(command.ExecuteScalar());
                }
                catch (Exception e) when (e is SqliteException or InvalidDataException) { return new StoreCompaction("failed", StoreCompaction.Length(path), StoreCompaction.Length(path)); }
                // 2 is incremental. Anything else keeps its free pages until a VACUUM that changes the mode.
                return StoreCompaction.Run(path, Open, mode == 2 ? "PRAGMA incremental_vacuum" : "PRAGMA auto_vacuum=INCREMENTAL; VACUUM");
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }
}
