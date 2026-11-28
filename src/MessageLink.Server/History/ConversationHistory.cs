using System.Diagnostics;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.App.Services;
using MessageLink.Core.Services;
using MessageLink.Protocol;
using MessageLink.Storage;
using static MessageLink.Server.HistoryParts;
using Position = MessageLink.Protocol.HistoryPosition;

namespace MessageLink.Server;

/// <summary>
/// The server's whole copy of every conversation, as the ports and protocol answers the host serves. The copy itself is
/// <see cref="HistoryCopy"/>, kept up from the providers' events, which this sees first (<see cref="Over"/>); the list rows are <see cref="HistoryRows"/>, the
/// background reads and the walk <see cref="HistoryWalker"/>, the mirror check <see cref="HistoryMirror"/>. A client asks only for what changed after the
/// number it holds (<see cref="SyncAsync"/>, <see cref="FeedAsync"/>), so its copy is whole by construction. The same copy answers the reads every client
/// version already makes (<c>messages.page</c>, <c>history.held</c>, <c>messages.resolve</c>, the peeks): this stands in front of the router as each of those
/// ports, and passes on whatever it does not hold.
/// </summary>
/// <remarks>
/// <para>The sends, actions and changes pass through here rather than beside it: each port mixes calls the copy answers or reads back with calls it only
/// hands on, and one object in front of the router is simpler for the host to wire than two for the same port.</para>
/// <para>Events never wait here: each conversation's reads and writes run in order on a chain of its own beside the feed. Reads nobody asked for are paced per
/// service, and a service whose reads cost a budget (<see cref="HistoryFacts.ReadsCostBudget"/>, Instagram's) has them one page at a time out of it. Logs
/// counts, states and one-way tags only.</para>
/// </remarks>
internal sealed class ConversationHistory : IWholeHistory, IChatReader, ILinkPreviewLoader, IMessageSender, IMessagePeek, IHistoryWindowReader, IMessageActions,
    IMessageMutations, IDisposable
{
    /// <summary>The conversations a reconnect reads again at once (the most recently active); the rest are read again when opened.</summary>
    public const int CatchUpChats = 100;
    private const string RowsPage = "rows:";

    private readonly HistoryDatabase db;
    private readonly IChatReader chats;
    private readonly IMessageSender sender;
    private readonly IMessagePeek? peek;
    private readonly IHistoryWindowReader? windows;
    private readonly IMessageActions? actions;
    private readonly IMessageMutations? mutations;
    private readonly IHistoryFacts facts;
    private readonly Action<string> log;
    private readonly HistoryTimings timings = new();
    private readonly CancellationTokenSource stop = new();
    private readonly HistoryCopy copy;
    private readonly HistoryRows rows;
    private readonly HistoryMirror mirror;
    private readonly HistoryWalker walker;
    private Task? working;
    private Task? walking;
    private Task? rowing;
    private readonly object syncRefreshGate = new();
    private long syncRefreshMinute;
    private int syncRefreshes;

    /// <param name="facts">Each conversation's service's own facts (its budget, its echo of sends, its events). Every server states them: none taken for granted.</param>
    public ConversationHistory(HistoryDatabase db, IChatReader chats, IMessageSender sender, IMessagePeek? peek, IHistoryWindowReader? windows,
        IMessageActions? actions, IMessageMutations? mutations, Action<string> log, IHistoryFacts facts)
    {
        this.db = db; this.chats = chats; this.sender = sender; this.peek = peek; this.windows = windows; this.actions = actions; this.mutations = mutations;
        this.log = log; this.facts = facts;
        copy = new HistoryCopy(db, chats, sender, peek, this.facts, timings, log, stop.Token);
        rows = new HistoryRows(copy, db, chats, this.facts, timings, log, stop.Token);
        mirror = new HistoryMirror(copy, timings, log);
        walker = new HistoryWalker(copy, rows, mirror, chats, this.facts, timings, log, stop.Token);
        copy.RowChanged = id => rows.Soon(id, read: false);
    }

    // The waits, paces and limits (HistoryTimings says each), set here so the checks can shorten them.
    public TimeSpan Pace { get => timings.Pace; init => timings.Pace = value; }
    public TimeSpan WalkDelay { get => timings.WalkDelay; init => timings.WalkDelay = value; }
    public TimeSpan WalkEvery { get => timings.WalkEvery; init => timings.WalkEvery = value; }
    public int ListPages { get => timings.ListPages; init => timings.ListPages = value; }
    public int DeepListPages { get => timings.DeepListPages; init => timings.DeepListPages = value; }
    public TimeSpan DeepListEvery { get => timings.DeepListEvery; init => timings.DeepListEvery = value; }
    public TimeSpan ListPagePace { get => timings.ListPagePace; init => timings.ListPagePace = value; }
    public TimeSpan OpenRefreshAfter { get => timings.OpenRefreshAfter; init => timings.OpenRefreshAfter = value; }
    public int SyncRefreshesPerMinute { get => timings.SyncRefreshesPerMinute; init => timings.SyncRefreshesPerMinute = value; }
    public IReadOnlyList<TimeSpan> ConfirmAfter { get => timings.ConfirmAfter; init => timings.ConfirmAfter = value; }
    public TimeSpan AbsenceGrace { get => timings.AbsenceGrace; init => timings.AbsenceGrace = value; }
    public TimeSpan RetryAfter { get => timings.RetryAfter; init => timings.RetryAfter = value; }
    public TimeSpan ResolveWait { get => timings.ResolveWait; init => timings.ResolveWait = value; }
    public TimeSpan SettleWait { get => timings.SettleWait; init => timings.SettleWait = value; }
    public TimeSpan RowsDelay { get => timings.RowsDelay; init => timings.RowsDelay = value; }
    public TimeSpan RowsReadyWait { get => timings.RowsReadyWait; init => timings.RowsReadyWait = value; }
    public int MirrorPerService { get => timings.MirrorPerService; init => timings.MirrorPerService = value; }
    public TimeSpan MirrorTimeout { get => timings.MirrorTimeout; init => timings.MirrorTimeout = value; }
    public TimeSpan SentReadBack { get => timings.SentReadBack; init => timings.SentReadBack = value; }

    /// <summary>Opens the store and starts the background work.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            if (!await copy.OpenAsync().ConfigureAwait(false))
            {
                rows.Abandon();
                return;
            }
            working = Task.Run(walker.WorkAsync, CancellationToken.None);
            walking = Task.Run(walker.WalkAsync, CancellationToken.None);
            // The list rows at once (the walk's fills wait their 20 s): a client that starts over is handed the list from them. Asked for before the rows'
            // loop starts, so its first pass reads the list ahead of any row an early event asked for.
            rows.ListDue();
            rowing = Task.Run(rows.RunAsync, CancellationToken.None);
        });
    }

    /// <summary>The store is open (for the checks).</summary>
    internal Task Opened => copy.Ready;

    /// <summary>Conversations waiting for a background read (for the checks).</summary>
    internal int Queued => copy.Queue.Count;

    /// <summary>Every conversation's event work done so far has finished (for the checks).</summary>
    internal Task SettledAsync() => copy.AllSettled();

    /// <summary>The log the history changes go into.</summary>
    public void Attach(EventLog log) => copy.Events = rows.Events = log;

    /// <summary>The providers' feed, each event's messages read into the copy beside it.</summary>
    public IRealtimeFeed Over(IRealtimeFeed source) => new ObservedFeed(this, source);

    internal static string Tag(string chatId) => HistoryParts.Tag(chatId);

    /// <summary>
    /// An account was signed out, removed from the server, or Google's local data was reset (<c>ProtocolServices.AccountEnded</c>): its conversations' copies
    /// go. Never waits.
    /// </summary>
    public void Ended(string account, string? key)
    {
        foreach (var prefix in ServiceTags.EndedPrefixes(account, key)) Forget(account, prefix);
    }

    private void Forget(string account, string prefix)
    {
        bool Matches(string id) => ServiceTags.EndedOwns(prefix, id);
        _ = Task.Run(async () =>
        {
            try
            {
                await copy.Ready.ConfigureAwait(false);
                var forgotten = await copy.ForgetWhereAsync(Matches).ConfigureAwait(false);
                // Its list rows leave the feed too, the conversations never filled among them.
                var gone = await rows.ForgetWhereAsync(Matches).ConfigureAwait(false);
                log($"history store forgotten account={account} chats={forgotten} rows={gone}");
            }
            catch (Exception failure) when (!stop.IsCancellationRequested) { log($"history store forget failed error={failure.GetType().Name}"); }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        copy.Queue.Complete();
        try { Task.WaitAll([.. new[] { working, walking, rowing }.OfType<Task>()], TimeSpan.FromSeconds(2)); } catch (Exception) { }
        db.Dispose();
    }

    // ---- IWholeHistory (protocol 24) ----

    public async Task<HistorySyncResult> SyncAsync(HistorySyncParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var heldOnly = request.HeldOnly == true;
        // A held sync (protocol 34) asks no service anything: one the server has not filled yet is refused as still loading, and nothing is read for it.
        var chat = heldOnly
            ? await copy.HeldFilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false)
                ?? throw new MessageLink.Core.Backend.BackendException(MessageLink.Core.Backend.BackendFailure.NotReady, "The server does not hold this conversation yet.")
            : await copy.FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        await copy.SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        // Not under Gate, which a read of the service holds for as long as that takes (a page waits at most SettleWait for the events under way, and so does this).
        var answer = await copy.SinceAsync(chat, request.Since, limit, cancellationToken).ConfigureAwait(false);
        // A sync is how a client of 24 opens a conversation (and reads its newest after an event), so it is read again behind the answer when stale, as a
        // newest page is: otherwise a conversation the service changed without an event stays as it was until the walk. Queued after the answer, so the
        // read cannot take Gate ahead of the events this sync waited for. A sync cannot say it is an open rather than a catch-up, so these are few a minute,
        // and never where reads cost a budget: a read nobody asked for is not one to make there.
        // A held sync is a phone keeping its copies ahead, not an open: it reads nothing behind its answer either.
        if (!heldOnly && copy.Stale(chat) && !facts.FactsOf(request.ChatId).ReadsCostBudget && TakeSyncRefresh()) copy.Enqueue(request.ChatId, HistoryWork.Open);
        return answer;
    }

    private bool TakeSyncRefresh()
    {
        var minute = Environment.TickCount64 / 60_000;
        lock (syncRefreshGate)
        {
            if (minute != syncRefreshMinute) (syncRefreshMinute, syncRefreshes) = (minute, 0);
            return ++syncRefreshes <= timings.SyncRefreshesPerMinute;
        }
    }

    public async Task<HistoryOlderResult> OlderAsync(HistoryOlderParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var chat = await copy.FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        var before = new Position(request.BeforeTimestamp.UtcTicks, request.BeforeSortKey ?? "", request.BeforeId ?? "");
        if (request.HeldOnly == true)
        {
            // Export's count and copy: what the server holds and nothing more, so no service is asked anything past the fill above.
            var held = await copy.HeldBeforeAsync(chat.ChatId, chat.State, before, limit + 1, cancellationToken).ConfigureAwait(false);
            return new(held.TakeLast(limit).ToArray(), held.Count > limit, chat.State.Complete);
        }
        var page = await copy.OlderPageAsync(chat, before, limit, cancellationToken).ConfigureAwait(false);
        return new(page.Messages, page.More, chat.State.Complete);
    }

    public Task<HistoryChangesResult> ChangesAsync(HistoryChangesParams request, CancellationToken cancellationToken = default) =>
        copy.ChangedChatsAsync(request.Since, cancellationToken);

    // ---- Protocol 25: the one ordered feed, and the copy checks ----

    public async Task<HistoryFeedResult> FeedAsync(HistoryFeedParams request, CancellationToken cancellationToken = default)
    {
        await copy.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultFeed, 1, WholeHistoryLimits.MaxFeed);
        var clock = Stopwatch.GetTimestamp();
        // A start-over carrying on: list rows alone, from where its last answer ended.
        if (request.Page is { } page)
        {
            if (!page.StartsWith(RowsPage, StringComparison.Ordinal)) throw new ArgumentException("Invalid feed page.");
            // One handed out before the store started over (another epoch): the start-over begins again.
            if (copy.Parse(page[RowsPage.Length..]) is not { } from) return await RowsFromAsync(0, reset: true, limit, clock, cancellationToken).ConfigureAwait(false);
            return await RowsFromAsync(from, reset: false, limit, clock, cancellationToken).ConfigureAwait(false);
        }
        // Nothing held, or held under another epoch: the whole list, and every copy read again when opened. Messages are not carried (every one held would
        // come after 0); a copy is filled when its conversation is opened.
        if (copy.Parse(request.Since) is not { } since || since > db.Seq)
        {
            try { await rows.Ready.WaitAsync(timings.RowsReadyWait, cancellationToken).ConfigureAwait(false); }
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
            changes.Add(new(chat.ChatId, copy.Revision(chat.From), copy.Revision(chat.To), messages, removed));
        }
        var (served, gone) = Rows(read.Rows);
        log($"history feed reset=0 rows={served.Count} gone={gone.Count} chats={changes.Count} messages={read.Chats.Sum(chat => chat.Messages.Count)} more={(read.More ? 1 : 0)} ms={Ms(clock)}");
        return new(copy.Revision(read.Through), false, served, gone, changes, read.More);
    }

    // What events and rows already under way have to write, waited for (at most SettleWait), before the feed is read.
    private async Task FeedSettledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await copy.AllSettled().WaitAsync(timings.SettleWait, cancellationToken).ConfigureAwait(false);
            await rows.Idle.WaitAsync(timings.SettleWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
    }

    private async Task<HistoryFeedResult> RowsFromAsync(long from, bool reset, int limit, long clock, CancellationToken cancellationToken)
    {
        var read = await db.ReadFeedAsync(from, limit, WholeHistoryLimits.MaxFeedBytes, rowsOnly: true, cancellationToken).ConfigureAwait(false);
        var (served, gone) = Rows(read.Rows);
        log($"history feed reset=1 rows={served.Count} gone={gone.Count} more={(read.More ? 1 : 0)} ms={Ms(clock)}");
        // Rows that left the list are nothing to a list being replaced, but one carrying on still needs them.
        return new(copy.Revision(read.Through), reset, served, reset ? [] : gone, [], false, read.More ? RowsPage + copy.Revision(read.Through) : null);
    }

    private static (IReadOnlyList<ChatSummary> Rows, IReadOnlyList<string> Gone) Rows(IReadOnlyList<HistoryFeedRow> read)
    {
        var served = new List<ChatSummary>();
        var gone = new List<string>();
        foreach (var row in read)
        {
            if (row.Payload is null) { gone.Add(row.ChatId); continue; }
            try { if (JsonSerializer.Deserialize<ChatSummary>(row.Payload, ProtocolJson.Options) is { } summary) served.Add(summary); }
            catch (JsonException) { }
        }
        return (served, gone);
    }

    public async Task<HistoryVerifyResult> VerifyAsync(HistoryVerifyParams request, CancellationToken cancellationToken = default)
    {
        await copy.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (request.Chats.Count > WholeHistoryLimits.MaxVerify) throw new ArgumentException("Too many copies to compare.");
        var answers = new List<HistoryCopyCheck>(request.Chats.Count);
        int same = 0, differ = 0, behind = 0, unknown = 0;
        foreach (var held in request.Chats)
        {
            var check = await copy.CompareAsync(held, cancellationToken).ConfigureAwait(false);
            switch (check.Outcome)
            {
                case HistoryCopyOutcomes.Same: same++; break;
                case HistoryCopyOutcomes.Differ: differ++; break;
                case HistoryCopyOutcomes.Behind: behind++; break;
                default: unknown++; break;
            }
            answers.Add(check);
        }
        log($"history verify chats={answers.Count} same={same} differ={differ} behind={behind} unknown={unknown}");
        return new(answers);
    }

    public async Task<HistoryMirrorResult> MirrorAsync(HistoryMirrorParams request, CancellationToken cancellationToken = default)
    {
        await copy.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        // From the list as last read; a server that has not read it yet reads it now.
        if (request.Start) mirror.Start(rows.Listed ?? await rows.ReadListAsync(stop.Token).ConfigureAwait(false) ?? [], "asked");
        return mirror.Result();
    }

    // ---- IChatReader: the list as the services give it, each row's last message from the copy; a conversation's pages from the copy ----

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var page = await chats.GetChatsAsync(olderThan, cancellationToken).ConfigureAwait(false);
        if (!copy.IsReady) return page;
        return page with { Items = await rows.PageForClientAsync(page.Items, cancellationToken).ConfigureAwait(false) };
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var row = await chats.GetChatAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!copy.IsReady) return row;
        // What a client was just told, the feed says too.
        return await rows.ReadForClientAsync(chatId, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // A cursor of the service's (handed out before this copy held the conversation) goes on to it, as before.
        if (olderThan is not null && !HistoryPosition.IsCursor(olderThan))
            return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        if (!await copy.IsReadyAsync(cancellationToken).ConfigureAwait(false)) return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        var chat = copy.Chat(chatId);
        var clock = Stopwatch.GetTimestamp();
        if (!chat.State.Filled)
        {
            var (page, _) = await copy.RefreshAsync(chat, HistoryReason.Open, cancellationToken).ConfigureAwait(false);
            // A page the service is still loading (or one that came back partial) is handed on as it is, and the copy waits for a whole one.
            if (!chat.State.Filled) return page ?? await chats.GetMessagesAsync(chatId, null, cancellationToken).ConfigureAwait(false);
        }
        else if (olderThan is null && copy.Stale(chat)) copy.Enqueue(chatId, HistoryWork.Open);
        if (olderThan is null) await copy.SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        var state = chat.State;
        (IReadOnlyList<ChatMessage> Messages, bool More) result = olderThan is null
            ? await copy.NewestAsync(chatId, state, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : HistoryPosition.ReadCursor(olderThan) is { } before ? await copy.OlderPageAsync(chat, before, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : throw new ArgumentException("Invalid page position.");
        log($"history serve method=page chat={Tag(chatId)} older={(olderThan is not null ? 1 : 0)} rows={result.Messages.Count} more={(result.More ? 1 : 0)} ms={Ms(clock)}");
        return new(result.Messages, result.More, result.More && result.Messages.Count > 0 ? HistoryPosition.Cursor(result.Messages[0]) : null, null);
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

    /// <summary>A conversation a first message just made: filled now, as an open would (the person has just sent), rather than at the walk's next pass.</summary>
    public void Started(string chatId)
    {
        if (!copy.IsReady) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(timings.SentReadBack, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            rows.Soon(chatId, read: true);
            copy.Enqueue(chatId, HistoryWork.Open);
        });
    }

    // A send to a conversation whose service does not announce its own sends (HistoryFacts.EchoesOwnSends: Instagram's) is read back as an open is, the
    // person having just sent: one read per send, and what it finds is announced as any find is. Files and scheduled sends come here too (HistoryReadBack).
    internal void ReadBackSent(string chatId)
    {
        if (facts.FactsOf(chatId).EchoesOwnSends || copy.Whole(chatId) is null) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(timings.SentReadBack, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            copy.Enqueue(chatId, HistoryWork.Open);
        });
    }

    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        copy.ResolveAsync(chatId, messageId, cancellationToken);

    // ---- IMessagePeek ----

    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await copy.PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await copy.HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    // The copy keeps only whole messages (pages, and event reads a whole copy stands behind), so what it holds is whole, and so is what an event's read brings.
    public async ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await copy.PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await copy.HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekWholeAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    public bool HoldsPartialCopies(string chatId) => peek?.HoldsPartialCopies(chatId) ?? false;

    // ---- IHistoryWindowReader ----

    public bool CanReadWindow(string chatId) => windows?.CanReadWindow(chatId) ?? false;

    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default) =>
        await copy.WindowAsync(chatId, at, older, newer, cancellationToken).ConfigureAwait(false)
            ?? (windows is null ? throw new NotSupportedException() : await windows.ReadWindowAsync(chatId, at, older, newer, cancellationToken).ConfigureAwait(false));

    public async Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default) =>
        await copy.HeldAfterAsync(chatId, after, limit, cancellationToken).ConfigureAwait(false)
            ?? (windows is null ? null : await windows.ReadHeldAsync(chatId, after, limit, cancellationToken).ConfigureAwait(false));

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
        copy.Confirm(chatId, messageId);
    }

    public async Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).EditAsync(chatId, messageId, originalText, text, cancellationToken).ConfigureAwait(false);
        copy.Edited(chatId, messageId, text);
        copy.Confirm(chatId, messageId);
    }

    public async Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).DeleteAsync(chatId, messageId, forEveryone, cancellationToken).ConfigureAwait(false);
        // Removed for this account alone: gone from every copy at once. Unsent for everyone: the service says how it shows, so it is read back.
        if (forEveryone || !copy.Removed(chatId, [messageId])) copy.Confirm(chatId, messageId);
    }

    // ---- Events: queued beside the feed, never waited for ----

    private readonly List<RealtimeEvent> early = [];
    private bool earlyTaken;

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

    private async Task TakeEarlyAsync()
    {
        try { await copy.Ready.ConfigureAwait(false); }
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
        if (!copy.IsReady) return;
        try { Handle(change); }
        catch (Exception failure) { log($"history event failed kind={change.Kind} error={failure.GetType().Name}"); }
    }

    private void Handle(RealtimeEvent change)
    {
        switch (change.Kind)
        {
            case RealtimeKind.MessageChanged when change.ChatId is { } chatId:
            {
                rows.Soon(chatId, read: true);
                var chat = copy.Chat(chatId);
                if (!chat.State.Filled) { copy.Enqueue(chatId, HistoryWork.Arrival); return; }
                if (change.Ids is not { Count: > 0 } ids) { copy.Enqueue(chatId, HistoryWork.Refresh); return; }
                if (ids.Count > HistoryCopy.MaxEventIds) copy.Suspect(chat);
                copy.ReadInto(chat, [.. ids.Distinct(StringComparer.Ordinal).Take(HistoryCopy.MaxEventIds)], HistoryReason.Event);
                return;
            }
            case RealtimeKind.MessageDeleted when change.ChatId is { } chatId && change.Ids is { Count: > 0 } ids:
                rows.Soon(chatId, read: true);
                copy.Removed(chatId, [.. ids]);
                return;
            case RealtimeKind.ChatChanged:
                // Every row it names is built again (a read of the router's own list), however many: a Messenger reconnect names them all.
                foreach (var id in Named(change)) rows.Soon(id, read: true);
                foreach (var id in Named(change).Take(HistoryCopy.MaxEventIds))
                {
                    // Where reads cost a budget (Instagram's), arrivals come as their own events from its own reads; a read here would cost the budget for a
                    // row that moved.
                    if (facts.FactsOf(id).ReadsCostBudget) continue;
                    copy.Enqueue(id, copy.Whole(id) is not null ? HistoryWork.Check : HistoryWork.Arrival);
                }
                return;
            case RealtimeKind.ChatDeleted:
            {
                var named = Named(change).ToArray();
                rows.Drop(named);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await copy.ForgetAsync([.. named.Where(copy.Holds)]).ConfigureAwait(false);
                        await rows.ForgetAsync(named).ConfigureAwait(false);
                    }
                    catch (Exception failure) when (!stop.IsCancellationRequested) { log($"history forget failed error={failure.GetType().Name}"); }
                });
                return;
            }
            case RealtimeKind.Disconnected:
                // What arrives until the service is back may have a gap below it: only a read of the newest page makes these whole again.
                foreach (var chat in copy.InScope(change.Scope)) chat.MarkSuspect();
                return;
            case RealtimeKind.Connected or RealtimeKind.ResyncRequired:
            {
                copy.Resynced(change.Scope);
                // Whatever the list did while the service was away: read it whole again.
                rows.ListDue();
                var scoped = copy.InScope(change.Scope).ToArray();
                foreach (var chat in scoped) chat.MarkSuspect();
                // Whatever these did while the service was away is read again: the most recently active now, the rest when opened.
                foreach (var chat in scoped.OrderByDescending(chat => chat.State.Newest?.Ticks ?? 0).Take(CatchUpChats)) copy.Enqueue(chat.ChatId, HistoryWork.CatchUp);
                return;
            }
        }
    }

    private static IEnumerable<string> Named(RealtimeEvent change) =>
        (change.ChatId is { } one ? [one] : Array.Empty<string>()).Concat(change.Ids ?? []).Distinct(StringComparer.Ordinal);

    private sealed class ObservedFeed(ConversationHistory owner, IRealtimeFeed source) : IRealtimeFeed
    {
        public Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken) =>
            source.RunAsync((change, token) =>
            {
                owner.Observe(change);
                return receive(change, token);
            }, cancellationToken);
    }
}
