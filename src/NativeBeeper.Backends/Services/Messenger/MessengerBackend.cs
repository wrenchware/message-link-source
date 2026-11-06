using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Messenger;

// Facebook Messenger: conversations, history, directory lookup and mutations go through the messenger-helper process
// over a private pipe; MessagingRouter keeps their ids away from the Google helper.
internal sealed partial class MessengerBackend(IStateProtector protector) : IChatReader, ILinkPreviewLoader, IMessageSender, IMediaReader, IAttachmentBackend, IAttachmentBatchBackend, IMessageActions, IMessageMutations, IRealtimeFeed, IDisposable, IAsyncDisposable
{
    private const string NotYet = "Sending files to Messenger is not available yet.";
    private const int Chunk = 192 * 1024;
    private const int Limit = 25 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim gate = new(1);
    private HelperConnection? helper;
    private volatile bool ready;
    private MessengerChat[] chats = [];
    public bool IsConnected => helper is not null && ready;
    // Raised after connecting or signing out so the chat list can reload.
    public event EventHandler? ChatsChanged;
    // What Messenger has changed since the app last looked, for the realtime feed. Bounded and dropping: a backlog
    // nobody is reading must never hold up the helper's own event pump. A drop reaches the reader as a resync.
    private readonly RealtimeBuffer realtime = new(512, MessengerLog.Write);

    public static string SessionPath => NativeBeeper.App.Services.AppPaths.Combine("Messenger", "session.protected");
    // The last known conversations, so the list keeps them at launch instead of dropping them until Messenger reconnects.
    private static string ChatsPath => Path.Combine(Path.GetDirectoryName(SessionPath)!, "chats.protected");
    public static bool HasSavedSession => File.Exists(SessionPath);
    private bool restored;
    // Set while the saved list is being read. restored is set before that read, so a second reader arriving during it
    // skips the restore and maps the list as it stands, possibly still empty. Whether two startup readers ever overlap
    // is not known, so it is counted before anything is changed for it.
    private volatile bool restoring;

    private void NoteRestoreOverlap(string reader)
    {
        if (restoring) MessengerLog.Write($"chats restore overlapped by {reader}");
    }

    private async Task RestoreChatsAsync()
    {
        restored = true;
        if (!File.Exists(ChatsPath)) return;
        // A connection finishing while the file is read supplies a fresher list; the saved one must not replace it.
        var before = chats;
        restoring = true;
        try
        {
            var stored = await protector.UnprotectAsync(await File.ReadAllBytesAsync(ChatsPath));
            try { if (ReferenceEquals(chats, before)) chats = JsonSerializer.Deserialize<MessengerChat[]>(stored, Json) ?? []; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored); }
        }
        catch (Exception) { /* A cache that cannot be read is simply not used. */ }
        finally { restoring = false; }
    }

    private async Task SaveChatsAsync(MessengerChat[] known)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(known, Json);
        try
        {
            var encrypted = await protector.ProtectAsync(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(ChatsPath)!);
            var temporary = ChatsPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted);
            File.Move(temporary, ChatsPath, true);
        }
        catch (Exception) { /* The list still works from memory this session. */ }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<Dictionary<string, string>?> ReadSavedCookiesAsync()
    {
        if (!HasSavedSession) return null;
        byte[] sealedSession;
        // Signed out between the look and the read: nothing saved, as if the look had seen it.
        try { sealedSession = await File.ReadAllBytesAsync(SessionPath); }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { return null; }
        var stored = await protector.UnprotectAsync(sealedSession);
        try
        {
            using var saved = JsonDocument.Parse(stored);
            return saved.RootElement.GetProperty("cookies").Deserialize<Dictionary<string, string>>();
        }
        catch (Exception) { return null; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored); }
    }

    // Returns how many conversations Messenger reported.
    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default, bool adoptLegacy = false) =>
        ConnectCoreAsync(cookies, cancellationToken, adoptLegacy, automatic: false);

    /// <summary>
    /// Whether the host lets this account sign in at all (the account move's gate: false while a move is staged or the account has moved away). Null (the app) lets it. Asked under the
    /// connect lock, after any shutdown that was waiting for it, so a hold that closes the helper cannot be undone by a connect that had already passed the ports' check.
    /// </summary>
    internal Func<bool>? MayConnect { get; set; }

    private async Task<int> ConnectCoreAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken, bool adoptLegacy, bool automatic)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (MayConnect?.Invoke() == false) throw new InvalidOperationException("account_moved_away");
            // An automatic reconnect (the recovery after a helper stopped) never reopens a close it did not make: a shutdown that came while it waited stands.
            if (automatic && closing) return chats.Length;
            // An explicit connect can reopen a signed-out backend. An automatic history restart cannot.
            closing = false;
            await ConnectLockedAsync(cookies, cancellationToken, adoptLegacy);
        }
        finally { gate.Release(); }
        AnnounceConnected();
        return chats.Length;
    }

    // The gate covers replacing a helper, including a restart's final idle check. Otherwise a user connect could
    // replace the checked helper before the restart got the gate, and the restart would stop that new connection.
    private async Task ConnectLockedAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default, bool adoptLegacy = false)
    {
        // A saved session's cookies (adoptLegacy) are read again under this lock, whatever the caller read before taking it: a sign-out that held it meanwhile (a server's removal,
        // Settings' Sign out) has deleted the session, or a new sign-in has replaced it, and the caller's cookies would sign the old account back in behind either.
        if (adoptLegacy) cookies = await ReadSavedCookiesAsync() ?? throw new InvalidOperationException("missing_cookies");
        var path = HelperExecutables.PathFor("messenger-helper");
        if (!File.Exists(path)) throw new InvalidOperationException("helper_missing");
        try
        {
            await CloseAsync();
            if (closing) throw new OperationCanceledException("Messenger is closing.");
            var started = await HelperConnection.StartAsync(path, null, cancellationToken);
            started.Scope = "messenger:";
            started.Received += (kind, data) =>
            {
                if (!ReferenceEquals(helper, started)) return;
                if (kind == "failed") SocketFailed(started);
                else if (kind == "history_stalled") HistoryStalled(started);
                else OnHelperEvent(kind, data);
            };
            started.Disconnected += () => HelperStopped(started);
            helper = started;
            void StillOpen()
            {
                if (closing || !ReferenceEquals(helper, started)) throw new OperationCanceledException("Messenger is closing.");
            }
            // Shutdown deliberately bypasses the gate, so it can close a connect still waiting on Facebook. A hold that came while the helper started recorded itself first and then shut
            // down: asked again here, now that there is a helper to put down, so nothing is said to Facebook for an account the host has held.
            if (MayConnect?.Invoke() == false) throw new InvalidOperationException("account_moved_away");
            StillOpen();
            // A new helper session has handed nothing out yet: what the last one named no longer says a picture is gone.
            NativeBeeper.Presentation.Services.MediaNews.Forget("messenger:");
            (catchUpUntil, catchUpLeft) = (DateTimeOffset.UtcNow + CatchUpWindow, CatchUpMessages);
            _ = NoteCatchUpAsync(started);
            // Nothing is readable in Messenger until this whole sequence finishes: an unconnected backend answers a
            // conversation with NotReady, which is what puts "Updating conversation…" up and keeps it there. On a cold
            // start that has been most of a minute, so each step says how long it took. Counts and durations only.
            var step = System.Diagnostics.Stopwatch.StartNew();
            void Took(string what)
            {
                StillOpen();
                MessengerLog.Write($"connect step={what} ms={step.ElapsedMilliseconds}");
                step.Restart();
            }
            var hello = await started.CallAsync("hello", null, cancellationToken);
            if (hello.GetProperty("protocol").GetInt32() != 4) throw new InvalidOperationException("unsupported_protocol");
            Took("hello");
            await started.CallAsync("login", new { cookies, platform = "facebook", adoptLegacy }, cancellationToken);
            Took("login");
            var session = await started.CallAsync("session", null, cancellationToken);
            Took("session");
            await SaveSessionAsync(session);
            Took("saveSession");
            await started.CallAsync("connect", null, cancellationToken);
            Took("connect");
            chats = await ReadChatsAsync(started, cancellationToken);
            chatsReadAt = DateTimeOffset.UtcNow;
            Took("readChats");
            await SaveChatsAsync(chats);
            Took("saveChats");
            await RestoreEncryptedAsync(started, cancellationToken);
            Took("restoreEncrypted");
            connectedAt = DateTimeOffset.UtcNow;
            ready = !closing && ReferenceEquals(helper, started);
        }
        // Windows refused to start the helper (blocked, or gone since the check above). Said by its fixed code, which
        // the restore treats as definite and Settings explains; the exception's own message names the path.
        catch (Exception failure) when (NativeBeeper.App.Services.HelperStartFailure.Code(failure) is { } code)
        {
            MessengerLog.Write($"helper start failed {NativeBeeper.App.Services.HelperStartFailure.Fields(failure)}");
            await CloseAsync();
            throw new InvalidOperationException(code);
        }
        catch
        {
            await CloseAsync();
            throw;
        }
    }

    private void AnnounceConnected()
    {
        if (closing || !ready) return;
        realtime.Writer.TryWrite(new(RealtimeKind.Connected));
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        // The saved inbox is available before the helper finishes restoring encrypted history and media. A user can
        // open one of those rows immediately; tell the shared sync path when Messenger is actually ready so a failed
        // first read repairs itself instead of requiring a switch to another conversation and back.
        if (chats.Length > 0)
            realtime.Writer.TryWrite(new(RealtimeKind.ChatChanged, null,
                chats.Select(chat => MessengerModels.Qualify(chat.Id)).ToArray(), null, DateTimeOffset.UtcNow));
    }

    // Most Messenger conversations are end-to-end encrypted, and their messages travel on a separate connection that
    // only a registered device may open. Turning this on registers this PC as one of the account's Messenger devices,
    // the same as opening Messenger on a new computer; it stays until the account removes it. A new sign-in turns it on by
    // itself, once (MessengerEncryptedAuto); a saved session never does, and otherwise it is the Settings button. The flag
    // is what makes it happen again on the next launch.
    private static string EncryptedPath => Path.Combine(Path.GetDirectoryName(SessionPath)!, "encrypted.on");
    public static bool EncryptedEnabled => File.Exists(EncryptedPath);

    // The Settings button. Null when it is on; otherwise what to tell the person.
    public async Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default) =>
        (await TurnOnEncryptedAsync(cancellationToken)).Failure;

    // Straight after a new sign-in, and only from there (MessengerEncryptedAuto). One call to the same registration the
    // button makes, never repeated by itself whatever it answers: an answer that never arrived may hide a registration
    // that happened. Null when it is on, or was already; otherwise what to tell the person.
    public async Task<string?> EnableEncryptedAfterSignInAsync()
    {
        if (MessengerEncryptedAuto.Skip(newSignIn: true, EncryptedEnabled) is { } skip)
        {
            MessengerLog.Write($"encrypted auto outcome=skipped reason={skip}");
            return null;
        }
        MessengerLog.Write("encrypted auto outcome=started");
        try
        {
            var (failure, code) = await TurnOnEncryptedAsync(CancellationToken.None);
            MessengerLog.Write(failure is null ? "encrypted auto outcome=registered" : $"encrypted auto outcome=failed reason={code}");
            return failure;
        }
        // Not the person cancelling: a helper that went away mid-call cancels its calls. Reported, not retried, and not
        // called a refusal, since the registration may have gone through.
        catch (OperationCanceledException)
        {
            MessengerLog.Write("encrypted auto outcome=failed reason=cancelled");
            return MessengerEncryptedAuto.NoAnswer;
        }
    }

    // The registration itself. Code: the helper's fixed code, or the exception's type, for the log.
    private async Task<(string? Failure, string Code)> TurnOnEncryptedAsync(CancellationToken cancellationToken)
    {
        if (helper is not { } started) return ("Connect Messenger first.", "not_connected");
        try
        {
            await started.CallAsync("ui.encrypted", null, cancellationToken);
            deviceMissing = false;
            Directory.CreateDirectory(Path.GetDirectoryName(EncryptedPath)!);
            await File.WriteAllTextAsync(EncryptedPath, "on", cancellationToken);
            MessengerLog.Write("encrypted conversations enabled");
            return (null, "ok");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            var code = MessengerFailure.Code(failure);
            MessengerLog.Write($"encrypted refused: {code}");
            return (failure.Message switch
            {
                "e2ee_register_failed" => "Facebook would not register this PC as a Messenger device. Try again later.",
                "e2ee_store_failed" or "no_storage" => "The device keys could not be stored on this PC.",
                "unknown_account" => "Messenger has not said which account this is yet. Try again in a moment.",
                _ when MessengerEncryptedAuto.Unanswered(code) => MessengerEncryptedAuto.NoAnswer,
                _ => "The encrypted connection could not be opened.",
            }, code);
        }
        catch (Exception failure)
        {
            var code = MessengerFailure.Code(failure);
            return (MessengerEncryptedAuto.Unanswered(code) ? MessengerEncryptedAuto.NoAnswer : "The encrypted connection could not be opened.", code);
        }
    }

    // Reconnects the encrypted side when it was turned on before; the device is already registered by then.
    private async Task RestoreEncryptedAsync(HelperConnection started, CancellationToken cancellationToken)
    {
        if (!EncryptedEnabled) return;
        // restore: true is the helper's promise never to register a device here. This is a reconnect of a device registered earlier; if its keys are not there
        // (the database was lost, or a copy of the data came without it), registering a fresh one would add a device to the account without anyone asking. Only the
        // Settings button and a new sign-in may do that (TurnOnEncryptedAsync).
        var outcome = await MessengerEncryptedRestore.RunAsync(parameters => started.CallAsync("ui.encrypted", parameters, cancellationToken), MessengerLog.Write);
        if (outcome == MessengerEncryptedRestore.Outcome.Restored) deviceMissing = false;
        else if (outcome == MessengerEncryptedRestore.Outcome.DeviceMissing)
        {
            // The flag goes, so the next launch does not try again and Settings offers the button that registers a device on purpose.
            try { File.Delete(EncryptedPath); } catch (Exception) { }
            deviceMissing = true;
            SetEncryptedState(MessengerEncryption.DeviceMissing);
        }
    }

    // Set when a restore found no device keys. The helper reports its encrypted connection as off then, which would hide the reason, so this keeps the sentence until a device is registered.
    private volatile bool deviceMissing;

    // What Messenger is doing right now, for the status line in Settings. Counts and a state word only.
    public async Task<string> DescribeAsync(CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) return "Messenger is not connected on this PC. Connect to sign in again.";
        var result = await started.CallAsync("counts", null, cancellationToken);
        var threads = result.TryGetProperty("threads", out var known) ? known.GetInt32() : chats.Length;
        var paging = result.TryGetProperty("paging", out var state) ? state.GetString() : null;
        // Archived conversations are not in the inbox view, which is most of why the two counts differ.
        var archived = chats.Count(chat => chat.Archived);
        var counted = archived > 0 ? $"{threads} conversations, {archived} of them archived" : $"{threads} conversations";
        // Facebook no longer has this PC on the account, or the encrypted connection is down: either way the encrypted
        // conversations — most of them — are carrying nothing at all. That matters more than how far the list has
        // loaded, so it is said first (MessengerEncryptedStatus).
        var encrypted = NoteEncryptedState(result);
        if (MessengerEncryptedStatus.Problem(encrypted, EncryptedEnabled) is { } problem)
            return $"Connected, {counted}. {problem}";
        return paging switch
        {
            "waiting" or "running" => $"Connected. {counted}, still loading older ones.",
            "complete" => $"Connected. {counted}; Facebook says there are no older ones.",
            "settled" => $"Connected. {counted}; Facebook stopped sending older ones.",
            "done" => $"Connected. {counted}, as far back as Messages loads.",
            "failed" or "stopped" => $"Connected. {counted}. Loading older ones did not finish; reconnect to try again.",
            _ => $"Connected. {counted}.",
        };
    }

    /// <summary>
    /// The encrypted connection's state as the helper last reported it, for the Settings card; Unknown until asked.
    /// </summary>
    public MessengerEncryption EncryptedState { get; private set; }
    private long encryptedAskedAt;

    private MessengerEncryption NoteEncryptedState(JsonElement counts)
    {
        var gone = counts.TryGetProperty("deviceGone", out var device) && device.ValueKind == JsonValueKind.True;
        var word = counts.TryGetProperty("encrypted", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
        return SetEncryptedState(MessengerEncryptedStatus.Parse(word, gone));
    }

    // Every change of the state tells the account's listeners, whichever way it was learned: the helper announces each
    // change as an "encrypted" event, and "counts" (Settings' refresh) can find one too. A host publishing its state
    // to a client hears it only from this, so a change that stayed silent stayed unseen there until something else
    // happened to republish (2026-10-09: Connecting shown for a connection that had been ready for seconds).
    private MessengerEncryption SetEncryptedState(MessengerEncryption parsed)
    {
        if (deviceMissing && parsed is MessengerEncryption.Off or MessengerEncryption.Unknown or MessengerEncryption.Down) parsed = MessengerEncryption.DeviceMissing;
        if (parsed == EncryptedState) return parsed;
        MessengerLog.Write($"encrypted state={parsed}");
        EncryptedState = parsed;
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return parsed;
    }

    // The helper's own "encrypted" event: the word "counts" would answer, sent when it changes (encryptedstate.go).
    internal void NoteEncryptedEvent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty("encrypted", out var state) && state.ValueKind == JsonValueKind.String)
            SetEncryptedState(MessengerEncryptedStatus.Parse(state.GetString(), false));
    }

    /// <summary>
    /// Asks the helper how the encrypted connection is doing, for the Settings card, at most once every ten seconds.
    /// True when the answer changed what the card should show. "counts" reads only this PC's helper; nothing goes to
    /// Facebook.
    /// </summary>
    public async Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default)
    {
        if (helper is not { } started)
        {
            var had = EncryptedState;
            return SetEncryptedState(MessengerEncryption.Unknown) != had;
        }
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref encryptedAskedAt) < 10_000) return false;
        Interlocked.Exchange(ref encryptedAskedAt, now);
        var before = EncryptedState;
        try { NoteEncryptedState(await started.CallAsync("counts", null, cancellationToken)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
        return before != EncryptedState;
    }

    public async Task SignOutAsync()
    {
        closing = true;
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            chats = [];
            deviceMissing = false;
            try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch (Exception) { }
            try { if (File.Exists(ChatsPath)) File.Delete(ChatsPath); } catch (Exception) { }
        }
        finally { gate.Release(); }
        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Call with the gate held.
    private async Task CloseAsync()
    {
        ready = false;
        // Connect calls this before opening its first helper. There is no lost connection in that case.
        if (helper is not { } started) return;
        realtime.Writer.TryWrite(new(RealtimeKind.Disconnected));
        helper = null;
        started.Received -= OnHelperEvent;
        try { await started.DisposeAsync(); } catch (Exception) { }
    }

    // The helper process ending is not something the app can paper over: every call after it fails, and without this
    // the backend would keep handing out a dead pipe until the app restarts.
    private int restarts;
    private DateTimeOffset connectedAt;
    // Set when the app is putting the backend down on purpose, so a deliberate close does not look like a helper that
    // fell over and start reconnecting behind it.
    private volatile bool closing;
    private void HelperStopped(HelperConnection stopped)
    {
        if (!ReferenceEquals(helper, stopped)) return;
        helper = null;
        ready = false;
        realtime.Writer.TryWrite(new(RealtimeKind.Disconnected));
        stopped.Received -= OnHelperEvent;
        // A helper that stops takes everything it knew with it, pictures included: the ids the app still holds mean
        // nothing to its replacement. Recorded by how long it ran and what kind of ending it was, in fixed words only.
        var uptime = DateTimeOffset.UtcNow - connectedAt;
        MessengerLog.Write(MessengerFailure.StoppedLine(uptime, stopped.LastError));
        // A helper that ran properly for a while and then stopped is an accident worth recovering from, and its own
        // budget of attempts. One that dies soon after every connection is a loop, and the budget has to hold across
        // those attempts — resetting it on each successful connect meant a helper dying repeatedly signed in forever.
        if (uptime > TimeSpan.FromMinutes(2)) Interlocked.Exchange(ref restarts, 0);
        if (closing) return;
        _ = RecoverAsync();
    }

    // messagix has left its connection loop for good (Facebook closed the socket as unauthorised) and will not come
    // back by itself. The process stays up, so without this nothing ever noticed: Settings said Connected, nothing
    // arrived, and every send waited out its twenty seconds before failing. Stopping the helper hands it to the same
    // bounded recovery a crash gets. That also drops the encrypted socket, which may still be healthy; what it misses
    // is queued by Facebook for the device, and the kept history comes back with the next login.
    private void SocketFailed(HelperConnection started)
    {
        if (closing || !ReferenceEquals(helper, started)) return;
        MessengerLog.Write("socket failed permanently");
        ready = false;
        // Not awaited: this runs on the connection's own pump, and disposing waits for that pump to finish. helper is
        // left pointing at it so HelperStopped recognises the stop and recovers.
        _ = Task.Run(async () => { try { await started.DisposeAsync(); } catch (Exception) { } });
    }

    // Reconnects from the saved session, a few times at most: a helper that cannot stay up must not become a loop of
    // sign-ins against Facebook. Until it comes back, the conversations already known are still served.
    private async Task RecoverAsync()
    {
        var attempt = Interlocked.Increment(ref restarts);
        if (attempt > 3) { MessengerLog.Write("helper recovery given up"); ChatsChanged?.Invoke(this, EventArgs.Empty); return; }
        recovering = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            if (closing) return;
            if (await ReadSavedCookiesAsync() is { } cookies) await ConnectCoreAsync(cookies, CancellationToken.None, adoptLegacy: true, automatic: true);
            // A new helper knows none of the media the old one had registered. ConnectAsync already says every
            // conversation changed once it is ready, so the app reads them again and picks up ids this helper has.
            if (helper is not null) MessengerLog.Write($"helper recovered; {chats.Length} conversations asked for again");
        }
        catch (Exception failure)
        {
            // Settings still offers Connect, and the next stop tries again. A refusal is said by its fixed code: after a
            // socket that failed permanently, session_expired here is how a dead session shows itself.
            MessengerLog.Write($"helper recovery failed: {MessengerFailure.Code(failure)}");
        }
        finally { recovering = false; }
    }

    // The helper announces which conversations and messages moved, by id. The app turns those into the same realtime
    // events Google Messages produces, so one chat list and one conversation view serve both networks.
    internal void OnHelperEvent(string kind, JsonElement data)
    {
        if (kind == "typing" && data.ValueKind == JsonValueKind.Object) { ReceiveTyping(data); return; }
        if (kind == "encrypted") { NoteEncryptedEvent(data); return; }
        if (kind != "changed" || data.ValueKind != JsonValueKind.Object) return;
        // A new message, late identity, or deletion can arrive inside the list's freshness window.
        // Reading the cached summary here would retain a nameless/stale row until some later unrelated event.
        chatsReadAt = default;
        var received = DateTimeOffset.UtcNow;
        foreach (var change in MessengerModels.MessageEvents(data, received))
        {
            NoteCaughtUp(change);
            realtime.Writer.TryWrite(change);
        }
        if (data.TryGetProperty("chats", out var chats) && chats.ValueKind == JsonValueKind.Array)
        {
            var ids = chats.EnumerateArray().Select(id => MessengerModels.Qualify(id.GetString() ?? "")).ToArray();
            if (ids.Length > 0) realtime.Writer.TryWrite(new(RealtimeKind.ChatChanged, null, ids, null, received));
        }
        // A whole conversation deleted, here or on another device: it leaves the list and the cached list with it.
        if (data.TryGetProperty("chatsDeleted", out var gone) && gone.ValueKind == JsonValueKind.Array)
            foreach (var id in gone.EnumerateArray().Select(item => item.GetString()).OfType<string>())
                ForgetChat(id, received);
    }

    // What Facebook delivers as a connection starts is what came while the app was closed. It is older than an arrival, so
    // the photos prepared ahead passed it by, and a reel shared then loaded only once its conversation was open. Named
    // before its event goes out, for a few minutes after connecting and a bounded number per connection; a new message
    // in that time is named too, which only prepares what would have been prepared anyway.
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(3);
    private const int CatchUpMessages = 60;
    private DateTimeOffset catchUpUntil;
    private int catchUpLeft;

    /// <summary>
    /// Messages delivered as a connection started, by conversation and the app's ids, so their pictures can be prepared
    /// whatever their age. Their own events follow. Raised on the helper's event thread.
    /// </summary>
    internal event Action<string, IReadOnlyList<string>>? CaughtUp;

    private void NoteCaughtUp(RealtimeEvent change)
    {
        if (change is not { Kind: RealtimeKind.MessageChanged, ReceiptOnly: false, ChatId: { } chat, Ids: { Count: > 0 } ids } ||
            change.ReceivedAt is not { } at || at > catchUpUntil || catchUpLeft <= 0) return;
        var named = ids.Take(catchUpLeft).ToArray();
        catchUpLeft -= named.Length;
        CaughtUp?.Invoke(chat, named);
    }

    private async Task NoteCatchUpAsync(HelperConnection started)
    {
        await Task.Delay(CatchUpWindow);
        if (ReferenceEquals(helper, started)) MessengerLog.Write($"catchup named={CatchUpMessages - Math.Max(0, catchUpLeft)}");
    }

    private void ForgetChat(string native, DateTimeOffset received)
    {
        var remaining = chats.Where(chat => chat.Id != native).ToArray();
        chats = remaining;
        _ = SaveChatsAsync(remaining);
        realtime.Writer.TryWrite(new(RealtimeKind.ChatDeleted, MessengerModels.Qualify(native), null, null, received));
    }

    // Deletes the conversation on Facebook and everything kept for it on this PC. Facebook starts a deleted conversation
    // fresh if that person writes again. The helper keeps hidden identity separately from the deleted history.
    public async Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to delete conversations.");
        try
        {
            await started.CallAsync("ui.deleteChat", new { chatId = MessengerModels.Native(chatId) }, cancellationToken);
            MessengerLog.Write($"delete chat={MessengerLog.Tag(chatId)} accepted");
            ForgetChat(MessengerModels.Native(chatId), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"delete chat refused: {MessengerFailure.Code(failure)}");
            // The flag used to be the other way round: an errored request read as a known outcome, and a plain refusal
            // as an uncertain one. Nothing consulted it yet, but anything that did would have repeated a deletion.
            throw new SendException(MessengerFailure.IsDeleteUnknown(failure.Message), MessengerFailure.ExplainDelete(failure.Message));
        }
    }

    // Messenger's half of the shared feed. The composite adds the provider scope to its lifecycle and chat events.
    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        if (IsConnected) await receive(new(RealtimeKind.Connected), cancellationToken);
        await realtime.DrainAsync(receive, cancellationToken);
    }

    private static async Task<MessengerChat[]> ReadChatsAsync(HelperConnection connection, CancellationToken cancellationToken)
    {
        var result = await connection.CallAsync("ui.chats", new { count = 200 }, cancellationToken);
        return result.Deserialize<MessengerChat[]>(Json) ?? [];
    }

    private async Task SaveSessionAsync(JsonElement session)
    {
        var plain = Encoding.UTF8.GetBytes(session.GetRawText());
        try
        {
            var encrypted = await protector.ProtectAsync(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            var temporary = SessionPath + ".tmp";
            await NativeBeeper.Storage.DurableFile.WriteAsync(temporary, encrypted);
            File.Move(temporary, SessionPath, true);
        }
        catch (Exception) { /* Keep working without a saved session; the next connect signs in again. */ }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        if (olderThan is not null) return new([], false, null, null);
        // Until the first connection of a session is ready, the conversations from last time keep the list steady. The
        // startup reconnect may already have a helper starting, so readiness rather than the process decides.
        if (!restored && !IsConnected && HasSavedSession) await RestoreChatsAsync();
        else NoteRestoreOverlap("list");
        if (IsConnected && helper is { } started)
        {
            try
            {
                chats = await ReadChatsAsync(started, cancellationToken);
                chatsReadAt = DateTimeOffset.UtcNow;
                await SaveChatsAsync(chats);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Keep the conversations already known if the helper is momentarily unavailable. */ }
        }
        return new(chats.Select(MessengerModels.Map).ToArray(), false, null, null);
    }

    // When many conversations move at once, the list is read once rather than once per conversation. The helper
    // answers one call at a time, and that pipe also carries the pictures and the open conversation.
    private static readonly TimeSpan ListFreshness = TimeSpan.FromSeconds(3);
    private DateTimeOffset chatsReadAt;

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var native = MessengerModels.Native(chatId);
        if (IsConnected && helper is { } started)
        {
            try
            {
                if (DateTimeOffset.UtcNow - chatsReadAt > ListFreshness)
                {
                    chats = await ReadChatsAsync(started, cancellationToken);
                    chatsReadAt = DateTimeOffset.UtcNow;
                }
                // A conversation that started while the app was open is asked for by name; everything else is already
                // in the list that was just read.
                if (!chats.Any(chat => chat.Id == native))
                {
                    var result = await started.CallAsync("ui.chat", new { chatId = native }, cancellationToken);
                    if (result.Deserialize<MessengerChat>(Json) is { } fresh) return MessengerModels.Map(fresh);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Fall back to the conversation already known. */ }
        }
        if (!IsConnected && !restored && HasSavedSession && !chats.Any(chat => chat.Id == native)) await RestoreChatsAsync();
        else NoteRestoreOverlap("chat");
        const string notLoaded = "That Messenger conversation is not loaded. Reconnect Messenger in Settings.";
        var known = chats.FirstOrDefault(chat => chat.Id == native)
            // While Messenger is still connecting, a conversation missing from the known list is not yet a reason to
            // send the user to Settings: its readiness event reads the list and the conversation again.
            ?? throw (IsConnected ? new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Unavailable, notLoaded)
                : NotConnected(notLoaded));
        return MessengerModels.Map(known);
    }

    private const int PageSize = 50;

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // Disconnected is not an authoritative empty conversation. Returning an empty page here lets the shared
        // history cache replace a valid saved snapshot with nothing during a startup reconnect failure.
        if (!IsConnected || helper is not { } started)
            throw NotConnected("Connect Messenger in Settings to load this conversation.");
        var result = await started.CallAsync("ui.messagePage",
            new { chatId = MessengerModels.Native(chatId), count = PageSize, before = olderThan ?? "" }, cancellationToken);
        var providerPage = result.Deserialize<MessengerMessagePage>(Json)
            ?? throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Unavailable,
                "Messenger sent back something Messages couldn't read. Refresh and try again.");
        var messages = providerPage.Messages ?? [];
        // The helper reports a restored page as refreshing only until its first background refresh finishes, judged by
        // what it has done rather than by who has read the page.
        var refreshing = providerPage.Refreshing;
        // How many of the page's messages arrived with anyone's reading against them. The helper's own log says
        // whether Facebook sent the receipts; this says whether they reached the conversation. Counts only.
        var read = messages.Count(message => message.SeenBy is { Length: > 0 });
        var delivered = messages.Count(message => message.Delivered > 0);
        MessengerLog.Write($"history chat={MessengerLog.Tag(chatId)} before={olderThan ?? "none"} returned={messages.Length} withReaders={read} withDelivered={delivered}");
        // The cursor is the oldest message in this page, in milliseconds; a full page means Facebook may have more.
        var oldest = messages.Length > 0
            ? messages[0].At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new(messages.Select(MessengerModels.Map).ToArray(), messages.Length >= PageSize, oldest, null,
            refreshing);
    }

    // Around a moment, from what the helper holds: everything kept for this account was restored into it at login, so a
    // window at any depth is a read from its memory and never a fetch from Facebook.
    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || helper is not { } started)
            throw NotConnected("Connect Messenger in Settings to load this conversation.");
        var result = await started.CallAsync("ui.messageWindow", new
        {
            chatId = MessengerModels.Native(chatId),
            at = WindowMoment(at),
            older = Math.Clamp(older, 0, 200),
            newer = Math.Clamp(newer, 0, 200),
        }, cancellationToken);
        var window = result.Deserialize<MessengerMessageWindow>(Json)
            ?? throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Unavailable,
                "Messenger sent back something Messages couldn't read. Refresh and try again.");
        var messages = window.Messages ?? [];
        MessengerLog.Write($"history window chat={MessengerLog.Tag(chatId)} older={older} newer={newer} returned={messages.Length} " +
            $"more_older={window.MoreOlder} more_newer={window.MoreNewer}");
        return new(messages.Select(MessengerModels.Map).ToArray(), window.MoreOlder, window.MoreNewer);
    }

    // Messenger's times are whole milliseconds, and a window splits at "before" and "from" a moment that can be finer (just past a message, or a merged
    // Google message's time): rounded up, every message lands on the side it did with the finer moment.
    internal static string WindowMoment(DateTimeOffset at) =>
        at.AddTicks(TimeSpan.TicksPerMillisecond - 1).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new InvalidOperationException("Messenger is not connected.");
        var ids = messageIds.Where(MessengerModels.Owns).Distinct(StringComparer.Ordinal).TakeLast(200).Select(MessengerModels.Native).ToArray();
        if (ids.Length == 0) return;
        await started.CallAsync("ui.linkPreviews", new { chatId = MessengerModels.Native(chatId), messageIds = ids }, cancellationToken);
    }

    // Photos, files and profile pictures: the helper holds the Facebook urls and downloads them with the session's own
    // cookies, then hands the bytes over in chunks.
    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false)
    {
        // A video always shows its still rather than the clip. A photo shows its smaller copy only while it is drawn
        // in the conversation: opening it is a request for the photograph, and answering that with the copy Facebook
        // sends for a thumbnail is what made opened pictures look soft.
        using var memory = new MemoryStream();
        await CopyToAsync(ImageSource(attachment, inline), memory, cancellationToken);
        return memory.ToArray();
    }

    private static MessageAttachment ImageSource(MessageAttachment attachment, bool inline) =>
        (inline || attachment.Type == "video") && attachment.SourceUrl is { Length: > 0 } smaller ? attachment with { Id = smaller } : attachment;

    // Where a photo's bytes may be kept on disk: the exact helper media id read, which names the same picture in every
    // helper session (an encrypted photo is its message id; an ordinary one is Facebook's attachment id). Profile pictures
    // have their own cache. A link card's picture the helper made itself is kept too: its id is a digest of the
    // conversation, the message and the link (linkpreview.go, attachLinkPreview), so it names the same picture in every
    // session. Left out, every card in a conversation opened after a launch was blank until Messenger had connected,
    // three to nine seconds, and then fetched from the website again.
    internal static string? ImageCacheKey(MessageAttachment attachment, bool inline)
    {
        if (attachment.Id is null) return null;
        var id = MessengerModels.NativeMedia(ImageSource(attachment, inline));
        return id.Length == 0 ? null : "messenger-image:" + id;
    }

    // Where a GIF's clip may be kept on disk, by the same stable id as its photo would be.
    internal static string ClipCacheKey(MessageAttachment attachment) => "messenger-clip:" + MessengerModels.NativeMedia(attachment);

    public bool CanSave(MessageAttachment attachment) => MessengerModels.OwnsMedia(attachment) && IsConnected;

    // A helper still starting, or a saved session that is about to reconnect, is worth waiting for: its readiness event
    // repairs what failed. Signed out, closing or after recovery has given up, the user has to act instead.
    private NativeBeeper.Core.Backend.BackendException NotConnected(string message) =>
        helper is not null || HasSavedSession && !closing && restarts <= 3
            ? new(NativeBeeper.Core.Backend.BackendFailure.NotReady, "Messenger is reconnecting.")
            : new(NativeBeeper.Core.Backend.BackendFailure.Unavailable, message);

    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || helper is not { } started)
            throw NotConnected("Connect Messenger in Settings to load this media.");
        var id = MessengerModels.NativeMedia(attachment);
        var offset = 0;
        var size = -1;
        while (true)
        {
            var response = await started.CallAsync("ui.mediaRead", new { id, offset, avatar = attachment.Id is null }, cancellationToken);
            var total = response.GetProperty("total").GetInt32();
            var next = response.GetProperty("next").GetInt32();
            var bytes = response.GetProperty("data").GetBytesFromBase64();
            // The size must not change between chunks: a transfer whose source was fetched again half way through
            // would otherwise be stitched together from two different downloads.
            if (size < 0) size = total;
            if (total < 0 || total > Limit || total != size || bytes.Length > Chunk || next != offset + bytes.Length || next > total)
                throw new IOException("Invalid media chunk.");
            await destination.WriteAsync(bytes, cancellationToken);
            offset = next;
            if (offset >= total) return;
        }
    }

    // Text messages. A send whose outcome the helper could not confirm is reported as unknown, never retried: the
    // outbox keeps it pending until the message itself arrives.
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
        => SendTextCoreAsync(chatId, text, null, cancellationToken);

    private async Task<SendReceipt> SendTextCoreAsync(string chatId, string text, string? replyTo, CancellationToken cancellationToken)
    {
        MessengerLog.Write($"send chat={MessengerLog.Tag(chatId)} length={text.Length} reply={replyTo is not null} connected={helper is not null} {NativeBeeper.Presentation.Services.TypingPings.Shared.Field(chatId)}");
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to send messages.");
        ThrowIfRestarting();
        JsonElement result;
        try
        {
            result = await started.CallAsync("ui.sendText", new
            {
                chatId = MessengerModels.Native(chatId),
                text,
                replyTo = replyTo is null ? "" : MessengerModels.Native(replyTo),
            }, cancellationToken);
        }
        catch (OperationCanceledException) { MessengerLog.Write("send cancelled"); throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"send refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), MessengerFailure.Explain(failure.Message));
        }
        catch (Exception) { MessengerLog.Write("send failed in transport"); throw new SendException(true, "Messenger did not confirm the message."); }
        var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            MessengerLog.Write("send returned no message id");
            throw new SendException(true, "Messenger did not confirm the message.");
        }
        MessengerLog.Write($"send accepted message={MessengerLog.Tag(id)}");
        return new(chatId, MessengerModels.Qualify(id));
    }

    // Replies and reactions. Messenger keeps one reaction per person, so adding one replaces whatever this account had
    // on that message; the six offered are the ones Messenger's own picker shows first.
    private static readonly string[] Reactions = ["👍", "❤️", "😆", "😮", "😢", "😡"];

    public async Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (helper is null || !MessengerModels.Owns(chatId) || !MessengerModels.Owns(messageId)) return new(false, [], []);
        var native = MessengerModels.Native(chatId);
        var known = chats.FirstOrDefault(chat => chat.Id == native);
        if (known is null || known.ReadOnly) return new(false, [], []);
        // Which reaction this account already has on the message, so the menu can offer to take it away.
        string[] mine = [];
        try
        {
            var message = await ResolveMessageAsync(chatId, messageId, cancellationToken);
            if (message.IsDeleted) return new(false, [], []);
            var self = MessengerModels.Person(known.Participants.FirstOrDefault(person => person.Self)?.NativeId ?? "");
            mine = message.Reactions.Where(item => item.ParticipantId == self).Select(item => item.Key).Distinct().ToArray();
            // Only your own words can be corrected, and only words — Facebook has no way to change a file already sent.
            var own = message.IsOutgoing;
            var edit = own && message.Attachments.Count == 0 && !string.IsNullOrEmpty(message.Text);
            // Taking a message back for everyone is for your own; hiding one from yourself works on anyone's, except
            // in an encrypted conversation, which offers no such thing at all.
            return new(true, Reactions, mine, edit, !known.Encrypted, own);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, [], []); }
    }

    // Read markers. Only a conversation this account can reply to is marked read, or an encrypted one whatever its
    // connection: with the encrypted connection away the helper still writes the thread watermark on the ordinary one,
    // which is what clears it on Facebook's web and phone (it sent nothing at all before, 2026-10-22). Failure is quiet —
    // a missed read marker is not worth interrupting anyone over.
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var native = MessengerModels.Native(chatId);
        return Task.FromResult(helper is not null && chats.Any(chat => chat.Id == native && (!chat.ReadOnly || chat.Encrypted)));
    }

    public async Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started || !MessengerModels.Owns(messageId))
            throw new InvalidOperationException("Messenger can't mark this as read.");
        try
        {
            var result = await started.CallAsync("ui.markRead", new { chatId = MessengerModels.Native(chatId), messageId = MessengerModels.Native(messageId) }, cancellationToken);
            if (!result.TryGetProperty("outcome", out var outcome) || outcome.GetString() != "accepted")
                throw new InvalidOperationException("mark_read_failed");
            var sent = result.TryGetProperty("receiptsSent", out var count) ? count.GetInt32() : -1;
            MessengerLog.Write($"mark read chat={MessengerLog.Tag(chatId)} completed receipts={sent}");
        }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"mark read refused: {MessengerFailure.Code(failure)}");
            // Keep failure quiet in the view, but do not let it cache this attempt as acknowledged.
            throw;
        }
    }

    // Changing a message after it has gone. The helper decides which connection carries it; both are one call here.
    // Nothing is ever retried: an edit or an unsend whose outcome is unknown stays unknown, the same rule sending has.
    public async Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to edit messages.");
        if (string.IsNullOrWhiteSpace(text)) throw new SendException(false, "An edited message still needs words.");
        try
        {
            await started.CallAsync("ui.editMessage", new
            {
                chatId = MessengerModels.Native(chatId), messageId = MessengerModels.Native(messageId), text,
            }, cancellationToken);
            MessengerLog.Write($"edit chat={MessengerLog.Tag(chatId)} accepted");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"edit refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), MutationReason(failure.Message, edit: true));
        }
    }

    public async Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to remove messages.");
        try
        {
            await started.CallAsync("ui.deleteMessage", new
            {
                chatId = MessengerModels.Native(chatId), messageId = MessengerModels.Native(messageId), everyone = forEveryone,
            }, cancellationToken);
            MessengerLog.Write($"delete chat={MessengerLog.Tag(chatId)} everyone={forEveryone} accepted");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"delete refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), MutationReason(failure.Message, edit: false));
        }
    }

    private static string MutationReason(string code, bool edit) => code switch
    {
        "not_yours" => edit ? "Only your own messages can be edited." : "Only your own messages can be taken back for everyone.",
        "message_unavailable" => "That message is no longer loaded.",
        "edit_rejected" => "Facebook refused the edit. It may be too old to change.",
        "delete_me_only_unsupported" => "An end-to-end encrypted conversation has no way to hide a message from you alone. It can only be taken back for everyone.",
        "e2ee_off" => "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to change messages in it.",
        "e2ee_offline" => "The encrypted connection is not up, so nothing was changed. It will work once Messenger reconnects.",
        _ => MessengerFailure.Explain(code),
    };

    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default)
        => SendTextCoreAsync(chatId, text, messageId, cancellationToken);

    public async Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to react to messages.");
        JsonElement result;
        try
        {
            result = await started.CallAsync("ui.react", new
            {
                chatId = MessengerModels.Native(chatId),
                messageId = MessengerModels.Native(messageId),
                key = reaction,
                remove,
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"react refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), failure.Message switch
            {
                "react_rejected" => "Facebook refused the reaction.",
                "reaction_unavailable" => "That message is no longer loaded, so it cannot be reacted to.",
                _ => MessengerFailure.Explain(failure.Message),
            });
        }
        catch (Exception) { throw new SendException(true, "Messenger did not confirm the reaction."); }
        if (!result.TryGetProperty("outcome", out var outcome) || outcome.GetString() != "accepted")
            throw new SendException(true, "Reaction outcome is unknown. Check the message before trying again.");
        MessengerLog.Write($"react chat={MessengerLog.Tag(chatId)} remove={remove}");
    }

    // The message as Facebook now has it, by its own id or by the one a pending send was given.
    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started)
            throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Unavailable, "Messenger is not connected.");
        var result = await started.CallAsync("ui.message",
            new { chatId = MessengerModels.Native(chatId), id = MessengerModels.Native(messageId) }, cancellationToken);
        var message = result.Deserialize<MessengerMessage>(Json)
            ?? throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Unavailable, "That message is not loaded.");
        MessengerLog.Write($"resolved chat={MessengerLog.Tag(chatId)} message={MessengerLog.Tag(messageId)}");
        return MessengerModels.Map(message);
    }

    // The same "ui.message", which the helper answers from its own store and never from Facebook, for a display-only
    // caller: skipped rather than queued when the pipe is busy, bounded at two seconds, and silent. A miss, a busy pipe
    // and a timeout are all null. No log line: one per arrival would drown the rest.
    // One at a time: the activity stream asks for several at once, and a skippable call finds the write gate still held
    // while the frame before it is flushed, so parallel peeks skipped one another rather than the rest of the app's traffic.
    internal async ValueTask<ChatMessage?> PeekMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) return null;
        var entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            await peeks.WaitAsync(linked.Token);
            entered = true;
            var result = await started.CallAsync("ui.message",
                new { chatId = MessengerModels.Native(chatId), id = MessengerModels.Native(messageId) }, linked.Token, skipIfBusy: true);
            // A skipped call now comes back as {"skipped":true} rather than throwing, and is a miss, not an empty message.
            if (NativeBeeper.GoogleMessages.Spike.HelperRequests.IsSkipped(result)) return null;
            return result.ValueKind == JsonValueKind.Object && result.Deserialize<MessengerMessage>(Json) is { } message ? MessengerModels.Map(message) : null;
        }
        catch (Exception) { return null; }
        finally { if (entered) peeks.Release(); }
    }
    private readonly SemaphoreSlim peeks = new(1);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
    {
        // A restart in progress (Connection health): the composer waits for the new helper rather than sending to the old.
        if (restarting) return Task.FromResult(new SendPermission(false, "Messenger is reconnecting…", Pending: true));
        if (helper is null)
            return Task.FromResult(NotConnected("").Failure == NativeBeeper.Core.Backend.BackendFailure.NotReady
                ? new SendPermission(false, "Waiting for Messenger to connect…", Pending: true)
                : new SendPermission(false, "Connect Messenger in Settings to send messages."));
        var native = MessengerModels.Native(chatId);
        var known = chats.FirstOrDefault(chat => chat.Id == native);
        var permission = known switch
        {
            { Encrypted: true, ReadOnly: true } => new SendPermission(false,
                "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to read and reply to it here."),
            { ReadOnly: true } => new SendPermission(false, "This Messenger conversation cannot be replied to."),
            _ => new SendPermission(true, "Messenger"),
        };
        MessengerLog.Write($"permission chat={MessengerLog.Tag(chatId)} known={known is not null} allowed={permission.Allowed}");
        return Task.FromResult(permission);
    }

    // Attachments. Facebook takes the file first and gives it a number, and the message then names that number, so
    // the bytes go over the pipe a chunk at a time before anything is sent. An encrypted conversation uploads them
    // itself instead, which the helper decides; both are the same call from here.
    private const string EncryptedNotYet = "Files can't be sent to an end-to-end encrypted conversation from Messages.";
    private readonly SemaphoreSlim transfers = new(1);

    private SendPermission FilePermission(string chatId)
    {
        if (helper is null) return new(false, "Connect Messenger in Settings to send files.");
        var known = chats.FirstOrDefault(chat => chat.Id == MessengerModels.Native(chatId));
        // An encrypted conversation this computer is not registered for cannot be replied to at all, in words or files.
        if (known is { Encrypted: true, ReadOnly: true }) return new(false,
            "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to send files to it.");
        if (known is { ReadOnly: true }) return new(false, "This Messenger conversation cannot be replied to.");
        return new(true, "Messenger");
    }

    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default)
        => Task.FromResult(FilePermission(chatId));
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => Task.FromResult(file.Length > Limit
            ? new SendPermission(false, $"That file is larger than {Limit / (1024 * 1024)} MB.")
            : FilePermission(chatId));
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default)
        => Task.FromResult(new AttachmentCaptionSupport(FilePermission(chatId).Allowed &&
            MessengerModels.SupportsAttachmentCaption(chats.FirstOrDefault(chat => chat.Id == MessengerModels.Native(chatId))), null));

    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) =>
        files.Count is >= 2 and <= AttachmentBatchRules.ItemLimit &&
        files.All(file => file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !file.MimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)) && FilePermission(chatId).Allowed;

    public async Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
    {
        if (!CanSendBatch(chatId, files)) throw new SendException(false, "Messenger can group two to ten still photos at a time.");
        foreach (var file in files)
        {
            var permission = await GetFilePermissionAsync(chatId, file, cancellationToken);
            if (!permission.Allowed) throw new SendException(false, permission.Reason);
        }
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to send photos.");
        ThrowIfRestarting();
        await transfers.WaitAsync(cancellationToken).ConfigureAwait(false);
        var submitted = false;
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                await using var stream = await file.OpenRead(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[Chunk];
                var offset = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (offset + count > Limit || offset + count > file.Length) throw new IOException("File changed or too large.");
                    var ack = await started.CallAsync("ui.mediaBatchStage", new
                    {
                        index,
                        offset,
                        reset = index == 0 && offset == 0,
                        data = Convert.ToBase64String(buffer, 0, count),
                    }, cancellationToken).ConfigureAwait(false);
                    offset += count;
                    if (ack.GetProperty("next").GetInt32() != offset) throw new IOException("Upload offset mismatch.");
                }
                if (offset == 0 || offset != file.Length) throw new IOException("File changed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            MessengerLog.Write($"sendMediaBatch chat={MessengerLog.Tag(chatId)} count={files.Count} bytes={files.Sum(file => file.Length)}");
            var result = await started.CallAsync("ui.sendMediaBatch", new
            {
                chatId = MessengerModels.Native(chatId),
                files = files.Select(file => new { name = Path.GetFileName(file.Name), mime = file.MimeType }).ToArray(),
            }, cancellationToken).ConfigureAwait(false);
            var outcome = result.TryGetProperty("outcome", out var state) ? state.GetString() : null;
            if (outcome != "accepted") throw new SendException(true, "Messenger did not confirm the photo set. Check the conversation before sending it again.");
            var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
            if (string.IsNullOrEmpty(id)) throw new SendException(true, "Messenger did not confirm the photo set. Check the conversation before sending it again.");
            MessengerLog.Write($"sendMediaBatch accepted message={MessengerLog.Tag(id)} count={files.Count}");
            var ids = result.TryGetProperty("ids", out var batchIds) && batchIds.ValueKind == JsonValueKind.Array
                ? batchIds.EnumerateArray().Select(value => value.GetString()).OfType<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(MessengerModels.Qualify).ToArray() : null;
            if (ids is not null && (ids.Length != files.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length))
                throw new SendException(true, "Messenger did not confirm every photo in the set. Check the conversation before sending it again.");
            return new(chatId, MessengerModels.Qualify(id), ids);
        }
        catch (SendException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"sendMediaBatch refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), failure.Message switch
            {
                "e2ee_off" => "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to send photos to it.",
                "e2ee_offline" => "The encrypted connection is not up, so the photos were not sent. They will work once Messenger reconnects.",
                "upload_failed" => "Messenger could not upload the photo set. Nothing was sent.",
                "send_rejected" => "Facebook refused the photo set. Its size or image kinds may not be allowed.",
                _ => MessengerFailure.Explain(failure.Message),
            });
        }
        catch (Exception)
        {
            throw new SendException(submitted, submitted
                ? "The photo set's outcome is unknown. Check the conversation before sending it again."
                : "The photo set was not sent. Check the files and connection, then try again.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await started.CallAsync("ui.mediaBatchDiscard", new { }, cleanup.Token).ConfigureAwait(false); }
            catch (Exception) { /* The next batch starts with reset=true; process shutdown also releases staged bytes. */ }
            transfers.Release();
        }
    }

    public async Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
    {
        var permission = await GetFilePermissionAsync(chatId, file, cancellationToken);
        if (!permission.Allowed) throw new SendException(false, permission.Reason);
        if (!string.IsNullOrWhiteSpace(caption) && !(await GetCaptionSupportAsync(chatId, file, cancellationToken)).Supported)
            throw new SendException(false, "Send the text as a separate message: Facebook doesn't show captions in encrypted conversations.");
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings to send files.");
        ThrowIfRestarting();
        await transfers.WaitAsync(cancellationToken);
        // Until the bytes are all across, nothing has been sent and saying so is safe. After that the outcome is
        // Facebook's to report, and an uncertain one stays uncertain — nothing here ever sends a file twice.
        var submitted = false;
        try
        {
            await using var stream = await file.OpenRead(cancellationToken);
            var buffer = new byte[Chunk];
            var offset = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (offset + count > Limit || offset + count > file.Length) throw new IOException("File changed or too large.");
                var ack = await started.CallAsync("ui.mediaStage",
                    new { offset, data = Convert.ToBase64String(buffer, 0, count) }, cancellationToken);
                offset += count;
                if (ack.GetProperty("next").GetInt32() != offset) throw new IOException("Upload offset mismatch.");
            }
            if (offset == 0 || offset != file.Length) throw new IOException("File changed.");
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            MessengerLog.Write($"sendMedia chat={MessengerLog.Tag(chatId)} bytes={offset}");
            var result = await started.CallAsync("ui.sendMedia", new
            {
                chatId = MessengerModels.Native(chatId),
                name = Path.GetFileName(file.Name),
                mime = file.MimeType,
                text = caption ?? "",
                voice = file.IsVoiceNote,
                // A recording's length, which an encrypted conversation carries with the message; zero for any other file.
                seconds = file.IsVoiceNote ? file.DurationSeconds ?? 0 : 0,
            }, cancellationToken);
            var outcome = result.TryGetProperty("outcome", out var state) ? state.GetString() : null;
            if (outcome != "accepted")
                throw new SendException(true, "Messenger did not confirm the file. Check the conversation before sending it again.");
            var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
            if (string.IsNullOrEmpty(id))
                throw new SendException(true, "Messenger did not confirm the file. Check the conversation before sending it again.");
            MessengerLog.Write($"sendMedia accepted message={MessengerLog.Tag(id)}");
            return new(chatId, MessengerModels.Qualify(id));
        }
        catch (SendException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            MessengerLog.Write($"sendMedia refused: {MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUnknown(failure.Message), failure.Message switch
            {
                "media_encrypted_unsupported" => EncryptedNotYet,
                "e2ee_off" => "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to send files to it.",
                "e2ee_offline" => "The encrypted connection is not up, so the file was not sent. It will work once Messenger reconnects.",
                "send_rejected" => "Facebook refused the file. Its size or kind may not be allowed.",
                _ => MessengerFailure.Explain(failure.Message),
            });
        }
        catch (Exception)
        {
            throw new SendException(submitted, submitted
                ? "The file's outcome is unknown. Check the conversation before sending it again."
                : "That file could not be prepared. It was not sent.");
        }
        finally { transfers.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        closing = true;
        await gate.WaitAsync();
        try { await CloseAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }

    // The service provider disposes synchronously at shutdown. Closing the pipe ends the helper process, so the
    // remaining teardown does not need to be waited for.
    public void Dispose()
    {
        closing = true;
        var started = helper;
        helper = null;
        if (started is not null) _ = started.DisposeAsync().AsTask();
    }
}
