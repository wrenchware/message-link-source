using CommunityToolkit.Mvvm.ComponentModel;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// Events invalidate REST snapshots. There is no durable server replay cursor.
public enum SyncState { Disconnected, Connecting, Waiting, Updating, Live, Interrupted, AuthenticationRequired, PartiallyConnected }

/// <summary>
/// One realtime batch the list has just applied, in order: Full when it was applied as a full resync, and the loaded
/// summary of every conversation the events name, as the list holds it now.
/// </summary>
public sealed record AppliedBatch(IReadOnlyList<RealtimeEvent> Events, bool Full,
    IReadOnlyDictionary<string, ChatSummary> Chats);

/// <summary>
/// The window's side of the realtime feed: <see cref="FeedBatcher"/> applies it to the list, and this view model adds the open conversations, the
/// composers' Live flag, the typing-widened batch window and the status line and warning.
/// </summary>
public sealed partial class SyncViewModel(IRealtimeFeed feed, IMessageSender sender, IUiDispatcher dispatcher,
    ChatListViewModel chats, ConversationViewModel history, ComposerViewModel composer, Outbox outbox,
    ComposerActivity? typing = null) : ObservableObject, IDisposable
{
    // How long a batch is gathered before it is applied. Applying is interface-thread work, and every provider's
    // events arrive here, so this is the one place that can stand out of the way of somebody typing. The ordinary
    // window only coalesces a burst; the typing window trades a little freshness for the composer, which is the
    // thing being watched. Google Messages already yielded its background indexing this way, but that was asked
    // only inside the Google backend, so Messenger and Google Voice bursts arrived regardless.
    private static readonly TimeSpan TypingBatchWindow = TimeSpan.FromMilliseconds(500),
        // How long after the last keystroke the wider window still applies. Longer than the gap between keystrokes at
        // any ordinary speed, so a burst is not re-narrowed mid-sentence, and short enough that stopping restores the
        // ordinary freshness almost at once. A batch is never held beyond TypingBatchWindow whatever happens here.
        TypingQuietPeriod = TimeSpan.FromMilliseconds(900);
    // How often a conversation that was busy when its events arrived is offered them again. Each offer only asks
    // whether it is still busy, so this costs nothing; a linked conversation waiting on an unreachable service stays
    // busy for as long as that service takes to give up.
    public TimeSpan HistoryRetry { get; init; } = TimeSpan.FromMilliseconds(500);
    // How long a full resync waits after a connection, and the first wait after a failed one (it then doubles, to 30 s).
    // The offline checks shorten both: every one of them connects at least once and otherwise sat through a second each,
    // and the ones that watch a busy conversation see more passes in less time.
    public TimeSpan ResyncDelay { get; init; } = TimeSpan.FromSeconds(1);
    // How much a single apply has to allocate before it is worth a line. Ordinary ones are a few hundred kilobytes.
    private const long SlowApplyKb = 4096;
    // Reported rather than logged: this view model is shared presentation and knows nothing about where a log lives.
    public event EventHandler<string>? Allocated;
    // Every batch once, at the moment it has been applied to the list and before any open conversation re-reads, so
    // ConversationCache still holds the copies from before it. Raised on the interface thread; a handler must be quick.
    // A batch held back for a busy list is raised when it is finally applied, never on the pass that held it.
    public event EventHandler<AppliedBatch>? BatchApplied { add => Batcher.BatchApplied += value; remove => Batcher.BatchApplied -= value; }

    // Made on first use: it is handed this view model's open conversations, which a field initializer cannot reach.
    private FeedBatcher? batcher;
    private OpenConversations? open;
    private FeedBatcher Batcher
    {
        get
        {
            if (batcher is not null) return batcher;
            open = new OpenConversations(this, sender, typing);
            batcher = new FeedBatcher(feed, chats, outbox, dispatcher, sender as IProviderConnectionState, line => Allocated?.Invoke(this, line), open);
            batcher.ListResynced += (_, resync) => Resynced(resync.Began);
            return batcher;
        }
    }
    internal int FeedWaitsEnded => batcher?.WaitsEnded ?? 0;

    // Conversations open in their own windows follow the same connection and events as the main one.
    private readonly List<(ConversationViewModel History, ComposerViewModel Composer)> windows = [];
    private SyncState connectionState;
    private string? syncWarning;
    public SyncState ConnectionState => connectionState;
    private void Report(SyncState state, string message)
    {
        if (state == SyncState.Interrupted) syncWarning = message;
        else if (state is not (SyncState.Waiting or SyncState.Updating)) syncWarning = null;
        SetProperty(ref connectionState, state, nameof(ConnectionState));
        Status = message;
        OnPropertyChanged(nameof(Warning));
    }
    // Accessed on the UI thread. A lifecycle event can sit behind a slow snapshot read, so the notice must
    // verify present readiness even before the matching Connected event reaches this coordinator.
    public string? Warning
    {
        get
        {
            var unavailable = Batcher.Unavailable;
            var missing = unavailable.Keys.Where(Batcher.IsUnavailable).ToArray();
            if (missing.Length > 0)
            {
                if (sender is IProviderConnectionState connections && !connections.AnyConnected)
                    return missing.Any(scope => unavailable[scope] == RealtimeKind.AuthenticationRequired)
                        ? "A service needs you to sign in again: Settings → Connections." : "Disconnected. Settings → Connections shows which service.";
                return PartialWarning(missing);
            }
            if (connectionState is SyncState.Disconnected or SyncState.AuthenticationRequired &&
                (sender is not IProviderConnectionState available || !available.AnyConnected)) return Status;
            return syncWarning;
        }
    }
    // An event with no scope is the stream's own, and always counts.
    private bool IsFromConfigured(RealtimeEvent change) => change.Scope is not { } scope ||
        sender is not IProviderConnectionState connections || connections.IsConfigured(scope);
    // Said once a run, by kind only: the realtime feed should leave such a service out altogether, so one getting here at
    // all is worth seeing in the log.
    private bool unconfiguredNoted;
    private void NoteUnconfigured(IReadOnlyList<RealtimeEvent> batch)
    {
        if (unconfiguredNoted) return;
        unconfiguredNoted = true;
        Allocated?.Invoke(this, $"sync ignored reason=not_set_up events={batch.Count} auth={batch.Count(e => e.Kind == RealtimeKind.AuthenticationRequired)}");
    }

    /// <summary>
    /// Whether a service (by its connection scope) has stopped and waits on the user: it said authorization is needed — a
    /// session expired, a checkpoint, a pairing or sign-in Google no longer accepts — and has not connected since. A
    /// connection that only dropped is not this, nor is the helper stopping after such a refusal the end of it. On the
    /// interface thread, where the batches are applied.
    /// </summary>
    public bool NeedsSignIn(string scope) => Batcher.NeedsSignIn(scope);
    // Google Messages that needs pairing again says so: "unavailable" read as something to wait out, and a pairing the
    // phone had removed was waited on for three quarters of an hour. Other services keep the general notice; Google Voice
    // in particular recovers its own sign-in, so telling someone to sign in again there would be premature. A Google
    // Messages sign-in Google stopped accepting, with the phone still paired, is renewed rather than paired again, and on
    // 2026-09-24 "needs pairing again" sent the user towards a new pairing the phone did not need.
    private string PartialWarning(IEnumerable<string> scopes)
    {
        var all = scopes.ToArray();
        var unavailable = Batcher.Unavailable;
        if (all.FirstOrDefault(scope => IsGoogleMessages(scope) && unavailable.GetValueOrDefault(scope) == RealtimeKind.AuthenticationRequired) is { } google)
            return sender is IProviderConnectionState connections && connections.NeedsSignInOnly(google)
                ? "Google Messages needs its sign-in renewed. Sign in again on your Message Link Server's page."
                : "Google Messages needs pairing again. Sign in again on your Message Link Server's page.";
        return string.Join(", ", all.Select(ServiceScopes.Name).Distinct()) + " is unavailable. Your other services are still working; Settings → Connections shows more.";
    }
    private static bool IsGoogleMessages(string scope) => ServiceScopes.Name(scope) == "Google Messages";
    private void ReportLive()
    {
        Batcher.ForgetConnected();
        if (Batcher.Unavailable.Count == 0) Report(SyncState.Live, "Live");
        else Report(SyncState.PartiallyConnected, PartialWarning(Batcher.Unavailable.Keys));
    }
    [ObservableProperty] private string status = "Not connected";
    public void Attach(ConversationViewModel other, ComposerViewModel otherComposer)
    {
        windows.Add((other, otherComposer));
        otherComposer.IsLive = sender is IProviderConnectionState connections
            ? otherComposer.ChatId is { } id && connections.IsConnected(id) : composer.IsLive;
    }
    public void Detach(ConversationViewModel other) => windows.RemoveAll(window => ReferenceEquals(window.History, other));
    private (ConversationViewModel History, ComposerViewModel Composer)[] Conversations() => [(history, composer), .. windows];
    private void SetLive(bool value)
    {
        foreach (var (_, open) in Conversations())
            open.IsLive = value && (sender is not IProviderConnectionState connections || open.ChatId is { } id && connections.IsConnected(id));
    }
    public void Start()
    {
        var feedBatcher = Batcher;
        if (feedBatcher.IsRunning) return;
        unconfiguredNoted = false;
        open!.Reset();
        Report(SyncState.Connecting, "Connecting to services…");
        feedBatcher.ResyncDelay = ResyncDelay;
        feedBatcher.Start();
    }
    public async Task StopAsync()
    {
        if (batcher is not null) await batcher.StopAsync();
        SetLive(false); Report(SyncState.Disconnected, "Not connected");
    }
    // Whether this conversation took the event into its messages: it names the conversation (or, merged, a member), and the
    // conversation's own service is connected, as the pass gives it events only then.
    private bool AppliesTo(ConversationViewModel open, RealtimeEvent change) =>
        (open.ContainsChat(change.ChatId) || change.Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted && change.Ids?.Any(open.ContainsChat) == true) &&
        (open.IsMerged || sender is not IProviderConnectionState available || open.ActiveChatId is not { } target || available.IsConnected(target));
    public void Dispose() => batcher?.Dispose();

    /// <summary>What the window adds to each pass of the feed: its open conversations, the composers and the status line.</summary>
    private sealed class OpenConversations(SyncViewModel sync, IMessageSender sender, ComposerActivity? typing) : IFeedConversations
    {
        // Events the list has applied but an open conversation has not, because it was busy loading when they arrived,
        // and whether what it missed was a full resync. They wait for the conversation alone. Asking for a full resync
        // instead re-read every conversation from every provider once a second for as long as the conversation stayed
        // busy: a linked conversation waiting on a service that could not connect did that for minutes on end.
        private List<RealtimeEvent> historyCarried = [];
        private bool historyResync;
        private DateTimeOffset? historyWaitingSince;
        // Conversations that took what was held on a pass that then stopped at a busy one, until a pass completes.
        private readonly HashSet<ConversationViewModel> caughtUp = new(ReferenceEqualityComparer.Instance);

        private IMessageSender Sender => sender;

        public void Reset() { historyCarried = []; historyResync = false; historyWaitingSince = null; caughtUp.Clear(); }

        public TimeSpan BatchWindow(TimeSpan ordinary) =>
            typing is not null && typing.RecentlyActive(TypingQuietPeriod) ? TypingBatchWindow : ordinary;

        public TimeSpan? HoldRetry => historyCarried.Count > 0 || historyResync ? sync.HistoryRetry : null;

        // A pinned route in an open merged conversation follows at once (MergedConversations.NeedsSignIn).
        public void SignInChanged()
        {
            foreach (var (open, _) in sync.Conversations())
                if (open.IsMerged) open.RefreshAutomaticSendRoute();
        }

        public void Offline(IReadOnlyList<RealtimeEvent> batch)
        {
            sync.SetLive(false);
            // Only what a service set up here says counts. One that never was (Google Messages never paired,
            // above all) is not disconnected and needs no authorization: it is not there. A first run said "A
            // service needs authorization" for it (2026-10-02 clean-PC run). A batch of nothing but such
            // events leaves the state as it was.
            var counted = batch.Where(sync.IsFromConfigured).ToArray();
            if (batch.Count == 0 || counted.Length > 0)
            {
                var auth = counted.Any(e => e.Kind == RealtimeKind.AuthenticationRequired);
                sync.Report(auth ? SyncState.AuthenticationRequired : SyncState.Disconnected,
                    auth ? "A service needs you to sign in again: Settings → Connections." : "Disconnected. Settings → Connections shows which service.");
            }
            else sync.NoteUnconfigured(batch);
        }

        public bool TakePass(bool listBusy)
        {
            // A live connection is enough to send. Sends are tracked by the outbox and confirmed by their echo, so
            // the composer no longer waits for the full resync, which at launch waits behind the initial load.
            sync.SetLive(true);
            // Nothing has been applied yet, so the batch is still whole and worth keeping. The
            // conversation-busy check in ApplyAsync is different: the list has already been applied by then,
            // so the state is half moved and the conversation holds what it missed.
            //
            // A merged conversation waits on the carry path instead. Its id belongs to no service, so the
            // router answered for it with Google's connection: while Google was connected, opening one held
            // every service's list updates for as long as it read its members and looked for an earlier route.
            if (listBusy || sync.Conversations().Any(open => open.History.IsBusy && !open.History.IsMerged &&
                    (Sender is not IProviderConnectionState available || open.History.ActiveChatId is not { } id || available.IsConnected(id))))
            {
                sync.Report(SyncState.Waiting, "Waiting to synchronize…");
                return false;
            }
            return true;
        }

        public async Task<bool> ApplyAsync(FeedPass pass, CancellationToken token)
        {
            var batch = pass.Events;
            // Counted and timed around everything, including the paths that give up part way. Recording the
            // duration only where the apply succeeded meant a batch that bailed on a busy conversation, or
            // threw, spent its time on this thread and reported none of it.
            UiActivity.SyncApply();
            var applying = UiActivity.Start();
            try
            {
                sync.Report(SyncState.Updating, pass.Full ? "Synchronizing…" : "Updating…");
                // A two-second window holding one synchronization allocates thirty megabytes while sync_ms
                // reads 32 and every other counter reads zero — so the allocation is not the conversation
                // render, the list rebuild or the avatars, and nothing that exists says what it is. These
                // four say. Measured per thread, which is this one: every await here resumes on it.
                var began = GC.GetAllocatedBytesForCurrentThread();
                long Since() { var now = GC.GetAllocatedBytesForCurrentThread(); var grew = now - began; began = now; return grew / 1024; }
                await pass.ListAsync(token);
                var listWaited = pass.Waited(batch);
                var listKb = Since();
                // The list is now current. What the conversations get is this batch plus anything one of them
                // was too busy to take last time.
                IReadOnlyList<RealtimeEvent> history = historyCarried.Count == 0 ? batch : [.. historyCarried, .. batch];
                var historyFull = pass.Full || historyResync;
                var passResync = pass.Full;
                // A window opened or closed meanwhile is picked up by the next batch; one closed is forgotten.
                pass.Stage = "conversations";
                var conversations = sync.Conversations();
                caughtUp.IntersectWith(conversations.Select(window => window.History));
                foreach (var (open, _) in conversations)
                {
                    // Its open conversation waits for its service, but what it keeps closed takes the batch, once
                    // per batch like the others (caughtUp), so a carried resync is not taken again every pass.
                    if (!open.IsMerged && Sender is IProviderConnectionState available && open.ActiveChatId is { } target && !available.IsConnected(target))
                    {
                        if (!caughtUp.Contains(open)) { await open.KeepClosedCurrentAsync(Sender, history, historyFull, token); caughtUp.Add(open); }
                        else if (batch.Count > 0 || passResync) await open.KeepClosedCurrentAsync(Sender, batch, passResync, token);
                        continue;
                    }
                    if (open.IsBusy)
                    {
                        // One caught up earlier that is busy again takes everything held, like any busy one.
                        caughtUp.Remove(open);
                        // Held for the conversation, not re-read for everyone. More than one pass can carry
                        // becomes a resync of the conversation, which is what the list-wide one did for it.
                        if (history.Count >= 128) { historyCarried = []; historyResync = true; }
                        else { historyCarried = [.. history]; historyResync = historyFull; }
                        if (historyWaitingSince is null)
                        {
                            historyWaitingSince = DateTimeOffset.UtcNow;
                            sync.Allocated?.Invoke(sync, $"sync conversation busy held={historyCarried.Count} resync={historyResync}");
                        }
                        sync.Report(SyncState.Waiting, "Waiting to synchronize…");
                        return false;
                    }
                    // Already given what was held, on an earlier pass that then stopped at a busy conversation
                    // further on: only this pass's own events are new to it. Taking the held ones again every
                    // HistoryRetry re-read a carried resync, an Instagram request each time, for nothing.
                    if (caughtUp.Contains(open))
                    {
                        if (batch.Count > 0 || passResync) await open.ApplyEventsAsync(Sender, batch, passResync, token);
                        continue;
                    }
                    await open.ApplyEventsAsync(Sender, history, historyFull, token);
                    caughtUp.Add(open);
                }
                caughtUp.Clear();
                // Arrival to applied, once per pass that got this far: the list's share (this batch) and the open
                // conversations' (this batch plus what they were too busy to take earlier). A pass that stopped at a
                // busy conversation says nothing, and its events are counted by the pass that finishes them.
                var openWaited = pass.Waited(history.Where(change => conversations.Any(window => sync.AppliesTo(window.History, change))));
                if (listWaited.Count > 0 || openWaited.Count > 0)
                    sync.Allocated?.Invoke(sync, $"sync event_applied list={listWaited.Count} list_avg_ms={listWaited.AvgMs} list_worst_ms={listWaited.WorstMs} " +
                        $"open={openWaited.Count} open_avg_ms={openWaited.AvgMs} open_worst_ms={openWaited.WorstMs}");
                if (historyWaitingSince is { } since)
                {
                    sync.Allocated?.Invoke(sync, $"sync conversation caught up held={history.Count - batch.Count} resync={historyFull} waited_ms={(long)(DateTimeOffset.UtcNow - since).TotalMilliseconds}");
                    historyWaitingSince = null;
                }
                historyCarried = []; historyResync = false;
                var historyKb = Since();
                pass.Stage = "outbox";
                await pass.OutboxAsync(history, historyFull, token);
                var outboxKb = Since();
                pass.Stage = "permission";
                foreach (var (_, open) in sync.Conversations())
                    if (open.ChatId is { } id && (Sender is not IProviderConnectionState available || available.IsConnected(id)) && (historyFull && history.Count == 0 || history.Any(change => change.Affects(id))))
                        await open.RefreshPermissionAsync();
                var permissionKb = Since();
                if (listKb + historyKb + outboxKb + permissionKb >= SlowApplyKb)
                    sync.Allocated?.Invoke(sync, $"sync alloc list_kb={listKb} history_kb={historyKb} outbox_kb={outboxKb} permission_kb={permissionKb} events={history.Count} resync={historyFull} reason={pass.Reason}");
                token.ThrowIfCancellationRequested();
                pass.Stage = "live";
                sync.SetLive(true); sync.ReportLive();
                return true;
            }
            finally { UiActivity.Sync(applying); }
        }

        // A failed resync doesn't block sending while still connected; a disconnect event turns it off.
        public void Failed(Exception failure) => sync.Report(SyncState.Interrupted, "Connection interrupted · Retrying…");
    }
}
