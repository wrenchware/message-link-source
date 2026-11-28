using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Puts the saved copies of recent Instagram and Google Voice conversations into the search catalogue, so search finds
/// their messages after a restart before each conversation is opened again. Those services have no history of their own
/// on this PC to search (Google's archive and Messenger's helper store answer for theirs), and the catalogue held only
/// what this session had read: everything in them was unfindable until opened (2026-10-02).
/// </summary>
/// <remarks>
/// Only conversations the list shows, newest first, a bounded number per service connection, and only copies already
/// saved here: nothing is asked of any service. Reading a copy decrypts it, and the store has one gate that an opening
/// conversation's own saved copy waits on, so nothing is read in the launch's first seconds, one copy at a time, a moment
/// apart. A reconnect clears its service's rows from the catalogue, and its copies go in again after it.
/// </remarks>
public sealed class SavedSearchSeeding(LocalSearchCatalog catalog, Func<string, Task<HistorySnapshot?>> readSaved, Action<string>? log = null,
    TimeSpan? quiet = null, TimeSpan? gap = null)
{
    private const int ChatsPerOffer = 20;
    private readonly TimeSpan quietAfterLaunch = quiet ?? TimeSpan.FromSeconds(15);
    private readonly TimeSpan readGap = gap ?? TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly Queue<(string ChatId, long Token)> waiting = new();
    private Task running = Task.CompletedTask;
    // Set and cleared under the gate, so an offer made as a run finds its queue empty still starts the next one.
    private bool active;

    public static bool Seeds(ChatSummary chat) => !chat.IsSpam && !MergedConversations.IsMerged(chat.Id) &&
        (chat.Id.StartsWith("instagram:", StringComparison.Ordinal) || chat.Id.StartsWith("gvoice:", StringComparison.Ordinal));

    /// <summary>Conversations the list has just shown (LocalSearchCatalog.ChatsObserved); any thread.</summary>
    public void Offer(IReadOnlyList<ChatSummary> chats)
    {
        var taken = catalog.TakeUnseeded(chats.Where(Seeds).OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue)
            .Select(chat => chat.Id), ChatsPerOffer);
        if (taken.Count == 0) return;
        lock (gate)
        {
            foreach (var item in taken) waiting.Enqueue(item);
            if (!active) { active = true; running = Task.Run(RunAsync); }
        }
    }

    /// <summary>Done when everything offered so far has been put in; for the offline checks.</summary>
    public Task Idle { get { lock (gate) return running; } }

    private async Task RunAsync()
    {
        var wait = quietAfterLaunch - ProgressIndicator.SinceLaunch;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int read = 0, none = 0, stale = 0, rows = 0;
        while (true)
        {
            (string ChatId, long Token) next;
            bool done;
            lock (gate)
            {
                done = !waiting.TryDequeue(out next);
                if (done) active = false;
            }
            if (done)
            {
                // Counts and a time only: how many saved copies went in, and how many rows search can now find.
                if (read + none + stale > 0)
                    log?.Invoke($"search seeded read={read} none={none} stale={stale} rows={rows} catalogued={catalog.MessageCount} ms={clock.ElapsedMilliseconds}");
                return;
            }
            // Cleared by a reconnect, a deletion or another profile since it was offered: nothing to read it for.
            if (!catalog.StillSeeding(next.ChatId, next.Token)) { stale++; continue; }
            if (read + none > 0) await Task.Delay(readGap);
            HistorySnapshot? saved;
            try { saved = await readSaved(next.ChatId); }
            catch (Exception) { saved = null; }
            if (saved is null) { none++; continue; }
            read++;
            rows += catalog.Seed(next.ChatId, next.Token, saved.Messages);
        }
    }
}
