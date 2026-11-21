using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

/// <summary>
/// Hermes Agent: one thread a profile, over Hermes's chat-completions API server (<see cref="HermesClient"/>). Plain HTTP, no
/// helper process. Each thread keeps one Hermes session and sends only the new turn; Hermes holds the conversation and compacts
/// it in place under the same id, so the thread's whole scrollback lives here and in the server's history, as for every service.
/// The reply streams in: the thread shows it as it fills, and the typing line says what Hermes is doing while it works.
/// </summary>
/// <remarks>
/// The owner's privacy line (2026-11-14): Hermes gets only what is typed or sent in its own thread. This backend is reached only
/// through the routers for <c>hermes:</c> ids, takes text and pictures, and offers Hermes nothing to call back through. Replying,
/// reacting, editing, deleting and every directory, group and settings action are refused by name in the routers.
/// </remarks>
internal sealed partial class HermesBackend : IChatReader, IRealtimeFeed, IMessageSender, IReadStateWriter, IAttachmentBackend, ITypingService,
    IHermesAccount, IHermesPrompts, IDisposable
{
    /// <summary>The longest a send waits for Hermes to accept the turn before it answers anyway and lets the reply come when it comes.</summary>
    internal TimeSpan AcceptWait { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How often a reply that is filling in is shown again.</summary>
    internal TimeSpan FillEvery { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Checks of a profile that answered; one that did not is asked again on <see cref="RetrySteps"/>, never faster.</summary>
    internal TimeSpan CheckEvery { get; init; } = TimeSpan.FromMinutes(15);
    internal IReadOnlyList<TimeSpan> RetrySteps { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];
    internal const int MaxProfiles = 20, MaxTextLength = 32_000, MaxTitleLength = 64;
    internal const long MaxImageBytes = 20 << 20;
    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private readonly HermesStore store;
    // Report tokens, setup and add codes, and their limits: who may post to a thread outside a turn.
    private readonly HermesReportAccess access;
    private readonly HermesClient client;
    private readonly TimeProvider clock;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RealtimeBuffer realtime;
    private readonly Channel<TypingUpdate> typing = Channel.CreateUnbounded<TypingUpdate>();
    private readonly ConcurrentDictionary<string, Status> statuses = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private TaskCompletionSource checkNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loaded;
    private HermesSettings settings = HermesSettings.Empty;
    private readonly Dictionary<string, HermesThread> threads = new(StringComparer.Ordinal);
    private int turnsRunning;
    // Moves on each save of the settings, so an answer to a check or a turn made with the settings before it never says how the new ones stand
    // (an old key's 401 arriving after a new key was saved would otherwise block the new one).
    private int generation;
    private CancellationTokenSource? checking;
    internal TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    private sealed record Status(string Word, string? Title, DateTimeOffset NextCheck, int Failures);

    public HermesBackend(IStateProtector protector, HermesStore? store = null, HttpClient? http = null, TimeProvider? clock = null, Action<string>? log = null)
    {
        this.store = store ?? new HermesStore(AppPaths.Combine("Hermes"), protector);
        this.clock = clock ?? TimeProvider.System;
        this.log = log ?? AppLog.Write;
        this.store.Log ??= this.log;
        access = new(this.store, this.clock, this.log, MaxProfiles);
        // No redirects: a 307 would carry the turn and its pictures to another host. A buffered answer (models, approvals, errors, the session read)
        // is a few kilobytes; the cap keeps an address that answers without end from filling the server's memory.
        client = new HermesClient(http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 });
        realtime = new(256, line => this.log("hermes " + line));
    }

    public event EventHandler? Changed;

    /// <summary>Whether Hermes is set up here: an address and at least one profile.</summary>
    public bool Configured => settings.Configured;
    public bool AnyConnected => settings.Profiles.Any(p => Word(p.Name) == "connected");
    public bool IsConnected(string chatId) => HermesIds.ProfileOf(chatId) is { } profile && Has(profile) && Word(profile) == "connected";

    private string Word(string profile) => statuses.TryGetValue(profile, out var status) ? status.Word : "unchecked";
    private bool Has(string profile) => settings.Profiles.Any(p => p.Name == profile);

    public HermesAccountState State => new(settings.Address,
        [.. settings.Profiles.Select(p => new HermesAgentState(p.Name, Title(p.Name), Word(p.Name), p.Key.Length > 0))],
        CanConfigure: true);

    private readonly object loading = new();
    // The settings or the report tokens a load found there but couldn't read (HermesStore holds the file): read as none,
    // and the load counted as done, Hermes stayed unconfigured, or every report token unknown, until the process restarted.
    // The next caller once the wait is over loads what is still missing again; the wait doubles while it stays held.
    private bool settingsPending = true, reportsPending = true;
    private DateTimeOffset loadRetryAt;
    private TimeSpan loadRetryWait;
    internal TimeSpan LoadRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadRetryMost = TimeSpan.FromMinutes(2);

    // A load that faulted is run again by the next caller: kept, it failed every Hermes call until the process restarted.
    public Task LoadAsync()
    {
        lock (loading)
        {
            if (loaded is null or { IsFaulted: true }
                || loaded.IsCompletedSuccessfully && (settingsPending || reportsPending) && clock.GetUtcNow() >= loadRetryAt)
                loaded = Task.Run(LoadCoreAsync);
            return loaded;
        }
    }

    private async Task LoadCoreAsync()
    {
        // Settings that loaded are not read again: what this run has changed since is in memory and saved, and the threads were read with them.
        var read = settingsPending ? await store.ReadSettingsAsync().ConfigureAwait(false) : null;
        var closed = new List<string>();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Taken as loaded only once the whole load is through: one that faulted after them reads both again.
            var settingsHeld = read is null ? settingsPending : store.SettingsHeld;
            if (read is not null) access.Commit(read, () => settings = read);
            var reportsHeld = reportsPending;
            if (reportsPending) reportsHeld = await access.ReadTokensAsync().ConfigureAwait(false);
            // Removing every profile forgets the tokens itself, before the settings are saved.
            await access.PruneAsync().ConfigureAwait(false);
            if (read is not null)
                foreach (var profile in settings.Profiles)
                    if (await store.ReadThreadAsync(profile.Name).ConfigureAwait(false) is { } thread)
                    {
                        var before = closed.Count;
                        threads[profile.Name] = Sequenced(CloseStale(thread, closed));
                        if (closed.Count > before) await store.SaveThreadAsync(threads[profile.Name]).ConfigureAwait(false);
                        SeedLastReport(thread);
                    }
            (settingsPending, reportsPending) = (settingsHeld, reportsHeld);
            loadRetryWait = settingsPending || reportsPending
                ? (loadRetryWait == TimeSpan.Zero ? LoadRetryDelay : TimeSpan.FromTicks(Math.Min(loadRetryWait.Ticks * 2, LoadRetryMost.Ticks)))
                : TimeSpan.Zero;
            loadRetryAt = clock.GetUtcNow() + loadRetryWait;
        }
        finally { gate.Release(); }
        log($"hermes loaded configured={(settings.Configured ? 1 : 0)} profiles={settings.Profiles.Count}" + (closed.Count > 0 ? $" approvals_closed={closed.Count}" : "")
            + (settingsPending || reportsPending ? $" held={(settingsPending ? "settings" : "")}{(settingsPending && reportsPending ? "," : "")}{(reportsPending ? "reports" : "")}" : ""));
        foreach (var id in closed) realtime.TryWrite(new(RealtimeKind.MessageChanged, HermesIds.Chat(HermesIds.ProfileOf(id)!), [id], false, clock.GetUtcNow()));
    }

    // ---- IHermesAccount ----

    public Task ConfigureAsync(string? address, IReadOnlyList<HermesProfileInput> profiles, CancellationToken cancellationToken = default) =>
        ConfigureAsync(_ => (address, profiles), null, null, cancellationToken);

    /// <summary>
    /// Saves what <paramref name="build"/> makes of the settings as they are once the gate is held, so a change made meanwhile (another assistant added in one
    /// command) is built on, never undone. <paramref name="still"/>, asked under the gate first, can call it off (false answered, nothing changed);
    /// <paramref name="committed"/> runs under the setup lock as the new settings take effect, so a setup code's state and the settings never disagree.
    /// </summary>
    private async Task<bool> ConfigureAsync(Func<HermesSettings, (string? Address, IReadOnlyList<HermesProfileInput> Profiles)> build, Func<bool>? still, Action? committed,
        CancellationToken cancellationToken)
    {
        await LoadAsync().ConfigureAwait(false);
        string[] removed, added;
        List<HermesProfileSetting> kept;
        var renamed = new List<(string ChatId, string[] Ids)>();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (still?.Invoke() == false) return false;
            var (address, profiles) = build(settings);
            var parsed = HermesClient.ParseBase(address);
            if (profiles.Count > 0 && parsed is null) throw new ArgumentException("Enter the address of Hermes's API server, such as http://192.168.1.20:8642.");
            if (profiles.Count > MaxProfiles) throw new ArgumentException($"Hermes can have up to {MaxProfiles} profiles here.");
            kept = [];
            foreach (var input in profiles)
            {
                var name = input.Name.Trim().ToLowerInvariant();
                if (!HermesClient.ValidProfile(name)) throw new ArgumentException("A profile name uses lowercase letters, numbers, hyphens and underscores, as in Hermes.");
                if (kept.Any(p => p.Name == name)) throw new ArgumentException("Each profile can be added once.");
                var key = input.Key?.Trim() is { Length: > 0 } typed ? typed : settings.Profiles.FirstOrDefault(p => p.Name == name)?.Key;
                if (string.IsNullOrEmpty(key)) throw new ArgumentException("Enter the API key for each profile.");
                if (key.Any(char.IsControl) || key.Length > 512) throw new ArgumentException("That API key can't be used.");
                // Like the key, a name of null keeps the saved one (the app's settings card and older clients send none) and a blank one clears it.
                // The default name typed out is kept as no name, so the field shows blank again and the thread follows the default.
                var title = input.Title is null ? settings.Profiles.FirstOrDefault(p => p.Name == name)?.Title
                    : input.Title.Trim() is { Length: > 0 } named && named != DefaultTitle(name) ? named : null;
                if (title is not null && (title.Any(char.IsControl) || title.Length > MaxTitleLength))
                    throw new ArgumentException($"A conversation name can have up to {MaxTitleLength} characters, on one line.");
                kept.Add(new(name, key, title));
            }
            var next = new HermesSettings(parsed?.ToString(), kept);
            // Moved before anything changes: an answer made with the old settings that lands meanwhile (during the save) is not taken for the new ones.
            Interlocked.Increment(ref generation);
            var addressChanged = settings.Address != next.Address;
            removed = [.. settings.Profiles.Select(p => p.Name).Except(kept.Select(p => p.Name))];
            added = [.. kept.Select(p => p.Name).Except(settings.Profiles.Select(p => p.Name))];
            // A changed key or address is checked again at once; an unchanged profile keeps what is known of it.
            foreach (var profile in kept)
                if (addressChanged || settings.Profiles.FirstOrDefault(p => p.Name == profile.Name)?.Key != profile.Key) statuses.TryRemove(profile.Name, out _);
            foreach (var gone in removed) statuses.TryRemove(gone, out _);
            // A removed profile's report token, codes and limits go with it, the token saved first (load drops any row whose profile is gone, too). A changed
            // address or key keeps them: the token is the profile's.
            await access.ProfilesGoneAsync(removed).ConfigureAwait(false);
            await store.SaveSettingsAsync(next).ConfigureAwait(false);
            var titled = kept.Where(p => settings.Profiles.Any(old => old.Name == p.Name) && (settings.Profiles.First(old => old.Name == p.Name).Title ?? DefaultTitle(p.Name))
                != (p.Title ?? DefaultTitle(p.Name))).Select(p => p.Name).ToArray();
            access.Commit(next, () =>
            {
                committed?.Invoke();
                settings = next;
            });
            // Hermes's messages carry the thread's name as their sender, and the server's history keeps them: a rename names each of them again, or the copy
            // there would keep the old name, its conversation not having moved since.
            foreach (var name in titled)
                if (threads.TryGetValue(name, out var held))
                    renamed.Add((HermesIds.Chat(name), [.. held.Messages.Where(m => !m.Outgoing).Select(m => m.Id)]));
            try { Volatile.Read(ref checking)?.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var name in added)
                if (!threads.ContainsKey(name)) threads[name] = await store.ReadThreadAsync(name).ConfigureAwait(false) is { } read ? Sequenced(CloseStale(read, [])) : NewThread(name);
        }
        finally { gate.Release(); }
        log($"hermes configured profiles={kept.Count} added={added.Length} removed={removed.Length}");
        if (removed.Length > 0) realtime.TryWrite(new(RealtimeKind.ChatDeleted, null, [.. removed.Select(HermesIds.Chat)], null, clock.GetUtcNow()));
        if (kept.Count > 0) realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [.. kept.Select(p => HermesIds.Chat(p.Name))], null, clock.GetUtcNow()));
        // In batches of 64, the most ids the server's history takes from one event (ConversationHistory.MaxEventIds) before reading the conversation again.
        foreach (var (chatId, ids) in renamed)
            foreach (var batch in ids.Chunk(64)) realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, batch, false, clock.GetUtcNow()));
        Raise();
        Interlocked.Exchange(ref checkNow, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return true;
    }

    public async Task NewConversationAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        await ReadHeldThreadAsync(profile, cancellationToken).ConfigureAwait(false);
        string noteId;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Refused before anything changes: the thread in memory took the new session, note, read mark and cleared handoff while
            // the save was refused, and the next turn ran on a session the saved thread never had.
            if (store.ThreadHeld(profile)) throw new IOException("hermes_store_unreadable");
            var thread = Thread(profile);
            var note = new HermesStoredMessage(NewMessageId(profile), clock.GetUtcNow(), false, NewConversationNote, Kind: "note", State: "done");
            noteId = note.Id;
            // A new conversation starts from nothing: every report so far counts as answered.
            var started = thread with
            {
                SessionId = NewSessionId(), Messages = [.. thread.Messages, note], ReadUpTo = note.At, ReportsAckedThrough = thread.NextReportSeq, Handoff = null,
            };
            await store.SaveThreadAsync(started).ConfigureAwait(false);
            threads[profile] = started;
        }
        finally { gate.Release(); }
        log($"hermes new_conversation profile={Tag(profile)}");
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [noteId], false, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
    }

    // ---- IChatReader ----

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        if (olderThan is not null) return new([], false, null, null);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return new([.. settings.Profiles.Select(p => Summary(p.Name))], false, null, null); }
        finally { gate.Release(); }
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Summary(profile); }
        finally { gate.Release(); }
    }

    private const int PageSize = 50;

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = Thread(profile).Messages;
            var end = all.Count;
            if (olderThan is not null)
            {
                var at = -1;
                for (var i = 0; i < all.Count; i++) if (all[i].Id == olderThan) { at = i; break; }
                if (at < 0) throw new BackendException(BackendFailure.InvalidResponse, "That page position is no longer valid.");
                end = at;
            }
            var start = Math.Max(0, end - PageSize);
            var items = all.Skip(start).Take(end - start).Select(m => Map(profile, m)).ToArray();
            return new(items, start > 0, start > 0 && items.Length > 0 ? all[start].Id : null, items.LastOrDefault()?.Id);
        }
        finally { gate.Release(); }
    }

    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Thread(profile).Messages.FirstOrDefault(m => m.Id == messageId) is { } found ? Map(profile, found)
                : throw new BackendException(BackendFailure.Unavailable, "That message isn't in this conversation.");
        }
        finally { gate.Release(); }
    }

    public ChatMessage? PeekMessage(string chatId, string messageId)
    {
        var profile = HermesIds.ProfileOf(chatId);
        if (profile is null || !gate.Wait(0)) return null;
        try { return threads.TryGetValue(profile, out var thread) && thread.Messages.FirstOrDefault(m => m.Id == messageId) is { } found ? Map(profile, found) : null; }
        finally { gate.Release(); }
    }

    // ---- reading ----

    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) => Task.FromResult(HermesIds.ProfileOf(chatId) is { } p && Has(p));

    /// <summary>Read only here: Hermes keeps no read state of its own and is told nothing.</summary>
    public async Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        bool moved;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var thread = Thread(profile);
            var at = thread.Messages.FirstOrDefault(m => m.Id == messageId)?.At ?? thread.Messages.LastOrDefault()?.At;
            moved = at is not null && (thread.ReadUpTo is null || at > thread.ReadUpTo);
            if (moved)
            {
                threads[profile] = thread with { ReadUpTo = at };
                await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
        if (moved) realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
    }

    // ---- sending ----

    public async Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (HermesIds.ProfileOf(chatId) is { } profile && HermesClient.ValidProfile(profile))
            await ReadHeldThreadAsync(profile, cancellationToken).ConfigureAwait(false);
        return Permission(chatId);
    }

    // A thread whose file is there but couldn't be read is read again when it is next asked for (a send, its permission, a new
    // conversation), so a file that was only locked or briefly out of reach comes back without a restart of the server.
    private async Task ReadHeldThreadAsync(string profile, CancellationToken cancellationToken)
    {
        if (!store.ThreadHeld(profile)) return;
        var closed = new List<string>();
        bool restored;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!store.ThreadHeld(profile) || !Has(profile)) return;
            var read = await store.ReadThreadAsync(profile).ConfigureAwait(false);
            restored = read is not null;
            if (read is not null)
            {
                threads[profile] = Sequenced(CloseStale(read, closed));
                if (closed.Count > 0) await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
                SeedLastReport(read);
            }
        }
        finally { gate.Release(); }
        if (store.ThreadHeld(profile)) return;
        log($"hermes thread_read_again profile={Tag(profile)} restored={(restored ? "yes" : "no")}");
        var chatId = HermesIds.Chat(profile);
        foreach (var id in closed) realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [id], false, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        Raise();
    }

    private SendPermission Permission(string chatId)
    {
        var profile = HermesIds.ProfileOf(chatId);
        if (profile is null || !Has(profile)) return new(false, "This Hermes profile isn't set up. Add it in Settings.");
        // Its saved file is there but couldn't be read: a send would start the thread over on top of it. Read again on the next try.
        if (store.ThreadHeld(profile)) return new(false, "This conversation's history couldn't be opened. Try again later.", MaxTextLength);
        return Word(profile) switch
        {
            "key_rejected" => new(false, "Hermes rejected this profile's API key. Change it in Settings.", MaxTextLength),
            "no_profile" => new(false, "Hermes doesn't have this profile at its address. Check it in Settings.", MaxTextLength),
            _ => new(true, "", MaxTextLength),
        };
    }

    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(chatId, text, [], "message", cancellationToken);

    public bool Accepts(string chatId) => HermesIds.ProfileOf(chatId) is { } profile && Has(profile);

    public Task<SendReceipt> SendScheduledAsync(string chatId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(chatId, text, [], "scheduled", cancellationToken);

    private async Task<SendReceipt> SendAsync(string chatId, string? text, IReadOnlyList<(HermesStoredImage Image, string DataUrl)> images, string kind,
        CancellationToken cancellationToken)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        await ReadHeldThreadAsync(profile, cancellationToken).ConfigureAwait(false);
        var permission = Permission(chatId);
        if (!permission.Allowed) throw new SendException(false, permission.Reason);
        if (text?.Length > MaxTextLength) throw new SendException(false, "That message is too long for Hermes.");
        var setting = settings.Profiles.First(p => p.Name == profile);
        var address = HermesClient.ParseBase(settings.Address) ?? throw new SendException(false, "Hermes isn't set up. Add its address in Settings.");
        string sessionId;
        TurnReports turn;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var thread = Thread(profile);
            sessionId = thread.SessionId;
            // The unanswered reports go to Hermes with this message; the thread keeps only what the owner typed (HermesTurnReports).
            turn = Carry(thread, kind, string.IsNullOrEmpty(text) ? null : text);
        }
        finally { gate.Release(); }
        var outgoing = new HermesStoredMessage(NewMessageId(profile), clock.GetUtcNow(), true, string.IsNullOrEmpty(text) ? null : text, kind, "sent",
            images.Count > 0 ? [.. images.Select(i => i.Image)] : null);
        var accepted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // In the thread from the start, so the receipt resolves at once; taken out again if Hermes refuses before the send answers.
        await UpdateAsync(profile, outgoing).ConfigureAwait(false);
        // The turn outlives the caller: the reply streams in after the send has answered.
        var generation = Volatile.Read(ref this.generation);
        _ = Task.Run(() => TurnAsync(profile, chatId, address, setting.Key, sessionId, outgoing, turn, [.. images.Select(i => i.DataUrl)], accepted, generation));
        try { await Task.WhenAny(accepted.Task, Task.Delay(AcceptWait, clock, cancellationToken)).ConfigureAwait(false); }
        finally
        {
            // Answered: from here a refusal marks the message not sent in the thread instead (TurnAsync).
            accepted.TrySetResult(null);
        }
        if (accepted.Task.Result is HermesException refused)
        {
            await RemoveAsync(profile, outgoing.Id).ConfigureAwait(false);
            realtime.TryWrite(new(RealtimeKind.MessageDeleted, chatId, [outgoing.Id], false, clock.GetUtcNow()));
            throw new SendException(false, Sentence(refused.Failure));
        }
        return new(chatId, outgoing.Id);
    }

    private async Task RemoveAsync(string profile, string messageId)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var thread = Thread(profile);
            threads[profile] = thread with { Messages = [.. thread.Messages.Where(m => m.Id != messageId)] };
            await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task TurnAsync(string profile, string chatId, Uri address, string key, string sessionId, HermesStoredMessage outgoing, TurnReports turn,
        IReadOnlyList<string> images, TaskCompletionSource<Exception?> accepted, int generation)
    {
        Interlocked.Increment(ref turnsRunning);
        try
        {
            // Once more at most, in a new session, when Hermes couldn't continue this one (HermesBackend.Restart): a retry never asks for another.
            var retrying = false;
            while (await OneTurnAsync(profile, chatId, address, key, sessionId, outgoing, turn, images, accepted, generation, retrying).ConfigureAwait(false) is { } next)
            {
                (sessionId, turn) = next;
                retrying = true;
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"hermes turn failed error={failure.GetType().Name}");
            // Whatever broke may have come after Hermes took the turn: uncertain, never a refusal.
            accepted.TrySetResult(null);
            try { await UncertainAsync(profile, chatId, outgoing).ConfigureAwait(false); } catch (Exception) { }
        }
        finally
        {
            Interlocked.Decrement(ref turnsRunning);
            Typing(chatId, null, stopped: true);
        }
    }

    // One request for the turn. Returns the session and what to carry when the turn is to be sent once more in a new session, else null.
    private async Task<(string SessionId, TurnReports Turn)?> OneTurnAsync(string profile, string chatId, Uri address, string key, string sessionId,
        HermesStoredMessage outgoing, TurnReports turn, IReadOnlyList<string> images, TaskCompletionSource<Exception?> accepted, int generation, bool retrying)
    {
        var started = clock.GetTimestamp();
        HttpResponseMessage? response = null;
        try
        {
            try { response = await client.OpenAsync(address, profile, key, sessionId, turn.Prompt, images, stop.Token).ConfigureAwait(false); }
            catch (HermesException failure) when (failure.Failure == HermesFailure.Unknown)
            {
                // The request may have reached Hermes, and a turn can run tools: the message stays, said as uncertain, and is never sent again.
                Saw(profile, failure.Failure, generation: generation);
                log($"hermes send outcome=unknown kind={outgoing.Kind} profile={Tag(profile)} code={failure.Code}");
                accepted.TrySetResult(null);
                await UncertainAsync(profile, chatId, outgoing).ConfigureAwait(false);
                return null;
            }
            catch (HermesException failure)
            {
                Saw(profile, failure.Failure, generation: generation);
                log($"hermes send outcome=refused kind={outgoing.Kind} profile={Tag(profile)} failure={failure.Failure} code={failure.Code}");
                // Refused before the send answered: the composer keeps the message as not sent, and the thread says why.
                if (accepted.TrySetResult(failure)) { await NoteAsync(profile, chatId, Sentence(failure.Failure)).ConfigureAwait(false); return null; }
                // Refused after the send already answered: the message stays, marked not sent.
                await UpdateAsync(profile, outgoing with { State = "failed" }).ConfigureAwait(false);
                realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [outgoing.Id], false, clock.GetUtcNow()));
                await NoteAsync(profile, chatId, Sentence(failure.Failure) + " Your message wasn't delivered.").ConfigureAwait(false);
                return null;
            }
            Saw(profile, null, generation: generation);
            if (HermesClient.SessionOf(response) is { } echoed && echoed != sessionId)
            {
                await MovedAsync(profile, sessionId, echoed).ConfigureAwait(false);
                sessionId = echoed;
            }
            // Kept from the start (SendAsync); accepted now.
            accepted.TrySetResult(null);
            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [outgoing.Id], false, clock.GetUtcNow()));
            realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
            return await StreamReplyAsync(profile, chatId, address, key, response, started, outgoing, turn, sessionId, generation, retrying).ConfigureAwait(false);
        }
        finally
        {
            response?.Dispose();
            await SettledAsync(profile, turn).ConfigureAwait(false);
        }
    }

    // Hermes answered under another session id than the one sent: it moved the conversation, so the next turn follows it. Not when the thread started
    // over while this turn ran: the new conversation's id wins.
    private async Task MovedAsync(string profile, string sent, string echoed)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var thread = Thread(profile);
            if (thread.SessionId != sent) return;
            threads[profile] = thread with { SessionId = echoed };
            await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        log($"hermes session_moved profile={Tag(profile)}");
    }

    private async Task UncertainAsync(string profile, string chatId, HermesStoredMessage outgoing)
    {
        await UpdateAsync(profile, outgoing with { State = "unknown" }).ConfigureAwait(false);
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [outgoing.Id], false, clock.GetUtcNow()));
        await NoteAsync(profile, chatId, "Hermes stopped answering. Your message may not have reached it.").ConfigureAwait(false);
    }

    // `turn`: the reports and the handoff this turn carried count as answered on the stream's first evidence that Hermes took it (more than one piece of
    // text, tool progress or a finished reply). The send's own answer is no evidence: it comes after AcceptWait whether Hermes confirmed or not. A lone first
    // piece of text waits for the end, since a Hermes without the failure line writes its failure message that way. A refusal in the stream, a failure
    // before any such event or an uncertain end answers nothing, so the next message carries them again. Returns the new session and what to carry there
    // when the turn is to be sent once more because Hermes couldn't continue this session (HermesBackend.Restart), else null.
    private async Task<(string SessionId, TurnReports Turn)?> StreamReplyAsync(string profile, string chatId, Uri address, string key, HttpResponseMessage response,
        long started, HermesStoredMessage outgoing, TurnReports turn, string sessionId, int generation, bool retrying)
    {
        var kind = outgoing.Kind;
        var carried = turn.Through is not null || turn.Handoff is not null;
        async Task TookAsync()
        {
            if (!carried) return;
            carried = false;
            await AnsweredAsync(profile, turn).ConfigureAwait(false);
        }
        HermesStoredMessage? reply = null;
        var words = new System.Text.StringBuilder();
        var shownAt = 0L;
        var chars = 0;
        var deltas = 0;
        string finish = "interrupted";
        string? error = null;
        int? status = null;
        // Hermes showed it ran a tool or asked to: the turn may have changed something, so it is never sent again by itself.
        var acted = false;
        // After Hermes said the turn failed (HermesTurnFailing), text is its own failure message: held, and shown at the end only if the session stays.
        var failing = false;
        var failureText = new System.Text.StringBuilder();
        var approvals = new List<Held>();
        Typing(chatId, null, stopped: false);
        try
        {
            await foreach (var piece in HermesClient.ReadAsync(response, stop.Token).ConfigureAwait(false))
            {
                switch (piece)
                {
                    case HermesProgress progress:
                        acted = true;
                        await TookAsync().ConfigureAwait(false);
                        // Hermes's tool line never replaces the wait for an answer: the tool it names is the one waiting.
                        if (!approvals.Any(a => held.ContainsKey(a.MessageId))) Typing(chatId, progress.Status.Length > 0 ? progress.Status : null, stopped: false);
                        break;
                    case HermesApprovalRequest request:
                        acted = true;
                        await TookAsync().ConfigureAwait(false);
                        // What Hermes said before it asked stays above the request, whole; what it says after (the command's outcome) starts a reply below it.
                        if (reply is not null)
                        {
                            reply = reply with { Text = words.ToString().Trim(), State = "done" };
                            await UpdateAsync(profile, reply).ConfigureAwait(false);
                            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [reply.Id], false, clock.GetUtcNow()));
                            chars += words.Length;
                            reply = null;
                            words.Clear();
                        }
                        approvals.Add(await HoldAsync(profile, chatId, request).ConfigureAwait(false));
                        break;
                    case HermesTurnFailing:
                        failing = true;
                        break;
                    case HermesDelta delta when failing:
                        failureText.Append(delta.Text);
                        break;
                    case HermesDelta delta:
                        if (++deltas > 1) await TookAsync().ConfigureAwait(false);
                        words.Append(delta.Text);
                        var now = clock.GetTimestamp();
                        if (reply is null)
                        {
                            reply = new(NewMessageId(profile), clock.GetUtcNow(), false, words.ToString().TrimStart(), State: "streaming");
                            await UpdateAsync(profile, reply, save: false).ConfigureAwait(false);
                            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [reply.Id], false, clock.GetUtcNow()));
                            shownAt = now;
                        }
                        else if (clock.GetElapsedTime(shownAt, now) >= FillEvery)
                        {
                            reply = reply with { Text = words.ToString().TrimStart() };
                            await UpdateAsync(profile, reply, save: false).ConfigureAwait(false);
                            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [reply.Id], false, clock.GetUtcNow()));
                            shownAt = now;
                        }
                        Typing(chatId, null, stopped: true);
                        break;
                    case HermesFinished end:
                        finish = end.Reason; error = end.Error; status = end.ProviderStatus;
                        break;
                }
            }
        }
        finally { await TurnEndedAsync(approvals).ConfigureAwait(false); }
        var fault = SessionFault(finish, status);
        // Whether the turn ran a tool, or may have: the stream says so when Hermes sends tool progress, and Hermes's own record of the session says so
        // either way. Without a certain no, it counts as having run one.
        var ran = !fault || acted || approvals.Count > 0
            || await client.LatestTurnRanToolsAsync(address, profile, key, sessionId, stop.Token).ConfigureAwait(false) != false;
        var moved = fault
            ? await RestartAsync(profile, chatId, sessionId, outgoing, status!.Value, generation, marked: !retrying, retry: !ran && !retrying).ConfigureAwait(false)
            : null;
        var restart = moved is not null;
        // Hermes's failure message shows when the session stays; when it restarts, the thread's own lines say what happened instead.
        if (!restart) words.Append(failureText);
        else if (!failing && !ran && reply is not null && deltas == 1)
        {
            // Without the failure line (an older Hermes), the failure message streamed as a reply: it goes, as the restart replaces it.
            await RemoveAsync(profile, reply.Id).ConfigureAwait(false);
            realtime.TryWrite(new(RealtimeKind.MessageDeleted, chatId, [reply.Id], false, clock.GetUtcNow()));
            reply = null;
            words.Clear();
        }
        chars += words.Length;
        // A reply that reached the model's output limit ("length") was taken but is not whole: kept, and the thread says it was cut off. A turn that ran
        // a tool was taken too, however it ended.
        var complete = finish == "stop";
        if (finish is "stop" or "length" || (restart ? ran : deltas > 0)) await TookAsync().ConfigureAwait(false);
        if (reply is not null || words.Length > 0)
        {
            reply = (reply ?? new(NewMessageId(profile), clock.GetUtcNow(), false, null)) with { Text = words.ToString().Trim(), State = complete ? "done" : "interrupted" };
            await UpdateAsync(profile, reply).ConfigureAwait(false);
            // Told as new only now, whole: a notification says the reply, not its first words.
            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [reply.Id], true, clock.GetUtcNow()));
            realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        }
        log($"hermes reply outcome={finish} kind={kind} profile={Tag(profile)} chars={chars} ms={(long)clock.GetElapsedTime(started).TotalMilliseconds}"
            + (error is null ? "" : $" code={error}") + (status is { } s ? $" status={s.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ""));
        if (restart)
        {
            if (!ran && !retrying) return moved;
            if (ran)
                // Hermes took it and may have done part of it: the message stays sent, and the owner decides whether to send it again.
                await NoteAsync(profile, chatId, SendAgain).ConfigureAwait(false);
            else
            {
                // The same refusal in a new session, before any tool: the message itself is what Hermes can't take. It stays, marked not sent.
                await UpdateAsync(profile, outgoing with { State = "failed" }).ConfigureAwait(false);
                realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [outgoing.Id], false, clock.GetUtcNow()));
                await NoteAsync(profile, chatId, NotAnswered).ConfigureAwait(false);
            }
            return null;
        }
        if (finish == "error" && error == "admission_conflict")
        {
            // Hermes said in the stream that it did not take this turn: the prompt is marked not sent, as a refusal before the stream would be.
            await UpdateAsync(profile, outgoing with { State = "failed" }).ConfigureAwait(false);
            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [outgoing.Id], false, clock.GetUtcNow()));
        }
        if (!complete)
            await NoteAsync(profile, chatId, finish switch
            {
                "cancelled" => "Hermes stopped this reply.",
                "length" => "Hermes's reply was cut off at the model's length limit.",
                "error" when error == "admission_conflict" => "Hermes was busy with another message in this conversation. Send it again.",
                "error" => "Hermes couldn't finish its reply.",
                _ => reply is null ? "Hermes stopped answering before it replied." : "Hermes stopped before finishing its reply.",
            }).ConfigureAwait(false);
        return null;
    }

    private async Task NoteAsync(string profile, string chatId, string words)
    {
        var note = new HermesStoredMessage(NewMessageId(profile), clock.GetUtcNow(), false, words, Kind: "note", State: "done");
        await UpdateAsync(profile, note).ConfigureAwait(false);
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [note.Id], false, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
    }

    // Adds the message or replaces it by id, and saves the thread unless a reply is still filling in.
    private async Task UpdateAsync(string profile, HermesStoredMessage message, bool save = true)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var thread = Thread(profile);
            var list = thread.Messages.ToList();
            var at = list.FindIndex(m => m.Id == message.Id);
            if (at >= 0) list[at] = message; else list.Add(message);
            threads[profile] = thread with { Messages = list };
            if (save) await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static string Sentence(HermesFailure failure) => failure switch
    {
        HermesFailure.KeyRejected => "Hermes rejected this profile's API key. Change it in Settings.",
        HermesFailure.NoProfile => "Hermes doesn't have this profile at its address. Check it in Settings.",
        HermesFailure.Busy => "Hermes is busy. Try again in a moment.",
        HermesFailure.Conflict => "Hermes is still working on another message in this conversation. Try again when it has replied.",
        HermesFailure.Refused => "Hermes couldn't accept that message.",
        HermesFailure.ServerError => "Hermes ran into a problem. Try again later.",
        HermesFailure.Unknown => "Hermes stopped answering. Your message may not have reached it.",
        _ => "Hermes isn't reachable.",
    };

    // ---- pictures ----

    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default) => GetSendPermissionAsync(chatId, cancellationToken);

    public async Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
    {
        var permission = await GetSendPermissionAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!permission.Allowed) return permission;
        if (!ImageTypes.Contains(file.MimeType, StringComparer.OrdinalIgnoreCase) || file.IsVoiceNote)
            return new SendPermission(false, "Hermes takes pictures only.");
        return file.Length > MaxImageBytes ? new SendPermission(false, "That picture is too large for Hermes.") : permission;
    }

    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AttachmentCaptionSupport(true, MaxTextLength));

    public async Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
    {
        var permission = await GetFilePermissionAsync(chatId, file, cancellationToken).ConfigureAwait(false);
        if (!permission.Allowed) throw new SendException(false, permission.Reason);
        byte[] bytes;
        await using (var source = await file.OpenRead(cancellationToken).ConfigureAwait(false))
        using (var copy = new MemoryStream())
        {
            await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            if (copy.Length > MaxImageBytes) throw new SendException(false, "That picture is too large for Hermes.");
            bytes = copy.ToArray();
        }
        var id = HermesStore.NewMediaId();
        await store.SaveMediaAsync(id, bytes).ConfigureAwait(false);
        var mime = file.MimeType.ToLowerInvariant();
        var image = new HermesStoredImage(id, mime, bytes.Length);
        return await SendAsync(chatId, caption, [(image, $"data:{mime};base64,{Convert.ToBase64String(bytes)}")], "message", cancellationToken).ConfigureAwait(false);
    }

    public bool CanSave(MessageAttachment attachment) => MediaId(attachment) is not null;

    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadImageAsync(attachment, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default)
    {
        var id = MediaId(attachment) ?? throw new BackendException(BackendFailure.Unsupported, "This picture isn't Hermes's.");
        return await store.ReadMediaAsync(id).ConfigureAwait(false) ?? throw new BackendException(BackendFailure.Unavailable, "This picture is no longer kept.");
    }

    public static bool OwnsMedia(MessageAttachment attachment) => MediaId(attachment) is not null;
    private static string? MediaId(MessageAttachment attachment) =>
        (attachment.Id ?? attachment.SourceUrl) is { } value && value.StartsWith(HermesIds.MediaPrefix, StringComparison.Ordinal)
        && HermesStore.ValidMediaId(value[HermesIds.MediaPrefix.Length..]) ? value[HermesIds.MediaPrefix.Length..] : null;

    // ---- typing: Hermes working ----

    public bool CanReceive => Configured;
    public bool CanSend => false;
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await typing.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (typing.Reader.TryRead(out var update)) yield return update;
    }

    private void Typing(string chatId, string? status, bool stopped)
    {
        var profile = HermesIds.ProfileOf(chatId)!;
        typing.Writer.TryWrite(new(chatId, stopped ? [] :
            [new(AgentId(profile), Title(profile), clock.GetUtcNow().AddMinutes(2), status)]));
    }

    // ---- the realtime feed and the checks ----

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        await LoadAsync().ConfigureAwait(false);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        var checks = Task.Run(() => CheckLoopAsync(both.Token), both.Token);
        try { await realtime.DrainAsync(receive, both.Token).ConfigureAwait(false); }
        finally
        {
            both.Cancel();
            try { await checks.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Checks every profile once, now. The sample server does this before it opens, so its first clients see settled rows.</summary>
    internal async Task CheckAllAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        if (HermesClient.ParseBase(settings.Address) is not { } address) return;
        foreach (var profile in settings.Profiles)
        {
            var generation = Volatile.Read(ref this.generation);
            try { Saw(profile.Name, null, await client.ProbeAsync(address, profile.Name, profile.Key, cancellationToken).ConfigureAwait(false), generation); }
            catch (HermesException failure) { Saw(profile.Name, failure.Failure, generation: generation); }
        }
    }

    // Each profile is asked at /v1/models when it was never checked, every CheckEvery while it answers, and on RetrySteps while it
    // does not. A refused key or a missing profile is not asked again until its settings change: the answer would be the same.
    private async Task CheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var woken = Volatile.Read(ref checkNow).Task;
            var now = clock.GetUtcNow();
            var address = HermesClient.ParseBase(settings.Address);
            var generation = Volatile.Read(ref this.generation);
            var due = address is null ? [] : settings.Profiles.Where(p => !statuses.TryGetValue(p.Name, out var s) || s.Word is not ("key_rejected" or "no_profile") && s.NextCheck <= now).ToArray();
            foreach (var profile in due)
            {
                // One check at a time, each bounded: a Hermes that never answers is unreachable, not a stall for every check after it. Saving new
                // settings cancels the one in flight (Superseded), so the new ones are checked at once.
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pass.CancelAfter(ProbeTimeout);
                Volatile.Write(ref checking, pass);
                try
                {
                    var title = await client.ProbeAsync(address!, profile.Name, profile.Key, pass.Token).ConfigureAwait(false);
                    Saw(profile.Name, null, title, generation);
                }
                catch (HermesException failure) { Saw(profile.Name, failure.Failure, generation: generation); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (generation != Volatile.Read(ref this.generation)) break;
                    Saw(profile.Name, HermesFailure.Unreachable, generation: generation);
                }
                finally { Interlocked.CompareExchange(ref checking, null, pass); }
            }
            var waits = settings.Profiles.Select(p => statuses.TryGetValue(p.Name, out var s) && s.Word is not ("key_rejected" or "no_profile") ? s.NextCheck - clock.GetUtcNow() : TimeSpan.MaxValue)
                .Where(w => w != TimeSpan.MaxValue).DefaultIfEmpty(TimeSpan.FromHours(1)).Min();
            var wait = waits < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : waits > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : waits;
            try { await Task.WhenAny(woken, Task.Delay(wait, clock, cancellationToken)).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // What a request said of its profile. The title is kept from the last answer that named one.
    private void Saw(string profile, HermesFailure? failure, string? title = null, int generation = -1)
    {
        if (generation >= 0 && generation != Volatile.Read(ref this.generation)) return;
        var before = statuses.TryGetValue(profile, out var was) ? was : null;
        var word = failure switch
        {
            null => "connected",
            HermesFailure.KeyRejected => "key_rejected",
            HermesFailure.NoProfile => "no_profile",
            HermesFailure.Unreachable or HermesFailure.ServerError or HermesFailure.Unknown => "unreachable",
            // Busy, conflict or a refused turn say Hermes answered.
            _ => "connected",
        };
        var failures = word == "unreachable" ? (before?.Failures ?? 0) + 1 : 0;
        var next = clock.GetUtcNow() + (word == "unreachable" ? RetrySteps[Math.Min(failures, RetrySteps.Count) - 1] : CheckEvery);
        var status = new Status(word, title ?? before?.Title, next, failures);
        statuses[profile] = status;
        if (before?.Word == word && before.Title == status.Title) return;
        log($"hermes profile state={word} profile={Tag(profile)} was={before?.Word ?? "unchecked"}");
        var chatId = HermesIds.Chat(profile);
        if (word == "connected") _ = GreetAsync(profile, chatId, generation >= 0 ? generation : Volatile.Read(ref this.generation));
        // No Connected, Disconnected or AuthenticationRequired event: a client reads those as a service connection (a sign-in banner, a phone's full
        // re-read on catch-up). How a profile stands travels in the host's state (HermesAccountState) and, when a turn fails, as a line in its thread.
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        Raise();
    }

    // A profile's first answer, while its thread is still empty, leaves a line of ours there, so the conversation has an activity and comes to the top of every
    // client's list (owner, 2026-11-15: an empty one sorted last, found only by filtering). Saved in the thread, so it happens once: a restart's first check finds
    // the line, and a send or a new conversation fills the thread first. No Hermes turn is spent on it.
    internal const string Greeting = "Connected to Hermes.";

    // Settings saved since the answer (a new key or address, never yet answered) make it not this profile's first answer any more: checked again under the
    // gate, which a save holds (code review, 2026-11-15).
    private async Task GreetAsync(string profile, string chatId, int generation)
    {
        try
        {
            HermesStoredMessage note;
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                // A thread whose file couldn't be read looks empty here, and a greeting saved over it took everything it held.
                if (generation != Volatile.Read(ref this.generation) || !Has(profile) || store.ThreadHeld(profile) || Thread(profile).Messages.Count > 0) return;
                note = new(NewMessageId(profile), clock.GetUtcNow(), false, Greeting, Kind: "note", State: "done");
                var greeted = Thread(profile) with { Messages = [note], ReadUpTo = note.At };
                await store.SaveThreadAsync(greeted).ConfigureAwait(false);
                threads[profile] = greeted;
            }
            finally { gate.Release(); }
            log($"hermes greeted profile={Tag(profile)}");
            realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [note.Id], false, clock.GetUtcNow()));
            realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { log($"hermes greet failed error={failure.GetType().Name}"); }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener must not stop Hermes */ }
    }

    // ---- mapping ----

    private HermesThread Thread(string profile) => threads.TryGetValue(profile, out var thread) ? thread : threads[profile] = NewThread(profile);
    private HermesThread NewThread(string profile) => new(profile, NewSessionId(), []);

    /// <summary>A session id in Hermes's own shape (<c>YYYYMMDD_HHMMSS_&lt;8 hex&gt;</c>), made here so the thread owns it from its first turn.</summary>
    private string NewSessionId() => clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss") + "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private static string NewMessageId(string profile) => HermesIds.Chat(profile) + ":" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private static string AgentId(string profile) => HermesIds.Chat(profile) + ":agent";
    private static string SelfId(string profile) => HermesIds.Chat(profile) + ":me";

    // The profile's own name as a conversation's, "Hermes" for the default (HermesThreads.DefaultTitle), which a person can rename here. The model Hermes
    // reports stays out of it; a model id read like a code, not a conversation.
    public static string DefaultTitle(string profile) => NativeBeeper.Presentation.Services.HermesThreads.DefaultTitle(profile);

    // The name the owner gave the profile on the server's page, else the default (owner, 2026-11-15: a profile name is lowercase with no spaces, which
    // reads poorly as a conversation's). It names the thread and Hermes's side of it, so a rename reaches the messages already there.
    private string Title(string profile) => settings.Profiles.FirstOrDefault(p => p.Name == profile)?.Title ?? DefaultTitle(profile);

    private static string Profile(string chatId) => HermesIds.ProfileOf(chatId) is { } profile && HermesClient.ValidProfile(profile) ? profile
        : throw new BackendException(BackendFailure.Unsupported, "That isn't a Hermes conversation.");

    private ChatSummary Summary(string profile)
    {
        if (!Has(profile)) throw new BackendException(BackendFailure.Unavailable, "This Hermes profile isn't set up.");
        var thread = Thread(profile);
        var title = Title(profile);
        var last = thread.Messages.LastOrDefault();
        var unread = thread.Messages.Count(m => !m.Outgoing && m.Kind is "message" or "approval" && m.State != "streaming" && (thread.ReadUpTo is null || m.At > thread.ReadUpTo));
        var chatId = HermesIds.Chat(profile);
        return new(chatId, chatId + ":account", title, HermesIds.Network, "single", last?.At, unread, false, false, false, false, null,
            last is null ? null : Map(profile, last), [], null, null,
            new ChatCapabilities(null, null, null, null, false, false, MaxTextLength),
            [new(AgentId(profile), title, IsSelf: false), new(SelfId(profile), "You", IsSelf: true)], 2, ReadUpTo: thread.ReadUpTo);
    }

    private ChatMessage Map(string profile, HermesStoredMessage message)
    {
        var chatId = HermesIds.Chat(profile);
        var attachments = (message.Images ?? []).Select(image => new MessageAttachment(null, "img", image.MimeType, image.Size, false,
            image.MimeType == "image/gif", false, HermesIds.MediaPrefix + image.Id, HermesIds.MediaPrefix + image.Id, image.Width, image.Height)).ToArray();
        var type = message.Kind switch { "note" => "system", "scheduled" => "scheduled", "approval" => "approval", _ => attachments.Length > 0 ? "img" : null };
        // The shared words every client reads: SUCCESS is Sent, FAILED is Failed.
        var sendState = !message.Outgoing ? null : message.State switch { "failed" => "FAILED", "unknown" => null, _ => "SUCCESS" };
        return new(message.Id, chatId, message.Outgoing ? SelfId(profile) : AgentId(profile),
            message.Outgoing ? null : Title(profile),
            message.At, message.At.UtcTicks.ToString("D19"), message.Text, type, message.Outgoing, false, false, null, null, attachments, [],
            new MessageDelivery(sendState, null, null, null, new Dictionary<string, bool?>()), Approval: message.Kind == "approval" ? message.Approval : null);
    }

    /// <summary>A short one-way tag for a profile in the logs: never its name.</summary>
    internal static string Tag(string profile) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("hermes:" + profile)))[..8];

    public async Task CloseAsync()
    {
        stop.Cancel();
        // A reply still streaming keeps what came; the next run shows it as it was.
        for (var i = 0; i < 20 && Volatile.Read(ref turnsRunning) > 0; i++) await Task.Delay(50).ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (profile, thread) in threads.ToArray())
                if (thread.Messages.Any(m => m.State == "streaming"))
                {
                    threads[profile] = thread with { Messages = [.. thread.Messages.Select(m => m.State == "streaming" ? m with { State = "interrupted" } : m)] };
                    await store.SaveThreadAsync(threads[profile]).ConfigureAwait(false);
                }
        }
        finally { gate.Release(); }
    }

    public void Dispose() => stop.Cancel();
}
