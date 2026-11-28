using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MessageLink.App.Services;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;
using MessageLink.Protocol;
using MessageLink.Terminal.Connection;

namespace MessageLink.Terminal.Session;

/// <summary>What <see cref="TerminalHost"/> is made of: the session's parts it reads and acts through.</summary>
internal sealed record TerminalHostParts(ServerLink Link, MlinkContext Context, Action<string> Copy,
    MessagingProxy Messaging, ChatListViewModel Chats, MergedConversations Merges, LabelStore Labels, ILocalChatPreferences ChatPreferences,
    IChatSnoozes Snoozes, SavedMessages Saved, MessageActionsViewModel Actions, Outbox Outbox, FeedBatcher Pump, HostDiagnosticsReader Diagnostics,
    IHostLogs Logs, IHostIdentity Identity, IAccessTokenStore Tokens, ReplicatedStateStore Replica, DateTimeOffset Launched);

/// <summary>
/// What the chat console asks of the program around it, in a terminal over Message Link Server, with every member answered by the server: its status
/// and helpers, its logs, its whole history as this terminal holds it, and its shared rows (mutes, pins, labels, snoozes, saved messages) through the replica, which sends each change on. Every action is one explicit command,
/// asked once and never retried; an answer that was lost is Unknown. Nothing here opens a window, raises a notification or reads a service by itself.
/// </summary>
internal sealed partial class TerminalHost : IConsoleHost
{
    private readonly TerminalHostParts parts;
    private ServerLink Link => parts.Link;
    private HostStateCache State => parts.Link.State;
    private IReadOnlyList<ConsoleQueueItem> serverQueue = [];
    private int scheduledHeld, unsettledHeld;

    public TerminalHost(TerminalHostParts parts)
    {
        this.parts = parts;
        parts.Replica.RemoteChanged += OnRemoteChanged;
        // The first read crosses the wire, so it starts now and /top, asked a moment later, has an answer (as the window's console host does).
        parts.Diagnostics.Latest();
    }

    // ---- The server and its helpers ----

    // Each service the console reports on, in the house order: its row's name, its connection scope and its network.
    private List<(string Name, string Scope, string Network)> ServiceRows()
    {
        var rows = new List<(string Name, string Scope, string Network)> { ("Google Messages", ProviderScopes.Google, "Google Messages"), ("Messenger", ProviderScopes.Messenger, "Messenger") };
        var voice = State.VoiceKnownKeys;
        if (voice.Count == 0) rows.Add(("Google Voice", ProviderScopes.VoicePrefix, "Google Voice"));
        for (var i = 0; i < voice.Count; i++) rows.Add(($"Google Voice {(i + 1).ToString(CultureInfo.InvariantCulture)}", ProviderScopes.Voice(voice[i]), "Google Voice"));
        rows.Add(("Instagram", ProviderScopes.Instagram, "Instagram"));
        // Hermes has no helper process of its own: the server reaches it over HTTP. Listed only where the server runs it.
        if (State.Hermes.Agents.Count > 0) rows.Add(("Hermes", ProviderScopes.Hermes, HermesIds.Network));
        return rows;
    }

    // The window's words: connected, unconfigured, sign-in needed (the server says only its sign-in was refused, or the feed said so), offline.
    private string StateOf(string scope)
    {
        if (State.IsScopeConnected(scope)) return "connected";
        if (!State.IsConfigured(scope)) return "unconfigured";
        return State.NeedsSignInOnly(scope) || parts.Pump.NeedsSignIn(scope) ? "sign-in needed" : "offline";
    }

    public ConsoleSystemStatus SystemStatus
    {
        get
        {
            var helpers = parts.Diagnostics.Latest().Helpers;
            var now = DateTimeOffset.UtcNow;
            var processes = ServiceRows().Select(row =>
            {
                var helper = helpers.FirstOrDefault(h => h.Scope == row.Scope);
                var exit = int.TryParse(helper?.LastExit?.Code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? (int?)code : null;
                var running = helper?.Running == true;
                return new ConsoleProcessStatus(row.Name, StateOf(row.Scope), running, running ? now - helper!.StartedAt : null,
                    Math.Max(0, (helper?.Starts ?? 1) - 1), exit, row.Network, running ? helper!.ProcessId : null, helper?.WorkingSetBytes,
                    helper?.CpuMilliseconds is { } cpu ? TimeSpan.FromMilliseconds(cpu) : null, null, null, helper?.LastExit?.How, helper?.LastExit?.At);
            }).ToArray();
            return new(parts.Context.Version, processes, parts.Outbox.Unsettled.Count, scheduledHeld);
        }
    }

    public IReadOnlyList<ConsoleQueueItem> Queue
    {
        get
        {
            var items = new List<ConsoleQueueItem>();
            foreach (var sending in parts.Outbox.Unsettled)
            {
                var state = sending.State switch
                {
                    OutgoingState.Sending => "sending",
                    OutgoingState.Pending => "pending",
                    OutgoingState.Unknown => "unknown",
                    OutgoingState.Held => "held",
                    _ => null,
                };
                if (state is not null) items.Add(new(state, sending.ChatId, sending.Text, sending.CreatedAt));
            }
            items.AddRange(serverQueue);
            return [.. items.OrderBy(item => item.Created)];
        }
    }

    // The server's scheduled sends and its record of sends that may not have gone are rows of the replica, which its own events keep: read again when they move.
    private void OnRemoteChanged(object? sender, SharedRowsChange change)
    {
        if (change.Keys.Any(key => key.StartsWith(SharedCategories.Scheduled + ":", StringComparison.Ordinal) || key.StartsWith(SharedCategories.Outbox + ":", StringComparison.Ordinal)))
            _ = RefreshQueueAsync();
    }

    /// <summary>Reads the server's scheduled and unsettled sends from the replica into what <see cref="Queue"/> answers with.</summary>
    public async Task RefreshQueueAsync()
    {
        try
        {
            if (await NotificationIdentity.ProfileAsync(parts.Tokens) is not { } profile) return;
            var items = new List<ConsoleQueueItem>();
            int scheduled = 0, unsettled = 0;
            foreach (var (key, value, _) in await parts.Replica.ReadPrefixWithRevAsync(SharedCategories.Scheduled + ":"))
            {
                ScheduledSend? item = null;
                try { item = JsonSerializer.Deserialize<ScheduledSend>(value); } catch (JsonException) { }
                // A row counts only under the key its own id gives it under this profile, as the window's link reads them.
                if (item is not { Id.Length: > 0, ChatId.Length: > 0 } || key != SharedCategories.KeyOf(profile, SharedCategories.Scheduled, item.Id)) continue;
                scheduled++;
                items.Add(new(item.State switch { ScheduledSendState.Waiting => "scheduled", ScheduledSendState.Sending => "sending", _ => "not sent" },
                    item.ChatId, item.Text, item.CreatedAt, item.Due));
            }
            foreach (var (key, value, _) in await parts.Replica.ReadPrefixWithRevAsync(SharedCategories.Outbox + ":"))
            {
                SharedUnsettledSend? send = null;
                try { send = JsonSerializer.Deserialize<SharedUnsettledSend>(value); } catch (JsonException) { }
                if (send is not { OperationId.Length: > 0, ChatId.Length: > 0 } || key != SharedCategories.KeyOf(profile, SharedCategories.Outbox, send.OperationId)) continue;
                unsettled++;
                items.Add(new(send.State == UnsettledStates.Unknown ? "unknown" : "not sent", send.ChatId, send.Text ?? "", send.CreatedAt));
            }
            serverQueue = items;
            scheduledHeld = scheduled;
            unsettledHeld = unsettled;
        }
        catch (Exception failure) { parts.Context.Log($"mlink queue read failed error={failure.GetType().Name}"); }
    }

    // /diag: every line from what is already held; no detail names a person, an account, a conversation or a message.
    public IReadOnlyList<ConsoleCheck> Checks()
    {
        var checks = new List<ConsoleCheck>();
        var environment = Environment;
        checks.Add(new(ConsoleCheckLevel.Ok, "version", $"Message Link {environment.Version} · {environment.Install}"));
        checks.Add(Link.Client.IsConnected ? new(ConsoleCheckLevel.Ok, "server", "connected · " + Link.Name)
            : new(ConsoleCheckLevel.Fail, "server", Link.Client.Refused ? "a different version of Message Link Server" : "not connected · " + Link.Name));
        var helpers = parts.Diagnostics.Latest().Helpers;
        foreach (var service in ServiceRows())
        {
            var state = StateOf(service.Scope);
            checks.Add(new(state switch { "connected" => ConsoleCheckLevel.Ok, "unconfigured" => ConsoleCheckLevel.Info, _ => ConsoleCheckLevel.Fail },
                service.Name, state == "unconfigured" ? "not set up" : state));
            if (helpers.FirstOrDefault(h => h.Scope == service.Scope) is not { } helper) continue;
            var restarts = Math.Max(0, helper.Starts - 1);
            if (restarts > 0)
            {
                var exit = helper.LastExit is { } last ? $" · last ended {last.How}, code {last.Code}" : "";
                checks.Add(new(ConsoleCheckLevel.Warn, service.Name + " helper",
                    $"restarted {restarts.ToString(CultureInfo.InvariantCulture)} time{(restarts == 1 ? "" : "s")} on the server{exit}"));
            }
            else checks.Add(new(helper.Running ? ConsoleCheckLevel.Ok : ConsoleCheckLevel.Info, service.Name + " helper",
                helper.Running ? "running" : helper.LastExit is { } last ? $"stopped ({last.How}, code {last.Code})" : "stopped"));
        }
        HermesChecks(checks);
        var now = DateTimeOffset.UtcNow;
        var unsettled = parts.Outbox.Unsettled;
        var stuck = unsettled.Count(m => m.State == OutgoingState.Unknown || m.State == OutgoingState.Held && now - m.CreatedAt > TimeSpan.FromMinutes(1));
        checks.Add(new(stuck > 0 ? ConsoleCheckLevel.Warn : ConsoleCheckLevel.Ok, "outbox",
            unsettled.Count == 0 ? "empty" : $"{unsettled.Count.ToString(CultureInfo.InvariantCulture)} unsettled" +
                (stuck > 0 ? $", {stuck.ToString(CultureInfo.InvariantCulture)} unknown or held over a minute" : "")));
        var notSent = serverQueue.Count(item => item.State == "not sent" && item.Due is not null);
        checks.Add(new(notSent > 0 ? ConsoleCheckLevel.Warn : ConsoleCheckLevel.Info, "scheduled",
            $"{scheduledHeld.ToString(CultureInfo.InvariantCulture)} scheduled on the server" + (notSent > 0 ? $", {notSent.ToString(CultureInfo.InvariantCulture)} not sent" : "")));
        if (unsettledHeld > 0)
            checks.Add(new(ConsoleCheckLevel.Warn, "server sends", $"{unsettledHeld.ToString(CultureInfo.InvariantCulture)} may not have been sent"));
        return checks;
    }

    // This copy of Message Link, and the machine the server runs on where that is this one; what the protocol does not say is left empty.
    public ConsoleEnvironment Environment => new(parts.Context.Version,
        Link.OnThisMachine ? RuntimeInformation.OSDescription : "", Link.OnThisMachine ? RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() : "",
        "", "", Link.OnThisMachine ? System.Environment.MachineName : Link.Name, System.Environment.UserName, Install, parts.Context.Folder, parts.Launched);

    // "dev" for a build run from the source tree, as the window says it.
    private static string Install
    {
        get
        {
            var folder = AppContext.BaseDirectory.Replace('\\', '/');
            return folder.Contains("/src/MessageLink.Terminal/bin/", StringComparison.OrdinalIgnoreCase) ? "dev" : "terminal";
        }
    }

    // /mirror: the server's check of its copies, and with "now" one started (its newest pages only; nothing sent or marked read), as the window asks it.
    public async Task<ConsoleMirror?> MirrorAsync(bool start, CancellationToken cancellationToken = default)
    {
        if (Link.Client.Version is not >= ProtocolVersion.Feed) return null;
        HistoryMirrorResult result;
        try { result = await Link.Client.CallAsync<HistoryMirrorParams, HistoryMirrorResult>(ProtocolMethods.HistoryMirror, new(start), cancellationToken); }
        catch (NotSupportedException) { return null; }
        return new ConsoleMirror(result.Running, [.. result.Reports.Select(report => new ConsoleMirrorService(report.Service switch
        {
            "google-direct" => "Google Messages", "messenger" => "Messenger", "instagram" => "Instagram", _ => "Google Voice",
        }, report.At, report.Checked, report.Differed, report.Missing, report.Extra, report.Changed, report.Skipped))]);
    }

    // ---- The server's logs (host.logs) ----

    // The names the pager shows and its words pick ("google", "helper", "voice"), to the names the wire uses; the server's own first.
    private static readonly (string Shown, string Wire)[] ServerLogs =
    [
        ("Server", HostLogNames.Server), ("Google Messages", HostLogNames.GoogleMessages), ("Messenger", HostLogNames.Messenger),
        ("Messenger helper", HostLogNames.MessengerHelper), ("Google Voice", HostLogNames.GoogleVoice), ("Instagram", HostLogNames.Instagram),
        ("Google Messages · sign-in history", HostLogNames.GoogleSignInHistory), ("Server · previous run", HostLogNames.ServerPrevious),
        ("Google Messages · previous run", HostLogNames.GoogleMessagesPrevious), ("Messenger helper · previous run", HostLogNames.MessengerHelperPrevious),
        ("Google Voice · previous run", HostLogNames.GoogleVoicePrevious), ("Instagram · previous run", HostLogNames.InstagramPrevious),
    ];

    /// <summary>How much of a log's end the first read shows; one answer's worth.</summary>
    public const int TailBytes = HostLogChunk.MaxChunk;

    public IReadOnlyList<string> LogSources => [.. ServerLogs.Select(log => log.Shown)];

    public async Task<ConsoleLogRead> ReadLogAsync(string source, long from, CancellationToken cancellationToken = default)
    {
        if (ServerLogs.FirstOrDefault(log => log.Shown.Equals(source, StringComparison.OrdinalIgnoreCase)) is not { Wire: { } name }) return new([], 0, true);
        HostLogChunk chunk;
        try { chunk = await parts.Logs.ReadAsync(name, from, cancellationToken); }
        catch (NotSupportedException) { return new(["This server doesn't share its logs."], 0, true); }
        if (!chunk.Exists) return new([], 0, from != 0);
        // The first read, or a file shorter than where this terminal had read to (the server started a new one): its end, replacing what was shown.
        var reset = from == 0 || from > chunk.Length;
        var skipFirst = false;
        if (reset && chunk.Length > TailBytes)
        {
            chunk = await parts.Logs.ReadAsync(name, chunk.Length - TailBytes, cancellationToken);
            // Read from the middle of a line: that line is left out.
            skipFirst = true;
        }
        else if (reset && from != 0) chunk = await parts.Logs.ReadAsync(name, 0, cancellationToken);
        var lines = chunk.Text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (skipFirst && lines.Count > 0) lines.RemoveAt(0);
        return new(lines, chunk.Next, reset);
    }

    // ---- History: the server's whole history as this terminal holds it ----

    // Only the copy of a conversation this terminal already holds from the server (HistoryCopies.Snapshot): memory, no call. A copy is made only by a read
    // the list itself asked for, so this never asks the server for anything, and the server never reads a service for it. A linked conversation has none of
    // its own; the session asks for each member.
    public Task<HistorySnapshot?> SavedHistoryAsync(string chatId) => Task.FromResult(HeldHistory(chatId));

    // A conversation a command named: the server's whole history (history.sync through the copies), which the server answers from its store and reads from
    // the service only if it has never read this conversation, as opening it would. Nothing is marked read. A merged row's members are each asked by the session.
    public async Task<HistorySnapshot?> ConversationHistoryAsync(string chatId)
    {
        if (HeldHistory(chatId) is { } held || MergedConversations.IsMerged(chatId)) return HeldHistory(chatId);
        if (parts.Chats.AllLoadedChats.FirstOrDefault(c => c.Id == chatId) is not { } chat) return null;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await parts.Messaging.Copies.NewestAsync(chatId, limit.Token) is not { } page) return null;
            return new HistorySnapshot(chat, page.Items, page.OldestCursor, page.HasMore, new HashSet<string>(StringComparer.Ordinal));
        }
        catch (Exception failure)
        {
            parts.Context.Log($"mlink history read failed type={failure.GetType().Name}");
            return null;
        }
    }

    public HistorySnapshot? HeldHistory(string chatId)
    {
        if (MergedConversations.IsMerged(chatId) || parts.Messaging.Copies.Snapshot(chatId) is not { } held) return null;
        var chat = parts.Chats.AllLoadedChats.FirstOrDefault(c => c.Id == chatId);
        if (chat is null) return null;
        return new HistorySnapshot(chat, held.Messages, held.Cursor, held.MoreOlder, new HashSet<string>(StringComparer.Ordinal));
    }

    public void Copy(string text)
    {
        if (!string.IsNullOrEmpty(text)) parts.Copy(text);
    }

    public void Dispose() => parts.Replica.RemoteChanged -= OnRemoteChanged;
}
