namespace NativeBeeper.Host;

/// <summary>A background read of a conversation, most urgent first. Open is the only one someone asked for.</summary>
internal enum HistoryWork { Open = 0, Arrival = 1, Check = 2, Refresh = 3, CatchUp = 4, Initial = 5, Mirror = 6 }

/// <summary>Why the copy changed, for its log lines.</summary>
internal enum HistoryReason { Open, Event, Arrival, Check, Refresh, CatchUp, Initial, Older, Mutation, Mirror }

/// <summary>
/// The background's work, each conversation at the most urgent of what it was asked for, and work put off until a time. A conversation holds at most
/// one read someone asked for (Open, read whatever the budget) and one of the server's own (which waits for the budget), so the queue is bounded by twice
/// the number of conversations.
/// </summary>
internal sealed class HistoryWorkQueue
{
    // Order: when it was asked for, so work of one urgency goes first come first served (the walk asks for the most recently active first).
    public sealed record Item(string ChatId, HistoryWork Work, int Tries, long NotBefore, long Order = 0);
    private readonly object gate = new();
    private readonly Dictionary<(string ChatId, bool Asked), Item> items = new();
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool completed;
    private long order;

    public int Count { get { lock (gate) return items.Count; } }

    public void Add(string chatId, HistoryWork work) => Put(new Item(chatId, work, 0, 0));
    public void Later(string chatId, HistoryWork work, TimeSpan after, int tries = 0) => Put(new Item(chatId, work, tries, Environment.TickCount64 + (long)after.TotalMilliseconds));

    private void Put(Item item)
    {
        TaskCompletionSource wake;
        lock (gate)
        {
            if (completed) return;
            // An asked read takes the place of the server's own read due no sooner, since it reads the conversation for both. The server's own read due
            // sooner stays apart: merged, it took the asked read's class and skipped the budget (review, 2026-11-18), or the asked read's later time.
            var asked = item.Work == HistoryWork.Open;
            if (asked && items.TryGetValue((item.ChatId, false), out var own) && own.NotBefore >= item.NotBefore) items.Remove((item.ChatId, false));
            if (!asked && items.TryGetValue((item.ChatId, true), out var open) && open.NotBefore <= item.NotBefore) return;
            var key = (item.ChatId, asked);
            if (items.TryGetValue(key, out var was) && was.Work <= item.Work && was.NotBefore <= item.NotBefore) return;
            // Merged, the work is the more urgent and the time the sooner, with that side's tries: a backed-off retry of more urgent work no longer puts
            // off work that was due now (it reads the conversation once either way), nor gives it the retry's count.
            var sooner = was is null || item.NotBefore < was.NotBefore ? item : was.NotBefore < item.NotBefore ? was : item with { Tries = Math.Max(was.Tries, item.Tries) };
            items[key] = was is null ? item with { Order = ++order }
                : sooner with { Work = (HistoryWork)Math.Min((int)was.Work, (int)item.Work), Order = was.Order };
            wake = signal;
        }
        wake.TrySetResult();
    }

    /// <summary>Takes away the conversation's waiting open when it is a retry (tries past 0). An open asked for anew has none: merged into a retry, it
    /// is due sooner and takes its own count.</summary>
    public void RetireRetry(string chatId)
    {
        lock (gate)
            if (items.TryGetValue((chatId, true), out var open) && open.Tries > 0) items.Remove((chatId, true));
    }

    public void Complete()
    {
        lock (gate) completed = true;
        signal.TrySetResult();
    }

    public async Task<Item?> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (completed) return null;
                var now = Environment.TickCount64;
                var due = items.Values.Where(item => item.NotBefore <= now).OrderBy(item => item.Work).ThenBy(item => item.Order).FirstOrDefault();
                if (due is not null)
                {
                    items.Remove((due.ChatId, due.Work == HistoryWork.Open));
                    // The server's own read of the conversation that is due too is done by this asked one (an asked one due sorts first).
                    if (due.Work == HistoryWork.Open && items.TryGetValue((due.ChatId, false), out var own) && own.NotBefore <= now) items.Remove((due.ChatId, false));
                    return due;
                }
                if (signal.Task.IsCompleted) signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var soonest = items.Count == 0 ? (long?)null : items.Values.Min(item => item.NotBefore) - now;
                wait = soonest is { } delay ? Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, delay)), cancellationToken)) : signal.Task;
            }
            try { await wait.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
    }
}
