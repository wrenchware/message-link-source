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
using Position = MessageLink.Protocol.HistoryPosition;

namespace MessageLink.Server;

/// <summary>
/// The kept copy of every conversation (SWITCHBOARD.md stage 8): each held from its newest message down to a floor with no gap between, filled from the
/// service's newest page, joined to it on each read, kept up from the events' own reads, and written so that every change takes the next number of one
/// sequence (<see cref="HistoryDatabase"/>) and is announced in the event log. It also holds the conversations waiting for a background read
/// (<see cref="Queue"/>), which the walker takes.
/// </summary>
/// <remarks>
/// A conversation's state changes only once what it says is written: a read cancelled or failed half way leaves it as it was. What it holds is known whole up
/// to <see cref="HeldState.Verified"/>, which only a page read, or an event while nothing was missed, moves. After a provider reconnects (or an event's messages
/// could not all be read) its conversations are suspect: events still write, but only a read of the newest page that reaches back to the verified position
/// (within <see cref="MaxCatchUpPages"/> pages) makes it whole again; one that cannot starts the conversation over, which tells every copy of it to replace
/// itself. A held message missing from inside a page that spans it went on the service (an Instagram unsend made elsewhere shows only so). Each
/// conversation's reads and writes run in order on a chain of its own, and a resolve asked meanwhile waits for the same read.
/// </remarks>
internal sealed class HistoryCopy
{
    /// <summary>The most pages a refresh reads back looking for what it holds before it starts the conversation over.</summary>
    public const int MaxCatchUpPages = 10;
    /// <summary>The most pages a read further back than the floor takes from the service for one answer.</summary>
    public const int MaxOlderPages = 8;
    /// <summary>The most messages one event is read for; an event naming more makes the conversation read again whole.</summary>
    public const int MaxEventIds = 64;

    private readonly HistoryDatabase db;
    private readonly IChatReader chats;
    private readonly IMessageSender sender;
    private readonly IMessagePeek? peek;
    private readonly IHistoryFacts facts;
    private readonly HistoryTimings timings;
    private readonly Action<string> log;
    private readonly CancellationToken stop;
    private readonly ConcurrentDictionary<string, HeldChat> held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ChatMessage?>> resolving = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> scopeResyncs = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HistoryCopy(HistoryDatabase db, IChatReader chats, IMessageSender sender, IMessagePeek? peek, IHistoryFacts facts, HistoryTimings timings,
        Action<string> log, CancellationToken stop)
    {
        (this.db, this.chats, this.sender, this.peek, this.facts, this.timings, this.log, this.stop) = (db, chats, sender, peek, facts, timings, log, stop);
    }

    /// <summary>The conversations waiting for a background read.</summary>
    public HistoryWorkQueue Queue { get; } = new();

    /// <summary>The log the history changes go into.</summary>
    public EventLog? Events { get; set; }

    /// <summary>A conversation's list row is to be built again: its last message is the copy's.</summary>
    public Action<string>? RowChanged { get; set; }

    /// <summary>Opens the store and takes what it holds. False when it could not be opened: then nothing is held, and every read goes on to the services.</summary>
    public async Task<bool> OpenAsync()
    {
        var clock = Stopwatch.GetTimestamp();
        try
        {
            var rows = await db.OpenAsync(stop).ConfigureAwait(false);
            foreach (var row in rows) held[row.ChatId] = HeldChat.From(row);
            log($"history store open chats={rows.Count} filled={FilledCount} seq={db.Seq} bytes={db.Bytes} ms={Ms(clock)}");
            ready.TrySetResult();
            return true;
        }
        catch (Exception failure)
        {
            // Served without a history: every read goes on to the services, as before stage 8.
            log($"history store open failed error={failure.GetType().Name}");
            ready.TrySetException(failure);
            return false;
        }
    }

    public Task Ready => ready.Task;
    public bool IsReady => ready.Task.IsCompletedSuccessfully;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch (Exception failure) when (failure is not OperationCanceledException) { return false; }
    }

    /// <summary>The store's newest number and its size on disk, for the walk's log line.</summary>
    public (long Seq, long Bytes) Size => (db.Seq, db.Bytes);

    // ---- Reads at a revision (protocols 24 and 25). The numbers, the resets and the epoch they are read against stay here. ----

    /// <summary>Called after a patch's changes are read and before it is checked for a reset that crossed them (for the checks, which make one there).</summary>
    internal Action<HeldChat>? AfterChangesRead { get; set; }

    /// <summary>
    /// What a copy of the conversation held at <paramref name="since"/> needs: the changes after it, or, when it can't be patched (no revision, another
    /// epoch, one from before the conversation's last reset, too many changes, or a reset written while they were read), the newest
    /// <paramref name="limit"/> messages to start over from.
    /// </summary>
    public async Task<HistorySyncResult> SinceAsync(HeldChat chat, string? since, int limit, CancellationToken cancellationToken)
    {
        // The numbers come first, so what is read after them is at least that new: a change written meanwhile comes again from that revision in the feed, and a
        // reset meanwhile arrives as a reset, which a copy cannot patch and so asks again.
        long seq, resetSeq;
        lock (chat.Numbers) (seq, resetSeq) = (chat.Seq, chat.ResetSeq);
        var state = chat.State;
        if (Parse(since) is { } at && at <= db.Seq && at >= resetSeq)
        {
            var changed = await db.ReadChangedAsync(chat.ChatId, at, WholeHistoryLimits.MaxChanges + 1, cancellationToken).ConfigureAwait(false);
            AfterChangesRead?.Invoke(chat);
            if (changed.Count <= WholeHistoryLimits.MaxChanges)
            {
                // A reset written while these were read took the messages it replaced away with no row to say so: what was read is no patch then, and the
                // answer starts the copy over instead (review, 2026-10-30).
                bool crossed;
                lock (chat.Numbers) crossed = chat.ResetSeq != resetSeq;
                if (!crossed)
                    return new(Revision(seq), false, changed.Where(row => !row.Removed).Select(Message).OfType<ChatMessage>().ToArray(),
                        changed.Where(row => row.Removed).Select(row => row.MessageId).ToArray(), MoreOlder: true, state.Complete);
            }
        }
        // Numbers first again: what is read after them is at least as new.
        lock (chat.Numbers) seq = chat.Seq;
        state = chat.State;
        var newest = await NewestAsync(chat.ChatId, state, limit, cancellationToken).ConfigureAwait(false);
        return new(Revision(seq), true, newest.Messages, [], newest.More, state.Complete);
    }

    /// <summary>The conversations changed after <paramref name="since"/>, each at its own revision; a start-over when there is no such revision here or too many changed.</summary>
    public async Task<HistoryChangesResult> ChangedChatsAsync(string? since, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Parse(since) is not { } from || from > db.Seq) return new(Revision(db.Seq), true, []);
        // The store's number and the conversations' numbers read together, so the revision handed out never passes a change it does not name.
        var (seq, changed) = await db.ReadChangedChatsAsync(from, WholeHistoryLimits.MaxChangedChats + 1, cancellationToken).ConfigureAwait(false);
        if (changed.Count > WholeHistoryLimits.MaxChangedChats) return new(Revision(seq), true, []);
        return new(Revision(seq), false, changed.Select(row => new HistoryChatRevision(row.ChatId, Revision(row.Seq))).ToArray());
    }

    /// <summary>
    /// Whether a copy matches what is held over its span: the same, different, behind (the conversation moved past its revision, before or while it was read), or
    /// unknown (not held whole, another epoch, or reaching below the floor).
    /// </summary>
    public async Task<HistoryCopyCheck> CompareAsync(HistoryCopyDigest copy, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Whole(copy.ChatId) is not { State: { Filled: true } state } chat || Parse(copy.Revision) is not { } revision || copy.Count < 0)
            return new(copy.ChatId, HistoryCopyOutcomes.Unknown, 0);
        long seq;
        lock (chat.Numbers) seq = chat.Seq;
        var oldest = new Position(copy.OldestTimestamp.UtcTicks, copy.OldestSortKey ?? "", copy.OldestId ?? "");
        if (seq > revision) return new(copy.ChatId, HistoryCopyOutcomes.Behind, 0);
        if (state.Floor is { } floor && oldest.CompareTo(floor) < 0) return new(copy.ChatId, HistoryCopyOutcomes.Unknown, 0);
        var stored = await db.ReadAtOrAfterAsync(copy.ChatId, copy.OldestTimestamp.ToUnixTimeMilliseconds(), copy.Count + 256, cancellationToken).ConfigureAwait(false);
        var span = stored.Select(Message).OfType<ChatMessage>().Where(message => Position.Of(message).CompareTo(oldest) >= 0).ToArray();
        // Read while it may have changed: compared only if the conversation is still where the copy is.
        lock (chat.Numbers) seq = chat.Seq;
        // Far more held over the copy's span than it has is a difference too.
        var outcome = seq > revision ? HistoryCopyOutcomes.Behind
            : stored.Count >= copy.Count + 256 ? HistoryCopyOutcomes.Differ
            : HistoryDigest.Of(span) == copy.Digest ? HistoryCopyOutcomes.Same : HistoryCopyOutcomes.Differ;
        return new(copy.ChatId, outcome, span.Length);
    }

    public int FilledCount => held.Values.Count(chat => chat.State.Filled);

    /// <summary>The conversation's place in the copy, made empty when it has none.</summary>
    public HeldChat Chat(string chatId) => held.GetOrAdd(chatId, id => new HeldChat(id));

    /// <summary>The conversation when it is held whole (filled); null otherwise.</summary>
    public HeldChat? Whole(string chatId) => held.TryGetValue(chatId, out var chat) && chat.State.Filled ? chat : null;

    public bool Holds(string chatId) => held.ContainsKey(chatId);

    /// <summary>Every conversation's event work done so far has finished.</summary>
    public Task AllSettled() => Task.WhenAll(held.Values.Select(chat => chat.Tail));

    public IEnumerable<HeldChat> InScope(string? scope) =>
        held.Values.Where(chat => chat.State.Filled && chat.ChatId.StartsWith(scope ?? "", StringComparison.Ordinal));

    public void Enqueue(string chatId, HistoryWork work) => Queue.Add(chatId, work);

    public string Revision(long seq) => db.Epoch + "." + seq.ToString(CultureInfo.InvariantCulture);

    public long? Parse(string? revision)
    {
        if (revision is null) return null;
        var dot = revision.LastIndexOf('.');
        return dot > 0 && revision.AsSpan(0, dot).SequenceEqual(db.Epoch) && long.TryParse(revision.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq : null;
    }

    // ---- Waiting on what is under way ----

    /// <summary>
    /// Waits, at most <see cref="HistoryTimings.SettleWait"/>, for what the conversation's events have already set going to be written, so a read served just
    /// after an event holds its messages: the event has gone on to the client before them, and a copy read without them a moment earlier was kept as current.
    /// </summary>
    public async Task SettledAsync(HeldChat chat, CancellationToken cancellationToken)
    {
        Task tail;
        lock (chat.ChainLock) tail = chat.Tail;
        if (tail.IsCompleted) return;
        try { await tail.WaitAsync(timings.SettleWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>A message an event's read is bringing right now, waited for (at most <see cref="HistoryTimings.ResolveWait"/>); null when none is on its way.</summary>
    public async Task<ChatMessage?> PendingAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!resolving.TryGetValue(Key(chatId, messageId), out var pending)) return null;
        try { return await pending.WaitAsync(timings.ResolveWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    // What is read here is never written from here: an answer can be a partial copy (Instagram's list words), and the copy takes messages only from pages
    // and the events' own reads.
    public async Task<ChatMessage> ResolveAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        // The event that named it is being read for the copy: wait for that read rather than read the service twice or answer the copy from before it, but
        // never long (a push is waiting behind this): past ResolveWait the service is asked itself.
        if (resolving.TryGetValue(Key(chatId, messageId), out var pending))
        {
            try
            {
                if (await pending.WaitAsync(timings.ResolveWait, cancellationToken).ConfigureAwait(false) is { } fresh) return fresh;
                return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is TimeoutException) { return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false); }
        }
        if (await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false) is { } kept) return kept;
        return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatMessage?> HeldMessageAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!ready.Task.IsCompletedSuccessfully || Whole(chatId) is null) return null;
        var row = await db.ReadOneAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
        return row is { Removed: false } ? Message(row) : null;
    }

    // ---- Events' and this host's own changes, each conversation's in order beside the feed ----

    // A conversation's own work in the order it was asked for, beside the feed. Each step catches its own failures, so one never stops the chain.
    private static void Chain(HeldChat chat, Func<Task> step)
    {
        lock (chat.ChainLock) chat.Tail = chat.Tail.ContinueWith(_ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Reads the named messages from the service at once, together, and writes them in the conversation's own order. The reads are not held behind anything
    /// (a push waits on them through <see cref="ResolveAsync"/>, whose wait is in place before the event goes on); only the writes wait their turn.
    /// </summary>
    public void ReadInto(HeldChat chat, IReadOnlyList<string> ids, HistoryReason reason)
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
                // A service whose event names only the newest of what arrived (Instagram's, between two of its polls: A1, server-mirror-paths.md) never says
                // by it that the copy is whole up to it, and the conversation is read again, within the budget.
                var partial = reason == HistoryReason.Event && !facts.FactsOf(chat.ChatId).EventsNameEveryArrival;
                if (found.Length > 0) await KeepAsync(chat, found, whole: !missed && !partial).ConfigureAwait(false);
                // Something the event named is not in the copy: until a page is read, nothing above it is known whole.
                if (missed) Suspect(chat);
                else if (partial) Enqueue(chat.ChatId, HistoryWork.Refresh);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                log($"history read failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
                Suspect(chat);
            }
        });
    }

    public void Suspect(HeldChat chat)
    {
        chat.MarkSuspect();
        Enqueue(chat.ChatId, HistoryWork.Refresh);
    }

    // A message as whole as a page would give it, or none. Where a service holds some messages only in part (Instagram's list words for a photo), only a copy its
    // whole memory stands behind is taken.
    private async Task<ChatMessage?> ReadWholeAsync(string chatId, string messageId)
    {
        try
        {
            if (peek?.HoldsPartialCopies(chatId) == true) return await peek.PeekWholeAsync(chatId, messageId, stop).ConfigureAwait(false);
            return await sender.ResolveMessageAsync(chatId, messageId, stop).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"history read failed chat={Tag(chatId)} error={failure.GetType().Name}");
            return null;
        }
    }

    /// <summary>A change made through this host, read back after each of <see cref="HistoryTimings.ConfirmAfter"/> in case its service sends no event for it.</summary>
    public void Confirm(string chatId, string messageId)
    {
        if (Whole(chatId) is not { } chat) return;
        _ = Task.Run(async () =>
        {
            foreach (var wait in timings.ConfirmAfter)
            {
                try { await Task.Delay(wait, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                ReadInto(chat, [messageId], HistoryReason.Mutation);
            }
        });
    }

    /// <summary>An edit done on the service: the copy says so at once, and the service's own copy replaces this when it is read back.</summary>
    public void Edited(string chatId, string messageId, string text)
    {
        if (Whole(chatId) is not { } chat) return;
        Chain(chat, async () =>
        {
            if (await HeldMessageAsync(chatId, messageId, CancellationToken.None).ConfigureAwait(false) is { } was)
                await KeepAsync(chat, [was with { Text = text, EditedAt = was.EditedAt ?? DateTimeOffset.UtcNow }], whole: false).ConfigureAwait(false);
        });
    }

    /// <summary>Messages gone (an event's delete, or one removed here for this account alone): gone from every copy, in the conversation's order. False
    /// when the conversation is not held whole, and nothing was asked.</summary>
    public bool Removed(string chatId, IReadOnlyList<string> ids)
    {
        if (Whole(chatId) is not { } chat) return false;
        Chain(chat, () => RemoveAsync(chat, ids));
        return true;
    }

    /// <summary>Lets the conversations go, each under its gate; returns how many of their messages the store dropped.</summary>
    public Task<int> ForgetWhereAsync(Func<string, bool> matches) => ForgetAsync([.. held.Keys.Where(matches)]);

    public async Task<int> ForgetAsync(IReadOnlyList<string> chatIds)
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
                held.TryRemove(new KeyValuePair<string, HeldChat>(id, chat));
            }
            finally { chat.Gate.Release(); }
        }
        return forgotten;
    }

    // ---- Staleness ----

    /// <summary>The service of this scope connected again (or asked for a resync): every conversation in it read before now is stale.</summary>
    public void Resynced(string? scope) => scopeResyncs[scope ?? ""] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool Stale(HeldChat chat)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scope = scopeResyncs.Where(pair => chat.ChatId.StartsWith(pair.Key, StringComparison.Ordinal)).Select(pair => pair.Value).DefaultIfEmpty(0).Max();
        return chat.Suspect || chat.RefreshedAt < scope || now - chat.RefreshedAt > (long)timings.OpenRefreshAfter.TotalMilliseconds;
    }

    // ---- Filling and catching up ----

    /// <summary>The conversation when it is already filled, and null when it is not: nothing is read from the service for it, nor added (protocol 34's held sync).</summary>
    public async Task<HeldChat?> HeldFilledAsync(string chatId, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Whole(chatId);
    }

    public async Task<HeldChat> FilledAsync(string chatId, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var chat = Chat(chatId);
        if (chat.State.Filled) return chat;
        await RefreshAsync(chat, HistoryReason.Open, cancellationToken).ConfigureAwait(false);
        return chat.State.Filled ? chat
            : throw new MessageLink.Core.Backend.BackendException(MessageLink.Core.Backend.BackendFailure.NotReady, "The conversation's history is still loading.");
    }

    /// <summary>
    /// Reads the service's newest page and joins it to what is held (or fills the conversation with it), reading further back until the two meet. Returns the
    /// service's own page, and whether the read was taken: false when it was put off (a page still loading or partial, a further page the budget did not
    /// allow), which changes nothing held. <paramref name="mayReadMore"/>, for a read nobody asked for, is asked before each further page.
    /// </summary>
    public async Task<(Page<ChatMessage>? Page, bool Taken)> RefreshAsync(HeldChat chat, HistoryReason reason, CancellationToken cancellationToken, Func<bool>? mayReadMore = null)
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
                var filled = new HeldState(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
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
                var restarted = new HeldState(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
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
    private static void Settle(HeldChat chat, long suspicions)
    {
        chat.Settle(suspicions);
        chat.RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool Usable(Page<ChatMessage> page) => !page.IsRefreshing && page.Warning is null && page.UnavailableScopes is not { Count: > 0 };

    // Whether a run read from the newest back reaches what is known whole: nothing is, or it reaches back to it.
    private static bool Meets(HeldState state, IReadOnlyList<ChatMessage> gathered) =>
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
        var graceTicks = (DateTimeOffset.UtcNow - timings.AbsenceGrace).UtcTicks;
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

    // ---- Reads of the copy ----

    /// <summary>Messages strictly before a position, from the copy, reading further back from the service while the copy runs out before <paramref name="limit"/>.</summary>
    public async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> OlderPageAsync(HeldChat chat, Position before, int limit, CancellationToken cancellationToken)
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
    private async Task<bool> ExtendAsync(HeldChat chat, CancellationToken cancellationToken)
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
    // when it was not found within the pages allowed: fewer where every page is a request out of the account's budget (Instagram's).
    private async Task<Page<ChatMessage>?> RecoverOlderAsync(string chatId, HeldState state, CancellationToken cancellationToken)
    {
        var limit = facts.FactsOf(chatId).ReadsCostBudget ? 4 : MaxCatchUpPages * 2;
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

    /// <summary>The held messages strictly before a position (and not below the floor), oldest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<ChatMessage>> HeldBeforeAsync(string chatId, HeldState state, Position before, int limit, CancellationToken cancellationToken,
        bool wholeMoments = false)
    {
        return await ReadOrderedAsync(chatId, state, long.MinValue, new DateTimeOffset(before.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            newestFirst: true, limit, message => Position.Of(message).CompareTo(before) < 0, cancellationToken, wholeMoments).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewestAsync(string chatId, HeldState state, int limit, CancellationToken cancellationToken)
    {
        var messages = await ReadOrderedAsync(chatId, state, long.MinValue, long.MaxValue, newestFirst: true, limit + 1, _ => true, cancellationToken)
            .ConfigureAwait(false);
        return (messages.TakeLast(limit).ToArray(), messages.Count > limit || !state.Complete);
    }

    // The held messages at or after a moment, oldest first: the first `limit`, or with `newestFirst` the newest that many.
    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewerAsync(string chatId, HeldState state, DateTimeOffset at, int limit, CancellationToken cancellationToken,
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

    /// <summary>A window around a moment from the copy, when it holds the conversation whole down past that moment; null otherwise.</summary>
    public async Task<HistoryWindow?> WindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken)
    {
        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false) || Whole(chatId) is not { } chat) return null;
        var state = chat.State;
        if (state.Floor is { } floor && at.UtcTicks <= floor.Ticks) return null;
        var before = await HeldBeforeAsync(chatId, state, new Position(at.UtcTicks, "", ""), older + 1, cancellationToken, wholeMoments: true).ConfigureAwait(false);
        var after = await NewerAsync(chatId, state, at, newer, cancellationToken).ConfigureAwait(false);
        var selected = TakeMoments(before, older, newestFirst: true);
        var moreOlder = before.Count > selected.Count || !state.Complete;
        return new HistoryWindow([.. selected, .. after.Messages], moreOlder, after.More);
    }

    /// <summary>The newest held after a moment, and whether they are all there are; null when the conversation is not held.</summary>
    public async Task<HeldHistory?> HeldAfterAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken)
    {
        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false) || Whole(chatId) is not { } chat || limit <= 0) return null;
        await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        var state = chat.State;
        var rows = await NewerAsync(chatId, state, after.AddTicks(1), limit, cancellationToken, newestFirst: true).ConfigureAwait(false);
        // Everything after the moment is here when the copy is known whole, reaches back past it (or to the conversation's start), and the newest asked
        // for cover it all.
        var reaches = state.Floor is null || state.Complete || state.Floor.Value.Ticks <= after.UtcTicks;
        return new HeldHistory(rows.Messages, reaches && !rows.More && !chat.Suspect);
    }

    private async Task<IReadOnlyList<ChatMessage>> ReadOrderedAsync(string chatId, HeldState state, long from, long to, bool newestFirst,
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

    private static bool AboveFloor(HeldState state, ChatMessage message) => state.Floor is not { } floor || Position.Of(message).CompareTo(floor) >= 0;

    // ---- Writing ----

    // Messages an event (or a change made here) brought, under the conversation's gate. They move the newest held; they move what is known whole only when
    // nothing was missed before them and none of them is missing (`whole`).
    private async Task KeepAsync(HeldChat chat, IReadOnlyList<ChatMessage> messages, bool whole)
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
            if (chat.Suspect) Enqueue(chat.ChatId, HistoryWork.Refresh);
            await WriteAsync(chat, kept, [], state with { Newest = top, Verified = verified }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task RemoveAsync(HeldChat chat, IReadOnlyList<string> ids) =>
        Gated(chat, async () =>
        {
            if (chat.State.Filled) await WriteAsync(chat, [], ids, chat.State).ConfigureAwait(false);
        });

    private async Task Gated(HeldChat chat, Func<Task> write)
    {
        try
        {
            await chat.Gate.WaitAsync(stop).ConfigureAwait(false);
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
    private async Task<HistoryWritten> WriteAsync(HeldChat chat, IReadOnlyList<ChatMessage> puts, IReadOnlyList<string> removes, HeldState next, bool reset = false, bool announce = true,
        bool found = false)
    {
        if (chat.Forgotten) return new(chat.Seq, [], [], false);
        var from = chat.Seq;
        var written = await db.WriteAsync(new HistoryEdit(chat.ChatId,
            puts.Select(message => new HistoryPut(message.Id, message.Timestamp.ToUnixTimeMilliseconds(), Bytes(message))).ToArray(),
            removes, JsonSerializer.SerializeToUtf8Bytes(next), reset), CancellationToken.None).ConfigureAwait(false);
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
        if (announce && next.Filled && Events is { } sequence)
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
        if (next.Filled) RowChanged?.Invoke(chat.ChatId);
        // Every message held went while older ones lie before them: nothing is left to serve a newest page from, so it is read again from the service's
        // newest (the server's own copy emptied by removals, ROADMAP.md).
        if (written.Removed.Count > 0 && next.Filled && !next.Complete && await db.CountAsync(chat.ChatId, CancellationToken.None).ConfigureAwait(false) == 0)
        {
            chat.MarkSuspect();
            Enqueue(chat.ChatId, HistoryWork.Refresh);
        }
        return written;
    }

    private static string Key(string chatId, string messageId) => chatId + "\n" + messageId;

    private static string Name(HistoryReason reason) => reason.ToString().ToLowerInvariant();
}

/// <summary>
/// A conversation as the copy holds it: whether it is filled, whether the service said it has nothing older, the service's cursor below the floor, the
/// oldest held (the floor), the newest held, and how far up from the floor it is known whole. Replaced whole, and only once what it says is written.
/// </summary>
internal sealed record HeldState(bool Filled = false, bool Complete = false, string? Cursor = null, Position? Floor = null, Position? Newest = null,
    Position? Verified = null)
{
    public static HeldState Empty { get; } = new();
}

/// <summary>One conversation's place in the copy. Its state is replaced only under its gate (or before anyone can see it); reads take it as it stands.</summary>
internal sealed class HeldChat(string chatId)
{
    public string ChatId { get; } = chatId;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public object ChainLock { get; } = new();
    public Task Tail = Task.CompletedTask;
    public volatile HeldState State = HeldState.Empty;
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
    public MirrorDifference? LastDifference;

    /// <summary>Whether the list moved past what is held, newly: then it is taken as seen, and the caller reads the conversation once for it.</summary>
    public bool MovedPast(DateTimeOffset last)
    {
        if (last.UtcTicks <= SeenActivity || State.Newest is { } newest && last.UtcTicks <= newest.Ticks + TimeSpan.TicksPerSecond * 2) return false;
        SeenActivity = last.UtcTicks;
        return true;
    }

    public static HeldChat From(HistoryChatRow row)
    {
        var chat = new HeldChat(row.ChatId) { Seq = row.Seq, ResetSeq = row.ResetSeq };
        if (row.State is { Length: > 0 } saved)
        {
            try
            {
                if (JsonSerializer.Deserialize<HeldState>(saved) is { } state)
                    chat.State = state.Filled && state.Verified is null && state.Newest is not null ? state with { Verified = state.Floor } : state;
            }
            catch (JsonException) { }
        }
        return chat;
    }
}
