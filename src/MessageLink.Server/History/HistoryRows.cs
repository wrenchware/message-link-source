using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.App.Services;
using MessageLink.Core.Services;
using MessageLink.Protocol;
using MessageLink.Storage;
using static MessageLink.Server.HistoryParts;

namespace MessageLink.Server;

/// <summary>
/// The list rows the feed serves (B1 and B2): the services' own rows as last read, each built with its last message from the copy,
/// kept in the store and announced as they change. Rows are built again a moment after an event names them, and the whole list is read on a reconnect and
/// by the walk.
/// </summary>
internal sealed partial class HistoryRows(HistoryCopy copy, HistoryDatabase db, IChatReader chats, IHistoryFacts facts, HistoryTimings timings, Action<string> log, CancellationToken stop)
{
    private const string DeepListNote = "deep_list_at";
    // A whole read of the list on a reconnect at most this often; the walk reads it every half hour anyway. Each reads Instagram's inbox when its list is
    // more than a few seconds old, as its poll does.
    private static readonly long ListReadEvery = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    // The services' own list rows as last read, which the served rows are built from.
    private readonly ConcurrentDictionary<string, ChatSummary> listed = new(StringComparer.Ordinal);
    // Rows to build again soon: true to read the service's row first, false to build again from the one held.
    private readonly ConcurrentDictionary<string, bool> rowsDue = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0);
    private int signaled;
    private volatile bool listDue;
    // The first whole read of the list is in the store: a feed that starts a client over waits for it (a little).
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // List reads and row writes one at a time: a list read seconds long overwrote rows written meanwhile from fresher events, and marked a conversation that
    // began meanwhile as gone (review, 2026-10-30).
    private readonly SemaphoreSlim gate = new(1, 1);
    private long listReadAt;
    // Completed while no row waits to be built: a feed read waits for it (a little), so a row asked for by an event just before is in what it hands out.
    private TaskCompletionSource idle = CompletedSource();
    // Rows a whole read did not list, and the read before it did not either: only those leave. A service can hand back a part of its list as if whole
    // right after it connects (its restored rows), and one read alone would drop the rest for every client until the next (review, 2026-10-30).
    private HashSet<string> missingOnce = new(StringComparer.Ordinal);
    private int deepReading;

    /// <summary>The log the row changes go into.</summary>
    public EventLog? Events { get; set; }

    /// <summary>The first whole read of the list is in the store (or there is no store to read it into).</summary>
    public Task Ready => ready.Task;

    /// <summary>Nothing will ever be read: the store did not open.</summary>
    public void Abandon() => ready.TrySetResult();

    /// <summary>No row is waiting to be built.</summary>
    public Task Idle => Volatile.Read(ref idle).Task;

    /// <summary>The list as last read, if it has been.</summary>
    public IReadOnlyList<ChatSummary>? Listed => listed.IsEmpty ? null : listed.Values.ToArray();

    private static TaskCompletionSource CompletedSource()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>
    /// A row to build again soon. Read: ask the router for it first (after an event). One signal however many are asked for at once: a Messenger reconnect
    /// names every conversation, and a release each made as many empty passes of the loop (review, 2026-10-30).
    /// </summary>
    public void Soon(string chatId, bool read)
    {
        rowsDue.AddOrUpdate(chatId, read, (_, was) => was || read);
        if (Volatile.Read(ref idle).Task.IsCompleted) Interlocked.Exchange(ref idle, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.Exchange(ref signaled, 1) == 0) signal.Release();
    }

    /// <summary>Whatever the list did while a service was away: read it whole again.</summary>
    public void ListDue()
    {
        listDue = true;
        signal.Release();
    }

    /// <summary>A row the router read for a client: kept, and built again for the feed too, since a client was just told it.</summary>
    public Task<ChatSummary> ReadForClientAsync(string chatId, ChatSummary row, CancellationToken cancellationToken)
    {
        listed[chatId] = row;
        Soon(chatId, read: false);
        return BuildAsync(row, cancellationToken);
    }

    /// <summary>A page of the list the router read for a client: each row kept and built.</summary>
    public async Task<IReadOnlyList<ChatSummary>> PageForClientAsync(IReadOnlyList<ChatSummary> page, CancellationToken cancellationToken)
    {
        var built = new ChatSummary[page.Count];
        for (var i = 0; i < built.Length; i++)
        {
            listed[page[i].Id] = page[i];
            built[i] = await BuildAsync(page[i], cancellationToken).ConfigureAwait(false);
        }
        return built;
    }

    /// <summary>Conversations deleted: no row of theirs is built again from now.</summary>
    public void Drop(IReadOnlyList<string> named)
    {
        foreach (var id in named) { listed.TryRemove(id, out _); rowsDue.TryRemove(id, out _); }
    }

    /// <summary>Deleted conversations' rows leave the list and the feed (after <see cref="Drop"/>, and after the copy lets them go).</summary>
    public async Task ForgetAsync(IReadOnlyList<string> named)
    {
        // Behind a whole read of the list under way: it can have listed these before they went, and would write them back over the mark until two more reads
        // left them out (review, 2026-10-30).
        await gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            foreach (var id in named) listed.TryRemove(id, out _);
            await db.WriteRowsAsync([.. named.Select(id => new HistoryListRow(id, null))], stop).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>An account's rows leave the feed, the conversations never filled among them; returns how many left.</summary>
    public async Task<int> ForgetWhereAsync(Func<string, bool> matches)
    {
        var rows = (await db.ReadListedAsync(stop).ConfigureAwait(false)).Where(matches).ToArray();
        foreach (var id in rows) listed.TryRemove(id, out _);
        return (await db.WriteRowsAsync([.. rows.Select(id => new HistoryListRow(id, null))], stop).ConfigureAwait(false)).Count;
    }

    /// <summary>Builds the rows asked for, a moment after the first ask so a burst builds each once, and the whole list when asked.</summary>
    public async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stop).ConfigureAwait(false);
                await Task.Delay(timings.RowsDelay, stop).ConfigureAwait(false);
                Volatile.Write(ref signaled, 0);
                if (listDue)
                {
                    listDue = false;
                    // A new store (no note of a deep read) reads deep before it says it is ready: a client's first read takes the rows it finds and is told of no more later, so a
                    // phone joining a new server would hold only the newest pages (review, 2026-11-01). A restart has its rows already, and its deep read comes when it is due.
                    if (!ready.Task.IsCompleted) await ReadDueAsync(stop).ConfigureAwait(false);
                    else if (Environment.TickCount64 - Interlocked.Read(ref listReadAt) >= ListReadEvery) await ReadListAsync(stop).ConfigureAwait(false);
                }
                await gate.WaitAsync(stop).ConfigureAwait(false);
                try
                {
                    var due = new List<(string Id, bool Read)>();
                    foreach (var id in rowsDue.Keys) if (rowsDue.TryRemove(id, out var read)) due.Add((id, read));
                    var rows = new List<ChatSummary>(due.Count);
                    foreach (var (id, read) in due)
                    {
                        ChatSummary? row = null;
                        // Read from the router after an event, or for one not listed since the last whole read (a conversation begun since). Never where reads
                        // cost a budget: Instagram's row can come from Instagram itself (a GetThread on the real account) when its list is a few seconds old, a
                        // read outside its budget nobody asked for (review, 2026-10-30). Its rows come from the list reads, their last line from the copy.
                        if ((read || !listed.ContainsKey(id)) && !facts.FactsOf(id).ReadsCostBudget)
                        {
                            try { row = listed[id] = await chats.GetChatAsync(id, stop).ConfigureAwait(false); }
                            catch (Exception failure) when (failure is not OperationCanceledException) { }
                        }
                        if ((row ?? listed.GetValueOrDefault(id)) is { } known) rows.Add(known);
                    }
                    await StoreAsync(rows, stop, announce: true).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"history rows failed error={failure.GetType().Name}"); }
            finally { if (rowsDue.IsEmpty) Volatile.Read(ref idle).TrySetResult(); }
        }
    }

    /// <summary>The list as a routine read takes it, or as far as the deep read goes when that is due (once a day).</summary>
    public async Task<IReadOnlyList<ChatSummary>?> ReadDueAsync(CancellationToken cancellationToken) =>
        await (await DeepListDueAsync().ConfigureAwait(false) ? ReadDeepListAsync(cancellationToken) : ReadListAsync(cancellationToken)).ConfigureAwait(false);

    // Kept with the store: the server restarts more often than a day, and a deep read at each start would be the same calls again. A first start has no rows, so it reads deep.
    private async Task<bool> DeepListDueAsync()
    {
        var note = await db.ReadNoteAsync(DeepListNote, stop).ConfigureAwait(false);
        return !DateTimeOffset.TryParse(note, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || DateTimeOffset.UtcNow - at >= timings.DeepListEvery;
    }

    // How far a read of the list got: true when it reached the list's end or the most it may read with every service answering.
    private sealed class ListCoverage { public bool Covered; }

    // A deep read, noted as done only if it was: a service down for it (a retry cursor, not a failure) would otherwise leave its older rows a day behind, restarts included
    // (review, 2026-11-01). Not noted, the next walk reads deep again, paced as ever. One at a time: the walk can come due while a first start's is still reading.
    private async Task<IReadOnlyList<ChatSummary>?> ReadDeepListAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref deepReading, 1) == 1) return await ReadListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var coverage = new ListCoverage();
            var rows = await ReadListAsync(cancellationToken, deep: true, coverage).ConfigureAwait(false);
            if (coverage.Covered) await db.WriteNoteAsync(DeepListNote, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return rows;
        }
        finally { Volatile.Write(ref deepReading, 0); }
    }

    /// <summary>
    /// Reads the services' list, its newest <see cref="HistoryTimings.ListPages"/> pages or with <paramref name="deep"/> up to <see cref="HistoryTimings.DeepListPages"/>, keeps
    /// every row for the feed, and lets go of rows no longer on it (only when the whole list was read, only for a service that answered with some rows, and only those two whole
    /// reads in a row left out). Null when it could not be read.
    /// </summary>
    public async Task<IReadOnlyList<ChatSummary>?> ReadListAsync(CancellationToken cancellationToken) => await ReadListAsync(cancellationToken, deep: false, null).ConfigureAwait(false);

    private async Task<IReadOnlyList<ChatSummary>?> ReadListAsync(CancellationToken cancellationToken, bool deep, ListCoverage? coverage)
    {
        var clock = Stopwatch.GetTimestamp();
        var most = deep ? timings.DeepListPages : timings.ListPages;
        string? cursor = null;
        var rows = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var (whole, done, pages, changed, gone) = (false, false, 0, 0, 0);
        try
        {
            Interlocked.Exchange(ref listReadAt, Environment.TickCount64);
            // A stretch of pages at a time under the gate: rows from events wait on it, and a feed read waits on them.
            while (!done)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var stretch = new List<ChatSummary>();
                    for (var inStretch = 0; inStretch < Math.Max(1, timings.ListPages) && !done; inStretch++)
                    {
                        if (pages > 0 && timings.ListPagePace > TimeSpan.Zero) await Task.Delay(timings.ListPagePace, cancellationToken).ConfigureAwait(false);
                        var page = await chats.GetChatsAsync(cursor, cancellationToken).ConfigureAwait(false);
                        pages++;
                        var added = 0;
                        foreach (var row in page.Items) if (rows.TryAdd(row.Id, row)) { added++; stretch.Add(row); }
                        unavailable.UnionWith(page.UnavailableScopes ?? []);
                        if (!page.HasMore) { whole = true; done = true; break; }
                        // A service that did not answer hands back a retry cursor for as long as it does not (ConversationSync does the same).
                        if (page.OldestCursor is null || page.OldestCursor == cursor || unavailable.Count > 0 && added == 0) { done = true; break; }
                        cursor = page.OldestCursor;
                        if (pages >= most) done = true;
                    }
                    foreach (var row in stretch) listed[row.Id] = row;
                    changed += await StoreAsync(stretch, cancellationToken, announceIf: rowsDue.ContainsKey).ConfigureAwait(false);
                    if (whole)
                    {
                        var answered = rows.Keys.Select(Service).ToHashSet(StringComparer.Ordinal);
                        var missing = (await db.ReadListedAsync(cancellationToken).ConfigureAwait(false))
                            .Where(id => !rows.ContainsKey(id) && answered.Contains(Service(id)) && !unavailable.Any(scope => id.StartsWith(scope, StringComparison.Ordinal)))
                            .ToHashSet(StringComparer.Ordinal);
                        var left = missing.Where(missingOnce.Contains).ToArray();
                        missingOnce = missing;
                        foreach (var id in left) listed.TryRemove(id, out _);
                        gone = (await db.WriteRowsAsync([.. left.Select(id => new HistoryListRow(id, null))], cancellationToken).ConfigureAwait(false)).Count;
                    }
                }
                finally { gate.Release(); }
            }
            if (coverage is not null) coverage.Covered = unavailable.Count == 0 && (whole || pages >= most);
            log($"history rows listed={rows.Count} changed={changed} gone={gone} whole={(whole ? 1 : 0)} unavailable={unavailable.Count} pages={pages} deep={(deep ? 1 : 0)} ms={Ms(clock)}");
            return [.. rows.Values];
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log($"history rows failed error={failure.GetType().Name}");
            return null;
        }
        // Not a short read while a first deep one is still going: that one is what the first client waits for.
        finally { if (deep || Volatile.Read(ref deepReading) == 0) ready.TrySetResult(); }
    }

    // Keeps rows as built, and with [announce] puts each that changed in the event sequence for clients of 25 (EventKinds.Row): what a row did by itself (its
    // copy moved, an event named it) reaches a connected client's list with no read. A whole read of the list announces nothing (on the first after this
    // version it changes every row, which would push real events out of the ring); clients take those from the feed when they next read it. Except a row an
    // event is waiting to build ([announceIf]): the read wrote what the event changed, so the event's own pass found nothing changed and no row event went out
    // (a Hermes reply during the server's first list read, 2026-11-15; the apps also read the row again on the realtime event, so their lists still moved).
    private async Task<int> StoreAsync(IReadOnlyList<ChatSummary> rows, CancellationToken cancellationToken, bool announce = false, Func<string, bool>? announceIf = null)
    {
        if (rows.Count == 0) return 0;
        var built = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var row in rows) built[row.Id] = await BuildAsync(row, cancellationToken).ConfigureAwait(false);
        var changed = await db.WriteRowsAsync([.. built.Values.Select(row => new HistoryListRow(row.Id, JsonSerializer.SerializeToUtf8Bytes(row, ProtocolJson.Options)))],
            cancellationToken).ConfigureAwait(false);
        if ((announce || announceIf is not null) && Events is { } sequence)
            foreach (var row in changed.Where(row => announce || announceIf!(row.ChatId))) sequence.AppendRow(new(row.ChatId, built.GetValueOrDefault(row.ChatId)));
        return changed.Count;
    }

    /// <summary>
    /// A conversation's list row as served (owner's D1, 2026-10-30): the service's row, its last message from the copy, so the list never says what the thread
    /// does not hold. The service's own line stays where it says what no message does (a reaction, in the service's words), and where nothing is held yet.
    /// A line newer than anything held is the copy being behind: the row says what is held, and the conversation is read so the two meet.
    /// </summary>
    private async Task<ChatSummary> BuildAsync(ChatSummary row, CancellationToken cancellationToken)
    {
        if (copy.Whole(row.Id) is not { } chat) return row;
        try
        {
            var service = row.Preview;
            // The message the line names as the copy holds it, or as the event naming it is reading it now (built a moment before, the row said the message
            // before it, and a client that read the feed then kept that).
            if (service is not null && (await copy.PendingAsync(row.Id, service.Id, cancellationToken).ConfigureAwait(false)
                    ?? await copy.HeldMessageAsync(row.Id, service.Id, cancellationToken).ConfigureAwait(false)) is { } same)
                return row with { Preview = same };
            var rows = await db.ReadNewestAsync(row.Id, 8, cancellationToken).ConfigureAwait(false);
            var last = Sorted(rows.Select(Message).OfType<ChatMessage>()).LastOrDefault(message => !message.IsHidden);
            if (last is null) return row;
            if (service is null || service.Timestamp <= last.Timestamp.AddSeconds(2)) return row with { Preview = last };
            if (IsReactionLine(service)) return row;
            if (row.LastActivity is { } moved && chat.MovedPast(moved)) copy.Enqueue(row.Id, HistoryWork.Refresh);
            return row with { Preview = last };
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { return row; }
    }

    // A service's line that says a reaction rather than a message: Google's "😂 to "…"", and the words Meta's apps use.
    private static bool IsReactionLine(ChatMessage line) =>
        line.Text is { Length: > 0 } text && (GoogleReaction().IsMatch(text) || text.Contains("reacted ", StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex("^\\S{1,16} to [\"“].*[\"”]$")]
    private static partial System.Text.RegularExpressions.Regex GoogleReaction();
}
