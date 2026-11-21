using NativeBeeper.App.Services;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// Media follows its owner (ConversationOwners): an attachment goes to the service its media name says, a send to the
// service that owns the conversation. What every service shares stays here: drafts, saved originals, the disk cache,
// clips kept ahead, pictures warmed ahead and stills made from videos.
internal sealed class MediaRouter(ConversationOwners owners,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] IAccessTokenStore tokens,
    NativeBeeper.Storage.MediaDiskCache disk, NativeBeeper.Presentation.Services.SavedPhotos? kept = null,
    IVideoFrameExtractor? frames = null)
    : IMediaReader, IAttachmentBackend, IAttachmentBatchBackend, IMediaPreparer
{
    internal ProviderAccounts? Accounts { get => owners.Accounts; set => owners.Accounts = value; }
    // Null where the host cannot make a frame from a video: a video that came with no still then draws as it did.
    private readonly VideoStills? stills = frames is null ? null : new(frames, disk, tokens);

    // A saved message's photo has its original kept on this PC (SavedPhotos). Opening it (not inline) reads that first, so
    // it opens whether or not the cache or the service still has it. Drawn in a conversation, the smaller copy is read as
    // before, and the kept original stands in only when that read fails: the cache let it go and the service no longer has
    // it, or the service is not connected.
    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false)
    {
        if (DraftMediaStore.TryRead(attachment.SourceUrl, out var draft)) return draft;
        if (!inline && kept is not null && await kept.ReadAsync(attachment, cancellationToken) is { } original) return original;
        try { return await ReadProviderImageAsync(attachment, cancellationToken, inline); }
        catch (Exception failure) when (failure is not OperationCanceledException && kept is not null)
        {
            if (await kept.ReadAsync(attachment, cancellationToken) is { } standIn) return standIn;
            throw;
        }
    }

    private Task<byte[]> ReadProviderImageAsync(MessageAttachment attachment, CancellationToken cancellationToken, bool inline)
        => DraftMediaStore.TryRead(attachment.SourceUrl, out var bytes) ? Task.FromResult(bytes)
            : inline && stills is not null && ProviderMedia.DerivesVideoStill(attachment) ? ReadDerivedStillAsync(attachment, cancellationToken)
            : owners.OfMedia(attachment).ReadImageAsync(attachment, inline, shelf, cancellationToken);

    // A video whose network sent no still (Google Messages often sends none; Google Voice never does) shows a frame made
    // from the video itself. The video is read the way playing it reads it, so it is in the provider's cache when play is
    // pressed, and the frame is kept apart from it (VideoStills). Only for the tile that is drawing: not for a copy that is opened.
    private Task<byte[]> ReadDerivedStillAsync(MessageAttachment video, CancellationToken cancellationToken) =>
        stills!.ReadAsync(video, token => owners.OfMedia(video).ReadVideoAsync(video, shelf, token), cancellationToken);

    // How long a picture, clip or recording is kept on disk, and how far each read pushes that out again (MediaDiskCache:
    // renewed at most once a day), so one somebody keeps looking at is not fetched again thirty days after it was saved.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    // From the disk cache when it holds the bytes, otherwise from the provider and then kept. A failed write costs only
    // the next launch's copy: the picture still draws and the clip still plays.
    private async Task<byte[]> KeptAsync(string? key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken)
    {
        var profile = key is null ? null : await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        if (profile is not null && await disk.ReadAsync(profile, key!, cancellationToken, renewFor: Kept) is { } saved) return saved;
        var bytes = await fetch(cancellationToken);
        if (profile is not null)
        {
            try { await disk.WriteAsync(profile, key!, bytes, Kept, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* Only the next launch misses. */ }
        }
        return bytes;
    }

    // What a service is lent for reading (IMediaShelf): the cache above, and the note of a still its provider sent.
    private IMediaShelf shelf => field ??= new Shelf(this);
    private sealed class Shelf(MediaRouter router) : IMediaShelf
    {
        public Task<byte[]> KeptAsync(string? key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken) => router.KeptAsync(key, fetch, cancellationToken);
        public void NoteProviderStill(MessageAttachment video) => router.stills?.NoteProvider(video);
    }

    // A GIF that Facebook or Instagram delivers as a short clip plays every time its message comes on screen, so it is kept
    // like a photo: without this each scroll past one downloaded it again, a request of the user's real account every
    // time (Instagram), and after each launch Messenger's were blank until it had connected. Keyed as its service says;
    // null for a service whose clips are not kept here.
    private string? ClipKey(MessageAttachment attachment) =>
        NativeBeeper.Presentation.Services.IncomingPhotos.IsClip(attachment) ? owners.OfMedia(attachment).ClipKey(attachment) : null;

    private Task<byte[]> ReadClipAsync(MessageAttachment attachment, string key, CancellationToken cancellationToken, long limit = long.MaxValue) =>
        KeptAsync(key, async token =>
        {
            using var memory = new CappedStream(limit);
            await owners.OfMedia(attachment).CopyToAsync(attachment, memory, shelf, token);
            return memory.ToArray();
        }, cancellationToken);

    /// <summary>
    /// A GIF clip's bytes kept on disk ahead of its conversation opening (IncomingPhotos), so it plays at once, before its
    /// provider has reconnected. Only a clip a provider here keeps (ClipKey), only one not already kept, and never more
    /// than <paramref name="limit"/> bytes: a clip whose size says more is left alone, and one that turns out larger is
    /// abandoned part way. Never a video: only what a conversation would play by itself. Whether anything was fetched.
    /// </summary>
    public async Task<bool> PrepareClipAsync(MessageAttachment clip, long limit, CancellationToken cancellationToken = default)
    {
        if (ClipKey(clip) is not { } key || clip.Size > limit) return false;
        if (await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken) is not { } profile ||
            await disk.HoldsAsync(profile, key, cancellationToken)) return false;
        await ReadClipAsync(clip, key, cancellationToken, limit);
        return true;
    }

    /// <summary>
    /// A picture already in a conversation, put in the media cache ahead of its opening (RecentPhotos): the smaller copy a
    /// conversation draws, read exactly as drawing it reads it. How many bytes were downloaded; 0 when the cache already
    /// holds it, so nothing was asked; null for a picture not warmed here: Instagram's (every request is the user's real
    /// account), one sent from this PC, and any whose copy the cache does not keep.
    /// </summary>
    public async Task<long?> PrepareImageAsync(MessageAttachment picture, CancellationToken cancellationToken = default)
    {
        // Nor a video that has no still of its own: its frame costs the whole video, which only a tile that is drawing asks for.
        if (DraftMediaStore.TryRead(picture.SourceUrl, out _) || ProviderMedia.DerivesVideoStill(picture)) return null;
        // The key each service's read keeps the picture under (WarmKey); none for a service that is not warmed.
        var key = owners.OfMedia(picture).WarmKey(picture);
        if (string.IsNullOrEmpty(key) ||
            await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken) is not { } profile) return null;
        if (await disk.HoldsAsync(profile, key, cancellationToken)) return 0;
        var bytes = await ReadProviderImageAsync(picture, cancellationToken, inline: true);
        return bytes.LongLength;
    }

    // A memory stream that refuses to grow past its limit, so a clip prepared ahead can never become a large download.
    private sealed class CappedStream(long limit) : MemoryStream
    {
        private void Check(long count) { if (Length + count > limit) throw new IOException("The clip is larger than may be prepared ahead."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }

    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default)
    {
        if (DraftMediaStore.TryRead(attachment.SourceUrl, out var bytes))
        {
            await destination.WriteAsync(bytes, cancellationToken);
            return;
        }
        // A saved message's photo is saved from its kept original, as it opens from it.
        if (kept is not null && await kept.ReadAsync(attachment, cancellationToken) is { } original)
        {
            await destination.WriteAsync(original, cancellationToken);
            return;
        }
        // A clip already played is saved from the cache instead of downloaded again. So is a Voice recording or photo
        // already played or seen, which Voice's own copy decides.
        if (ClipKey(attachment) is { } clip)
            await destination.WriteAsync(await ReadClipAsync(attachment, clip, cancellationToken), cancellationToken);
        else await owners.OfMedia(attachment).CopyToAsync(attachment, destination, shelf, cancellationToken);
    }

    public bool CanSave(MessageAttachment attachment)
        => DraftMediaStore.TryRead(attachment.SourceUrl, out _) || owners.OfMedia(attachment).CanSave(attachment);

    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default)
        => owners.Of(chatId).GetAttachmentPermissionAsync(chatId, cancellationToken);

    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => owners.Of(chatId).GetFilePermissionAsync(chatId, file, cancellationToken);

    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => owners.Of(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken);

    public Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
        => owners.Of(chatId).SendFileAsync(chatId, file, caption, cancellationToken);

    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files)
        => owners.Of(chatId).CanSendBatch(chatId, files);

    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
        => owners.Of(chatId).SendBatchAsync(chatId, files, cancellationToken);

    // Whether every photo can be drawn when Send is pressed: Google Messages' alone (its copies carry their receipts).
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files)
        => owners.Of(chatId).DrawsPhotosAtOnce(chatId, files);
}
