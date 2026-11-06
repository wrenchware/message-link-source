using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Storage;

namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// Instagram Direct. Conversations, participants, history and media come through the instagram-helper process, and
/// so do sending, attachments, replying, reacting, editing, taking a message back, marking read, typing in both
/// directions, finding somebody to write to and opening the conversation with them. Changing a conversation.s
/// settings and accepting a message request do not exist there.
/// </summary>
/// <remarks>
/// What is not implemented is absent rather than disabled: those methods do not exist on the other side of the pipe,
/// and the helper's own diagnostic mode cannot mutate at all, so the stage-2 and stage-3 probes stay provably
/// incapable of changing the account even though the application can now send.
///
/// Starting a conversation is a lookup rather than a creation, because that is all Instagram offers: see
/// <see cref="InstagramBackend"/>'s directory half for what that means for a pair who have never written to
/// each other.
/// </remarks>
internal sealed partial class InstagramBackend : IBeeperBackend, IChatReader, IRealtimeFeed, IMessageSender, IReadStateWriter, IMessageActions, IMessageMutations, IAttachmentBackend, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ListFreshness = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly InstagramSessionStore store;
    // Bounded and dropping the oldest; a drop reaches the reader as a resync.
    private readonly RealtimeBuffer realtime = new(256, line => InstagramLog.Write("instagram " + line));

    private HelperConnection? helper;
    private InstagramIdentity? identity;
    private InstagramChat[] chats = [];
    private DateTimeOffset chatsReadAt;
    private bool restored;
    private volatile bool ready;
    // Set when the app puts Instagram down on purpose — signing out or quitting — so a deliberate close does not look
    // like a helper that fell over. Only a start the user asked for clears it; an automatic one refuses to run.
    private volatile bool closing;
    private volatile bool disposed;
    // Whether something will reconnect Instagram without the user: the launch restore while it runs, or a recovery
    // scheduled after a helper stopped. True from the start when a session is saved, so a conversation opened at launch
    // says "reconnecting" from the first moment rather than "not connected" and then "reconnecting".
    private volatile bool reconnectComing;
    // The newest message the poll has already considered in each conversation. The poll's alone: the list's own reads
    // replace `chats`, and an arrival they absorbed was one the poll never saw move.
    private Dictionary<string, InstagramNewest> considered = new(StringComparer.Ordinal);
    // The list's copies of arrivals that are not text, answered to whoever asks what came while nothing shows them.
    private readonly InstagramGlimpses glimpses = new();
    // Each conversation's newest page as this connection last read it, for an opening to draw at once (ReadHeldAsync).
    private readonly InstagramHeldPages heldPages = new();
    // The code Instagram refused the session with, which stopped the poll. Cleared by the next connection.
    private volatile string? refused;

    private static string DefaultRoot => AppPaths.Combine("Instagram");
    private readonly IAppPresence presence;

    public InstagramBackend(IStateProtector protector, IAppPresence appPresence, InstagramSessionStore? sessions = null)
    {
        presence = appPresence;
        store = sessions ?? new InstagramSessionStore(DefaultRoot, protector);
        // The limits kept from earlier runs first: a session held after an account check is not reconnected by anything but a new sign-in.
        LoadGuard();
        reconnectComing = store.HasSession && refused is null;
    }

    /// <summary>Whether Instagram is signed in, for the startup decisions made before a backend is built.</summary>
    public static bool AnySavedSession => InstagramSessionStore.Exists(DefaultRoot);

    public event EventHandler? ChatsChanged;

    /// <summary>
    /// Raised when nothing will reconnect Instagram any more without the user, so what an open conversation says about
    /// sending can change from "reconnecting" to "not connected". Raised on whichever thread decided it.
    /// </summary>
    internal event EventHandler? ReconnectAbandoned;

    /// <summary>
    /// Whether an automatic reconnect is on its way. The launch restore owns it while it runs; a helper that stops by
    /// itself decides it from whether it will be recovered.
    /// </summary>
    internal void ExpectReconnect(bool coming)
    {
        var was = reconnectComing;
        reconnectComing = coming;
        if (was && !coming && !IsConnected)
        {
            WaitEnded("abandoned");
            ReconnectAbandoned?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Why the last resume found no session to use, as one fixed word from the store, or null when it found one. Only
    /// meaningful straight after <see cref="ResumeAsync"/> answered -1.
    /// </summary>
    internal string? SavedSessionProblem { get; private set; }

    public bool IsConnected => ready && helper is not null && identity is not null;

    /// The account this session is signed in as, named by <see cref="InstagramAccount"/>. Null until a session
    /// starts, and null rather than a guess where Instagram named neither.
    internal string? AccountName { get; private set; }
    public bool HasSavedSession => store.HasSession;

    private static string HelperPath => HelperExecutables.PathFor("instagram-helper");

    public Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new BackendInfo("Instagram", "instameow", true));

    public Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChatAccount>>(identity is null ? []
            : [new(identity.Account, InstagramIdentity.Network, "Instagram", IsConnected ? "connected" : "disconnected",
                identity.Account, "instagram")]);

    /// <summary>Signs in with cookies just captured, and remembers the session so the next launch does not ask again.</summary>
    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default) =>
        StartAsync(new { cookies }, false, cancellationToken);

    // A resume Instagram answers with a resnapshot is loaded again from its cookies alone, once per launch at most.
    private int reloadedFromCookies;

    /// <summary>Resumes the saved session. Returns -1 when there is none this Windows user can read.</summary>
    /// <param name="automatic">
    /// True for the launch restore and for recovery, neither of which may undo a close somebody asked for.
    /// </param>
    public Task<int> ResumeAsync(bool automatic = false, CancellationToken cancellationToken = default) =>
        ResumeAsync(StartLockedAsync, automatic, cancellationToken);

    // The login hook lets the offline contracts use a store-only login in place of a provider executable.
    internal async Task<int> ResumeAsync(Func<object, bool, CancellationToken, Task<int>> start, bool automatic, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        int shown;
        try { shown = await ResumeLockedAsync(start, automatic, cancellationToken); }
        finally { gate.Release(); }
        if (shown >= 0) ChatsChanged?.Invoke(this, EventArgs.Empty);
        return shown;
    }

    private async Task<int> ResumeLockedAsync(Func<object, bool, CancellationToken, Task<int>> start, bool automatic, CancellationToken cancellationToken)
    {
        // Recovery calls this backend directly. Read and use the session under the same lock as sign-out and fresh sign-in, including the cookies-only fallback,
        // so a resume waiting behind account removal/replacement cannot bring the previous account back.
        var (saved, problem) = await store.ReadSessionAsync();
        SavedSessionProblem = problem;
        if (saved is null) return -1;
        try
        {
            RefuseResume(automatic);
            using var document = JsonDocument.Parse(saved);
            try { return await start(new { session = document.RootElement }, automatic, cancellationToken); }
            // The saved cursor is one Instagram will no longer continue from. Upstream answers this with a full
            // reconnect, and so does this: the cookies alone make the helper load the inbox afresh, which gives it a
            // new cursor, where resuming again would be refused the same way every time until the state aged out.
            catch (InvalidOperationException failure) when (failure.Message == "resnapshot_required" && startResumed
                && document.RootElement.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Object
                && Interlocked.Exchange(ref reloadedFromCookies, 1) == 0)
            {
                InstagramLog.Write("instagram connect resnapshot=reloading");
                try
                {
                    var shown = await start(new { cookies }, automatic, cancellationToken);
                    InstagramLog.Write("instagram connect resnapshot=reloaded");
                    return shown;
                }
                catch (Exception) { InstagramLog.Write("instagram connect resnapshot=reload_failed"); throw; }
            }
        }
        // Credential-bearing: it is never logged, printed or attached to an exception.
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(saved); }
    }

    // A resume logs the saved session in again, which is the traffic that gets an account checkpointed, so it answers to the same rules whoever asks:
    // the launch restore, a recovery, the Settings button, or a client of a server (accounts.instagram.resume). Nothing resumes a session Instagram
    // refused (an account check, a consent, a suspension, an ended session) until the person signs in again, which is a new sign-in and releases the hold.
    // A resume somebody asked for also takes one attempt from the recovery budget (three, given back after two connected minutes), as a restart from
    // Connection health does; the launch restore and a recovery have their own pacing and take theirs where they start.
    private void RefuseResume(bool automatic)
    {
        if (RefusedWith() is { } held && InstagramGuardFile.Persistable(held))
        {
            InstagramLog.Write($"instagram resume refused code={held} automatic={automatic}");
            throw new InvalidOperationException(held);
        }
        if (automatic) return;
        var (reached, uptime) = Uptime();
        if (Budget(recovery => recovery.TakeForUser(reached, uptime, null)) is { } spent)
        {
            InstagramLog.Write($"instagram resume refused code={spent} automatic=False");
            throw new BackendException(BackendFailure.RateLimited,
                "Instagram was reconnected 3 times without staying connected for 2 minutes. To protect the account, it is not reconnected again until Messages is restarted.");
        }
    }

    // Whether the start in progress resumed a saved session rather than loading the inbox, from the login answer.
    private bool startResumed;

    /// <summary>The account move's gate (see MessengerBackend.MayConnect): asked under the start lock, so a hold that closed the helper is not undone by a start that had passed the ports' check. Null (the app) lets it.</summary>
    internal Func<bool>? MayStart { get; set; }

    private async Task<int> StartAsync(object login, bool automatic, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        int shown;
        try { shown = await StartLockedAsync(login, automatic, cancellationToken); }
        finally { gate.Release(); }
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return shown;
    }

    private async Task<int> StartLockedAsync(object login, bool automatic, CancellationToken cancellationToken)
    {
        if (!File.Exists(HelperPath))
        {
            InstagramLog.Write($"instagram connect start_failed reason={HelperStartFailure.Missing} win32=none");
            throw new BackendException(BackendFailure.Unavailable, HelperStartFailure.MissingSentence);
        }
        try
        {
            if (MayStart?.Invoke() == false) throw new InvalidOperationException("account_moved_away");
            await CloseCoreAsync();
            startResumed = false;
            heldPages.Clear();
            newestReads.Clear();
            // The list as the app last held it: saved at the end of the last run, or this run's before a helper stopped.
            // What came after it is what the catch-up reads (InstagramBackend.CatchUp), and only for the same account.
            // The launch connects before anything has asked for the list (the app draws its own saved list), so the saved
            // one is read here: assumed instead, the first 0.1.363 launch caught up on nothing (`outcome=no_baseline`).
            if (!restored && readySince is null) await RestoreChatsAsync();
            var previous = chats;
            var previousAccount = identity?.AccountKey;
            // A close asked for while this waited for the gate wins. Only a start somebody asked for clears it, since
            // signing in again after signing out is exactly that; a restore or a recovery never does.
            if (disposed || automatic && closing) throw new OperationCanceledException("Instagram is closing.");
            closing = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var started = await HelperConnection.StartAsync(HelperPath, "--app", cancellationToken);
            started.Scope = "instagram:";
            started.Disconnected += () => Stopped(started);
            started.Received += (kind, data) => { if (kind == "counts" && ReferenceEquals(helper, started)) ObserveRealtime(data); };
            // Typing has its own event, so the frequent counts frames are not cloned for it.
            started.Typing += data => { if (ReferenceEquals(helper, started)) ReceiveTyping(data); };
            lock (realtimeGate) (realtimeState, realtimeFrames) = (null, 0);
            helper = started;
            // The helper took time to start, and the host may have held this account (or the app closed it) meanwhile: a close that came then found no helper to close. Asked again now that
            // there is one, and still under this lock, so a hold's close can no longer be followed by a login. The helper is put down here without a word to the service.
            if (MayStart?.Invoke() == false) throw new InvalidOperationException("account_moved_away");
            if (disposed || closing) throw new OperationCanceledException("Instagram is closing.");
            // A new helper session has handed nothing out yet: what the last one named no longer says a picture is gone.
            NativeBeeper.Presentation.Services.MediaNews.Forget("instagram:");
            var hello = await started.CallAsync("hello", null, cancellationToken);
            if (hello.GetProperty("protocol").GetInt32() != 2) throw new BackendException(BackendFailure.Unsupported, HelperStartFailure.OutOfDateSentence);
            var helloMs = clock.ElapsedMilliseconds;
            var signedIn = await started.CallAsync("login", login, cancellationToken);
            startResumed = signedIn.ValueKind == JsonValueKind.Object && signedIn.TryGetProperty("resumed", out var resumed)
                && resumed.ValueKind == JsonValueKind.True;
            var loginMs = clock.ElapsedMilliseconds;
            // Who the session belongs to, for Settings to show. The helper reads it from the viewer record it has just
            // validated the account against, on a fresh login and on a resume alike, and it is deliberately absent from
            // the counts frame pushed on every event — so this answer is the only place it arrives.
            AccountName = InstagramAccount.Named(signedIn);
            var session = await started.CallAsync("session", null, cancellationToken);
            identity = new InstagramIdentity(session.GetProperty("accountKey").GetString()
                ?? throw new BackendException(BackendFailure.InvalidResponse, "Instagram did not say which account this is."));
            if (previousAccount != identity.AccountKey) previous = [];
            await SaveSessionAsync(started, session, "login");
            await started.CallAsync("connect", null, cancellationToken);
            var connectMs = clock.ElapsedMilliseconds;
            // Every page, as a launch, a resume and a recovery always have: the poll after it reads the first page and
            // counts its ten minutes to the next whole read from here.
            chats = await ReadChatsAsync(started, false, "all", cancellationToken);
            chatsReadAt = DateTimeOffset.UtcNow;
            schedule.Swept();
            var chatsMs = clock.ElapsedMilliseconds;
            AdoptReads(chats, announce: true);
            // What a new connection can already see is not news; only what arrives after it is.
            considered = InstagramArrivals.Seed(chats);
            await SaveChatsAsync();
            // The session was saved before connecting, with the cursor it was started from. Connecting has moved it,
            // and a resume from the old one replays everything since the last full sign-in or is refused outright.
            await ResaveSessionAsync(started, "connected", cancellationToken);
            // A helper that stopped while this finished is not a connection, whatever the calls above answered.
            if (!ReferenceEquals(helper, started)) throw new IOException("Helper disconnected.");
            refused = null;
            startRefused = null;
            ReleaseHold();
            readySince = new(started, DateTimeOffset.UtcNow);
            // Connected, so nothing is waiting to reconnect; whether something will when this ends is the stop's to say.
            reconnectComing = false;
            ready = true;
            WaitEnded("connected");
            realtime.Writer.TryWrite(new(RealtimeKind.Connected, ReceivedAt: DateTimeOffset.UtcNow));
            InstagramLog.Write($"instagram connect resumed={startResumed} hello_ms={helloMs} login_ms={loginMs - helloMs} " +
                $"connect_ms={connectMs - loginMs} chats_ms={chatsMs - connectMs} total_ms={clock.ElapsedMilliseconds}");
            _ = CountFirstMinuteAsync(started);
            StartCatchUp(started, previous, chats);
        }
        catch (Exception failure)
        {
            // An answer only the user can change, kept so a restart from Connection health does not put the same
            // question to Instagram again. Only a connection that succeeds clears it.
            var code = InstagramConnectionFailure.Code(failure);
            if (InstagramConnectionFailure.NeedsUser(code)) { startRefused = code; Hold(code); }
            // A start somebody asked for, with nothing else to reconnect after it: whatever waited on it has its answer.
            // A restore or a recovery with tries left keeps waiting, and says how it ended when it gives up.
            if (!reconnectComing) WaitEnded("failed");
            await CloseCoreAsync();
            // Windows would not start the helper: a backend failure that names it, which the restore does not retry
            // (InstagramConnectionFailure.Definitive) and Settings shows as it is. Only the number is logged.
            if (HelperStartFailure.Code(failure) is { } start)
            {
                InstagramLog.Write($"instagram connect start_failed {HelperStartFailure.Fields(failure)}");
                throw new BackendException(BackendFailure.Unavailable, HelperStartFailure.Sentence(start)!);
            }
            throw;
        }
        return Shown().Length;
    }

    // One writer of the saved session at a time, and a sign-out waits for any in flight: two writers share one
    // temporary file, and a save landing after the sign-out removed the account would sign it back in.
    private readonly SemaphoreSlim sessionWrites = new(1, 1);
    private long savedCursor = -1;

    // Saves what this helper holds now, and only while it is the current one: a helper being put down must not write
    // its session back behind whoever put it down.
    private async Task SaveSessionAsync(HelperConnection started, JsonElement session, string after)
    {
        var cursor = session.ValueKind == JsonValueKind.Object && session.TryGetProperty("seqID", out var seq)
            && seq.TryGetInt64(out var value) ? value : 0;
        var plain = Encoding.UTF8.GetBytes(session.GetRawText());
        var saved = false;
        try
        {
            await sessionWrites.WaitAsync();
            try
            {
                if (!ReferenceEquals(helper, started)) return;
                await store.SaveSessionAsync(plain);
                saved = true;
            }
            finally { sessionWrites.Release(); }
        }
        // A session that cannot be saved still works for this run; the next launch signs in again.
        catch (Exception) { }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        if (!saved) return;
        // Whether the cursor the next launch resumes from moved: the whole point of saving again. Zero is the helper
        // having been told to start over, which makes the next launch load the inbox afresh.
        var before = Interlocked.Exchange(ref savedCursor, cursor);
        var moved = cursor <= 0 ? "reset" : before < 0 ? "first" : cursor == before ? "same" : "advanced";
        InstagramLog.Write($"instagram session saved after={after} cursor={moved}");
    }

    // Asks the helper for its session as it stands, which it answers from memory, and saves it. A helper with none to
    // give, or one still busy when the caller's patience runs out, leaves the last one saved in place.
    private async Task<bool> ResaveSessionAsync(HelperConnection started, string after, CancellationToken cancellationToken)
    {
        JsonElement session;
        try { session = await started.CallAsync("session", null, cancellationToken); }
        catch (Exception) { return false; }
        await SaveSessionAsync(started, session, after);
        return true;
    }

    // What the helper's per-event frames say about its realtime connection. Only the fixed state word is read, and it
    // is written down only when it changes: connected, or the code the connection ended with.
    private readonly object realtimeGate = new();
    private string? realtimeState;
    private int realtimeFrames;

    private void ObserveRealtime(JsonElement counts)
    {
        var state = counts.ValueKind == JsonValueKind.Object && counts.TryGetProperty("state", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? changed = null;
        lock (realtimeGate)
        {
            realtimeFrames++;
            if (state is not null && state != realtimeState) changed = realtimeState = state;
        }
        // Instagram ended the session on the realtime connection: held like the poll's refusal, across restarts too.
        if (changed is not null && InstagramConnectionFailure.NeedsUser(changed)) Hold(changed);
        if (changed is not null) InstagramLog.Write($"instagram realtime state={InstagramConnectionFailure.RealtimeState(changed)}");
    }

    // How much a connection had to catch up on: every frame is one event, and a resume from a stale cursor replays
    // everything since it. Near zero once the saved cursor keeps up.
    private async Task CountFirstMinuteAsync(HelperConnection started)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        if (!ReferenceEquals(helper, started)) return;
        int frames;
        lock (realtimeGate) frames = realtimeFrames;
        InstagramLog.Write($"instagram realtime frames_first_minute={frames}");
    }

    private async Task SaveChatsAsync()
    {
        if (identity is null) return;
        var plain = JsonSerializer.SerializeToUtf8Bytes(InstagramArrivals.ForStorage(chats), Json);
        try { await store.SaveChatsAsync(identity.AccountKey, plain); }
        catch (Exception) { /* The list still works from memory this session. */ }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    // Until the first connection is ready, the conversations from last time keep the list steady rather than leaving
    // it empty. A connection that finishes while this is reading supplies a fresher list, which must not be replaced.
    private async Task RestoreChatsAsync()
    {
        restored = true;
        var key = await store.ActiveAccountAsync();
        if (key is null) return;
        identity ??= new InstagramIdentity(key);
        var stored = await store.LoadChatsAsync(key);
        if (stored is null) return;
        try
        {
            var before = chats;
            if (ReferenceEquals(chats, before) && JsonSerializer.Deserialize<InstagramChat[]>(stored, Json) is { } saved)
            {
                chats = saved;
                // Positions last time were true then, and positions only move forward.
                AdoptReads(saved, announce: false);
            }
        }
        catch (Exception) { /* A cache that cannot be read is simply not used. */ }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored); }
    }

    // pages: "all" reads every page of every folder shown, "first" the inbox's first page over the list already held,
    // and null leaves it to the helper, which reads the first page once it has read them all (InstagramPollSchedule).
    private static async Task<InstagramChat[]> ReadChatsAsync(HelperConnection connection, bool refresh, string? pages, CancellationToken cancellationToken)
    {
        var result = await connection.CallAsync("ui.chats", new { count = 300, refresh, pages }, cancellationToken);
        return result.Deserialize<InstagramChat[]>(Json) ?? [];
    }

    // A message request is read but not shown: stage 7 decides how requests appear, and putting one in the inbox
    // before then would say it is an ordinary conversation.
    private InstagramChat[] Shown() => [.. chats.Where(InstagramIdentity.IsShown)];

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // Instagram's folders are small — stage 3 exhausted all three at 86 threads — so the list arrives whole.
        if (olderThan is not null) return new([], false, null, null);
        if (!restored && !IsConnected && HasSavedSession) await RestoreChatsAsync();
        if (IsConnected && helper is { } started)
        {
            try
            {
                if (DateTimeOffset.UtcNow - chatsReadAt > ListFreshness)
                {
                    chats = await ReadChatsAsync(started, false, null, cancellationToken);
                    chatsReadAt = DateTimeOffset.UtcNow;
                    AdoptReads(chats, announce: true);
                    await SaveChatsAsync();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Keep the conversations already known if the helper is momentarily unavailable. */ }
        }
        if (identity is not { } known) return new([], false, null, null);
        return new([.. Shown().Select(known.Map)], false, null, null);
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (!restored && !IsConnected && HasSavedSession) await RestoreChatsAsync();
        if (identity is not { } known) throw NotConnected("read");
        var native = known.NativeChat(chatId);
        // The poll has just read every conversation, and the list asks for each one it was told about. Going back to
        // Instagram for each of those would be a round trip per changed row every cycle, for an answer already in
        // hand; the list is only ever told about a conversation because this read it a moment ago.
        if (DateTimeOffset.UtcNow - chatsReadAt <= ListFreshness
            && chats.FirstOrDefault(chat => chat.Id == native) is { } current) return known.Map(current);
        if (IsConnected && helper is { } started)
        {
            try
            {
                var result = await started.CallAsync("ui.chat", new { chatId = native }, cancellationToken);
                if (result.Deserialize<InstagramChat>(Json) is { } fresh)
                {
                    chats = [.. chats.Where(chat => chat.Id != fresh.Id), fresh];
                    AdoptReads([fresh], announce: true);
                    return known.Map(fresh);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Fall back to the conversation already known. */ }
        }
        return known.Map(chats.FirstOrDefault(chat => chat.Id == native)
            ?? throw (IsConnected
                ? new BackendException(BackendFailure.Unavailable, "That Instagram conversation is not loaded.")
                : NotConnected("read")));
    }

    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // Before anything can fail, and whether or not Instagram is connected yet: a conversation that asked is one that
        // may be showing, whatever it got back.
        glimpses.Opened(chatId);
        return ReadMessagesAsync(chatId, olderThan, cancellationToken);
    }

    private async Task<Page<ChatMessage>> ReadMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken)
    {
        if (identity is not { } known) throw NotConnected("read");
        if (helper is not { } started || !IsConnected) throw NotConnected("read");
        async Task<InstagramMessagePage> AskAsync(CancellationToken token)
        {
            var result = await started.CallAsync("ui.messages",
                new { chatId = known.NativeChat(chatId), olderThan = olderThan is null ? null : known.NativeChat(olderThan), count = 20 }, token);
            return result.Deserialize<InstagramMessagePage>(Json) ?? new([], false, null);
        }
        // The newest page is a GetThread on the real account each time: reads of it at the same moment share one, and one asked again a moment later
        // with nothing announced for the conversation since is answered with it (InstagramNewestReads). An older page is always read.
        var (page, answer) = olderThan is null
            ? await newestReads.ReadAsync(started, chatId, AskAsync, cancellationToken)
            : (await AskAsync(cancellationToken), InstagramNewestAnswer.Asked);
        if (answer != InstagramNewestAnswer.Asked) Interlocked.Increment(ref threadsReused);
        // The newest page brings the freshest read positions. Whoever asked for it is about to draw it, so the page is
        // not announced as a change as well; asking again for it would be a second read of Instagram for nothing.
        var native = known.NativeChat(chatId);
        AdoptReads([(native, page.Reads)], announce: false);
        var positions = ReadsOf(native);
        // The cursor is Instagram's own and is opaque, so it is qualified the same way a conversation is rather than
        // handed back raw where another provider's page could be mistaken for it.
        var mapped = page.Messages.Select(message => known.WithReaders(known.Map(message), positions)).ToArray();
        Remember(mapped);
        if (olderThan is null && answer != InstagramNewestAnswer.Held) heldPages.Record(started, chatId, mapped, page.HasMore);
        // One line per request Instagram saw; the answers shared or reused are counted in the ledger (threadsReused=).
        if (answer == InstagramNewestAnswer.Asked)
            InstagramLog.Write(InstagramLog.History(native, mapped.Length, olderThan is not null, positions?.Count ?? 0,
                mapped.Count(message => message.Delivery.SeenBy.Count > 0)));
        return new(mapped, page.HasMore, page.Cursor is null ? null : known.Chat(page.Cursor), null);
    }

    // Newest-page reads shared and reused, and how many were answered without asking Instagram since the last ledger line.
    private readonly InstagramNewestReads newestReads = new();
    private long threadsReused;

    // Something about a conversation was announced or changed by this app: its next newest-page read asks Instagram again.
    private void Announced(string chatId) => newestReads.Announced(chatId);

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        if (IsConnected) await receive(new(RealtimeKind.Connected), cancellationToken);
        // The helper reports that events arrived but not which conversations moved: stage 3's observation windows were
        // quiet, so there is no verified delta shape to map. Polling is what stands in for that. Without it nothing
        // ever re-read Instagram while the app sat idle, so a conversation read, edited or unsent on the phone stayed
        // as it was here until something else forced a refresh.
        var polling = PollAsync(cancellationToken);
        try
        {
            await realtime.DrainAsync(receive, cancellationToken);
        }
        finally { try { await polling; } catch (Exception) { /* The feed's own end is what matters. */ } }
    }

    // When the poll asks and what (the first page every twenty seconds, every page every ten minutes, a minute apart
    // while nobody could be looking), and when the request ledger is written: every fifteen minutes, which is what every
    // change to how often this asks Instagram is judged against.
    private readonly InstagramPollSchedule schedule = new();
    private HelperConnection? ledgerHelper;
    private InstagramRequestCounts? ledgerCounts;
    private InstagramTypingCounts? ledgerTyping;
    private InstagramDeltaCounts? ledgerDeltas;

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        // Subscribed before reading, so a change in between is not lost; taken off when the poll ends.
        void Presence(bool present)
        {
            var was = schedule.Present;
            schedule.SetPresent(present);
            if (was != present) InstagramLog.Write($"instagram poll pace={(present ? "present" : "away")}");
        }
        presence.Changed += Presence;
        try
        {
            Presence(presence.Present);
            schedule.LedgerDue();
            await PollLoopAsync(cancellationToken);
        }
        finally { presence.Changed -= Presence; }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var tally = InstagramPollTally.None;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await schedule.WaitAsync(failures, cancellationToken); }
            catch (OperationCanceledException) { return; }
            // Written whether or not the poll could read, so a poll stopped by Instagram still shows it asked nothing.
            if (helper is { } current && schedule.LedgerDue())
            {
                // Reads are answered on other threads; what this line did not carry stays counted for the next.
                var reused = Interlocked.Exchange(ref threadsReused, 0);
                if (await WriteLedgerAsync(current, tally with { ThreadsReused = reused }, cancellationToken)) tally = InstagramPollTally.None;
                else Interlocked.Add(ref threadsReused, reused);
                // The cursor moves with every event, and the saved one is where the next launch resumes from. Saving
                // only at connect left a session that ran for days resuming from the moment it connected.
                if (IsConnected && ReferenceEquals(helper, current))
                {
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    bound.CancelAfter(TimeSpan.FromSeconds(10));
                    await ResaveSessionAsync(current, "timer", bound.Token);
                }
            }
            if (!IsConnected || helper is not { } started || identity is not { } known) continue;
            var read = schedule.Next();
            try
            {
                var before = chats.ToDictionary(chat => chat.Id);
                tally = tally with
                {
                    Polls = tally.Polls + 1,
                    Sweeps = tally.Sweeps + (read == InstagramPollRead.Sweep ? 1 : 0),
                    Away = tally.Away + (schedule.Present ? 0 : 1),
                };
                // Asking for a refresh rather than accepting the helper's own cached list: the point of the poll is to
                // find out what changed elsewhere. A refresh that fails says so, where any other read of the list is
                // answered with the one already held. Most polls read the first page alone, which is where a
                // conversation with a new message rises to; a read or a change further down waits for the sweep.
                var fresh = await ReadChatsAsync(started, true, read == InstagramPollRead.Sweep ? "all" : "first", cancellationToken);
                if (read == InstagramPollRead.Sweep) schedule.Swept();
                if (failures > 0) InstagramLog.Write($"instagram poll recovered after={failures}");
                failures = 0;
                if (fresh.Length == 0) continue;
                chats = fresh;
                chatsReadAt = DateTimeOffset.UtcNow;
                // Somebody reading a message moves neither the newest message nor the unread flag, so it is not a
                // changed conversation below. It redraws the messages it covers instead.
                AdoptReads(fresh, announce: true);
                var (arrived, partial) = Arrivals(started, known, fresh);
                tally = tally with { Arrivals = tally.Arrivals + arrived.Count };
                // Reporting a conversation as changed is not free: the list marks the row as updating and reads it
                // again, so saying so when nothing moved makes the inbox look like it is refreshing forever. Only the
                // newest message's time and the unread flag decide it — the preview is derived from the same message,
                // so it cannot move on its own, and comparing it only adds a way to be wrong.
                var changed = fresh.Where(chat => !before.TryGetValue(chat.Id, out var old)
                    || old.At != chat.At || old.Unread != chat.Unread).ToArray();
                // Before anything is announced or read whole below: a conversation that moved, or that an arrival came in, is read from Instagram again,
                // never from a page held from before it. A page read after this (an arrival's whole read) already holds the arrival.
                foreach (var chat in changed) Announced(known.Chat(chat.Id));
                foreach (var native in partial) Announced(known.Chat(native));
                var moved = changed.Where(InstagramIdentity.IsShown).Select(chat => known.Chat(chat.Id)).ToArray();
                if (moved.Length > 0)
                {
                    await SaveChatsAsync();
                    realtime.Writer.TryWrite(new(RealtimeKind.ChatChanged, null, moved, null, DateTimeOffset.UtcNow));
                }
                // A photo, a video or a share is read whole before it is announced, so everyone who asks for it — the
                // conversation it lands in, a merge keeping its rows, the photos prepared ahead — gets its picture rather
                // than the list's words for it (InstagramBackend.CatchUp).
                if (partial.Length > 0) await ReadArrivalsAsync(started, known, partial, cancellationToken);
                foreach (var change in arrived) realtime.Writer.TryWrite(change);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception failure)
            {
                var code = InstagramPollFailure.Code(failure);
                if (InstagramPollFailure.StopsPolling(code))
                {
                    // Nothing but signing in again changes this answer, so the poll stops asking until a new connection
                    // starts, and the app is told the account needs the user rather than that it is reconnecting.
                    if (!ReferenceEquals(helper, started)) continue;
                    refused = code;
                    Hold(code);
                    ready = false;
                    failures = 0;
                    InstagramLog.Write($"instagram poll stopped code={code}");
                    realtime.Writer.TryWrite(new(RealtimeKind.AuthenticationRequired, ReceivedAt: DateTimeOffset.UtcNow));
                    continue;
                }
                failures++;
                InstagramLog.Write($"instagram poll failing code={code} consecutive={failures} next={schedule.Interval(failures).TotalSeconds:0}s");
            }
        }
    }

    // Messages that arrived, as the list's newest message in each conversation shows them, announced as new messages so
    // they can notify. A text message is remembered, so answering "what arrived" reads nothing. Any other kind is kept
    // apart, since the list's copy of it has no pictures: it answers only while its conversation cannot be on screen.
    // Partial names the conversations those came in, for the poll to read whole before announcing them.
    private (List<RealtimeEvent> Events, string[] Partial) Arrivals(HelperConnection started, InstagramIdentity known, InstagramChat[] fresh)
    {
        var (arrived, newest) = InstagramArrivals.Find(considered, fresh);
        // A connection that started meanwhile has seeded its own; this answer belongs to the one before it.
        if (!ReferenceEquals(helper, started)) return ([], []);
        considered = newest;
        if (arrived.Length == 0) return ([], []);
        var mapped = arrived.Select(message => (Whole: InstagramArrivals.Complete(message),
            Message: known.WithReaders(known.Map(message), ReadsOf(message.ChatId)))).ToArray();
        var complete = mapped.Where(item => item.Whole).Select(item => item.Message).ToArray();
        foreach (var (_, message) in mapped.Where(item => !item.Whole)) glimpses.Keep(message);
        // Never over a copy a conversation read supplied, which is the whole message rather than the list's view of it.
        lock (receiptGate) Remember([.. complete.Where(message => !seen.ContainsKey(message.Id))]);
        InstagramLog.Write($"instagram arrivals announced={arrived.Length} complete={complete.Length}");
        var now = DateTimeOffset.UtcNow;
        return ([.. arrived.Select(message => new RealtimeEvent(RealtimeKind.MessageChanged, known.Chat(message.ChatId),
            [known.Message(message.Id)], true, now))],
            [.. arrived.Where(message => !InstagramArrivals.Complete(message)).Select(message => message.ChatId).Distinct(StringComparer.Ordinal)]);
    }

    // One line of the request ledger: what the helper asked of Instagram since the last line. The helper answers this
    // from memory, asking Instagram nothing. True when a line was written.
    private async Task<bool> WriteLedgerAsync(HelperConnection current, InstagramPollTally tally, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await current.CallAsync("snapshot", null, cancellationToken);
            if (!snapshot.TryGetProperty("requests", out var raw) || raw.Deserialize<InstagramRequestCounts>(Json) is not { } counts) return false;
            // A different helper counts from zero; the reset rule in Since covers the same helper counting again.
            var same = ReferenceEquals(ledgerHelper, current);
            var since = counts.Since(same ? ledgerCounts : null);
            // Typing beside it, which uses the sockets rather than GraphQL: the line that shows typing added none of
            // the requests above.
            var typing = snapshot.TryGetProperty("typing", out var rawTyping) ? rawTyping.Deserialize<InstagramTypingCounts>(Json) : null;
            var typingSince = typing?.Since(same ? ledgerTyping : null);
            // The realtime deltas, counted and never applied, to set against the arrivals the poll announced.
            var deltas = snapshot.TryGetProperty("realtime", out var rawDeltas) ? rawDeltas.Deserialize<InstagramDeltaCounts>(Json) : null;
            var deltasSince = deltas?.Since(same ? ledgerDeltas : null);
            (ledgerHelper, ledgerCounts, ledgerTyping, ledgerDeltas) = (current, counts, typing, deltas);
            InstagramLog.Write(since.Describe(tally, deltasSince));
            if (typingSince is not null) InstagramLog.Write(typingSince.Describe());
            return true;
        }
        // Cancelled or not, a missing line is only a missing line; the poll's own wait notices the cancellation.
        catch (Exception) { return false; }
    }

    // Which helper reached ready, and when: only a helper that connected is recovered, and how long it stayed up decides
    // whether its recovery budget comes back.
    private sealed record ReadyHelper(HelperConnection Helper, DateTimeOffset At);
    private ReadyHelper? readySince;
    private readonly InstagramRecovery recovery = new();

    // A helper that stops by itself takes every call after it down with it. Before this Instagram then stayed down,
    // silently, until the next launch or a Reconnect by hand.
    private void Stopped(HelperConnection stopped)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref helper, null, stopped), stopped)) return;
        ready = false;
        ForgetTyping();
        realtime.Writer.TryWrite(new(RealtimeKind.Disconnected, ReceivedAt: DateTimeOffset.UtcNow));
        var since = readySince;
        var reached = since is not null && ReferenceEquals(since.Helper, stopped);
        var uptime = reached ? DateTimeOffset.UtcNow - since!.At : TimeSpan.Zero;
        string? state;
        lock (realtimeGate) state = realtimeState;
        // A session Instagram refused, by the poll or on the realtime connection, is the user's to sign in again.
        var refusedWith = refused ?? (InstagramConnectionFailure.NeedsUser(state) ? state : null);
        string? declined;
        lock (recovery) declined = recovery.Refuse(reached, uptime, closing, refusedWith);
        // How it ended in fixed words only: the helper's standard error is runtime output that can quote anything.
        InstagramLog.Write(InstagramLog.HelperStopped(uptime, reached, stopped.LastError, declined));
        if (declined is null) { reconnectComing = true; _ = RecoverAsync(); }
        // One that never connected belongs to whoever was starting it, and that caller says what happens next.
        else if (declined != "never_ready") ExpectReconnect(false);
    }

    // Starts again from the saved session, a few times at most and further apart each time, the same path the launch
    // restore takes. It stands aside for any other start: one already under way, or a helper already back, ends it.
    private async Task RecoverAsync()
    {
        string? last = null;
        while (true)
        {
            int attempt;
            lock (recovery) attempt = recovery.Take();
            if (attempt == 0) break;
            await Task.Delay(InstagramRecovery.Delay(attempt));
            if (closing || disposed) return;
            if (helper is not null || gate.CurrentCount == 0)
            {
                InstagramLog.Write($"instagram helper recovery stood_aside attempt={attempt}");
                // That start decides it. Once it is over, a start that failed leaves nothing else reconnecting.
                await gate.WaitAsync();
                gate.Release();
                if (!IsConnected) ExpectReconnect(false);
                return;
            }
            try
            {
                var shown = await ResumeAsync(automatic: true);
                if (shown < 0) { last = "saved_session_" + (SavedSessionProblem ?? "unreadable"); break; }
                InstagramLog.Write($"instagram helper recovered attempt={attempt} conversations={shown}");
                return;
            }
            catch (Exception failure)
            {
                last = InstagramConnectionFailure.Code(failure);
                if (InstagramConnectionFailure.Definitive(failure)) break;
            }
        }
        int used;
        lock (recovery) used = recovery.Used;
        InstagramLog.Write($"instagram helper recovery gave_up attempts={used} code={last ?? "budget_spent"}");
        ExpectReconnect(false);
    }

    // Pictures come through the helper, which holds the session Instagram's urls are fetched with. The app never
    // receives an Instagram address and never contacts Instagram itself.
    private const int MediaChunk = 192 << 10;
    private readonly SemaphoreSlim mediaTurn = new(1, 1);
    private const int MediaLimit = 25 << 20;

    public bool CanSave(MessageAttachment attachment) => InstagramIdentity.OwnsMedia(attachment) && IsConnected;

    /// <summary>Whether a file can be sent to this conversation at all.</summary>
    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default) =>
        GetSendPermissionAsync(chatId, cancellationToken);

    // What Instagram's own upload accepts, and what this build has a path for. A video from an account Instagram
    // refuses web video uploads for would need its Android fallback, which is not built; that shows as a failed
    // upload rather than a silent nothing.
    private const long MaxAttachmentBytes = 25L << 20;

    public async Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
    {
        var permission = await GetSendPermissionAsync(chatId, cancellationToken);
        if (!permission.Allowed) return permission;
        if (file.Length <= 0) return new(false, "That file is empty.");
        if (file.Length > MaxAttachmentBytes) return new(false, "Instagram will not take a file that large.");
        return new(true, "");
    }

    /// <summary>Instagram carries no caption with a file; a message alongside it is a separate message.</summary>
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AttachmentCaptionSupport(false, null));

    /// <summary>
    /// Sends a file: its bytes go over in chunks, then one call uploads them to Instagram and sends a message naming
    /// what was uploaded. The pipe carries a line of JSON at a time, so a photograph in one piece would be a line of
    /// megabytes.
    /// </summary>
    public async Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
    {
        if (identity is not { } known) throw NotConnected("send");
        if (helper is not { } started || !IsConnected) throw NotConnected("send");
        var permission = await GetFilePermissionAsync(chatId, file, cancellationToken);
        if (!permission.Allowed) throw Refused(permission.Reason);
        var native = known.NativeChat(chatId);
        try
        {
            await using var source = await file.OpenRead(cancellationToken);
            // The helper holds one file at a time, so every file, a voice note included, goes through the same turn.
            var result = await InstagramMediaTransfer.RunAsync(mediaTurn,
                (method, parameters, token) => started.CallAsync(method, parameters, token), source, MediaChunk,
                new { chatId = native, name = file.Name, mime = file.MimeType, voice = file.IsVoiceNote }, cancellationToken);
            var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
            if (string.IsNullOrEmpty(id)) throw new SendException(true, "Instagram did not confirm the file. Check the conversation before sending it again.");
            chatsReadAt = DateTimeOffset.MinValue;
            TypingEndedBySend(started, chatId, native);
            return new(chatId, known.Message(id));
        }
        catch (OperationCanceledException) { throw; }
        catch (SendException) { throw; }
        catch (BackendException) { throw; }
        // The file may have reached Instagram even when the message did not, so this is never retried automatically;
        // only a refusal the helper gave before sending anything says plainly that nothing went.
        catch (Exception failure) { throw InstagramRefusal.File(failure); }
        // Sent, refused or unknown, the conversation's newest page is read again rather than reused (InstagramNewestReads).
        finally { Announced(chatId); }
    }

    /// <summary>The bytes of a picture, by the id the helper issued for it.</summary>
    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false)
    {
        using var memory = new MemoryStream();
        await CopyToAsync(Source(attachment, inline), memory, cancellationToken);
        return memory.ToArray();
    }

    // A video always shows its still rather than the clip. A photo shows its smaller copy only while it is drawn in
    // the conversation: opening it is a request for the photograph, and answering that with the thumbnail is what
    // makes an opened picture look soft.
    private static MessageAttachment Source(MessageAttachment attachment, bool inline) =>
        (inline || attachment.Type == "video") && attachment.SourceUrl is { Length: > 0 } smaller
            ? attachment with { Id = smaller } : attachment;

    /// <summary>Where a picture may be kept on disk: the exact helper media id read.</summary>
    internal static string? ImageCacheKey(MessageAttachment attachment, bool inline)
    {
        if (attachment.Id is null) return null;
        var id = InstagramIdentity.NativeMedia(Source(attachment, inline));
        return id.Length == 0 ? null : "instagram-image:" + id;
    }

    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || helper is not { } started) throw NotConnected("media");
        var id = InstagramIdentity.NativeMedia(attachment);
        var offset = 0;
        var size = -1;
        while (true)
        {
            var response = await started.CallAsync("ui.mediaRead", new { id, offset }, cancellationToken);
            var total = response.GetProperty("total").GetInt32();
            var next = response.GetProperty("next").GetInt32();
            var bytes = response.GetProperty("data").GetBytesFromBase64();
            // The size must not change between chunks: a transfer whose source was fetched again half way through
            // would otherwise be stitched together from two different downloads.
            if (size < 0) size = total;
            if (total < 0 || total > MediaLimit || total != size || bytes.Length > MediaChunk
                || next != offset + bytes.Length || next > total) throw new IOException("Invalid media chunk.");
            await destination.WriteAsync(bytes, cancellationToken);
            offset = next;
            if (offset >= total) return;
        }
    }

    /// <summary>Whether a message can be sent to this conversation.</summary>
    public async Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known) return Reconnecting(out var pending) ? pending : new(false, "Instagram is not connected. Sign in to Instagram in Settings.");
        // A session Instagram refused is not reconnecting, whatever is still running: only signing in again helps.
        if (!IsConnected) return refused is null && Reconnecting(out var resuming) ? resuming : new(false, "Instagram is not connected. Sign in to Instagram in Settings.");
        var native = known.NativeChat(chatId);
        var chat = chats.FirstOrDefault(item => item.Id == native);
        // A message request or a spam thread is not somewhere to reply from: answering one accepts it, which is a
        // separate decision nobody has made.
        if (chat is not null && chat.Folder != "inbox")
            return new(false, chat.Folder == "spam" ? "This Instagram conversation is filed as spam." : "This is an Instagram message request.");
        if (chat?.ReadOnly == true) return new(false, "Instagram does not allow replies in this conversation.");
        return new(true, "", MaxTextLength);
    }

    // A helper still starting, or a saved session about to reconnect, is worth waiting for rather than telling the
    // user to act: its readiness refreshes the permission. Only while something will actually reconnect, though: a
    // saved session alone said "reconnecting" forever after the launch restore gave up or a helper died, and held a
    // message typed there until the user happened to press Reconnect.
    private bool Reconnecting(out SendPermission permission)
    {
        permission = new(false, "Instagram is reconnecting.", null, Pending: true);
        return helper is not null || reconnectComing && !closing;
    }

    internal const int MaxTextLength = 8192;

    public async Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known) throw NotConnected("send");
        if (helper is not { } started || !IsConnected) throw NotConnected("send");
        InstagramLog.Write(InstagramLog.Send(known.NativeChat(chatId), text.Length, false, NativeBeeper.Presentation.Services.TypingPings.Shared.SinceMilliseconds(chatId)));
        JsonElement result;
        try { result = await started.CallAsync("ui.sendText", new { chatId = known.NativeChat(chatId), text }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // A send whose outcome is unknown is never retried automatically: the message may already have arrived. A refusal
        // the helper gave before sending is not unknown, and calling it that made "not loaded" read as a risk.
        catch (Exception failure) { throw InstagramRefusal.Send(failure); }
        finally { Announced(chatId); }
        var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
        if (string.IsNullOrEmpty(id)) throw new SendException(true, "Instagram did not confirm the message. Check the conversation before sending it again.");
        chatsReadAt = DateTimeOffset.MinValue;
        TypingEndedBySend(started, chatId, known.NativeChat(chatId));
        return new(chatId, known.Message(id));
    }

    /// <summary>What can be done to one message.</summary>
    public async Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known) return new(false, [], []);
        var permission = await GetSendPermissionAsync(chatId, cancellationToken);
        if (!permission.Allowed) return new(false, [], []);
        try
        {
            var message = await ResolveMessageAsync(chatId, messageId, cancellationToken);
            if (message.IsDeleted) return new(false, [], []);
            // Only the reactions this account actually placed can be taken away. Offering the whole list to remove
            // meant the menu showed six removals on a message with no reactions at all.
            var chat = chats.FirstOrDefault(item => item.Id == known.NativeChat(chatId));
            var self = known.Person(chat?.Participants.FirstOrDefault(person => person.Self)?.Id ?? "");
            var mine = message.Reactions.Where(item => item.ParticipantId == self).Select(item => item.Key).Distinct().ToArray();
            var own = message.IsOutgoing;
            // Only your own words can be corrected, and only words: there is no way to change a picture already sent.
            var edit = own && message.Attachments.Count == 0 && !string.IsNullOrEmpty(message.Text);
            // Instagram takes a message back for everyone or not at all, so there is no hiding one from yourself.
            return new(true, CommonReactions, mine, edit, false, own, MaxTextLength);
        }
        catch (OperationCanceledException) { throw; }
        // A message this build cannot find is one it should not offer to change.
        catch (Exception) { return new(false, [], []); }
    }

    // Instagram's own short list. A reaction it does not accept is refused by Instagram rather than guessed at here.
    private static readonly string[] CommonReactions = ["❤️", "😂", "😮", "😢", "😡", "👍"];

    public async Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known) throw NotConnected("send");
        if (helper is not { } started || !IsConnected) throw NotConnected("send");
        InstagramLog.Write(InstagramLog.Send(known.NativeChat(chatId), text.Length, true, NativeBeeper.Presentation.Services.TypingPings.Shared.SinceMilliseconds(chatId)));
        JsonElement result;
        try
        {
            result = await started.CallAsync("ui.sendText",
                new { chatId = known.NativeChat(chatId), text, replyTo = known.NativeMessage(messageId) }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) { throw InstagramRefusal.Send(failure); }
        finally { Announced(chatId); }
        var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
        if (string.IsNullOrEmpty(id)) throw new SendException(true, "Instagram did not confirm the reply. Check the conversation before sending it again.");
        chatsReadAt = DateTimeOffset.MinValue;
        TypingEndedBySend(started, chatId, known.NativeChat(chatId));
        return new(chatId, known.Message(id));
    }

    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default) =>
        MutateAsync(chatId, "ui.react", InstagramRefusal.MessageChange.Reaction, chat => new { chatId = chat, messageId = Native(chatId, messageId), emoji = reaction, remove }, cancellationToken);

    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default) =>
        MutateAsync(chatId, "ui.editMessage", InstagramRefusal.MessageChange.Edit, chat => new { chatId = chat, messageId = Native(chatId, messageId), text }, cancellationToken);

    /// <summary>Takes back a message, which removes it for everyone. Instagram has no "delete for me".</summary>
    public async Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        if (!forEveryone) throw Refused("Instagram removes a message for everyone or not at all.");
        await MutateAsync(chatId, "ui.deleteMessage", InstagramRefusal.MessageChange.Unsend, chat => new { chatId = chat, messageId = Native(chatId, messageId) }, cancellationToken);
        // Instagram says nothing of an unsend but that the conversation moved, and that alone left the message drawn until the
        // conversation was opened again (2026-10-15). It is gone, so it is said so, as Messenger's helper does, and forgotten:
        // a receipt or a lookup answered from memory would otherwise draw it back.
        lock (receiptGate) seen.Remove(messageId);
        realtime.Writer.TryWrite(new(RealtimeKind.MessageDeleted, chatId, [messageId], false, DateTimeOffset.UtcNow));
    }

    private string Native(string chatId, string messageId) => identity!.NativeMessage(messageId);

    // One shape for the mutations that either work or do not. None is retried: an uncertain change to someone else's
    // conversation is not something to repeat on a guess.
    private async Task MutateAsync(string chatId, string method, InstagramRefusal.MessageChange kind, Func<string, object> parameters, CancellationToken cancellationToken)
    {
        if (identity is not { } known) throw NotConnected("send");
        if (helper is not { } started || !IsConnected) throw NotConnected("send");
        try { await started.CallAsync(method, parameters(known.NativeChat(chatId)), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // A change whose outcome is unknown must say so, as a send does: the request may already have reached Instagram,
        // and reporting it as refused is what let the person submit it again.
        catch (Exception failure) { throw InstagramRefusal.Change(failure, kind); }
        finally { Announced(chatId); }
        chatsReadAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Finds a message the app has a receipt for. The conversation is the only place to look, because a sent message
    /// is not reported separately: the list going stale is what brings it back.
    /// </summary>
    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        // Anything already read stays known, so acting on a message further up the conversation does not depend on it
        // still being on the newest page.
        lock (receiptGate) if (seen.TryGetValue(messageId, out var remembered)) return remembered;
        // An arrival that is not text, asked about by the notifier or the unarchiver, costs no conversation read while
        // nothing has asked for that conversation's messages. Reading it here does not count as asking.
        var (copy, from) = glimpses.Answer(chatId, messageId);
        if (from is not null) InstagramLog.Write(InstagramLog.ArrivalResolved(chatId, from));
        if (copy is not null) return copy;
        var page = await ReadMessagesAsync(chatId, null, cancellationToken);
        return page.Items.FirstOrDefault(message => message.Id == messageId)
            ?? throw new BackendException(BackendFailure.Unavailable, "That Instagram message is not loaded.");
    }

    // Memory only, for a display-only caller: what ResolveMessageAsync would answer without reading the conversation,
    // and the list's copy of an arrival even once its conversation has been opened, since a one-line mention of it does
    // not need the whole message. Never a request to Instagram, and no "arrival resolved" line.
    internal ChatMessage? PeekMessage(string messageId)
    {
        lock (receiptGate) if (seen.TryGetValue(messageId, out var remembered)) return remembered;
        return glimpses.Peek(messageId);
    }

    // Memory only: whether ResolveMessageAsync would answer without reading the conversation. Narrower than PeekMessage,
    // whose list copy of an arrival stays usable once its conversation is opened while ResolveMessageAsync's does not;
    // Up-to-edit asks this before offering a message, because the options lookup it leads to must never read Instagram
    // on a key press.
    internal bool AnswersFromMemory(string chatId, string messageId)
    {
        lock (receiptGate) if (seen.TryGetValue(messageId, out var remembered)) return remembered.ChatId == chatId;
        return glimpses.Answer(chatId, messageId).Copy is { } copy && copy.ChatId == chatId;
    }

    // The messages this session has read, so one can be found again without paging back to it. Bounded: a long
    // conversation must not become a second copy of itself in memory.
    private readonly Dictionary<string, ChatMessage> seen = [];
    private readonly Queue<string> seenOrder = new();
    private const int SeenLimit = 2000;

    private void Remember(IReadOnlyList<ChatMessage> messages)
    {
        lock (receiptGate)
        {
            foreach (var message in messages)
            {
                if (!seen.ContainsKey(message.Id)) seenOrder.Enqueue(message.Id);
                seen[message.Id] = message;
            }
            while (seenOrder.Count > SeenLimit) seen.Remove(seenOrder.Dequeue());
        }
    }

    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsConnected && identity is { } known && known.Owns(chatId)
            && InstagramMarkRead.Allowed(chats.FirstOrDefault(item => item.Id == known.NativeChat(chatId))));

    /// <summary>Tells Instagram this account has read the conversation as far as a message.</summary>
    /// <remarks>
    /// Never a spam conversation: those are Instagram's hidden message requests, and its own apps do not tell a sender
    /// their request was seen until it is accepted. Such a conversation therefore stays unread here after it is opened,
    /// because whether it is unread is Instagram's answer and nothing here changes it.
    /// </remarks>
    public async Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        // A read not made is said so, never answered as done: the caller then showed the conversation read and never told
        // Instagram again, so it stayed unread on Instagram's own apps (2026-10-22 audit). The view's own quiet catch keeps
        // the error from the person, and the conversation stays unread here, which is what Instagram says.
        if (identity is not { } known || helper is not { } started || !IsConnected) throw NotConnected("read");
        var native = known.NativeChat(chatId);
        var message = known.NativeMessage(messageId);
        var chat = chats.FirstOrDefault(item => item.Id == native);
        if (!InstagramMarkRead.Allowed(chat))
        {
            InstagramLog.Write(InstagramLog.MarkReadSkipped(native, chat!.Folder));
            return;
        }
        // Instagram wants the message's own time alongside its id, as upstream sends it.
        ChatMessage? remembered;
        lock (receiptGate) seen.TryGetValue(messageId, out remembered);
        var (at, source) = InstagramMarkRead.Time(remembered, chat);
        if (at <= 0) throw Refused("Instagram can't mark this as read yet.");
        // A failed read is tried again on the next look at the conversation, and at most once every RetryAfterFailure for
        // one conversation (owner, 2026-10-22): a refusing or throttled Instagram is never asked faster than that.
        if (readFailures.HoldsBack(native, DateTimeOffset.UtcNow))
        {
            InstagramLog.Write(InstagramLog.MarkReadFailed(native, "held_back"));
            throw Refused("Instagram refused this read a moment ago.");
        }
        JsonElement result;
        try { result = await started.CallAsync("ui.markRead", new { chatId = native, messageId = message, at }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // Read state is not worth an error in front of the user, and never worth an automatic retry.
        catch (Exception failure)
        {
            readFailures.Failed(native, DateTimeOffset.UtcNow);
            // The helper's failure code, which MarkReadFailed reduces to a fixed word; any other exception is only "other".
            var code = failure is InvalidOperationException ? failure.Message : "other";
            InstagramLog.Write(InstagramLog.MarkReadFailed(native, code));
            throw Refused("Instagram did not take this read.");
        }
        finally { Announced(chatId); }
        readFailures.Succeeded(native);
        chatsReadAt = DateTimeOffset.MinValue;
        // Whether Instagram accepted the id and time it was given, and which time that was: the measurement that says
        // whether the conversation's latest activity was ever a good enough stand-in for the message's own time. Short is
        // how far the watermark Instagram recorded falls short of the conversation's activity time (owner, 2026-10-22:
        // measured before the unread rule is changed to Instagram's).
        long? shortMs = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("watermarkShortMs", out var gap) && gap.TryGetInt64(out var ms) ? ms : null;
        InstagramLog.Write(InstagramLog.MarkRead(native, Flag(result, "validated"), Flag(result, "answered"), source, shortMs));
    }

    private readonly InstagramReadFailures readFailures = new();

    private static bool Flag(JsonElement result, string name) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Says plainly that Instagram here can only read. Every mutation the app offers other providers comes through
    /// this, so a refusal is one sentence the user can act on rather than a failure that looks like a fault.
    /// </summary>
    public static BackendException Refused(string message) => new(BackendFailure.Unsupported, message);

    // Asked before Instagram has connected. While a reconnect is coming (InstagramWaiting) that is NotReady, a waiting
    // failure: a conversation waits it out quietly and reads again on Connected, and a picture shows it is waiting and
    // tries again. Only a session Instagram refused, or nothing left to reconnect it, is the user's to act on.
    private BackendException NotConnected(string kind = "other")
    {
        if (!InstagramWaiting.Waits(helper is not null, reconnectComing, closing, RefusedWith()))
            return new(BackendFailure.AuthenticationRequired, "Instagram is not connected. Sign in to Instagram in Settings.");
        if (waits.Answered(kind) is { } first) InstagramLog.Write(first);
        return new(BackendFailure.NotReady, "Instagram is reconnecting.");
    }
    private readonly InstagramWaitTally waits = new();
    private void WaitEnded(string then)
    {
        if (waits.Settle(then) is { } line) InstagramLog.Write(line);
    }

    /// <summary>Signs out and forgets this account entirely.</summary>
    public Task SignOutAsync() => SignOutCoreAsync(null);

    // A completed move-back already removed the pointer. Its host owns the lifecycle lock and names the account from the move record; an older cached identity must not delete
    // another account's partition while clearing the backend's presentation state.
    internal Task SignOutMovedAwayAsync(string accountKey) => SignOutCoreAsync(accountKey);

    private async Task SignOutCoreAsync(string? recordedKey)
    {
        var key = recordedKey ?? identity?.AccountKey ?? await store.ActiveAccountAsync();
        await CloseAsync(keepSession: false);
        // Wait out any start that was already past its own check, and close what it opened, so nothing signs this
        // account back in after its session is gone.
        await gate.WaitAsync();
        try
        {
            await CloseCoreAsync();
            await sessionWrites.WaitAsync();
            try { if (key is not null) await store.ForgetAsync(key); }
            finally { sessionWrites.Release(); }
            identity = null;
            AccountName = null;
            chats = [];
            lock (receiptGate) readPositions.Clear();
            glimpses.Clear();
            heldPages.Clear();
            newestReads.Clear();
            restored = false;
            // Signed out: there is no session left for a hold to be about, and the next sign-in starts clean. The read budget is kept.
            refused = null;
            startRefused = null;
            ReleaseHold();
            directorySearch.ForgetAnswers();
        }
        finally { gate.Release(); }
        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes Instagram for quitting, and returns within a few seconds whatever the helper is doing, so it can be awaited
    /// at shutdown.
    /// </summary>
    /// <remarks>
    /// The session is saved first, as the helper holds it now, so the next launch resumes from where this one got to;
    /// that is a question the helper answers from memory, but after whatever read it is already running (its calls are
    /// served in order), so it is given three seconds; a save that misses them is logged. The gate is not waited for: a
    /// start holds it through a connect of up to 45 seconds. Closing the helper under a start fails that start's next
    /// call, and the start cleans up behind itself; closing its standard input is what tells the helper to end, and the
    /// connection kills it three seconds later if it has not.
    /// </remarks>
    public Task CloseAsync() => CloseAsync(keepSession: true);

    internal async Task StopForMoveAsync(CancellationToken cancellationToken)
    {
        await CloseAsync();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await CloseCoreAsync();
            await sessionWrites.WaitAsync(cancellationToken);
            sessionWrites.Release();
        }
        finally { gate.Release(); }
    }

    private async Task CloseAsync(bool keepSession)
    {
        closing = true;
        reconnectComing = false;
        WaitEnded("closing");
        if (keepSession && IsConnected && helper is { } current)
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (!await ResaveSessionAsync(current, "close", bound.Token)) InstagramLog.Write("instagram session not saved after=close");
        }
        await CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        ready = false;
        if (Interlocked.Exchange(ref helper, null) is not { } running) return;
        ForgetTyping();
        realtime.Writer.TryWrite(new(RealtimeKind.Disconnected));
        try { await running.DisposeAsync(); } catch (Exception) { }
    }

    /// <summary>The service provider's asynchronous disposal: the same bounded close as quitting.</summary>
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await CloseAsync(keepSession: true);
    }

    /// <summary>
    /// The service provider disposes synchronously at shutdown. Closing the helper's standard input happens before the
    /// first wait inside its disposal, so the helper learns of the exit now; the rest is not waited for.
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        closing = true;
        ready = false;
        if (Interlocked.Exchange(ref helper, null) is { } started) _ = started.DisposeAsync().AsTask();
    }
}
