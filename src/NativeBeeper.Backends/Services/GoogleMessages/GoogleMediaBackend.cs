using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMediaBackend(GoogleMessagesBackend backend, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] IAccessTokenStore tokens,NativeBeeper.Storage.MediaDiskCache disk) : IMediaReader, IAttachmentBackend
{
    private const int Limit = 50 * 1024 * 1024, Chunk = 256 * 1024;
    // What may be received and opened: the largest Google Messages sends, and the helper's own downloadLimit. A video over 50 MB could not be opened once
    // the server, with no copy of its own, had to fetch it (2026-10-12). Sending stays at Limit.
    internal const int DownloadLimit = 100 * 1024 * 1024;
    private readonly SemaphoreSlim transfers = new(1);
    private readonly SemaphoreSlim downloadSlots = new(4);
    private readonly SemaphoreSlim previewDownloadSlots = new(2);
    private readonly Dictionary<string, byte[]> images = [];
    private readonly object imageLock = new();
    internal (int Count, long Bytes) HeldImages { get { lock (imageLock) return (images.Count, images.Values.Sum(v => (long)v.Length)); } }
    private async Task VerifyProfileAsync(string profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (profile != await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens,token)) throw new OperationCanceledException("Media account changed.");
    }
    private async Task SaveCacheAsync(string profile, string id, byte[] bytes, TimeSpan lifetime, CancellationToken token)
    {
        try { await disk.WriteAsync(profile, id, bytes, lifetime, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* Cache writes must not prevent viewing downloaded media. */ }
    }
    public Task<SendPermission> GetPermissionAsync(string chatId,CancellationToken cancellationToken=default) => backend.GetSendPermissionAsync(chatId,cancellationToken);
    public Task<SendPermission> GetFilePermissionAsync(string chatId,AttachmentFile file,CancellationToken cancellationToken=default)
        => file.Length<=0 || file.Length>Limit ? Task.FromResult(new SendPermission(false,"Choose a file under 50 MB. Carrier limits may be lower."))
        : !(file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/") || file.MimeType.StartsWith("audio/") || file.MimeType.StartsWith("application/") || file.MimeType.StartsWith("text/"))
        ? Task.FromResult(new SendPermission(false,"Google Messages can't send this kind of file.")) : GetPermissionAsync(chatId,cancellationToken);
    // Photo and caption in one request (media then text, as upstream does) repeatedly left the phone with the text
    // pending and no photo. Attachments go alone and the text follows as its own tracked message.
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId,AttachmentFile file,CancellationToken cancellationToken=default) => Task.FromResult(new AttachmentCaptionSupport(false,null));
    public bool CanSave(MessageAttachment attachment) => attachment.Id?.StartsWith("google-media:",StringComparison.Ordinal)==true;
    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment,CancellationToken cancellationToken=default,bool inline=false)
    {
        if(attachment.SourceUrl?.StartsWith("google-avatar:",StringComparison.Ordinal)==true)
            return await ReadAvatarAsync(backend.Slot.Ids.Native(attachment.SourceUrl)[14..],cancellationToken);
        var profile = await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        if (profile is null || !CanSave(attachment)) throw new BackendException(BackendFailure.Unavailable,"This attachment isn't available right now.");
        var id = (attachment.Id ?? "") + (attachment.Type == "video" ? ":thumbnail" : "");
        var key = profile + "\n" + id;
        lock (imageLock) if (images.TryGetValue(key, out var hit) && hit.Length > 0) return hit;
        // Disk hits must not queue behind an unrelated phone download or upload.
        // Preview-only photos are never served from disk: reaching the helper lets it ask the phone for the full size.
        var preview = attachment.Availability == "preview";
        // Read again pushes its thirty days out, so a picture still being looked at is not fetched from the phone again.
        // An empty copy is a miss: one was kept for 30 days before DownloadAsync refused them (2026-10-19).
        if (!preview && await disk.ReadAsync(profile, id, cancellationToken, renewFor: TimeSpan.FromDays(30)) is { Length: > 0 } saved)
        { await VerifyProfileAsync(profile,cancellationToken); return saved; }
        var queued = DateTimeOffset.UtcNow;
        var readSlots = attachment.Id?.StartsWith("google-media:link-preview:", StringComparison.Ordinal) == true ? previewDownloadSlots : downloadSlots;
        await readSlots.WaitAsync(cancellationToken);
        try
        {
            lock (imageLock) if(images.TryGetValue(key,out var cached) && cached.Length > 0) return cached;
            var started = DateTimeOffset.UtcNow;
            using var data=new MemoryStream();
            TimeSpan ready;
            try { ready = await DownloadAsync(attachment,data,cancellationToken,attachment.Type=="video"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { RecordDownloadFailure(DateTimeOffset.UtcNow - queued, ex); throw; }
            RecordDownload(started - queued, ready, DateTimeOffset.UtcNow - started);
            var bytes=data.ToArray();
            if (profile != await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken)) throw new OperationCanceledException();
            lock (imageLock)
            {
                while (images.Count > 0 && (images.Count >= 128 || images.Values.Sum(v=>(long)v.Length)+bytes.Length>128L*1024*1024)) images.Remove(images.Keys.First());
                images[key]=bytes;
            }
            if (!preview) await SaveCacheAsync(profile,id,bytes,TimeSpan.FromDays(30),cancellationToken);
            return bytes;
        }
        finally {readSlots.Release();}
    }
    public async Task CopyToAsync(MessageAttachment attachment,Stream destination,CancellationToken cancellationToken=default)
    {
        var profile = await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        if (profile is not null && CanSave(attachment) && await disk.ReadAsync(profile, attachment.Id!, cancellationToken, renewFor: TimeSpan.FromDays(30)) is { Length: > 0 } cached)
        { await VerifyProfileAsync(profile,cancellationToken); await destination.WriteAsync(cached,cancellationToken); return; }
        var readSlots = attachment.Id?.StartsWith("google-media:link-preview:", StringComparison.Ordinal) == true ? previewDownloadSlots : downloadSlots;
        await readSlots.WaitAsync(cancellationToken);
        try
        {
            using var data = new MemoryStream();
            await DownloadAsync(attachment,data,cancellationToken);
            var bytes = data.ToArray();
            if (profile is not null) await VerifyProfileAsync(profile,cancellationToken);
            await destination.WriteAsync(bytes,cancellationToken);
            if (profile is not null && profile == await NativeBeeper.Presentation.Services.NotificationIdentity.ProfileAsync(tokens,cancellationToken))
                await SaveCacheAsync(profile,attachment.Id!,bytes,TimeSpan.FromDays(30),cancellationToken);
        }
        finally {readSlots.Release();}
    }
    // Returns how long the helper took to have the file ready (before the first chunk).
    private Task<TimeSpan> DownloadAsync(MessageAttachment attachment,Stream destination,CancellationToken token,bool thumbnail=false)
        => DownloadAsync(attachment with { Id = backend.Slot.Ids.Native(attachment.Id!), SourceUrl = backend.Slot.Ids.NativeMediaValue(attachment.SourceUrl) },destination,(parameters,t)=>backend.MediaCallAsync("ui.mediaRead",parameters,t),t=>backend.RelearnMediaAsync(backend.Slot.Ids.Native(attachment.Id!)[13..],t),token,thumbnail);
    internal static async Task<TimeSpan> DownloadAsync(MessageAttachment attachment,Stream destination,Func<object,CancellationToken,Task<System.Text.Json.JsonElement>> read,
        Func<CancellationToken,Task<bool>> relearn,CancellationToken token,bool thumbnail=false)
    {
        if(!(attachment.Id?.StartsWith("google-media:",StringComparison.Ordinal)==true) || attachment.Size>DownloadLimit) throw new BackendException(BackendFailure.Unsupported,"This attachment is unavailable or exceeds the 100 MB limit.");
        var offset=0;
        var began=DateTimeOffset.UtcNow;
        TimeSpan? ready=null;
        var deadline=DateTimeOffset.UtcNow.AddSeconds(135);
        var relearned=false;
        do
        {
            if(DateTimeOffset.UtcNow>=deadline) throw new BackendException(BackendFailure.Unavailable,"This attachment is taking too long to download. Try again shortly.");
            System.Text.Json.JsonElement response;
            try { response=await read(new {id=attachment.Id![13..],offset,thumbnail},token); }
            // The helper holds an attachment's reference only from handing its message out in this run, so after a restart
            // or its map's clear-out an old photo or video answered media_unavailable (2026-10-12, a video sent before the
            // Messages Server existed). It is re-learned once from the saved message it came from, then read once more;
            // a second miss, or one with nothing saved, is the answer as before.
            catch(InvalidOperationException unavailable) when(unavailable.Message=="media_unavailable" && offset==0 && !relearned)
            {
                relearned=true;
                if(!await relearn(token)) throw;
                continue;
            }
            if(response.TryGetProperty("pending",out var pending) && pending.GetBoolean())
            { await Task.Delay(500,token); continue; }
            ready ??= DateTimeOffset.UtcNow-began;
            var total=response.GetProperty("total").GetInt32();
            // A photo sent from this PC read back as zero bytes a minute after sending, was cached as the photo, and its tile and
            // viewer failed on it from then on (2026-10-19). Nothing is cached from this, so the next read asks Google again.
            if(total==0) throw new BackendException(BackendFailure.Unavailable,"This attachment came back empty. Try again shortly.");
            var next=response.GetProperty("next").GetInt32();
            var bytes=response.GetProperty("data").GetBytesFromBase64();
            if(total<0 || total>DownloadLimit || bytes.Length>Chunk || next!=offset+bytes.Length || next>total || (bytes.Length==0 && offset!=total)) throw new IOException("Invalid media chunk.");
            await destination.WriteAsync(bytes,token);
            offset=next;
            if(offset==total) return ready ?? TimeSpan.Zero;
        } while(true);
    }
    public async Task<SendReceipt> SendAsync(string chatId,AttachmentFile file,CancellationToken cancellationToken=default,string? caption=null)
    {
        await backend.ThrowIfRenumberedAsync();
        var permission=await GetFilePermissionAsync(chatId,file,cancellationToken);
        if(!permission.Allowed || caption?.Length>65536) throw new SendException(false,permission.Allowed?"Caption is too long.":permission.Reason);
        await transfers.WaitAsync(cancellationToken);
        var submitted=false;
        var began=System.Diagnostics.Stopwatch.GetTimestamp(); TimeSpan? staged=null; var outcome="failed before submitting";
        try
        {
            await using var stream=await file.OpenRead(cancellationToken);
            var buffer=new byte[Chunk]; var offset=0;
            int count;
            while((count=await stream.ReadAsync(buffer,cancellationToken))>0)
            {
                if(offset+count>Limit || offset+count>file.Length) throw new IOException("File changed or too large.");
                var ack=await backend.MediaCallAsync("ui.mediaStage",new {offset,data=Convert.ToBase64String(buffer,0,count)},cancellationToken);
                offset+=count;
                if(ack.GetProperty("next").GetInt32()!=offset) throw new IOException("Upload offset mismatch.");
            }
            if(offset!=file.Length) throw new IOException("File changed.");
            cancellationToken.ThrowIfCancellationRequested();
            staged=System.Diagnostics.Stopwatch.GetElapsedTime(began);
            backend.ThrowIfGoneFromList(chatId);
            submitted=true; outcome="outcome unknown";
            var result=await backend.MediaCallAsync("ui.sendMedia",new {chatId=backend.Models.Native(chatId),name=Path.GetFileName(file.Name),mime=file.MimeType,text=caption ?? ""},cancellationToken);
            outcome=result.GetProperty("outcome").GetString() ?? outcome;
            if(outcome=="rejected") throw new SendException(false,backend.NotDefaultSmsRefusal(result) ?? "Google Messages couldn't send this attachment. Carrier size or format limits may apply.");
            if(outcome=="unknown") throw new SendException(true,"Attachment outcome is unknown. Check the conversation before sending again.");
            var txn=result.GetProperty("pendingId").GetString();
            if(string.IsNullOrWhiteSpace(txn)) throw new IOException("Missing attachment transaction.");
            return new(chatId,backend.Models.PendingPrefix+txn);
        }
        catch(SendException) {throw;}
        catch(Exception) {throw new SendException(submitted,submitted?"Attachment outcome is unknown. Check the conversation before sending again.":"Couldn't prepare this file. It was not sent.");}
        finally {transfers.Release(); RecordUpload(file.MimeType,file.Length,staged,System.Diagnostics.Stopwatch.GetElapsedTime(began),outcome);}
    }
}
