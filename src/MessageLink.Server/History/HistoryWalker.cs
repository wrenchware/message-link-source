using System.Diagnostics;
using MessageLink.Core.Messaging;
using MessageLink.App.Services;
using MessageLink.Core.Services;
using static MessageLink.Server.HistoryParts;

namespace MessageLink.Server;

/// <summary>
/// The whole history's background: the worker that takes the copy's queue one conversation at a time, paced per service, and the walk that reads the list
/// now and then. Reads nobody asked for wait while the service's budget says so (<see cref="IHistoryFacts.TryTakeUnaskedRead"/>), page by page.
/// </summary>
internal sealed class HistoryWalker(HistoryCopy copy, HistoryRows rows, HistoryMirror mirror, IChatReader chats, IHistoryFacts facts, HistoryTimings timings,
    Action<string> log, CancellationToken stop)
{
    private HistoryWorkQueue Queue => copy.Queue;

    /// <summary>Takes the queue until the history stops.</summary>
    public async Task WorkAsync()
    {
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await Queue.TakeAsync(stop).ConfigureAwait(false) is { } item)
        {
            try
            {
                var service = Service(item.ChatId);
                if (next.TryGetValue(service, out var at) && at > Environment.TickCount64) await Task.Delay(TimeSpan.FromMilliseconds(at - Environment.TickCount64), stop).ConfigureAwait(false);
                var chat = copy.Chat(item.ChatId);
                if (chat.Forgotten) { mirror.Read(item.ChatId, null); continue; }
                if (item.Work == HistoryWork.Check)
                {
                    ChatSummary summary;
                    // A row the router cannot read (four Messenger conversations failed this five times each, every walk, 2026-10-29) is left to the walk,
                    // which reads the conversation from the row it lists.
                    try { summary = await chats.GetChatAsync(item.ChatId, stop).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        log($"history check unreadable chat={Tag(item.ChatId)} error={failure.GetType().Name}");
                        continue;
                    }
                    // Only a conversation that moved past what is held is read again (a read or a mute moves nothing held), and only once for each move: a
                    // reaction moves the list past the newest message for good, and would otherwise be read again at every walk.
                    if (summary.LastActivity is not { } last || !chat.MovedPast(last)) continue;
                }
                // Opening is a read someone asked for; everything else is the server's own, and waits while the service's budget says so, page by page. A
                // mirror check's read the budget does not allow is counted as not read rather than put off.
                var asked = item.Work == HistoryWork.Open;
                if (!asked && !facts.TryTakeUnaskedRead(item.ChatId))
                {
                    if (item.Work == HistoryWork.Mirror) mirror.Read(item.ChatId, null);
                    else Queue.Later(item.ChatId, item.Work, TimeSpan.FromMinutes(10), item.Tries);
                    continue;
                }
                var (_, taken) = await copy.RefreshAsync(chat, item.Work switch
                {
                    HistoryWork.Open => HistoryReason.Open, HistoryWork.Arrival => HistoryReason.Arrival, HistoryWork.Check => HistoryReason.Check,
                    HistoryWork.CatchUp => HistoryReason.CatchUp, HistoryWork.Initial => HistoryReason.Initial, HistoryWork.Mirror => HistoryReason.Mirror,
                    _ => HistoryReason.Refresh,
                }, stop, asked ? null : () => facts.TryTakeUnaskedRead(item.ChatId)).ConfigureAwait(false);
                next[service] = Environment.TickCount64 + (long)timings.Pace.TotalMilliseconds;
                // Whatever the read was asked for, it compared the service's newest page with the copy: a mirror check waiting on this conversation counts it.
                mirror.Read(item.ChatId, taken ? chat.LastDifference : null);
                if (!taken && item.Work != HistoryWork.Mirror && item.Tries < 5) Queue.Later(item.ChatId, item.Work, timings.RetryAfter * (1 << item.Tries), item.Tries + 1);
                // The conversation was read: a failed open's retry still waiting has what it was for, and read as an open it would pass the budget
                // (review, 2026-11-18). An open asked for again since is not a retry, and stays.
                if (taken) Queue.RetireRetry(item.ChatId);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure)
            {
                log($"history work failed chat={Tag(item.ChatId)} work={item.Work} error={failure.GetType().Name}");
                if (item.Work != HistoryWork.Mirror && item.Tries < 5) Queue.Later(item.ChatId, item.Work, timings.RetryAfter * (1 << item.Tries), item.Tries + 1);
            }
            // However the work ended (a check that found no move, a read put off, merged into other work), a mirror check waiting on it is not left to wait
            // out its 20 minutes; one already counted is not counted again (review, 2026-10-30).
            finally { mirror.Read(item.ChatId, null); }
        }
    }

    /// <summary>
    /// Walks the list now and then: every row is kept for the feed, a conversation not held yet is filled (the most recently active first), one whose list
    /// moved past what is held is read again, and the most recently active of each service are compared with it (the mirror check).
    /// </summary>
    public async Task WalkAsync()
    {
        try { await Task.Delay(timings.WalkDelay, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var clock = Stopwatch.GetTimestamp();
            int listedRows = 0, filling = 0, checking = 0;
            try
            {
                var list = await rows.ReadDueAsync(stop).ConfigureAwait(false) ?? [];
                foreach (var row in list)
                {
                    // Spam is read only when someone opens it.
                    if (row.IsSpam) continue;
                    listedRows++;
                    var chat = copy.Chat(row.Id);
                    if (!chat.State.Filled) { copy.Enqueue(row.Id, HistoryWork.Initial); filling++; continue; }
                    // Read from the row in hand: asking the router for it again failed for some Messenger conversations every time.
                    if (row.LastActivity is not { } last || !chat.MovedPast(last)) continue;
                    checking++;
                    copy.Enqueue(row.Id, HistoryWork.Refresh);
                }
                log($"history walk listed={listedRows} filling={filling} checking={checking} held={copy.FilledCount} queued={Queue.Count} seq={copy.Size.Seq} bytes={copy.Size.Bytes} ms={Ms(clock)}");
                mirror.Start(list, "walk");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"history walk failed listed={listedRows} error={failure.GetType().Name}"); }
            try { await Task.Delay(timings.WalkEvery, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
