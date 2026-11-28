using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// A client's own copy of the conversations it reads, following the server's whole history (protocol 24, SWITCHBOARD.md stage 8): each copy holds a run of a
/// conversation from its newest message down, with the server's revision of it, and takes every change the server makes from the event feed, where the change
/// carries its messages. A read of the newest messages always asks <c>history.sync</c> for what changed since the copy's revision (one small answer instead of
/// a page, and the server's cue to read a conversation again that it has not read for a while); older pages, a message, a peek and the held rows are answered
/// from a copy known current with no call at all. Below it, the reads go to the server as they always did.
/// </summary>
/// <remarks>
/// <para>Known current means: a sync answered in this run of the feed, every change since applied, and no realtime event naming the conversation since that
/// sync was asked. The server sends a conversation's realtime event before the change it leads to, but one event's change cannot be told from another's
/// (two events about the same message, a read that changed nothing and sends none), so only a sync asked after an event, which the server answers once that
/// event's read is written, settles it.</para>
/// <para>Bounded: <see cref="Capacity"/> conversations, the one used longest ago let go first, each at most <see cref="MaxMessages"/> of its newest.
/// In memory only: the client's own saved copies draw the first frame after a start, as they did. Logs a count line now and then; never ids or text.</para>
/// </remarks>
public sealed class HistoryCopies
{
    public const int Capacity = 64, PageSize = 50;
    private readonly ProtocolClient client;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly Dictionary<string, Copy> copies = new(StringComparer.Ordinal);
    private long clock, generation;
    private int local, synced, forwarded, applied, missed;

    public HistoryCopies(ProtocolClient client, Action<string>? log = null)
    {
        this.client = client;
        this.log = log;
        client.HistoryChanged += (_, change) => Changed(change);
        client.RealtimeRead += (_, change) => Named(change);
        // Whatever came while the feed was not running is not in any copy: each asks once more before it answers.
        client.FeedResynced += (_, _) => Unsettle();
        client.Connected += (_, _) => Unsettle();
        client.HistoryUnreadable += (_, _) => Unsettle();
        checker = new Timer(_ => _ = CheckSoonAsync(), null, CheckFirst, CheckEvery);
    }

    // ---- The copies checked against the server's (SWITCHBOARD.md stage 9, B4) ----

    private readonly Timer checker;
    private int checking;

    /// <summary>When the copies are first compared with the server's after a start, and how often after that.</summary>
    public static readonly TimeSpan CheckFirst = TimeSpan.FromMinutes(2), CheckEvery = TimeSpan.FromMinutes(30);

    private async Task CheckSoonAsync()
    {
        if (Interlocked.Exchange(ref checking, 1) == 1) return;
        try { await CheckAsync().ConfigureAwait(false); }
        catch (Exception) { }
        finally { Volatile.Write(ref checking, 0); }
    }

    /// <summary>
    /// Compares every copy known current with the server's by fingerprint (<c>history.verify</c>, which reads no service) and starts again any that differs:
    /// a copy that drifted from the server's is how a thread shows a message twice, or misses one, while the phone's is right. Logs counts only. Null when
    /// nothing could be compared (no copy current, a server below 25, no feed).
    /// </summary>
    public async Task<(int Same, int Differ, int Behind, int Unknown)?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || client.Version is not >= ProtocolVersion.Feed || !client.HasEventFeed) return null;
        List<HistoryCopyDigest> asked;
        lock (gate)
        {
            asked = copies.Where(pair => pair.Value.Settled && pair.Value.Messages.Count > 0)
                .OrderByDescending(pair => pair.Value.Used).Take(WholeHistoryLimits.MaxVerify)
                .Select(pair => new HistoryCopyDigest(pair.Key, pair.Value.Revision, pair.Value.Messages[0].Timestamp, pair.Value.Messages[0].SortKey ?? "",
                    pair.Value.Messages[0].Id, pair.Value.Messages.Count, HistoryDigest.Of(pair.Value.Messages))).ToList();
        }
        if (asked.Count == 0) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        HistoryVerifyResult result;
        try { result = await client.CallAsync<HistoryVerifyParams, HistoryVerifyResult>(ProtocolMethods.HistoryVerify, new(asked), cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
        int same = 0, differ = 0, behind = 0, unknown = 0;
        lock (gate)
        {
            var sent = asked.ToDictionary(copy => copy.ChatId, StringComparer.Ordinal);
            foreach (var check in result.Chats)
            {
                switch (check.Outcome)
                {
                    case HistoryCopyOutcomes.Same: same++; break;
                    case HistoryCopyOutcomes.Behind: behind++; break;
                    case HistoryCopyOutcomes.Differ:
                        differ++;
                        // Starts again from the server's newest at its next read, unless it moved on since it was compared.
                        if (copies.TryGetValue(check.ChatId, out var copy) && sent.TryGetValue(check.ChatId, out var was) && copy.Revision == was.Revision)
                        {
                            copy.Messages = [];
                            copy.Revision = "";
                            copy.Current = false;
                        }
                        break;
                    default: unknown++; break;
                }
            }
        }
        try { log?.Invoke($"history check chats={asked.Count} same={same} differ={differ} behind={behind} unknown={unknown} ms={clock.ElapsedMilliseconds}"); }
        catch (Exception) { }
        return (same, differ, behind, unknown);
    }

    // The server waits this long at most for the events under way before it answers a sync (ConversationHistory.SettleWait, 3 s); an answer slower than this
    // one may have come without them, so it settles no event.
    private static readonly TimeSpan SettleTrust = TimeSpan.FromSeconds(2.5);

    /// <summary>The most messages one copy holds; older pages past it are read from the server as before. Settable for the checks.</summary>
    public int MaxMessages { get; set; } = 1000;

    /// <summary>Whether the server keeps the whole history this way; below 24 every read goes to the server as before.</summary>
    public bool Enabled => client.Version >= ProtocolVersion.WholeHistory && Volatile.Read(ref unsupportedOn) != client.ConnectionNumber;

    // The connection whose host answered that it keeps no whole history (the app's own in-process host, a Windows server without one): nothing is asked of
    // it again, and its reads go as they always did.
    private int unsupportedOn = -1;

    /// <summary>Reads answered from a copy, reads that asked the server for changes, and reads left to the server (for the checks and the count line).</summary>
    public (int Local, int Synced, int Forwarded) Counts { get { lock (gate) return (local, synced, forwarded); } }

    private sealed class Copy
    {
        public List<ChatMessage> Messages = [];
        public string Revision = "";
        public bool MoreOlder;
        // Took a sync's answer in this run of the feed and has followed every change since.
        public bool Current;
        // Realtime events that named it, counted, and how many of them a finished sync has answered for (those counted before it was asked).
        public long Marks, MarksSettled;
        public long Used;
        // The sync in flight: every read that needs one waits on it rather than ask again, and the changes read meanwhile wait for its answer.
        public Task<bool>? Sync;
        public long SyncMarks;
        public List<HistoryChange>? Held;
        // While an older page is being read for it: what changes said of messages below its oldest, which it cannot apply itself (null for one removed),
        // so the page is brought up to them before it is kept.
        public int Reading;
        public Dictionary<string, ChatMessage?>? Under;
        public bool Settled => Current && Marks == MarksSettled && Sync is null;
    }

    /// <summary>The newest page of a conversation from its copy once a sync has brought it up to date; null leaves the read to the server.</summary>
    public async Task<Page<ChatMessage>?> NewestAsync(string chatId, CancellationToken cancellationToken)
    {
        if (!Enabled) return null;
        var copy = await SyncedAsync(chatId, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (copy is null) { Count(ref forwarded); return null; }
            Count(ref synced);
            return Newest(copy);
        }
    }

    /// <summary>
    /// The page before a position the copy handed out (or the server did): from a copy known current where it holds a page of them, reading on from the
    /// server's copy (<c>history.older</c>) where it reaches its own oldest. Null leaves the read to the server.
    /// </summary>
    public async Task<Page<ChatMessage>?> OlderAsync(string chatId, string cursor, CancellationToken cancellationToken)
    {
        if (!Enabled || HistoryPosition.ReadCursor(cursor) is not { } before) return null;
        HistoryPosition oldest;
        List<ChatMessage> below;
        Copy reader;
        lock (gate)
        {
            if (!copies.TryGetValue(chatId, out var copy) || !Answers(copy) || copy.Messages.Count == 0) { Count(ref forwarded); return null; }
            copy.Used = ++clock;
            oldest = HistoryPosition.Of(copy.Messages[0]);
            // A position below what the copy holds was handed out by a read the copy has since let go of: the server's.
            if (before.CompareTo(oldest) < 0) { Count(ref forwarded); return null; }
            below = Before(copy.Messages, before);
            if (below.Count >= PageSize || !copy.MoreOlder)
            {
                Count(ref local);
                return Page(below.TakeLast(PageSize).ToList(), below.Count > PageSize || copy.MoreOlder);
            }
            // A full copy keeps no more: the rest is the server's to page, from where this one ends.
            if (copy.Messages.Count >= MaxMessages) { Count(ref forwarded); return null; }
            reader = copy;
            reader.Reading++;
            reader.Under ??= new(StringComparer.Ordinal);
        }
        // The copy runs out before a page: read on from its own oldest, which the server's copy continues without a gap.
        HistoryOlderResult? older;
        try { older = await ReadOlderAsync(chatId, oldest, cancellationToken).ConfigureAwait(false); }
        catch { lock (gate) Done(reader); throw; }
        lock (gate)
        {
            var under = Done(reader);
            if (older is null) { Count(ref forwarded); return null; }
            // As the changes made while it was read say, not as it was when read: one edited or reacted to, one removed, and one put in among these (a
            // message the server took in between two of them would otherwise never be read again: its change is past the copy's revision; review, 2026-10-30).
            var answered = older.Messages.Where(message => HistoryPosition.Of(message).CompareTo(oldest) < 0).ToList();
            var low = older.More && answered.Count > 0 ? answered.Select(HistoryPosition.Of).Min() : (HistoryPosition?)null;
            var added = under?.Values.OfType<ChatMessage>().Where(message => HistoryPosition.Of(message).CompareTo(oldest) < 0
                && (low is not { } floor || HistoryPosition.Of(message).CompareTo(floor) >= 0)) ?? [];
            var read = Sorted(answered.Select(message => under is not null && under.TryGetValue(message.Id, out var now) ? now : message).OfType<ChatMessage>()
                .Concat(added));
            // The copy's own rows as they are now, where it still ends where the read began.
            if (copies.GetValueOrDefault(chatId) is { Messages.Count: > 0 } same && ReferenceEquals(same, reader) && HistoryPosition.Of(same.Messages[0]).Equals(oldest))
                below = Before(same.Messages, before);
            var rows = read.Concat(below).ToList();
            // Kept (as much of it as the copy has room for, nearest its oldest first) only by the same copy, still current and still ending where the read
            // began; either way the page is the server's answer.
            if (copies.TryGetValue(chatId, out var copy) && ReferenceEquals(copy, reader) && Answers(copy) && copy.Messages.Count > 0 && HistoryPosition.Of(copy.Messages[0]).Equals(oldest)
                && MaxMessages - copy.Messages.Count is > 0 and var room && read.Count > 0)
            {
                var kept = read.TakeLast(room).ToList();
                copy.Messages = [.. kept, .. copy.Messages];
                copy.MoreOlder = kept.Count < read.Count || older.More;
            }
            Count(ref synced);
            return Page(rows.TakeLast(PageSize).ToList(), rows.Count > PageSize || older.More);
        }
    }

    /// <summary>A message from a copy known current that holds it; null leaves it to the server.</summary>
    public ChatMessage? Find(string chatId, string messageId)
    {
        if (!Enabled) return null;
        lock (gate)
        {
            if (!copies.TryGetValue(chatId, out var copy) || !Answers(copy)) return null;
            for (var i = copy.Messages.Count - 1; i >= 0; i--)
                if (copy.Messages[i].Id == messageId)
                {
                    copy.Used = ++clock;
                    Count(ref local);
                    return copy.Messages[i];
                }
            return null;
        }
    }

    /// <summary>
    /// A copy known current as a whole, for the conversation view to draw from it alone (stage 9, B3): its messages oldest first, whether more lie before them,
    /// and the page position to read on from. Null when no copy is current.
    /// </summary>
    public (IReadOnlyList<ChatMessage> Messages, bool MoreOlder, string? Cursor)? Snapshot(string chatId)
    {
        if (!Enabled) return null;
        lock (gate)
        {
            if (!copies.TryGetValue(chatId, out var copy) || !Answers(copy) || copy.Messages.Count == 0) return null;
            return (copy.Messages.ToArray(), copy.MoreOlder, copy.MoreOlder ? HistoryPosition.Cursor(copy.Messages[0]) : null);
        }
    }

    /// <summary>The held rows after a moment (<c>history.held</c>) from a copy known current; null leaves them to the server.</summary>
    public HeldHistory? Held(string chatId, DateTimeOffset after, int limit)
    {
        if (!Enabled || limit <= 0) return null;
        lock (gate)
        {
            if (!copies.TryGetValue(chatId, out var copy) || !Answers(copy)) return null;
            copy.Used = ++clock;
            var rows = copy.Messages.Where(message => message.Timestamp > after).ToList();
            // Whole after the moment when the copy reaches back to it (or to the conversation's start) and every row after it is here.
            var reaches = !copy.MoreOlder || copy.Messages.Count > 0 && copy.Messages[0].Timestamp <= after;
            Count(ref local);
            return new HeldHistory(rows.TakeLast(limit).ToList(), reaches && rows.Count <= limit);
        }
    }

    // ---- Keeping the copies ----

    // An older read done: what changes said below the copy meanwhile, let go of once no other older read is out. Caller holds the gate.
    private static Dictionary<string, ChatMessage?>? Done(Copy copy)
    {
        var under = copy.Under;
        if (--copy.Reading <= 0) (copy.Reading, copy.Under) = (0, null);
        return under;
    }

    // What a change says of messages below the copy's oldest, for an older read under way. Caller holds the gate.
    private static void Note(Copy copy, IReadOnlyList<ChatMessage> changed, IReadOnlyList<string> removed)
    {
        if (copy.Under is not { } under) return;
        var floor = copy.Messages.Count > 0 ? HistoryPosition.Of(copy.Messages[0]) : (HistoryPosition?)null;
        foreach (var message in changed) if (floor is not { } at || HistoryPosition.Of(message).CompareTo(at) < 0) under[message.Id] = message;
        foreach (var id in removed) under[id] = null;
    }

    // A copy answers by itself only while the feed runs: between a new connection and its subscription nothing would tell it what changed.
    private bool Answers(Copy copy) => copy.Settled && client.HasEventFeed;

    // The copy once a sync asked no earlier than every event this read has seen has answered (one already running serves if no event came after it was
    // asked; otherwise this read asks again once it is done); null when the server could not say.
    private async Task<Copy?> SyncedAsync(string chatId, CancellationToken cancellationToken)
    {
        long wanted = -1;
        for (var round = 0; round < 3; round++)
        {
            Task<bool> waiting;
            TaskCompletionSource<bool>? mine = null;
            string? since = null;
            long asked, run = 0;
            lock (gate)
            {
                var copy = Take(chatId);
                if (wanted < 0) wanted = copy.Marks;
                if (copy.Sync is { } running) (waiting, asked) = (running, copy.SyncMarks);
                else
                {
                    mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    copy.Sync = waiting = mine.Task;
                    since = copy.Messages.Count > 0 || copy.Current ? copy.Revision : null;
                    if (string.IsNullOrEmpty(since)) since = null;
                    copy.SyncMarks = asked = copy.Marks;
                    run = generation;
                }
            }
            if (mine is not null) _ = SyncAsync(chatId, since, asked, run, mine);
            if (!await waiting.WaitAsync(cancellationToken).ConfigureAwait(false)) return null;
            lock (gate)
            {
                // Its answer is as new as a page read when it was asked; a resync since (not current) or an event this read saw first means asking again.
                if (asked >= wanted && copies.TryGetValue(chatId, out var copy) && copy.Current) return copy;
            }
        }
        return null;
    }

    // One sync of a copy, shared by every read waiting on it: a read given up does not give it up for the others.
    private async Task SyncAsync(string chatId, string? since, long marks, long run, TaskCompletionSource<bool> done)
    {
        HistorySyncResult? result = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await client.CallAsync<HistorySyncParams, HistorySyncResult>(ProtocolMethods.HistorySync, new(chatId, since, PageSize)).ConfigureAwait(false);
        }
        catch (NotSupportedException) { Volatile.Write(ref unsupportedOn, client.ConnectionNumber); }
        catch (Exception) { }
        finally
        {
            lock (gate)
            {
                if (copies.TryGetValue(chatId, out var copy) && ReferenceEquals(copy.Sync, done.Task))
                {
                    copy.Sync = null;
                    var waiting = copy.Held;
                    copy.Held = null;
                    if (result is null || !Take(copy, result, since)) copy.Current = false;
                    else
                    {
                        // Current only if the feed ran without a gap since it was asked: an answer from before a resync may lack what the gap held.
                        copy.Current = run == generation;
                        if (elapsed.Elapsed < SettleTrust) copy.MarksSettled = Math.Max(copy.MarksSettled, marks);
                    }
                    foreach (var change in waiting ?? []) Apply(copy, change);
                    Hollow(copy);
                }
            }
            done.TrySetResult(result is not null);
        }
    }

    // A sync's answer into its copy. A reset replaces it; what changed since a revision patches the copy only while it is still at that revision (the copy
    // takes no change while a sync is out, so it is, unless it was let go and made again). Caller holds the gate.
    private bool Take(Copy copy, HistorySyncResult result, string? since)
    {
        if (result.Reset || since is null)
        {
            copy.Messages = Sorted(result.Messages);
            copy.MoreOlder = result.MoreOlder;
        }
        else if (copy.Revision == since)
        {
            Note(copy, result.Messages, result.Removed);
            copy.Messages = Applied(copy.Messages, result.Messages, result.Removed);
        }
        else return false;
        Trim(copy);
        copy.Revision = result.Revision;
        return true;
    }

    private void Changed(HistoryChange change)
    {
        lock (gate)
        {
            if (!copies.TryGetValue(change.ChatId, out var copy)) return;
            // Asked meanwhile: it waits for the answer, which says where it begins. An older read under way learns of it now, not after it has answered.
            if (copy.Sync is not null)
            {
                Note(copy, change.Messages, change.Removed);
                (copy.Held ??= []).Add(change);
                return;
            }
            Apply(copy, change);
        }
    }

    // A change carries the server's messages, so the copy follows it; it never settles an event, which only a sync asked after it does. Caller holds the gate.
    private void Apply(Copy copy, HistoryChange change)
    {
        if (copy.Revision == change.To) return;
        if (copy.Current && copy.Revision == change.From && !change.Reset && !change.Truncated)
        {
            Note(copy, change.Messages, change.Removed);
            copy.Messages = Applied(copy.Messages, change.Messages, change.Removed);
            Trim(copy);
            copy.Revision = change.To;
            applied++;
            Hollow(copy);
            return;
        }
        // A change it cannot follow from where it is: it asks for what it missed at its next read.
        if (Seq(change.To) > Seq(copy.Revision)) { copy.Current = false; missed++; }
    }

    // A realtime event that may change what a copy holds marks it until a sync answers for it: one naming the conversation or its messages, and a service's
    // own reconnect or resync, after which the server reads its conversations again and may find what the copy lacks.
    private void Named(RealtimeEvent change)
    {
        if (change.Kind is RealtimeKind.AuthenticationRequired || change.ClientLink == true) return;
        lock (gate)
        {
            foreach (var (chatId, copy) in copies)
                if (change.Affects(chatId)) copy.Marks++;
        }
    }

    // Every message it held went (removed on the service) while more lie before them: nothing is left to read on from, so it starts again from the
    // newest rather than serve an empty page that says there is more (review, 2026-10-30). Caller holds the gate.
    private static void Hollow(Copy copy)
    {
        if (copy.Messages.Count > 0 || !copy.MoreOlder) return;
        copy.Current = false;
        copy.Revision = "";
    }

    private void Unsettle()
    {
        lock (gate)
        {
            generation++;
            foreach (var copy in copies.Values) copy.Current = false;
        }
    }

    // The copy for a conversation, made if there is none and the one used longest ago let go past the capacity. Caller holds the gate.
    private Copy Take(string chatId)
    {
        if (copies.TryGetValue(chatId, out var copy)) { copy.Used = ++clock; return copy; }
        if (copies.Count >= Capacity)
        {
            var oldest = copies.Where(pair => pair.Value.Sync is null).MinBy(pair => pair.Value.Used);
            if (oldest.Key is not null) copies.Remove(oldest.Key);
        }
        copy = new Copy { Used = ++clock };
        copies[chatId] = copy;
        return copy;
    }

    private async Task<HistoryOlderResult?> ReadOlderAsync(string chatId, HistoryPosition oldest, CancellationToken cancellationToken)
    {
        try
        {
            return await client.CallAsync<HistoryOlderParams, HistoryOlderResult>(ProtocolMethods.HistoryOlder,
                new(chatId, new DateTimeOffset(oldest.Ticks, TimeSpan.Zero), oldest.SortKey, oldest.Id, PageSize), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    private static List<ChatMessage> Before(List<ChatMessage> messages, HistoryPosition before) =>
        messages.TakeWhile(message => HistoryPosition.Of(message).CompareTo(before) < 0).ToList();

    private static Page<ChatMessage> Newest(Copy copy) =>
        Page(copy.Messages.TakeLast(PageSize).ToList(), copy.Messages.Count > PageSize || copy.MoreOlder);

    private static Page<ChatMessage> Page(List<ChatMessage> rows, bool more) =>
        new(rows, more, more && rows.Count > 0 ? HistoryPosition.Cursor(rows[0]) : null, null);

    private void Trim(Copy copy)
    {
        if (copy.Messages.Count <= MaxMessages) return;
        copy.Messages = copy.Messages.TakeLast(MaxMessages).ToList();
        copy.MoreOlder = true;
    }

    // Changed messages put in by id (a new one in order) and removed ones taken out; one older than the oldest held is left to the page that holds it.
    private static List<ChatMessage> Applied(List<ChatMessage> messages, IReadOnlyList<ChatMessage> changed, IReadOnlyList<string> removed)
    {
        if (changed.Count == 0 && removed.Count == 0) return messages;
        var gone = removed.ToHashSet(StringComparer.Ordinal);
        var oldest = messages.Count > 0 ? HistoryPosition.Of(messages[0]) : (HistoryPosition?)null;
        var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var message in messages) if (!gone.Contains(message.Id)) byId[message.Id] = message;
        foreach (var message in changed)
        {
            if (gone.Contains(message.Id)) continue;
            if (!byId.ContainsKey(message.Id) && oldest is { } floor && HistoryPosition.Of(message).CompareTo(floor) < 0) continue;
            byId[message.Id] = message;
        }
        return Sorted(byId.Values);
    }

    private static List<ChatMessage> Sorted(IEnumerable<ChatMessage> messages) =>
        messages.DistinctBy(message => message.Id).OrderBy(HistoryPosition.Of).ToList();

    private static long Seq(string revision) =>
        revision.LastIndexOf('.') is > 0 and var dot && long.TryParse(revision.AsSpan(dot + 1), out var seq) ? seq : -1;

    // Every 200 reads one line of counts, so a log says how many reads the copies took off the network. Caller holds the gate.
    private void Count(ref int counter)
    {
        counter++;
        if ((local + synced + forwarded) % 200 != 0) return;
        try { log?.Invoke($"history copies local={local} synced={synced} forwarded={forwarded} applied={applied} missed={missed} held={copies.Count}"); }
        catch (Exception) { }
    }
}
