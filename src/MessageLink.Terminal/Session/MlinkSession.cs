using System.Diagnostics;
using System.Globalization;
using MessageLink.App.Services;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;
using MessageLink.Protocol;
using MessageLink.Terminal.Connection;

namespace MessageLink.Terminal.Session;

/// <summary>
/// The chat console's session over one server: the proxies, the replica of the server's shared rows, the conversation list and the feed that fills the
/// activity stream, the outbox, and the host the session asks things of (<see cref="IConsoleHost"/>). Made on the <see cref="TerminalLoop"/>'s thread.
/// </summary>
/// <remarks>
/// Composed as the window composes itself as a client of a server (ProtocolSeams.Over, ClientPortRegistrations.AddLocal, App.xaml.cs), without its Windows
/// parts: no notifications, no conversation view, no composer of its own. It reads only when asked (/read, through the private-reading gate's explicit
/// writer): seeing a line never marks anything read, and it never tells anyone it is typing.
/// </remarks>
public sealed class MlinkSession : IAsyncDisposable
{
    /// <summary>
    /// The unknown-outcome line: said on <see cref="MlinkContext.Out"/> when the session ends with a send or a change still waiting for its answer.
    /// </summary>
    public const string UnknownOutcomeLine = "A message may not have been sent: its outcome is unknown, and it was not sent again.";

    /// <summary>How long a full resync waits after a connection, and the first wait after a failed one, as the window's SyncViewModel. Settable for the checks.</summary>
    public static TimeSpan ResyncDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the feed waits before it subscribes again after its subscription ended (<see cref="RealtimeFeedProxy.RetryDelay"/>). Settable for the checks.</summary>
    public static TimeSpan FeedRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    private readonly ServerLink link;
    private readonly MlinkContext context;
    private readonly List<IDisposable> owned = [];
    private readonly CancellationTokenSource lifetime = new();
    private ReplicatedStateStore replica = null!;
    private StateIdentity identity = null!;
    private MessagingProxy messaging = null!;
    private ChatListViewModel chats = null!;
    private LabelStore labels = null!;
    private ActivityStream stream = null!;
    private Outbox outbox = null!;
    private FeedBatcher pump = null!;
    private TerminalHost host = null!;
    private ConsolePreferences preferences = null!;
    private ConsoleSender sender = null!;
    private ConversationTargets targets = null!;
    private ComposerViewModel composer = null!;
    private SavedMessages saved = null!;
    private Task? streamWorker, loading;
    private EventHandler? reconnected, mergesChanged;
    private EventHandler<string>? markedUnreadCleared, snoozeCleared;
    private EventHandler<ServerState>? stateChanged;
    private SharedListChanges? shared;
    private MergedConversations? merges;
    private int disposed;

    private MlinkSession(ServerLink link, MlinkContext context)
    {
        this.link = link;
        this.context = context;
    }

    /// <param name="copy">Puts text on the clipboard of the terminal the person is looking at (OSC 52).</param>
    public static async Task<MlinkSession> CreateAsync(ServerLink link, MlinkContext context, Action<string> copy, CancellationToken cancellationToken)
    {
        var session = new MlinkSession(link, context);
        try { await session.ComposeAsync(copy, cancellationToken); }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    private async Task ComposeAsync(Action<string> copy, CancellationToken cancellationToken)
    {
        var loop = SynchronizationContext.Current ?? throw new InvalidOperationException("The session is made on the terminal's loop.");
        var dispatcher = new LoopDispatcher(loop);
        var log = context.Log;
        var client = link.Client;
        var state = link.State;
        // Everything this terminal keeps of one server is in a folder of its own, so two servers' rows never mix.
        var serverFolder = Path.Combine(context.Folder, ReplicatedStateStore.FolderName, link.ServerId);
        Directory.CreateDirectory(serverFolder);

        // ---- The proxies (ProtocolSeams.Over) ----
        messaging = new MessagingProxy(client, state, log: log);
        identity = new StateIdentity(state, new LastKnownIdentity(Path.Combine(serverFolder, "identity.protected"), context.Protector), log);
        owned.Add(identity);
        var tokens = new IdentityAccessTokens(identity);
        var typingProxy = new TypingProxy(client, state);
        var searchProxy = new SearchProxy(client);
        var kept = new KeptHistoryProxy(client);
        var feed = new RealtimeFeedProxy(client, state) { RetryDelay = FeedRetryDelay };
        var snoozeProxy = new SnoozeProxy(client);
        var diagnostics = new ServerDiagnosticsReader(new ServerDiagnosticsProxy(client));
        var logs = new ServerLogsProxy(client);

        // ---- The replica of the server's shared rows, and its companions (ClientPortRegistrations.AddLocal) ----
        replica = new ReplicatedStateStore(Path.Combine(serverFolder, ReplicatedStateStore.FileName), context.Protector, client, tokens, log);
        owned.Add(replica);
        var privateState = new PrivateState(replica, tokens);
        var source = new ReplicaPreferenceSource(replica, log);
        // The files the rows fall back on with a server too old to keep them: this terminal's own, beside the replica, never the app's data folder. Every
        // server mlink can reach keeps the rows, so they stay empty.
        var chatPreferences = new ReplicaChatPreferences(source, new LocalChatPreferenceStore(Path.Combine(serverFolder, "ChatPreferences-v1")), log);
        owned.Add(chatPreferences);
        var names = new ReplicaContactNames(source, new LocalContactNameStore(Path.Combine(serverFolder, "ContactNames-v1"), context.Protector));
        owned.Add(names);
        var replicaSnoozes = new ReplicaChatSnoozes(source, snoozeProxy);
        owned.Add(replicaSnoozes);
        var snoozes = new ObservedChatSnoozes(replicaSnoozes);
        var merges = new MergedConversations(privateState, GoogleVoiceIds.TextRouteForCall);
        labels = new LabelStore(privateState, error => log($"mlink labels load failed error={error}"));
        saved = new SavedMessages(replica);
        owned.Add(saved);

        // ---- The conversation list, read as the window reads it (App.xaml.cs's IChatReader) ----
        var catalog = new LocalSearchCatalog();
        // It keeps the copies it read, so another device's change to a row is applied to them again (SharedListChanges, below).
        var sourceRows = new LocalPreferenceChatReader(messaging, chatPreferences, tokens, names, snoozes, remembersSources: true);
        var reader = new LocalPreferenceChatReader(new MergedChatReader(new LocalSearchChatReader(sourceRows, catalog, tokens), merges), chatPreferences, tokens, names,
            snoozes, mergedOnly: true);
        chats = new ChatListViewModel(reader, privateState, merges, labels);
        chats.Allocated += (_, line) => log(line);

        // ---- Another device's changes, and this terminal's reads, reaching the server's rows (ClientPortRegistrations.AddLocal, as the window does) ----
        // A pin, mute, mark, snooze or name changed elsewhere, the pinned order, the merges and the labels: the list follows with no reload.
        shared = new SharedListChanges(replica, chatPreferences, names, replicaSnoozes, tokens, log);
        shared.ReappliesWith(sourceRows);
        shared.Follows(merges, labels);
        shared.Attach(dispatcher, chats);
        this.merges = merges;
        mergesChanged = (_, _) => _ = dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref disposed) == 0) chats.RebuildMerged();
            return Task.CompletedTask;
        });
        merges.Changed += mergesChanged;
        // Reading clears Mark unread and a snooze in the list (/read); the server's rows follow, so they do not come back here or stay on other devices.
        markedUnreadCleared = (_, chatId) => _ = ClearRowAsync("marked_unread", async profile => await chatPreferences.SetAsync(profile, chatId, ChatSetting.MarkedUnread, false));
        snoozeCleared = (_, chatId) => _ = ClearRowAsync("snooze", async profile => await snoozes.SetAsync(profile, chatId, null));
        chats.MarkedUnreadCleared += markedUnreadCleared;
        chats.SnoozeCleared += snoozeCleared;

        async Task ClearRowAsync(string what, Func<string, Task> clear)
        {
            try
            {
                if (await NotificationIdentity.ProfileAsync(tokens) is { } profile) await clear(profile);
            }
            // The type alone. The list shows it read either way; a failed write only means the mark or the reminder may come back.
            catch (Exception failure) { log($"mlink read clear failed part={what} type={failure.GetType().Name}"); }
        }

        // ---- The stream, the outbox and the feed (App.Activity.cs, SyncViewModel) ----
        var launched = LaunchedAt();
        stream = new ActivityStream(messaging, tokens, merges, cached: id => HeldHistory(id), launchedAt: launched);
        outbox = new Outbox(messaging, outcomes: client, dispatcher: dispatcher);
        outbox.Resolved += (_, message) => stream.ObserveSent(message);
        outbox.BatchResolved += (_, batch) => stream.ObserveSent(batch.Messages[^1]);
        var gate = new PrivateReadingGate(messaging, typingProxy, new PrivateReading(PrivateReading.All));
        Typing = new WatchOnlyTyping(gate);
        var token = lifetime.Token;
        streamWorker = Task.Run(() => stream.RunAsync(action => dispatcher.InvokeAsync(() => { action(); return Task.CompletedTask; }), token));
        _ = Task.Run(() => WatchTypingAsync(gate, stream, log, token));
        // The window's batcher with no open conversations. Its first pass reads the list whole: what the server held before this terminal started is in no
        // event. Its lines carry mlink's prefix.
        pump = new FeedBatcher(feed, chats, outbox, dispatcher, messaging, line => log("mlink " + line), resyncAtStart: true) { ResyncDelay = ResyncDelay };
        pump.BatchApplied += (_, batch) => stream.Offer(batch);
        pump.ListResynced += (_, resync) => log($"mlink resync reason={resync.Reason} chats={chats.AllLoadedChats.Count.ToString(CultureInfo.InvariantCulture)}");
        // Back after a loss: what changed meanwhile is read again whole, as the window does after a connection.
        reconnected = (_, _) => pump.Resync();
        client.Reconnected += reconnected;
        // The server's state comes after the connection: a resync kept for a connection runs once something reads as connected.
        stateChanged = (_, _) => pump.ConnectionChanged();
        state.StateChanged += stateChanged;

        // ---- Search, history, and what the console is built from ----
        var search = new MergedSearchBackend(new UniversalSearchBackend(searchProxy, catalog, tokens, merges, kept), reader, merges);
        targets = new ConversationTargets(() => chats.AllLoadedChats, merges, merges.VoiceTextRoute);
        var reads = new ExplicitRead(gate.Explicit, chats, merges, async chatId =>
        {
            if (await NotificationIdentity.ProfileAsync(tokens) is { } profile) await snoozes.SetAsync(profile, chatId, null);
        });
        sender = new ConsoleSender(messaging, outbox, targets, merges, null, log, replies: messaging);
        // /history reads only what this terminal already holds of the server's whole history (HistoryCopies.Snapshot, memory alone) and, failing that, each
        // conversation's newest message from the list: no call is made for it, so neither the server nor any service is asked for anything. No source is
        // given that answers a span by itself (the window's is Google's archive on this PC, which a terminal does not have).
        var history = new ConsoleHistory(() => chats.AllLoadedChats, id => Task.FromResult(HeldHistory(id)), [], merges);
        composer = new ComposerViewModel(messaging, outbox);
        var actions = new MessageActionsViewModel(messaging, tokens, composer, messaging);
        preferences = await ConsolePreferences.LoadAsync(context.Folder, context.Protector, log);
        host = new TerminalHost(new TerminalHostParts(link, context, copy, messaging, chats, merges, labels, chatPreferences, snoozes, saved, actions,
            outbox, pump, diagnostics, logs, identity, tokens, replica, launched));
        Console = new ConsoleSessionViewModel(stream, targets, reads, sender, search, merges, host, preferences, null, log, history);

        // The console opens once the server's pushed state (accounts, helpers, identity) is here and the list has been read, so its first lines (/neofetch's
        // services and chats) and a first command typed or piped at once see the server's conversations rather than an empty list. The feed brings the state
        // (RealtimeFeedProxy applies it to the cache) and holds its first pass while the list's first read runs; that full pass reads the rest of the list,
        // which a name typed at once may need. All bounded: a slow server opens a console that fills in as they arrive.
        var started = Stopwatch.StartNew();
        await merges.EnsureLoadedAsync().WaitAsync(cancellationToken);
        loading = chats.RefreshChosenAsync();
        pump.Start();
        await WhenAsync(() => state.HasState, handler => state.StateChanged += handler, handler => state.StateChanged -= handler, StartWait, cancellationToken);
        await Task.WhenAny(loading, Task.Delay(StartWait, cancellationToken));
        var left = StartWait - started.Elapsed;
        if (left > TimeSpan.Zero) await Task.WhenAny(pump.FirstPass, Task.Delay(left, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        log($"mlink session ready chats={chats.AllLoadedChats.Count.ToString(CultureInfo.InvariantCulture)} state={(state.HasState ? 1 : 0)} ms={started.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        _ = host.RefreshQueueAsync();
        Console.Attach();
        log($"mlink session started on_this_machine={(link.OnThisMachine ? 1 : 0)} protocol={(client.Version?.ToString(CultureInfo.InvariantCulture) ?? "none")}");
    }

    /// <summary>The most the console waits, as it opens, for the server's first state and then its conversation list.</summary>
    public static TimeSpan StartWait { get; set; } = TimeSpan.FromSeconds(10);

    private static async Task WhenAsync(Func<bool> done, Action<EventHandler<ServerState>> add, Action<EventHandler<ServerState>> remove, TimeSpan bound,
        CancellationToken cancellationToken)
    {
        if (done()) return;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, ServerState state) { if (done()) arrived.TrySetResult(); }
        add(Changed);
        try
        {
            if (done()) return;
            await Task.WhenAny(arrived.Task, Task.Delay(bound, cancellationToken));
        }
        finally { remove(Changed); }
    }

    private static async Task WatchTypingAsync(ITypingService typing, ActivityStream stream, Action<string> log, CancellationToken token)
    {
        try { await foreach (var update in typing.WatchAsync(token)) stream.ObserveTyping(update); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure) { log($"mlink typing watch ended error={failure.GetType().Name}"); }
    }

    // When this program started, which the stream's start-up quiet is measured from.
    private static DateTimeOffset LaunchedAt()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { return DateTimeOffset.UtcNow; }
    }

    private HistorySnapshot? HeldHistory(string chatId) => host?.HeldHistory(chatId);

    public ConsoleSessionViewModel Console { get; private set; } = null!;

    /// <summary>Typing in every conversation, for the typing strip; null where the server gives none. Watching only: mlink never says it is typing.</summary>
    public ITypingService? Typing { get; private set; }

    /// <summary>What the console asks of this program: the server's status, logs and shared rows.</summary>
    public IConsoleHost Host => host;

    /// <summary>Every conversation the list holds now.</summary>
    public IReadOnlyList<ChatSummary> Conversations => chats?.AllLoadedChats ?? [];

    /// <summary>How long ending the session waits for sends and changes still waiting for their answer. Settable for the checks.</summary>
    public TimeSpan SettleBound { get; set; } = TimeSpan.FromSeconds(5);

    // Sends whose answer has not come, and other changes (a reaction, an edit, a delete) still on the wire.
    private int Unsettled() => Math.Max(outbox?.SendingCount ?? 0, link.Client.MutationsInFlight);

    /// <summary>
    /// Waits, bounded, for this run's sends still waiting for their answer, then lets the connection go. A send cut off is outcome unknown: it is said
    /// on <see cref="MlinkContext.Out"/> and never sent again.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        var before = outbox is null ? 0 : Unsettled();
        var clock = Stopwatch.StartNew();
        while (outbox is not null && Unsettled() > 0 && clock.Elapsed < SettleBound) await Task.Delay(50);
        var after = outbox is null ? 0 : Unsettled();
        context.Log($"mlink quit sends before={before.ToString(CultureInfo.InvariantCulture)} after={after.ToString(CultureInfo.InvariantCulture)} ms={clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        if (after > 0)
        {
            try { context.Out.WriteLine(UnknownOutcomeLine); } catch (Exception) { }
        }
        if (preferences is not null) await preferences.SaveInputsAsync();
        if (labels is not null)
        {
            // The last label change is written behind it; a write that hangs must not keep the program from ending.
            try { await labels.LastSave.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
        if (reconnected is not null) link.Client.Reconnected -= reconnected;
        if (stateChanged is not null) link.State.StateChanged -= stateChanged;
        if (merges is not null && mergesChanged is not null) merges.Changed -= mergesChanged;
        if (chats is not null)
        {
            if (markedUnreadCleared is not null) chats.MarkedUnreadCleared -= markedUnreadCleared;
            if (snoozeCleared is not null) chats.SnoozeCleared -= snoozeCleared;
            shared?.Detach(chats);
        }
        shared?.Dispose();
        Console?.Dispose();
        sender?.Dispose();
        targets?.Dispose();
        if (pump is not null) { await pump.StopAsync(TimeSpan.FromSeconds(2)); pump.Dispose(); }
        lifetime.Cancel();
        if (streamWorker is not null) await Task.WhenAny(streamWorker, Task.Delay(TimeSpan.FromSeconds(2)));
        if (loading is not null) await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(2)));
        host?.Dispose();
        composer?.Dispose();
        // Never resent: the outbox lets go of what it holds, and the connection's end settles nothing by asking again.
        outbox?.Dispose();
        chats?.Dispose();
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            try { owned[i].Dispose(); }
            catch (Exception failure) { context.Log($"mlink dispose failed part={owned[i].GetType().Name} error={failure.GetType().Name}"); }
        }
    }
}
