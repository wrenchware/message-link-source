using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Saved Messages keep the original photos (2026-10-04). Saving a message with photos keeps a full-size copy of each on this
/// PC (<see cref="ISavedPhotoStore"/>), so a saved photo still opens after the media cache has let it go, after the service
/// has, and while the service is not connected. The conversation and the viewer ask for it before the service
/// (<see cref="ReadAsync"/>).
/// </summary>
/// <remarks>
/// <para>Each photo is read once, through the media reader exactly as opening it would: the media cache answers if it
/// holds the photo, and otherwise the provider is asked once. No retry; one that fails is left to its conversation, as
/// before. Read one at a time, in the background, so saving is never slower for it.</para>
/// <para>One copy however many saved messages show the photo, and it goes when the last of them is unsaved. The budget
/// (<see cref="ISavedPhotoStore.Capacity"/>, 1 GB) takes out the photos saved longest ago first, with a line in the log.
/// Settings > Storage clears the copies without unsaving anything; an account signing out, or Google Messages' data reset,
/// takes that account's copies.</para>
/// <para>Photos only: the picture of a link card, a sticker, a GIF and a video's still are not kept, and nor is a Google
/// photo the phone has not downloaded in full yet (its "original" would be a thumbnail).</para>
/// </remarks>
public sealed class SavedPhotos(ISavedPhotoStore store, Func<IMediaReader?> reader, Action<string>? log = null)
{
    // As long as opening a photo may take: a Google photo the phone is still fetching is waited for up to 135 s.
    private static readonly TimeSpan FetchLimit = TimeSpan.FromMinutes(2.5);
    private sealed record Work(string Reference, string Owner, IReadOnlyList<MessageAttachment> Photos)
    {
        public readonly CancellationTokenSource Cancel = new();
    }
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    // Each saved message's work, by reference, while it is queued or running. Unsaving takes it out and cancels it, and a
    // write lands only while its work is still the one here, checked inside the same turn (writes) that unsaving takes.
    private readonly Dictionary<string, Work> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writes = new(1, 1);
    private int started, outstanding;
    private TaskCompletionSource idle = Completed();

    /// <summary>Whether a saved message keeps this attachment's original: a photo, identified, and whole.</summary>
    public static bool Keeps(MessageAttachment a) =>
        PhotoPresentation.IsPhoto(a) && a.Link is null && !a.Play && !a.IsGif && !a.IsSticker && a.Availability is null &&
        (!string.IsNullOrEmpty(a.Id) || !string.IsNullOrEmpty(a.SourceUrl));

    /// <summary>The photo's key: its id, or where there is none its source, one way. The same photo in two saved messages is
    /// one key, and the conversation's own copy of the message finds it again.</summary>
    public static string PhotoKey(MessageAttachment a) =>
        Digest("saved-photo\n" + (string.IsNullOrEmpty(a.Id) ? "source\n" + a.SourceUrl : "id\n" + a.Id));

    /// <summary>A saved message's key here, from what names it in Saved Messages: its account scope, conversation and id.</summary>
    public static string Reference(string scope, string chatId, string messageId) =>
        Digest("saved-message\n" + scope + "\n" + chatId + "\n" + messageId);

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Queues the saved message's photos to be kept; how many it has. Nothing for a message without any.</summary>
    public int Keep(SavedMessage saved) => Keep(saved, Reference(saved.Scope, saved.Chat.Id, saved.Message.Id));

    /// <summary>
    /// As <see cref="Keep(SavedMessage)"/> under a reference of the caller's: a server keeps the originals for the saved messages of every device and names each
    /// by the shared row's key (HostSavedPhotos), which it holds when the row is deleted and nothing else is left to say which message it was.
    /// </summary>
    public int Keep(SavedMessage saved, string reference)
    {
        var photos = saved.Message.Attachments.Where(Keeps).DistinctBy(PhotoKey).ToArray();
        if (photos.Length == 0) return 0;
        var work = new Work(reference, saved.Chat.Id, photos);
        lock (pending)
        {
            if (pending.Remove(work.Reference, out var earlier)) earlier.Cancel.Cancel();
            pending[work.Reference] = work;
            if (outstanding++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        queue.Writer.TryWrite(work);
        if (Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(RunAsync);
        return photos.Length;
    }

    /// <summary>Completes when everything queued has been kept or let go.</summary>
    public Task WhenIdleAsync() { lock (pending) return idle.Task; }

    /// <summary>The kept original of this photo, or null when none is kept.</summary>
    public async Task<byte[]?> ReadAsync(MessageAttachment attachment, CancellationToken token = default) =>
        Keeps(attachment) ? await store.ReadAsync(PhotoKey(attachment), token) : null;

    /// <summary>Whether this photo's original is kept, without reading it.</summary>
    public Task<bool> HoldsAsync(MessageAttachment attachment, CancellationToken token = default) =>
        Keeps(attachment) ? store.HoldsAsync(PhotoKey(attachment), token) : Task.FromResult(false);

    /// <summary>A message was unsaved: its copies go, except a photo another saved message still shows.</summary>
    public Task<SavedPhotoChange> ReleaseAsync(string scope, string chatId, string messageId) => ReleaseAsync(Reference(scope, chatId, messageId));

    /// <summary>As above for a message kept under a reference of the caller's (<see cref="Keep(SavedMessage, string)"/>).</summary>
    public async Task<SavedPhotoChange> ReleaseAsync(string reference)
    {
        lock (pending) if (pending.Remove(reference, out var work)) work.Cancel.Cancel();
        await writes.WaitAsync();
        SavedPhotoChange change;
        try { change = await store.ReleaseAsync(reference); }
        finally { writes.Release(); }
        if (change.Photos > 0) log?.Invoke($"saved photos released photos={change.Photos} kb={change.Bytes / 1024}");
        return change;
    }

    /// <summary>
    /// An account signed out, or its data was reset: every copy kept for a saved message from a conversation whose id starts
    /// with <paramref name="chatPrefix"/> goes, and any still being kept stops. The saved messages stay, as they always have.
    /// </summary>
    public async Task<SavedPhotoChange> ForgetAsync(string chatPrefix, string reason)
    {
        bool Matches(string chat) => chat.StartsWith(chatPrefix, StringComparison.Ordinal) && (chatPrefix is not ("google-direct:" or "messenger:" or "instagram:") || NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(chat) is null);
        lock (pending)
            foreach (var (reference, work) in pending.ToArray())
                if (Matches(work.Owner)) { pending.Remove(reference); work.Cancel.Cancel(); }
        await writes.WaitAsync();
        SavedPhotoChange change;
        try { change = await store.ForgetAsync(Matches); }
        finally { writes.Release(); }
        log?.Invoke($"saved photos forgotten reason={reason} photos={change.Photos} kb={change.Bytes / 1024}");
        return change;
    }

    /// <summary>Settings > Storage: every kept copy goes and nothing is unsaved. A photo still being kept stops.</summary>
    public async Task<SavedPhotoChange> ClearAsync()
    {
        lock (pending)
        {
            foreach (var work in pending.Values) work.Cancel.Cancel();
            pending.Clear();
        }
        await writes.WaitAsync();
        try { return await store.ClearAsync(); }
        finally { writes.Release(); }
    }

    public Task<(int Photos, long Bytes)> UsageAsync(CancellationToken token = default) => store.UsageAsync(token);

    private async Task RunAsync()
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            try { await KeepAsync(work); }
            catch (Exception failure) { log?.Invoke($"saved photos failed error={failure.GetType().Name}"); }
            finally
            {
                lock (pending)
                {
                    if (pending.GetValueOrDefault(work.Reference) == work) pending.Remove(work.Reference);
                    if (--outstanding == 0) idle.TrySetResult();
                }
                work.Cancel.Dispose();
            }
        }
    }

    private bool Current(Work work)
    {
        lock (pending) return !work.Cancel.IsCancellationRequested && pending.GetValueOrDefault(work.Reference) == work;
    }

    private async Task KeepAsync(Work work)
    {
        int kept = 0, held = 0, failed = 0;
        long bytes = 0;
        var evicted = SavedPhotoChange.None;
        foreach (var photo in work.Photos)
        {
            if (!Current(work)) break;
            var key = PhotoKey(photo);
            // Already kept for another saved message: only this one's reference is added.
            await writes.WaitAsync();
            try
            {
                if (!Current(work)) break;
                if (await store.ReferAsync(work.Reference, key, work.Owner)) { held++; continue; }
            }
            finally { writes.Release(); }
            if (reader() is not { } media) { failed++; continue; }
            byte[] data;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(work.Cancel.Token))
            {
                limit.CancelAfter(FetchLimit);
                try { data = await media.ReadImageAsync(photo, limit.Token, inline: false); }
                catch (OperationCanceledException) when (work.Cancel.IsCancellationRequested) { break; }
                // Read once: a photo that could not be had is left to its conversation, which asks again when it is drawn.
                catch (Exception) { failed++; continue; }
            }
            await writes.WaitAsync();
            try
            {
                if (!Current(work)) break;
                var result = await store.KeepAsync(work.Reference, key, work.Owner, data);
                if (result.Kept) { kept++; bytes += data.Length; } else failed++;
                evicted = new(evicted.References + result.Evicted.References, evicted.Photos + result.Evicted.Photos, evicted.Bytes + result.Evicted.Bytes);
            }
            finally { writes.Release(); }
        }
        var outcome = Current(work) ? "done" : "stopped";
        log?.Invoke($"saved photos kept outcome={outcome} photos={kept} held={held} failed={failed} kb={bytes / 1024}");
        if (evicted.Photos > 0) log?.Invoke($"saved photos evicted reason=budget photos={evicted.Photos} kb={evicted.Bytes / 1024}");
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}
