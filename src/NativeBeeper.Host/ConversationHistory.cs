using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Storage;
using Position = NativeBeeper.Protocol.HistoryPosition;

namespace NativeBeeper.Host;

/// <summary>
/// The server's whole copy of every conversation (SWITCHBOARD.md stage 8). Each conversation is held from its newest message down to a floor with no gap
/// between, filled from the service's newest page and kept up from the providers' events, which this sees first (<see cref="Over"/>). Every change takes the
/// next number of one sequence (<see cref="HistoryDatabase"/>), and a client asks only for what changed after the number it holds (<see cref="SyncAsync"/>), so
/// its copy is whole by construction. The same copy answers the reads every client version already makes (<c>messages.page</c>, <c>history.held</c>,
/// <c>messages.resolve</c>, the peeks): this stands in front of the router as each of those ports, and passes on whatever it does not hold.
/// </summary>
/// <remarks>
/// <para>A conversation's state changes only once what it says is written: a read cancelled or failed half way leaves it as it was. What it holds is known
/// whole up to <see cref="State.Verified"/>, which only a page read, or an event while nothing was missed, moves. After a provider reconnects (or an event's
/// messages could not all be read) its conversations are suspect: events still write, but only a read of the newest page that reaches back to the verified
/// position (within <see cref="MaxCatchUpPages"/> pages) makes it whole again; one that cannot starts the conversation over, which tells every copy of it to
/// replace itself. A held message missing from inside a page that spans it went on the service (an Instagram unsend made elsewhere shows only so).</para>
/// <para>Events never wait here: each conversation's reads and writes run in order on a chain of its own beside the feed, and a resolve asked meanwhile waits
/// for the same read. Reads nobody asked for are paced per service, and Instagram's come one page at a time out of its hourly budget, only while half of it is
/// left for arrivals. Logs counts, states and one-way tags only.</para>
/// </remarks>
internal sealed partial class ConversationHistory : IWholeHistory, IChatReader, ILinkPreviewLoader, IMessageSender, IMessagePeek, IHistoryWindowReader, IMessageActions,
    IMessageMutations, IDisposable
{
    /// <summary>The most pages a refresh reads back looking for what it holds before it starts the conversation over.</summary>
    public const int MaxCatchUpPages = 10;
    /// <summary>The most pages a read further back than the floor takes from the service for one answer.</summary>
    public const int MaxOlderPages = 8;
    /// <summary>The conversations a reconnect reads again at once (the most recently active); the rest are read again when opened.</summary>
    public const int CatchUpChats = 100;
    /// <summary>The most messages one event is read for; an event naming more makes the conversation read again whole.</summary>
    public const int MaxEventIds = 64;
    private const string InstagramPrefix = "instagram:";

    private readonly HistoryDatabase db;
    private readonly IChatReader chats;
    private readonly IMessageSender sender;
    private readonly IMessagePeek? peek;
    private readonly IHistoryWindowReader? windows;
    private readonly IMessageActions? actions;
    private readonly IMessageMutations? mutations;
    private readonly Action<string> log;
    private readonly Func<string, bool> mayReadUnasked;
    private readonly ConcurrentDictionary<string, Held> held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ChatMessage?>> resolving = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> scopeResyncs = new(StringComparer.Ordinal);
    private readonly WorkQueue queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EventLog? events;
    private Task? worker;
    private Task? walker;
    private Task? rower;
    // The services' own list rows as last read, which the served rows are built from (stage 9, B2).
    private readonly ConcurrentDictionary<string, ChatSummary> listed = new(StringComparer.Ordinal);
    // Rows to build again soon: true to read the service's row first, false to build again from the one held.
    private readonly ConcurrentDictionary<string, bool> rowsDue = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim rowsSignal = new(0);
    private volatile bool listDue;
    // The first whole read of the list is in the store: a feed that starts a client over waits for it (a little).
    private readonly TaskCompletionSource rowsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The mirror check (stage 9, A2): the round under way per service, which round each conversation it asked for belongs to, and the newest finished.
    private readonly object mirrorGate = new();
    private readonly Dictionary<string, MirrorRound> rounds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MirrorRound> mirroring = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistoryMirrorReport> reports = new(StringComparer.Ordinal);

    /// <param name="mayReadUnasked">Asked before each service read nobody asked for (filling or catching up a conversation in the background): false defers
    /// it. Instagram's answers from its background budget.</param>
    public ConversationHistory(HistoryDatabase db, IChatReader chats, IMessageSender sender, IMessagePeek? peek, IHistoryWindowReader? windows,
        IMessageActions? actions, IMessageMutations? mutations, Action<string> log, Func<string, bool>? mayReadUnasked = null)
    {
        this.db = db; this.chats = chats; this.sender = sender; this.peek = peek; this.windows = windows; this.actions = actions; this.mutations = mutations;
        this.log = log; this.mayReadUnasked = mayReadUnasked ?? (_ => true);
    }

    /// <summary>Between two reads of one service that nobody asked for.</summary>
    // A second: Messenger's reads of conversations not on the device go to Facebook, and nobody is waiting on these.
    public TimeSpan Pace { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long after starting the first walk of the list begins, and how often it repeats.</summary>
    public TimeSpan WalkDelay { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan WalkEvery { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>
    /// How many pages of the list a routine read takes, between each the wait below. Reading all of it each walk was 200 pages every half hour of Google Voice's call records
    /// alone, about 9,600 calls a day to a real account, unpaced, for rows that almost never change (audit, 2026-11-01). The newest pages are where a list changes, and a read that
    /// stops short is never taken for the whole list, so it drops nothing.
    /// </summary>
    public int ListPages { get; init; } = 10;
    /// <summary>How far the read goes once a day, for the rows below those: read in stretches of <see cref="ListPages"/>, so rows waiting to be written are not held behind it.</summary>
    public int DeepListPages { get; init; } = 200;
    public TimeSpan DeepListEvery { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan ListPagePace { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>A conversation opened after this long without a read of its newest page has it read again behind the answer.</summary>
    public TimeSpan OpenRefreshAfter { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// The most stale conversations a minute that syncs have read again. A sync is how a client of 24 opens a conversation, but also how a phone catches up
    /// on every copy it keeps: after a restart (every conversation suspect) one catch-up's 1,700 syncs queued 500 reads of the services (2026-10-30).
    /// </summary>
    public int SyncRefreshesPerMinute { get; init; } = 6;
    private readonly object syncRefreshGate = new();
    private long syncRefreshMinute;
    private int syncRefreshes;
    /// <summary>When a change made through this host is read back, in case its service sends no event for it.</summary>
    public IReadOnlyList<TimeSpan> ConfirmAfter { get; init; } = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6)];
    /// <summary>A held message this young is not taken as gone for missing from a page: the service's page may not have it yet.</summary>
    public TimeSpan AbsenceGrace { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a background read put off (its page partial, the budget spent) waits before it is tried again.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The longest a resolve waits for an event's own read of the same message before it asks the service itself.</summary>
    public TimeSpan ResolveWait { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>The longest a page, a sync or a held read waits for the conversation's events already under way to be written.</summary>
    public TimeSpan SettleWait { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long after an event its conversation's list row is built again, so a burst builds it once.</summary>
    public TimeSpan RowsDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The longest a feed that starts a client over waits for the first whole read of the list.</summary>
    public TimeSpan RowsReadyWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// The most recently active conversations of each service a mirror check compares with the service. Each is one read of its newest page, a second apart
    /// per service (owner, 2026-10-30: the walk's pace for Google, Messenger and Voice; Instagram only within its budget).
    /// </summary>
    public int MirrorPerService { get; init; } = 25;
    /// <summary>A mirror check still waiting on reads after this long is reported with those counted as not read.</summary>
    public TimeSpan MirrorTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Opens the store and starts the background work.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.GetTimestamp();
            try
            {
                var rows = await db.OpenAsync(stop.Token).ConfigureAwait(false);
                foreach (var row in rows) held[row.ChatId] = Held.From(row);
                log($"history store open chats={rows.Count} filled={held.Values.Count(h => h.State.Filled)} seq={db.Seq} bytes={db.Bytes} ms={Ms(clock)}");
                ready.TrySetResult();
                worker = Task.Run(WorkAsync, CancellationToken.None);
                walker = Task.Run(WalkAsync, CancellationToken.None);
                // The list rows at once (the walk's fills wait their 20 s): a client that starts over is handed the list from them.
                listDue = true;
                rower = Task.Run(RowsAsync, CancellationToken.None);
                rowsSignal.Release();
            }
            catch (Exception failure)
            {
                // Served without a history: every read goes on to the services, as before stage 8.
                log($"history store open failed error={failure.GetType().Name}");
                ready.TrySetException(failure);
                rowsReady.TrySetResult();
            }
        });
    }

    /// <summary>The store is open (for the checks).</summary>
    internal Task Opened => ready.Task;

    /// <summary>Conversations waiting for a background read (for the checks).</summary>
    internal int Queued => queue.Count;

    /// <summary>Every conversation's event work done so far has finished (for the checks).</summary>
    internal Task SettledAsync() => Task.WhenAll(held.Values.Select(chat => chat.Tail));

    /// <summary>The log the history changes go into.</summary>
    public void Attach(EventLog log) => events = log;

    /// <summary>The providers' feed, each event's messages read into the copy beside it.</summary>
    public IRealtimeFeed Over(IRealtimeFeed source) => new ObservedFeed(this, source);

    /// <summary>
    /// An account was signed out, removed from the server, or Google's local data was reset (<c>ProtocolServices.AccountEnded</c>): its conversations' copies
    /// go. Never waits.
    /// </summary>
    public void Ended(string account, string? key)
    {
        if (account == "account-slot" && NativeBeeper.Core.Messaging.AccountNamespace.ValidKey(key))
        {
            foreach (var service in new[] { "google-direct:", "messenger:", "instagram:" }) Ended(new NativeBeeper.Core.Messaging.AccountNamespace(key).Qualify(service), null);
            return;
        }
        var prefix = account switch
        {
            "messenger" => "messenger:",
            "instagram" => InstagramPrefix,
            "voice" when !string.IsNullOrEmpty(key) => NativeBeeper.App.Services.GoogleVoice.GoogleVoiceModels.Account(key) + ":",
            "google-reset" => "google-direct:",
            _ => NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(account) is not null ? account : null,
        };
        if (prefix is null) return;
        bool Matches(string id) => id.StartsWith(prefix, StringComparison.Ordinal) && (NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(prefix) is not null || NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(id) is null);
        _ = Task.Run(async () =>
        {
            try
            {
                await ready.Task.ConfigureAwait(false);
                var forgotten = await ForgetAsync(held.Keys.Where(id => Matches(id)).ToArray()).ConfigureAwait(false);
                // Its list rows leave the feed too, the conversations never filled among them.
                var rows = (await db.ReadListedAsync(stop.Token).ConfigureAwait(false)).Where(id => Matches(id)).ToArray();
                foreach (var id in rows) listed.TryRemove(id, out _);
                var gone = await db.WriteRowsAsync([.. rows.Select(id => new HistoryListRow(id, null))], stop.Token).ConfigureAwait(false);
                log($"history store forgotten account={account} chats={forgotten} rows={gone.Count}");
            }
            catch (Exception failure) when (!stop.IsCancellationRequested) { log($"history store forget failed error={failure.GetType().Name}"); }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        queue.Complete();
        try { Task.WaitAll([.. new[] { worker, walker, rower }.OfType<Task>()], TimeSpan.FromSeconds(2)); } catch (Exception) { }
        db.Dispose();
    }

    // ---- IWholeHistory (protocol 24) ----

    public async Task<HistorySyncResult> SyncAsync(HistorySyncParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var chat = await FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        // Not under Gate, which a read of the service holds for as long as that takes (a page waits at most SettleWait for the events under way, and so does
        // this). The numbers come first, so what is read after them is at least that new: a change written meanwhile comes again from that revision in the
        // feed, and a reset meanwhile arrives as a reset, which a copy cannot patch and so asks again.
        long seq, resetSeq;
        lock (chat.Numbers) (seq, resetSeq) = (chat.Seq, chat.ResetSeq);
        var state = chat.State;
        var since = Parse(request.Since);
        var revision = Revision(seq);
        HistorySyncResult? answer = null;
        if (since is { } at && at <= db.Seq && at >= resetSeq)
        {
            var changed = await db.ReadChangedAsync(chat.ChatId, at, WholeHistoryLimits.MaxChanges + 1, cancellationToken).ConfigureAwait(false);
            if (changed.Count <= WholeHistoryLimits.MaxChanges)
            {
                var messages = changed.Where(row => !row.Removed).Select(Message).OfType<ChatMessage>().ToArray();
                var removed = changed.Where(row => row.Removed).Select(row => row.MessageId).ToArray();
                // A reset written while these were read took the messages it replaced away with no row to say so: what was read is no patch then, and the
                // answer starts the copy over instead (review, 2026-10-30).
                bool crossed;
                lock (chat.Numbers) crossed = chat.ResetSeq != resetSeq;
                if (!crossed) answer = new(revision, false, messages, removed, MoreOlder: true, state.Complete);
            }
        }
        if (answer is null)
        {
            // Numbers first again: what is read after them is at least as new.
            lock (chat.Numbers) seq = chat.Seq;
            state = chat.State;
            var newest = await NewestAsync(chat.ChatId, state, limit, cancellationToken).ConfigureAwait(false);
            answer = new(Revision(seq), true, newest.Messages, [], newest.More, state.Complete);
        }
        // A sync is how a client of 24 opens a conversation (and reads its newest after an event), so it is read again behind the answer when stale, as a
        // newest page is: otherwise a conversation the service changed without an event stays as it was until the walk. Queued after the answer, so the
        // read cannot take Gate ahead of the events this sync waited for. A sync cannot say it is an open rather than a catch-up, so these are few a minute,
        // and never Instagram's: a read nobody asked for is not one to make there.
        if (Stale(chat) && !request.ChatId.StartsWith(InstagramPrefix, StringComparison.Ordinal) && TakeSyncRefresh()) Enqueue(request.ChatId, Work.Open);
        return answer;
    }

    public async Task<HistoryOlderResult> OlderAsync(HistoryOlderParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var chat = await FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        var page = await OlderPageAsync(chat, new Position(request.BeforeTimestamp.UtcTicks, request.BeforeSortKey ?? "", request.BeforeId ?? ""), limit, cancellationToken)
            .ConfigureAwait(false);
        return new(page.Messages, page.More, chat.State.Complete);
    }

    public async Task<HistoryChangesResult> ChangesAsync(HistoryChangesParams request, CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Parse(request.Since) is not { } since || since > db.Seq) return new(Revision(db.Seq), true, []);
        // The store's number and the conversations' numbers read together, so the revision handed out never passes a change it does not name.
        var (seq, changed) = await db.ReadChangedChatsAsync(since, WholeHistoryLimits.MaxChangedChats + 1, cancellationToken).ConfigureAwait(false);
        if (changed.Count > WholeHistoryLimits.MaxChangedChats) return new(Revision(seq), true, []);
        return new(Revision(seq), false, changed.Select(row => new HistoryChatRevision(row.ChatId, Revision(row.Seq))).ToArray());
    }

    // ---- Protocol 25: the one ordered feed, and the copy checks (stage 9) ----

    private const string RowsPage = "rows:";

    public async Task<HistoryFeedResult> FeedAsync(HistoryFeedParams request, CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultFeed, 1, WholeHistoryLimits.MaxFeed);
        var clock = Stopwatch.GetTimestamp();
        // A start-over carrying on: list rows alone, from where its last answer ended.
        if (request.Page is { } page)
        {
            if (!page.StartsWith(RowsPage, StringComparison.Ordinal)) throw new ArgumentException("Invalid feed page.");
            // One handed out before the store started over (another epoch): the start-over begins again.
            if (Parse(page[RowsPage.Length..]) is not { } from) return await RowsFromAsync(0, reset: true, limit, clock, cancellationToken).ConfigureAwait(false);
            return await RowsFromAsync(from, reset: false, limit, clock, cancellationToken).ConfigureAwait(false);
        }
        // Nothing held, or held under another epoch: the whole list, and every copy read again when opened. Messages are not carried (every one held would
        // come after 0); a copy is filled when its conversation is opened.
        if (Parse(request.Since) is not { } since || since > db.Seq)
        {
            try { await rowsReady.Task.WaitAsync(RowsReadyWait, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            return await RowsFromAsync(0, reset: true, limit, clock, cancellationToken).ConfigureAwait(false);
        }
        await FeedSettledAsync(cancellationToken).ConfigureAwait(false);
        var read = await db.ReadFeedAsync(since, limit, WholeHistoryLimits.MaxFeedBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = new List<HistoryChange>(read.Chats.Count);
        foreach (var chat in read.Chats)
        {
            var messages = chat.Messages.Where(row => !row.Removed).Select(Message).OfType<ChatMessage>().ToArray();
            var removed = chat.Messages.Where(row => row.Removed).Select(row => row.MessageId).ToArray();
            changes.Add(new(chat.ChatId, Revision(chat.From), Revision(chat.To), messages, removed));
        }
        var (rows, gone) = Rows(read.Rows);
        log($"history feed reset=0 rows={rows.Count} gone={gone.Count} chats={changes.Count} messages={read.Chats.Sum(chat => chat.Messages.Count)} more={(read.More ? 1 : 0)} ms={Ms(clock)}");
        return new(Revision(read.Through), false, rows, gone, changes, read.More);
    }

    private async Task<HistoryFeedResult> RowsFromAsync(long from, bool reset, int limit, long clock, CancellationToken cancellationToken)
    {
        var read = await db.ReadFeedAsync(from, limit, WholeHistoryLimits.MaxFeedBytes, rowsOnly: true, cancellationToken).ConfigureAwait(false);
        var (rows, gone) = Rows(read.Rows);
        log($"history feed reset=1 rows={rows.Count} gone={gone.Count} more={(read.More ? 1 : 0)} ms={Ms(clock)}");
        // Rows that left the list are nothing to a list being replaced, but one carrying on still needs them.
        return new(Revision(read.Through), reset, rows, reset ? [] : gone, [], false, read.More ? RowsPage + Revision(read.Through) : null);
    }

    private static (IReadOnlyList<ChatSummary> Rows, IReadOnlyList<string> Gone) Rows(IReadOnlyList<HistoryFeedRow> read)
    {
        var rows = new List<ChatSummary>();
        var gone = new List<string>();
        foreach (var row in read)
        {
            if (row.Payload is null) { gone.Add(row.ChatId); continue; }
            try { if (JsonSerializer.Deserialize<ChatSummary>(row.Payload, ProtocolJson.Options) is { } summary) rows.Add(summary); }
            catch (JsonException) { }
        }
        return (rows, gone);
    }

    public async Task<HistoryVerifyResult> VerifyAsync(HistoryVerifyParams request, CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (request.Chats.Count > WholeHistoryLimits.MaxVerify) throw new ArgumentException("Too many copies to compare.");
        var answers = new List<HistoryCopyCheck>(request.Chats.Count);
        int same = 0, differ = 0, behind = 0, unknown = 0;
        foreach (var copy in request.Chats)
        {
            var outcome = HistoryCopyOutcomes.Unknown;
            var count = 0;
            if (held.TryGetValue(copy.ChatId, out var chat) && chat.State is { Filled: true } state && Parse(copy.Revision) is { } revision && copy.Count >= 0)
            {
                long seq;
                lock (chat.Numbers) seq = chat.Seq;
                var oldest = new Position(copy.OldestTimestamp.UtcTicks, copy.OldestSortKey ?? "", copy.OldestId ?? "");
                if (seq > revision) outcome = HistoryCopyOutcomes.Behind;
                else if (state.Floor is { } floor && oldest.CompareTo(floor) < 0) outcome = HistoryCopyOutcomes.Unknown;
                else
                {
                    var rows = await db.ReadAtOrAfterAsync(copy.ChatId, copy.OldestTimestamp.ToUnixTimeMilliseconds(), copy.Count + 256, cancellationToken).ConfigureAwait(false);
                    var span = rows.Select(Message).OfType<ChatMessage>().Where(message => Position.Of(message).CompareTo(oldest) >= 0).ToArray();
                    count = span.Length;
                    // Read while it may have changed: compared only if the conversation is still where the copy is.
                    lock (chat.Numbers) seq = chat.Seq;
                    // Far more held over the copy's span than it has is a difference too.
                    outcome = seq > revision ? HistoryCopyOutcomes.Behind
                        : rows.Count >= copy.Count + 256 ? HistoryCopyOutcomes.Differ
                        : HistoryDigest.Of(span) == copy.Digest ? HistoryCopyOutcomes.Same : HistoryCopyOutcomes.Differ;
                }
            }
            switch (outcome)
            {
                case HistoryCopyOutcomes.Same: same++; break;
                case HistoryCopyOutcomes.Differ: differ++; break;
                case HistoryCopyOutcomes.Behind: behind++; break;
                default: unknown++; break;
            }
            answers.Add(new(copy.ChatId, outcome, count));
        }
        log($"history verify chats={answers.Count} same={same} differ={differ} behind={behind} unknown={unknown}");
        return new(answers);
    }

    public async Task<HistoryMirrorResult> MirrorAsync(HistoryMirrorParams request, CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (request.Start)
        {
            // From the list as last read; a server that has not read it yet reads it now.
            var rows = listed.IsEmpty ? await ReadListAsync(stop.Token).ConfigureAwait(false) ?? [] : listed.Values.ToArray();
            StartMirror(rows, "asked");
        }
        lock (mirrorGate)
        {
            SweepRounds();
            return new(rounds.Count > 0, reports.Values.OrderBy(report => report.Service, StringComparer.Ordinal).ToArray());
        }
    }

    // ---- IChatReader: the list as the services give it, each row's last message from the copy; a conversation's pages from the copy ----

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var page = await chats.GetChatsAsync(olderThan, cancellationToken).ConfigureAwait(false);
        if (!ready.Task.IsCompletedSuccessfully) return page;
        var built = new ChatSummary[page.Items.Count];
        for (var i = 0; i < built.Length; i++)
        {
            listed[page.Items[i].Id] = page.Items[i];
            built[i] = await BuildRowAsync(page.Items[i], cancellationToken).ConfigureAwait(false);
        }
        return page with { Items = built };
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var row = await chats.GetChatAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!ready.Task.IsCompletedSuccessfully) return row;
        listed[chatId] = row;
        // What a client was just told, the feed says too.
        RowSoon(chatId, read: false);
        return await BuildRowAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // A cursor of the service's (handed out before this copy held the conversation) goes on to it, as before.
        if (olderThan is not null && !HistoryPosition.IsCursor(olderThan))
            return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false)) return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        var chat = Chat(chatId);
        var clock = Stopwatch.GetTimestamp();
        if (!chat.State.Filled)
        {
            var (page, _) = await RefreshAsync(chat, Reason.Open, cancellationToken).ConfigureAwait(false);
            // A page the service is still loading (or one that came back partial) is handed on as it is, and the copy waits for a whole one.
            if (!chat.State.Filled) return page ?? await chats.GetMessagesAsync(chatId, null, cancellationToken).ConfigureAwait(false);
        }
        else if (olderThan is null && Stale(chat)) Enqueue(chatId, Work.Open);
        if (olderThan is null) await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        var state = chat.State;
        (IReadOnlyList<ChatMessage> Messages, bool More) result = olderThan is null
            ? await NewestAsync(chatId, state, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : ReadCursor(olderThan) is { } before ? await OlderPageAsync(chat, before, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : throw new ArgumentException("Invalid page position.");
        log($"history serve method=page chat={Tag(chatId)} older={(olderThan is not null ? 1 : 0)} rows={result.Messages.Count} more={(result.More ? 1 : 0)} ms={Ms(clock)}");
        return new(result.Messages, result.More, result.More && result.Messages.Count > 0 ? Cursor(result.Messages[0]) : null, null);
    }

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        chats is ILinkPreviewLoader loader ? loader.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : throw new NotSupportedException();

    // ---- IMessageSender: sends go on; a resolve is the copy's when it holds the message ----

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default) => sender.GetSendPermissionAsync(chatId, cancellationToken);
    public async Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
    {
        var receipt = await sender.SendTextAsync(chatId, text, cancellationToken).ConfigureAwait(false);
        ReadBackSent(chatId);
        return receipt;
    }

    /// <summary>
    /// How long after a send through this host to an Instagram conversation its newest page is read again. Instagram announces only what arrives, so a
    /// message sent from here reached no copy: a client drawing from the copy lost its own reply (2026-10-30). The other services announce their own sends.
    /// </summary>
    public TimeSpan SentReadBack { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A conversation a first message just made: filled now, as an open would (the person has just sent), rather than at the walk's next pass.</summary>
    public void Started(string chatId)
    {
        if (!ready.Task.IsCompletedSuccessfully) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SentReadBack, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            RowSoon(chatId, read: true);
            Enqueue(chatId, Work.Open);
        });
    }

    // Read as an open is, the person having just sent: one read of Instagram per send, and what it finds is announced as any find is. Files and scheduled
    // sends come here too (HistoryReadBack, stage 9).
    internal void ReadBackSent(string chatId)
    {
        if (!chatId.StartsWith(InstagramPrefix, StringComparison.Ordinal) || !held.TryGetValue(chatId, out var chat) || !chat.State.Filled) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SentReadBack, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Enqueue(chatId, Work.Open);
        });
    }

    // What is read here is never written from here: an answer can be a partial copy (Instagram's list words), and the copy takes messages only from pages
    // and the events' own reads.
    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        // The event that named it is being read for the copy: wait for that read rather than read the service twice or answer the copy from before it, but
        // never long (a push is waiting behind this): past ResolveWait the service is asked itself.
        if (resolving.TryGetValue(Key(chatId, messageId), out var pending))
        {
            try
            {
                if (await pending.WaitAsync(ResolveWait, cancellationToken).ConfigureAwait(false) is { } fresh) return fresh;
                return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is TimeoutException) { return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false); }
        }
        if (await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false) is { } kept) return kept;
        return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
    }

    // ---- IMessagePeek ----

    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    // The copy keeps only whole messages (pages, and event reads a whole copy stands behind), so what it holds is whole, and so is what an event's read brings.
    public async ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekWholeAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    public bool HoldsPartialCopies(string chatId) => peek?.HoldsPartialCopies(chatId) ?? false;

    // ---- IHistoryWindowReader ----

    public bool CanReadWindow(string chatId) => windows?.CanReadWindow(chatId) ?? false;

    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false) && held.TryGetValue(chatId, out var chat) && chat.State is { Filled: true } state
            && (state.Floor is null || at.UtcTicks > state.Floor.Value.Ticks))
        {
            var before = await HeldBeforeAsync(chatId, state, new Position(at.UtcTicks, "", ""), older + 1, cancellationToken, wholeMoments: true).ConfigureAwait(false);
            var after = await NewerAsync(chatId, state, at, newer, cancellationToken).ConfigureAwait(false);
            var selected = TakeMoments(before, older, newestFirst: true);
            var moreOlder = before.Count > selected.Count || !state.Complete;
            return new HistoryWindow([.. selected, .. after.Messages], moreOlder, after.More);
        }
        return windows is null ? throw new NotSupportedException() : await windows.ReadWindowAsync(chatId, at, older, newer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false) && held.TryGetValue(chatId, out var chat) && chat.State.Filled && limit > 0)
        {
            await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
            var state = chat.State;
            var rows = await NewerAsync(chatId, state, after.AddTicks(1), limit, cancellationToken, newestFirst: true).ConfigureAwait(false);
            // Everything after the moment is here when the copy is known whole, reaches back past it (or to the conversation's start), and the newest asked
            // for cover it all.
            var reaches = state.Floor is null || state.Complete || state.Floor.Value.Ticks <= after.UtcTicks;
            return new HeldHistory(rows.Messages, reaches && !rows.More && !chat.Suspect);
        }
        return windows is null ? null : await windows.ReadHeldAsync(chatId, after, limit, cancellationToken).ConfigureAwait(false);
    }

    // ---- IMessageActions, IMessageMutations: done by the router; what they change is read back into the copy ----

    public bool HasLocalActionOptions => actions?.HasLocalActionOptions ?? false;
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        (actions ?? throw new NotSupportedException()).GetOptionsAsync(chatId, messageId, cancellationToken);
    public async Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default)
    {
        var receipt = await (actions ?? throw new NotSupportedException()).ReplyAsync(chatId, messageId, text, cancellationToken).ConfigureAwait(false);
        ReadBackSent(chatId);
        return receipt;
    }

    public async Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default)
    {
        await (actions ?? throw new NotSupportedException()).ReactAsync(chatId, messageId, reaction, remove, cancellationToken).ConfigureAwait(false);
        Confirm(chatId, messageId);
    }

    public async Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).EditAsync(chatId, messageId, originalText, text, cancellationToken).ConfigureAwait(false);
        // Done on the service: the copy says so at once, and the service's own copy replaces this when it is read back.
        if (held.TryGetValue(chatId, out var chat) && chat.State.Filled)
            Chain(chat, async () =>
            {
                if (await HeldMessageAsync(chatId, messageId, CancellationToken.None).ConfigureAwait(false) is { } was)
                    await KeepAsync(chat, [was with { Text = text, EditedAt = was.EditedAt ?? DateTimeOffset.UtcNow }], Reason.Mutation, whole: false).ConfigureAwait(false);
            });
        Confirm(chatId, messageId);
    }

    public async Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).DeleteAsync(chatId, messageId, forEveryone, cancellationToken).ConfigureAwait(false);
        // Removed for this account alone: gone from every copy at once. Unsent for everyone: the service says how it shows, so it is read back.
        if (!forEveryone && held.TryGetValue(chatId, out var chat) && chat.State.Filled) Chain(chat, () => RemoveAsync(chat, [messageId], Reason.Mutation));
        else Confirm(chatId, messageId);
    }

    // ---- Events: queued beside the feed, never waited for ----

    private void Observe(RealtimeEvent change)
    {
        // Before the store is open, events wait in arrival order and are taken in that order once it is (a delete never ahead of the change it followed).
        lock (early)
        {
            if (!earlyTaken)
            {
                early.Add(change);
                if (early.Count == 1) _ = TakeEarlyAsync();
                return;
            }
        }
        Take(change);
    }

    private readonly List<RealtimeEvent> early = [];
    private bool earlyTaken;

    private async Task TakeEarlyAsync()
    {
        try { await ready.Task.ConfigureAwait(false); }
        catch (Exception) { }
        // Under the lock, so nothing that arrives meanwhile is taken ahead of what waited (taking one only queues its work).
        lock (early)
        {
            foreach (var change in early) Take(change);
            early.Clear();
            earlyTaken = true;
        }
    }

    private void Take(RealtimeEvent change)
    {
        if (!ready.Task.IsCompletedSuccessfully) return;
        try { Handle(change); }
        catch (Exception failure) { log($"history event failed kind={change.Kind} error={failure.GetType().Name}"); }
    }

    private void Handle(RealtimeEvent change)
    {
        switch (change.Kind)
        {
            case RealtimeKind.MessageChanged when change.ChatId is { } chatId:
            {
                RowSoon(chatId, read: true);
                var chat = Chat(chatId);
                if (!chat.State.Filled) { Enqueue(chatId, Work.Arrival); return; }
                if (change.Ids is not { Count: > 0 } ids) { Enqueue(chatId, Work.Refresh); return; }
                if (ids.Count > MaxEventIds) Suspect(chat);
                ReadInto(chat, [.. ids.Distinct(StringComparer.Ordinal).Take(MaxEventIds)], Reason.Event);
                return;
            }
            case RealtimeKind.MessageDeleted when change.ChatId is { } chatId && change.Ids is { Count: > 0 } ids:
                RowSoon(chatId, read: true);
                if (held.TryGetValue(chatId, out var deleting) && deleting.State.Filled) Chain(deleting, () => RemoveAsync(deleting, [.. ids], Reason.Event));
                return;
            case RealtimeKind.ChatChanged:
                // Every row it names is built again (a read of the router's own list), however many: a Messenger reconnect names them all.
                foreach (var id in Named(change)) RowSoon(id, read: true);
                foreach (var id in Named(change).Take(MaxEventIds))
                {
                    // Instagram's arrivals come as their own events from its own reads; a read here would cost its budget for a row that moved.
                    if (id.StartsWith(InstagramPrefix, StringComparison.Ordinal)) continue;
                    Enqueue(id, held.TryGetValue(id, out var known) && known.State.Filled ? Work.Check : Work.Arrival);
                }
                return;
            case RealtimeKind.ChatDeleted:
            {
                var named = Named(change).ToArray();
                foreach (var id in named) { listed.TryRemove(id, out _); rowsDue.TryRemove(id, out _); }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ForgetAsync([.. named.Where(held.ContainsKey)]).ConfigureAwait(false);
                        // Behind a whole read of the list under way: it can have listed these before they went, and would write them back over the mark
                        // until two more reads left them out (review, 2026-10-30).
                        await rowsGate.WaitAsync(stop.Token).ConfigureAwait(false);
                        try
                        {
                            foreach (var id in named) listed.TryRemove(id, out _);
                            await db.WriteRowsAsync([.. named.Select(id => new HistoryListRow(id, null))], stop.Token).ConfigureAwait(false);
                        }
                        finally { rowsGate.Release(); }
                    }
                    catch (Exception failure) when (!stop.IsCancellationRequested) { log($"history forget failed error={failure.GetType().Name}"); }
                });
                return;
            }
            case RealtimeKind.Disconnected:
                // What arrives until the service is back may have a gap below it: only a read of the newest page makes these whole again.
                foreach (var chat in InScope(change.Scope)) chat.MarkSuspect();
                return;
            case RealtimeKind.Connected or RealtimeKind.ResyncRequired:
            {
                scopeResyncs[change.Scope ?? ""] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Whatever the list did while the service was away: read it whole again.
                listDue = true;
                rowsSignal.Release();
                var scoped = InScope(change.Scope).ToArray();
                foreach (var chat in scoped) chat.MarkSuspect();
                // Whatever these did while the service was away is read again: the most recently active now, the rest when opened.
                foreach (var chat in scoped.OrderByDescending(chat => chat.State.Newest?.Ticks ?? 0).Take(CatchUpChats)) Enqueue(chat.ChatId, Work.CatchUp);
                return;
            }
        }
    }

    /// <summary>
    /// Waits, at most <see cref="SettleWait"/>, for what the conversation's events have already set going to be written, so a read served just after an event
    /// holds its messages: the event has gone on to the client before them, and a copy read without them a moment earlier was kept as current.
    /// </summary>
    private async Task SettledAsync(Held chat, CancellationToken cancellationToken)
    {
        Task tail;
        lock (chat.ChainLock) tail = chat.Tail;
        if (tail.IsCompleted) return;
        try { await tail.WaitAsync(SettleWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>A message an event's read is bringing right now, waited for (at most <see cref="ResolveWait"/>); null when none is on its way.</summary>
    private async Task<ChatMessage?> PendingAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!resolving.TryGetValue(Key(chatId, messageId), out var pending)) return null;
        try { return await pending.WaitAsync(ResolveWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    private IEnumerable<Held> InScope(string? scope) =>
        held.Values.Where(chat => chat.State.Filled && chat.ChatId.StartsWith(scope ?? "", StringComparison.Ordinal));

    private static IEnumerable<string> Named(RealtimeEvent change) =>
        (change.ChatId is { } one ? [one] : Array.Empty<string>()).Concat(change.Ids ?? []).Distinct(StringComparer.Ordinal);

    // A conversation's own work in the order it was asked for, beside the feed. Each step catches its own failures, so one never stops the chain.
    private static void Chain(Held chat, Func<Task> step)
    {
        lock (chat.ChainLock) chat.Tail = chat.Tail.ContinueWith(_ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    // Reads the named messages from the service at once, together, and writes them in the conversation's own order. The reads are not held behind anything
    // (a push waits on them through ResolveMessageAsync, whose wait is in place before the event goes on); only the writes wait their turn.
    private void ReadInto(Held chat, IReadOnlyList<string> ids, Reason reason)
    {
        var reads = ids.Select(id => (Key: Key(chat.ChatId, id), Read: ReadWholeAsync(chat.ChatId, id))).ToArray();
        foreach (var (key, read) in reads) resolving[key] = read;
        var all = Task.WhenAll(reads.Select(entry => entry.Read));
        _ = all.ContinueWith(_ =>
        {
            foreach (var (key, read) in reads) resolving.TryRemove(new KeyValuePair<string, Task<ChatMessage?>>(key, read));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Chain(chat, async () =>
        {
            try
            {
                var found = (await all.ConfigureAwait(false)).OfType<ChatMessage>().ToArray();
                var missed = found.Length < reads.Length;
                // Instagram names only the newest of what arrived between two of its polls (A1, server-mirror-paths.md): its event never says the copy is
                // whole up to it, and the conversation is read again, within the budget.
                var partial = reason == Reason.Event && chat.ChatId.StartsWith(InstagramPrefix, StringComparison.Ordinal);
                if (found.Length > 0) await KeepAsync(chat, found, reason, whole: !missed && !partial).ConfigureAwait(false);
                // Something the event named is not in the copy: until a page is read, nothing above it is known whole.
                if (missed) Suspect(chat);
                else if (partial) Enqueue(chat.ChatId, Work.Refresh);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                log($"history read failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
                Suspect(chat);
            }
        });
    }

    private void Suspect(Held chat)
    {
        chat.MarkSuspect();
        Enqueue(chat.ChatId, Work.Refresh);
    }

    // A message as whole as a page would give it, or none. Where a service holds some messages only in part (Instagram's list words for a photo), only a copy its
    // whole memory stands behind is taken.
    private async Task<ChatMessage?> ReadWholeAsync(string chatId, string messageId)
    {
        try
        {
            if (peek?.HoldsPartialCopies(chatId) == true) return await peek.PeekWholeAsync(chatId, messageId, stop.Token).ConfigureAwait(false);
            return await sender.ResolveMessageAsync(chatId, messageId, stop.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"history read failed chat={Tag(chatId)} error={failure.GetType().Name}");
            return null;
        }
    }

    private void Confirm(string chatId, string messageId)
    {
        if (!held.TryGetValue(chatId, out var chat) || !chat.State.Filled) return;
        _ = Task.Run(async () =>
        {
            foreach (var wait in ConfirmAfter)
            {
                try { await Task.Delay(wait, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                ReadInto(chat, [messageId], Reason.Mutation);
            }
        });
    }

    private async Task<int> ForgetAsync(IReadOnlyList<string> chatIds)
    {
        var forgotten = 0;
        foreach (var id in chatIds)
        {
            if (!held.TryGetValue(id, out var chat)) continue;
            // Under its gate, so a write in flight finishes first and none after it reaches the store; and its rows go before it leaves the map, so a
            // conversation made again under the same id (an event after this) fills afresh and is never deleted by this.
            await chat.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                chat.Forgotten = true;
                forgotten += await db.ForgetAsync([id], CancellationToken.None).ConfigureAwait(false);
                held.TryRemove(new KeyValuePair<string, Held>(id, chat));
            }
            finally { chat.Gate.Release(); }
        }
        return forgotten;
    }

    // ---- Filling and catching up ----

    private async Task<Held> FilledAsync(string chatId, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var chat = Chat(chatId);
        if (chat.State.Filled) return chat;
        await RefreshAsync(chat, Reason.Open, cancellationToken).ConfigureAwait(false);
        return chat.State.Filled ? chat
            : throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.NotReady, "The conversation's history is still loading.");
    }

    /// <summary>
    /// Reads the service's newest page and joins it to what is held (or fills the conversation with it), reading further back until the two meet. Returns the
    /// service's own page, and whether the read was taken: false when it was put off (a page still loading or partial, a further page the budget did not
    /// allow), which changes nothing held. <paramref name="mayReadMore"/>, for a read nobody asked for, is asked before each further page.
    /// </summary>
    private async Task<(Page<ChatMessage>? Page, bool Taken)> RefreshAsync(Held chat, Reason reason, CancellationToken cancellationToken, Func<bool>? mayReadMore = null)
    {
        await chat.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.GetTimestamp();
        try
        {
            if (chat.Forgotten) return (null, true);
            chat.LastDifference = null;
            var state = chat.State;
            // A suspicion that comes while this reads (a provider dropping again) is not cleared by it.
            var suspicions = chat.SuspicionsNow;
            var page = await chats.GetMessagesAsync(chat.ChatId, null, cancellationToken).ConfigureAwait(false);
            if (!Usable(page))
            {
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=partial refreshing={(page.IsRefreshing ? 1 : 0)} ms={Ms(clock)}");
                return (page, false);
            }
            var gathered = Sorted(page.Items);
            var cursor = page.OldestCursor;
            var more = page.HasMore;
            if (!state.Filled)
            {
                var newest = gathered.Count > 0 ? Position.Of(gathered[^1]) : (Position?)null;
                var filled = new State(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
                // Not announced: no copy of it can be held yet, and a first fill of every conversation would push real events out of the log's ring. It clears
                // whatever was kept of the conversation before it was whole, so nothing stray sits under the floor.
                await WriteAsync(chat, gathered, [], filled, reset: true, announce: false).ConfigureAwait(false);
                Settle(chat, suspicions);
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=filled rows={gathered.Count} complete={(filled.Complete ? 1 : 0)} ms={Ms(clock)}");
                return (page, true);
            }
            // A held conversation the service now calls empty is not taken at its word yet: read again later, nothing changed.
            if (gathered.Count == 0 && state.Newest is not null)
            {
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=empty ms={Ms(clock)}");
                return (page, false);
            }
            var pages = 1;
            while (!Meets(state, gathered) && more && cursor is not null && pages < MaxCatchUpPages)
            {
                if (mayReadMore?.Invoke() == false)
                {
                    log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=deferred pages={pages} ms={Ms(clock)}");
                    return (page, false);
                }
                var older = await chats.GetMessagesAsync(chat.ChatId, cursor, cancellationToken).ConfigureAwait(false);
                pages++;
                // A page that says nothing yet is no reason to start over: the read is tried again later, and nothing changes now.
                if (!Usable(older) || older.Items.Count == 0 && older.HasMore)
                {
                    log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=partial pages={pages} ms={Ms(clock)}");
                    return (page, false);
                }
                gathered = Sorted([.. older.Items, .. gathered]);
                cursor = older.OldestCursor;
                more = older.HasMore;
            }
            if (!Meets(state, gathered))
            {
                // What the service has now does not reach what is known whole: start the conversation over from this, and every copy of it does the same.
                var newest = gathered.Count > 0 ? Position.Of(gathered[^1]) : (Position?)null;
                var restarted = new State(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
                await WriteAsync(chat, gathered, [], restarted, reset: true, found: true).ConfigureAwait(false);
                chat.LastDifference = new(gathered.Count, 0, 0);
                Settle(chat, suspicions);
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=reset rows={gathered.Count} pages={pages} ms={Ms(clock)}");
                return (page, true);
            }
            var (absent, missing, changed) = await CompareAsync(chat.ChatId, gathered, state.Floor, cancellationToken).ConfigureAwait(false);
            chat.LastDifference = new(missing, absent.Count, changed);
            var joined = state;
            var reached = true;
            if (gathered.Count > 0)
            {
                // Known whole up to what was read, and no further: messages events wrote above the page's newest (a service answering from a copy a moment
                // old) were not checked by it, and the conversation stays suspect until a read reaches them.
                var seen = Position.Of(gathered[^1]);
                var top = Max(state.Newest, seen);
                var verified = Max(state.Verified, seen);
                reached = verified.Equals(top);
                joined = joined with { Newest = top, Verified = verified };
                // Read further back than the floor while finding where the two meet: the floor moves down with it. One filled while it held nothing (a
                // Messenger encrypted conversation this device has no messages of) takes its floor, and whether there is more, from this read.
                if (state.Floor is not { } floor || Position.Of(gathered[0]).CompareTo(floor) < 0)
                    joined = joined with { Floor = Position.Of(gathered[0]), Cursor = more ? cursor : null, Complete = !more };
            }
            var written = await WriteAsync(chat, gathered, absent, joined, found: true).ConfigureAwait(false);
            if (reached) Settle(chat, suspicions);
            log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=joined rows={gathered.Count} put={written.Put.Count} absent={written.Removed.Count} pages={pages} reached={(reached ? 1 : 0)} ms={Ms(clock)}");
            return (page, reached);
        }
        finally { chat.Gate.Release(); }
    }

    // A whole read of the newest page: the conversation is whole again, and was read just now, unless something was suspected of it since the read began.
    private static void Settle(Held chat, long suspicions)
    {
        chat.Settle(suspicions);
        chat.RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool Usable(Page<ChatMessage> page) => !page.IsRefreshing && page.Warning is null && page.UnavailableScopes is not { Count: > 0 };

    // Whether a run read from the newest back reaches what is known whole: nothing is, or it reaches back to it.
    private static bool Meets(State state, IReadOnlyList<ChatMessage> gathered) =>
        state.Verified is not { } verified || gathered.Count == 0 || Position.Of(gathered[0]).CompareTo(verified) <= 0;

    // A run of pages against what is held over its span. Absent: the held messages it spans (strictly inside its oldest and newest, older than the grace) that
    // it does not have, gone on the service. Missing: those it has that are not held. Changed: those held otherwise (HistoryDigest.Mark: times, edit marks,
    // reactions; never words). The last two only count, for the mirror check: the write that follows takes the service's.
    private async Task<(IReadOnlyList<string> Absent, int Missing, int Changed)> CompareAsync(string chatId, IReadOnlyList<ChatMessage> gathered, Position? floor,
        CancellationToken cancellationToken)
    {
        if (gathered.Count == 0) return ([], 0, 0);
        var oldest = Position.Of(gathered[0]);
        var newest = Position.Of(gathered[^1]);
        var graceTicks = (DateTimeOffset.UtcNow - AbsenceGrace).UtcTicks;
        var rows = await db.ReadBetweenAsync(chatId, gathered[0].Timestamp.ToUnixTimeMilliseconds(), gathered[^1].Timestamp.ToUnixTimeMilliseconds(), 5000, cancellationToken)
            .ConfigureAwait(false);
        var holding = rows.Where(row => !row.Removed).Select(Message).OfType<ChatMessage>().DistinctBy(message => message.Id).ToDictionary(message => message.Id, StringComparer.Ordinal);
        int missing = 0, changed = 0;
        foreach (var message in gathered)
        {
            if (!holding.TryGetValue(message.Id, out var was)) { if (floor is not { } bottom || Position.Of(message).CompareTo(bottom) >= 0) missing++; }
            else if (HistoryDigest.Mark(was) != HistoryDigest.Mark(message)) changed++;
        }
        if (gathered.Count < 2) return ([], missing, changed);
        var present = gathered.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var absent = holding.Values.Where(message => !present.Contains(message.Id))
            .Where(message => Position.Of(message) is var at && at.CompareTo(oldest) > 0 && at.CompareTo(newest) < 0 && at.Ticks < graceTicks)
            .Select(message => message.Id).ToArray();
        return (absent, missing, changed);
    }

    // Messages strictly before a position, from the copy, reading further back from the service while the copy runs out before `limit`.
    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> OlderPageAsync(Held chat, Position before, int limit, CancellationToken cancellationToken)
    {
        var pages = 0;
        while (true)
        {
            var state = chat.State;
            var rows = await HeldBeforeAsync(chat.ChatId, state, before, limit + 1, cancellationToken).ConfigureAwait(false);
            if (rows.Count > limit) return (rows.TakeLast(limit).ToArray(), true);
            if (state.Complete || pages >= MaxOlderPages) return (rows, !state.Complete);
            if (!await ExtendAsync(chat, cancellationToken).ConfigureAwait(false)) return (rows, !chat.State.Complete);
            pages++;
        }
    }

    // One page older than the floor from the service, kept, and the floor moved down to it once written. False when nothing more could be read now (it
    // changes nothing then: a page the service has not loaded is read again on the next ask).
    private async Task<bool> ExtendAsync(Held chat, CancellationToken cancellationToken)
    {
        await chat.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.GetTimestamp();
        try
        {
            var state = chat.State;
            if (state.Complete || chat.Forgotten) return false;
            Page<ChatMessage>? page;
            try
            {
                page = state.Cursor is { } cursor ? await chats.GetMessagesAsync(chat.ChatId, cursor, cancellationToken).ConfigureAwait(false)
                    : await RecoverOlderAsync(chat.ChatId, state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && state.Cursor is not null)
            {
                // The service's position from an earlier run may mean nothing to it now: find the floor again from its newest page.
                log($"history older cursor failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
                page = await RecoverOlderAsync(chat.ChatId, state, cancellationToken).ConfigureAwait(false);
            }
            if (page is null || !Usable(page)) return false;
            var items = Sorted(page.Items);
            var below = state.Floor is { } floor ? items.Where(message => Position.Of(message).CompareTo(floor) < 0).ToArray() : items.ToArray();
            // Complete only on the service's word; a page that says more but brings nothing is read again on the next ask.
            if (items.Count == 0 && page.HasMore) return false;
            var next = state with
            {
                Floor = below.Length > 0 ? Position.Of(below[0]) : state.Floor,
                Cursor = page.HasMore ? page.OldestCursor : null,
                Complete = !page.HasMore,
            };
            await WriteAsync(chat, items, [], next).ConfigureAwait(false);
            log($"history older chat={Tag(chat.ChatId)} rows={items.Count} below={below.Length} complete={(next.Complete ? 1 : 0)} ms={Ms(clock)}");
            return below.Length > 0;
        }
        finally { chat.Gate.Release(); }
    }

    // Pages back from the service's newest until a page reaches below the floor, and returns that page (it holds the floor's neighbours below it). Null
    // when it was not found within the pages allowed: fewer for Instagram, every page of which is a request on the real account.
    private async Task<Page<ChatMessage>?> RecoverOlderAsync(string chatId, State state, CancellationToken cancellationToken)
    {
        var limit = chatId.StartsWith(InstagramPrefix, StringComparison.Ordinal) ? 4 : MaxCatchUpPages * 2;
        var page = await chats.GetMessagesAsync(chatId, null, cancellationToken).ConfigureAwait(false);
        for (var pages = 1; ; pages++)
        {
            if (!Usable(page)) return null;
            var items = Sorted(page.Items);
            if (state.Floor is not { } floor || items.Count > 0 && Position.Of(items[0]).CompareTo(floor) < 0) return page;
            if (!page.HasMore || page.OldestCursor is null) return page;
            if (pages >= limit) return null;
            page = await chats.GetMessagesAsync(chatId, page.OldestCursor, cancellationToken).ConfigureAwait(false);
        }
    }

    // The held messages strictly before a position (and not below the floor), oldest first, at most `limit`.
    private async Task<IReadOnlyList<ChatMessage>> HeldBeforeAsync(string chatId, State state, Position before, int limit, CancellationToken cancellationToken,
        bool wholeMoments = false)
    {
        return await ReadOrderedAsync(chatId, state, long.MinValue, new DateTimeOffset(before.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            newestFirst: true, limit, message => Position.Of(message).CompareTo(before) < 0, cancellationToken, wholeMoments).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewestAsync(string chatId, State state, int limit, CancellationToken cancellationToken)
    {
        var messages = await ReadOrderedAsync(chatId, state, long.MinValue, long.MaxValue, newestFirst: true, limit + 1, _ => true, cancellationToken)
            .ConfigureAwait(false);
        return (messages.TakeLast(limit).ToArray(), messages.Count > limit || !state.Complete);
    }

    // The held messages at or after a moment, oldest first: the first `limit`, or with `newestFirst` the newest that many.
    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewerAsync(string chatId, State state, DateTimeOffset at, int limit, CancellationToken cancellationToken,
        bool newestFirst = false)
    {
        var ticks = at.UtcTicks;
        var messages = await ReadOrderedAsync(chatId, state, at.ToUnixTimeMilliseconds(), long.MaxValue, newestFirst, limit + 1,
            message => message.Timestamp.UtcTicks >= ticks, cancellationToken, wholeMoments: !newestFirst).ConfigureAwait(false);
        if (newestFirst)
        {
            return (messages.TakeLast(limit).ToArray(), messages.Count > limit);
        }
        var selected = TakeMoments(messages, limit, newestFirst: false);
        return (selected, messages.Count > selected.Count);
    }

    private async Task<IReadOnlyList<ChatMessage>> ReadOrderedAsync(string chatId, State state, long from, long to, bool newestFirst,
        int limit, Func<ChatMessage, bool> accepts, CancellationToken cancellationToken, bool wholeMoments = false)
    {
        if (state.Floor is { } floor) from = Math.Max(from, new DateTimeOffset(floor.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds());
        var messages = new List<ChatMessage>();
        var lookedBeyond = false;
        while ((messages.Count < limit || wholeMoments && !lookedBeyond) && from <= to)
        {
            // SQL knows only milliseconds, not the encrypted within-millisecond order. Finish each boundary group before
            // choosing a page. If the cursor/floor excludes that group, continue at the next millisecond, not a fixed allowance.
            if (messages.Count >= limit) lookedBeyond = true;
            var rows = await db.ReadTimeSliceAsync(chatId, from, to, newestFirst, Math.Max(1, limit - messages.Count), cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0) break;
            messages.AddRange(rows.Select(Message).OfType<ChatMessage>().Where(message => AboveFloor(state, message) && accepts(message)));
            if (newestFirst)
            {
                var oldest = rows.Min(row => row.Order);
                if (oldest == long.MinValue) break;
                to = oldest - 1;
            }
            else
            {
                var newest = rows.Max(row => row.Order);
                if (newest == long.MaxValue) break;
                from = newest + 1;
            }
        }
        var ordered = Sorted(messages);
        // A time-only window may consume the entire boundary group, including the usual extra row. Keep a look-ahead
        // beyond that group so it can still say whether another window follows.
        if (wholeMoments) return ordered;
        return (newestFirst ? ordered.TakeLast(limit) : ordered.Take(limit)).ToArray();
    }

    // Window cursors name only a time, unlike normal history cursors. Never cut through that time or the next window
    // would step past the rest of its messages. The window port explicitly allows a larger page for these ties.
    private static IReadOnlyList<ChatMessage> TakeMoments(IReadOnlyList<ChatMessage> messages, int limit, bool newestFirst)
    {
        if (limit <= 0) return [];
        if (messages.Count <= limit) return messages;
        var boundary = messages[newestFirst ? messages.Count - limit : limit - 1].Timestamp;
        return messages.Where(message => newestFirst ? message.Timestamp >= boundary : message.Timestamp <= boundary).ToArray();
    }

    private static bool AboveFloor(State state, ChatMessage message) => state.Floor is not { } floor || Position.Of(message).CompareTo(floor) >= 0;

    private async Task<ChatMessage?> HeldMessageAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!ready.Task.IsCompletedSuccessfully || !held.TryGetValue(chatId, out var chat) || !chat.State.Filled) return null;
        var row = await db.ReadOneAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
        return row is { Removed: false } ? Message(row) : null;
    }

    // Messages an event (or a change made here) brought, under the conversation's gate. They move the newest held; they move what is known whole only when
    // nothing was missed before them and none of them is missing (`whole`).
    private async Task KeepAsync(Held chat, IReadOnlyList<ChatMessage> messages, Reason reason, bool whole)
    {
        await Gated(chat, async () =>
        {
            var state = chat.State;
            if (!state.Filled) return;
            var kept = messages.Where(message => AboveFloor(state, message)).ToArray();
            if (kept.Length == 0) return;
            var top = Max(state.Newest, kept.Select(Position.Of).Max());
            var verified = whole && !chat.Suspect ? Max(state.Verified, top) : state.Verified;
            // Written while something may be missing below it: read the newest page soon, rather than leave it suspect until it is next opened.
            if (chat.Suspect) Enqueue(chat.ChatId, Work.Refresh);
            await WriteAsync(chat, kept, [], state with { Newest = top, Verified = verified }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task RemoveAsync(Held chat, IReadOnlyList<string> ids, Reason reason) =>
        Gated(chat, async () =>
        {
            if (chat.State.Filled) await WriteAsync(chat, [], ids, chat.State).ConfigureAwait(false);
        });

    private async Task Gated(Held chat, Func<Task> write)
    {
        try
        {
            await chat.Gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (!chat.Forgotten) await write().ConfigureAwait(false);
            }
            finally { chat.Gate.Release(); }
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"history write failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
        }
    }

    /// <summary>
    /// Writes a change to one conversation (the caller holds its gate) with the state it leaves the conversation in, and only once that is written takes the
    /// state and puts what changed in the event sequence. The write itself is never cancelled: a service's page in hand is kept, or nothing changes.
    /// </summary>
    /// <remarks>
    /// <paramref name="found"/>: the write is a read of the service's pages (a join or a fresh start), not an event's own messages. What it put and removed is then
    /// said as realtime events too, history-marked so no one notifies for it: a client that keeps its own copy and ignores history changes (the PC, every client
    /// below 24) learns of a message no event announced, as a provider's event would have told it. A Google Voice text the PC had read a moment before its event
    /// reached the copy stayed missing there for good (2026-10-29).
    /// </remarks>
    private async Task<HistoryWritten> WriteAsync(Held chat, IReadOnlyList<ChatMessage> puts, IReadOnlyList<string> removes, State next, bool reset = false, bool announce = true,
        bool found = false)
    {
        if (chat.Forgotten) return new(chat.Seq, [], [], false);
        var from = chat.Seq;
        var written = await db.WriteAsync(new HistoryEdit(chat.ChatId,
            puts.Select(message => new HistoryPut(message.Id, message.Timestamp.ToUnixTimeMilliseconds(), Bytes(message))).ToArray(),
            removes, Save(next), reset), CancellationToken.None).ConfigureAwait(false);
        chat.State = next;
        if (written.Skipped > 0)
        {
            // A message too large to keep is a hole: the conversation is read again rather than called whole.
            log($"history write skipped chat={Tag(chat.ChatId)} messages={written.Skipped}");
            chat.MarkSuspect();
        }
        if (!written.Changed) return written;
        lock (chat.Numbers)
        {
            chat.Seq = written.Seq;
            if (reset) chat.ResetSeq = written.Seq;
        }
        if (announce && next.Filled && events is { } sequence)
        {
            var changed = written.Put.ToHashSet(StringComparer.Ordinal);
            var messages = puts.Where(message => changed.Contains(message.Id)).DistinctBy(message => message.Id).ToArray();
            var truncated = reset || messages.Length > WholeHistoryLimits.MaxEventMessages;
            sequence.AppendHistory(new HistoryChange(chat.ChatId, Revision(from), Revision(written.Seq), truncated ? [] : messages, truncated ? [] : written.Removed,
                reset, truncated));
            if (found)
            {
                var newest = messages.OrderByDescending(Position.Of).Take(MaxEventIds).Select(message => message.Id).ToArray();
                if (newest.Length > 0) sequence.Append(new RealtimeEvent(RealtimeKind.MessageChanged, chat.ChatId, newest, false, DateTimeOffset.UtcNow, IsHistory: true));
                if (written.Removed.Count > 0) sequence.Append(new RealtimeEvent(RealtimeKind.MessageDeleted, chat.ChatId, [.. written.Removed], ReceivedAt: DateTimeOffset.UtcNow));
                // More than one event names: a copy kept closed is read again when it opens rather than patched.
                if (messages.Length > MaxEventIds || reset) sequence.Append(new RealtimeEvent(RealtimeKind.ChatChanged, chat.ChatId, [chat.ChatId], ReceivedAt: DateTimeOffset.UtcNow));
            }
        }
        // The row's last message is the copy's: built again from it (B2).
        if (next.Filled) RowSoon(chat.ChatId, read: false);
        // Every message held went while older ones lie before them: nothing is left to serve a newest page from, so it is read again from the service's
        // newest (the server's own copy emptied by removals, ROADMAP.md).
        if (written.Removed.Count > 0 && next.Filled && !next.Complete && await db.CountAsync(chat.ChatId, CancellationToken.None).ConfigureAwait(false) == 0)
        {
            chat.MarkSuspect();
            Enqueue(chat.ChatId, Work.Refresh);
        }
        return written;
    }

    private bool TakeSyncRefresh()
    {
        var minute = Environment.TickCount64 / 60_000;
        lock (syncRefreshGate)
        {
            if (minute != syncRefreshMinute) (syncRefreshMinute, syncRefreshes) = (minute, 0);
            return ++syncRefreshes <= SyncRefreshesPerMinute;
        }
    }

    private bool Stale(Held chat)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scope = scopeResyncs.Where(pair => chat.ChatId.StartsWith(pair.Key, StringComparison.Ordinal)).Select(pair => pair.Value).DefaultIfEmpty(0).Max();
        return chat.Suspect || chat.RefreshedAt < scope || now - chat.RefreshedAt > (long)OpenRefreshAfter.TotalMilliseconds;
    }

    // ---- The background: filling every conversation, catching up, checking ----

    private enum Work { Open = 0, Arrival = 1, Check = 2, Refresh = 3, CatchUp = 4, Initial = 5, Mirror = 6 }
    private enum Reason { Open, Event, Arrival, Check, Refresh, CatchUp, Initial, Older, Mutation, Mirror }

    private void Enqueue(string chatId, Work work) => queue.Add(chatId, work);

    private async Task WorkAsync()
    {
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await queue.TakeAsync(stop.Token).ConfigureAwait(false) is { } item)
        {
            try
            {
                var service = Service(item.ChatId);
                if (next.TryGetValue(service, out var at) && at > Environment.TickCount64) await Task.Delay(TimeSpan.FromMilliseconds(at - Environment.TickCount64), stop.Token).ConfigureAwait(false);
                var chat = Chat(item.ChatId);
                if (chat.Forgotten) { Mirrored(item.ChatId, null); continue; }
                if (item.Work == Work.Check)
                {
                    ChatSummary summary;
                    // A row the router cannot read (four Messenger conversations failed this five times each, every walk, 2026-10-29) is left to the walk,
                    // which reads the conversation from the row it lists.
                    try { summary = await chats.GetChatAsync(item.ChatId, stop.Token).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        log($"history check unreadable chat={Tag(item.ChatId)} error={failure.GetType().Name}");
                        continue;
                    }
                    // Only a conversation that moved past what is held is read again (a read or a mute moves nothing held), and only once for each move: a
                    // reaction moves the list past the newest message for good, and would otherwise be read again at every walk.
                    if (summary.LastActivity is not { } last || !Moved(chat, last)) continue;
                }
                // Opening is a read someone asked for; everything else is the server's own, and waits while the service's budget says so, page by page. A
                // mirror check's read the budget does not allow is counted as not read rather than put off.
                var asked = item.Work == Work.Open;
                if (!asked && !mayReadUnasked(item.ChatId))
                {
                    if (item.Work == Work.Mirror) Mirrored(item.ChatId, null);
                    else queue.Later(item.ChatId, item.Work, TimeSpan.FromMinutes(10), item.Tries);
                    continue;
                }
                var (_, taken) = await RefreshAsync(chat, item.Work switch
                {
                    Work.Open => Reason.Open, Work.Arrival => Reason.Arrival, Work.Check => Reason.Check, Work.CatchUp => Reason.CatchUp,
                    Work.Initial => Reason.Initial, Work.Mirror => Reason.Mirror, _ => Reason.Refresh,
                }, stop.Token, asked ? null : () => mayReadUnasked(item.ChatId)).ConfigureAwait(false);
                next[service] = Environment.TickCount64 + (long)Pace.TotalMilliseconds;
                // Whatever the read was asked for, it compared the service's newest page with the copy: a mirror check waiting on this conversation counts it.
                Mirrored(item.ChatId, taken ? chat.LastDifference : null);
                if (!taken && item.Work != Work.Mirror && item.Tries < 5) queue.Later(item.ChatId, item.Work, RetryAfter * (1 << item.Tries), item.Tries + 1);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure)
            {
                log($"history work failed chat={Tag(item.ChatId)} work={item.Work} error={failure.GetType().Name}");
                if (item.Work != Work.Mirror && item.Tries < 5) queue.Later(item.ChatId, item.Work, RetryAfter * (1 << item.Tries), item.Tries + 1);
            }
            // However the work ended (a check that found no move, a read put off, merged into other work), a mirror check waiting on it is not left to wait
            // out its 20 minutes; one already counted is not counted again (review, 2026-10-30).
            finally { Mirrored(item.ChatId, null); }
        }
    }

    // Whether the list moved past what is held, newly: then it is taken as seen, and the caller reads the conversation once for it.
    private static bool Moved(Held chat, DateTimeOffset last)
    {
        if (last.UtcTicks <= chat.SeenActivity || chat.State.Newest is { } newest && last.UtcTicks <= newest.Ticks + TimeSpan.TicksPerSecond * 2) return false;
        chat.SeenActivity = last.UtcTicks;
        return true;
    }

    // Walks the list now and then: every row is kept for the feed, a conversation not held yet is filled (the most recently active first), one whose list
    // moved past what is held is read again, and the most recently active of each service are compared with it (the mirror check, stage 9).
    private async Task WalkAsync()
    {
        try { await Task.Delay(WalkDelay, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var clock = Stopwatch.GetTimestamp();
            int listedRows = 0, filling = 0, checking = 0;
            try
            {
                var rows = await (await DeepListDueAsync().ConfigureAwait(false) ? ReadDeepListAsync(stop.Token) : ReadListAsync(stop.Token)).ConfigureAwait(false) ?? [];
                foreach (var row in rows)
                {
                    // Spam is read only when someone opens it.
                    if (row.IsSpam) continue;
                    listedRows++;
                    var chat = Chat(row.Id);
                    if (!chat.State.Filled) { Enqueue(row.Id, Work.Initial); filling++; continue; }
                    // Read from the row in hand: asking the router for it again failed for some Messenger conversations every time.
                    if (row.LastActivity is not { } last || !Moved(chat, last)) continue;
                    checking++;
                    Enqueue(row.Id, Work.Refresh);
                }
                log($"history walk listed={listedRows} filling={filling} checking={checking} held={held.Values.Count(h => h.State.Filled)} queued={queue.Count} seq={db.Seq} bytes={db.Bytes} ms={Ms(clock)}");
                StartMirror(rows, "walk");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"history walk failed listed={listedRows} error={failure.GetType().Name}"); }
            try { await Task.Delay(WalkEvery, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private const string DeepListNote = "deep_list_at";

    // Kept with the store: the server restarts more often than a day, and a deep read at each start would be the same calls again. A first start has no rows, so it reads deep.
    private async Task<bool> DeepListDueAsync()
    {
        var note = await db.ReadNoteAsync(DeepListNote, stop.Token).ConfigureAwait(false);
        return !DateTimeOffset.TryParse(note, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) || DateTimeOffset.UtcNow - at >= DeepListEvery;
    }

    // How far a read of the list got: true when it reached the list's end or the most it may read with every service answering.
    private sealed class ListCoverage { public bool Covered; }

    private int deepReading;

    // A deep read, noted as done only if it was: a service down for it (a retry cursor, not a failure) would otherwise leave its older rows a day behind, restarts included
    // (review, 2026-11-01). Not noted, the next walk reads deep again, paced as ever. One at a time: the walk can come due while a first start's is still reading.
    private async Task<IReadOnlyList<ChatSummary>?> ReadDeepListAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref deepReading, 1) == 1) return await ReadListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var coverage = new ListCoverage();
            var rows = await ReadListAsync(cancellationToken, deep: true, coverage).ConfigureAwait(false);
            if (coverage.Covered) await db.WriteNoteAsync(DeepListNote, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return rows;
        }
        finally { Volatile.Write(ref deepReading, 0); }
    }

    // ---- The list rows (stage 9, B1 and B2) ----

    // A row to build again soon. Read: ask the router for it first (after an event). One signal however many are asked for at once: a Messenger reconnect
    // names every conversation, and a release each made as many empty passes of the loop (review, 2026-10-30).
    private void RowSoon(string chatId, bool read)
    {
        rowsDue.AddOrUpdate(chatId, read, (_, was) => was || read);
        if (Volatile.Read(ref rowsIdle).Task.IsCompleted) Interlocked.Exchange(ref rowsIdle, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.Exchange(ref rowsSignaled, 1) == 0) rowsSignal.Release();
    }

    private int rowsSignaled;

    // List reads and row writes one at a time: a list read seconds long overwrote rows written meanwhile from fresher events, and marked a conversation that
    // began meanwhile as gone (review, 2026-10-30).
    private readonly SemaphoreSlim rowsGate = new(1, 1);

    // A whole read of the list on a reconnect at most this often; the walk reads it every half hour anyway. Each reads Instagram's inbox when its list is
    // more than a few seconds old, as its poll does.
    private long listReadAt;
    private static readonly long ListReadEvery = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    // Completed while no row waits to be built: a feed read waits for it (a little), so a row asked for by an event just before is in what it hands out.
    private TaskCompletionSource rowsIdle = CompletedSource();

    private static TaskCompletionSource CompletedSource()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    // What events and rows already under way have to write, waited for (at most SettleWait), before the feed is read.
    private async Task FeedSettledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(held.Values.Select(chat => chat.Tail)).WaitAsync(SettleWait, cancellationToken).ConfigureAwait(false);
            await Volatile.Read(ref rowsIdle).Task.WaitAsync(SettleWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
    }

    // Builds the rows asked for, a moment after the first ask so a burst builds each once, and the whole list when asked.
    private async Task RowsAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await rowsSignal.WaitAsync(stop.Token).ConfigureAwait(false);
                await Task.Delay(RowsDelay, stop.Token).ConfigureAwait(false);
                Volatile.Write(ref rowsSignaled, 0);
                if (listDue)
                {
                    listDue = false;
                    // A new store (no note of a deep read) reads deep before it says it is ready: a client's first read takes the rows it finds and is told of no more later, so a
                    // phone joining a new server would hold only the newest pages (review, 2026-11-01). A restart has its rows already, and its deep read comes when it is due.
                    if (!rowsReady.Task.IsCompleted) await (await DeepListDueAsync().ConfigureAwait(false) ? ReadDeepListAsync(stop.Token) : ReadListAsync(stop.Token)).ConfigureAwait(false);
                    else if (Environment.TickCount64 - Interlocked.Read(ref listReadAt) >= ListReadEvery) await ReadListAsync(stop.Token).ConfigureAwait(false);
                }
                await rowsGate.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    var due = new List<(string Id, bool Read)>();
                    foreach (var id in rowsDue.Keys) if (rowsDue.TryRemove(id, out var read)) due.Add((id, read));
                    var rows = new List<ChatSummary>(due.Count);
                    foreach (var (id, read) in due)
                    {
                        ChatSummary? row = null;
                        // Read from the router after an event, or for one not listed since the last whole read (a conversation begun since). Never
                        // Instagram's: its row can come from Instagram itself (a GetThread on the real account) when its list is a few seconds old, a read
                        // outside its budget nobody asked for (review, 2026-10-30). Its rows come from the list reads, their last line from the copy.
                        if ((read || !listed.ContainsKey(id)) && !id.StartsWith(InstagramPrefix, StringComparison.Ordinal))
                        {
                            try { row = listed[id] = await chats.GetChatAsync(id, stop.Token).ConfigureAwait(false); }
                            catch (Exception failure) when (failure is not OperationCanceledException) { }
                        }
                        if ((row ?? listed.GetValueOrDefault(id)) is { } known) rows.Add(known);
                    }
                    await StoreRowsAsync(rows, stop.Token, announce: true).ConfigureAwait(false);
                }
                finally { rowsGate.Release(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"history rows failed error={failure.GetType().Name}"); }
            finally { if (rowsDue.IsEmpty) Volatile.Read(ref rowsIdle).TrySetResult(); }
        }
    }

    // Rows a whole read did not list, and the read before it did not either: only those leave. A service can hand back a part of its list as if whole
    // right after it connects (its restored rows), and one read alone would drop the rest for every client until the next (review, 2026-10-30).
    private HashSet<string> missingOnce = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the services' list, its newest <see cref="ListPages"/> pages or with <paramref name="deep"/> up to <see cref="DeepListPages"/>, keeps every row for the feed, and lets go
    /// of rows no longer on it (only when the whole list was read, only for a service that answered with some rows, and only those two whole reads in a row left out). Null when it
    /// could not be read.
    /// </summary>
    private async Task<IReadOnlyList<ChatSummary>?> ReadListAsync(CancellationToken cancellationToken, bool deep = false, ListCoverage? coverage = null)
    {
        var clock = Stopwatch.GetTimestamp();
        var most = deep ? DeepListPages : ListPages;
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
                await rowsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var stretch = new List<ChatSummary>();
                    for (var inStretch = 0; inStretch < Math.Max(1, ListPages) && !done; inStretch++)
                    {
                        if (pages > 0 && ListPagePace > TimeSpan.Zero) await Task.Delay(ListPagePace, cancellationToken).ConfigureAwait(false);
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
                    changed += await StoreRowsAsync(stretch, cancellationToken).ConfigureAwait(false);
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
                finally { rowsGate.Release(); }
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
        finally { if (deep || Volatile.Read(ref deepReading) == 0) rowsReady.TrySetResult(); }
    }

    // Keeps rows as built, and with [announce] puts each that changed in the event sequence for clients of 25 (EventKinds.Row): what a row did by itself (its
    // copy moved, an event named it) reaches a connected client's list with no read. A whole read of the list announces nothing (on the first after this
    // version it changes every row, which would push real events out of the ring); clients take those from the feed when they next read it.
    private async Task<int> StoreRowsAsync(IReadOnlyList<ChatSummary> rows, CancellationToken cancellationToken, bool announce = false)
    {
        if (rows.Count == 0) return 0;
        var built = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var row in rows) built[row.Id] = await BuildRowAsync(row, cancellationToken).ConfigureAwait(false);
        var changed = await db.WriteRowsAsync([.. built.Values.Select(row => new HistoryListRow(row.Id, JsonSerializer.SerializeToUtf8Bytes(row, ProtocolJson.Options)))],
            cancellationToken).ConfigureAwait(false);
        if (announce && events is { } sequence)
            foreach (var row in changed) sequence.AppendRow(new(row.ChatId, built.GetValueOrDefault(row.ChatId)));
        return changed.Count;
    }

    /// <summary>
    /// A conversation's list row as served (owner's D1, 2026-10-30): the service's row, its last message from the copy, so the list never says what the thread
    /// does not hold. The service's own line stays where it says what no message does (a reaction, in the service's words), and where nothing is held yet.
    /// A line newer than anything held is the copy being behind: the row says what is held, and the conversation is read so the two meet.
    /// </summary>
    private async Task<ChatSummary> BuildRowAsync(ChatSummary row, CancellationToken cancellationToken)
    {
        if (!held.TryGetValue(row.Id, out var chat) || !chat.State.Filled) return row;
        try
        {
            var service = row.Preview;
            // The message the line names as the copy holds it, or as the event naming it is reading it now (built a moment before, the row said the message
            // before it, and a client that read the feed then kept that).
            if (service is not null && (await PendingAsync(row.Id, service.Id, cancellationToken).ConfigureAwait(false)
                    ?? await HeldMessageAsync(row.Id, service.Id, cancellationToken).ConfigureAwait(false)) is { } same)
                return row with { Preview = same };
            var rows = await db.ReadNewestAsync(row.Id, 8, cancellationToken).ConfigureAwait(false);
            var last = Sorted(rows.Select(Message).OfType<ChatMessage>()).LastOrDefault(message => !message.IsHidden);
            if (last is null) return row;
            if (service is null || service.Timestamp <= last.Timestamp.AddSeconds(2)) return row with { Preview = last };
            if (IsReactionLine(service)) return row;
            if (row.LastActivity is { } moved && Moved(chat, moved)) Enqueue(row.Id, Work.Refresh);
            return row with { Preview = last };
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { return row; }
    }

    // A service's line that says a reaction rather than a message: Google's "😂 to "…"", and the words Meta's apps use.
    private static bool IsReactionLine(ChatMessage line) =>
        line.Text is { Length: > 0 } text && (GoogleReaction().IsMatch(text) || text.Contains("reacted ", StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex("^\\S{1,16} to [\"“].*[\"”]$")]
    private static partial System.Text.RegularExpressions.Regex GoogleReaction();

    // ---- The mirror check (stage 9, A2) ----

    // One service's check under way: the conversations it waits on, and what those that were read found.
    private sealed class MirrorRound(string service, int asked, string reason)
    {
        public string Service { get; } = service;
        public string Why { get; } = reason;
        public int Pending = asked;
        public int Checked, Differed, Missing, Extra, Changed, Skipped;
        public long Started { get; } = Stopwatch.GetTimestamp();
    }

    /// <summary>What a read of a conversation's newest page found against the copy (counts only).</summary>
    internal sealed record Difference(int Missing, int Extra, int Changed)
    {
        public bool Any => Missing > 0 || Extra > 0 || Changed > 0;
    }

    // Starts a check of each service that has none under way: its most recently active conversations held whole, each read once, a second apart per service
    // and Instagram's only while its budget allows (owner's D3).
    private void StartMirror(IReadOnlyList<ChatSummary> rows, string why)
    {
        var started = new List<string>();
        lock (mirrorGate)
        {
            SweepRounds();
            foreach (var group in rows.Where(row => !row.IsSpam && held.TryGetValue(row.Id, out var chat) && chat.State.Filled && !mirroring.ContainsKey(row.Id))
                         .GroupBy(row => Service(row.Id), StringComparer.Ordinal))
            {
                if (rounds.ContainsKey(group.Key)) continue;
                var chosen = group.OrderByDescending(row => row.LastActivity ?? DateTimeOffset.MinValue).Take(MirrorPerService).Select(row => row.Id).ToArray();
                if (chosen.Length == 0) continue;
                var round = new MirrorRound(group.Key, chosen.Length, why);
                rounds[group.Key] = round;
                foreach (var id in chosen) mirroring[id] = round;
                started.Add(group.Key + "=" + chosen.Length);
                foreach (var id in chosen) Enqueue(id, Work.Mirror);
            }
        }
        if (started.Count > 0) log($"history mirror started reason={why} {string.Join(' ', started)}");
    }

    // A conversation a check waits on was read (with what it found), or could not be (null).
    private void Mirrored(string chatId, Difference? found)
    {
        if (!mirroring.TryRemove(chatId, out var round)) return;
        lock (mirrorGate)
        {
            if (found is null) round.Skipped++;
            else
            {
                round.Checked++;
                if (found.Any)
                {
                    round.Differed++;
                    round.Missing += found.Missing; round.Extra += found.Extra; round.Changed += found.Changed;
                    log($"history mirror chat={Tag(chatId)} missing={found.Missing} extra={found.Extra} changed={found.Changed}");
                }
            }
            if (--round.Pending <= 0) Finish(round);
        }
    }

    // Reports a check whose reads all came back, and one that waited past its time with the rest counted as not read. Caller holds mirrorGate.
    private void SweepRounds()
    {
        foreach (var round in rounds.Values.Where(round => Stopwatch.GetElapsedTime(round.Started) > MirrorTimeout).ToArray())
        {
            foreach (var (id, waiting) in mirroring.ToArray())
                if (ReferenceEquals(waiting, round) && mirroring.TryRemove(id, out _)) { round.Skipped++; round.Pending--; }
            Finish(round);
        }
    }

    // Caller holds mirrorGate.
    private void Finish(MirrorRound round)
    {
        if (!rounds.TryGetValue(round.Service, out var current) || !ReferenceEquals(current, round)) return;
        rounds.Remove(round.Service);
        reports[round.Service] = new(round.Service, DateTimeOffset.UtcNow, round.Checked, round.Differed, round.Missing, round.Extra, round.Changed, round.Skipped);
        log($"history mirror service={round.Service} reason={round.Why} checked={round.Checked} differed={round.Differed} missing={round.Missing} extra={round.Extra} changed={round.Changed} skipped={round.Skipped} ms={Ms(round.Started)}");
    }


    // ---- Small parts ----

    private Held Chat(string chatId) => held.GetOrAdd(chatId, id => new Held(id));

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch (Exception failure) when (failure is not OperationCanceledException) { return false; }
    }

    private string Revision(long seq) => db.Epoch + "." + seq.ToString(CultureInfo.InvariantCulture);

    private long? Parse(string? revision)
    {
        if (revision is null) return null;
        var dot = revision.LastIndexOf('.');
        return dot > 0 && revision.AsSpan(0, dot).SequenceEqual(db.Epoch) && long.TryParse(revision.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq : null;
    }

    private static string Key(string chatId, string messageId) => chatId + "\n" + messageId;

    private static Position? Max(Position? a, Position b) => a is { } known && known.CompareTo(b) > 0 ? known : b;
    private static Position? Max(Position? a, Position? b) => b is { } other ? Max(a, other) : a;

    private static byte[] Bytes(ChatMessage message)
    {
        // Who has seen it in one order, so the same message read twice is the same bytes and takes no change number.
        var canonical = message.Delivery.SeenBy.Count > 1
            ? message with { Delivery = message.Delivery with { SeenBy = new SortedDictionary<string, bool?>(message.Delivery.SeenBy.ToDictionary(), StringComparer.Ordinal) } }
            : message;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, ProtocolJson.Options);
        // Past what a row holds (megabytes of text): kept with its words cut short rather than left out, which would be a hole in the middle of the copy.
        return bytes.Length <= MaxRowBytes ? bytes
            : JsonSerializer.SerializeToUtf8Bytes(canonical with { Text = canonical.Text?[..Math.Min(canonical.Text.Length, 65_536)] }, ProtocolJson.Options);
    }

    // Under the store's limit for one row, with room for its seal.
    private const int MaxRowBytes = 4 * 1024 * 1024 - 1024;

    private static ChatMessage? Message(HistoryRow row)
    {
        if (row.Removed) return null;
        try { return JsonSerializer.Deserialize<ChatMessage>(row.Payload, ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    private static List<ChatMessage> Sorted(IEnumerable<ChatMessage> messages) =>
        messages.DistinctBy(message => message.Id).OrderBy(message => Position.Of(message)).ToList();

    private static string Cursor(ChatMessage oldest) => HistoryPosition.Cursor(oldest);

    private static Position? ReadCursor(string cursor) => HistoryPosition.ReadCursor(cursor);

    private static string Service(string chatId) => chatId.IndexOf(':') is > 0 and var colon ? chatId[..colon] : chatId;

    private static string Tag(string chatId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + chatId)).AsSpan(0, 3)).ToLowerInvariant();

    private static string Name(Reason reason) => reason.ToString().ToLowerInvariant();

    private static long Ms(long clock) => (long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds;

    private static byte[] Save(State state) => JsonSerializer.SerializeToUtf8Bytes(state);

    /// <summary>
    /// A conversation as the copy holds it: whether it is filled, whether the service said it has nothing older, the service's cursor below the floor, the
    /// oldest held (the floor), the newest held, and how far up from the floor it is known whole. Replaced whole, and only once what it says is written.
    /// </summary>
    internal sealed record State(bool Filled = false, bool Complete = false, string? Cursor = null, Position? Floor = null, Position? Newest = null,
        Position? Verified = null)
    {
        public static State Empty { get; } = new();
    }

    // One conversation's place in the copy. Its state is replaced only under its gate (or before anyone can see it); reads take it as it stands.
    private sealed class Held(string chatId)
    {
        public string ChatId { get; } = chatId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object ChainLock { get; } = new();
        public Task Tail = Task.CompletedTask;
        public volatile State State = State.Empty;
        public long Seq;
        public long ResetSeq;
        // Seq and ResetSeq change together under this, so a sync reads them as one pair without waiting on Gate (held across a read of the service).
        public object Numbers { get; } = new();
        public long RefreshedAt;
        // The list's newest activity when it was last checked against what is held (in memory: a restart checks each once more).
        public long SeenActivity;
        // Something may be missing above what is known whole (a provider reconnected, an event's message could not be read, the server restarted): until its
        // newest page is read again, events still write but nothing is called whole past the verified position.
        public volatile bool Suspect = true;
        // How many times it was made suspect: a read clears the suspicion only if none came while it read.
        public long Suspicions;
        private readonly object suspectLock = new();

        public void MarkSuspect()
        {
            lock (suspectLock) { Suspicions++; Suspect = true; }
        }

        public void Settle(long seen)
        {
            lock (suspectLock) if (Suspicions == seen) Suspect = false;
        }

        public long SuspicionsNow { get { lock (suspectLock) return Suspicions; } }
        public volatile bool Forgotten;
        // What its last read of the service found against what was held (for the mirror check); null when that read was not taken.
        public Difference? LastDifference;

        public static Held From(HistoryChatRow row)
        {
            var chat = new Held(row.ChatId) { Seq = row.Seq, ResetSeq = row.ResetSeq };
            if (row.State is { Length: > 0 } saved)
            {
                try
                {
                    if (JsonSerializer.Deserialize<State>(saved) is { } state)
                        chat.State = state.Filled && state.Verified is null && state.Newest is not null ? state with { Verified = state.Floor } : state;
                }
                catch (JsonException) { }
            }
            return chat;
        }
    }

    private sealed class ObservedFeed(ConversationHistory owner, IRealtimeFeed source) : IRealtimeFeed
    {
        public Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken) =>
            source.RunAsync((change, token) =>
            {
                owner.Observe(change);
                return receive(change, token);
            }, cancellationToken);
    }

    /// <summary>
    /// The background's work, each conversation once at the most urgent of what it was asked for, and work put off until a time. Bounded by the number of
    /// conversations, since a conversation is in it at most once.
    /// </summary>
    private sealed class WorkQueue
    {
        // Order: when it was asked for, so work of one urgency goes first come first served (the walk asks for the most recently active first).
        public sealed record Item(string ChatId, Work Work, int Tries, long NotBefore, long Order = 0);
        private readonly object gate = new();
        private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool completed;
        private long order;

        public int Count { get { lock (gate) return items.Count; } }

        public void Add(string chatId, Work work) => Put(new Item(chatId, work, 0, 0));
        public void Later(string chatId, Work work, TimeSpan after, int tries = 0) => Put(new Item(chatId, work, tries, Environment.TickCount64 + (long)after.TotalMilliseconds));

        private void Put(Item item)
        {
            TaskCompletionSource wake;
            lock (gate)
            {
                if (completed) return;
                if (items.TryGetValue(item.ChatId, out var was) && was.Work <= item.Work && was.NotBefore <= item.NotBefore) return;
                items[item.ChatId] = was is null ? item with { Order = ++order }
                    : item with { Work = (Work)Math.Min((int)was.Work, (int)item.Work), Tries = Math.Max(was.Tries, item.Tries), Order = was.Order };
                wake = signal;
            }
            wake.TrySetResult();
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
                        items.Remove(due.ChatId);
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
}
