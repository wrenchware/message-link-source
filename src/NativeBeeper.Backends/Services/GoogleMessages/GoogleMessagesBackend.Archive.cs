using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    private readonly GoogleIndexActivity foregroundActivity = new();
    // Background indexing yields to foreground helper work and active composer edits for a quiet period.
    private void NoteForeground() => foregroundActivity.NoteForeground();
    internal void NoteComposerActivity(string? chatId) => foregroundActivity.NoteComposerActivity(chatId);
    internal bool RecentlyActive(TimeSpan quiet) => foregroundActivity.RecentlyActive(quiet);
    private long lastRealtimeMessage;
    // Incoming messages mean a live conversation: keep the phone free for it for a while.
    private void NoteRealtimeMessage() => Interlocked.Exchange(ref lastRealtimeMessage, Environment.TickCount64);
    private bool RecentRealtimeMessage(TimeSpan window) =>
        Interlocked.Read(ref lastRealtimeMessage) is var last && last != 0 && Environment.TickCount64 - last < window.TotalMilliseconds;
    private void ArchiveChats(IEnumerable<GoogleChat> list) { if (archive.Profile is { } profile) archive.RememberChats(profile, list); }
    private void ArchiveMessages(IEnumerable<GoogleMessage> list) { if (archive.Profile is { } profile) archive.RememberMessages(profile, list); }
    private async Task<string?> LoadArchiveAsync(CancellationToken token)
    {
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        var loaded = profile is not null && await archive.LoadAsync(profile, YieldArchiveWarmupAsync);
        return loaded ? profile : null;
    }

    // Set once someone is searching (the search dialog opened, or a search arrived before the archive had loaded): from
    // then on the warm-up reads straight through instead of pacing itself behind other activity. Paced, it took about five
    // minutes after a launch to have 45,000 messages searchable (2026-10-02), and searches meanwhile quietly missed
    // anything not read yet.
    private volatile bool archiveWanted;
    private int archiveHurried;
    internal void WarmArchive()
    {
        archiveWanted = true;
        if (!archive.IsLoaded && connected) _ = Task.Run(async () => { try { await LoadArchiveAsync(CancellationToken.None); } catch { } });
    }

    // A message search waits for the archive (within ArchiveSearchWait) rather than answering from part of it.
    private static readonly TimeSpan ArchiveSearchWait = TimeSpan.FromSeconds(10);
    private async Task WaitForArchiveAsync(CancellationToken cancellationToken)
    {
        if (archive.IsLoaded) return;
        archiveWanted = true;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        try { await LoadArchiveAsync(cancellationToken).WaitAsync(ArchiveSearchWait, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
        GoogleMessagesLog.Write($"search archive waited_ms={waited.ElapsedMilliseconds} loaded={archive.IsLoaded} messages={archive.MessageCount}");
    }

    private async Task YieldArchiveWarmupAsync()
    {
        if (archiveWanted)
        {
            if (stopping) throw new OperationCanceledException();
            if (Interlocked.Exchange(ref archiveHurried, 1) == 0) GoogleMessagesLog.Write($"archive warmup hurried reason=search messages={archive.MessageCount}");
            return;
        }
        // Saved-history indexing is background work too, not just the later phone crawl. Pace its allocations,
        // and recheck activity between bounded batches instead of rebuilding the whole archive in one burst.
        await Task.Delay(25);
        var paused = false;
        while (RecentlyActive(IndexQuietPeriod))
        {
            if (stopping) throw new OperationCanceledException();
            if (!paused) { GoogleMessagesLog.Write("archive warmup paused reason=activity"); paused = true; }
            await Task.Delay(250);
        }
        if (stopping) throw new OperationCanceledException();
        if (paused) GoogleMessagesLog.Write("archive warmup resumed reason=quiet");
    }
}
