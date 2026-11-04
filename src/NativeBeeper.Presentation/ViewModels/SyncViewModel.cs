using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// Events invalidate REST snapshots. There is no durable server replay cursor.
public enum SyncState { Disconnected, Connecting, Waiting, Updating, Live, Interrupted, AuthenticationRequired, PartiallyConnected }

/// <summary>
/// One realtime batch the list has just applied, in order: Full when it was applied as a full resync, and the loaded
/// summary of every conversation the events name, as the list holds it now.
/// </summary>
public sealed record AppliedBatch(IReadOnlyList<RealtimeEvent> Events, bool Full,
    IReadOnlyDictionary<string, ChatSummary> Chats);

public sealed partial class SyncViewModel(IRealtimeFeed feed, IMessageSender sender, IUiDispatcher dispatcher,
    ChatListViewModel chats, ConversationViewModel history, ComposerViewModel composer, Outbox outbox,
    ComposerActivity? typing = null) : ObservableObject, IDisposable
{
    // How long a batch is gathered before it is applied. Applying is interface-thread work, and every provider's
    // events arrive here, so this is the one place that can stand out of the way of somebody typing. The ordinary
    // window only coalesces a burst; the typing window trades a little freshness for the composer, which is the
    // thing being watched. Google Messages already yielded its background indexing this way, but that was asked
    // only inside the Google backend, so Messenger and Google Voice bursts arrived regardless.
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(150),
        TypingBatchWindow = TimeSpan.FromMilliseconds(500),
        // How long after the last keystroke the wider window still applies. Longer than the gap between keystrokes at
        // any ordinary speed, so a burst is not re-narrowed mid-sentence, and short enough that stopping restores the
        // ordinary freshness almost at once. A batch is never held beyond TypingBatchWindow whatever happens here.
        TypingQuietPeriod = TimeSpan.FromMilliseconds(900);
    // How soon a batch held back for a busy interface is offered again. Short: it is already late.
    private static readonly TimeSpan DeferredRetry = TimeSpan.FromMilliseconds(120);
    // How often a conversation that was busy when its events arrived is offered them again. Each offer only asks
    // whether it is still busy, so this costs nothing; a linked conversation waiting on an unreachable service stays
    // busy for as long as that service takes to give up.
    public TimeSpan HistoryRetry { get; init; } = TimeSpan.FromMilliseconds(500);
    // How long a full resync waits after a connection, and the first wait after a failed one (it then doubles, to 30 s).
    // The offline checks shorten both: every one of them connects at least once and otherwise sat through a second each,
    // and the ones that watch a busy conversation see more passes in less time.
    public TimeSpan ResyncDelay { get; init; } = TimeSpan.FromSeconds(1);
    private TimeSpan BatchDelay() =>
        typing is not null && typing.RecentlyActive(TypingQuietPeriod) ? TypingBatchWindow : BatchWindow;
    // How much a single apply has to allocate before it is worth a line. Ordinary ones are a few hundred kilobytes.
    private const long SlowApplyKb = 4096;
    // Reported rather than logged: this view model is shared presentation and knows nothing about where a log lives.
    public event EventHandler<string>? Allocated;
    // Every batch once, at the moment it has been applied to the list and before any open conversation re-reads, so
    // ConversationCache still holds the copies from before it. Raised on the interface thread; a handler must be quick.
    // A batch held back for a busy list is raised when it is finally applied, never on the pass that held it.
    public event EventHandler<AppliedBatch>? BatchApplied;
    private void Tap(IReadOnlyList<RealtimeEvent> events, bool full, bool withChats)
    {
        if (events.Count == 0 || BatchApplied is not { } tap) return;
        try
        {
            var summaries = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
            if (withChats)
            {
                var named = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in events)
                {
                    if (change.ChatId is { } id) named.Add(id);
                    if (change.Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted && change.Ids is { } ids) named.UnionWith(ids);
                }
                if (named.Count > 0)
                    foreach (var chat in chats.AllLoadedChats)
                        if (named.Contains(chat.Id)) summaries[chat.Id] = chat;
            }
            tap(this, new AppliedBatch(events.ToArray(), full, summaries));
        }
        // Whatever a listener does wrong, the sync pass goes on. The type alone: an exception's message may name anything.
        catch (Exception failure) { Allocated?.Invoke(this, $"activity tap failed error={failure.GetType().Name}"); }
    }

    // When each realtime event reached this coordinator, as a monotonic stamp, for the "sync event_applied" line. Not
    // RealtimeEvent.ReceivedAt: that is wall-clock, set by only some backends, and the copy NotificationRealtimeFeed
    // stamps does not travel on to here. By reference, so a stamp goes with its event and needs no cleaning up.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RealtimeEvent, System.Runtime.CompilerServices.StrongBox<long>> arrivals = new();
    // Live changes only: a history snapshot is old state refreshing the baseline, and connection events apply nothing.
    private static bool IsMeasured(RealtimeEvent change) => !change.IsHistory &&
        change.Kind is RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted or RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted;
    // How long the measured events among these have waited since they arrived: how many, the mean and the worst.
    private (int Count, long AvgMs, long WorstMs) Waited(IEnumerable<RealtimeEvent> events)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        int count = 0; long total = 0, worst = 0;
        foreach (var change in events)
        {
            if (!arrivals.TryGetValue(change, out var arrived)) continue;
            var ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(arrived.Value, now).TotalMilliseconds;
            count++; total += ms; worst = Math.Max(worst, ms);
        }
        return (count, count == 0 ? 0 : total / count, worst);
    }

    private CancellationTokenSource? session;
    private Task? running;
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
            var missing = unavailable.Keys.Where(IsUnavailable).ToArray();
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
    private readonly Dictionary<string, RealtimeKind> unavailable = new(StringComparer.Ordinal);
    private bool IsUnavailable(string scope) => sender is not IProviderConnectionState connections ||
        connections.IsConfigured(scope) && !connections.IsScopeConnected(scope);
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
    public bool NeedsSignIn(string scope) => signInWaiting.Contains(scope) && IsUnavailable(scope);
    private readonly HashSet<string> signInWaiting = new(StringComparer.Ordinal);
    private string[] WaitingOnSignIn() => [.. signInWaiting.Order(StringComparer.Ordinal)];
    // Google Messages that needs pairing again says so: "unavailable" read as something to wait out, and a pairing the
    // phone had removed was waited on for three quarters of an hour. Other services keep the general notice; Google Voice
    // in particular recovers its own sign-in, so telling someone to sign in again there would be premature. A Google
    // Messages sign-in Google stopped accepting, with the phone still paired, is renewed rather than paired again, and on
    // 2026-09-24 "needs pairing again" sent the user towards a new pairing the phone did not need.
    private string PartialWarning(IEnumerable<string> scopes)
    {
        var all = scopes.ToArray();
        if (all.FirstOrDefault(scope => IsGoogleMessages(scope) && unavailable.GetValueOrDefault(scope) == RealtimeKind.AuthenticationRequired) is { } google)
            return sender is IProviderConnectionState connections && connections.NeedsSignInOnly(google)
                ? "Google Messages needs its sign-in renewed. Open Settings and choose Renew sign-in."
                : "Google Messages needs pairing again. Open Settings and choose Pair again.";
        return string.Join(", ", all.Select(ServiceScopes.Name).Distinct()) + " is unavailable. Your other services are still working; Settings → Connections shows more.";
    }
    private static bool IsGoogleMessages(string scope) => ServiceScopes.Name(scope) == "Google Messages";
    private void ReportLive()
    {
        foreach (var scope in unavailable.Keys.Where(scope => !IsUnavailable(scope)).ToArray()) unavailable.Remove(scope);
        if (unavailable.Count == 0) Report(SyncState.Live, "Live");
        else Report(SyncState.PartiallyConnected, PartialWarning(unavailable.Keys));
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
        if (running is { IsCompleted: false }) return;
        session?.Dispose(); session = new();
        unavailable.Clear(); unconfiguredNoted = false;
        Report(SyncState.Connecting, "Connecting to services…");
        var token = session.Token;
        running = Task.Run(() => RunAsync(token));
    }
    public async Task StopAsync()
    {
        session?.Cancel();
        if (running is not null) await running;
        SetLive(false); Report(SyncState.Disconnected, "Not connected");
    }
    private async Task RunAsync(CancellationToken token)
    {
        using var loop = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = loop.Token;
        // Provider events and confirmed automatic unarchives can arrive from separate workers.
        var channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var producer = ProduceAsync(channel, token);
        var online = false;
        var needsResync = false;
        // Why the next apply is a full one. A full apply re-reads every conversation from every provider, which is
        // where a synchronization's whole allocation turned out to live, so the reason it was asked for is the next
        // thing worth knowing: four providers each announcing their own connection would mean four of them.
        var resyncReason = "none";
        var retryDelay = (int)ResyncDelay.TotalMilliseconds;
        // Events the list has applied but an open conversation has not, because it was busy loading when they arrived,
        // and whether what it missed was a full resync. They wait for the conversation alone. Asking for a full resync
        // instead re-read every conversation from every provider once a second for as long as the conversation stayed
        // busy: a linked conversation waiting on a service that could not connect did that for minutes on end.
        var historyCarried = new List<RealtimeEvent>();
        var historyResync = false;
        DateTimeOffset? historyWaitingSince = null;
        // Conversations that took what was held on a pass that then stopped at a busy one, until a pass completes.
        var caughtUp = new HashSet<ConversationViewModel>(ReferenceEqualityComparer.Instance);
        try
        {
            // Events held back because something was busy when they arrived. They are kept rather than dropped: the
            // only way to recover a dropped batch is a full resync, and a full resync re-reads every conversation
            // from every provider — eighteen megabytes, measured, to recover a handful of events that were still in
            // hand. Both full resyncs in the measured session were this, and neither needed to be.
            var carried = new List<RealtimeEvent>();
            while (!token.IsCancellationRequested)
            {
                var historyWaiting = historyCarried.Count > 0 || historyResync;
                if (carried.Count == 0 && !needsResync && !historyWaiting && !await channel.Reader.WaitToReadAsync(token)) break;
                await Task.Delay(needsResync ? TimeSpan.FromMilliseconds(retryDelay)
                    : carried.Count > 0 ? DeferredRetry : historyWaiting && !channel.Reader.TryPeek(out _) ? HistoryRetry : BatchDelay(), token);
                var batch = carried;
                carried = [];
                while (batch.Count < 128 && channel.Reader.TryRead(out var change)) batch.Add(change);
                foreach (var change in batch)
                {
                    if (change.Kind == RealtimeKind.Connected) { online = true; needsResync = true; resyncReason = "connected"; }
                    if (change.Kind is RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired) online = false;
                    if (change.Kind == RealtimeKind.ResyncRequired) { needsResync = true; resyncReason = "required"; }
                }
                var deferred = false;
                await dispatcher.InvokeAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    var waitingBefore = WaitingOnSignIn();
                    foreach (var change in batch)
                    {
                        if (change.Scope is not { } scope) continue;
                        if (change.Kind == RealtimeKind.Connected && (sender is not IProviderConnectionState || !IsUnavailable(scope))) { unavailable.Remove(scope); signInWaiting.Remove(scope); }
                        else if (change.Kind is RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired) unavailable[scope] = change.Kind;
                        if (change.Kind == RealtimeKind.AuthenticationRequired) signInWaiting.Add(scope);
                    }
                    // A pinned route in an open merged conversation follows at once (MergedConversations.NeedsSignIn).
                    if (!waitingBefore.SequenceEqual(WaitingOnSignIn()))
                        foreach (var (open, _) in Conversations())
                            if (open.IsMerged) open.RefreshAutomaticSendRoute();
                    if (sender is IProviderConnectionState connections) online = connections.AnyConnected;
                    if (!online)
                    {
                        SetLive(false);
                        // Only what a service set up here says counts. One that never was (Google Messages never paired,
                        // above all) is not disconnected and needs no authorization: it is not there. A first run said "A
                        // service needs authorization" for it (2026-10-02 clean-PC run). A batch of nothing but such
                        // events leaves the state as it was.
                        var counted = batch.Where(IsFromConfigured).ToArray();
                        if (batch.Count == 0 || counted.Length > 0)
                        {
                            var auth = counted.Any(e => e.Kind == RealtimeKind.AuthenticationRequired);
                            Report(auth ? SyncState.AuthenticationRequired : SyncState.Disconnected,
                                auth ? "A service needs you to sign in again: Settings → Connections." : "Disconnected. Settings → Connections shows which service.");
                        }
                        else NoteUnconfigured(batch);
                        // Still observed while nothing is connected: a disconnect is an event worth knowing about.
                        // A resync request goes too: it moves the stream's since, as it does the notifier's.
                        Tap(batch.Where(e => e.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired
                            or RealtimeKind.ResyncRequired).ToArray(), false, false);
                        needsResync = false; return;
                    }
                    // A live connection is enough to send. Sends are tracked by the outbox and confirmed by their echo, so
                    // the composer no longer waits for the full resync, which at launch waits behind the initial load.
                    SetLive(true);
                    // Nothing has been applied yet, so the batch is still whole and worth keeping. The
                    // conversation-busy check further down is different: the list has already been applied by then,
                    // so the state is half moved and a full resync is the honest way back.
                    //
                    // A merged conversation waits on the carry path below instead. Its id belongs to no service, so the
                    // router answered for it with Google's connection: while Google was connected, opening one held
                    // every service's list updates for as long as it read its members and looked for an earlier route.
                    if (chats.IsBusy || Conversations().Any(open => open.History.IsBusy && !open.History.IsMerged && (sender is not IProviderConnectionState available || open.History.ActiveChatId is not { } id || available.IsConnected(id)))) { deferred = true; Report(SyncState.Waiting, "Waiting to synchronize…"); return; }
                    // Counted and timed around everything, including the paths that give up part way. Recording the
                    // duration only where the apply succeeded meant a batch that bailed on a busy conversation, or
                    // threw, spent its time on this thread and reported none of it.
                    UiActivity.SyncApply();
                    var applying = UiActivity.Start();
                    // Where a pass that throws had got to, for its log line: the 2026-10-14 failure behind a two-second
                    // "Connection interrupted" said nothing about what threw or where.
                    var stage = "list";
                    try
                    {
                        Report(SyncState.Updating, needsResync ? "Synchronizing…" : "Updating…");
                        // A two-second window holding one synchronization allocates thirty megabytes while sync_ms
                        // reads 32 and every other counter reads zero — so the allocation is not the conversation
                        // render, the list rebuild or the avatars, and nothing that exists says what it is. These
                        // four say. Measured per thread, which is this one: every await here resumes on it.
                        var began = GC.GetAllocatedBytesForCurrentThread();
                        long Since() { var now = GC.GetAllocatedBytesForCurrentThread(); var grew = now - began; began = now; return grew / 1024; }
                        // A pass that only offers held events to a conversation has nothing for the list.
                        var full = needsResync;
                        var resyncBegan = ProgressIndicator.SinceLaunch;
                        if (batch.Count > 0 || needsResync) await chats.ApplyEventsAsync(batch, needsResync, token);
                        var listWaited = Waited(batch);
                        // The list is fresh and no open conversation has re-read yet.
                        Tap(batch, full, true);
                        if (full) Resynced(resyncBegan);
                        var listKb = Since();
                        // The list is now current. What the conversations get is this batch plus anything one of them
                        // was too busy to take last time.
                        List<RealtimeEvent> history = historyCarried.Count == 0 ? batch : [.. historyCarried, .. batch];
                        var historyFull = needsResync || historyResync;
                        var passResync = needsResync;
                        var reason = resyncReason;
                        needsResync = false; resyncReason = "none";
                        // A window opened or closed meanwhile is picked up by the next batch; one closed is forgotten.
                        stage = "conversations";
                        var conversations = Conversations();
                        caughtUp.IntersectWith(conversations.Select(window => window.History));
                        foreach (var (open, _) in conversations)
                        {
                            // Its open conversation waits for its service, but what it keeps closed takes the batch, once
                            // per batch like the others (caughtUp), so a carried resync is not taken again every pass.
                            if (!open.IsMerged && sender is IProviderConnectionState available && open.ActiveChatId is { } target && !available.IsConnected(target))
                            {
                                if (!caughtUp.Contains(open)) { await open.KeepClosedCurrentAsync(sender, history, historyFull, token); caughtUp.Add(open); }
                                else if (batch.Count > 0 || passResync) await open.KeepClosedCurrentAsync(sender, batch, passResync, token);
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
                                    Allocated?.Invoke(this, $"sync conversation busy held={historyCarried.Count} resync={historyResync}");
                                }
                                Report(SyncState.Waiting, "Waiting to synchronize…");
                                return;
                            }
                            // Already given what was held, on an earlier pass that then stopped at a busy conversation
                            // further on: only this pass's own events are new to it. Taking the held ones again every
                            // HistoryRetry re-read a carried resync, an Instagram request each time, for nothing.
                            if (caughtUp.Contains(open))
                            {
                                if (batch.Count > 0 || passResync) await open.ApplyEventsAsync(sender, batch, passResync, token);
                                continue;
                            }
                            await open.ApplyEventsAsync(sender, history, historyFull, token);
                            caughtUp.Add(open);
                        }
                        caughtUp.Clear();
                        // Arrival to applied, once per pass that got this far: the list's share (this batch) and the open
                        // conversations' (this batch plus what they were too busy to take earlier). A pass that stopped at a
                        // busy conversation says nothing, and its events are counted by the pass that finishes them.
                        var openWaited = Waited(history.Where(change => conversations.Any(window => AppliesTo(window.History, change))));
                        if (listWaited.Count > 0 || openWaited.Count > 0)
                            Allocated?.Invoke(this, $"sync event_applied list={listWaited.Count} list_avg_ms={listWaited.AvgMs} list_worst_ms={listWaited.WorstMs} " +
                                $"open={openWaited.Count} open_avg_ms={openWaited.AvgMs} open_worst_ms={openWaited.WorstMs}");
                        if (historyWaitingSince is { } since)
                        {
                            Allocated?.Invoke(this, $"sync conversation caught up held={history.Count - batch.Count} resync={historyFull} waited_ms={(long)(DateTimeOffset.UtcNow - since).TotalMilliseconds}");
                            historyWaitingSince = null;
                        }
                        historyCarried = []; historyResync = false;
                        var historyKb = Since();
                        stage = "outbox";
                        await outbox.ReconcileAsync(cancellationToken: token, changes: historyFull ? null : history);
                        var outboxKb = Since();
                        stage = "permission";
                        foreach (var (_, open) in Conversations())
                            if (open.ChatId is { } id && (sender is not IProviderConnectionState available || available.IsConnected(id)) && (historyFull && history.Count == 0 || history.Any(change => change.Affects(id))))
                                await open.RefreshPermissionAsync();
                        var permissionKb = Since();
                        if (listKb + historyKb + outboxKb + permissionKb >= SlowApplyKb)
                            Allocated?.Invoke(this, $"sync alloc list_kb={listKb} history_kb={historyKb} outbox_kb={outboxKb} permission_kb={permissionKb} events={history.Count} resync={historyFull} reason={reason}");
                        token.ThrowIfCancellationRequested();
                        stage = "live";
                        retryDelay = (int)ResyncDelay.TotalMilliseconds; SetLive(true); ReportLive();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    // A failed resync doesn't block sending while still connected; a disconnect event turns it off.
                    catch (Exception failure)
                    {
                        // The exception's type, never its message: a provider's message can carry a name or a number.
                        Allocated?.Invoke(this, $"sync failed stage={stage} type={failure.GetType().Name} inner={failure.InnerException?.GetType().Name ?? "none"} " +
                            $"hresult=0x{failure.HResult:X8} events={batch.Count} retry_ms={Math.Min(30000, retryDelay * 2)}");
                        needsResync = true; resyncReason = "failed"; retryDelay = Math.Min(30000, retryDelay * 2); Report(SyncState.Interrupted, "Connection interrupted · Retrying…");
                    }
                    finally { UiActivity.Sync(applying); }
                });
                // Held for the next pass, unless there are now more than one pass can carry — at which point a full
                // resync is both cheaper and what the old behaviour always did.
                if (!deferred) continue;
                if (batch.Count >= 128) { needsResync = true; resyncReason = "overflow"; }
                else carried = batch;
            }
        }
        catch (OperationCanceledException) { }
        finally { loop.Cancel(); try { await producer; } catch (OperationCanceledException) { } }
    }
    // Whether this conversation took the event into its messages: it names the conversation (or, merged, a member), and the
    // conversation's own service is connected, as the pass above gives it events only then.
    private bool AppliesTo(ConversationViewModel open, RealtimeEvent change) =>
        (open.ContainsChat(change.ChatId) || change.Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted && change.Ids?.Any(open.ContainsChat) == true) &&
        (open.IsMerged || sender is not IProviderConnectionState available || open.ActiveChatId is not { } target || available.IsConnected(target));
    private async Task ProduceAsync(Channel<RealtimeEvent> channel, CancellationToken token)
    {
        // Stamped before the write, which waits when the channel is full, so that wait is part of the time measured.
        try
        {
            await feed.RunAsync((change, ct) =>
            {
                if (IsMeasured(change)) arrivals.AddOrUpdate(change, new(System.Diagnostics.Stopwatch.GetTimestamp()));
                return channel.Writer.WriteAsync(change, ct).AsTask();
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { channel.Writer.TryWrite(new(RealtimeKind.AuthenticationRequired)); }
        finally { channel.Writer.TryComplete(); }
    }
    public void Dispose() { var previous = session; session = null; previous?.Cancel(); previous?.Dispose(); }
}
