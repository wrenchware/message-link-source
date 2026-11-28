using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MessageLink.App.Services.Backup;

/// <summary>Why a backup file could not be opened. Wrong password and damage are one answer on purpose: AES-GCM
/// cannot tell them apart, and guessing would send someone hunting for a password that was right. Retired is a backup that
/// opened with its password and is in a format this build no longer reads (<see cref="RetiredBackupFormats"/>).</summary>
internal enum BackupFileFailure { NotABackup, Unsupported, Unreadable, Retired }

/// <summary>
/// Formats of a kind this build writes, under a name it no longer reads: the product's earlier one (#123, owner 2026-11-27), in backups made
/// before the rename. Told apart by shape alone, one lowercase word before the kind that is not today's and does not begin with it
/// (so no later name of ours, messagelink2 say, is ever called older), so that such a backup is said to be
/// in an older format, not damaged or protected by another password; it is never read. Any other unknown format stays unreadable.
/// </summary>
internal static class RetiredBackupFormats
{
    private const string Current = "messagelink";
    private const string BackupKind = ".backup", ServerBackupKind = ".server-backup", MessengerHistoryKind = ".messenger.history";

    /// <summary>A manifest's format: a PC backup's, a server backup's or a Messenger page's kind under an earlier name.</summary>
    public static bool Is(string? format) => Earlier(format, BackupKind) || Earlier(format, ServerBackupKind) || Earlier(format, MessengerHistoryKind);

    /// <summary>A Messenger history page's format under an earlier name.</summary>
    public static bool IsMessengerHistory(string? format) => Earlier(format, MessengerHistoryKind);

    private static bool Earlier(string? format, string kind) =>
        format is { } said && said.EndsWith(kind, StringComparison.Ordinal) && said[..^kind.Length] is { Length: > 0 } name && !name.StartsWith(Current, StringComparison.Ordinal)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');
}

internal sealed class BackupFileException(BackupFileFailure failure) : Exception(failure.ToString())
{
    public BackupFileFailure Failure { get; } = failure;
}

/// <summary>
/// The backup file: a short fixed header, then the payload sealed with AES-256-GCM in chunks under a key derived from
/// the user's password.
/// </summary>
/// <remarks>
/// Everything on this PC is sealed to this Windows user, so a copy of the data folder is unreadable anywhere else. A
/// backup is the one thing meant to be read elsewhere, so it carries the content itself, re-sealed under a password.
///
/// Layout, little-endian: magic (8) · format version (2) · KDF id (1) · iterations (4) · salt (16) · chunk size (4) ·
/// nonce prefix (8), then records. Every record but the last holds exactly one chunk of payload plus its 16-byte tag;
/// the last holds what is left (possibly nothing) plus its tag, so the file's length says where the last record is.
/// Each chunk's nonce is the prefix and its index; its associated data is the header's SHA-256, its index and whether
/// it is the last. Changing any byte, reordering records, dropping the end or adding to it all fail authentication.
/// The header names no account and counts nothing: what a backup holds is inside it, in the manifest.
/// </remarks>
internal static class BackupFile
{
    public const string Extension = ".msgbackup";
    public const ushort FormatVersion = 1;
    public const byte Pbkdf2Sha256 = 1;
    // OWASP's 2023 figure for PBKDF2-HMAC-SHA256. A reader accepts more, up to a bound that keeps a damaged header
    // from stalling the app, and never fewer.
    public const int Iterations = 600_000;
    private const int MaxIterations = 10_000_000;
    public const int ChunkSize = 1 << 20;
    private const int MinChunkSize = 4 << 10, MaxChunkSize = 4 << 20;
    public const int SaltSize = 16;
    private const int PrefixSize = 8, TagSize = 16, KeySize = 32;
    // "MSGBKUP" and a byte a text viewer stops at.
    private static readonly byte[] Magic = [0x4D, 0x53, 0x47, 0x42, 0x4B, 0x55, 0x50, 0x1A];
    public const int HeaderSize = 8 + 2 + 1 + 4 + SaltSize + 4 + PrefixSize;

    internal sealed record Header(ushort Version, byte Kdf, int Iterations, byte[] Salt, int ChunkSize, byte[] NoncePrefix, byte[] Hash);

    /// <summary>A stream the payload is written into. <see cref="BackupWriter.Complete"/> seals the last record;
    /// disposing without it leaves the file without an end, which no reader accepts.</summary>
    public static BackupWriter Create(Stream output, string password, int chunkSize = ChunkSize)
    {
        if (chunkSize is < MinChunkSize or > MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var bytes = new byte[HeaderSize];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], FormatVersion);
        span[10] = Pbkdf2Sha256;
        BinaryPrimitives.WriteInt32LittleEndian(span[11..], Iterations);
        salt.CopyTo(span[15..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[(15 + SaltSize)..], chunkSize);
        prefix.CopyTo(span[(19 + SaltSize)..]);
        var header = new Header(FormatVersion, Pbkdf2Sha256, Iterations, salt, chunkSize, prefix, SHA256.HashData(bytes));
        output.Write(bytes);
        return new BackupWriter(output, header, DeriveKey(password, header));
    }

    /// <summary>
    /// Opens a backup for reading, after checking every record. Wrong password and damage throw the same
    /// <see cref="BackupFileFailure.Unreadable"/>; so does a file that ends early or has something added.
    /// </summary>
    /// <remarks>The source must be seekable; the payload is decrypted a chunk at a time as it is read, never to disk.</remarks>
    public static BackupReader Open(Stream source, string password)
    {
        var header = ReadHeader(source);
        var reader = new BackupReader(source, header, DeriveKey(password, header));
        try { reader.Verify(); return reader; }
        catch { reader.Dispose(); throw; }
    }

    internal static Header ReadHeader(Stream source)
    {
        if (!source.CanSeek) throw new ArgumentException("A backup is read from a seekable stream.", nameof(source));
        var bytes = new byte[HeaderSize];
        source.Position = 0;
        if (source.ReadAtLeast(bytes, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !bytes.AsSpan(0, 8).SequenceEqual(Magic))
            throw new BackupFileException(BackupFileFailure.NotABackup);
        var span = bytes.AsSpan();
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var kdf = span[10];
        if (version != FormatVersion || kdf != Pbkdf2Sha256) throw new BackupFileException(BackupFileFailure.Unsupported);
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(span[11..]);
        var chunk = BinaryPrimitives.ReadInt32LittleEndian(span[(15 + SaltSize)..]);
        if (iterations is < Iterations or > MaxIterations || chunk is < MinChunkSize or > MaxChunkSize)
            throw new BackupFileException(BackupFileFailure.Unreadable);
        return new(version, kdf, iterations, span.Slice(15, SaltSize).ToArray(), chunk,
            span.Slice(19 + SaltSize, PrefixSize).ToArray(), SHA256.HashData(bytes));
    }

    private static byte[] DeriveKey(string password, Header header)
    {
        var secret = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(secret, header.Salt, header.Iterations, HashAlgorithmName.SHA256, KeySize); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static void Nonce(Header header, uint index, Span<byte> nonce)
    {
        header.NoncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], index);
    }

    internal static void AssociatedData(Header header, uint index, bool final, Span<byte> data)
    {
        header.Hash.CopyTo(data);
        BinaryPrimitives.WriteUInt64BigEndian(data[32..], index);
        data[40] = final ? (byte)1 : (byte)0;
    }

    internal const int NonceSize = PrefixSize + 4, AssociatedSize = 32 + 8 + 1, Tag = TagSize;
}

internal sealed class BackupWriter : Stream
{
    private readonly Stream output;
    private readonly BackupFile.Header header;
    private readonly AesGcm aes;
    private readonly byte[] buffer, sealedChunk;
    private int filled;
    private uint index;
    private bool completed, disposed;

    internal BackupWriter(Stream output, BackupFile.Header header, byte[] key)
    {
        this.output = output; this.header = header;
        try { aes = new AesGcm(key, BackupFile.Tag); }
        finally { CryptographicOperations.ZeroMemory(key); }
        buffer = new byte[header.ChunkSize];
        sealedChunk = new byte[header.ChunkSize + BackupFile.Tag];
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !completed && !disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("The backup is already complete.");
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, buffer.Length - filled);
            data[..take].CopyTo(buffer.AsSpan(filled));
            filled += take; data = data[take..];
            // A full chunk goes out at once, so the last record is always shorter than a chunk and the file's length
            // alone says where it is.
            if (filled == buffer.Length) Seal(final: false);
        }
    }

    /// <summary>Seals what is left as the last record. Until this runs the file has no end and cannot be opened.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return;
        Seal(final: true);
        completed = true;
        output.Flush();
    }

    private void Seal(bool final)
    {
        if (index == uint.MaxValue) throw new InvalidOperationException("The backup is too large.");
        Span<byte> nonce = stackalloc byte[BackupFile.NonceSize];
        Span<byte> associated = stackalloc byte[BackupFile.AssociatedSize];
        BackupFile.Nonce(header, index, nonce);
        BackupFile.AssociatedData(header, index, final, associated);
        var plain = buffer.AsSpan(0, filled);
        var cipher = sealedChunk.AsSpan(0, filled);
        var tag = sealedChunk.AsSpan(filled, BackupFile.Tag);
        aes.Encrypt(nonce, plain, cipher, tag, associated);
        output.Write(sealedChunk, 0, filled + BackupFile.Tag);
        CryptographicOperations.ZeroMemory(plain);
        filled = 0; index++;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            aes.Dispose();
            CryptographicOperations.ZeroMemory(buffer);
        }
        base.Dispose(disposing);
    }
}

/// <summary>The decrypted payload of a backup, readable and seekable, one chunk held in memory at a time.</summary>
internal sealed class BackupReader : Stream
{
    private readonly Stream source;
    private readonly BackupFile.Header header;
    private readonly AesGcm aes;
    private readonly long fullRecords;
    private readonly int lastRecord;
    private readonly byte[] record, plain;
    private long position;
    private long cached = -1;
    private int cachedLength;
    private bool disposed;

    internal BackupReader(Stream source, BackupFile.Header header, byte[] key)
    {
        this.source = source; this.header = header;
        try { aes = new AesGcm(key, BackupFile.Tag); }
        finally { CryptographicOperations.ZeroMemory(key); }
        var recordSize = (long)header.ChunkSize + BackupFile.Tag;
        var body = source.Length - BackupFile.HeaderSize;
        fullRecords = body / recordSize;
        lastRecord = (int)(body % recordSize);
        record = new byte[recordSize];
        plain = new byte[header.ChunkSize];
        // Every file ends in a record shorter than a whole one, if only a bare tag. Anything else ends early or late.
        if (lastRecord < BackupFile.Tag || fullRecords >= uint.MaxValue)
        {
            aes.Dispose();
            throw new BackupFileException(BackupFileFailure.Unreadable);
        }
        Length = fullRecords * header.ChunkSize + (lastRecord - BackupFile.Tag);
    }

    // Authenticates every record once, so a file that is damaged anywhere is refused before any of it is used.
    internal void Verify()
    {
        for (long chunk = 0; chunk <= fullRecords; chunk++) Load(chunk);
    }

    private void Load(long chunk)
    {
        if (cached == chunk) return;
        var final = chunk == fullRecords;
        var size = final ? lastRecord : header.ChunkSize + BackupFile.Tag;
        source.Position = BackupFile.HeaderSize + chunk * ((long)header.ChunkSize + BackupFile.Tag);
        if (source.ReadAtLeast(record.AsSpan(0, size), size, throwOnEndOfStream: false) < size)
            throw new BackupFileException(BackupFileFailure.Unreadable);
        Span<byte> nonce = stackalloc byte[BackupFile.NonceSize];
        Span<byte> associated = stackalloc byte[BackupFile.AssociatedSize];
        BackupFile.Nonce(header, (uint)chunk, nonce);
        BackupFile.AssociatedData(header, (uint)chunk, final, associated);
        var length = size - BackupFile.Tag;
        cached = -1;
        try { aes.Decrypt(nonce, record.AsSpan(0, length), record.AsSpan(length, BackupFile.Tag), plain.AsSpan(0, length), associated); }
        catch (AuthenticationTagMismatchException) { throw new BackupFileException(BackupFileFailure.Unreadable); }
        cached = chunk; cachedLength = length;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position
    {
        get => position;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); position = value; }
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var total = 0;
        while (destination.Length > 0 && position < Length)
        {
            var chunk = position / header.ChunkSize;
            var offset = (int)(position % header.ChunkSize);
            Load(chunk);
            var take = Math.Min(destination.Length, cachedLength - offset);
            if (take <= 0) break;
            plain.AsSpan(offset, take).CopyTo(destination);
            destination = destination[take..]; position += take; total += take;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => Length + offset,
        };
        return position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            aes.Dispose();
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(record);
        }
        base.Dispose(disposing);
    }
}
