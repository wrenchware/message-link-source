using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    private Outbox? outbox;
    // Confirmed local sends survive a history request which began before the send finished.
    private readonly Dictionary<string, ChatMessage> confirmations = [];
    private readonly Dictionary<string, string> exactPhotoBatches = [];
    // When each send confirmed here was made on this PC (Outbox CreatedAt), by its message: a send still on its way is
    // drawn after what was sent before it, never after what was sent after it (WithOutgoing).
    private readonly Dictionary<string, DateTimeOffset> sentAt = [];
    // The order each map's keys came in, oldest first, for its cap. A Dictionary's own order is not that once anything has
    // been removed: it fills freed slots last in, first out, so Keys.First() was most often the entry just added, and a full
    // map kept its oldest entries and let every new one go.
    private readonly Queue<string> sentOrder = new(), confirmationOrder = new(), photoBatchOrder = new();
    private static void Keep<TValue>(Dictionary<string, TValue> map, Queue<string> order, string key, int cap)
    {
        order.Enqueue(key);
        while (map.Count > cap && order.TryDequeue(out var oldest))
            // A key given again since is let go at its later place.
            if (!order.Contains(oldest)) map.Remove(oldest);
        // Keys removed by other means leave their places behind; dropped once there are too many.
        if (order.Count > cap * 2)
        {
            var live = order.Reverse().Where(map.ContainsKey).Distinct(StringComparer.Ordinal).Reverse().ToArray();
            order.Clear();
            foreach (var kept in live) order.Enqueue(kept);
        }
    }
    private void NoteSentAt(ChatMessage message, DateTimeOffset at)
    {
        sentAt[MessageIdentity.Key(message)] = at;
        Keep(sentAt, sentOrder, MessageIdentity.Key(message), 1000);
    }
    // A deleted conversation's own entries only; see MessageIdentity.Key for the photo batches' prefix.
    private void ForgetSends(string chatId)
    {
        foreach (var key in confirmations.Keys.Where(key => key.StartsWith(chatId + "\n", StringComparison.Ordinal)).ToArray()) confirmations.Remove(key);
        var prefix = $"{chatId.Length}:{chatId}";
        foreach (var key in exactPhotoBatches.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) exactPhotoBatches.Remove(key);
        foreach (var key in sentAt.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) sentAt.Remove(key);
    }
    public event EventHandler<string?>? ActiveChatChanged;
    public bool IsLiveRender { get; private set; }

    // Whether this message is in the current history snapshot, rather than known only from a send confirmation, and the
    // snapshot is not the saved copy shown while the first page is still being read. A snapshot can still hold rows the
    // previous run saved that are older than the fresh page (HistoryMerge.KeepOlder), so for Instagram — which answers a
    // message's options from memory only for one it has read this session — a view also asks the service's own memory
    // (EditTarget.ProviderHolds).
    public bool IsFromProviderPage(ChatMessage message)
    {
        if (IsShowingSavedHistory || current is not { } snapshot) return false;
        foreach (var row in snapshot.Messages)
            if (row.Id == message.Id && row.ChatId == message.ChatId) return true;
        return false;
    }

    public void AttachOutbox(Outbox value)
    {
        if (outbox is not null) throw new InvalidOperationException("Outbox is already attached.");
        outbox = value; value.Changed += OutboxChanged; value.Resolved += OutboxResolved; value.BatchResolved += OutboxBatchResolved;
    }
    // Send later's messages, drawn after everything else in their conversation (ScheduledSendRows).
    private ScheduledSends? scheduled;
    public void AttachScheduled(ScheduledSends value)
    {
        if (scheduled is not null) throw new InvalidOperationException("Scheduled messages are already attached.");
        scheduled = value; value.Changed += ScheduledChanged;
    }
    private void ScheduledChanged(object? sender, string chatId)
    {
        if (chatId.Length != 0 && !ContainsChat(chatId)) return;
        // In place, keeping the reader's position: a reader at the end stays there as the row comes or goes.
        RenderLive();
    }
    // Sending one message moves it through several states — submitted, accepted, resolved, then echoed back by the
    // provider — and each one used to re-render the whole conversation. A render walks the entire loaded history and
    // builds a row view model for every one of up to a thousand messages to diff against the rows already on screen,
    // so a single send in a busy group ran that four to six times.
    //
    // The one that must not wait is the first: a message has to appear the instant it is sent. Everything after it is
    // a decoration on a row already on screen — a tick changing, an id being confirmed — and those share one render.
    private const int OutboxRenderCoalesceMs = 120;
    private bool renderQueued;
    private void OutboxChanged(object? sender, string chatId)
    {
        if (!ContainsChat(chatId)) return;
        // A send through a service the filter hides (Send later, the console) is not drawn, so it is not a new row to
        // follow; its changes share the ordinary coalesced render.
        var newSend = ShownChatIds()?.Contains(chatId) != false &&
            outbox!.ForChat(chatId).Any(item => item.State is OutgoingState.Sending or OutgoingState.Held && IsNewSend(item));
        if (newSend)
        {
            renderQueued = false;
            // A send from a window of older history goes back to the newest messages, where it appears.
            if (window is not null) { WindowLeaving?.Invoke(this, EventArgs.Empty); window = null; }
            RenderLive(followLatest: true); return;
        }
        if (renderQueued) return;
        renderQueued = true;
        _ = QueueOutboxRenderAsync(generation);
    }
    private async Task QueueOutboxRenderAsync(long version)
    {
        try { await Task.Delay(OutboxRenderCoalesceMs); }
        catch (Exception) { return; }
        // Selecting another conversation, or a render the send itself triggered, retires this one.
        if (!renderQueued || version != generation) { renderQueued = false; return; }
        renderQueued = false;
        RenderLive();
    }
    private void OutboxResolved(object? sender, ChatMessage message)
    {
        // Keep the realized row when the exact outbox transaction gets its server ID.
        // Replacing the list item causes a brief unload/reload and visible bounce.
        var pending=outbox?.ForChat(message.ChatId).FirstOrDefault(item=>item.ConfirmedId==message.Id);
        if (pending is not null) NoteSentAt(message, pending.CreatedAt);
        if (ContainsChat(message.ChatId) && current is not null && pending is not null &&
            !Messages.Any(item => item.Message.ChatId == message.ChatId && item.Message.Id == message.Id) &&
            Messages.FirstOrDefault(row => row.Message.ChatId == message.ChatId && row.Message.Id == pending.LocalId) is { } row)
        {
            var index=Messages.IndexOf(row);
            var lookup=Messages.Select(item=>item.Message).Where(item => item.ChatId == message.ChatId).ToDictionary(item=>item.Id);
            lookup[message.Id]=message;
            var source = IsMerged ? SourceChat(message.ChatId) : current.Chat;
            var chat=WithEncryption(source, current.Messages.Where(item => item.ChatId == message.ChatId));
            // Newest across the whole timeline, as a render decides it: a merged conversation shows one receipt.
            var newest = !Messages.Skip(index + 1).Any(item => !item.Message.IsDeleted && item.Message.Type != "system" && !ScheduledSendRows.Is(item.Message));
            row.ConfirmFrom(new MessageItemViewModel(message,index>0?Messages[index-1].Message:null,chat,lookup,
                index+1<Messages.Count?Messages[index+1].Message:null,CarriesReceipt(Messages.Skip(index + 1), message, chat),newest, IsMerged, quotes));
        }
        confirmations[message.ChatId + "\n" + message.Id] = message;
        Keep(confirmations, confirmationOrder, message.ChatId + "\n" + message.Id, 1000);
        if (ContainsChat(message.ChatId)) RenderLive();
    }

    // Whether a sent message just confirmed carries the receipt, as RenderCore decides it: no later message of yours, and no
    // reply after it once it is known to have arrived.
    private static bool CarriesReceipt(IEnumerable<MessageItemViewModel> after, ChatMessage message, ChatSummary chat)
    {
        var later = after.Select(row => row.Message).Where(m => !m.IsDeleted && m.Type != "system" && !ScheduledSendRows.Is(m)).ToArray();
        return !later.Any(m => m.IsOutgoing) && !(later.Any(m => !m.IsOutgoing) && ReceiptRules.IsDelivered(message, chat));
    }

    private void OutboxBatchResolved(object? sender, ResolvedOutgoingBatch batch)
    {
        foreach (var message in batch.Messages)
        {
            confirmations[message.ChatId + "\n" + message.Id] = message;
            Keep(confirmations, confirmationOrder, message.ChatId + "\n" + message.Id, 1000);
            exactPhotoBatches[MessageIdentity.Key(message)] = batch.Outgoing.LocalId;
            Keep(exactPhotoBatches, photoBatchOrder, MessageIdentity.Key(message), 1000);
            NoteSentAt(message, batch.Outgoing.CreatedAt);
        }
        if (!ContainsChat(batch.Outgoing.ChatId) || current is null) return;
        if (Messages.FirstOrDefault(row => row.Message.ChatId == batch.Outgoing.ChatId && row.Message.Id == batch.Outgoing.LocalId) is { } local)
        {
            // Encryption as a render works it out, so the set's last photo does not gain or lose a lock for a moment.
            var source = WithEncryption(IsMerged ? SourceChat(batch.Outgoing.ChatId) : current.Chat,
                current.Messages.Where(message => message.ChatId == batch.Outgoing.ChatId));
            var lookup = batch.Messages.ToDictionary(message => message.Id);
            var prior = Messages.TakeWhile(row => row != local).LastOrDefault()?.Message;
            // The set's last photo carries the receipt as a render would give it (CarriesReceipt); the lock only while nothing,
            // from any linked service, has come after it.
            var after = Messages.SkipWhile(row => row != local).Skip(1).ToArray();
            var newest = !after.Any(row => !row.Message.IsDeleted && row.Message.Type != "system" && !ScheduledSendRows.Is(row.Message));
            var group = batch.Messages.Select((message, index) => new MessageItemViewModel(message,
                index == 0 ? prior : batch.Messages[index - 1], source, lookup,
                index + 1 < batch.Messages.Count ? batch.Messages[index + 1] : null,
                index == batch.Messages.Count - 1 && CarriesReceipt(after, message, source), newest && index == batch.Messages.Count - 1, IsMerged, quotes)).ToArray();
            PhotoGroups.Apply(group, source.Network, exactPhotoBatches);
            // The existing collage remains the visible final row; hidden member rows can
            // now be inserted before it without passing through a one-photo layout.
            local.ConfirmFrom(group[^1]);
        }
        RenderLive();
    }
    private void RenderLive(bool followLatest = false, bool preserveRecovery = false)
    {
        IsLiveRender = true;
        try { Render(!followLatest, preserveRecovery || needsRecovery); } finally { IsLiveRender = false; }
    }

    // A Messenger restored page is useful immediately, but its completion event is not guaranteed when Facebook
    // answers an anchored fetch with no changed rows. Re-read only the selected conversation with bounded backoff.
    // This is the automatic equivalent of switching away and back, without refreshing the whole inbox.
    private void EnsureRecoveryRetry(long version)
    {
        if (recoveryRetry is not null || selection is null || version != generation) return;
        var retry = CancellationTokenSource.CreateLinkedTokenSource(selection.Token);
        recoveryRetry = retry;
        _ = RecoverSelectedConversationAsync(version, retry);
    }

    // Five rechecks span the 30-second grace (2 + 5 + 8 + 8 + 8 seconds). A provider that takes longer announces its
    // readiness, which reaches the selected conversation through ApplyEventsAsync instead of further polling.
    private const int RecoveryAttempts = 5;
    private async Task RecoverSelectedConversationAsync(long version, CancellationTokenSource retry)
    {
        try
        {
            for (var attempt = 0; attempt < RecoveryAttempts && needsRecovery && version == generation; attempt++)
            {
                await Task.Delay(recoveryDelay?.Invoke(attempt) ?? TimeSpan.FromSeconds(Math.Min(8, 2 + attempt * 3)), retry.Token);
                if (version != generation || ActiveChatId is not { } id || !needsRecovery) return;
                // Let an event-driven refresh finish rather than placing a duplicate read beside it.
                if (IsBusy) continue;
                try
                {
                    // A merged conversation waiting only on services that have not answered asks those alone until they do; the
                    // rest keep their rows, and their own events bring them up to date. Each recheck read every service again.
                    if (RecoveryWaiting(current) is { } waiting && !await AnswersAsync(waiting, retry.Token)) continue;
                    var detail = await reader.GetChatAsync(id, retry.Token);
                    var page = await reader.GetMessagesAsync(id, cancellationToken: retry.Token);
                    retry.Token.ThrowIfCancellationRequested();
                    if (version != generation || ActiveChatId != id || !needsRecovery) return;
                    current = HistoryMerge.KeepOlder(current,
                        WithCards(current, Merge(new(detail, [], null, true, new HashSet<string>()), page)), page.HasMore);
                    if (IsMerged && page.IsRefreshing)
                    {
                        UpdateMergedRecoveryState();
                        cache.Put(current); RenderLive(preserveRecovery: true);
                        await cache.SaveAsync(current);
                        continue;
                    }
                    awaitingProviderRefresh = page.IsRefreshing;
                    if (IsMerged) { mergedRecoveryExpired = false; mergedRecoveryWarning = null; }
                    needsRecovery = false;
                    // The successful recheck is enough to settle the visible recovery state. A provider refresh still
                    // running is followed quietly: its completion event re-reads the page through awaitingProviderRefresh.
                    current = current with { IsRefreshing = false };
                    // Media controls whose load failed while the provider was unavailable try again. Decoded pictures and
                    // downloads in progress are left alone by the view.
                    mediaReloadVersion++;
                    cache.Put(current); Title = detail.Title;
                    RenderLive();
                    await cache.SaveAsync(current);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                // A retry the service refused outright says why when the wait runs out, rather than the "reconnecting" the
                // wait began with (Instagram reports NotReady only while a reconnect is coming).
                catch (Exception ex) when (ex is NativeBeeper.Core.Backend.BackendException { Waits: false }) { recoveryRefusal = (version, Error(ex)); }
                catch (Exception) { /* The existing recovery deadline reveals the actionable error if retries fail. */ }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(recoveryRetry, retry)) recoveryRetry = null;
            retry.Dispose();
        }
    }
    private IEnumerable<ChatMessage> WithOutgoing(IReadOnlyList<ChatMessage> history)
    {
        var rows = history.ToDictionary(MessageIdentity.Key);
        foreach (var message in confirmations.Values.Where(m => ContainsChat(m.ChatId))) rows[MessageIdentity.Key(message)] = message;
        // The network's copy of a sent message can arrive before the send call has returned its id, and for that moment
        // nothing links the two: both were drawn, then one vanished and the list jumped. A still-unlinked pending message
        // is also matched to an outgoing message with the same text sent around the same time — each such message
        // matching at most one pending copy, oldest first, so two identical sends in a row still show as two.
        var targets = IsMerged ? CurrentChat?.MemberChatIds ?? [] : ActiveChatId is null ? [] : new[] { ActiveChatId };
        var pendingItems = targets.SelectMany(id => outbox?.ForChat(id) ?? []).OrderBy(item => item.CreatedAt).ToArray();
        var claimed = pendingItems.SelectMany(item => new[] { item.PendingId, item.ConfirmedId }.OfType<string>().Select(id => MessageIdentity.Key(item.ChatId, id))).ToHashSet();
        // Our own copies that carry the id their receipt named them by (Google's transaction id), by that id.
        Dictionary<string, string>? byReceipt = null;
        if (pendingItems.Any(item => item.ConfirmedId is null && (item.PendingId is not null || item.PendingMessageIds is not null)))
            foreach (var (key, row) in rows)
                if (row.PendingId is { } pendingId) (byReceipt ??= new(StringComparer.Ordinal)).TryAdd(MessageIdentity.Key(row.ChatId, pendingId), key);
        bool HideCopy(string chatId, string id)
        {
            var hidden = rows.Remove(MessageIdentity.Key(chatId, id));
            if (byReceipt?.GetValueOrDefault(MessageIdentity.Key(chatId, id)) is { } copy) hidden |= rows.Remove(copy);
            return hidden;
        }
        var drawn = new List<OutgoingMessage>(pendingItems.Length);
        foreach (var item in pendingItems)
        {
            var pendingPhotoSet = item.Attachments is { Count: > 1 } && item.ConfirmedId is null;
            var copyHidden = false;
            if (pendingPhotoSet)
            {
                // Only receipt-identified copies are covered by the still-pending collage.
                // A same-sender/time guess would hide unrelated photos in a busy group.
                foreach (var id in item.PendingMessageIds ?? (item.PendingId is null ? [] : new[] { item.PendingId }))
                    HideCopy(item.ChatId, id);
            }
            else if ((item.PendingId is not null && rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.PendingId))) || (item.ConfirmedId is not null && rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.ConfirmedId)))) continue;
            // A copy that arrives before its send is confirmed was drawn beside the pending message and then replaced it:
            // for a photo, a new tile that downloaded our own picture again, an empty box and a spinner where the photo had
            // been. Known exactly by its receipt, the copy waits behind the pending message instead, and the confirmation
            // hands the message its identity in place (OutboxResolved).
            else if (item is { ConfirmedId: null, PendingId: { } single }) copyHidden = HideCopy(item.ChatId, single);
            // Linked only by a pending id the network's copy does not carry (Google's transaction id) counts as unlinked too:
            // that gap, between the send call returning and the copy being confirmed, drew a second copy for half a second.
            if (!copyHidden && item.Attachments is not { Count: > 0 } && item.ConfirmedId is null && item.State is OutgoingState.Sending or OutgoingState.Pending &&
                history.FirstOrDefault(m => m.ChatId == item.ChatId && m.IsOutgoing && !claimed.Contains(MessageIdentity.Key(m)) && m.Text == item.Text &&
                    (m.Timestamp - item.CreatedAt).Duration() < TimeSpan.FromMinutes(2)) is { } echo)
            { claimed.Add(MessageIdentity.Key(echo)); continue; }
            drawn.Add(item);
        }
        // A send still on its way is drawn after everything sent before it. Its time is this PC's moment of sending, and a
        // message sent just before it takes the service's time when confirmed, which can be later: photos sent one at a
        // time were confirmed one by one with times after the words written with them, and the words jumped above them.
        // What was sent after it does not count, though it may be confirmed first; each keeps its order among the others.
        var sent = drawn.Count == 0 ? [] : rows.Values.Where(m => m.IsOutgoing)
            .Select(m => (m.Timestamp, From: sentAt.TryGetValue(MessageIdentity.Key(m), out var made) ? made : (DateTimeOffset?)null)).ToArray();
        var after = DateTimeOffset.MinValue;
        foreach (var item in drawn)
        {
            foreach (var (time, from) in sent) if (time > after && (from is null || from < item.CreatedAt)) after = time;
            var at = item.CreatedAt > after ? item.CreatedAt : after.AddTicks(1);
            after = at;
            // A captioned photo is drawn as it will be confirmed: the services that take captions (Messenger, Google
            // Voice) return the caption as the message's text, so it sits in a bubble above the photo. Drawn under the
            // photo until then, the caption jumped above it and the row grew the moment the send was confirmed.
            rows[MessageIdentity.Key(item.ChatId, item.LocalId)] = new(item.LocalId, item.ChatId, "local:self", "You", at, item.LocalId,
                item.Attachments is [{ Caption: { Length: > 0 } caption }] ? caption : item.Attachments is { Count: > 0 } ? null : item.Text,
                item.Attachments is { Count: > 0 } ? "media" : "text",
                true, false, false, null, item.ReplyToId, item.Attachments ?? [], [],
                new(!item.CanRestoreText && item.State == OutgoingState.Rejected ? "LOCAL_ATTACHMENT_REJECTED"
                    : item is { State: OutgoingState.Pending, Unconfirmed: true } ? "LOCAL_UNCONFIRMED"
                    : "LOCAL_" + item.State.ToString().ToUpperInvariant(), null, null, null, new Dictionary<string, bool?>()),
                // Expected route until the phone reports the actual one.
                NativeBeeper.Presentation.Services.ComposeTransport.BubbleFamily(IsMerged ? SourceChat(item.ChatId).ExpectedTransport : ExpectedTransport));
        }
        var ordered = rows.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal);
        // Scheduled messages come after everything sent, soonest first, whatever their time. One the outbox has taken is
        // already drawn above as a send, under the same id.
        ChatMessage[] waiting = scheduled is null ? [] : targets.SelectMany(scheduled.ForChat)
            .Where(item => outbox?.Find(item.Id) is null && !rows.ContainsKey(MessageIdentity.Key(item.ChatId, item.Id)))
            .OrderBy(item => item.Due).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => ScheduledSendRows.Row(item, NativeBeeper.Presentation.Services.ComposeTransport.BubbleFamily(
                IsMerged ? SourceChat(item.ChatId).ExpectedTransport : ExpectedTransport))).ToArray();
        return waiting.Length == 0 ? ordered : ordered.Concat(waiting);
    }

    public async Task ApplyEventsAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, bool full, CancellationToken token)
    {
        var version = generation;
        IsBusy = true;
        try
        {
            await ApplyEventsCoreAsync(lookup, events, full, token);
            NoteDeleted(events);
            // A resync's batch too: the one a connection starts can carry what the service delivers as it connects — the
            // messages that came while the app was closed (Messenger's, 2026-10-02) — and the merges it names are brought up
            // to date where they are kept. After the open conversation has been brought up to date, since looking a message
            // up can ask the service and the open one waits for none of it.
            if (full) await RefreshKeptMergesAsync(lookup, events, token);
        }
        finally { if (version == generation) IsBusy = false; }
        // Conversations of their own, for every batch, once the open one is current and no longer busy: none of it is the
        // open conversation's, and opening another meanwhile is safe (KeptHistory.KeepAsync asks again before it lands).
        await RefreshKeptSinglesAsync(lookup, events, token);
    }
    // A window whose open conversation's service is not connected is passed over by a batch (SyncViewModel), which left the
    // conversations it keeps closed unmarked as well: one reopened from it drew its old copy as current (2026-10-04). They
    // take the batch here, as ApplyEventsAsync gives it to them; the open conversation is left until its service is back.
    public async Task KeepClosedCurrentAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, bool full, CancellationToken token)
    {
        InvalidateCache(events, full);
        ForgetQuotes(events);
        if (!full) await RefreshKeptReceiptsAsync(lookup, events, token);
        await RefreshKeptMergesAsync(lookup, events, token);
        await RefreshKeptSinglesAsync(lookup, events, token);
    }
    // Counts only: whether a message its service said was deleted is still drawn in the open conversation. One still drawn
    // is this view's doing, not the service's (an Instagram unsend stayed drawn until the conversation was reopened, 2026-10-15).
    private void NoteDeleted(IReadOnlyList<RealtimeEvent> events)
    {
        var deleted = events.Where(e => e.Kind == RealtimeKind.MessageDeleted && e.ChatId is not null && ContainsChat(e.ChatId))
            .SelectMany(e => (e.Ids ?? []).Select(id => MessageIdentity.Key(e.ChatId!, id))).ToHashSet(StringComparer.Ordinal);
        if (deleted.Count == 0) return;
        var drawn = Messages.Count(row => deleted.Contains(MessageIdentity.Key(row.Message)));
        Timed?.Invoke(this, $"history deleted named={deleted.Count} still_drawn={drawn} merged={(IsMerged ? "yes" : "no")}");
    }
    // No inactive conversation may reuse history invalidated by an event or reconnect without reading it again. Chats an
    // event doesn't name keep their in-memory history, so reopening them shows current history at once with no read; the
    // ones it names, and every one after a resync, are kept marked to be read when they open, and drawn at once meanwhile.
    private void InvalidateCache(IReadOnlyList<RealtimeEvent> events, bool full)
    {
        if (full || events.Any(e => e.Kind is not (RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted or RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted)
            || e.ChatId is null && (e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted || e.Ids is null)))
        {
            // A deletion that names no conversation could be any of them.
            if (events.Any(e => e.Kind == RealtimeKind.ChatDeleted && e.ChatId is null && e.Ids is null)) { cache.Clear(); return; }
            // A resync or a connection says only that something may have moved: every copy is kept, to be read when it opens
            // (ConversationCache.MarkAllNeedRead), where all were dropped and each reopening fell back to an older saved copy.
            // The open conversation's is read again by the batch; a conversation deleted in it still goes.
            var marked = cache.MarkAllNeedRead();
            foreach (var deleted in events.Where(e => e.Kind == RealtimeKind.ChatDeleted))
                foreach (var id in (deleted.Ids ?? []).Concat(deleted.ChatId is null ? [] : new[] { deleted.ChatId }))
                { cache.Remove(id); if (merges is not null && merges.DisplayId(id) is { } display && display != id) cache.Remove(display); }
            // Counts only: how many copies a resync kept to be read on opening, where it used to drop every one.
            if (marked > 0) Timed?.Invoke(this, $"history kept resync marked={marked} held={cache.SnapshotCount} full={full}");
            return;
        }
        foreach (var change in events)
        {
            // The open conversation's history is put back once the batch has brought it up to date, as before; the rest
            // are kept and brought up to date where they are (RefreshKeptReceiptsAsync).
            if (IsKeptReceipt(change))
            {
                if (ActiveChatId is { } open && (change.ChatId == open || merges?.DisplayId(change.ChatId!) == open)) cache.Remove(open);
                continue;
            }
            // Only a chat event's ids are conversations. A message event's are its messages, and working out which merge
            // each belonged to walked every merge's members per id to find none — megabytes for one backfill batch.
            foreach (var affected in (IsChatEvent(change) ? change.Ids ?? [] : []).Concat(change.ChatId is null ? [] : new[] { change.ChatId }))
            {
                var display = merges?.DisplayId(affected) ?? affected;
                // A conversation that is not open keeps its rows for a change, marked to be read again when it opens; the
                // messages the change names are brought into it where it is (RefreshKeptMergesAsync for a merge,
                // RefreshKeptSinglesAsync for one of its own). Dropped, it reopened on its older saved copy, about a second
                // behind (2026-10-01). Deleted, it goes, and so does a merge a member left.
                if (display != ActiveChatId && change.Kind != RealtimeKind.ChatDeleted && !MergedConversations.IsMerged(affected) && cache.Peek(display) is { } kept)
                { if (!kept.NeedsRead) cache.Replace(kept, kept with { NeedsRead = true }); }
                else cache.Remove(display);
                // A merge's member is drawn only as part of the merge; its own copy goes, as before.
                if (display != affected) cache.Remove(affected);
            }
        }
    }
    // A merged conversation that is not open takes the messages its members' events name, from each service's own copy,
    // into the rows it keeps, so opening it again draws what is there now at once instead of its older saved copy. A row
    // it cannot have leaves it to be read whole on opening, as before. Each window does this for the merges it keeps;
    // a merge open in another window is brought up to date by that window and only lands here if it is still the one kept.
    private async Task RefreshKeptMergesAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, CancellationToken token)
    {
        if (merges is null) return;
        foreach (var named in events.Where(e => e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted && !e.ReceiptOnly &&
            e.ChatId is not null && e.Ids is { Count: > 0 }).GroupBy(e => merges.DisplayId(e.ChatId!)))
        {
            var display = named.Key;
            // Open here or in another window, it is that view's to keep current.
            if (!MergedConversations.IsMerged(display) || display == ActiveChatId || cache.IsShowing(display)) continue;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            // One keeper at a time for this conversation (ConversationCache.KeepingAsync): Instagram's catch-up writing the same
            // saved copy meanwhile lost its rows to this write, or this one's to it.
            using var turn = await cache.KeepingAsync(display, token);
            var snapshot = cache.Peek(display);
            var fromSaved = snapshot is null;
            if (fromSaved)
            {
                // Not opened since launch, or made room for others: its saved copy is what opening would draw first, so that
                // is the copy to bring up to date (2026-10-01: texts that came while it was closed appeared only after the
                // read, 3.9 s, with a 749 px jump). One without the members it has now opens as it always did.
                try { snapshot = await cache.ReadSavedAsync(display); }
                catch (Exception ex) when (ex is not OperationCanceledException) { snapshot = null; }
                if (snapshot is null || merges.AddedSince(display, snapshot.Chat.MemberChatIds) || display == ActiveChatId || cache.Peek(display) is not null) continue;
            }
            var rows = snapshot!.Messages.ToDictionary(MessageIdentity.Key);
            int changed = 0, left = 0;
            var added = false;
            try
            {
                foreach (var change in named)
                    foreach (var messageId in change.Ids!)
                    {
                        var key = MessageIdentity.Key(change.ChatId!, messageId);
                        if (change.Kind == RealtimeKind.MessageDeleted) { if (rows.Remove(key)) changed++; continue; }
                        if (await KeptCopyAsync(lookup, change.ChatId!, messageId, token) is not { } message) { left++; continue; }
                        if (message.ChatId != change.ChatId || message.Id != messageId) throw new InvalidDataException("A different message came back.");
                        // A reaction or an edit to a message older than everything kept would sit above a gap: left to the read.
                        if (!rows.TryGetValue(key, out var before) && KeptHistory.BeforeHeld(snapshot, message)) { left++; continue; }
                        added |= before is null;
                        rows[key] = WithCard(before, message); changed++;
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                if (!fromSaved) cache.Remove(display);
                Timed?.Invoke(this, $"history merged kept outcome=dropped from={(fromSaved ? "saved" : "memory")}");
                continue;
            }
            // A new row says nothing of messages no event named before it (HistorySnapshot.KeptAfter).
            var updated = snapshot with { Messages = OrderMerged(rows.Values), NeedsRead = true, KeptAfter = added ? KeptHistory.ReadThrough(snapshot) : snapshot.KeptAfter };
            bool kept;
            if (!fromSaved) kept = cache.Replace(snapshot, updated);
            // Opened, or brought in by another window, while its messages were being looked up: that copy stands.
            else if (kept = display != ActiveChatId && cache.Peek(display) is null) cache.Put(updated);
            // Written back too, so the first opening after a restart starts from what had arrived before it.
            if (kept) await cache.SaveAsync(updated, keepPlace: true);
            Timed?.Invoke(this, $"history merged kept outcome={(kept ? "updated" : "superseded")} from={(fromSaved ? "saved" : "memory")} " +
                $"changed={changed} left={left} rows={rows.Count} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
        }
    }
    private static bool IsChatEvent(RealtimeEvent change) => change.Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted;
    private static bool IsKeptReceipt(RealtimeEvent change) =>
        change is { ReceiptOnly: true, Kind: RealtimeKind.MessageChanged, ChatId: not null, Ids: not null };

    // Receipts change who has read rows a conversation already holds, not what it holds. A conversation kept here but
    // not open takes the rows they name again from the service's own copy — Instagram answers from memory — instead of
    // being dropped, since dropped, its next opening read the newest page again, which for Instagram is a request. A
    // row the service cannot give drops the kept history as before, and one changed meanwhile, by another window or a
    // later batch, is left as it is. The open conversation is brought up to date by the rest of the batch.
    private async Task RefreshKeptReceiptsAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, CancellationToken token)
    {
        foreach (var change in events.Where(IsKeptReceipt))
        {
            var chat = change.ChatId!;
            foreach (var kept in new[] { chat, merges?.DisplayId(chat) ?? chat }.Distinct(StringComparer.Ordinal))
            {
                if (kept == ActiveChatId || cache.Peek(kept) is not { } snapshot) continue;
                var rows = snapshot.Messages.ToArray();
                var changed = false;
                try
                {
                    foreach (var messageId in change.Ids!)
                    {
                        var index = Array.FindIndex(rows, m => m.ChatId == chat && m.Id == messageId);
                        if (index < 0) continue;
                        // Memory holding only part of it keeps the row as it was.
                        if (await KeptCopyAsync(lookup, chat, messageId, token) is not { } message) continue;
                        if (message.ChatId != chat || message.Id != messageId) throw new InvalidDataException("A different message came back.");
                        rows[index] = WithCard(rows[index], message); changed = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { cache.Remove(kept); continue; }
                if (changed) cache.Replace(snapshot, snapshot with { Messages = rows });
            }
        }
    }

    private async Task ApplyEventsCoreAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events, bool full, CancellationToken token)
    {
        InvalidateCache(events, full);
        ForgetQuotes(events);
        if (!full) { await RefreshKeptReceiptsAsync(lookup, events, token); await RefreshKeptMergesAsync(lookup, events, token); }
        // A conversation drawing a deleted message only from its send confirmation has changed too, though no row of its
        // history went: the open one below redraws for these. It used to find nothing removed and leave the message drawn.
        var unconfirmed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in events.Where(e => e.Kind == RealtimeKind.MessageDeleted))
            foreach (var messageId in change.Ids ?? [])
                if (confirmations.Remove(change.ChatId + "\n" + messageId)) unconfirmed.Add(change.ChatId + "\n" + messageId);
        if (ActiveChatId is null) return;
        var id = ActiveChatId; var version = generation;
        if (IsMerged)
        {
            var relevant = events.Where(e => ContainsChat(e.ChatId) || IsChatEvent(e) && e.Ids?.Any(ContainsChat) == true).ToArray();
            var affected = full || current is null || relevant.Length > 0;
            if (!affected) return;
            // A named message is already available from its provider. Paint it before details or linked history reads,
            // just as a separate conversation does. Chat-only changes and recovery still take the snapshot path.
            if (!full && current is not null && !needsRecovery && !awaitingProviderRefresh &&
                await ApplyMergedMessagesAsync(lookup, relevant, version, token)) return;
            // Receipts change who has read the rows they name, not what the conversation holds, and each service
            // already has those rows. Reading every member's newest page again for one was a request to every service
            // per receipt — Instagram's poll can send one every twenty seconds, and Instagram counts requests.
            if (!full && current is not null && !needsRecovery && !awaitingProviderRefresh && relevant.All(e => e.ReceiptOnly))
            {
                var receiptBefore = current.Messages.ToDictionary(MessageIdentity.Key);
                var receiptRows = new Dictionary<string, ChatMessage>(receiptBefore);
                if (await ResolveMergedChangesAsync(lookup, relevant, receiptRows, version, token) is not true) return;
                current = current with { Messages = OrderMerged(Rebased(receiptBefore, receiptRows, current.Messages)) };
                cache.Put(current); RenderLive(); await cache.SaveAsync(current);
                return;
            }
            // An ordinary batch asks again only about the services it names; the others' details have not moved, and
            // asking Instagram for its conversation again is a request per Google or Messenger message. A full read, a
            // recovering one that is not only waiting on services (below), or a batch that names no linked service by its own
            // id, still asks every service.
            var named = full || current is null ? null : relevant
                .SelectMany(e => (IsChatEvent(e) ? e.Ids ?? [] : []).Append(e.ChatId)).OfType<string>()
                .Where(member => !MergedConversations.IsMerged(member) && ContainsChat(member)).ToHashSet(StringComparer.Ordinal);
            // Waiting only on linked services that have not answered, the batch asks those first, alone. Still not answering,
            // it goes on as an ordinary batch for the services it names besides them, and the conversation keeps waiting;
            // answering, every service is read once, as recovery always settled. Every batch used to read every service
            // again while one was connecting: an Instagram request each time when it was linked, 7-16 of them for one
            // conversation in the half minute after a server restart (2026-10-14).
            var waiting = named is null ? null : RecoveryWaiting(current);
            var recovered = false;
            if (waiting is not null)
            {
                recovered = await AnswersAsync(waiting, token);
                if (version != generation) return;
                if (recovered) named = null;
                else
                {
                    named!.ExceptWith(waiting);
                    // Nothing else named, and nothing to apply to the rows held: the waiting goes on as it was.
                    if (named.Count == 0 && relevant.All(IsChatEvent)) return;
                }
            }
            else if (needsRecovery || awaitingProviderRefresh) named = null;
            var refreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var refreshBegan = GC.GetAllocatedBytesForCurrentThread();
            ChatSummary detail;
            using (MergedChatReader.RefreshOnly(named is { Count: > 0 } ? named : null)) detail = await reader.GetChatAsync(id, token);
            if (version != generation) return;
            var prior = current;
            if (prior is not null)
                foreach (var deleted in events.Where(e => e.Kind == RealtimeKind.MessageDeleted))
                    prior = prior with { Messages = prior.Messages.Where(m => m.ChatId != deleted.ChatId || deleted.Ids?.Contains(m.Id) != true).ToArray() };
            // A message arriving in one linked service used to read every service's newest page again and rebuild the
            // combined paging token from all of them: measured at about a second and seven to ten megabytes for a lone chat
            // event, with an Instagram request among them whichever service had moved. The services an ordinary batch names
            // are read on their own and put back among the rows already loaded; the others' rows stay exactly as they were.
            var why = MergedSpliceRefusal(full, prior, detail, named, relevant, waiting, recovered);
            (HistorySnapshot? Snapshot, string? Why) spliced = why is null ? await SpliceMembersAsync(prior!, named!, version, token) : (null, null);
            if (version != generation) return;
            if (why is null && spliced.Snapshot is null) why = spliced.Why;
            if (spliced.Snapshot is { } splice) current = splice with { Chat = detail };
            else
            {
                var page = await reader.GetMessagesAsync(id, cancellationToken: token);
                if (version != generation) return;
                current = HistoryMerge.KeepOlder(prior, WithCards(prior, Merge(new(detail, [], null, true, new HashSet<string>()), page)), page.HasMore);
            }
            var mergedBefore = current.Messages.ToDictionary(MessageIdentity.Key);
            var mergedRows = new Dictionary<string, ChatMessage>(mergedBefore);
            if (await ResolveMergedChangesAsync(lookup, events, mergedRows, version, token) is null) return;
            current = current with { Messages = OrderMerged(Rebased(mergedBefore, mergedRows, current.Messages)) };
            await PeekQuotesAsync(current.Messages, lookup as IMessagePeek, version, token);
            if (version != generation) return;
            foreach (var message in current.Messages) confirmations.Remove(message.ChatId + "\n" + message.Id);
            UpdateMergedRecoveryState();
            var manualRoute = spliced.Snapshot is null ? null : merges!.Find(current.Chat.Id)?.SendingRoute;
            cache.Put(current); Title = detail.Title; RenderLive();
            Timed?.Invoke(this, $"history merged refresh members={(spliced.Snapshot is null ? current.Chat.MemberChatIds.Count : named!.Count)} " +
                $"of={current.Chat.MemberChatIds.Count} path={(spliced.Snapshot is null ? "full" : "splice")}{(why is null ? "" : " why=" + why)} " +
                $"rows={current.Messages.Count} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds} " +
                $"kb={Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - refreshBegan) / 1024}");
            if (needsRecovery) BeginRecoveryWait(mergedRecoveryWarning!, version, CachedRecoveryGrace);
            // The combined read observes the sending route's arrivals itself; a splice reads one service directly, so its
            // rows are offered here, after they are drawn, as an arrival's are.
            if (spliced.Snapshot is not null)
            {
                await merges!.ObserveIncomingMessagesAsync(current.Chat.Id, current.Messages);
                if (version != generation) return;
                if (manualRoute != merges.Find(current.Chat.Id)?.SendingRoute) RenderLive();
            }
            await cache.SaveAsync(current);
            return;
        }
        // Deselect, not Clear: the history cache is shared with every window, and the send confirmations and photo
        // batches are other conversations' too. InvalidateCache has already dropped this one's kept history.
        if (events.Any(e => e.Kind == RealtimeKind.ChatDeleted && (e.ChatId == id || e.Ids?.Contains(id) == true))) { Deselect(); ForgetSends(id); return; }
        var selectedChanged = events.Any(e => e.ChatId == id || e.Ids?.Contains(id) == true);
        if (full || current is null || (needsRecovery || awaitingProviderRefresh) && selectedChanged)
        {
            var recovering = needsRecovery;
            var waitingQuietly = awaitingProviderRefresh && !needsRecovery;
            var priorConfirmations = confirmations.Keys.ToArray();
            ChatSummary detail; Page<ChatMessage> page;
            try
            {
                detail = await reader.GetChatAsync(id, token);
                page = await reader.GetMessagesAsync(id, cancellationToken: token);
            }
            // A provider that is still connecting, or a recheck this conversation asked for itself, belongs to this
            // conversation's own bounded recovery. Failing the batch would stall synchronization of every other open
            // conversation and the outbox behind one provider. Other resync failures still fail the batch as before.
            catch (Exception ex) when (ex is not OperationCanceledException && (IsNotReady(ex) || !full && current is not null))
            {
                if (version != generation) return;
                if (IsNotReady(ex) && !recovering)
                {
                    needsRecovery = true;
                    // Waited out quietly with or without history on screen, as ReportLoadError does.
                    BeginRecoveryWait(Error(ex), version, CachedRecoveryGrace);
                }
                else if (initialFailureDelay is null && recoveryRetry is null) Status = Error(ex);
                return;
            }
            if (version != generation) return;
            // A reconnect resync keeps older history already loaded rather than shrinking to one page.
            current = HistoryMerge.KeepOlder(current, WithCards(current, Merge(new(detail, [], null, true, new HashSet<string>()), page)), page.HasMore);
            await PeekQuotesAsync(current.Messages, lookup as IMessagePeek, version, token);
            if (version != generation) return;
            awaitingProviderRefresh = page.IsRefreshing;
            needsRecovery = page.IsRefreshing && !recovering && !waitingQuietly;
            if (!needsRecovery) current = current with { IsRefreshing = false };
            if (!page.IsRefreshing) foreach (var key in priorConfirmations) confirmations.Remove(key);
            // Media controls whose load failed while the provider was unavailable try again; the view leaves loaded
            // pictures and downloads in progress alone.
            if (recovering) mediaReloadVersion++;
            cache.Put(current); Title = detail.Title; RenderLive(preserveRecovery: needsRecovery); await cache.SaveAsync(current);
            if (needsRecovery)
                BeginRecoveryWait("Could not finish updating this conversation. Try refreshing or check your connection.", version, CachedRecoveryGrace);
            return;
        }
        // Whether anything below is worth doing. Every batch used to re-sort this conversation's whole history,
        // rebuild a view model for every message in it and write the conversation back to disk — for a message that
        // arrived in a different conversation, which is most of them. Measured at eight hundred messages: two and a
        // half megabytes for an event naming another chat entirely, exactly as much as one naming this one.
        var touched = false;
        // Kept as changes and put to the rows only after the last await below: rows copied before them and written back
        // after dropped any page of older messages read meanwhile, while the paging cursor kept that page's place.
        var changes = new List<(string Id, ChatMessage? Message)>();
        foreach (var change in events.Where(e => e.ChatId == id))
            foreach (var messageId in change.Ids ?? [])
            {
                if (change.Kind == RealtimeKind.MessageDeleted)
                { changes.Add((messageId, null)); touched |= unconfirmed.Contains(id + "\n" + messageId); }
                else if (change.Kind == RealtimeKind.MessageChanged)
                {
                    var message = await lookup.ResolveMessageAsync(id, messageId, token);
                    if (version != generation) return;
                    changes.Add((message.Id, message)); confirmations.Remove(id + "\n" + message.Id);
                }
            }
        if (version != generation) return;
        if (events.Any(e => e.Kind == RealtimeKind.ChatChanged && (e.ChatId == id || e.Ids?.Contains(id) == true)))
        {
            // A provider still connecting keeps the details already shown; its readiness event reads them again.
            try
            {
                var detail = await reader.GetChatAsync(id, token);
                if (version != generation) return;
                current = current with { Chat = detail }; Title = detail.Title;
                touched = true;
            }
            catch (Exception ex) when (IsNotReady(ex)) { if (version != generation) return; }
        }
        var rows = current.Messages.ToDictionary(m => m.Id);
        foreach (var (messageId, message) in changes)
            if (message is null) touched |= rows.Remove(messageId);
            else { rows[message.Id] = WithCard(rows.GetValueOrDefault(message.Id), message); touched = true; }
        // Nothing in this conversation moved, so its history is the history it already had and the rows on screen
        // are already drawing it. A render here would build the same thing again and throw it away.
        //
        // One thing it did for an untouched conversation was worth keeping: a link preview whose request failed
        // while the helper was away gets its next chance from a render, and a batch that changes nothing here is
        // exactly how a reconnect arrives. So the previews are offered again and the rest is not — the same input
        // the render would have handed them, without the view model per message that goes with it.
        if (!touched)
        {
            LoadVisibleLinkPreviews([.. WithOutgoing(current.Messages).Where(m => !m.IsHidden).Select(WithPendingReaction)]);
            return;
        }
        current = current with { Messages = rows.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).TakeLast(ConversationCache.MaxMessages).ToArray() };
        await PeekQuotesAsync(current.Messages, lookup as IMessagePeek, version, token);
        if (version != generation) return;
        cache.Put(current); RenderLive(); await cache.SaveAsync(current);
    }

    private async Task<bool> ApplyMergedMessagesAsync(IMessageSender lookup, IReadOnlyList<RealtimeEvent> events,
        long version, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var messageChats = events.Where(e => e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted &&
            e.ChatId is not null && e.Ids is { Count: > 0 }).Select(e => e.ChatId!).ToHashSet(StringComparer.Ordinal);
        if (events.Count == 0 || events.Any(e => e.Kind switch
        {
            RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted => e.ChatId is null || e.Ids is not { Count: > 0 },
            RealtimeKind.ChatChanged => !((e.Ids ?? []).Append(e.ChatId).OfType<string>().Where(ContainsChat)
                .All(messageChats.Contains)),
            _ => true
        })) return false;
        var before = current!.Messages.ToDictionary(MessageIdentity.Key);
        var rows = new Dictionary<string, ChatMessage>(before);
        foreach (var change in events.Where(e => e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted))
            foreach (var messageId in change.Ids!)
            {
                var key = MessageIdentity.Key(change.ChatId!, messageId);
                if (change.Kind == RealtimeKind.MessageDeleted) { rows.Remove(key); continue; }
                if (change.ReceiptOnly && !rows.ContainsKey(key)) continue;
                try
                {
                    var message = await lookup.ResolveMessageAsync(change.ChatId!, messageId, token);
                    if (version != generation) return true;
                    if (message.ChatId != change.ChatId || message.Id != messageId) return false;
                    rows[key] = WithCard(rows.GetValueOrDefault(key), message);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return false; } // A missing delta needs the authoritative snapshot.
            }
        if (version != generation) return true;
        var basis = current.Messages;
        var arrived = OrderMerged(Rebased(before, rows, basis));
        await PeekQuotesAsync(arrived, lookup as IMessagePeek, version, token);
        if (version != generation) return true;
        current = current with { Messages = ReferenceEquals(current.Messages, basis) ? arrived : OrderMerged(Rebased(before, rows, current.Messages)) };
        foreach (var change in events.Where(e => e.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted))
            foreach (var messageId in change.Ids!) confirmations.Remove(change.ChatId + "\n" + messageId);
        var manualRoute = merges!.Find(current.Chat.Id)?.SendingRoute;
        cache.Put(current); RenderLive();
        Timed?.Invoke(this, $"history merged delta events={events.Count} rows={current.Messages.Count} render_ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        // Route persistence and summary reads must not hold the arriving bubble off screen.
        await merges!.ObserveIncomingMessagesAsync(current.Chat.Id, current.Messages);
        if (version != generation) return true;
        var refreshDetails = events.Any(e => !e.ReceiptOnly);
        if (refreshDetails)
        {
            ChatSummary detail;
            using (MergedChatReader.RefreshOnly(messageChats)) detail = await reader.GetChatAsync(current.Chat.Id, token);
            if (version != generation) return true;
            current = current with { Chat = detail }; Title = detail.Title;
        }
        cache.Put(current);
        if (refreshDetails || manualRoute != merges.Find(current.Chat.Id)?.SendingRoute) RenderLive();
        await cache.SaveAsync(current);
        return true;
    }

    // Asks each service for the loaded rows its changes name. True when any row changed, false when none did, and null
    // when the conversation changed underneath and the answer no longer applies.
    private async Task<bool?> ResolveMergedChangesAsync(IMessageSender lookup, IEnumerable<RealtimeEvent> events,
        Dictionary<string, ChatMessage> rows, long version, CancellationToken token)
    {
        var touched = false;
        foreach (var change in events.Where(e => e.Kind == RealtimeKind.MessageChanged && ContainsChat(e.ChatId)))
            foreach (var messageId in change.Ids ?? [])
            {
                var key = MessageIdentity.Key(change.ChatId!, messageId);
                if (!rows.ContainsKey(key)) continue;
                try
                {
                    var changed = await lookup.ResolveMessageAsync(change.ChatId!, messageId, token);
                    if (version != generation) return null;
                    if (changed.ChatId == change.ChatId && changed.Id == messageId) { rows[key] = WithCard(rows[key], changed); touched = true; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* Retain the loaded row while this source is unavailable. */ }
            }
        return touched;
    }

    // Why a merged batch cannot be brought in by reading only the services it names; null when it can. A resync, a cold or
    // recovering history, a service still answering partly, membership that moved, a call record (whose row is its
    // summary, not a page), or an event naming no linked service by its own id all read every service as before. Waiting
    // only on services that have not answered (RecoveryWaiting) is no reason: those keep their rows, and an event naming one
    // is covered by asking it (AnswersAsync); once they answer, every service is read ("recovered").
    private string? MergedSpliceRefusal(bool full, HistorySnapshot? prior, ChatSummary detail, IReadOnlySet<string>? named, IReadOnlyList<RealtimeEvent> relevant,
        IReadOnlySet<string>? waiting, bool recovered)
    {
        if (full) return "resync";
        if (prior is null) return "cold";
        if (recovered) return "recovered";
        if (waiting is null && (needsRecovery || awaitingProviderRefresh || prior.IsRefreshing || prior.UnavailableSources is { Count: > 0 })) return "recovery";
        if (named is not { Count: > 0 }) return "unnamed";
        if (relevant.Any(e => e.Kind is not (RealtimeKind.ChatChanged or RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted))) return "kind";
        if (relevant.Any(e => !(IsChatEvent(e) ? e.Ids ?? [] : []).Append(e.ChatId).OfType<string>()
            .Any(member => named.Contains(member) || waiting?.Contains(member) == true))) return "unnamed";
        if (!detail.MemberChatIds.ToHashSet(StringComparer.Ordinal).SetEquals(prior.Chat.MemberChatIds)) return "members";
        var members = merges!.Members(detail.Id).Where(m => named.Contains(m.Id)).ToArray();
        if (members.Length != named.Count) return "members";
        if (members.Any(MergedConversations.IsCall)) return "calls";
        return null;
    }

    // The linked services this conversation's recovery waits on, when they alone keep it from being current: members whose
    // newest page did not come (HistorySnapshot.UnavailableSources). Null when there are none, or when one is no longer a
    // member or is a call record, whose row is its summary rather than a page: recovery then reads every service as before.
    // A member answering only partly (a Messenger page still refreshing) is not among them, so it is still read whole.
    // Null too while such a member is refreshing beside them: only a read of every service reads it again, and its completion
    // event may never come. The snapshot cannot say so itself, as one waiting member already marks it refreshing.
    private HashSet<string>? RecoveryWaiting(HistorySnapshot? snapshot)
    {
        if (!IsMerged || merges is null || snapshot?.UnavailableSources is not { Count: > 0 } unavailable) return null;
        if (merges.StillRefreshing(snapshot.Chat.Id).Any(id => !unavailable.Contains(id))) return null;
        var members = merges.Members(snapshot.Chat.Id);
        foreach (var id in unavailable)
            if (members.FirstOrDefault(m => m.Id == id) is not { } member || MergedConversations.IsCall(member)) return null;
        return unavailable.ToHashSet(StringComparer.Ordinal);
    }

    // Whether every service the conversation waits on now gives its newest page whole. Nothing read here is kept: an answer
    // leads to the one read of every service that settles the conversation, as a recheck's always did.
    private async Task<bool> AnswersAsync(IReadOnlySet<string> waiting, CancellationToken token)
    {
        var answers = await Task.WhenAll(waiting.Select(async member =>
        {
            try { return await reader.GetMessagesAsync(member, cancellationToken: token) is { IsRefreshing: false, Warning: null, UnavailableScopes: not { Count: > 0 } }; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return false; }
        }));
        var answered = answers.All(answer => answer);
        Timed?.Invoke(this, $"history merged waiting asked={waiting.Count} answered={answered}");
        return answered;
    }

    // Reads the newest page of each named service and puts it in place of that service's rows in the page's own window,
    // so a message deleted there goes and one edited there changes. Rows of that service older than the page, and every
    // row of the other services, are kept as they were. A page that neither overlaps the service's loaded rows nor
    // reaches below the oldest row loaded may leave a gap, and a page arriving partial or failing says nothing certain:
    // both give up, and the batch reads every service. Rows older than the history loaded so far are left to Load
    // earlier, whose paging token still holds them, so they are not drawn above the rows they belong among.
    private async Task<(HistorySnapshot? Snapshot, string? Why)> SpliceMembersAsync(HistorySnapshot prior, IReadOnlySet<string> named,
        long version, CancellationToken token)
    {
        var reads = await Task.WhenAll(named.Select(async member =>
        {
            try { return (Member: member, Page: (Page<ChatMessage>?)await reader.GetMessagesAsync(member, cancellationToken: token)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return (Member: member, Page: (Page<ChatMessage>?)null); }
        }));
        if (version != generation) return (null, "stale");
        DateTimeOffset? floor = prior.HasMore && prior.Messages.Count > 0 ? prior.Messages.Min(m => m.Timestamp) : null;
        var rows = prior.Messages.ToDictionary(MessageIdentity.Key);
        foreach (var (member, page) in reads)
        {
            if (page is null) return (null, "failed");
            if (page.IsRefreshing || page.Warning is not null || page.UnavailableScopes is { Count: > 0 }) return (null, "unavailable");
            var items = page.Items.Where(m => m.ChatId == member).ToArray();
            var keys = items.Select(MessageIdentity.Key).ToHashSet(StringComparer.Ordinal);
            var start = DateTimeOffset.MinValue;
            if (page.HasMore)
            {
                if (items.Length == 0) return (null, "gap");
                start = items.Min(m => m.Timestamp);
                var overlaps = prior.Messages.Any(m => m.ChatId == member && keys.Contains(MessageIdentity.Key(m))) || floor is { } oldest && start <= oldest;
                if (!overlaps) return (null, "gap");
            }
            foreach (var message in prior.Messages.Where(m => m.ChatId == member && m.Timestamp >= start)) rows.Remove(MessageIdentity.Key(message));
            foreach (var message in items.Where(m => floor is not { } oldest || m.Timestamp >= oldest)) rows[MessageIdentity.Key(message)] = message;
        }
        var messages = OrderMerged(rows.Values);
        var atLimit = messages.Length >= ConversationCache.MaxMessages || prior.AtLimit;
        return (WithCards(prior, prior with { Messages = messages, AtLimit = atLimit, HasMore = prior.HasMore && !atLimit }), null);
    }

    // What a resolve did to its copy of the rows (before → after), put to the rows as they are now. The copy was taken
    // before its awaits, and writing it back whole dropped a page of older messages read meanwhile.
    private static IEnumerable<ChatMessage> Rebased(Dictionary<string, ChatMessage> before, Dictionary<string, ChatMessage> after,
        IReadOnlyList<ChatMessage> now)
    {
        var rows = now.ToDictionary(MessageIdentity.Key);
        foreach (var key in before.Keys) if (!after.ContainsKey(key)) rows.Remove(key);
        foreach (var (key, row) in after)
            if (!before.TryGetValue(key, out var was) || !ReferenceEquals(was, row)) rows[key] = row;
        return rows.Values;
    }

    private static ChatMessage[] OrderMerged(IEnumerable<ChatMessage> messages) =>
        messages.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .ThenBy(MessageIdentity.Key, StringComparer.Ordinal).TakeLast(ConversationCache.MaxMessages).ToArray();
}
