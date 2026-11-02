using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

/// <summary>What a server's host needs to fire its clients' scheduled sends itself (SWITCHBOARD.md stage 6, slice 11).</summary>
/// <param name="Clock">The time the schedule runs by (the harness moves it by hand).</param>
/// <param name="Custody">
/// Whether this host holds the schedule (S9). A server's is always its own: its schedule is the only one that fires. Slice 13 (migration and rollback)
/// replaces it with the custody marker, so that a schedule being moved, or handed back to a PC, fires nothing here; the engine already refuses to fire,
/// hold or write without it.
/// </param>
internal sealed record HostScheduled(TimeProvider? Clock = null, Func<ValueTask<bool>>? Custody = null);

/// <summary>
/// The server's scheduled sends (stage 6 slice 11, owner D6): the host keeps the schedule, fires it with every client closed, and answers its clients' four
/// commands. It is the app's own <see cref="ScheduledSendEngine"/> over the host's ports, so a message goes by the rules it goes by on a PC: within ten
/// minutes of its time, once, and never when the account is not the one it was written for.
/// </summary>
/// <remarks>
/// <para><b>Rows.</b> Each message is a row of the shared state, <c>scheduled:&lt;H(profile, id)&gt;</c> (<see cref="SharedCategories.Scheduled"/>, host-owned), so every
/// client's replica draws it and none can write it. The engine saves its whole list; <see cref="Rows"/> turns each save into the rows that changed, in one
/// commit. A command is made against the rev of the row its client drew.</para>
/// <para><b>At most once (S4).</b> The engine commits the row <c>Sending</c> with <c>attempt: n</c> before it calls the outlet; the outlet then sends through the
/// <see cref="OperationJournal"/> under the fixed id <c>sched:&lt;id&gt;:&lt;n&gt;</c>. A host that dies after the row was committed finds it <c>Sending</c> at its next
/// start and records it as a send that may not have gone (a shared <c>outbox:</c> row every client draws, unknown, and never sent again); the journal, which flushed the operation as running before the provider was asked,
/// calls it <c>outcome_unknown</c> to <c>ops.status</c>. A Send now of a message that was refused is attempt <c>n + 1</c>, another id. Nothing here asks twice.</para>
/// <para><b>One at a time.</b> The engine runs on one thread (<see cref="HostSerialContext"/>), and each message's commands run one after another: of two clients
/// acting on one message the second finds its rev gone and is answered <c>conflict</c>, and an edit, cancel or Send now of a message that is sending is answered
/// <c>already_sending</c>.</para>
/// <para>Logs carry fixed words, counts and one-way tags; never a message, a name or a number.</para>
/// </remarks>
internal sealed class HostScheduledSends : IScheduledCommands, IDisposable
{
    // The rows are the host's, whatever the clients call a message: this prefix keeps a host's ids from ever meeting a PC's (local:scheduled:...).
    public const string IdPrefix = ScheduledIds.Prefix;
    /// <summary>The id a send is made under at the provider's door: the message and the attempt, so no attempt is ever sent under another's id.</summary>
    public static string OperationId(string id, int attempt) => $"sched:{id}:{attempt}";
    /// <summary>A scheduled message as its row holds it, in System.Text.Json's default spelling, which every client reads it in (ScheduledHostLink): the support window holds the bytes.</summary>
    internal static byte[] RowValue(ScheduledSend item) => JsonSerializer.SerializeToUtf8Bytes(item);

    private readonly Rows rows;
    private readonly ScheduledSendEngine engine;
    private readonly IScheduledSendCustody custody;
    private readonly Outlet outlet;
    private readonly HostSerialContext serial = new();
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stopping = new();
    private readonly Action<string> log;
    // One command at a time on a message. Only the serial thread touches it.
    private readonly Dictionary<string, Gate> gates = new(StringComparer.Ordinal);
    private sealed class Gate { public readonly SemaphoreSlim Lock = new(1, 1); public int Users; }

    /// <param name="localKey">The key the host files its shared rows under (its space key once pinned), read when asked.</param>
    public HostScheduledSends(SharedStateStore store, Func<CancellationToken, Task<string?>> localKey, IMessageSender sender, IMessageActions actions,
        IChatReader chats, IHostIdentity? identity, OperationJournal journal, HostScheduled options, Action<string> log)
    {
        this.log = log;
        var clock = options.Clock ?? TimeProvider.System;
        rows = new Rows(store, localKey);
        this.clock = clock;
        outlet = new Outlet(sender, actions, journal, rows, clock, serial, log);
        var conditions = new Conditions(sender, chats, identity);
        custody = new Custody(options.Custody);
        engine = new ScheduledSendEngine(rows, outlet, conditions, custody, clock, serial, log) { NumbersAttempts = true, KeepsRejected = true };
        engine.Held += Held;
    }

    /// <summary>
    /// Loads the schedule and starts it (the engine's own start): a message found sending was left by a host that ended mid-send and becomes an interrupted send,
    /// never sent; one whose window passed while the host was down is held as not sent; the rest wait for their time.
    /// </summary>
    public Task StartAsync() => serial.Run(async () =>
    {
        await engine.StartAsync().ConfigureAwait(true);
        // Unsettled sends the clients have had a week to see: the rows are the host's alone to remove.
        await rows.PruneUnsettledAsync(clock.GetUtcNow() - UnsettledKept).ConfigureAwait(true);
        return true;
    });

    private static readonly TimeSpan UnsettledKept = TimeSpan.FromDays(7);

    /// <summary>
    /// <see cref="StartAsync"/> until it has loaded: a start that fails (the key or the store could not be read yet) is tried again after 5 seconds, then 10, 20, 40 and every
    /// minute, until it holds or the schedule is disposed. Left to one try, a host whose first read failed would run the whole of its life with a schedule that was never
    /// loaded and an alarm that never rang. Each try is the same load, which carries a message found sending again from the row that still says so.
    /// </summary>
    public async Task StartWithRetryAsync()
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!stopping.IsCancellationRequested)
        {
            try { await StartAsync().ConfigureAwait(false); return; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"scheduled start failed error={failure.GetType().Name} retry_s={(int)wait.TotalSeconds}"); }
            try { await Task.Delay(wait, clock, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
        }
    }

    // Held before anyone was listening, as the commonest hold comes at the start: the server was off at the time. Only those from the last hour are told, so that pushes turned on
    // days later don't announce old news.
    private static readonly TimeSpan HeldTellable = TimeSpan.FromHours(1);
    private readonly object heldLock = new();
    private readonly List<(string ChatId, DateTimeOffset At)> heldUntold = [];
    private Action<string>? heldListener;

    /// <summary>Who to tell of a message held as not sent, by its conversation. Given once; what was held just before it is told at once.</summary>
    public void OnHeld(Action<string> listener)
    {
        string[] told;
        lock (heldLock)
        {
            heldListener = listener;
            var now = clock.GetUtcNow();
            told = [.. heldUntold.Where(held => now - held.At < HeldTellable).Select(held => held.ChatId)];
            heldUntold.Clear();
        }
        foreach (var chatId in told) listener(chatId);
    }

    private void Held(ScheduledSend item)
    {
        Action<string>? listener;
        lock (heldLock)
        {
            listener = heldListener;
            if (listener is null) heldUntold.Add((item.ChatId, clock.GetUtcNow()));
        }
        listener?.Invoke(item.ChatId);
    }

    /// <summary>The messages the schedule holds, for the harness and the diagnostics' count (read on the schedule's own thread, which owns the list).</summary>
    public Task<int> CountAsync() => serial.Run(() => Task.FromResult(engine.All.Count));

    /// <summary>The latest pass of the schedule's alarm; the harness awaits it after moving the clock.</summary>
    public Task Idle => engine.Idle;

    // ---- The four commands ----

    public Task<ScheduledAnswer> CreateAsync(ScheduledCreateParams request, CancellationToken cancellationToken = default) => serial.Run(async () =>
    {
        // Made from the client's operation id: a lost answer is looked for in the replica by this id, and the same operation never makes a second message.
        var id = IdPrefix + request.ClientOpId;
        // Under the message's gate, so that nothing that ends a message with this id (a cancel, a take-back, a discard) runs between the looks below and the making of the message.
        // The sending of a message that ends it is not under the gate; the store's own commit looks at the retired ids once more (ScheduledIdRetiredException).
        return await WithGateAsync(id, async () =>
        {
            // Loaded before anything is asked of the retired ids: a load settles the rows an earlier host left sending, and retires them.
            try { await engine.StartAsync().ConfigureAwait(true); }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException) { return Refused("scheduled create", failure); }
            // An id whose message has gone (cancelled, taken back, sent, interrupted) is not made again, whether or not the journal still remembers its create: two sends under one id,
            // with the attempt count starting over, is what that would be. The answer says so, so the client does not offer it again.
            if (await rows.IsRetiredAsync(id).ConfigureAwait(true)) return Gone("scheduled create");
            try { await engine.ScheduleAsync(request.ChatId, request.Text, request.Due, request.ReplyToId, request.ReplyPreview, id).ConfigureAwait(true); }
            catch (ScheduledIdRetiredException) { return Gone("scheduled create"); }
            // The engine's own sentences are written to be shown; a store that could not keep the message left nothing behind.
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException) { return Refused("scheduled create", failure); }
            // Kept. Nothing after this may refuse it, or the person schedules it again and both send; a rev that cannot be read now is 0, and the replica gives the client the real one.
            long rev;
            try { rev = await rows.RevOfAsync(id).ConfigureAwait(true); }
            catch (Exception) { rev = 0; log("scheduled create rev unread"); }
            return new ScheduledAnswer(ScheduledStatuses.Done, id, rev);
        }).ConfigureAwait(true);
    }, cancellationToken);

    private ScheduledAnswer Refused(string what, Exception failure)
    {
        log($"{what} refused reason={(failure is IOException ? "store" : failure is ArgumentException ? "invalid" : "state")}");
        return new ScheduledAnswer(ScheduledStatuses.Refused, Message: failure.Message);
    }

    private ScheduledAnswer NotNow(string reason)
    {
        log($"scheduled command outcome=refused reason={reason}");
        return new ScheduledAnswer(ScheduledStatuses.Refused, Message: "The server can't change this message right now. Try again in a moment.");
    }

    private ScheduledAnswer Gone(string what)
    {
        log($"{what} outcome=retired");
        return new ScheduledAnswer(ScheduledStatuses.Retired, Message: "That message has already gone from the server.");
    }

    public Task<ScheduledAnswer> EditAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        request.IfRev == 0 ? DiscardAsync(request, TakeBack, cancellationToken) : CommandAsync(request, TakeBack, cancellationToken);

    public Task<ScheduledAnswer> CancelAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        request.IfRev == 0 ? DiscardAsync(request, CancelIt, cancellationToken) : CommandAsync(request, CancelIt, cancellationToken);

    private async Task<ScheduledAnswer> TakeBack(ScheduledSend item)
    {
        if (await engine.TakeForEditAsync(item.Id).ConfigureAwait(true) is not { } taken) return Sending();
        return new ScheduledAnswer(ScheduledStatuses.Done, item.Id, 0, Text: new ScheduledText(taken.ChatId, taken.Text, taken.Due, taken.ReplyToId, taken.ReplyPreview));
    }

    private async Task<ScheduledAnswer> CancelIt(ScheduledSend item) =>
        await engine.CancelAsync(item.Id).ConfigureAwait(true) ? new ScheduledAnswer(ScheduledStatuses.Done, item.Id) : Sending();

    /// <summary>
    /// A cancel or an edit that names no rev (<c>IfRev</c> 0), because the client never saw the row: it holds a stand-in for a create whose outcome it could not tell. The message is
    /// taken away if the host made it; if it never did, its id is retired, so that the create, whether lost on the way or asked again by anything, can never make it. Nothing here
    /// makes a message, so nothing can be sent by it. An id already gone is answered <c>retired</c>: what became of that message is not this command's to say.
    /// </summary>
    private Task<ScheduledAnswer> DiscardAsync(ScheduledCommandParams request, Func<ScheduledSend, Task<ScheduledAnswer>> act, CancellationToken cancellationToken) => serial.Run(() =>
        WithGateAsync(request.Id, async () =>
        {
            if (!request.Id.StartsWith(IdPrefix, StringComparison.Ordinal))
            {
                log("scheduled command outcome=conflict");
                return new ScheduledAnswer(ScheduledStatuses.Conflict);
            }
            try
            {
                // Closing an id says "no such message here". That is only true of a host that holds the schedule and can tell whose it is: with no key (signed out) the engine loads nothing
                // and a row that exists under the profile it had would be reloaded, and sent, when that profile returns; and a schedule that is not this host's to change is left alone.
                if (!await custody.IsLocalAsync().ConfigureAwait(true)) return NotNow("custody");
                await engine.StartAsync().ConfigureAwait(true);
                if (await rows.ProfileAsync().ConfigureAwait(true) is null) return NotNow("profile");
                if (engine.Find(request.Id) is { } item)
                {
                    if (item.State == ScheduledSendState.Sending)
                    {
                        log("scheduled command outcome=already_sending");
                        return Sending();
                    }
                    return await act(item).ConfigureAwait(true);
                }
                if (await rows.IsRetiredAsync(request.Id).ConfigureAwait(true)) return Gone("scheduled command");
                await rows.RetireAsync(request.Id).ConfigureAwait(true);
                log("scheduled command outcome=discarded_unmade");
                return new ScheduledAnswer(ScheduledStatuses.Done, request.Id);
            }
            catch (Exception failure) when (failure is InvalidOperationException or IOException)
            {
                log("scheduled command outcome=refused reason=store");
                return new ScheduledAnswer(ScheduledStatuses.Refused, Message: failure.Message);
            }
        }), cancellationToken);

    public Task<ScheduledAnswer> SendNowAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        CommandAsync(request, async item =>
            await engine.SendNowAsync(item.Id).ConfigureAwait(true) ? new ScheduledAnswer(ScheduledStatuses.Done, item.Id, await rows.RevOfAsync(item.Id).ConfigureAwait(true)) : Sending(),
            cancellationToken);

    /// <summary>
    /// Removes the shared record of a send that may not have gone (<c>outbox:</c>), for every client. It sends and asks nothing: the send is never made again whatever the
    /// person does with its record. One already gone is done (the journal answers a repeat, and two clients dismissing at once both succeed).
    /// </summary>
    public Task<Done> DismissAsync(OutboxDismissParams request, CancellationToken cancellationToken = default) => serial.Run(async () =>
    {
        if (request.OperationId is not { Length: > 0 and <= 300 }) throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        await rows.DismissUnsettledAsync(request.OperationId).ConfigureAwait(true);
        log("scheduled unsettled dismissed");
        return new Done();
    }, cancellationToken);

    private static ScheduledAnswer Sending() => new(ScheduledStatuses.AlreadySending);

    // One command at a time on a message, then its rev held against the one the client drew. Whichever command comes second finds the rev moved (or the row
    // gone) and nothing is done: that is a conflict, not a failure. A message that is sending cannot be changed whatever rev is named: the engine answers false
    // for it, which is `already_sending`, and the alarm that makes a waiting message sending runs on this same thread, so no step between the check and the
    // engine's own can be passed by it unseen (the engine re-reads the message itself).
    private Task<ScheduledAnswer> CommandAsync(ScheduledCommandParams request, Func<ScheduledSend, Task<ScheduledAnswer>> act, CancellationToken cancellationToken) => serial.Run(() =>
        WithGateAsync(request.Id, async () =>
        {
            var rev = await rows.RevOfAsync(request.Id).ConfigureAwait(true);
            if (rev == 0 || rev != request.IfRev || engine.Find(request.Id) is not { } item)
            {
                log("scheduled command outcome=conflict");
                return new ScheduledAnswer(ScheduledStatuses.Conflict);
            }
            if (item.State == ScheduledSendState.Sending)
            {
                log("scheduled command outcome=already_sending");
                return Sending();
            }
            try { return await act(item).ConfigureAwait(true); }
            // A removal the store could not keep: the engine put the message back as it was, so nothing was done.
            catch (IOException failure)
            {
                log("scheduled command outcome=refused reason=store");
                return new ScheduledAnswer(ScheduledStatuses.Refused, Message: failure.Message);
            }
        }), cancellationToken);

    // One thing at a time on a message id, create included. Only the serial thread calls it.
    private async Task<T> WithGateAsync<T>(string id, Func<Task<T>> run)
    {
        if (!gates.TryGetValue(id, out var gate)) gates[id] = gate = new Gate();
        gate.Users++;
        await gate.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try { return await run().ConfigureAwait(true); }
        finally
        {
            gate.Lock.Release();
            if (--gate.Users == 0) gates.Remove(id);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        engine.Dispose();
        outlet.Dispose();
        serial.Dispose();
    }

    // ---- The schedule's rows ----

    /// <summary>
    /// The engine's store over the shared state: it reads and saves the whole list, and a save is turned into the rows that differ (a changed or new message is a
    /// write, one that is gone a delete), in one commit, so the clients' replicas hear one change however many messages it touched. Only the rows that
    /// belong to the host's profile are its: a row of another profile in the space is neither read nor deleted.
    /// </summary>
    private sealed class Rows(SharedStateStore store, Func<CancellationToken, Task<string?>> localKey) : IScheduledSendStore
    {
        private const string Prefix = SharedCategories.Scheduled + ":";
        // What the space held for the profile at the last read or save, by key.
        private Dictionary<string, byte[]> known = new(StringComparer.Ordinal);

        public async Task<string?> ProfileAsync() => NotificationIdentity.ProfileOf(await localKey(CancellationToken.None).ConfigureAwait(false));

        public async Task<ScheduledSend[]?> ReadAsync(string profile)
        {
            var items = new List<ScheduledSend>();
            var held = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (key, value, _) in await store.LiveRowsWithRevAsync(Prefix).ConfigureAwait(false))
            {
                if (Read(value) is not { } item || key != KeyOf(profile, item.Id)) continue;
                items.Add(item);
                held[key] = value;
            }
            known = held;
            return [.. items];
        }

        public async Task WriteAsync(string profile, ScheduledSend[] items)
        {
            var wanted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in items) wanted[KeyOf(profile, item.Id)] = RowValue(item);
            var writes = new List<(string Key, byte[]? Value)>();
            foreach (var (key, value) in wanted)
                if (!known.TryGetValue(key, out var was) || !was.AsSpan().SequenceEqual(value)) writes.Add((key, value));
            // A message that leaves the list is retired in the same commit that removes its row: its id is never made again, whatever the journal remembers.
            var retired = new List<string>();
            foreach (var key in known.Keys)
                if (!wanted.ContainsKey(key))
                {
                    writes.Add((key, null));
                    if (Read(known[key]) is { } gone) retired.Add(gone.Id);
                }

            // One save is one commit, however many messages it touches: a save that stopped halfway would leave rows the engine's list does not match (a message held in
            // memory and still waiting on disk, a deleted one still sending), and the clients would draw the half. The first row kept in a space pins its key.
            if (writes.Count == 0) return;
            // A message the schedule has that the space does not is being made: the same commit looks at the retired ids, so a message that went (cancelled, sent, interrupted) can never be
            // made again by a create that checked a moment too early.
            var made = items.Where(item => !known.ContainsKey(KeyOf(profile, item.Id))).Select(item => item.Id).ToList();
            try { await store.WriteOwnBatchAsync(writes, pin: true, retire: retired, makes: made).ConfigureAwait(false); }
            catch (RetiredIdException failure) { throw new ScheduledIdRetiredException(failure.Message, failure.Ids); }
            foreach (var (key, value) in writes)
                if (value is null) known.Remove(key);
                else known[key] = value;
        }

        public Task<bool> IsRetiredAsync(string id) => store.IsRetiredAsync(id);
        public Task RetireAsync(string id) => store.RetireAsync([id]);

        private const string UnsettledPrefix = SharedCategories.Outbox + ":";

        /// <summary>Keeps the record of a send that may not have gone, as a row every client draws. Throws when it could not be kept: nothing may go on as if it had been.</summary>
        public async Task WriteUnsettledAsync(SharedUnsettledSend send)
        {
            var profile = await ProfileAsync().ConfigureAwait(false) ?? throw new IOException("There is no key to file the record under.");
            await store.WriteOwnBatchAsync([(SharedCategories.KeyOf(profile, SharedCategories.Outbox, send.OperationId), HostUnsettledSends.RowValue(send))], pin: true).ConfigureAwait(false);
        }

        /// <summary>Removes the record; one that is not there is done.</summary>
        public async Task DismissUnsettledAsync(string operationId)
        {
            if (await ProfileAsync().ConfigureAwait(false) is not { } profile) return;
            await store.WriteOwnBatchAsync([(SharedCategories.KeyOf(profile, SharedCategories.Outbox, operationId), null)], retire: [HostUnsettledSends.RetirementId(operationId)]).ConfigureAwait(false);
        }

        /// <summary>Removes the records older than <paramref name="before"/>: a week is long enough to have seen one.</summary>
        public async Task PruneUnsettledAsync(DateTimeOffset before)
        {
            if (await ProfileAsync().ConfigureAwait(false) is not { } profile) return;
            var old = new List<(string Key, byte[]? Value)>();
            foreach (var (key, value, _) in await store.LiveRowsWithRevAsync(UnsettledPrefix).ConfigureAwait(false))
            {
                SharedUnsettledSend? send = null;
                try { send = JsonSerializer.Deserialize<SharedUnsettledSend>(value); }
                catch (JsonException) { }
                if (send is { OperationId.Length: > 0 } && (send.RecordedAt ?? send.CreatedAt) < before && key == SharedCategories.KeyOf(profile, SharedCategories.Outbox, send.OperationId)) old.Add((key, null));
            }
            for (var at = 0; at < old.Count; at += SharedLimits.PutWrites)
                await store.WriteOwnBatchAsync(old.Skip(at).Take(SharedLimits.PutWrites).ToArray()).ConfigureAwait(false);
        }

        /// <summary>The rev of the message's row, or 0 when it has none.</summary>
        public async Task<long> RevOfAsync(string id)
        {
            var profile = await ProfileAsync().ConfigureAwait(false);
            return profile is null ? 0 : await store.LiveRevAsync(KeyOf(profile, id)).ConfigureAwait(false);
        }

        private static string KeyOf(string profile, string id) => SharedCategories.KeyOf(profile, SharedCategories.Scheduled, id);

        // The whole-list row the in-process app keeps (scheduled:<H(profile, "all")>) is an array, not a message: not one of these.
        private static ScheduledSend? Read(byte[] value)
        {
            try { return JsonSerializer.Deserialize<ScheduledSend>(value) is { Id.Length: > 0, ChatId.Length: > 0 } item ? item : null; }
            catch (JsonException) { return null; }
        }
    }

    // ---- Custody ----

    private sealed class Custody(Func<ValueTask<bool>>? held) : IScheduledSendCustody
    {
        public ValueTask<bool> IsLocalAsync() => held is null ? ValueTask.FromResult(true) : held();
    }

    // ---- What a due message is asked before it goes ----

    private sealed class Conditions(IMessageSender sender, IChatReader chats, IHostIdentity? identity) : IScheduledSendConditions
    {
        // The account a message was written from is checked where a conversation names none (Messenger's): the scope the saved messages use.
        public bool TracksAccounts => identity is not null;

        public bool ExpectsAccount(string chatId) => chatId.StartsWith("messenger:", StringComparison.Ordinal);

        // As the app's: from the conversation as the host lists it, never a request to the service for the sake of this. Not listed yet (null) is waited for.
        public async Task<string?> AccountOfAsync(string chatId)
        {
            if (identity is null || !chatId.StartsWith("messenger:", StringComparison.Ordinal)) return null;
            ChatSummary source;
            try { source = await chats.GetChatAsync(chatId).ConfigureAwait(false); }
            catch (Exception) { return null; }
            var scopes = (await identity.ReadAsync().ConfigureAwait(false)).SavedScopes;
            if (scopes.FirstOrDefault(scope => scope.AccountId == source.AccountId)?.Scope is not { } found) return null;
            // A row can outlive an account switch: Messenger rows name their own signed-in participant (SavedMessageScopeAsync).
            if (source.Participants.FirstOrDefault(person => person.IsSelf == true) is { } self && found != "messenger:" + NotificationIdentity.Hash(self.Id)) return null;
            return found;
        }

        public bool IsConnected(string chatId) => sender is not IProviderConnectionState connections || connections.IsConnected(chatId);

        public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) => sender.GetSendPermissionAsync(chatId, cancellationToken);
    }

    // ---- The send path ----

    /// <summary>
    /// What the engine hands a due message to. Not the app's outbox (that is an interface object): a send here goes straight to the provider through the host's
    /// journal under <see cref="OperationId"/>, and the message is the schedule's until the provider has it and its copy has come back.
    /// </summary>
    /// <remarks>
    /// An accepted send stays <c>Sending</c> in the schedule (drawn on every client as on its way) until the provider shows the message under the receipt's id,
    /// by the same lookups the app's outbox makes, a few seconds at most; then the row is removed with its receipt. A send the provider certainly refused is
    /// kept as not sent with its words (the engine's KeepsRejected). A send whose outcome is unknown is recorded the same way (an <c>outbox:</c> row, dismissed by <c>outbox.dismiss</c>),
    /// and never made again.
    /// </remarks>
    private sealed class Outlet : IScheduledSendOutlet, IDisposable
    {
        private const int ConfirmAttempts = 4;
        private static readonly TimeSpan ConfirmStep = TimeSpan.FromSeconds(1.5);
        private readonly IMessageSender sender;
        private readonly IMessageActions actions;
        private readonly OperationJournal journal;
        private readonly Rows rows;
        private readonly TimeProvider clock;
        private readonly HostSerialContext serial;
        private readonly Action<string> log;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Dictionary<string, OutgoingState> states = new(StringComparer.Ordinal);
        public event EventHandler<string>? Changed;

        public Outlet(IMessageSender sender, IMessageActions actions, OperationJournal journal, Rows rows, TimeProvider clock, HostSerialContext serial, Action<string> log)
        {
            this.sender = sender; this.actions = actions; this.journal = journal; this.rows = rows; this.clock = clock; this.serial = serial; this.log = log;
        }

        public OutgoingState? StateOf(string id) => states.TryGetValue(id, out var found) ? found : null;

        public async Task SendAsync(ScheduledSend item)
        {
            // Refused before anything is submitted, so the engine holds the message for the user: an attempt that was not numbered cannot be given its own id.
            if (item.Attempt < 1) throw new InvalidOperationException("A scheduled send is made under a numbered attempt.");
            var id = OperationId(item.Id, item.Attempt);
            states[item.Id] = OutgoingState.Sending;
            log($"scheduled fire via=host id={ScheduledSendEngine.Tag(item.Id)} attempt={item.Attempt}");
            SendReceipt receipt;
            try { receipt = await SubmitAsync(item, id).ConfigureAwait(true); }
            catch (Exception failure)
            {
                // The same reading of the host's answer as a client's outbox makes: only what says plainly that nothing was done is a refusal; anything else,
                // a cancellation included, is unknown.
                var refused = failure is ProtocolException { Error: { } error } && ProtocolErrors.IsKnown(error.Code)
                    && error.Code is not (ProtocolErrorCodes.Internal or ProtocolErrorCodes.OutcomeUnknown or ProtocolErrorCodes.Cancelled) && !error.OutcomeUnknown && !error.Done;
                // The provider did it and only what followed failed (SendException.Done): it went, so the row goes as an accepted send does, and it is neither kept to be sent
                // again nor recorded as one that may not have gone.
                if (failure is ProtocolException { Error.Done: true })
                {
                    log($"scheduled fire outcome=done id={ScheduledSendEngine.Tag(item.Id)}");
                    Settle(item.Id, OutgoingState.Pending);
                    return;
                }
                log($"scheduled fire outcome={(refused ? "refused" : "unknown")} id={ScheduledSendEngine.Tag(item.Id)}");
                // Recorded as a row every client draws before the schedule lets go of the message. Should the record not be kept, the message stays sending: a restart
                // finds it so and carries it again, and nothing is sent again meanwhile.
                if (!refused && !await CarryAsync(item, id).ConfigureAwait(true))
                {
                    log($"scheduled fire kept_sending id={ScheduledSendEngine.Tag(item.Id)}");
                    return;
                }
                Settle(item.Id, refused ? OutgoingState.Rejected : OutgoingState.Unknown);
                return;
            }
            // Accepted. The row stays as sending until the provider's copy is found under the receipt's id (or the lookups run out): the shared event that
            // carries it draws it on every client as on its way, and the echo that follows is the message itself.
            for (var attempt = 0; attempt < ConfirmAttempts; attempt++)
            {
                if (attempt > 0)
                    try { await Task.Delay(ConfirmStep * attempt, clock, lifetime.Token).ConfigureAwait(true); }
                    catch (OperationCanceledException) { break; }
                try
                {
                    await sender.ResolveMessageAsync(receipt.ChatId, receipt.PendingMessageId, lifetime.Token).ConfigureAwait(true);
                    break;
                }
                // Not found yet, or not reachable now: accepted all the same, as the app's outbox treats it.
                catch (Exception) { }
            }
            Settle(item.Id, OutgoingState.Pending);
        }

        // The send through the host's journal, as messages.send (or messages.reply) would be served: the same params and fingerprint, one operation id for the
        // attempt, so a repeat of the attempt answers the first outcome and a host that died mid-send answers outcome_unknown.
        private async Task<SendReceipt> SubmitAsync(ScheduledSend item, string id)
        {
            JsonElement request;
            string method;
            Func<CancellationToken, Task<SendReceipt>> call;
            if (item.ReplyToId is { } replyTo)
            {
                method = ProtocolMethods.MessagesReply;
                request = ProtocolJson.ToElement(new ReplyParams(id, item.ChatId, replyTo, item.Text));
                call = token => actions.ReplyAsync(item.ChatId, replyTo, item.Text, token);
            }
            else
            {
                method = ProtocolMethods.MessagesSend;
                request = ProtocolJson.ToElement(new SendTextParams(id, item.ChatId, item.Text));
                call = token => sender.SendTextAsync(item.ChatId, item.Text, token);
            }
            var answer = await journal.ExecuteAsync(id, OperationJournal.Fingerprint(method, request),
                async token => JsonSerializer.SerializeToUtf8Bytes(await call(token).ConfigureAwait(false), ProtocolJson.Options), lifetime.Token).ConfigureAwait(true);
            var receipt = JsonSerializer.Deserialize<SendReceipt>(answer, ProtocolJson.Options);
            if (receipt is null || receipt.ChatId != item.ChatId || string.IsNullOrWhiteSpace(receipt.PendingMessageId))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.OutcomeUnknown, OperationJournal.UnknownSentence, OutcomeUnknown: true));
            return receipt;
        }

        // The engine reads the state when told, and reads it once: it is let go as soon as it has been told.
        private void Settle(string id, OutgoingState outcome)
        {
            states[id] = outcome;
            Changed?.Invoke(this, id);
            states.Remove(id);
        }

        // The engine hands the message itself (the overload below). The one that has only an id and a time cannot name the attempt, so it is named by the message.
        public Task CarryInterruptedAsync(InterruptedSend send) => throw new NotSupportedException("A host carries an interrupted send with its message.");

        // A message found sending at the host's start: the attempt in its row is the one that was begun, so its record has the operation id that attempt was sent under.
        // Not kept, this throws: the row still says sending, the load fails and is tried again, and the message is neither dropped nor sent.
        public async Task CarryInterruptedAsync(ScheduledSend item, InterruptedSend send)
        {
            await rows.WriteUnsettledAsync(Record(item, OperationId(item.Id, Math.Max(1, item.Attempt)))).ConfigureAwait(false);
            log($"scheduled interrupted id={ScheduledSendEngine.Tag(send.LocalId)}");
        }

        // The record every client draws as "may not have sent": the attempt, the conversation, the words (the server's to keep; no client draws them) and when it began.
        private SharedUnsettledSend Record(ScheduledSend item, string operationId) =>
            new(operationId, item.ChatId, item.Text, UnsettledStates.Unknown, item.SendingAt ?? item.Due, item.Id, clock.GetUtcNow());

        private async Task<bool> CarryAsync(ScheduledSend item, string operationId)
        {
            try { await rows.WriteUnsettledAsync(Record(item, operationId)).ConfigureAwait(true); return true; }
            catch (Exception failure)
            {
                log($"scheduled unsettled record failed error={failure.GetType().Name}");
                return false;
            }
        }

        public void Dispose() => lifetime.Cancel();
    }
}
