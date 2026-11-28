using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace MessageLink.Storage;

/// <summary>
/// The first 100 bytes of a SQLite database, read without opening it as one: its page size, how many pages it has and
/// how many of those are free. Free pages times the page size is what compacting would give back.
/// </summary>
/// <remarks>
/// Opened for reading while sharing everything, so a database another process holds open — Messenger's helper holds
/// its own — is read beside it and never locked, queried or changed. A file that is not SQLite, or that is locked
/// outright, is simply not read.
/// </remarks>
public static class SqliteFileHeader
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    public static bool TryRead(string path, out long pageSize, out long pages, out long freePages)
    {
        pageSize = pages = freePages = 0;
        try
        {
            Span<byte> header = stackalloc byte[100];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
                if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
            if (!header[..16].SequenceEqual(Magic)) return false;
            // Big-endian throughout; a stored page size of 1 means 65536, the one size too large for two bytes.
            var size = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
            pageSize = size == 1 ? 65536 : size;
            pages = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
            freePages = BinaryPrimitives.ReadUInt32BigEndian(header[36..]);
            if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0 || freePages > pages) { pageSize = pages = freePages = 0; return false; }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Free pages times the page size, or 0 when the header cannot be read.</summary>
    public static long Reclaimable(string path) => TryRead(path, out var pageSize, out _, out var free) ? pageSize * free : 0;
}

/// <summary>What compacting one database did: ok, space (the disk filled while it ran; the database is unchanged) or
/// failed, with its size before and after in bytes.</summary>
public sealed record StoreCompaction(string Outcome, long Before, long After)
{
    public static StoreCompaction Nothing { get; } = new("ok", 0, 0);

    // SQLITE_FULL: VACUUM writes a whole new copy before it replaces the old one, and ran out of room for it.
    private const int SqliteFull = 13;

    /// <summary>Runs <paramref name="statement"/> on a database that already exists, for a store that holds its own
    /// gate around this call, and measures the file either side of it.</summary>
    public static StoreCompaction Run(string path, Func<SqliteConnection> open, string statement)
    {
        if (!File.Exists(path)) return Nothing;
        var before = Length(path);
        try
        {
            using var connection = open();
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteFull) { return new("space", before, Length(path)); }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        { return new("failed", before, Length(path)); }
        return new("ok", before, Length(path));
    }

    internal static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>
/// A store Settings > Troubleshooting > Storage can compact inside its own gate: the protected state, whichever file holds it (<c>state.db</c> in process,
/// the replica of a Messages Server's shared state in the client modes).
/// </summary>
public interface ICompactableStore
{
    Task<StoreCompaction> CompactAsync(CancellationToken cancellationToken = default);
}
