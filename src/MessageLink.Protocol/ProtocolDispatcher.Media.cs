using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The media methods of the host: uploads into the UploadStore, sends that claim them, descriptions of a file
// checked without its bytes, and media read in chunks.
public sealed partial class ProtocolDispatcher
{
    /// <summary>The most a saved copy (<c>IAttachmentBackend.CopyToAsync</c>) may hold in the server's memory while its chunks are read.</summary>
    public const long MaxCopyBytes = 256L * 1024 * 1024;

    private static ProtocolException Invalid(string sentence = "The request's params are missing or not valid.") =>
        new(new ProtocolError(ProtocolErrorCodes.InvalidRequest, sentence));

    // A route on the upload store. `requireValues`: the params have a number whose absence would otherwise read as zero.
    private static Route OnUploads<TParams, TResult>(Func<UploadStore, TParams, CancellationToken, Task<TResult>> call, bool requireValues = false)
        where TParams : class
        => new(typeof(TParams), typeof(TResult), async (services, _, request, cancellationToken) =>
        {
            var parameters = ReadParams<TParams>(request);
            if (requireValues && !ParamsShape.HasRequiredValues(typeof(TParams), request.Params)) throw Invalid();
            var store = services.Uploads ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(store, parameters, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
        });

    private static byte[] DecodeChunk(string data)
    {
        // The text is measured before it is decoded, so an oversize chunk costs nothing.
        if (data.Length > UploadStore.MaxChunkText) throw Invalid("That chunk is too large.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw Invalid("That chunk is not valid."); }
        if (bytes.Length > UploadStore.MaxChunkBytes) throw Invalid("That chunk is too large.");
        return bytes;
    }

    // A file as a backend sees it for a question that needs only what describes it. No backend reads the bytes to answer one
    // (the checks are the length, the type and the conversation); one that did would get NotSupported, and its caller an
    // upload to make instead.
    private static AttachmentFile Described(AttachmentMeta meta) =>
        new(meta.FileName, meta.MimeType, meta.Length,
            _ => Task.FromException<Stream>(new NotSupportedException("This file is described, not uploaded.")), meta.IsVoiceNote, meta.DurationSeconds);

    // What a send of uploaded files is checked for at the door, before the journal records the operation: that the server can hold
    // uploads at all and the shape of the list. Whether the uploads are there is decided when the send claims them, inside the
    // journal, so a repeat of a send that already ran is answered with its first answer (the upload it used is long gone) instead
    // of being refused.
    private static void CheckUploads(ProtocolServices services, IReadOnlyList<UploadRef> uploads)
    {
        _ = services.Uploads ?? throw new NotSupportedException();
        if (uploads.Count is 0 or > MaxBatchFiles || uploads.Any(upload => upload is null || !ParamsShape.IsComplete(upload))) throw Invalid();
    }

    /// <summary>The most files one <see cref="ProtocolMethods.AttachmentsSendBatch"/> may name (the app's own limit is ten).</summary>
    public const int MaxBatchFiles = 32;

    private sealed record MediaKey(MessageAttachment Attachment, bool Inline, bool Save);

    /// <summary>
    /// The most media fetches one connection runs at once. The cache bounds what it keeps, not fetches still running: without this, a client could start
    /// as many whole-file reads as it has requests in flight (each up to <see cref="MaxCopyBytes"/>) and take the server's memory. Chunks of one item share
    /// its one fetch, so only distinct items wait here.
    /// </summary>
    public const int MaxMediaFetches = 4;
    private readonly SemaphoreSlim mediaFetches = new(MaxMediaFetches, MaxMediaFetches);

    private async Task<byte[]> FetchMediaAsync(MediaReadParams parameters, CancellationToken cancellationToken)
    {
        await mediaFetches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await (parameters.Save ? CopyOutAsync(parameters.Attachment, cancellationToken)
                : services.Media!.ReadImageAsync(parameters.Attachment, cancellationToken, parameters.Inline)).ConfigureAwait(false);
        }
        finally { mediaFetches.Release(); }
    }

    private async Task<byte[]> ReadMediaAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<MediaReadParams>(request);
        if (!ParamsShape.HasRequiredValues(typeof(MediaReadParams), request.Params) || parameters.Offset < 0 || parameters.Count < 1 || parameters.Count > UploadStore.MaxChunkBytes)
            throw Invalid();
        if (parameters.Save ? services.Attachments is null : services.Media is null) throw new NotSupportedException();
        // What one item is, however it is cut: its chunks all find the one fetch.
        var key = OperationJournal.Fingerprint(ProtocolMethods.MediaRead, ProtocolJson.ToElement(new MediaKey(parameters.Attachment, parameters.Inline, parameters.Save)));
        // A backend's "media_unavailable" is the word a picture's tile and the viewer decide on (MediaAnswers), so it crosses as the code word it is
        // (Coded) and not as the generic internal error, which left a remote tile saying Failed for what is only "not yet" (2026-10-12).
        var bytes = await readCache.GetAsync(key, token => Coded(() => FetchMediaAsync(parameters, token)), cancellationToken).ConfigureAwait(false);
        if (parameters.Offset > bytes.LongLength) throw Invalid("That is past the end.");
        var count = (int)Math.Min(parameters.Count, bytes.LongLength - parameters.Offset);
        if (parameters.Offset + count >= bytes.LongLength) readCache.Finish(key);
        return JsonSerializer.SerializeToUtf8Bytes(new MediaChunk(Convert.ToBase64String(bytes, (int)parameters.Offset, count), bytes.LongLength), ProtocolJson.Options);
    }

    private async Task<byte[]> CopyOutAsync(MessageAttachment attachment, CancellationToken cancellationToken)
    {
        using var sink = new LimitedMemory(MaxCopyBytes);
        await services.Attachments!.CopyToAsync(attachment, sink, cancellationToken).ConfigureAwait(false);
        return sink.ToArray();
    }

    // A memory stream that refuses to grow past its limit, so one copy cannot take the server's memory.
    private sealed class LimitedMemory(long limit) : MemoryStream
    {
        private void Check(long count) { if (Length + count > limit) throw new IOException("The file is larger than the host reads through."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
