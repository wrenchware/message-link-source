using System.Threading.Channels;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

/// <summary>
/// The originals of the saved messages' photos, kept by the server for every device (SWITCHBOARD.md stage 6 slice 8, protocol 8). A device that saves a message
/// writes its row into the shared state; the server, which is always there, keeps the full-size copy of each photo in the row through its own
/// <see cref="SavedPhotos"/> (the same one the in-process app keeps on its PC: read once through the media reader as opening it would read it, kept once however
/// many saved messages show it, within the same 1 GB), and lets it go when the row is deleted. A device then opens a saved photo as it opens any photo, by
/// <c>media.read</c>, and the server's <c>MediaRouter</c> answers from this copy before it asks the service. Nothing is sent to any device ahead of that, so a
/// device fetches an original only when one is shown or opened; and none keeps a copy of its own (a client connected at 8 skips its own, SavedMessages).
/// </summary>
/// <remarks>
/// <para><b>Why not rows or a new method.</b> A shared row is read in full by every device that syncs, and a value is capped at 1 MiB (<see cref="SharedLimits.ValueBytes"/>)
/// while a photo's original is several MiB; <c>state.get</c> would need chunked rows that every device pulled at once, which is the opposite of lazy. <c>media.read</c>
/// already carries a picture by reference in chunks and already prefers this copy, so no method is added.</para>
/// <para><b>One try.</b> As on a PC, a photo is read once when its message is first seen in a run; one that could not be had is left to its conversation. The row is
/// seen once per run: a reminder's mark rewrites it, and the photos are not read again for that. A row that arrives while the server is down is kept when
/// the row is next written. The copies are named by the row's key (<see cref="SavedPhotos.Keep(SavedMessage, string)"/>), which is all that is left to name a message once
/// its row is deleted. An unsave the server missed (it stopped between the commit and the release) leaves a copy that the 1 GB budget takes out oldest first.</para>
/// <para>Logs carry counts and fixed words only.</para>
/// </remarks>
internal sealed class HostSavedPhotos : IDisposable
{
    // As many saved messages as one person keeps over a run; past it the memory starts again, which only means one more look at a row that arrives again.
    private const int SeenLimit = 50_000;
    private readonly SharedStateStore store;
    private readonly SavedPhotos photos;
    private readonly Action<string>? log;
    private readonly Channel<Look> keys = Channel.CreateUnbounded<Look>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    // Worker only: the saved rows whose photos were queued this run.
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private bool disposed;

    public HostSavedPhotos(SharedStateStore store, SavedPhotos photos, Action<string>? log = null)
    {
        this.store = store; this.photos = photos; this.log = log;
        store.Committed += OnCommitted;
        worker = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>Completes when every commit seen so far has been looked at and its photos queued; the photos themselves are SavedPhotos' (<see cref="SavedPhotos.WhenIdleAsync"/>).</summary>
    public Task IdleAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A marker comes out of the channel behind everything written before it.
        if (!keys.Writer.TryWrite(new Look(null, done))) done.TrySetResult();
        return done.Task;
    }

    private sealed record Look(string? Key, TaskCompletionSource? Done, string? ForgetPrefix = null, string? Reason = null);

    /// <summary>
    /// An account was signed out here, or Google Messages' local data was reset (<c>reset</c>): the originals kept for saved messages from its conversations go, and
    /// the saved messages stay, as on a PC (<c>App.AccountSignedOut</c>). <paramref name="account"/> is <c>messenger</c>, <c>instagram</c>, <c>voice</c> (with the
    /// account's key) or <c>google-reset</c>. The copies are named by their row's key here and by the message on a PC, but both record the conversation that owns
    /// them (<c>SavedPhotos.Keep</c>), and an account's conversations share an id prefix, so the same forget by prefix finds them. Never waits and never throws.
    /// </summary>
    public void Ended(string account, string? key)
    {
        if (account == "account-slot" && NativeBeeper.Core.Messaging.AccountNamespace.ValidKey(key))
        {
            foreach (var service in new[] { "google-direct:", "messenger:", "instagram:" }) keys.Writer.TryWrite(new Look(null, null, new NativeBeeper.Core.Messaging.AccountNamespace(key).Qualify(service), "signed_out"));
            return;
        }
        var (prefix, reason) = account switch
        {
            "messenger" => ("messenger:", "signed_out"),
            "instagram" => ("instagram:", "signed_out"),
            "voice" when !string.IsNullOrEmpty(key) => (NativeBeeper.App.Services.GoogleVoice.GoogleVoiceModels.Account(key) + ":", "signed_out"),
            "google-reset" => ("google-direct:", "reset"),
            _ => ((string?)null, ""),
        };
        if (prefix is not null) keys.Writer.TryWrite(new Look(null, null, prefix, reason));
    }

    // Under the store's gate: only queued, never read here.
    private void OnCommitted(object? sender, SharedChange change)
    {
        foreach (var row in change.Rows)
            if (row.Key.StartsWith(SharedCategories.Saved + ":", StringComparison.Ordinal)) keys.Writer.TryWrite(new Look(row.Key, null));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var look in keys.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (look.ForgetPrefix is { } prefix)
                {
                    try
                    {
                        var change = await photos.ForgetAsync(prefix, look.Reason ?? "signed_out").ConfigureAwait(false);
                        // Their rows are looked at again if they are written again, which is when their photos are read from the service again.
                        seen.Clear();
                        log?.Invoke($"server saved photos forgotten reason={look.Reason} photos={change.Photos}");
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
                    {
                        log?.Invoke($"server saved photos forget failed error={failure.GetType().Name}");
                    }
                    continue;
                }
                if (look.Key is not { } key) { look.Done?.TrySetResult(); continue; }
                try { await LookAsync(key).ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    log?.Invoke($"server saved photos failed error={failure.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) { /* the host is closing */ }
    }

    // What the row holds now, whatever the commit that named it said: a delete lets the copies go, a saved message with photos queues them.
    private async Task LookAsync(string key)
    {
        var bytes = await store.ReadLiveAsync(key, stop.Token).ConfigureAwait(false);
        if (bytes is null)
        {
            seen.Remove(key);
            var change = await photos.ReleaseAsync(key).ConfigureAwait(false);
            if (change.References > 0) log?.Invoke($"server saved photos released photos={change.Photos}");
            return;
        }
        if (seen.Count >= SeenLimit) seen.Clear();
        if (!seen.Add(key)) return;
        var saved = SavedMessages.FromRow(key, bytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        if (saved is null) return;
        var queued = photos.Keep(saved, key);
        if (queued > 0) log?.Invoke($"server saved photos queued photos={queued}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        store.Committed -= OnCommitted;
        stop.Cancel();
        keys.Writer.TryComplete();
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* closing */ }
    }
}
