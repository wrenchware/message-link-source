using MessageLink.Core.Backend;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// A send still unsettled when the app last closed. Identifiers and times only: no message text is stored.
public sealed record InterruptedSend(string LocalId, string ChatId, DateTimeOffset CreatedAt, string? PendingId, string? ConfirmedId,
    IReadOnlyList<string>? PendingMessageIds = null,
    // The server's name for a send whose connection ended before its answer (SendException.OperationId): all that is needed to ask the
    // server's journal what became of it. Not a message, not a person.
    string? OperationId = null,
    // Kept by a server for a send it made on a client's behalf (a scheduled one, or any device's ordinary send): shown as the server's, and dismissed on the server for every device (outbox.dismiss). Never stored on this PC.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool OnServer = false,
    // A server's record of a scheduled message it fired (SharedUnsettledSend.ScheduledId), as against an ordinary send from this or another device: only these are worded "scheduled".
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Scheduled = false,
    // A server's record of a send its service refused (UnsettledStates.Failed): certainly not sent, as against one whose outcome is unknown.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Refused = false);

// Remembers sends whose outcome was still open when the app closed, so the next launch can say so instead of silently
// forgetting them. It never sends anything: each carried send gets one lookup, and one the provider knows is dropped;
// the rest stay until the user dismisses them or they are a week old. Owned by the UI context like the outbox.
public sealed class InterruptedSends : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    // SharedCategories.Refused and its RefusedLegacyRow: filed as outbox: until protocol 35, and renamed in place by the stores that held it (SqlitePrivateStateStore,
    // ReplicatedStateStore).
    private const string Category = "refused", Id = "unsettled";
    private readonly Outbox outbox;
    private readonly IMessageSender sender;
    private readonly PrivateState? state;
    private readonly SemaphoreSlim writing = new(1);
    private readonly HashSet<string> looked = [];
    // Replaced, never changed in place: a save or a draw enumerates it from whatever thread raised it while an answer is applied on the dispatcher (the
    // harness caught a save enumerating it while an outcome changed it).
    private List<InterruptedSend> carried = [];
    private string? profile;
    private bool loaded;
    public event EventHandler<string>? Changed;
    // The carried list has just been read from disk for the signed-in account, with the operation ids it holds (OperationIds): what a
    // client of a server over a connection asks that server about (ProtocolClient.Reconcile). Raised on whichever thread loaded it.
    public event EventHandler<IReadOnlyList<string>>? Loaded;

    private readonly IOperationOutcomes? outcomes;
    private readonly IUiDispatcher? dispatcher;
    private readonly IServerUnsettledLink? host;
    private List<InterruptedSend> hostRows = [];
    private bool hostLoaded;

    // outcomes: what a server's journal says became of a carried send that went over a connection. A send of this
    // session is the outbox's to settle; this is for one carried from an earlier run whose id the connection was asked about.
    // An answer changes the carried list the interface reads, so it is applied on dispatcher, which is required with outcomes.
    // server: the server's own record of sends it made for its clients that may not have gone (refused: rows): drawn beside the carried ones, and dismissed on the
    // server for every device. A client of a server gives it; the in-process app and the loopback do not, and nothing here changes for them.
    public InterruptedSends(Outbox outbox, IMessageSender sender, PrivateState? state = null, IOperationOutcomes? outcomes = null, IUiDispatcher? dispatcher = null,
        IServerUnsettledLink? host = null)
    {
        if (outcomes is not null && dispatcher is null)
            throw new ArgumentException("Outcomes are applied on the interface thread: a dispatcher is required with them.", nameof(dispatcher));
        this.outbox = outbox; this.sender = sender; this.state = state; this.outcomes = outcomes; this.dispatcher = dispatcher; this.host = host;
        if (host is not null) host.UnsettledChanged += (_, _) => _ = RefreshServerQuietAsync();
        outbox.Changed += OutboxChanged;
        if (host is not null) outbox.RefusalSettled += RefusalSettled;
        if (outcomes is not null) outcomes.Reconciled += OperationReconciled;
    }

    // A refused send retried, removed or taken back to edit on this PC is dealt with, so the server's record of it goes for every device.
    // A record not written yet is retired by the same call, so it never appears. One that can't be reached now is owed, and dismissed
    // at the next read of the server's records this session (RefreshServerAsync); after a restart it would show as any other.
    private readonly HashSet<string> owed = new(StringComparer.Ordinal);
    private async void RefusalSettled(object? source, string operationId)
    {
        try { await DismissSettledAsync(operationId); }
        catch (Exception) { owed.Add(operationId); }
    }
    private async Task DismissSettledAsync(string operationId)
    {
        await host!.DismissUnsettledAsync(operationId);
        owed.Remove(operationId);
        if (hostRows.FirstOrDefault(send => send.OperationId == operationId) is { } row)
        {
            hostRows = [.. hostRows.Where(known => !ReferenceEquals(known, row))];
            Changed?.Invoke(this, row.ChatId);
        }
    }
    private bool Shown(InterruptedSend send) => send.OperationId is not { } name || !outbox.MadeHere(name);

    // The names to ask a server about: every carried send that has one.
    public IReadOnlyList<string> OperationIds => [.. carried.Select(send => send.OperationId).OfType<string>()];

    private async void OperationReconciled(object? source, OperationOutcome outcome)
    {
        try
        {
            await dispatcher!.InvokeAsync(() => ApplyOutcomeAsync(outcome));
        }
        catch { /* Recovery is best effort; the sends themselves are unaffected. */ }
    }

    // A carried send is never sent again. Done gives it the id a normal answer would have, so the lookups that settle it can run.
    // Failed and unknown leave it as it is: a send that certainly failed is the one the user most needs to hear about, and its text
    // was never kept, so dropping the warning would read as "it went" (review, 2026-10-08).
    private async Task ApplyOutcomeAsync(OperationOutcome outcome)
    {
        if (!loaded || outcome.Verdict != OperationVerdict.Done) return;
        var at = carried.FindIndex(send => send.OperationId == outcome.OperationId);
        if (at < 0) return;
        var send = carried[at];
        if (outcome.Receipt is { } receipt && receipt.ChatId == send.ChatId && !string.IsNullOrWhiteSpace(receipt.PendingMessageId))
        {
            var next = carried.ToList();
            next[at] = send with { PendingId = receipt.PendingMessageId, PendingMessageIds = receipt.PendingMessageIds?.ToArray(), OperationId = null };
            carried = next;
            looked.Remove(send.LocalId);
        }
        else return;
        Changed?.Invoke(this, send.ChatId);
        await SaveAsync();
        await CheckAsync();
    }

    // The server's record of a send this PC made this session is left out: the message itself shows how it ended, with Retry when it
    // was refused, and the banner said it again and outlived the resend (owner, 2026-11-19). Other devices still see the record.
    public IReadOnlyList<InterruptedSend> ForChat(string chatId) =>
        carried.Concat(hostRows.Where(Shown)).Where(send => send.ChatId == chatId).ToArray();
    // The conversations with a carried send, so a reset can dismiss those it clears.
    public IReadOnlyList<string> ChatIds => [.. carried.Concat(hostRows).Select(send => send.ChatId).Distinct(StringComparer.Ordinal)];

    public async Task LoadAsync()
    {
        if (host is not null && !hostLoaded)
        {
            try { await RefreshServerAsync(); }
            catch (Exception) { /* read again at the next change */ }
        }
        if (state is null) return;
        var current = await state.ProfileAsync();
        if (loaded && current == profile) return;
        var saved = current is null ? null : await state.ReadAsync<InterruptedSend[]>(current, Category, Id);
        var open = outbox.Unsettled.Select(m => m.LocalId).ToHashSet();
        profile = current; loaded = true; looked.Clear();
        carried = (saved ?? []).Where(send => DateTimeOffset.UtcNow - send.CreatedAt < Retention && !open.Contains(send.LocalId)).ToList();
        Changed?.Invoke(this, "");
        try { Loaded?.Invoke(this, OperationIds); }
        catch { /* a listener that fails must not fail the load */ }
        await SaveAsync();
    }

    // One lookup per carried send. A provider still connecting is asked again on the next call.
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync();
        foreach (var send in carried.ToArray())
        {
            if ((send.ConfirmedId ?? send.PendingId) is not { } messageId || looked.Contains(send.LocalId)) continue;
            try
            {
                var ids = send.PendingMessageIds is { Count: > 1 } batch ? batch : new[] { messageId };
                var complete = true;
                foreach (var id in ids)
                {
                    var resolved = await sender.ResolveMessageAsync(send.ChatId, id, cancellationToken);
                    if (resolved.ChatId != send.ChatId || resolved.Delivery.SendState == "PENDING" ||
                        ids.Count > 1 && resolved.Id != id) complete = false;
                }
                looked.Add(send.LocalId);
                if (!complete) continue;
                carried = [.. carried.Where(known => !ReferenceEquals(known, send))];
                Changed?.Invoke(this, send.ChatId);
                await SaveAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (BackendException ex) when (ex.Waits) { }
            catch (Exception) { looked.Add(send.LocalId); }
        }
    }

    // A send another part of the app began and cannot vouch for: Send later's, when Messages closed or crashed after the
    // message was handed to be sent. Recorded like any send left open, never sent again. One already carried under the
    // same id (the outbox's own record of it) is kept as it is, since that one may know more.
    public async Task CarryAsync(InterruptedSend send)
    {
        await LoadAsync();
        if (carried.Any(known => known.LocalId == send.LocalId) || outbox.Find(send.LocalId) is not null) return;
        carried = [.. carried, send];
        Changed?.Invoke(this, send.ChatId);
        await SaveAsync();
    }

    // all: every record of the conversation, those this PC leaves out of the banner too (Google's renumbering reset). Otherwise only what
    // the banner showed: Dismiss there must not retire, for every device, a record of this PC's own send the person never saw in it.
    public async Task DismissAsync(string chatId, bool all = false)
    {
        var server = hostRows.Count == 0 ? [] : hostRows.Where(send => send.ChatId == chatId && (all || Shown(send))).ToArray();
        var removed = carried.Count(send => send.ChatId == chatId);
        if (removed > 0) carried = [.. carried.Where(send => send.ChatId != chatId)];
        if (removed == 0 && server.Length == 0) return;
        // The server's own are dismissed on the server, so they go from every device; one that could not be reached stays drawn, and nothing is sent or asked of a provider either way.
        // Each await stays on the caller's thread: Changed redraws the composer, and raised from the pool it crashed the app (2026-10-26).
        foreach (var send in server)
        {
            try { await host!.DismissUnsettledAsync(send.OperationId!); hostRows = [.. hostRows.Where(known => !ReferenceEquals(known, send))]; }
            catch (Exception) { /* still the server's: it stays until the person dismisses it again */ }
        }
        Changed?.Invoke(this, chatId);
        if (removed > 0) await SaveAsync();
    }

    /// <summary>Reads the server's unsettled sends again and tells the conversations whose list changed.</summary>
    public async Task RefreshServerAsync()
    {
        if (host is null) return;
        // Not ConfigureAwait(false): Changed below redraws the interface, so it is raised on the thread that asked (the dispatcher's, from RefreshServerQuietAsync).
        var read = (await host.ReadUnsettledAsync())
            .Select(row => new InterruptedSend("server:" + row.OperationId, row.ChatId, row.CreatedAt, null, null, null, row.OperationId, OnServer: true, Scheduled: row.ScheduledId is not null,
                Refused: row.State == UnsettledStates.Failed)).ToList();
        hostLoaded = true;
        var before = hostRows;
        hostRows = read;
        foreach (var chatId in before.Concat(read).GroupBy(send => send.LocalId).Where(group => group.Count() != 2).SelectMany(group => group).Select(send => send.ChatId).Distinct())
            Changed?.Invoke(this, chatId);
        // Settled refusals whose dismissal didn't reach the server: once each read finds them there, not in a loop of their own.
        foreach (var name in owed.Where(name => read.Any(send => send.OperationId == name)).ToArray())
        {
            try { await DismissSettledAsync(name); }
            catch (Exception) { /* still owed */ }
        }
    }

    private async Task RefreshServerQuietAsync()
    {
        try { await (dispatcher is null ? RefreshServerAsync() : dispatcher.InvokeAsync(RefreshServerAsync)); }
        catch (Exception) { /* read again at the next change or load */ }
    }

    private async void OutboxChanged(object? sender, string chatId)
    {
        try { await SaveAsync(); }
        catch { /* Recovery is best effort; the sends themselves are unaffected. */ }
    }

    private async Task SaveAsync()
    {
        if (state is null || !loaded || profile is null) return;
        // A message still waiting for Undo send has reached no provider: after a crash it was certainly not sent, and
        // "may not have sent" would say otherwise.
        var records = outbox.Unsettled.Where(m => m.State != OutgoingState.Held)
            .Select(m => new InterruptedSend(m.LocalId, m.ChatId, m.CreatedAt, m.PendingId, m.ConfirmedId, m.PendingMessageIds, m.OperationId))
            .Concat(carried).ToArray();
        await writing.WaitAsync();
        try
        {
            // A different account on this PC keeps its own record; nothing carries across profiles.
            if (await state.ProfileAsync() != profile) return;
            await state.WriteAsync(profile, Category, Id, records);
        }
        finally { writing.Release(); }
    }

    public void Dispose()
    {
        outbox.Changed -= OutboxChanged;
        if (host is not null) outbox.RefusalSettled -= RefusalSettled;
        if (outcomes is not null) outcomes.Reconciled -= OperationReconciled;
        writing.Dispose();
    }
}
