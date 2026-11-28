using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;

namespace MessageLink.Storage;

// What the server's backup reads out of each store and writes back into a new one: rows as they are kept, each store's own key still sealed
// by the protector that sealed it, so the backup unseals that key alone and the restoring server seals it again with its own protector (its master key is never
// replaced). Each store is read in one transaction on a connection of its own, so a store in use is read as one moment of it (history.db's write-ahead log gives
// the snapshot without holding a writer up). A restore writes a new file at a path of its choosing, whole, in one transaction: a stop half way leaves a file the
// restore sweeps, never one that is opened.

/// <summary>One conversation's own row of <c>history.db</c> as it is kept.</summary>
public sealed record HistoryRawChat(string Id, long Seq, long ResetSeq, byte[] Payload);
/// <summary>One message row of <c>history.db</c> as it is kept.</summary>
public sealed record HistoryRawMessage(string Id, string Chat, long Ord, long Seq, bool Removed, byte[]? Hash, byte[] Payload);
/// <summary>One list row of <c>history.db</c> as it is kept.</summary>
public sealed record HistoryRawRow(string Id, long Seq, bool Removed, byte[]? Hash, byte[] Payload);
/// <summary>One record of the search archive as it is kept, under its profile's digest.</summary>
public sealed record ArchiveRawRecord(string Id, string Profile, int Kind, byte[] Payload);

/// <summary>A new store being filled by a restore, in one transaction: <c>Complete</c> commits it, disposing without it leaves a file nothing opens.</summary>
public abstract class RawStoreImport : IDisposable
{
    private protected readonly SqliteConnection connection;
    private protected readonly SqliteTransaction transaction;
    private bool disposed;

    private protected RawStoreImport(SqliteConnection connection)
    {
        this.connection = connection;
        transaction = connection.BeginTransaction();
    }

    private protected void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private protected void Commit() => transaction.Commit();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        transaction.Dispose();
        connection.Dispose();
    }

    private protected static void DeleteFile(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(file)) File.Delete(file);
    }
}

public sealed partial class HistoryDatabase
{
    /// <summary>
    /// Reads every row of the file at <paramref name="path"/> as it is kept, in one read transaction: its key still sealed, its epoch and its newest change number,
    /// then each conversation, message and list row. Null when there is no file or it holds no key yet.
    /// </summary>
    public static (byte[] SealedKey, string Epoch, long Seq)? ExportRaw(string path, Action<HistoryRawChat> chat, Action<HistoryRawMessage> message, Action<HistoryRawRow> row,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        object? Meta(string name)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM meta WHERE name=$name";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() is var value && value is DBNull ? null : value;
        }
        if (Meta("key") is not byte[] key) return null;
        var epoch = Convert.ToString(Meta("epoch")) ?? "";
        var seq = Convert.ToInt64(Meta("seq") ?? 0L);
        void Each(string sql, Action<SqliteDataReader> read)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                read(reader);
            }
        }
        Each("SELECT id, seq, reset_seq, payload FROM chats", r => chat(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), (byte[])r[3])));
        Each("SELECT id, chat, ord, seq, removed, hash, payload FROM messages", r => message(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0,
            r.IsDBNull(5) ? null : (byte[])r[5], (byte[])r[6])));
        Each("SELECT id, seq, removed, hash, payload FROM rows", r => row(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : (byte[])r[3], (byte[])r[4])));
        return (key, epoch, seq);
    }

    /// <summary>
    /// A new file at <paramref name="path"/> (whatever was there goes, its log too) holding <paramref name="sealedKey"/>, the restoring server's seal of the backup's key,
    /// under a new epoch: every copy a client made of the old file starts over, since its revisions name the old epoch.
    /// </summary>
    public static Import ImportRaw(string path, byte[] sealedKey) => new(path, sealedKey);

    public sealed class Import : RawStoreImport
    {
        internal Import(string path, byte[] sealedKey) : base(Open(path))
        {
            Run("INSERT INTO meta(name, value) VALUES('key', $key)", ("$key", sealedKey));
            Run("INSERT INTO meta(name, value) VALUES('epoch', $epoch)", ("$epoch", Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()));
        }

        private static SqliteConnection Open(string path)
        {
            DeleteFile(path);
            // The store's own schema, made by the store: then nothing here repeats it.
            var made = new HistoryDatabase(path, NoProtector.Instance);
            made.OpenFile();
            return made.connection!;
        }

        public void Chat(HistoryRawChat chat) => Run("INSERT INTO chats(id, seq, reset_seq, payload) VALUES($id, $seq, $reset, $payload)",
            ("$id", chat.Id), ("$seq", chat.Seq), ("$reset", chat.ResetSeq), ("$payload", chat.Payload));

        public void Message(HistoryRawMessage message) => Run("INSERT INTO messages(id, chat, ord, seq, removed, hash, payload) VALUES($id, $chat, $ord, $seq, $removed, $hash, $payload)",
            ("$id", message.Id), ("$chat", message.Chat), ("$ord", message.Ord), ("$seq", message.Seq), ("$removed", message.Removed ? 1 : 0), ("$hash", message.Hash), ("$payload", message.Payload));

        public void Row(HistoryRawRow row) => Run("INSERT INTO rows(id, seq, removed, hash, payload) VALUES($id, $seq, $removed, $hash, $payload)",
            ("$id", row.Id), ("$seq", row.Seq), ("$removed", row.Removed ? 1 : 0), ("$hash", row.Hash), ("$payload", row.Payload));

        /// <summary>Commits the file with <paramref name="seq"/> as its newest change number, and folds its log into it so the file stands alone.</summary>
        public void Complete(long seq)
        {
            Run("INSERT INTO meta(name, value) VALUES('seq', $seq)", ("$seq", seq));
            Commit();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
    }
}

public sealed partial class MessageArchive
{
    /// <summary>Every profile's key (still sealed) and every record as it is kept, in one read transaction. Nothing when there is no file.</summary>
    public static void ExportRaw(string path, Action<string, byte[]> profileKey, Action<ArchiveRawRecord> record, string? snapshotFolder = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return;
        using var snapshot = StoreSnapshot.Of(path, snapshotFolder);
        using var connection = snapshot.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT profile, key FROM meta";
            using var reader = command.ExecuteReader();
            while (reader.Read()) profileKey(reader.GetString(0), (byte[])reader[1]);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, profile, kind, payload FROM records";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                record(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), (byte[])reader[3]));
            }
        }
    }

    /// <summary>A new archive at <paramref name="path"/>, whatever was there gone.</summary>
    public static Import ImportRaw(string path) => new(path);

    public sealed class Import : RawStoreImport
    {
        internal Import(string path) : base(Open(path)) { }

        private static SqliteConnection Open(string path)
        {
            DeleteFile(path);
            return new MessageArchive(path, NoProtector.Instance).Open();
        }

        public void Profile(string profile, byte[] sealedKey) => Run("INSERT INTO meta(profile, key) VALUES($profile, $key)", ("$profile", profile), ("$key", sealedKey));

        public void Record(ArchiveRawRecord record) => Run("INSERT INTO records(id, profile, kind, payload) VALUES($id, $profile, $kind, $payload)",
            ("$id", record.Id), ("$profile", record.Profile), ("$kind", record.Kind), ("$payload", record.Payload));

        public void Complete() => Commit();
    }
}

public sealed partial class SavedPhotoStore
{
    /// <summary>Every photo (its bytes still sealed) and every reference (its owner still sealed), in one read transaction. Nothing when there is no file.</summary>
    public static void ExportRaw(string path, Action<string, byte[], long> photo, Action<string, string, byte[]> reference, string? snapshotFolder = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return;
        using var snapshot = StoreSnapshot.Of(path, snapshotFolder);
        using var connection = snapshot.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT key, payload, kept FROM photos";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                photo(reader.GetString(0), (byte[])reader[1], reader.GetInt64(2));
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ref, photo, owner FROM refs";
            using var reader = command.ExecuteReader();
            while (reader.Read()) reference(reader.GetString(0), reader.GetString(1), (byte[])reader[2]);
        }
    }

    /// <summary>A new store of saved photos at <paramref name="path"/>, whatever was there gone.</summary>
    public static Import ImportRaw(string path) => new(path);

    public sealed class Import : RawStoreImport
    {
        internal Import(string path) : base(Open(path)) { }

        private static SqliteConnection Open(string path)
        {
            DeleteFile(path);
            return new SavedPhotoStore(path, NoProtector.Instance).Open();
        }

        /// <summary><paramref name="bytes"/> is the photo's own size (what the store's capacity counts), not its sealed size.</summary>
        public void Photo(string key, byte[] sealedBytes, long bytes, long kept) => Run("INSERT INTO photos(key, payload, bytes, kept) VALUES($key, $payload, $bytes, $kept)",
            ("$key", key), ("$payload", sealedBytes), ("$bytes", bytes), ("$kept", kept));

        public void Reference(string reference, string photo, byte[] sealedOwner) => Run("INSERT OR IGNORE INTO refs(ref, photo, owner) VALUES($ref, $photo, $owner)",
            ("$ref", reference), ("$photo", photo), ("$owner", sealedOwner));

        public void Complete() => Commit();
    }
}

/// <summary>
/// A store read for a backup. A store with a rollback journal holds every writer for as long as a reader's transaction lasts, and a backup compresses and encrypts as it
/// reads, so the store is first copied whole (<c>VACUUM INTO</c>, one short read) into <c>snapshotFolder</c>, and the backup reads the copy, which goes when this is
/// disposed. The copy holds the store's bytes as they are (still sealed). With no folder the store itself is read, in one transaction.
/// </summary>
internal sealed class StoreSnapshot : IDisposable
{
    private readonly string path;
    private readonly string? copy;

    private StoreSnapshot(string path, string? copy) { this.path = path; this.copy = copy; }

    public static StoreSnapshot Of(string path, string? snapshotFolder)
    {
        if (snapshotFolder is null) return new(path, null);
        Directory.CreateDirectory(snapshotFolder);
        var copy = Path.Combine(snapshotFolder, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".snapshot");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $copy";
            command.Parameters.AddWithValue("$copy", copy);
            command.ExecuteNonQuery();
        }
        catch { Delete(copy); throw; }
        return new(path, copy);
    }

    /// <summary>A read-only connection to the copy (or the store), inside one read transaction the caller begins.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy ?? path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() { if (copy is not null) Delete(copy); }

    private static void Delete(string file)
    {
        foreach (var leftover in new[] { file, file + "-journal", file + "-wal", file + "-shm" })
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// The stores above are made with a protector they never use (the restore seals what goes in itself); anything that did use one would fail loudly.
internal sealed class NoProtector : IStateProtector
{
    public static readonly NoProtector Instance = new();
    public Task<byte[]> ProtectAsync(byte[] plain) => throw new InvalidOperationException("A store made for a restore seals nothing itself.");
    public Task<byte[]> UnprotectAsync(byte[] protectedBytes) => throw new InvalidOperationException("A store made for a restore opens nothing itself.");
}
