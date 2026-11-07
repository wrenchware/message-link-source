using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// Media follows its conversation: Messenger photos, files and profile pictures come from the Messenger helper, and
// everything else from Google Messages. Sending attachments to Messenger is refused by name.
internal sealed class MediaRouter(GoogleMediaBackend google, MessengerBackend messenger,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] IAccessTokenStore tokens,
    NativeBeeper.Storage.MediaDiskCache disk, NativeBeeper.App.Services.GoogleVoice.GoogleVoiceBackend voice,
    NativeBeeper.App.Services.Instagram.InstagramBackend instagram, NativeBeeper.Presentation.Services.SavedPhotos? kept = null,
    IVideoFrameExtractor? frames = null)
    : IMediaReader, IAttachmentBackend, IAttachmentBatchBackend, IMediaPreparer
{
    internal ProviderAccounts? Accounts { get; set; }
    private GoogleMediaBackend Google(string id) => Accounts?.GoogleMedia(id) ?? google;
    private MessengerBackend Messenger(string id) => Accounts?.Messenger(id) ?? messenger;
    private NativeBeeper.App.Services.Instagram.InstagramBackend InstagramAccount(string id) => Accounts?.Instagram(id) ?? instagram;
    // Null where the host cannot make a frame from a video: a video that came with no still then draws as it did.
    private readonly VideoStills? stills = frames is null ? null : new(frames, disk, tokens);

    private static bool Mine(MessageAttachment attachment) => MessengerModels.OwnsMedia(attachment);
    private static bool Voice(MessageAttachment attachment) => NativeBeeper.App.Services.GoogleVoice.GoogleVoiceModels.OwnsMedia(attachment);
    private static bool Voice(string chatId) => NativeBeeper.App.Services.GoogleVoice.GoogleVoiceModels.Owns(chatId);
    private static bool Insta(string chatId) => NativeBeeper.App.Services.Instagram.InstagramIdentity.OwnsAny(chatId);
    private static bool Insta(MessageAttachment attachment) => NativeBeeper.App.Services.Instagram.InstagramIdentity.OwnsMedia(attachment);

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
            : Voice(attachment) ? ReadVoiceMediaAsync(attachment, cancellationToken)
            : Insta(attachment) ? ReadInstagramImageAsync(attachment, cancellationToken, inline)
            : Mine(attachment) ? ReadMessengerImageAsync(attachment, cancellationToken, inline) : ReadGoogleImageAsync(attachment, cancellationToken, inline);

    // A video whose network sent no still (Google Messages often sends none; Google Voice never does) shows a frame made
    // from the video itself. The video is read the way playing it reads it, so it is in the provider's cache when play is
    // pressed, and the frame is kept apart from it (VideoStills). Only for the tile that is drawing: not for a copy that is opened.
    private Task<byte[]> ReadDerivedStillAsync(MessageAttachment video, CancellationToken cancellationToken) =>
        stills!.ReadAsync(video, token => ReadVideoAsync(video, token), cancellationToken);

    private async Task<byte[]> ReadVideoAsync(MessageAttachment video, CancellationToken cancellationToken)
    {
        if (Voice(video)) return await ReadVoiceMediaAsync(video, cancellationToken);
        using var memory = new MemoryStream();
        await Google(video.Id ?? video.SourceUrl ?? "").CopyToAsync(video, memory, cancellationToken);
        return memory.ToArray();
    }

    private async Task<byte[]> ReadGoogleImageAsync(MessageAttachment attachment, CancellationToken cancellationToken, bool inline)
    {
        var bytes = await Google(attachment.Id ?? attachment.SourceUrl ?? "").ReadImageAsync(attachment, cancellationToken, inline);
        if (inline && stills is not null && attachment.Type == "video" && !attachment.IsGif && ProviderMedia.IsVideoStill(attachment.SourceUrl))
            stills.NoteProvider(attachment);
        return bytes;
    }

    // How long a picture, clip or recording is kept on disk, and how far each read pushes that out again (MediaDiskCache:
    // renewed at most once a day), so one somebody keeps looking at is not fetched again thirty days after it was saved.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    // Messenger photos persist in the same encrypted media cache as Google Messages photos, so a conversation seen before
    // draws them at once on the next launch, even before Messenger has reconnected, instead of every helper session
    // downloading and decrypting each photo again. Link cards' pictures the helper made are among them (ImageCacheKey).
    private Task<byte[]> ReadMessengerImageAsync(MessageAttachment attachment, CancellationToken cancellationToken, bool inline) =>
        ReadKeptAsync(MessengerBackend.ImageCacheKey(attachment, inline), token => Messenger(attachment.Id ?? attachment.SourceUrl ?? "").ReadImageAsync(attachment, token, inline), cancellationToken);

    // Instagram photos and profile pictures persist in the same encrypted media cache as everything else, so a
    // conversation seen before draws them at once on the next launch, even before Instagram has reconnected, instead
    // of each helper session downloading them again. Instagram's own urls expire; the id does not.
    private Task<byte[]> ReadInstagramImageAsync(MessageAttachment attachment, CancellationToken cancellationToken, bool inline) =>
        ReadKeptAsync(NativeBeeper.App.Services.Instagram.InstagramBackend.ImageCacheKey(attachment, inline),
            token => InstagramAccount(attachment.Id ?? attachment.SourceUrl ?? "").ReadImageAsync(attachment, token, inline), cancellationToken);

    // Google Voice photos and voicemail audio are kept in the same encrypted, profile-scoped cache, keyed by their
    // account-qualified id, so a voicemail played once plays again at once and without asking Google.
    private Task<byte[]> ReadVoiceMediaAsync(MessageAttachment attachment, CancellationToken cancellationToken) =>
        ReadKeptAsync(attachment.Id, token => voice.ReadImageAsync(attachment, token), cancellationToken);

    // From the disk cache when it holds the bytes, otherwise from the provider and then kept. A failed write costs only
    // the next launch's copy: the picture still draws and the clip still plays.
    private async Task<byte[]> ReadKeptAsync(string? key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken)
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

    // A GIF that Facebook or Instagram delivers as a short clip plays every time its message comes on screen, so it is kept
    // like a photo: without this each scroll past one downloaded it again, a request of the user's real account every
    // time (Instagram), and after each launch Messenger's were blank until it had connected. Keyed by the clip's own id,
    // which names the same clip in every helper session; null for a provider whose clips are not kept here.
    private static string? ClipKey(MessageAttachment attachment) =>
        !NativeBeeper.Presentation.Services.IncomingPhotos.IsClip(attachment) ? null
            : Insta(attachment) ? "instagram-clip:" + NativeBeeper.App.Services.Instagram.InstagramIdentity.NativeMedia(attachment)
            : Mine(attachment) ? MessengerBackend.ClipCacheKey(attachment) : null;

    private Task<byte[]> ReadClipAsync(MessageAttachment attachment, string key, CancellationToken cancellationToken, long limit = long.MaxValue) =>
        ReadKeptAsync(key, async token =>
        {
            using var memory = new CappedStream(limit);
            if (Insta(attachment)) await InstagramAccount(attachment.Id ?? attachment.SourceUrl ?? "").CopyToAsync(attachment, memory, token);
            else await Messenger(attachment.Id ?? attachment.SourceUrl ?? "").CopyToAsync(attachment, memory, token);
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
        if (DraftMediaStore.TryRead(picture.SourceUrl, out _) || Insta(picture) || ProviderMedia.DerivesVideoStill(picture)) return null;
        // The key each provider's read keeps the picture under: Voice by its id, Messenger by the smaller copy's source,
        // Google by its id (a video's still under ":thumbnail"), as their reads above and GoogleMediaBackend write them.
        var key = Voice(picture) ? picture.Id
            : Mine(picture) ? MessengerBackend.ImageCacheKey(picture, inline: true)
            : Google(picture.Id ?? picture.SourceUrl ?? "").CanSave(picture) ? picture.Id + (picture.Type == "video" ? ":thumbnail" : "") : null;
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
        // A Voice recording or photo already played or seen is saved from the cache instead of downloaded again.
        if (Voice(attachment)) await destination.WriteAsync(await ReadVoiceMediaAsync(attachment, cancellationToken), cancellationToken);
        else if (ClipKey(attachment) is { } clip)
            await destination.WriteAsync(await ReadClipAsync(attachment, clip, cancellationToken), cancellationToken);
        else if (Insta(attachment)) await InstagramAccount(attachment.Id ?? attachment.SourceUrl ?? "").CopyToAsync(attachment, destination, cancellationToken);
        else if (Mine(attachment)) await Messenger(attachment.Id ?? attachment.SourceUrl ?? "").CopyToAsync(attachment, destination, cancellationToken);
        else await Google(attachment.Id ?? attachment.SourceUrl ?? "").CopyToAsync(attachment, destination, cancellationToken);
    }

    public bool CanSave(MessageAttachment attachment)
        => DraftMediaStore.TryRead(attachment.SourceUrl, out _) || (Voice(attachment) ? voice.CanSave(attachment)
            : Insta(attachment) ? InstagramAccount(attachment.Id ?? attachment.SourceUrl ?? "").CanSave(attachment)
            : Mine(attachment) ? Messenger(attachment.Id ?? attachment.SourceUrl ?? "").CanSave(attachment) : Google(attachment.Id ?? attachment.SourceUrl ?? "").CanSave(attachment));

    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).GetPermissionAsync(chatId, cancellationToken)
            : Voice(chatId) ? voice.GetSendPermissionAsync(chatId) : MessengerModels.Owns(chatId) ? Messenger(chatId).GetPermissionAsync(chatId, cancellationToken) : Google(chatId).GetPermissionAsync(chatId, cancellationToken);

    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).GetFilePermissionAsync(chatId, file, cancellationToken)
            : Voice(chatId) ? voice.GetPhotoPermissionAsync(chatId, file) : MessengerModels.Owns(chatId) ? Messenger(chatId).GetFilePermissionAsync(chatId, file, cancellationToken) : Google(chatId).GetFilePermissionAsync(chatId, file, cancellationToken);

    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken)
            : Voice(chatId) ? Task.FromResult(new AttachmentCaptionSupport(true, null)) : MessengerModels.Owns(chatId) ? Messenger(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken) : Google(chatId).GetCaptionSupportAsync(chatId, file, cancellationToken);

    public Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
        => Insta(chatId) ? InstagramAccount(chatId).SendAsync(chatId, file, cancellationToken, caption)
            : Voice(chatId) ? voice.SendPhotoAsync(chatId, file, caption, cancellationToken) : MessengerModels.Owns(chatId) ? Messenger(chatId).SendAsync(chatId, file, cancellationToken, caption) : Google(chatId).SendAsync(chatId, file, cancellationToken, caption);

    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files)
        => MessengerModels.Owns(chatId) && Messenger(chatId).CanSendBatch(chatId, files);

    public Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
        => MessengerModels.Owns(chatId) ? Messenger(chatId).SendBatchAsync(chatId, files, cancellationToken)
            : throw new NotSupportedException("This service can't send photos as one set.");

    // Google Messages sends each photo as its own message, and each copy carries its receipt's transaction id, so all of
    // them can be drawn when Send is pressed. Still photos only, as chosen together in the composer.
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files)
        => GoogleModels.Owns(chatId) && files.Count is >= 2 and <= NativeBeeper.Presentation.Services.AttachmentBatchRules.ItemLimit &&
            files.All(file => file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !file.MimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));
}
