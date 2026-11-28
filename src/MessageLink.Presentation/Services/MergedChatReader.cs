using System.Text.Json;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

public sealed class MergedChatReader(IChatReader source, MergedConversations merges) : IChatReader, ILinkPreviewLoader
{
    // The cursor carries the unconsumed ends of provider pages. It is only held in memory or in
    // protected history snapshots. Keeping these buffers avoids gaps when one busy service has
    // fifty messages today and another service's first page reaches back several months.
    public sealed record StreamCursor(string ChatId, string? Next, bool Complete, ChatMessage[] Buffer, string[] Seen);
    public sealed record MergeCursor(string ChatId, string[] Members, StreamCursor[] Streams);
    private const int PageSize = 80;
    // A merge's newest page ends where one service's first page runs out, once it holds this many rows: going on meant
    // asking that service for its next page only after every first page was back, about 450 ms in series on every open
    // (Google's page is 50 rows; 2026-10-01). The rows past that point come with scrolling up, as older pages always did.
    private const int EnoughToOpen = 30;
    // Older history from Google Messages is the phone's: each 50-row page is a round trip of 0.3–1.1 s (2026-10-07), and
    // an 80-row merged page can need two in a row. Once somebody is scrolling back, the next older rows are read while
    // they read this page, so the trip is behind them rather than in front. Google only: the other services answer older
    // pages from memory, and Instagram's are network reads its account rules keep to what the user asks for. Never the
    // newest page, never more than two pages ahead, and a read is kept a minute at most.
    private static readonly TimeSpan AheadKept = TimeSpan.FromMinutes(1);
    private const int AheadPages = 2;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string ChatId, string Next), (Task<Page<ChatMessage>> Read, long At)> ahead = new();
    private static bool IsGoogle(string chatId) => chatId.StartsWith("google-direct:", StringComparison.Ordinal);
    // On in the app. Off by default, so a reader made for a check pages only when it is asked to and its reads stay countable.
    public bool ReadsAhead { get; init; }
    // One line per merged page read: each service's first-page time (read together), and the older pages read after
    // them one at a time to fill the page. Counts, times and service names only.
    public Action<string>? Timed { get; init; }
    // Which members a merged details read asks their services about again; null asks every one. Ambient, so that it
    // reaches this reader through the preferences layer wrapped around it, from the one caller that narrows it.
    private static readonly AsyncLocal<IReadOnlySet<string>?> refreshOnly = new();
    public static IDisposable RefreshOnly(IReadOnlySet<string>? members)
    {
        var prior = refreshOnly.Value;
        refreshOnly.Value = members;
        return new Restore(prior);
    }
    private sealed class Restore(IReadOnlySet<string>? prior) : IDisposable { public void Dispose() => refreshOnly.Value = prior; }
    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        await merges.EnsureLoadedAsync();
        var page = await source.GetChatsAsync(olderThan, cancellationToken);
        merges.Observe(page.Items);
        return page;
    }
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default) =>
        GetChatAsync(chatId, refreshOnly.Value, cancellationToken);
    // A member left out of refresh keeps the details last seen, which the chat list keeps current as it reads each batch.
    public async Task<ChatSummary> GetChatAsync(string chatId, IReadOnlySet<string>? refresh, CancellationToken cancellationToken = default)
    {
        await merges.EnsureLoadedAsync();
        if (!MergedConversations.IsMerged(chatId))
        {
            var chat = await source.GetChatAsync(chatId, cancellationToken);
            merges.Observe([chat]);
            return chat;
        }
        var members = merges.Members(chatId);
        if (members.Count == 0) throw new InvalidOperationException("This merged conversation no longer exists.");
        await Task.WhenAll(members.Where(m => !MergedConversations.IsCall(m) && refresh?.Contains(m.Id) != false).Select(async member =>
        {
            try { merges.Observe([await source.GetChatAsync(member.Id, cancellationToken)]); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Saved source details are useful offline; history reports unavailable sources. */ }
        }));
        return merges.Summary(chatId);
    }
    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        if (!MergedConversations.IsMerged(chatId))
        {
            var (single, _) = await ReadAsync(chatId, olderThan, cancellationToken);
            // One Google page answers one page here, so one ahead is enough.
            if (olderThan is not null && single.HasMore && single.OldestCursor is { } nextPage) ReadAhead(chatId, nextPage, 1);
            return single;
        }
        await merges.EnsureLoadedAsync();
        var members = merges.Members(chatId);
        if (members.Count == 0) throw new InvalidOperationException("This merged conversation no longer exists.");
        var ids = members.Select(m => m.Id).Order(StringComparer.Ordinal).ToArray();
        MergeCursor cursor;
        if (olderThan is null)
        {
            var streams = members.Where(m => !MergedConversations.IsCall(m) || m.Preview is null)
                .Select(m => new StreamCursor(m.Id, null, false, [], [])).ToList();
            // Voice call/voicemail previews are the full single-message call record, including
            // the protected media reference. No audio is downloaded until the user plays it.
            var calls = members.Where(MergedConversations.IsCall).Select(m => m.Preview).OfType<ChatMessage>().ToArray();
            if (calls.Length > 0) streams.Add(new("calls", null, true, NewestFirst(calls), []));
            cursor = new(chatId, ids, streams.ToArray());
        }
        else
        {
            cursor = JsonSerializer.Deserialize<MergeCursor>(Convert.FromBase64String(olderThan))
                ?? throw new InvalidOperationException("Refresh to reload this merged history.");
            if (cursor.ChatId != chatId) throw new InvalidOperationException("Refresh to reload this merged history.");
            if (!cursor.Members.SequenceEqual(ids)) cursor = Reconcile(cursor, members, ids);
        }
        var working = cursor.Streams.ToArray();
        var failed = new HashSet<string>();
        var unavailable = new HashSet<string>();
        var refreshing = false;
        var refreshingMembers = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var extraPages = 0;
        var aheadUsed = 0;
        long extraMs = 0;
        // Independent providers can begin their first pages together. Opening a merge used to add every member's
        // latency in series before it could choose even the first row. Subsequent cursor refills retain their ordering.
        var firstMs = new Dictionary<string, long>(StringComparer.Ordinal);
        var firstReads = await Task.WhenAll(working.Select(async stream =>
        {
            if (stream.Buffer.Length > 0 || stream.Complete) return (Stream: stream, Page: (Page<ChatMessage>?)null, Waiting: false, Failed: false);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var (page, readAhead) = await ReadAsync(stream.ChatId, stream.Next, cancellationToken);
                if (readAhead) Interlocked.Increment(ref aheadUsed);
                return (Stream: stream, Page: (Page<ChatMessage>?)page, Waiting: false, Failed: false);
            }
            catch (OperationCanceledException) { throw; }
            catch (BackendException ex) when (ex.Waits) { return (Stream: stream, Page: (Page<ChatMessage>?)null, Waiting: true, Failed: false); }
            catch (Exception) { return (Stream: stream, Page: (Page<ChatMessage>?)null, Waiting: true, Failed: true); }
            finally { lock (firstMs) firstMs[stream.ChatId] = Ms(began); }
        }));
        var prefetched = new Dictionary<string, Page<ChatMessage>>(StringComparer.Ordinal);
        foreach (var read in firstReads)
        {
            if (read.Page is not null) prefetched[read.Stream.ChatId] = read.Page;
            if (read.Waiting) unavailable.Add(read.Stream.ChatId);
            if (read.Failed) failed.Add(read.Stream.ChatId);
        }
        string? ranOut = null;
        for (var n = 0; n < PageSize && ranOut is null; n++)
        {
            for (var i = 0; i < working.Length; i++)
            {
                var stream = working[i];
                var emptyPages = 0;
                while (stream.Buffer.Length == 0 && !stream.Complete && !unavailable.Contains(stream.ChatId))
                {
                    // Every row chosen so far is newer than anything this service has not yet given, so the page can end
                    // here without a gap; its cursor resumes from the same place.
                    if (olderThan is null && result.Count >= EnoughToOpen && !prefetched.ContainsKey(stream.ChatId))
                    { ranOut = stream.ChatId; break; }
                    try
                    {
                        var prefetchedPage = prefetched.Remove(stream.ChatId, out var ready);
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var (page, readAhead) = prefetchedPage ? (ready!, false) : await ReadAsync(stream.ChatId, stream.Next, cancellationToken);
                        if (readAhead) aheadUsed++;
                        if (!prefetchedPage) { extraPages += readAhead ? 0 : 1; extraMs += Ms(began); }
                        refreshing |= page.IsRefreshing;
                        if (page.IsRefreshing) refreshingMembers.Add(stream.ChatId);
                        if (page.Warning is not null) { failed.Add(stream.ChatId); unavailable.Add(stream.ChatId); }
                        var stalled = page.HasMore && (page.OldestCursor is null || stream.Seen.Contains(page.OldestCursor));
                        if (stalled) { failed.Add(stream.ChatId); unavailable.Add(stream.ChatId); }
                        stream = stream with
                        {
                            Buffer = NewestFirst(page.Items.Where(m => m.ChatId == stream.ChatId)),
                            Next = page.OldestCursor, Complete = !page.HasMore || stalled,
                            Seen = page.OldestCursor is null ? stream.Seen : [.. stream.Seen, page.OldestCursor]
                        };
                        // Bound providers which return empty pages indefinitely.
                        if (++emptyPages >= 8 && stream.Buffer.Length == 0 && !stream.Complete)
                        { failed.Add(stream.ChatId); unavailable.Add(stream.ChatId); }
                    }
                    catch (OperationCanceledException) { throw; }
                    // Startup restores services independently. A service which has not connected yet still owns
                    // its saved rows, but has not reported a failure worth asking the user to fix.
                    catch (BackendException ex) when (ex.Waits) { unavailable.Add(stream.ChatId); }
                    catch (Exception) { failed.Add(stream.ChatId); unavailable.Add(stream.ChatId); }
                }
                working[i] = stream;
                if (ranOut is not null) break;
            }
            if (ranOut is not null) break;
            var next = Enumerable.Range(0, working.Length).Where(i => working[i].Buffer.Length > 0)
                .OrderByDescending(i => working[i].Buffer[0].Timestamp)
                .ThenByDescending(i => working[i].Buffer[0].SortKey, StringComparer.Ordinal)
                .ThenByDescending(i => MessageIdentity.Key(working[i].Buffer[0]), StringComparer.Ordinal).FirstOrDefault(-1);
            if (next < 0) break;
            result.Add(working[next].Buffer[0]);
            working[next] = working[next] with { Buffer = working[next].Buffer[1..] };
        }
        var more = working.Any(s => !s.Complete || s.Buffer.Length > 0);
        if (Timed is { } timed)
        {
            var services = members.ToDictionary(m => m.Id, m => m.Network, StringComparer.Ordinal);
            var first = string.Join(",", firstMs.OrderByDescending(p => p.Value)
                .Select(p => $"{(services.TryGetValue(p.Key, out var network) ? network : p.Key == "calls" ? "calls" : "?").Replace(" ", "")}:{p.Value}"));
            string Name(string id) => (services.TryGetValue(id, out var network) ? network : id == "calls" ? "calls" : "?").Replace(" ", "");
            timed($"history merged read older={olderThan is not null} streams={working.Length} first_ms=[{first}] extra_pages={extraPages} extra_ms={extraMs} " +
                $"ended_at={(ranOut is null ? "none" : Name(ranOut))} rows={result.Count} total_ms={Ms(started)} ahead_used={aheadUsed}");
        }
        // Scrolling back: the Google rows the next page will want are read now, behind the reader.
        if (olderThan is not null)
            foreach (var stream in working)
                if (!stream.Complete && !unavailable.Contains(stream.ChatId) && stream.Buffer.Length < PageSize && stream.Next is { } next)
                    ReadAhead(stream.ChatId, next, PageSize - stream.Buffer.Length);
        await merges.ObserveIncomingMessagesAsync(chatId, result);
        if (olderThan is null) merges.ObserveRefreshing(chatId, refreshingMembers.Where(id => !unavailable.Contains(id)).ToArray());
        var token = more ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor with { Streams = working })) : null;
        return new(result.DistinctBy(MessageIdentity.Key).OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .ThenBy(MessageIdentity.Key, StringComparer.Ordinal).ToArray(), more, token, null,
            refreshing || unavailable.Count > 0,
            failed.Count > 0 ? "Some linked history is unavailable. Showing the services that answered; refresh to try again." : null,
            unavailable.ToArray());
    }
    // A page read ahead under this cursor when there is one (Ahead), otherwise read now. A read ahead that failed, or is
    // older than AheadKept, is read again as if it had never been made.
    private async Task<(Page<ChatMessage> Page, bool Ahead)> ReadAsync(string chatId, string? next, CancellationToken cancellationToken)
    {
        // Reading the newest page again (an open, a reload) starts this conversation's paging afresh, so nothing read
        // ahead for the paging before is used.
        if (next is null)
            foreach (var key in ahead.Keys)
                if (key.ChatId == chatId) ahead.TryRemove(key, out _);
        if (next is not null && ahead.TryRemove((chatId, next), out var held) && System.Diagnostics.Stopwatch.GetElapsedTime(held.At) < AheadKept)
        {
            try { return (await held.Read.WaitAsync(cancellationToken), true); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        return (await source.GetMessagesAsync(chatId, next, cancellationToken), false);
    }

    // Up to `rows` older rows of one Google conversation, a page at a time, each kept under the cursor that will ask for
    // it. Nothing waits on these. A failed read is cancelled rather than faulted, so nothing unobserved is left behind.
    private void ReadAhead(string chatId, string next, int rows)
    {
        if (!ReadsAhead || !IsGoogle(chatId)) return;
        foreach (var (key, held) in ahead)
            if (System.Diagnostics.Stopwatch.GetElapsedTime(held.At) >= AheadKept) ahead.TryRemove(key, out _);
        // Reserve the first cursor before scheduling work. Under CPU load the worker may start after the reader asks
        // for its next page; registering inside Task.Run let that read miss the reservation and ask the phone twice.
        var first = new TaskCompletionSource<Page<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ahead.TryAdd((chatId, next), (first.Task, System.Diagnostics.Stopwatch.GetTimestamp()))) return;
        _ = Task.Run(async () =>
        {
            var cursor = next;
            var pending = first;
            for (var pages = 0; pages < AheadPages && rows > 0 && cursor is not null; pages++)
            {
                Page<ChatMessage> page;
                try { page = await source.GetMessagesAsync(chatId, cursor, CancellationToken.None); }
                catch (Exception)
                {
                    ahead.TryRemove((chatId, cursor), out _);
                    pending.TrySetCanceled();
                    return;
                }
                rows -= page.Items.Count;
                // Reserve the following cursor before waking a reader which may need both 50-row pages for its 80 rows.
                // Otherwise its continuation can outrun this worker and fetch the second page a second time too.
                TaskCompletionSource<Page<ChatMessage>>? following = null;
                if (rows > 0 && pages + 1 < AheadPages && page.HasMore && page.Warning is null && page.OldestCursor is { } older && older != cursor)
                {
                    var candidate = new TaskCompletionSource<Page<ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (ahead.TryAdd((chatId, older), (candidate.Task, System.Diagnostics.Stopwatch.GetTimestamp()))) following = candidate;
                }
                pending.TrySetResult(page);
                if (following is null) return;
                cursor = page.OldestCursor;
                pending = following;
            }
        });
    }

    // A merge's members change with nobody paging it: the user adds a conversation, the chat list pages in an older Voice
    // call record of the same person, or one goes to spam. A token made before refused to page on, and Load earlier
    // failed with a connection error. It follows instead. An added conversation is read from its newest page and an added
    // call record joins the "calls" stream, each whole, so rows of theirs newer than what was shown arrive with the next
    // page among rows already drawn, once each; the services already being paged carry on where they were rather than
    // start again from their newest page. Whatever left drops out.
    private static MergeCursor Reconcile(MergeCursor cursor, IReadOnlyList<ChatSummary> members, string[] ids)
    {
        var had = cursor.Members.ToHashSet(StringComparer.Ordinal);
        var now = ids.ToHashSet(StringComparer.Ordinal);
        var added = members.Where(m => !had.Contains(m.Id)).ToArray();
        var streams = cursor.Streams.Where(s => s.ChatId == "calls" || now.Contains(s.ChatId)).ToList();
        streams.AddRange(added.Where(m => !MergedConversations.IsCall(m) || m.Preview is null).Select(m => new StreamCursor(m.Id, null, false, [], [])));
        var index = streams.FindIndex(s => s.ChatId == "calls");
        var calls = index < 0 ? new StreamCursor("calls", null, true, [], []) : streams[index];
        calls = calls with { Buffer = NewestFirst(calls.Buffer.Where(m => now.Contains(m.ChatId))
            .Concat(added.Where(MergedConversations.IsCall).Select(m => m.Preview).OfType<ChatMessage>())) };
        if (index >= 0) streams[index] = calls; else if (calls.Buffer.Length > 0) streams.Add(calls);
        return cursor with { Members = ids, Streams = [.. streams] };
    }
    private static long Ms(long since) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(since).TotalMilliseconds;
    private static ChatMessage[] NewestFirst(IEnumerable<ChatMessage> messages) => messages.DistinctBy(MessageIdentity.Key)
        .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.SortKey, StringComparer.Ordinal)
        .ThenByDescending(MessageIdentity.Key, StringComparer.Ordinal).ToArray();
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        source is ILinkPreviewLoader loader ? loader.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : Task.CompletedTask;
}
