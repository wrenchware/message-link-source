using MessageLink.Core.Backend;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// Where a server keeps the files a client has sent up for a send that has not happened yet (<c>media.upload*</c>). One store serves
/// every connection of a server. An upload is begun with its declared length (which is reserved at once), filled by chunks whose
/// offsets follow one another, and ended; only a finished upload can be claimed by a send, and a send that has run (however it
/// ended) deletes what it claimed.
/// <list type="bullet">
/// <item>Bounds, each settable: <see cref="MaxUploadBytes"/> for one upload, <see cref="MaxTotalBytes"/> for everything held at once
/// (declared lengths, so a client cannot open many large uploads and fill them later), and <see cref="MaxUploads"/> in number.</item>
/// <item>An upload nobody has touched for <see cref="IdleLifetime"/> is deleted, unless a send holds it. Expiry is checked on every
/// operation, so a server with no traffic holds nothing it was not asked to hold for longer than the next call.</item>
/// <item>Where the bytes live: in memory up to <see cref="MemoryThreshold"/>, in a file under the folder the server gave above that.
/// A server with no folder keeps everything in memory. The folder is the server's scratch space, never its protected data. A file holds
/// only ciphertext (<see cref="UploadCipher"/>) under a key made when the store was made and kept only in its memory, and is named by
/// a random id. Every spill file already in the folder when the store is made is an earlier run's, unreadable now, and is deleted.</item>
/// </list>
/// Log lines carry fixed words and sizes only: never a file name, a type or a byte.
/// </summary>
public sealed class UploadStore : IDisposable
{
    /// <summary>The most bytes one <c>media.uploadChunk</c> may carry, before base64.</summary>
    public const int MaxChunkBytes = 1024 * 1024;

    /// <summary>
    /// The longest base64 text of a chunk of <see cref="MaxChunkBytes"/>. The most any provider here takes is 50 MB (Google
    /// Messages, and Voice's source limit), so <see cref="MaxUploadBytes"/> leaves room above that.
    /// </summary>
    public static int MaxChunkText => (MaxChunkBytes + 2) / 3 * 4;

    internal sealed class Upload(string id, string mimeType, long length, DateTimeOffset touched, string? path, UploadCipher? cipher)
    {
        public string Id { get; } = id;
        public string MimeType { get; } = mimeType;
        public long Length { get; } = length;
        public string? Path { get; } = path;
        public DateTimeOffset Touched = touched;
        public long Received;
        public bool Sealed;
        public bool Claimed;
        public bool Removed;
        // While receiving: the stream bytes are written to (a MemoryStream, or a file). Once sealed a file's stream is closed
        // and each reader opens the file again; a MemoryStream stays and each reader gets a view of its buffer.
        public Stream? Writer;
        public readonly SemaphoreSlim Gate = new(1, 1);

        public Stream OpenRead() => Path is null
            ? new MemoryStream(((MemoryStream)Writer!).GetBuffer(), 0, checked((int)Length), writable: false)
            : cipher!.OpenReader(Path, Length);
    }

    private readonly string? folder;
    private readonly UploadCipher? cipher;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly Dictionary<string, Upload> uploads = new(StringComparer.Ordinal);
    private long reserved;
    private bool disposed;

    public UploadStore(string? folder = null, TimeProvider? time = null, Action<string>? log = null)
    {
        this.folder = folder;
        clock = time ?? TimeProvider.System;
        this.log = log;
        if (folder is not null) cipher = new UploadCipher();
        SweepOrphans();
    }

    /// <summary>The longest file one upload may hold.</summary>
    public long MaxUploadBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>The most bytes (declared lengths) held at once, across all uploads.</summary>
    public long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>The most uploads held at once.</summary>
    public int MaxUploads { get; set; } = 64;

    /// <summary>How long an upload that nobody touches is kept.</summary>
    public TimeSpan IdleLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Uploads up to this many bytes stay in memory even when the server gave a folder (photos then never touch the disk).</summary>
    public long MemoryThreshold { get; set; } = 8L * 1024 * 1024;

    /// <summary>How many uploads are held now.</summary>
    public int Count { get { lock (gate) return uploads.Count; } }

    /// <summary>The declared bytes held now.</summary>
    public long ReservedBytes { get { lock (gate) return reserved; } }

    private static ProtocolException Refuse(string sentence) => new(new ProtocolError(ProtocolErrorCodes.InvalidRequest, sentence));
    private static ProtocolException Unknown() => Refuse("The upload is not known to the server.");

    // ---- Receiving ----

    /// <summary>Starts an upload of <paramref name="length"/> bytes and returns its handle.</summary>
    public string Begin(string mimeType, long length)
    {
        Sweep();
        if (length < 0 || length > MaxUploadBytes)
        {
            log?.Invoke("protocol upload refused reason=size");
            throw Refuse("That file is too large to send.");
        }
        Upload upload;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (uploads.Count >= MaxUploads || reserved + length > MaxTotalBytes)
            {
                log?.Invoke("protocol upload refused reason=full");
                throw new BackendException(BackendFailure.RateLimited, "The server is holding too many uploads. Try again in a moment.");
            }
            var id = Guid.NewGuid().ToString("N");
            var inFile = folder is not null && length > MemoryThreshold;
            upload = new Upload(id, mimeType, length, clock.GetUtcNow(), inFile ? Path.Combine(folder!, id + FileExtension) : null, cipher);
            uploads[id] = upload;
            reserved += length;
        }
        try
        {
            if (upload.Path is null) upload.Writer = new MemoryStream();
            else
            {
                Directory.CreateDirectory(folder!);
                upload.Writer = cipher!.CreateWriter(upload.Path, length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Remove(upload);
            log?.Invoke("protocol upload refused reason=storage");
            throw new BackendException(BackendFailure.ServerError, "The server could not store the file.");
        }
        log?.Invoke($"protocol upload begin bytes={length} open={Count}");
        return upload.Id;
    }

    /// <summary>
    /// Adds <paramref name="data"/> at <paramref name="offset"/>, which must be where the last chunk ended. Returns the bytes held now.
    /// A chunk that is too large, out of order, past the declared length or for an upload that is not known (or is already ended)
    /// is refused <c>invalid_request</c> and changes nothing.
    /// </summary>
    public async Task<long> AppendAsync(string uploadId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Sweep();
        var upload = Find(uploadId);
        if (data.Length > MaxChunkBytes) throw Refuse("That chunk is too large.");
        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (upload.Removed) throw Unknown();
            if (upload.Sealed) throw Refuse("The upload has ended.");
            if (offset != upload.Received) throw Refuse("The chunk does not continue the upload.");
            if (upload.Received + data.Length > upload.Length) throw Refuse("The chunk is past the length the upload declared.");
            await upload.Writer!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            upload.Received += data.Length;
            return upload.Received;
        }
        finally { upload.Gate.Release(); }
    }

    /// <summary>Seals the upload once everything it declared has arrived. A second call on a sealed upload answers its length again.</summary>
    public async Task<long> EndAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        Sweep();
        var upload = Find(uploadId);
        await upload.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (upload.Removed) throw Unknown();
            if (upload.Sealed) return upload.Length;
            if (upload.Received != upload.Length)
            {
                log?.Invoke("protocol upload refused reason=length");
                throw Refuse("The upload is not the length it declared.");
            }
            if (upload.Path is not null)
            {
                await upload.Writer!.FlushAsync(cancellationToken).ConfigureAwait(false);
                await upload.Writer.DisposeAsync().ConfigureAwait(false);
                upload.Writer = null;
            }
            upload.Sealed = true;
            log?.Invoke($"protocol upload end bytes={upload.Length}");
            return upload.Length;
        }
        finally { upload.Gate.Release(); }
    }

    /// <summary>Drops an upload that no send holds. Whether there was one.</summary>
    public bool Abort(string uploadId)
    {
        Upload? upload;
        lock (gate)
        {
            if (!uploads.TryGetValue(uploadId, out upload) || upload.Claimed) return false;
        }
        Remove(upload);
        log?.Invoke("protocol upload aborted");
        return true;
    }

    // ---- Sending ----

    /// <summary>
    /// Takes the uploads <paramref name="refs"/> names for one send, all or none: each must be finished, unclaimed, and the length and
    /// type it was begun with, or <c>invalid_request</c> is thrown and nothing is claimed. Disposing the claim deletes them.
    /// </summary>
    public UploadClaim Claim(IReadOnlyList<UploadRef> refs) => new(this, Resolve(refs), refs);

    private List<Upload> Resolve(IReadOnlyList<UploadRef> refs)
    {
        Sweep();
        var found = new List<Upload>(refs.Count);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var reference in refs)
            {
                if (reference is null || !uploads.TryGetValue(reference.UploadId ?? "", out var upload) || upload.Claimed || !upload.Sealed
                    || upload.Length != reference.Length || !string.Equals(upload.MimeType, reference.MimeType, StringComparison.OrdinalIgnoreCase)
                    || found.Contains(upload))
                    throw Refuse("An upload this request names is not ready or does not match.");
                found.Add(upload);
            }
            foreach (var upload in found) { upload.Claimed = true; upload.Touched = now; }
        }
        return found;
    }

    /// <summary>The uploads one send holds. Disposing it, which a send does when it ends however it ended, deletes them.</summary>
    public sealed class UploadClaim : IDisposable
    {
        private readonly UploadStore store;
        private readonly List<Upload> held;
        private readonly IReadOnlyList<UploadRef> refs;
        private bool disposed;

        internal UploadClaim(UploadStore store, List<Upload> held, IReadOnlyList<UploadRef> refs)
        {
            this.store = store; this.held = held; this.refs = refs;
        }

        /// <summary>The file the server hands a backend: its bytes are the claimed upload's, read afresh by each <c>OpenRead</c>.</summary>
        public AttachmentFile FileAt(int index)
        {
            var reference = refs[index];
            var upload = held[index];
            return new AttachmentFile(reference.FileName, reference.MimeType, reference.Length,
                _ => Task.FromResult(upload.OpenRead()), reference.IsVoiceNote, reference.DurationSeconds);
        }

        public IReadOnlyList<AttachmentFile> Files() => [.. Enumerable.Range(0, held.Count).Select(FileAt)];

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var upload in held) store.Remove(upload);
            store.log?.Invoke($"protocol upload consumed bytes={held.Sum(upload => upload.Length)} files={held.Count}");
        }
    }

    // ---- Housekeeping ----

    /// <summary>Deletes the uploads nobody has touched for <see cref="IdleLifetime"/> and no send holds. How many.</summary>
    public int Sweep()
    {
        List<Upload>? expired = null;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var upload in uploads.Values)
                if (!upload.Claimed && now - upload.Touched >= IdleLifetime) (expired ??= []).Add(upload);
        }
        if (expired is null) return 0;
        foreach (var upload in expired) Remove(upload);
        log?.Invoke($"protocol upload expired n={expired.Count}");
        return expired.Count;
    }

    private Upload Find(string uploadId)
    {
        lock (gate)
        {
            if (!uploads.TryGetValue(uploadId, out var upload)) throw Unknown();
            upload.Touched = clock.GetUtcNow();
            return upload;
        }
    }

    private void Remove(Upload upload)
    {
        lock (gate)
        {
            if (!uploads.Remove(upload.Id)) return;
            reserved -= upload.Length;
        }
        // Waits for a chunk that is being written, so the file is closed before it is deleted.
        upload.Gate.Wait();
        try
        {
            upload.Removed = true;
            upload.Writer?.Dispose();
            upload.Writer = null;
            if (upload.Path is not null)
            {
                try { File.Delete(upload.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept with the next run's orphans */ }
            }
        }
        finally { upload.Gate.Release(); }
    }

    private const string FileExtension = ".upload";

    // Whatever spill file is in the folder now, an earlier run wrote it under a key that died with that run: it can never be read.
    private void SweepOrphans()
    {
        if (folder is null || !Directory.Exists(folder)) return;
        var swept = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*" + FileExtension))
                try { File.Delete(path); swept++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (swept > 0) log?.Invoke($"uploads swept files={swept}");
    }

    /// <summary>Deletes everything held.</summary>
    public void Dispose()
    {
        List<Upload> all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            all = [.. uploads.Values];
        }
        foreach (var upload in all) Remove(upload);
        cipher?.Dispose();
    }
}
