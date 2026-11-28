using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MessageLink.Protocol;

/// <summary>
/// How <see cref="UploadStore"/> keeps a large upload on disk: only as ciphertext, under a key made when the store was made and held
/// only in its memory, so a file an earlier run left behind (or one anybody copies) is unreadable. The format is a 16-byte header
/// (magic, version, an 8-byte random nonce prefix) followed by chunks of <see cref="ChunkSize"/> plaintext bytes (the last one
/// shorter), each written as its AES-256-GCM ciphertext and 16-byte tag. A chunk's nonce is the prefix and its index, so no two
/// chunks of any file share one; its associated data is the upload's declared length and whether it is the last chunk, so a
/// chunk moved, dropped, or cut short fails its tag, and a file of any other size than the declared length implies is refused at once.
/// </summary>
internal sealed class UploadCipher : IDisposable
{
    public const int ChunkSize = 1024 * 1024;
    public const int TagSize = 16;
    public const int HeaderSize = 16;
    private const int NonceSize = 12;
    private const int AssociatedSize = 9;
    private const byte Version = 1;
    private static readonly byte[] Magic = "MWUS"u8.ToArray();

    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public static long ChunkCount(long length) => (length + ChunkSize - 1) / ChunkSize;

    public static long FileLength(long length) => HeaderSize + length + TagSize * ChunkCount(length);

    private static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, long index)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], checked((uint)index));
    }

    private static void Associated(Span<byte> data, long length, bool last)
    {
        BinaryPrimitives.WriteInt64LittleEndian(data, length);
        data[8] = last ? (byte)1 : (byte)0;
    }

    public Stream CreateWriter(string path, long length) => new Writer(this, path, length);

    public Stream OpenReader(string path, long length) => new Reader(this, path, length);

    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    /// <summary>Write-only and sequential: a chunk is sealed and written as soon as it is full or the declared length has arrived.</summary>
    private sealed class Writer : Stream
    {
        private readonly FileStream file;
        private readonly AesGcm aes;
        private readonly byte[] prefix = RandomNumberGenerator.GetBytes(8);
        private readonly long length;
        private byte[]? plain = new byte[ChunkSize];
        private byte[] sealedChunk = new byte[ChunkSize + TagSize];
        private int buffered;
        private long accepted;
        private long chunks;

        public Writer(UploadCipher owner, string path, long length)
        {
            this.length = length;
            aes = new AesGcm(owner.key, TagSize);
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                var header = new byte[HeaderSize];
                Magic.CopyTo(header, 0);
                header[4] = Version;
                prefix.CopyTo(header, 8);
                file.Write(header);
            }
            catch { aes.Dispose(); throw; }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => accepted;
        public override long Position { get => accepted; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(plain is null, this);
            if (accepted + data.Length > length) throw new InvalidOperationException("The upload is past its declared length.");
            while (data.Length > 0)
            {
                var take = Math.Min(data.Length, ChunkSize - buffered);
                data.Span[..take].CopyTo(plain.AsSpan(buffered));
                buffered += take;
                accepted += take;
                data = data[take..];
                if (buffered == ChunkSize || accepted == length)
                {
                    var bytes = Seal();
                    await file.WriteAsync(sealedChunk.AsMemory(0, bytes), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Encrypts the buffered chunk into sealedChunk (ciphertext, then tag) and returns the bytes to write.
        private int Seal()
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            Span<byte> associated = stackalloc byte[AssociatedSize];
            Nonce(nonce, prefix, chunks);
            Associated(associated, length, accepted == length);
            aes.Encrypt(nonce, plain.AsSpan(0, buffered), sealedChunk.AsSpan(0, buffered), sealedChunk.AsSpan(buffered, TagSize), associated);
            var bytes = buffered + TagSize;
            chunks++;
            buffered = 0;
            return bytes;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
                plain = null;
                aes.Dispose();
                file.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Read-only and seekable; each chunk is verified as it is first needed, and a chunk that fails, or a file of the wrong size,
    /// throws <see cref="InvalidDataException"/> rather than return a byte.
    /// </summary>
    private sealed class Reader : Stream
    {
        private readonly FileStream file;
        private readonly AesGcm aes;
        private readonly byte[] prefix = new byte[8];
        private readonly long length;
        private byte[]? plain = new byte[ChunkSize];
        private readonly byte[] sealedChunk = new byte[ChunkSize + TagSize];
        private long loaded = -1;
        private int loadedBytes;
        private long position;

        public Reader(UploadCipher owner, string path, long length)
        {
            this.length = length;
            aes = new AesGcm(owner.key, TagSize);
            try
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
                try
                {
                    var header = new byte[HeaderSize];
                    if (file.Length != FileLength(length) || file.Read(header) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] != Version)
                        throw Corrupt();
                    header.AsSpan(8).CopyTo(prefix);
                }
                catch { file.Dispose(); throw; }
            }
            catch { aes.Dispose(); throw; }
        }

        private static InvalidDataException Corrupt() => new("The stored upload is damaged.");

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
            return position;
        }

        private int CipherBytes(long index) => (int)Math.Min(ChunkSize, length - index * ChunkSize) + TagSize;

        private static long Offset(long index) => HeaderSize + index * (ChunkSize + TagSize);

        // Decrypts the chunk just read into sealedChunk; a chunk that does not verify is never exposed.
        private void Open(long index, int cipherBytes)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            Span<byte> associated = stackalloc byte[AssociatedSize];
            Nonce(nonce, prefix, index);
            Associated(associated, length, index == ChunkCount(length) - 1);
            var bytes = cipherBytes - TagSize;
            loaded = -1;
            try { aes.Decrypt(nonce, sealedChunk.AsSpan(0, bytes), sealedChunk.AsSpan(bytes, TagSize), plain.AsSpan(0, bytes), associated); }
            catch (CryptographicException) { throw Corrupt(); }
            loaded = index;
            loadedBytes = bytes;
        }

        private int Take(Span<byte> destination)
        {
            var inChunk = (int)(position % ChunkSize);
            var count = Math.Min(destination.Length, loadedBytes - inChunk);
            plain.AsSpan(inChunk, count).CopyTo(destination);
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(plain is null, this);
            if (position >= length || destination.IsEmpty) return 0;
            var index = position / ChunkSize;
            if (loaded != index)
            {
                var cipherBytes = CipherBytes(index);
                file.Position = Offset(index);
                try { file.ReadExactly(sealedChunk.AsSpan(0, cipherBytes)); } catch (EndOfStreamException) { throw Corrupt(); }
                Open(index, cipherBytes);
            }
            return Take(destination);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(plain is null, this);
            if (position >= length || destination.IsEmpty) return 0;
            var index = position / ChunkSize;
            if (loaded != index)
            {
                var cipherBytes = CipherBytes(index);
                file.Position = Offset(index);
                try { await file.ReadExactlyAsync(sealedChunk.AsMemory(0, cipherBytes), cancellationToken).ConfigureAwait(false); }
                catch (EndOfStreamException) { throw Corrupt(); }
                Open(index, cipherBytes);
            }
            return Take(destination.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
                plain = null;
                aes.Dispose();
                file.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
