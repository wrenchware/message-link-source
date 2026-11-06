using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Held (appended) is Undo send's wait: on this PC only, nothing has reached the provider yet.
public enum OutgoingState { Sending, Pending, Rejected, Unknown, Held }
public sealed record OutgoingMessage(string LocalId, string ChatId, string Text, DateTimeOffset CreatedAt,
    OutgoingState State, string? PendingId = null, string? Explanation = null, string? ConfirmedId = null, bool CanRestoreText = true,
    string? ReplyToId = null, IReadOnlyList<MessageAttachment>? Attachments = null,
    // Set once every automatic confirmation check has finished without the provider knowing the message. Every send is
    // briefly pending between its receipt and its echo; only one still unknown after the background checks says so.
    bool Unconfirmed = false, IReadOnlyList<string>? PendingMessageIds = null,
    // The host's name for an unknown send whose connection ended before its answer (SendException.OperationId), so the host's journal
    // can later say what became of it. Never shown.
    string? OperationId = null);

public sealed record ResolvedOutgoingBatch(OutgoingMessage Outgoing, IReadOnlyList<ChatMessage> Messages);
// How a photo set sent one at a time ended (Outbox.SendSetAsync). Handled counts the photos, from the first, that have left
// the draft: accepted, or possibly sent. Failed is the state of the photo after them when one failed; null when none did.
public sealed record PhotoSetOutcome(int Handled, OutgoingState? Failed, string? Explanation = null);

// Owned by the UI context, independent of conversation selection. Never retries a POST.
// followUpDelays replaces the background confirmation schedule, and lookupStep the spacing of the quick lookups after a
// receipt (2 s, then 4 s, then 6 s); the offline checks use them to keep those checks fast.
// outcomes, when the sender is a host over a connection that can be lost, says what became of a send whose answer was lost
// (SWITCHBOARD.md 3.0c); it arrives on another thread, so dispatcher (the interface thread) is where it is applied, and is required
// with outcomes. This class is used only on that thread, so an answer applied there is ordered against SettleEarly.
public sealed class Outbox(IMessageSender sender, IReadOnlyList<TimeSpan>? followUpDelays = null, TimeSpan? lookupStep = null,
    IOperationOutcomes? outcomes = null, IUiDispatcher? dispatcher = null) : IDisposable
{
    private readonly IUiDispatcher? applyOn = outcomes is not null && dispatcher is null
        ? throw new ArgumentException("Outcomes are applied on the interface thread: a dispatcher is required with them.", nameof(dispatcher))
        : dispatcher;
    private readonly TimeSpan step = lookupStep ?? TimeSpan.FromSeconds(2);
    // After the quick checks, a few spaced lookups (never a resend) before a message is shown as not confirmed yet.
    private static readonly TimeSpan[] DefaultFollowUps = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
    private readonly Dictionary<string, OutgoingMessage> messages = [];
    private CancellationTokenSource lifetime = new();
    private int generation;
    public event EventHandler<string>? Changed;
    public event EventHandler<ChatMessage>? Resolved;
    public event EventHandler<ResolvedOutgoingBatch>? BatchResolved;
    // One send by its local id, each time its state changes and once more when it leaves the outbox (Find is then null).
    // Changed names only the conversation, which is all a composer needs; a caller following one send needs this.
    public event EventHandler<string>? ItemChanged;
    // One line per send's outcome (send outcome=... ms=...), reported rather than logged: this class is shared presentation
    // and knows nothing about where a log lives. Words and numbers only, never a conversation, message or its text.
    public event EventHandler<string>? Timed;
    // When each send in the outbox began, as a monotonic stamp (Stopwatch.GetTimestamp), by local id. The clock starts when
    // the send is accepted here, and again when a wait that is not the provider's ends: Undo send's hold, and the photos
    // that words queued behind them wait for. Dropped with the send (Notify), kept while it is unconfirmed so a late echo
    // still reports how long it really took.
    private readonly Dictionary<string, long> started = [];
    public OutgoingMessage? Find(string localId) => messages.GetValueOrDefault(localId);
    private void Notify(string chatId, string localId)
    {
        if (!messages.ContainsKey(localId)) started.Remove(localId);
        Changed?.Invoke(this, chatId); ItemChanged?.Invoke(this, localId);
    }
    private void Restart(string localId) => started[localId] = System.Diagnostics.Stopwatch.GetTimestamp();
    // outcome: confirmed, unknown, failed or unconfirmed. Only unconfirmed leaves the clock running, because the message
    // can still be confirmed later; any other outcome ends it, so each send reports once.
    private void Report(OutgoingMessage item, string outcome, string? via = null)
    {
        long at;
        if (outcome == "unconfirmed" ? !started.TryGetValue(item.LocalId, out at) : !started.Remove(item.LocalId, out at)) return;
        var service = ServiceScopes.Name(item.ChatId).ToLowerInvariant().Replace(' ', '_');
        Timed?.Invoke(this, $"send outcome={outcome} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds}" +
            $"{(via is null ? "" : " via=" + via)} service={service}");
    }
    public IReadOnlyList<OutgoingMessage> ForChat(string chatId) => messages.Values.Where(m => m.ChatId == chatId).OrderBy(m => m.CreatedAt).ToArray();
    public bool HasUnsettled => messages.Count > 0;
    // Sends handed to the provider whose answer has not come back: what a quit waits for before it lets the connection go.
    public int SendingCount => messages.Values.Count(m => m.State == OutgoingState.Sending);
    // Sends whose outcome is still open (sending, pending or unknown); a rejected send is settled and restorable.
    public IReadOnlyList<OutgoingMessage> Unsettled => messages.Values.Where(m => m.State != OutgoingState.Rejected).OrderBy(m => m.CreatedAt).ToArray();

    public Task SendAsync(string chatId, string text, object? holdFor = null, Func<Task<bool>>? holdEnding = null, string? localId = null) =>
        SendOperationAsync(chatId, text, ct => sender.SendTextAsync(chatId, text, ct), true, holdFor: holdFor, localId: localId, holdEnding: holdEnding);

    // Undo send. A send made with holdFor (the composer that made it) first waits here for Hold, on this PC only: until it
    // ends nothing has reached the provider, so Undo takes it back whole and hands it to that composer (Undone). When the
    // wait ends it is sent as any other, once. Quitting used to end every wait by sending at once; it now takes each back
    // as Undo does (TakeHeld) and the words go to their conversation's saved draft (HeldSends, 2026-10-04): the wait is
    // there to take a message back, and quitting inside it is the last moment anyone could. Zero, the default, sends at once.
    public TimeSpan Hold { get; set; }
    private static readonly TimeSpan HoldEndingLimit = TimeSpan.FromSeconds(2);
    private sealed record Holding(CancellationTokenSource Undo, object Owner);
    private readonly Dictionary<string, Holding> holds = [];
    public sealed record UndoneSend(OutgoingMessage Message, object Owner);
    public event EventHandler<UndoneSend>? Undone;
    // Still in its wait. A hold stays listed while the send it let go is under way, so the state is what says so.
    public bool IsHeld(string localId) =>
        holds.ContainsKey(localId) && messages.TryGetValue(localId, out var item) && item.State == OutgoingState.Held;
    public bool IsHolding => holds.Keys.Any(IsHeld);

    /// <summary>Takes back a send still waiting to go. False once it has gone to the provider, or was never held.</summary>
    public bool TryUndo(string localId)
    {
        // Held, not just listed: a send already let go stays in holds until it settles, and must not be taken back then.
        if (!IsHeld(localId) || !holds.TryGetValue(localId, out var holding) || !messages.Remove(localId, out var item)) return false;
        holding.Undo.Cancel();
        Notify(item.ChatId, localId);
        Undone?.Invoke(this, new(item, holding.Owner));
        return true;
    }

    /// <summary>
    /// Takes back every send still waiting to go, for quitting: each wait ends as Undo's does and nothing reaches the
    /// provider. Raises nothing; the caller puts the words back (HeldSends). Oldest first, so words that go back to the
    /// same conversation keep the order they were written in.
    /// </summary>
    public IReadOnlyList<UndoneSend> TakeHeld()
    {
        var taken = new List<UndoneSend>();
        foreach (var (localId, holding) in holds.ToArray())
        {
            if (!IsHeld(localId) || !messages.Remove(localId, out var item)) continue;
            holding.Undo.Cancel();
            Notify(item.ChatId, localId);
            taken.Add(new(item, holding.Owner));
        }
        return [.. taken.OrderBy(undone => undone.Message.CreatedAt)];
    }

    /// <summary>
    /// Starts sending a text and returns its local id at once, for a caller that follows the send through
    /// <see cref="ItemChanged"/> and <see cref="Find"/> rather than awaiting it. The same operation as the composer's:
    /// one submission, never retried, confirmed by lookups only, and restorable if it is refused. Throws before anything
    /// is submitted when too many sends are still unsettled.
    /// </summary>
    public string Submit(string chatId, string text)
    {
        var version = generation;
        var item = Add(chatId, text, canRestoreText: true, replyToId: null, attachments: null);
        // The operation settles every outcome itself, including failures, so nothing is left to observe here.
        _ = RunAsync(item, version, ct => sender.SendTextAsync(chatId, text, ct), waitForConfirmation: true);
        return item.LocalId;
    }

    /// <summary>
    /// <see cref="Submit(string, string)"/> for a quoted reply: the same single submission through
    /// <paramref name="submit"/> (the provider's reply operation, as the composer's reply uses), followed the same way.
    /// </summary>
    public string SubmitReply(string chatId, string text, string replyToId, Func<CancellationToken, Task<SendReceipt>> submit)
    {
        var version = generation;
        var item = Add(chatId, text, canRestoreText: true, replyToId: replyToId, attachments: null);
        _ = RunAsync(item, version, submit, waitForConfirmation: true);
        return item.LocalId;
    }

    public async Task SendOperationAsync(string chatId, string text, Func<CancellationToken, Task<SendReceipt>> submit, bool canRestoreText = false,
        string? replyToId = null, bool waitForConfirmation = true, IReadOnlyList<MessageAttachment>? attachments = null, object? holdFor = null,
        // Send later hands over the id its scheduled row already has, so the row the conversation shows stays the same row
        // as it starts sending, and a send it began before a crash is known by the same id (ScheduledSends).
        string? localId = null,
        // Undo send: asked as the wait ends and before the provider is, for the composer to take the words out of the saved draft they waited in. False, or no
        // answer in time, and the send doesn't go, refused as any other is, with its banner and Restore: a sent message left in its draft could be sent again by someone who took it for unsent.
        Func<Task<bool>>? holdEnding = null)
    {
        var version = generation;
        var hold = holdFor is not null && Hold > TimeSpan.Zero ? Hold : TimeSpan.Zero;
        var item = Add(chatId, text, canRestoreText, replyToId, attachments, hold > TimeSpan.Zero ? OutgoingState.Held : OutgoingState.Sending, localId);
        if (hold > TimeSpan.Zero)
        {
            var holding = new Holding(new(), holdFor!);
            holds[item.LocalId] = holding;
            try
            {
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(holding.Undo.Token, lifetime.Token))
                    // WhenAny, so a wait that was ended (Undo, quitting, signing out) returns here rather than throwing.
                    await Task.WhenAny(Task.Delay(hold, waiting.Token));
                // Undone (by Undo or by quitting), or the outbox is ending: nothing reaches the provider.
                if (holding.Undo.IsCancellationRequested || version != generation || lifetime.IsCancellationRequested || !messages.ContainsKey(item.LocalId)) return;
                if (holdEnding is not null)
                {
                    bool ended;
                    try { ended = await holdEnding().WaitAsync(HoldEndingLimit); }
                    catch (Exception) { ended = false; }
                    // Undo or quitting may have come while that was written.
                    if (holding.Undo.IsCancellationRequested || version != generation || lifetime.IsCancellationRequested || !messages.ContainsKey(item.LocalId)) return;
                    if (!ended)
                    {
                        messages[item.LocalId] = item with { State = OutgoingState.Rejected, Explanation = "The message wasn't sent. Message Link couldn't update its saved draft." };
                        Notify(chatId, item.LocalId);
                        return;
                    }
                }
                item = item with { State = OutgoingState.Sending };
                messages[item.LocalId] = item; Restart(item.LocalId); Notify(chatId, item.LocalId);
                await RunAsync(item, version, submit, waitForConfirmation);
            }
            finally { holds.Remove(item.LocalId); holding.Undo.Dispose(); }
            return;
        }
        await RunAsync(item, version, submit, waitForConfirmation);
    }

    /// <summary>
    /// Sends chosen photos one at a time, for a provider with no album operation, with every photo drawn the moment Send
    /// is pressed: each is a pending send of its own from the start, in order, and each is then submitted once, never
    /// retried, and confirmed in place. Drawn one by one as each went, the strip emptied a photo at a time and the photos
    /// arrived as a procession (2026-10-01 recordings). submit returns null to stop before a photo without submitting it.
    /// After a failure the failed photo stays as its send — refused, or uncertain — and the photos after it, never
    /// attempted, are withdrawn; the caller keeps them in its draft.
    /// </summary>
    public async Task<PhotoSetOutcome> SendEachAsync(string chatId, string text, IReadOnlyList<MessageAttachment> attachments,
        Func<int, CancellationToken, Task<SendReceipt?>> submit)
    {
        if (attachments.Count == 0) throw new ArgumentException("Nothing to send.", nameof(attachments));
        if (messages.Count + attachments.Count > 100) throw new InvalidOperationException("Review pending sends before sending more messages.");
        var version = generation;
        // Added together and announced once, so the conversation draws every photo in one pass.
        var items = attachments.Select(attachment => Place(chatId, text, canRestoreText: false, replyToId: null, [attachment])).ToArray();
        Changed?.Invoke(this, chatId);
        foreach (var item in items) ItemChanged?.Invoke(this, item.LocalId);
        for (var index = 0; index < items.Length; index++)
        {
            var localId = items[index].LocalId;
            SendReceipt? receipt;
            try
            {
                receipt = await Named(items[index], () => submit(index, lifetime.Token));
                // Cleared meanwhile (an account change): a photo handed over may have gone, so it leaves the draft.
                if (version != generation || !messages.ContainsKey(localId)) return new(receipt is null ? index : index + 1, null);
                if (receipt is null) { Withdraw(index); return new(index, null); }
                if (receipt.ChatId != chatId || string.IsNullOrWhiteSpace(receipt.PendingMessageId) || receipt.PendingMessageIds is { Count: > 1 })
                    throw new SendException(true, "Message Link couldn't confirm the photo was sent. Check the conversation before sending it again.");
            }
            catch (Exception ex)
            {
                if (version != generation || !messages.ContainsKey(localId)) return new(index + 1, null);
                var unknown = ex is SendException { OutcomeUnknown: true } || ex is OperationCanceledException;
                Report(messages[localId], unknown ? "unknown" : "failed");
                var explanation = ex is SendException send ? send.Message : "The photo wasn't sent. Check your connection and try again.";
                messages[localId] = messages[localId] with { State = unknown ? OutgoingState.Unknown : OutgoingState.Rejected, Explanation = explanation,
                    OperationId = unknown ? (ex as SendException)?.OperationId : null };
                Notify(chatId, localId);
                SettleEarly(localId);
                Withdraw(index + 1);
                return new(index + (unknown ? 1 : 0), unknown ? OutgoingState.Unknown : OutgoingState.Rejected, explanation);
            }
            messages[localId] = messages[localId] with { State = OutgoingState.Pending, PendingId = receipt.PendingMessageId, OperationId = null };
            Notify(chatId, localId);
            _ = ConfirmAsync(localId, version, lifetime.Token);
        }
        return new(items.Length, null);

        void Withdraw(int from)
        {
            for (var index = from; index < items.Length; index++)
                if (messages.Remove(items[index].LocalId)) Notify(chatId, items[index].LocalId);
        }
    }

    /// <summary>
    /// A text drawn now and submitted only when ready says so: the words written with photos that go one at a time. It
    /// leaves the composer and shows under the photos at once, and still reaches the other person after them. When ready
    /// says no (a photo before it was not sent) it is not sent and is withdrawn here, for the caller to give back.
    /// </summary>
    public string SendWhenReady(string chatId, string text, Task<bool> ready) => SendWhenReady(chatId, text, ready, out _);
    private string SendWhenReady(string chatId, string text, Task<bool> ready, out Task<bool> accepted)
    {
        var version = generation;
        var item = Add(chatId, text, canRestoreText: true, replyToId: null, attachments: null);
        // Watched before it can start: with ready already true and a service that answers at once, it could be accepted,
        // confirmed and gone before a watch begun afterwards, which would then call it not accepted.
        accepted = AcceptedAsync(item.LocalId);
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queued[item.LocalId] = taken;
        _ = RunAsync(item, version, async ct =>
        {
            try
            {
                await Task.WhenAny(ready, taken.Task).WaitAsync(ct);
                // Taken back for quitting (TakeQueued), or nothing before it was accepted: it is not sent.
                if (taken.Task.IsCompleted || !queued.Remove(item.LocalId) || !await ready) throw new WithheldException();
            }
            finally { queued.Remove(item.LocalId); }
            Restart(item.LocalId);
            return await sender.SendTextAsync(chatId, text, ct);
        }, waitForConfirmation: true, withheld: () => { if (messages.Remove(item.LocalId)) Notify(chatId, item.LocalId); });
        return item.LocalId;
    }
    private sealed class WithheldException : Exception;
    // Words still waiting for what goes before them (SendWhenReady), by local id, until they are let go to the provider.
    private readonly Dictionary<string, TaskCompletionSource> queued = [];

    /// <summary>
    /// Takes back, for quitting, every text still waiting for the photos before it (<see cref="SendWhenReady"/>,
    /// <see cref="SendAfter"/>): none of it has reached the provider, and the end of a Windows session during an upload
    /// left those words in neither the conversation nor the draft (2026-10-04 cross-lane review). Raises nothing; the
    /// caller puts the words back (HeldSends). Oldest first. Synchronous, so nothing can let one go meanwhile.
    /// </summary>
    public IReadOnlyList<UndoneSend> TakeQueued()
    {
        var back = new List<UndoneSend>();
        foreach (var (localId, taken) in queued.ToArray())
        {
            queued.Remove(localId);
            taken.TrySetResult();
            if (!messages.Remove(localId, out var item)) continue;
            Notify(item.ChatId, localId);
            back.Add(new(item, taken));
        }
        return [.. back.OrderBy(undone => undone.Message.CreatedAt)];
    }

    /// <summary>
    /// Words written while photos upload (Enter during the upload, 2026-10-04): drawn now, and submitted through
    /// <see cref="SendWhenReady"/> only once <paramref name="after"/> says everything before them was accepted. Returns
    /// the words' own acceptance too, which is what words queued behind them wait for, so several keep their order and
    /// none can overtake a photo or other words that failed.
    /// </summary>
    public (string LocalId, Task<bool> Accepted) SendAfter(string chatId, string text, Task<bool> after)
    {
        var localId = SendWhenReady(chatId, text, after, out var accepted);
        return (localId, accepted);
    }

    /// <summary>
    /// True once the send has its provider's receipt; false when it ends without one: refused, uncertain, withheld,
    /// taken back or cleared. Asked about one no longer here it says false, which is the safe answer for anything waiting
    /// on it; <see cref="SendAfter"/> asks before its send can start, so it never misses the outcome.
    /// </summary>
    public Task<bool> AcceptedAsync(string localId)
    {
        if (Verdict(localId) is { } known) return Task.FromResult(known);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(object? sender, string id)
        {
            if (id != localId || Verdict(localId) is not { } settled) return;
            ItemChanged -= Watch;
            answer.TrySetResult(settled);
        }
        ItemChanged += Watch;
        return answer.Task;

        bool? Verdict(string id) => messages.GetValueOrDefault(id) switch
        {
            null => false,
            { PendingId: not null } or { ConfirmedId: not null } => true,
            { State: OutgoingState.Rejected or OutgoingState.Unknown } => false,
            _ => null,
        };
    }


    private OutgoingMessage Add(string chatId, string text, bool canRestoreText, string? replyToId, IReadOnlyList<MessageAttachment>? attachments,
        OutgoingState state = OutgoingState.Sending, string? localId = null)
    {
        if (messages.Count >= 100) throw new InvalidOperationException("Review pending sends before sending more messages.");
        // One send per id, ever: a second hand-over of the same one is refused before anything is submitted.
        if (localId is not null && (!localId.StartsWith("local:", StringComparison.Ordinal) || messages.ContainsKey(localId)))
            throw new InvalidOperationException("This message is already being sent.");
        var item = Place(chatId, text, canRestoreText, replyToId, attachments, state, localId);
        Notify(chatId, item.LocalId);
        return item;
    }
    // Sends are drawn in the order they were made. Several made in one turn could share a clock reading and then sort by
    // their random ids instead, so each is given a time after the one before.
    private DateTimeOffset lastCreated;
    private OutgoingMessage Place(string chatId, string text, bool canRestoreText, string? replyToId, IReadOnlyList<MessageAttachment>? attachments,
        OutgoingState state = OutgoingState.Sending, string? localId = null)
    {
        // Listening starts with the first send, there being nothing to hear about before it (a primary constructor has no body to do it in).
        if (outcomes is not null && !listening) { listening = true; outcomes.Reconciled += OperationReconciled; }
        var now = DateTimeOffset.UtcNow;
        lastCreated = now > lastCreated ? now : lastCreated.AddTicks(1);
        // Named now, before anything is sent: the record kept for the next launch (InterruptedSends) then holds the name of a send that was in flight when the
        // app was killed, and the host's journal can say what became of it. A port that runs in this process never looks at it.
        var item = new OutgoingMessage(localId ?? "local:" + Guid.NewGuid().ToString("N"), chatId, text, lastCreated, state,
            CanRestoreText: canRestoreText, ReplyToId: replyToId, Attachments: attachments, OperationId: PlannedOperation.NewId());
        messages.Add(item.LocalId, item);
        Restart(item.LocalId);
        return item;
    }

    // Makes the one submission under the send's own name (PlannedOperation), so the host's journal knows the send by the name already kept for the next
    // launch. The plan is open only while the call is made: a proxy takes the name when it builds its request, and a second mutation made by the same call
    // gets one of its own.
    private static Task<T> Named<T>(OutgoingMessage item, Func<Task<T>> call)
    {
        using (PlannedOperation.Begin(item.OperationId)) return call();
    }

    // The one body behind SendOperationAsync and Submit: a single submission, then lookups. Never a second submit.
    private async Task RunAsync(OutgoingMessage item, int version, Func<CancellationToken, Task<SendReceipt>> submit, bool waitForConfirmation,
        Action? withheld = null)
    {
        var chatId = item.ChatId;
        try
        {
            var receipt = await Named(item, () => submit(lifetime.Token));
            if (version != generation) return;
            if (receipt.ChatId != chatId || string.IsNullOrWhiteSpace(receipt.PendingMessageId) ||
                receipt.PendingMessageIds is { Count: > 0 } ids &&
                (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || !ids.Contains(receipt.PendingMessageId)))
                throw new SendException(true, "Message Link couldn't confirm the message was sent. Check the conversation before sending it again.");
            // Accepted: the receipt names it from here, so the name given before sending is dropped.
            messages[item.LocalId] = item with { State = OutgoingState.Pending, PendingId = receipt.PendingMessageId, OperationId = null,
                PendingMessageIds = receipt.PendingMessageIds?.ToArray() };
            Notify(chatId, item.LocalId);
            if (!waitForConfirmation)
            {
                _ = ConfirmAsync(item.LocalId, version, lifetime.Token);
                return;
            }
            // The HTTP receipt may follow the realtime event; lookup is the authoritative correlation.
            for (var attempt = 0; attempt < 4 && messages.ContainsKey(item.LocalId); attempt++)
            {
                if (attempt > 0) await Task.Delay(step * attempt, lifetime.Token);
                if (version != generation) return;
                await ResolveOneAsync(item.LocalId, version, lifetime.Token);
            }
            _ = FollowUpAsync(item.LocalId, version, lifetime.Token);
        }
        catch (WithheldException) { if (version == generation) withheld?.Invoke(); }
        catch (Exception ex)
        {
            if (version != generation || !messages.ContainsKey(item.LocalId)) return;
            // A lookup failure after an accepted receipt is still pending, not a failed send.
            if (messages[item.LocalId].PendingId is not null) { _ = FollowUpAsync(item.LocalId, version, lifetime.Token); return; }
            var unknown = ex is SendException { OutcomeUnknown: true } || ex is OperationCanceledException;
            Report(item, unknown ? "unknown" : "failed");
            messages[item.LocalId] = item with { State = unknown ? OutgoingState.Unknown : OutgoingState.Rejected,
                Explanation = ex is SendException send ? send.Message : "The message wasn't sent. Check your connection and try again.",
                OperationId = unknown ? (ex as SendException)?.OperationId : null };
            Notify(chatId, item.LocalId);
            SettleEarly(item.LocalId);
        }
    }

    // ---- What the host says became of a send whose answer was lost ----
    //
    // Never a second send: an answer only changes what is shown for the send that was already made. Done gives the send the receipt a
    // normal answer would have (it is then pending and confirmed by the same lookups); failed makes it a refused send, which the composer
    // can give back; unknown changes nothing.
    private bool listening;
    // Answers that came before the send they are for was marked unknown (the reader runs ahead of the caller's continuation).
    private readonly Dictionary<string, OperationOutcome> early = [];

    private void OperationReconciled(object? source, OperationOutcome outcome)
    {
        // Always on the dispatcher, where the sends are settled (SettleEarly) and every other change to messages is made: an answer
        // that lands in early is then either seen by SettleEarly, which runs after it, or it finds the send already unknown.
        _ = applyOn!.InvokeAsync(() => { ApplyOutcome(outcome); return Task.CompletedTask; });
    }

    private void SettleEarly(string localId)
    {
        if (messages.TryGetValue(localId, out var item) && item.OperationId is { } id && early.Remove(id, out var outcome)) ApplyOutcome(outcome);
    }

    private void ApplyOutcome(OperationOutcome outcome)
    {
        if (outcome.Verdict == OperationVerdict.Unknown) return;
        var item = messages.Values.FirstOrDefault(m => m.OperationId == outcome.OperationId);
        if (item is null)
        {
            // Not yet marked unknown, or not this outbox's: kept briefly for the first.
            if (early.Count >= 64) early.Clear();
            early[outcome.OperationId] = outcome;
            return;
        }
        if (item.State != OutgoingState.Unknown) return;
        if (outcome.Verdict == OperationVerdict.Failed)
        {
            // Failed is the host saying it was not done, and an item here is only ever a message send, reply or attachment send (its
            // OperationId comes from their SendException). Directory and group operations, some of whose refusals say the thing was done, are
            // never in the outbox; SendExceptionSentenceChecks keeps those sentences out of the send paths.
            messages[item.LocalId] = item with { State = OutgoingState.Rejected, OperationId = null,
                Explanation = outcome.Explanation ?? "The message wasn't sent. Check your connection and try again." };
            Notify(item.ChatId, item.LocalId);
            return;
        }
        // Done without a receipt that names this conversation and a message identifies nothing to look up: it stays unknown.
        if (outcome.Receipt is not { } receipt || receipt.ChatId != item.ChatId || string.IsNullOrWhiteSpace(receipt.PendingMessageId) ||
            receipt.PendingMessageIds is { Count: > 0 } ids && (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || !ids.Contains(receipt.PendingMessageId)))
            return;
        messages[item.LocalId] = item with { State = OutgoingState.Pending, PendingId = receipt.PendingMessageId, OperationId = null, Explanation = null,
            PendingMessageIds = receipt.PendingMessageIds?.ToArray() };
        Notify(item.ChatId, item.LocalId);
        _ = ConfirmAsync(item.LocalId, generation, lifetime.Token);
    }

    private async Task ConfirmAsync(string id, int version, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; attempt < 4 && version == generation && messages.ContainsKey(id); attempt++)
            {
                if (attempt > 0) await Task.Delay(step * attempt, token);
                if (version != generation) return;
                await ResolveOneAsync(id, version, token);
            }
        }
        catch (OperationCanceledException) { return; }
        // The accepted operation stays pending for realtime/manual reconciliation. Never resend it.
        catch { }
        _ = FollowUpAsync(id, version, token);
    }

    // Lookups only. Realtime reconciliation and Check again can still settle the message at any time.
    private async Task FollowUpAsync(string localId, int version, CancellationToken token)
    {
        foreach (var delay in followUpDelays ?? DefaultFollowUps)
        {
            if (version != generation || !messages.ContainsKey(localId)) return;
            try
            {
                await Task.Delay(delay, token);
                if (version != generation) return;
                await ResolveOneAsync(localId, version, token);
            }
            catch (OperationCanceledException) { return; }
            catch { /* One failed lookup does not end the later checks. */ }
        }
        if (version != generation || !messages.TryGetValue(localId, out var item) || item.PendingId is null || item.ConfirmedId is not null || item.Unconfirmed) return;
        messages[localId] = item with { Unconfirmed = true };
        Report(item, "unconfirmed");
        Notify(item.ChatId, localId);
    }

    public async Task ReconcileAsync(string? chatId = null, CancellationToken cancellationToken = default, IReadOnlyList<RealtimeEvent>? changes = null)
    {
        var version = generation;
        foreach (var item in messages.Values.Where(m => m.PendingId is not null && (chatId is null || m.ChatId == chatId) && (changes is null || changes.Any(change => change.Affects(m.ChatId)))).ToArray())
            await ResolveOneAsync(item.LocalId, version, cancellationToken, "reconcile");
    }

    // via, for the timing line: "lookup" for the checks that follow a receipt, "reconcile" for a realtime batch or Check again.
    private async Task ResolveOneAsync(string localId, int version, CancellationToken token, string via = "lookup")
    {
        if (!messages.TryGetValue(localId, out var item) || item.PendingId is null) return;
        if (sender is IProviderConnectionState connections && !connections.IsConnected(item.ChatId)) return;
        try
        {
            if (item.PendingMessageIds is { Count: > 1 } batchIds)
            {
                var batch = new List<ChatMessage>(batchIds.Count);
                foreach (var id in batchIds)
                {
                    var photo = await sender.ResolveMessageAsync(item.ChatId, id, token);
                    // A receipt names the photo by its own id, or by one its copy carries (Google's transaction id).
                    if (photo.ChatId != item.ChatId || photo.Id != id && photo.PendingId != id || photo.Delivery.SendState == "PENDING" ||
                        batch.Any(member => member.Id == photo.Id)) return;
                    if (!photo.IsDeleted && item.Attachments is { Count: > 1 } && photo.Attachments.Count == 0 &&
                        photo.Delivery.SendState is null or "SUCCESS") return;
                    batch.Add(photo);
                }
                if (version != generation || !messages.ContainsKey(localId)) return;
                // All members are authoritative before anything changes on screen. The first
                // accepted photo alone must never replace a six-photo optimistic collage.
                messages[localId] = messages[localId] with { ConfirmedId = batch[^1].Id };
                Report(item, "confirmed", via);
                BatchResolved?.Invoke(this, new(messages[localId], batch));
                messages.Remove(localId);
                Notify(item.ChatId, localId);
                return;
            }
            var resolved = await sender.ResolveMessageAsync(item.ChatId, item.PendingId, token);
            if (version != generation || !messages.ContainsKey(localId)) return;
            if (resolved.ChatId != item.ChatId) return;
            // A native multi-attachment message can also arrive in stages. Keep its local
            // collage until the exact confirmed message has its complete attachment list.
            if (item.Attachments is { Count: > 1 } attachments && resolved.Attachments.Count < attachments.Count &&
                resolved.Delivery.SendState is null or "SUCCESS" or "PENDING") return;
            messages[localId] = messages[localId] with { ConfirmedId = resolved.Id };
            Report(item, "confirmed", via);
            Resolved?.Invoke(this, resolved);
            if (resolved.Delivery.SendState == "PENDING") return;
            // Even a network-rejected message now has an authoritative row and must not retain a duplicate optimistic row.
            messages.Remove(localId);
            Notify(item.ChatId, localId);
        }
        // Any failed lookup is "not yet" for this one message, never sent or failed and never a second send. A helper that has the
        // pairing but not the row answers unknown_message, which arrives as InvalidOperationException (in-process) or ProtocolException
        // (remote); letting it out failed the whole reconcile pass for every other pending message (seen 2026-10-12, a delivered photo).
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public string? TakeRejected(string chatId)
        => TakeRejectedOperation(chatId)?.Text;
    public OutgoingMessage? TakeRejectedOperation(string chatId)
    {
        var item = ForChat(chatId).LastOrDefault(m => m.State == OutgoingState.Rejected && m.CanRestoreText);
        if (item is null) return null;
        messages.Remove(item.LocalId); Notify(chatId, item.LocalId); return item;
    }
    /// <summary>Removes one refused send by its local id, for a caller that keeps its text itself. Anything not refused
    /// stays; returns whether it was removed.</summary>
    public bool DiscardRejected(string localId)
    {
        if (!messages.TryGetValue(localId, out var item) || item.State != OutgoingState.Rejected) return false;
        messages.Remove(localId); Notify(item.ChatId, localId); return true;
    }
    public void Clear()
    {
        generation++; lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        var removed = messages.Keys.ToArray();
        messages.Clear(); started.Clear(); Changed?.Invoke(this, "");
        foreach (var localId in removed) ItemChanged?.Invoke(this, localId);
    }
    public void Dispose()
    {
        if (listening && outcomes is not null) outcomes.Reconciled -= OperationReconciled;
        generation++; lifetime.Cancel(); lifetime.Dispose();
    }
}
