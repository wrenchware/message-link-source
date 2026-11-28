using System.Buffers;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The client's side of media: turns an <see cref="AttachmentFile"/> into an upload and the chunks of a read
/// back into bytes, so that no <c>Func&lt;Stream&gt;</c>, <c>Stream</c> or <c>byte[]</c> crosses the wire as such. Chunks go one at a
/// time, each a round trip; a transport that makes that slow has a better way to move bytes (ranged HTTP), and these methods
/// stay for a pipe or a loopback.
/// </summary>
internal static class MediaTransfer
{
    /// <summary>The sentence for a file that could not be handed to the server: nothing was sent.</summary>
    public const string UploadFailedSentence = "The file could not be handed to the server. It wasn't sent.";

    /// <summary>
    /// Sends <paramref name="file"/>'s bytes up and returns the handle a send names. Any failure ends the upload on the server
    /// (best effort) and is thrown as it is: the caller decides what it means for a send, and until a send is requested nothing
    /// has been sent.
    /// </summary>
    public static async Task<UploadRef> UploadAsync(ProtocolClient client, AttachmentFile file, CancellationToken cancellationToken)
    {
        var begun = await client.CallAsync<UploadBeginParams, UploadBegun>(ProtocolMethods.MediaUploadBegin,
            new(file.MimeType, file.Length, file.Name), cancellationToken).ConfigureAwait(false);
        var uploadId = begun.UploadId;
        try
        {
            await using var source = await file.OpenRead(cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(UploadStore.MaxChunkBytes);
            try
            {
                long offset = 0;
                while (true)
                {
                    // A chunk is as full as the stream can make it, so a stream that answers in small pieces is not a slow upload.
                    var read = await source.ReadAtLeastAsync(buffer.AsMemory(0, UploadStore.MaxChunkBytes), UploadStore.MaxChunkBytes,
                        throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (offset + read > file.Length) throw new IOException("The file is longer than it said.");
                    var received = await client.CallAsync<UploadChunkParams, UploadReceived>(ProtocolMethods.MediaUploadChunk,
                        new(uploadId, offset, Convert.ToBase64String(buffer, 0, read)), cancellationToken).ConfigureAwait(false);
                    offset += read;
                    if (received.Received != offset) throw new IOException("The server holds another amount than was sent.");
                }
                if (offset != file.Length) throw new IOException("The file is not the length it said.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            var ended = await client.CallAsync<UploadIdParams, UploadEnded>(ProtocolMethods.MediaUploadEnd, new(uploadId), cancellationToken).ConfigureAwait(false);
            if (ended.Length != file.Length) throw new IOException("The server holds another amount than was sent.");
            return new UploadRef(uploadId, file.Name, file.MimeType, file.Length, file.IsVoiceNote, file.DurationSeconds);
        }
        catch
        {
            await AbortAsync(client, [uploadId]).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// How long cleaning up one upload waits for the server. The abort runs on the way out of a failure, and the failure is what the person is waiting to be told:
    /// a server that answers pings but not this (the case <see cref="ProtocolClient.CancelGrace"/> is for) must not hold the report back, and what it holds
    /// expires on its own.
    /// </summary>
    public static TimeSpan AbortWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks the server to drop uploads no send has claimed. Never throws and never waits on the caller's token: this runs after a
    /// failure or a cancel, and a connection that is gone has nothing left to clean (the server expires what it holds). Each ask waits
    /// at most <see cref="AbortWait"/>; one the server has not answered by then is left, not repeated.
    /// </summary>
    public static async Task AbortAsync(ProtocolClient client, IEnumerable<string> uploadIds)
    {
        foreach (var uploadId in uploadIds)
        {
            try
            {
                // The call's own token ends it a grace after this (ProtocolClient.CancelGrace); the wait here is what the caller is held for.
                using var bound = new CancellationTokenSource(AbortWait);
                await client.CallAsync<UploadIdParams, Done>(ProtocolMethods.MediaUploadAbort, new(uploadId), bound.Token).WaitAsync(AbortWait + TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// The most one item may be, whatever the server says: what a server reads through for a saved copy (<see cref="ProtocolDispatcher.MaxCopyBytes"/>). A total
    /// above it is refused before anything is kept, so a server cannot make this client hold more than it would itself.
    /// </summary>
    internal const long MaxItemBytes = ProtocolDispatcher.MaxCopyBytes;

    /// <summary>Everything <paramref name="attachment"/> is, as <c>IMediaReader.ReadImageAsync</c> would return it.</summary>
    public static async Task<byte[]> ReadAsync(ProtocolClient client, MessageAttachment attachment, bool inline, CancellationToken cancellationToken)
    {
        // Grown as the chunks arrive, never sized from the server's total: a server that claims a large item and sends little costs this client what it sent.
        byte[] whole = [];
        long total = -1, position = 0;
        while (true)
        {
            var chunk = await ReadChunkAsync(client, attachment, position, inline, save: false, cancellationToken).ConfigureAwait(false);
            if (total < 0)
            {
                if (chunk.Total is < 0 or > MaxItemBytes) throw Unreadable();
                total = chunk.Total;
            }
            else if (chunk.Total != total) throw Unreadable();
            var bytes = Decode(chunk.Data);
            if (position + bytes.Length > total) throw Unreadable();
            if (position + bytes.Length > whole.Length)
            {
                // Doubling, but never past the total, so the last growth leaves the array exactly the item's length.
                var grown = new byte[Math.Min(total, Math.Max(position + bytes.Length, (long)whole.Length * 2))];
                whole.AsSpan(0, (int)position).CopyTo(grown);
                whole = grown;
            }
            bytes.CopyTo(whole, position);
            position += bytes.Length;
            if (position == total) return whole;
            if (bytes.Length == 0) throw Unreadable();
        }
    }

    /// <summary>Writes everything <paramref name="attachment"/> is, as <c>IAttachmentBackend.CopyToAsync</c> would, to <paramref name="destination"/> a chunk at a time.</summary>
    public static async Task CopyAsync(ProtocolClient client, MessageAttachment attachment, Stream destination, CancellationToken cancellationToken)
    {
        long total = -1, position = 0;
        while (true)
        {
            var chunk = await ReadChunkAsync(client, attachment, position, inline: false, save: true, cancellationToken).ConfigureAwait(false);
            if (total < 0)
            {
                if (chunk.Total is < 0 or > MaxItemBytes) throw Unreadable();
                total = chunk.Total;
            }
            else if (chunk.Total != total) throw Unreadable();
            var bytes = Decode(chunk.Data);
            // The running count is held to the total, and the total to the limit, so no server keeps a copy going past it.
            if (position + bytes.Length > total || position + bytes.Length > MaxItemBytes) throw Unreadable();
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            position += bytes.Length;
            if (position == total) return;
            if (bytes.Length == 0) throw Unreadable();
        }
    }

    private static Task<MediaChunk> ReadChunkAsync(ProtocolClient client, MessageAttachment attachment, long offset, bool inline, bool save, CancellationToken cancellationToken) =>
        client.CallAsync<MediaReadParams, MediaChunk>(ProtocolMethods.MediaRead, new(attachment, offset, UploadStore.MaxChunkBytes, inline, save), cancellationToken);

    private static byte[] Decode(string data)
    {
        try { return Convert.FromBase64String(data); }
        catch (FormatException) { throw Unreadable(); }
    }

    private static BackendException Unreadable() => new(BackendFailure.InvalidResponse, ProtocolClient.UnreadableSentence);
}
