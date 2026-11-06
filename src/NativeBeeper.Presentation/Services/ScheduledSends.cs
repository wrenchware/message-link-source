using System.Text.Json.Serialization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Waiting: its time has not come. Sending: handed to the outbox; if Messages ends now it is an interrupted send, never
// sent again. NotSent: its time came and it was not sent, for Reason; it waits for Send now, Edit or Cancel.
public enum ScheduledSendState { Waiting, Sending, NotSent }
// Account (appended): it was scheduled while a different account of its service was signed in.
// Scheduling (appended): a client's own, never stored or sent to a server: a message drawn as waiting while the server that keeps the schedule has not answered yet.
// Moved (appended): kept on this PC for a host that fired it itself until the host became one that fires for every client; it is not fired by anyone, and Send now makes it on the server.
// Unconfirmed (appended): a client asked its server to keep it and never learned whether it did; Send now asks again under the same operation id, so the server makes it once.
public enum ScheduledSendReason { None, Closed, Offline, Refused, Late, Account, Scheduling, Moved, Unconfirmed }

// One message the user asked Messages to send later: the words, the conversation and the message it replies to. Kept in
// the protected local store (PrivateState), as drafts are; never written to a log.
public sealed record ScheduledSend(string Id, string ChatId, string Text, DateTimeOffset Due, DateTimeOffset CreatedAt,
    string? ReplyToId = null, string? ReplyPreview = null, ScheduledSendState State = ScheduledSendState.Waiting,
    ScheduledSendReason Reason = ScheduledSendReason.None, DateTimeOffset? SendingAt = null,
    // Which signed-in account it was scheduled from (App.SavedMessageScopeAsync), so it never goes from another one: a
    // Messenger conversation id names no account, and a one-to-one thread with the same person has the same id under
    // each. Null when that could not be told. Appended: built positionally.
    string? Scope = null,
    // Which hand-over this is (SWITCHBOARD.md stage 6 S4): a host that fires for its clients writes n here, in the same save that marks the row Sending, and
    // sends under the fixed id sched:<id>:<n>; a Send now of a not-sent row is attempt n + 1, a different id. 0 where nothing counts (the in-process app), and
    // then not written at all, so a row there is exactly the bytes it always was.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Attempt = 0);

/// <summary>
/// Send later in the in-process app: text kept on this PC until its time, then sent through the same outbox the composer
/// uses, once. The rules are <see cref="ScheduledSendEngine"/>'s; this gives it this PC's protected store
/// (<see cref="PrivateState"/>), the one <see cref="Outbox"/>, the interrupted-send record and the backends' send check,
/// and its schedule is always this PC's (custody is local until a schedule can move to a server, SWITCHBOARD.md stage 6).
/// </summary>
public sealed class ScheduledSends : IDisposable
{
    public static readonly TimeSpan SendWindow = ScheduledSendEngine.SendWindow;
    public static readonly TimeSpan RetryInterval = ScheduledSendEngine.RetryInterval;
    public static readonly TimeSpan CancelGrace = ScheduledSendEngine.CancelGrace;
    public const int Limit = ScheduledSendEngine.Limit;
    private const string Category = "scheduled", Key = "all";
    private readonly ScheduledSendEngine engine;
    // A client of a Messages Server (stage 6 slice 11): the server's schedule, drawn from the replica. Null in the in-process app and the loopback, where every
    // member below is the engine's call exactly as it was.
    private readonly IScheduledSendHostLink? link;
    private readonly HostedScheduledSends? hosted;
    // The server fires this client's scheduled sends (decided at load and again whenever the link says it may have changed).
    private volatile bool useHost;
    public event EventHandler<string>? Changed;

    /// <param name="hostLink">
    /// Given by a client of a server (<c>local</c> and <c>remote</c>). The engine is then the older-host path only: its custody is local exactly while the
    /// server certainly does not fire (a host below protocol 9), so it never fires beside a server that does (S9) and never while that is not known; with a host
    /// of 9 or later every member goes to the server's schedule (<see cref="HostedScheduledSends"/>) and the alarm does not run here (S2).
    /// </param>
    public ScheduledSends(Outbox outbox, IMessageSender sender, IMessageActions? actions = null, PrivateState? state = null,
        InterruptedSends? interrupted = null, TimeProvider? clock = null, IUiDispatcher? dispatcher = null, Action<string>? log = null,
        Func<string, Task<string?>>? scopeOf = null, IScheduledSendHostLink? hostLink = null)
    {
        link = hostLink;
        engine = new ScheduledSendEngine(state is null ? null : new Store(state), new Outlet(outbox, sender, actions, interrupted),
            new Conditions(sender, scopeOf), hostLink is null ? ScheduledSendCustody.Local : new HostLinkCustody(hostLink), clock, dispatcher, log);
        // Raised as this object, as it was before the rules moved into the engine.
        engine.Changed += (_, chatId) => Changed?.Invoke(this, chatId);
        if (hostLink is not null)
        {
            hosted = new HostedScheduledSends(hostLink, clock, log);
            hosted.Changed += (_, chatId) => Changed?.Invoke(this, chatId);
            hostLink.HostChanged += (_, _) => _ = SettleModeQuietAsync();
        }
    }

    // Decided from the link: the server's schedule when it fires, this client's own engine otherwise (older host, or a host not heard yet, whose custody is false
    // and which therefore only reads). Moving to the server's draws its rows; leaving it lets the engine's alarm look at its own.
    private async Task SettleModeAsync()
    {
        if (link is null || hosted is null) return;
        var fires = await link.HostFiresAsync().ConfigureAwait(false);
        var was = useHost;
        useHost = fires == true;
        if (useHost) await hosted.LoadAsync().ConfigureAwait(false);
        if (was != useHost) Changed?.Invoke(this, "");
        if (fires == false) _ = engine.RunDueAsync();
    }

    private async Task SettleModeQuietAsync()
    {
        try { await SettleModeAsync().ConfigureAwait(false); }
        catch (Exception) { /* the next change of the link, or the next call, decides again */ }
    }

    public IReadOnlyList<ScheduledSend> ForChat(string chatId) => useHost ? [.. hosted!.ForChat(chatId), .. Leftovers().Where(item => item.ChatId == chatId)] : engine.ForChat(chatId);
    public IReadOnlyList<ScheduledSend> All => useHost ? [.. hosted!.All, .. Leftovers()] : engine.All;
    public ScheduledSend? Find(string id) => useHost ? hosted!.Find(id) ?? Leftovers().FirstOrDefault(item => item.Id == id) : engine.Find(id);

    // What this PC kept for an older host, once the host is one that fires for every client (stage 6 slice 11): the PC's engine has no custody then and fires none of it, and a
    // message that went dark would be a message lost. It is drawn as not sent (moved) with Send now, Edit and Cancel, none of which fires anything by itself: Send now makes it on
    // the server as a new operation (HostedScheduledSends.SendMovedAsync), Edit and Cancel are this PC's own, as they were. One found sending is the engine's to settle, and not drawn.
    private IEnumerable<ScheduledSend> Leftovers() => engine.All.Where(item => item.State != ScheduledSendState.Sending)
        .Select(item => item with { State = ScheduledSendState.NotSent, Reason = ScheduledSendReason.Moved });
    private bool IsLeftover(string id) => useHost && hosted!.Find(id) is null && engine.Find(id) is { State: not ScheduledSendState.Sending };
    // The latest pass; the offline checks await it after moving the clock.
    public Task Idle => engine.Idle;

    /// <inheritdoc cref="ScheduledSendEngine.LoadAsync"/>
    public Task LoadAsync() => link is null ? engine.LoadAsync() : LoadWithHostAsync();
    private async Task LoadWithHostAsync()
    {
        await engine.LoadAsync().ConfigureAwait(false);
        await SettleModeAsync().ConfigureAwait(false);
    }
    /// <inheritdoc cref="ScheduledSendEngine.ScheduleAsync"/>
    public Task<ScheduledSend> ScheduleAsync(string chatId, string text, DateTimeOffset due, string? replyToId = null, string? replyPreview = null) =>
        link is null ? engine.ScheduleAsync(chatId, text, due, replyToId, replyPreview) : ScheduleWithHostAsync(chatId, text, due, replyToId, replyPreview);
    private async Task<ScheduledSend> ScheduleWithHostAsync(string chatId, string text, DateTimeOffset due, string? replyToId, string? replyPreview)
    {
        await SettleModeAsync().ConfigureAwait(false);
        if (useHost) return await hosted!.ScheduleAsync(chatId, text, due, replyToId, replyPreview).ConfigureAwait(false);
        // No host heard yet: the host that may hold the schedule is the one thing this must not guess at, so nothing is scheduled until one is (an older host,
        // which does not fire, lets the engine keep it as before).
        if (await link!.HostFiresAsync().ConfigureAwait(false) is null)
            throw new InvalidOperationException("Messages isn't connected to its server yet. Try again in a moment.");
        return await engine.ScheduleAsync(chatId, text, due, replyToId, replyPreview).ConfigureAwait(false);
    }
    /// <inheritdoc cref="ScheduledSendEngine.CancelAsync"/>
    public Task<bool> CancelAsync(string id) => useHost && !IsLeftover(id) ? hosted!.CancelAsync(id) : engine.CancelAsync(id);
    /// <inheritdoc cref="ScheduledSendEngine.TakeForEditAsync"/>
    public Task<ScheduledSend?> TakeForEditAsync(string id) => useHost && !IsLeftover(id) ? hosted!.TakeForEditAsync(id) : engine.TakeForEditAsync(id);
    /// <inheritdoc cref="ScheduledSendEngine.SendNowAsync"/>
    public Task<bool> SendNowAsync(string id) =>
        !useHost ? engine.SendNowAsync(id) : IsLeftover(id) ? hosted!.SendMovedAsync(engine.Find(id)!, () => engine.CancelAsync(id)) : hosted!.SendNowAsync(id);
    /// <inheritdoc cref="ScheduledSendEngine.ForgetAsync"/>
    public Task<int> ForgetAsync(Func<string, bool> chat) => useHost ? ForgetBothAsync(chat) : engine.ForgetAsync(chat);
    private async Task<int> ForgetBothAsync(Func<string, bool> chat) => await hosted!.ForgetAsync(chat).ConfigureAwait(false) + await engine.ForgetAsync(chat).ConfigureAwait(false);
    public bool IsSendingIn(Func<string, bool> chat) => useHost ? hosted!.IsSendingIn(chat) : engine.IsSendingIn(chat);
    /// <inheritdoc cref="ScheduledSendEngine.StartAsync"/>
    public Task StartAsync() => link is null ? engine.StartAsync() : LoadWithHostAsync();
    /// <inheritdoc cref="ScheduledSendEngine.Stop"/>
    public void Stop() => engine.Stop();
    /// <inheritdoc cref="ScheduledSendEngine.RunDueAsync"/>
    public Task RunDueAsync() => engine.RunDueAsync();

    // A one-way tag of the message and of its conversation, so a live run can be followed without naming anything.
    public static string Tag(string value) => ScheduledSendEngine.Tag(value);

    public void Dispose() => engine.Dispose();

    // This PC's protected store: one list per profile under scheduled:all, removed when empty.
    private sealed class Store(PrivateState state) : IScheduledSendStore
    {
        public Task<string?> ProfileAsync() => state.ProfileAsync();
        public Task<ScheduledSend[]?> ReadAsync(string profile) => state.ReadAsync<ScheduledSend[]>(profile, Category, Key);
        public Task WriteAsync(string profile, ScheduledSend[] items) =>
            items.Length == 0 ? state.RemoveAsync(profile, Category, Key) : state.WriteAsync(profile, Category, Key, items);
    }

    // The outbox every composer uses, under the scheduled message's own id, so its row stays the same row as it starts
    // sending and a send begun before a crash is known by that id.
    private sealed class Outlet(Outbox outbox, IMessageSender sender, IMessageActions? actions, InterruptedSends? interrupted) : IScheduledSendOutlet
    {
        public Task SendAsync(ScheduledSend item)
        {
            var chatId = item.ChatId; var text = item.Text;
            // No Undo send wait: the time was chosen, and the message has waited already.
            return item.ReplyToId is { } replyTo && actions is not null
                ? outbox.SendOperationAsync(chatId, text, ct => actions.ReplyAsync(chatId, replyTo, text, ct), true, replyTo, localId: item.Id)
                : outbox.SendOperationAsync(chatId, text, ct => sender.SendTextAsync(chatId, text, ct), true, localId: item.Id);
        }
        public OutgoingState? StateOf(string id) => outbox.Find(id)?.State;
        // Added and removed on the outbox itself, so the engine is called in the order it subscribed, as before.
        public event EventHandler<string>? Changed { add => outbox.ItemChanged += value; remove => outbox.ItemChanged -= value; }
        public Task CarryInterruptedAsync(InterruptedSend send) => interrupted is null ? Task.CompletedTask : interrupted.CarryAsync(send);
    }

    // What the composer asks before sending. scopeOf is null where no conversation needs its account checked.
    private sealed class Conditions(IMessageSender sender, Func<string, Task<string?>>? scopeOf) : IScheduledSendConditions
    {
        public bool TracksAccounts => scopeOf is not null;
        public Task<string?> AccountOfAsync(string chatId) => scopeOf!(chatId);
        public bool IsConnected(string chatId) => sender is not IProviderConnectionState connections || connections.IsConnected(chatId);
        public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken) =>
            sender.GetSendPermissionAsync(chatId, cancellationToken);
    }
}
