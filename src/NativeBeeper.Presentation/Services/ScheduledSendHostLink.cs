namespace NativeBeeper.Presentation.Services;

// What a client of a Messages Server schedules through (SWITCHBOARD.md stage 6 slice 11, S2 and S6). The server holds the schedule and fires it; the client draws
// its rows from its replica and asks the server for the four commands. The Presentation library does not know the protocol: the app gives ScheduledSends a
// link that does (ScheduledHostLink), and a harness gives it a fake.

/// <summary>One scheduled message as the client's replica holds it, with the rev of the row it was drawn from (the condition of a command on it).</summary>
public sealed record HostedScheduledRow(ScheduledSend Item, long Rev);

/// <summary>
/// What a command did. <see cref="Done"/>: it was carried out. <see cref="Conflict"/>: the row had changed (or was gone) since it was drawn, so nothing was
/// done and the client redraws from its replica; this is no failure. <see cref="AlreadySending"/>: the message is on its way and cannot be changed. <see cref="Retired"/>: a create
/// for an id whose message has since gone from the server; nothing was made, and none will be under that id.
/// </summary>
public enum ScheduledCommandStatus { Done, Conflict, AlreadySending, Retired }

/// <param name="Item">With <see cref="ScheduledCommandStatus.Done"/>: the message a create kept (its id and the rev it was kept at), or the one an edit took back.</param>
public sealed record ScheduledCommandResult(ScheduledCommandStatus Status, HostedScheduledRow? Item = null);

/// <summary>
/// A create whose request was handed to the server and whose answer was lost, with nothing in the server's journal to say what became of it. <see cref="ItemId"/> is the id
/// the message has if the server made it (derived from <see cref="OperationId"/>), so a client can draw a stand-in under it and ask again under the same operation id.
/// </summary>
public sealed class ScheduledOutcomeUnknownException(string message, string operationId, string itemId) : IOException(message)
{
    public string OperationId { get; } = operationId;
    public string ItemId { get; } = itemId;
}

/// <summary>
/// The server cannot be reached, so nothing was asked of it and nothing was done. A refusal in words (<see cref="InvalidOperationException"/>) is the server's; this one is the
/// connection's, and a caller that must tell the two apart (a stand-in that may or may not exist on the server) does.
/// </summary>
public sealed class ScheduledOfflineException(string message) : InvalidOperationException(message);

/// <summary>
/// An edit whose outcome could not be told: the words are given back as <see cref="Copy"/>, but the message may still be scheduled on the server, and will send. It is not a
/// successful edit and must not be shown as one: scheduling the copy again could make the message go twice.
/// </summary>
public sealed class ScheduledEditUnresolvedException(ScheduledSend copy, string message) : IOException(message)
{
    public ScheduledSend Copy { get; } = copy;
}

/// <summary>A stand-in for a create of unknown outcome, as a device keeps it until its replica settles it: the words, and the operation id that asks for the same create again.</summary>
public sealed record StandInRecord(ScheduledSend Item, string OperationId);

/// <summary>
/// A send of unknown outcome, or failed, that the server made for a client (<c>outbox:</c> rows, stage 6 slice 11): all a client needs to say "may not have sent". The words are
/// the server's to keep for a later slice; no client draws them. <see cref="State"/> is one of <see cref="UnsettledStates"/>.
/// <see cref="CreatedAt"/> is when the send began (shown); <see cref="RecordedAt"/> is when the server wrote this record, which is what it is kept by: a send interrupted days before a restart is recorded at the restart.
/// </summary>
public sealed record SharedUnsettledSend(string OperationId, string ChatId, string? Text, string State, DateTimeOffset CreatedAt, string? ScheduledId = null, DateTimeOffset? RecordedAt = null);

public static class UnsettledStates
{
    public const string Unknown = "unknown", Failed = "failed";
}

/// <summary>
/// The server's unsettled sends as a client draws and dismisses them. Dismissing sends nothing and asks nothing of a provider: it removes the shared record, for every device.
/// </summary>
public interface IHostUnsettledLink
{
    Task<IReadOnlyList<SharedUnsettledSend>> ReadUnsettledAsync(CancellationToken cancellationToken = default);
    /// <summary>Rows of the replica changed. Raised on any thread.</summary>
    event EventHandler? UnsettledChanged;
    Task DismissUnsettledAsync(string operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The client's line to the server that fires its scheduled sends. A command that cannot be answered throws: an <see cref="InvalidOperationException"/> whose message is
/// safe to show when the server refused it or is not connected (nothing was done), and an <see cref="IOException"/> when its outcome could not be told (it may have been
/// done; the replica will say). No command is ever repeated by the link, and none waits for a connection: with none it fails at once.
/// </summary>
public interface IScheduledSendHostLink
{
    /// <summary>
    /// Whether the server fires this client's scheduled sends: true from a host of protocol 9, false from an older one (the client fires its own as before), and null while
    /// no host has been heard at all. Unknown is neither: such a client fires nothing, since a server that may fire the same message is the one risk (S9).
    /// </summary>
    ValueTask<bool?> HostFiresAsync(CancellationToken cancellationToken = default);
    /// <summary>The answer of <see cref="HostFiresAsync"/> may have changed. Raised on any thread.</summary>
    event EventHandler? HostChanged;

    /// <summary>Every scheduled message in the replica, with its rev.</summary>
    Task<IReadOnlyList<HostedScheduledRow>> ReadRowsAsync(CancellationToken cancellationToken = default);
    /// <summary>Rows of the replica changed (another device's, or the server's own: a message fired, a remote command). Raised on any thread.</summary>
    event EventHandler? RowsChanged;

    /// <param name="operationId">
    /// Names the create when the caller must be able to ask again for the same one (an unconfirmed create, a message moved from the PC): the server makes the message once for one operation id, and answers
    /// the first answer to a repeat. Left out, a new one is made. A repeat must carry the same words and time.
    /// </param>
    /// <exception cref="ScheduledOutcomeUnknownException">The request was handed over and its answer lost, and the server's journal could not say what became of it.</exception>
    Task<ScheduledCommandResult> CreateAsync(string chatId, string text, DateTimeOffset due, string? replyToId, string? replyPreview, string? operationId = null, CancellationToken cancellationToken = default);
    Task<ScheduledCommandResult> EditAsync(string id, long ifRev, CancellationToken cancellationToken = default);
    Task<ScheduledCommandResult> CancelAsync(string id, long ifRev, CancellationToken cancellationToken = default);
    Task<ScheduledCommandResult> SendNowAsync(string id, long ifRev, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel (or take back, <paramref name="edit"/>) a message this device never saw the row of: its stand-in for a create of unknown outcome. The server takes the message away if it made it,
    /// and otherwise closes the id for good; it never makes one. <paramref name="operationId"/> is the caller's, fixed per stand-in and verb, so asking again is the same command and the
    /// server answers it with the first answer. Throws <see cref="InvalidOperationException"/> in words when the server is too old to know this command (nothing was done).
    /// </summary>
    Task<ScheduledCommandResult> DiscardUnseenAsync(string id, bool edit, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stand-ins this device kept (its own, never sent anywhere: a row of the device's replica that no category shares), so a restart does not lose the words of a create
    /// that may not have reached the server. Kept until the replica shows the message or the person cancels or edits the stand-in.
    /// </summary>
    Task<IReadOnlyList<StandInRecord>> ReadStandInsAsync(CancellationToken cancellationToken = default);
    Task KeepStandInAsync(StandInRecord record, CancellationToken cancellationToken = default);
    Task ForgetStandInAsync(string itemId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Custody of the client's own engine (S9) while a link says whether a server fires for it: the engine fires only when the server certainly does not
/// (an older host), never when it does and never while that is not known.
/// </summary>
internal sealed class HostLinkCustody(IScheduledSendHostLink link) : IScheduledSendCustody
{
    public async ValueTask<bool> IsLocalAsync() => await link.HostFiresAsync().ConfigureAwait(false) == false;
}

/// <summary>
/// Send later as a client of a server that fires it (S2): the rows are the replica's, the alarm does not run here, and every change is a command to the server.
/// Same surface as <see cref="ScheduledSendEngine"/>'s, so the interface does not know which it is drawing.
/// </summary>
/// <remarks>
/// <para><b>Nothing here sends.</b> A message goes only when the server's engine fires it. A Send now is a command that asks it to; one that is refused,
/// conflicting or lost is never made again by this class, and the next press is a new command against the row as it is drawn then.</para>
/// <para><b>Optimistic create (S6).</b> The message is drawn at once as <see cref="ScheduledSendReason.Scheduling"/> while the server answers; the box keeps its
/// words until then, so a refusal needs nothing returned. Once kept it stays drawn from the answer until the replica holds the row (a few seconds at most).
/// A cancel or edit that was done hides its row the same way. A lost answer is settled by the link against the server's journal, never asked again.</para>
/// </remarks>
public sealed class HostedScheduledSends(IScheduledSendHostLink link, TimeProvider? time = null, Action<string>? log = null)
{
    // How long what a command did is drawn from its answer before the replica must hold it (the shared event follows within milliseconds).
    private static readonly TimeSpan Bridge = TimeSpan.FromSeconds(5);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim refreshing = new(1, 1);
    private IReadOnlyList<HostedScheduledRow> replica = [];
    // Messages being scheduled (drawn as scheduling), messages kept by an answer the replica has not shown yet, and ids an answer took out.
    private readonly Dictionary<string, ScheduledSend> scheduling = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HostedScheduledRow Row, DateTimeOffset Until)> kept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> taken = new(StringComparer.Ordinal);
    // A create whose outcome could not be told: a stand-in under the id the server would have made, drawn as scheduling for a while and then as not sent (unconfirmed), until the
    // replica shows the message (the server made it after all) or the person edits or cancels it. The words are kept here, in memory; the operation id asks again for the same create.
    private readonly Dictionary<string, Placeholder> placeholders = new(StringComparer.Ordinal);
    private sealed record Placeholder(ScheduledSend Item, string OperationId, DateTimeOffset Until);
    private static readonly TimeSpan Unconfirming = TimeSpan.FromSeconds(15);
    private readonly object gate = new();
    private bool loaded, listening;
    public event EventHandler<string>? Changed;

    public IReadOnlyList<ScheduledSend> ForChat(string chatId) => [.. Rows().Where(row => row.ChatId == chatId).OrderBy(row => row.Due)];
    public IReadOnlyList<ScheduledSend> All => [.. Rows().OrderBy(row => row.Due)];
    public ScheduledSend? Find(string id) => Rows().FirstOrDefault(row => row.Id == id);
    public bool IsSendingIn(Func<string, bool> chat) => Rows().Any(row => row.State == ScheduledSendState.Sending && chat(row.ChatId));

    private List<ScheduledSend> Rows()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var id in kept.Where(entry => entry.Value.Until <= now).Select(entry => entry.Key).ToArray()) kept.Remove(id);
            foreach (var id in taken.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray()) taken.Remove(id);
            var rows = replica.Where(row => !taken.ContainsKey(row.Item.Id)).Select(row => row.Item).ToList();
            foreach (var (id, entry) in kept)
                if (!taken.ContainsKey(id) && rows.All(row => row.Id != id)) rows.Add(entry.Row.Item);
            rows.AddRange(scheduling.Values);
            foreach (var (id, stand) in placeholders)
                if (rows.All(row => row.Id != id)) rows.Add(Standing(stand, now));
            return rows;
        }
    }

    private static ScheduledSend Standing(Placeholder stand, DateTimeOffset now) =>
        now < stand.Until ? stand.Item : stand.Item with { State = ScheduledSendState.NotSent, Reason = ScheduledSendReason.Unconfirmed };

    private (ScheduledSend Item, long Rev)? Drawn(string id)
    {
        lock (gate)
        {
            if (taken.ContainsKey(id)) return null;
            if (replica.FirstOrDefault(row => row.Item.Id == id) is { } held) return (held.Item, held.Rev);
            if (kept.TryGetValue(id, out var entry)) return (entry.Row.Item, entry.Row.Rev);
            return placeholders.TryGetValue(id, out var stand) ? (Standing(stand, clock.GetUtcNow()), 0) : null;
        }
    }

    /// <summary>Reads the replica's rows, and from then on follows its changes.</summary>
    public async Task LoadAsync()
    {
        if (!listening) { listening = true; link.RowsChanged += (_, _) => _ = RefreshQuietAsync(); }
        await RefreshAsync().ConfigureAwait(false);
        if (standInsRead) return;
        // The stand-ins this device kept before it was closed: words of creates that may not have reached the server, back as messages not sent (unconfirmed) unless the replica
        // already shows them. Read once the replica is, so one the server did make is not drawn twice.
        IReadOnlyList<StandInRecord> kept;
        try { kept = await link.ReadStandInsAsync().ConfigureAwait(false); }
        catch (Exception failure) { log?.Invoke($"scheduled-send hosted stand-ins unreadable error={failure.GetType().Name}"); return; }
        standInsRead = true;
        var settled = new List<string>();
        HashSet<string> drawn = new(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var record in kept)
            {
                if (replica.Any(row => row.Item.Id == record.Item.Id)) settled.Add(record.Item.Id);
                else if (placeholders.TryAdd(record.Item.Id, new Placeholder(record.Item with { Reason = ScheduledSendReason.Scheduling }, record.OperationId, DateTimeOffset.MinValue)))
                    drawn.Add(record.Item.ChatId);
            }
        }
        foreach (var id in settled) await ForgetQuietAsync(id).ConfigureAwait(false);
        foreach (var chatId in drawn) Raise(chatId);
    }

    private bool standInsRead;

    // False when the device could not keep the stand-in: the words are then in memory only, and the caller must not let go of the box's copy.
    private async Task<bool> KeepAsync(Placeholder stand)
    {
        try { await link.KeepStandInAsync(new StandInRecord(stand.Item with { Reason = ScheduledSendReason.None }, stand.OperationId)).ConfigureAwait(false); return true; }
        catch (Exception failure) { log?.Invoke($"scheduled-send hosted stand-in not kept error={failure.GetType().Name}"); return false; }
    }

    public const string StandInNotKeptSentence = "The server hasn't said whether it kept this message, and this device couldn't keep a copy of your words either. Your text is still here: check the conversation before scheduling it again.";

    private async Task ForgetQuietAsync(string id)
    {
        try { await link.ForgetStandInAsync(id).ConfigureAwait(false); }
        catch (Exception failure) { log?.Invoke($"scheduled-send hosted stand-in not forgotten error={failure.GetType().Name}"); }
    }

    private async Task RefreshQuietAsync()
    {
        try { await RefreshAsync().ConfigureAwait(false); }
        catch (Exception failure) { log?.Invoke($"scheduled-send hosted refresh failed error={failure.GetType().Name}"); }
    }

    /// <summary>Reads the replica again and tells the conversations whose rows changed.</summary>
    public async Task RefreshAsync()
    {
        await refreshing.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = await link.ReadRowsAsync().ConfigureAwait(false);
            HashSet<string> changed = new(StringComparer.Ordinal);
            List<string> settledStandIns = [];
            lock (gate)
            {
                var before = replica.ToDictionary(row => row.Item.Id);
                var after = read.ToDictionary(row => row.Item.Id);
                foreach (var (id, row) in after)
                    if (!before.TryGetValue(id, out var was) || was.Rev != row.Rev || was.Item != row.Item) changed.Add(row.Item.ChatId);
                foreach (var (id, row) in before)
                    if (!after.ContainsKey(id)) changed.Add(row.Item.ChatId);
                replica = read;
                // The replica holds it now: the answer's copy has done its part. A row an answer took out and the replica no longer holds is out for good.
                foreach (var id in after.Keys) { kept.Remove(id); scheduling.Remove(id); if (placeholders.Remove(id)) settledStandIns.Add(id); }
                foreach (var id in taken.Keys.Where(id => !after.ContainsKey(id)).ToArray()) taken.Remove(id);
                loaded = true;
            }
            foreach (var chatId in changed) Raise(chatId);
            foreach (var id in settledStandIns) await ForgetQuietAsync(id).ConfigureAwait(false);
        }
        finally { refreshing.Release(); }
    }

    private void Raise(string chatId)
    {
        try { Changed?.Invoke(this, chatId); }
        catch (Exception) { /* a listener that fails must not drop the rows */ }
    }

    /// <summary>Keeps this text for <paramref name="due"/> on the server. Throws, with the words safe to show, when it was not kept.</summary>
    public async Task<ScheduledSend> ScheduleAsync(string chatId, string text, DateTimeOffset due, string? replyToId = null, string? replyPreview = null)
    {
        if (!loaded) await RefreshAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(chatId)) throw new ArgumentException("There is nothing to schedule.");
        var now = clock.GetUtcNow();
        // Said in the composer as it is, so no parameter name is attached to it.
        if (due <= now) throw new ArgumentException("Choose a time that hasn't passed yet.");
        if (Rows().Count >= ScheduledSendEngine.Limit) throw new InvalidOperationException("Too many messages are scheduled. Send or cancel some first.");
        var drawing = new ScheduledSend("scheduling:" + Guid.NewGuid().ToString("N"), chatId, text, due, now, replyToId, replyToId is null ? null : replyPreview,
            Reason: ScheduledSendReason.Scheduling);
        lock (gate) scheduling[drawing.Id] = drawing;
        Raise(chatId);
        ScheduledCommandResult result;
        try { result = await link.CreateAsync(chatId, text, due, replyToId, replyToId is null ? null : replyPreview).ConfigureAwait(false); }
        // Handed over and never answered, and the server's journal cannot say: the box must not keep the words (a second press would make a second message, and it
        // would be the person's mistake only because nobody told them). A stand-in row under the id the server would have made keeps them, and the replica settles it.
        catch (ScheduledOutcomeUnknownException unknown)
        {
            var stand = new Placeholder(drawing with { Id = unknown.ItemId, Reason = ScheduledSendReason.Scheduling }, unknown.OperationId, clock.GetUtcNow() + Unconfirming);
            lock (gate) { scheduling.Remove(drawing.Id); placeholders[unknown.ItemId] = stand; }
            // Kept on this device before the box lets go of the words: the stand-in is their only copy if the server never got the request, and a restart must not lose it.
            var kept = await KeepAsync(stand).ConfigureAwait(false);
            log?.Invoke($"scheduled-send hosted create outcome=unknown id={ScheduledSendEngine.Tag(unknown.ItemId)} chat={ScheduledSendEngine.Tag(chatId)}{(kept ? "" : " kept=0")}");
            Raise(chatId);
            _ = RefreshQuietAsync();
            // Not kept: this is not a success, or the box would clear and save an empty draft over the only copy of the words. The stand-in stays drawn for this run (the server may
            // have the message), and the words stay in the box.
            if (!kept) throw new InvalidOperationException(StandInNotKeptSentence);
            return stand.Item;
        }
        catch (Exception failure)
        {
            lock (gate) scheduling.Remove(drawing.Id);
            Raise(chatId);
            // The composer shows the words of an invalid-operation failure and keeps the text; an outcome that could not be told reads the same way, with its own sentence.
            if (failure is IOException) throw new InvalidOperationException(failure.Message);
            throw;
        }
        lock (gate)
        {
            scheduling.Remove(drawing.Id);
            if (result is { Status: ScheduledCommandStatus.Done, Item: { } row }) kept[row.Item.Id] = (row, clock.GetUtcNow() + Bridge);
        }
        Raise(chatId);
        if (result is not { Status: ScheduledCommandStatus.Done, Item: { } done })
            throw new InvalidOperationException("The server could not keep this message. Try again.");
        return done.Item;
    }

    /// <summary>Removes a message that has not started sending. False once it has, when another device changed it first, or when it is not here.</summary>
    public async Task<bool> CancelAsync(string id)
    {
        if (Drawn(id) is not { Item: { State: not ScheduledSendState.Sending } item, Rev: var rev }) return false;
        // A stand-in is cancelled on the server, by the id it was made with: the server may have made the message and the answer been lost, and the replica not have it
        // yet. Dropping it here alone would leave a message that sends.
        if (StandIn(id)) return await CancelStandInAsync(id, item).ConfigureAwait(false);
        var result = await CommandAsync(() => link.CancelAsync(id, rev), id, item.ChatId).ConfigureAwait(false);
        if (result.Status != ScheduledCommandStatus.Done) return false;
        Took(id, item.ChatId);
        return true;
    }

    /// <summary>Takes a message that has not started sending back for the composer: it is no longer scheduled. Null when it cannot be taken.</summary>
    public async Task<ScheduledSend?> TakeForEditAsync(string id)
    {
        if (Drawn(id) is not { Item: { State: not ScheduledSendState.Sending } item, Rev: var rev }) return null;
        if (StandIn(id)) return await EditStandInAsync(id, item).ConfigureAwait(false);
        ScheduledCommandResult result;
        try { result = await CommandAsync(() => link.EditAsync(id, rev), id, item.ChatId).ConfigureAwait(false); }
        // The edit may or may not have been made. The words are in this client's copy of the row and must not be lost, but handing them back as a finished edit would invite
        // scheduling them again while the original still sends. The outcome is explicit: a copy, and the word that the original may still go.
        catch (IOException unknown) when (unknown is not ScheduledEditUnresolvedException)
        {
            log?.Invoke($"scheduled-send hosted edit outcome=unknown id={ScheduledSendEngine.Tag(id)} chat={ScheduledSendEngine.Tag(item.ChatId)} reason={unknown.GetType().Name}");
            throw new ScheduledEditUnresolvedException(item with { State = ScheduledSendState.Waiting, Reason = ScheduledSendReason.None }, EditUnresolvedSentence);
        }
        if (result.Status != ScheduledCommandStatus.Done) return null;
        Took(id, item.ChatId);
        return result.Item?.Item ?? item;
    }

    /// <summary>Asks the server to send a waiting or not-sent message now. False when it cannot be asked: already sending, changed by another device, or not here.</summary>
    public async Task<bool> SendNowAsync(string id)
    {
        if (Drawn(id) is not { Item: { State: not ScheduledSendState.Sending } item, Rev: var rev }) return false;
        if (StandIn(id))
        {
            // Still waiting for the server's answer: nothing to press yet.
            if (item.Reason == ScheduledSendReason.Scheduling) return false;
            return await SendStandInAsync(id, item).ConfigureAwait(false);
        }
        var result = await CommandAsync(() => link.SendNowAsync(id, rev), id, item.ChatId).ConfigureAwait(false);
        if (result.Status != ScheduledCommandStatus.Done) return false;
        // The server's row is what it says next (waiting for its service, or sending): the event that carries it redraws.
        return true;
    }

    private bool StandIn(string id) { lock (gate) return placeholders.ContainsKey(id) && replica.All(row => row.Item.Id != id); }

    private async Task DroppedAsync(string id, string chatId)
    {
        lock (gate) placeholders.Remove(id);
        await ForgetQuietAsync(id).ConfigureAwait(false);
        Raise(chatId);
    }

    public const string EditUnresolvedSentence = "This edit may not have applied. The original may still send, so what you see here is a copy: check the conversation before scheduling it again.";

    // The create asked again under the operation id it was first asked under, with the words and the time it had. Journaled, so the server makes the message once whichever of
    // the two requests it saw and answers the second with the first's answer. Returns the message as the server holds it, or null when the server holds none and will make none
    // (it refused, or the id has since gone from it). With no connection, or no answer, it throws and the stand-in stays: nothing is guessed about what the server has.
    private async Task<(HostedScheduledRow? Row, bool Retired)> AskAgainAsync(Placeholder stand, string verb)
    {
        try
        {
            var created = await link.CreateAsync(stand.Item.ChatId, stand.Item.Text, stand.Item.Due, stand.Item.ReplyToId, stand.Item.ReplyPreview, stand.OperationId).ConfigureAwait(false);
            if (created.Status == ScheduledCommandStatus.Retired) return (null, true);
            return created is { Status: ScheduledCommandStatus.Done, Item: { } row } ? (row, false) : (null, false);
        }
        catch (ScheduledOfflineException)
        {
            throw new ScheduledOfflineException($"Can't {verb} yet: not connected to the server, which may already have this message. Try again once it is connected.");
        }
        catch (ScheduledOutcomeUnknownException)
        {
            throw new IOException($"Can't {verb} yet: the server hasn't said whether it has this message. Try again in a moment.");
        }
        // The server answered in words that it will not make it: it does not have it.
        catch (InvalidOperationException)
        {
            return (null, false);
        }
    }

    private bool TryStand(string id, out Placeholder stand)
    {
        lock (gate) return placeholders.TryGetValue(id, out stand!);
    }

    // Cancelling or editing a stand-in never asks the server to make the message again: made just before its time and removed just after, it would be sent by the person's own cancel. The
    // command names the id and no rev (the device never saw the row): the server takes the message away if it made it, and if it never did it closes the id, so that no create, lost on the
    // way or asked again, can make it. One command, so nothing can pass between the making and the removal.
    // The operation id is fixed by the stand-in and the verb, so a press whose answer was lost and then pressed again is the same command: the server's journal answers it with the
    // first answer, and a second press can never be told "gone" by the first one's own work.
    private async Task<ScheduledCommandResult> DiscardStandInAsync(Placeholder stand, string chatId, string verb, bool edit)
    {
        var operationId = (edit ? "discard-edit:" : "discard-cancel:") + stand.OperationId;
        try { return await CommandAsync(() => link.DiscardUnseenAsync(stand.Item.Id, edit, operationId), stand.Item.Id, chatId).ConfigureAwait(false); }
        catch (ScheduledOfflineException)
        {
            throw new ScheduledOfflineException($"Can't {verb} yet: not connected to the server, which may already have this message. Try again once it is connected.");
        }
    }

    public const string GoneSentence = "That message has already gone from the server: it was sent, or canceled from another device. Check the conversation.";
    public const string GoneCopySentence = "This message has already gone from the server (it was sent, or canceled from another device), so what you see here is a copy of your words: check the conversation before scheduling it again.";

    private async Task<bool> CancelStandInAsync(string id, ScheduledSend item)
    {
        if (!TryStand(id, out var stand)) return false;
        ScheduledCommandResult result;
        try { result = await DiscardStandInAsync(stand, item.ChatId, "cancel", edit: false).ConfigureAwait(false); }
        catch (IOException) { throw new IOException("Can't cancel yet: the server hasn't said whether it has this message. Try again in a moment."); }
        switch (result.Status)
        {
            // The message the server made is gone, or it was never made and never will be: cancelled.
            case ScheduledCommandStatus.Done:
                await DroppedAsync(id, item.ChatId).ConfigureAwait(false);
                Took(id, item.ChatId);
                return true;
            // Gone from the server already, and what became of it is not ours to say: the person is told so, not left to wonder why the row went.
            case ScheduledCommandStatus.Retired:
                await DroppedAsync(id, item.ChatId).ConfigureAwait(false);
                throw new InvalidOperationException(GoneSentence);
            default: return false;
        }
    }

    private async Task<ScheduledSend?> EditStandInAsync(string id, ScheduledSend item)
    {
        if (!TryStand(id, out var stand)) return null;
        var words = item with { State = ScheduledSendState.Waiting, Reason = ScheduledSendReason.None };
        ScheduledCommandResult result;
        // An answer that did not come: the words go back to the person as a copy, with the stand-in still here to press again (same command), and never dropped first.
        try { result = await DiscardStandInAsync(stand, item.ChatId, "edit", edit: true).ConfigureAwait(false); }
        catch (IOException) { throw new ScheduledEditUnresolvedException(words, EditUnresolvedSentence); }
        switch (result.Status)
        {
            case ScheduledCommandStatus.Done:
                await DroppedAsync(id, item.ChatId).ConfigureAwait(false);
                Took(id, item.ChatId);
                return result.Item?.Item ?? words;
            // The words travel in the exception, which is thrown at once; the stand-in has nothing more to ask.
            case ScheduledCommandStatus.Retired:
                await DroppedAsync(id, item.ChatId).ConfigureAwait(false);
                throw new ScheduledEditUnresolvedException(words, GoneCopySentence);
            default: return null;
        }
    }

    // Send now on a stand-in the server never confirmed: the create again, then the message is sent now, as any Send now is.
    private async Task<bool> SendStandInAsync(string id, ScheduledSend item)
    {
        if (!TryStand(id, out var stand)) return false;
        var (row, retired) = await AskAgainAsync(stand, "send").ConfigureAwait(false);
        if (row is null)
        {
            // Not made, and the id has gone from the server or was refused: the words are the person's again, as a message not sent that cannot be sent from here.
            if (retired) await DroppedAsync(id, item.ChatId).ConfigureAwait(false);
            return false;
        }
        lock (gate) { placeholders.Remove(id); kept[row.Item.Id] = (row, clock.GetUtcNow() + Bridge); }
        await ForgetQuietAsync(id).ConfigureAwait(false);
        Raise(item.ChatId);
        var sent = await CommandAsync(() => link.SendNowAsync(row.Item.Id, row.Rev), row.Item.Id, item.ChatId).ConfigureAwait(false);
        return sent.Status == ScheduledCommandStatus.Done;
    }

    /// <summary>
    /// Makes a message this PC kept for an older server on the server, and sends it now: a new operation, named for the message so asking again makes it once. It is made for a time
    /// long after anything, so that if the Send now that follows does not land it waits there for the person rather than going by itself. <paramref name="removeLocal"/> takes the PC's own copy
    /// away once the server has the message, so it cannot be made twice.
    /// </summary>
    public async Task<bool> SendMovedAsync(ScheduledSend local, Func<Task> removeLocal)
    {
        var operationId = "moved:" + local.Id;
        if (!loaded) await RefreshAsync().ConfigureAwait(false);
        HostedScheduledRow? row = null;
        lock (gate) { if (replica.FirstOrDefault(found => found.Item.Id == MovedId(local)) is { } held) row = held; }
        if (row is null)
        {
            var created = await link.CreateAsync(local.ChatId, local.Text, local.CreatedAt.AddYears(10), local.ReplyToId, local.ReplyPreview, operationId).ConfigureAwait(false);
            // The server has already finished with this message (it was sent, cancelled or taken back from another device): the PC's copy has nothing left to be, and goes.
            if (created.Status == ScheduledCommandStatus.Retired) { await removeLocal().ConfigureAwait(false); Raise(local.ChatId); return false; }
            if (created is not { Status: ScheduledCommandStatus.Done, Item: { } made }) return false;
            row = made;
            lock (gate) kept[made.Item.Id] = (made, clock.GetUtcNow() + Bridge);
        }
        await removeLocal().ConfigureAwait(false);
        Raise(local.ChatId);
        try
        {
            var sent = await CommandAsync(() => link.SendNowAsync(row.Item.Id, row.Rev), row.Item.Id, local.ChatId).ConfigureAwait(false);
            return sent.Status == ScheduledCommandStatus.Done;
        }
        // Made and kept by the server, not yet sent: it is a scheduled message there, and the person can send it from its row.
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            log?.Invoke($"scheduled-send hosted moved outcome=made_not_sent id={ScheduledSendEngine.Tag(local.Id)} chat={ScheduledSendEngine.Tag(local.ChatId)}");
            return true;
        }
    }

    /// <summary>The id the server gives the message <see cref="SendMovedAsync"/> makes for <paramref name="local"/>.</summary>
    public static string MovedId(ScheduledSend local) => ScheduledIdPrefix + "moved:" + local.Id;

    /// <summary>The start of every id a server makes for a message (<c>ScheduledIds.Prefix</c> of the protocol, which this library cannot see).</summary>
    public const string ScheduledIdPrefix = "host:scheduled:";

    // A conflict or a row already sending is no failure: the replica is read again and the row drawn as it is. Any other failure goes to the caller as it came.
    private async Task<ScheduledCommandResult> CommandAsync(Func<Task<ScheduledCommandResult>> run, string id, string chatId)
    {
        ScheduledCommandResult result;
        try { result = await run().ConfigureAwait(false); }
        catch (Exception)
        {
            // Whatever happened to the command, the replica is what the row looks like now.
            await RefreshQuietAsync().ConfigureAwait(false);
            throw;
        }
        if (result.Status != ScheduledCommandStatus.Done)
        {
            log?.Invoke($"scheduled-send hosted command outcome={(result.Status == ScheduledCommandStatus.Conflict ? "conflict" : "already_sending")} id={ScheduledSendEngine.Tag(id)} chat={ScheduledSendEngine.Tag(chatId)}");
            await RefreshQuietAsync().ConfigureAwait(false);
        }
        return result;
    }

    private void Took(string id, string chatId)
    {
        lock (gate) { taken[id] = clock.GetUtcNow() + Bridge; kept.Remove(id); }
        Raise(chatId);
    }

    /// <summary>Removes every message this predicate names that is not already sending (a Google reset); each is a command, and one that cannot be made is left.</summary>
    public async Task<int> ForgetAsync(Func<string, bool> chat)
    {
        if (!loaded) await RefreshAsync().ConfigureAwait(false);
        var gone = 0;
        foreach (var item in Rows().Where(row => row.State != ScheduledSendState.Sending && chat(row.ChatId)).ToArray())
        {
            try { if (await CancelAsync(item.Id).ConfigureAwait(false)) gone++; }
            catch (Exception) { /* left where it is; the reset says how many went */ }
        }
        return gone;
    }
}
