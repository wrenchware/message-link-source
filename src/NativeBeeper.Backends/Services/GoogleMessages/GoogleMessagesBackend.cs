using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend(GoogleArchive archive, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] IAccessTokenStore tokens, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(BackendServices.HostTokensKey)] PrivateState privateState, IStateProtector protector, ProviderAccountSlot? accountSlot = null) : IBeeperBackend, IChatReader, IMessageSender, IRealtimeFeed, ITypingService, IReadStateWriter, IMessageActions, ILinkPreviewLoader, IMessageMutations, IDisposable
{
    internal ProviderAccountSlot Slot { get; } = accountSlot ?? ProviderAccountSlot.Legacy;
    internal GoogleSessionProfile Files { get; } = tokens as GoogleSessionProfile ?? new(protector, accountSlot);
    internal GoogleAccountModels Models { get; } = new((accountSlot ?? ProviderAccountSlot.Legacy).Ids);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, GoogleChat> chats = new();
    private readonly ConcurrentDictionary<string, GoogleMessage> messages = new();
    internal int HeldMessages => messages.Count;
    private readonly Channel<RealtimeEvent> realtime = Channel.CreateBounded<RealtimeEvent>(256);
    private readonly DirectTypingState typing = new();
    private readonly HashSet<string> typingChats = [];
    private readonly object typingLock = new();
    private HelperConnection? helper;
    private volatile bool connected;
    private volatile bool authenticationRequired;
    private bool stopping;
    public bool CanReceive => connected;
    internal async Task<JsonElement> AvatarCallAsync(string nativeChat,CancellationToken token)
    {
        // Avatar loading should not reconnect a stopped/unavailable backend. Not connected yet is no answer about the
        // picture, so it is a waiting failure, which the avatar layer neither remembers nor counts (GoogleMediaBackend):
        // said as Unavailable, a launch's early asks were held as failed for ten minutes in memory and thirty on disk,
        // and three of them paused every avatar for two (2026-10-02).
        if(!connected) throw NotConnectedYet();
        // Chat-list data already identifies direct-chat participants; avoid a second phone
        // conversation lookup for every visible avatar. Groups retain the existing lookup.
        string? participantId = null;
        // "chat|participant" asks for one member's photo, which is how a group builds its composite avatar.
        if (nativeChat.Split('|') is [var groupChat, var member] && member.Length > 0)
        {
            nativeChat = groupChat;
            participantId = member;
        }
        else if (chats.TryGetValue(nativeChat, out var chat) && !chat.Group)
        {
            var people = chat.Participants.Where(p => !p.Self).ToArray();
            if (people.Length == 1) participantId = people[0].NativeId;
        }
        return await ReadAsync("ui.avatar",new {chatId=nativeChat,participantId},token);
    }
    internal async Task<JsonElement> MediaCallAsync(string method, object parameters, CancellationToken token)
    {
        NoteForeground();
        await ConnectAsync(token);
        // Reading media skips the timeout below, because the phone can take far longer to produce a full-size photo
        // than any other call. It skipped the logging with it, so a picture that failed to download left no trace on
        // either side — a grid of warning triangles and nothing to say why.
        if (method == "ui.mediaRead")
        {
            var mediaBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var media = await DrainReadAsync(helper!, method, parameters, token);
                GoogleMessagesLog.Write($"read=media_read outcome=ok ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(mediaBegan).TotalMilliseconds}");
                return media;
            }
            catch (Exception mediaFailure)
            {
                GoogleMessagesLog.Write($"read=media_read outcome=failed reason={GoogleMessagesLog.Reason(mediaFailure)} ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(mediaBegan).TotalMilliseconds}");
                throw;
            }
        }
        var operation = GoogleMessagesLog.Operation(method);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(135));
        try
        {
            var result = await helper!.CallAsync(method,parameters,timeout.Token);
            // A reaction and an attachment send also say which description of the conversation they were checked against.
            GoogleMessagesLog.Write($"operation={operation} outcome=ok ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}{GoogleMessagesLog.Conversation(result)}");
            return result;
        }
        catch (Exception failure)
        {
            GoogleMessagesLog.Write($"operation={operation} outcome=failed reason={GoogleMessagesLog.Reason(failure)} ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
            throw;
        }
    }
    public bool CanSend => connected;
    // A resync this backend asks for (an event it could not read, or one dropped by a full queue) means the conversations it
    // holds missed something, so the last whole list is not answered again (GoogleDirectory.CanReuseList).
    private void Publish(RealtimeEvent evt)
    {
        if (evt.Kind == RealtimeKind.ResyncRequired) lastList = null;
        if (!realtime.Writer.TryWrite(evt)) { lastList = null; while(realtime.Reader.TryRead(out _)) {} realtime.Writer.TryWrite(new(RealtimeKind.ResyncRequired)); }
    }
    internal GoogleIncomingCards IncomingCards { get; } = new(archive, tokens);
    internal Func<string?, CancellationToken, Task>? ValidateAccount { get; set; }
    internal async Task<string?> SavedIdentityAsync()
    {
        if (!Files.SessionSaved) return null;
        var bytes = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Files.SessionFile));
        try { using var document = JsonDocument.Parse(bytes); return AccountIdentity(document.RootElement); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string? AccountIdentity(JsonElement session) => GooglePairingIdentity.Identity(session).Item2 is { } account ? ProviderAccounts.Identity("google", account) : null;
    private async Task ConnectAsync(CancellationToken token)
    {
        if (connected) return;
        ThrowIfNotSetUp();
        ThrowIfNotConnectable();
        await gate.WaitAsync(token);
        var held = true;
        try
        {
            if (connected) return;
            // Another caller's attempt may have failed, or the pairing been lost, while this one waited for the lock.
            ThrowIfNotConnectable();
            // A sign-in about as old as Google allows is refreshed from the kept profile first; connecting with it would
            // only be refused (connect_refresh at every launch after a couple of hours closed, 2026-09-24).
            if (!stopping && Files.SessionSaved && PresumeExpired()) throw GoogleReadFailure.Renewing();
            held = false;
        }
        finally { if (held) gate.Release(); }
        using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt = current;
        try
        {
            if (stopping) throw new OperationCanceledException();
            GoogleMessagesLog.Write($"connect starting sign_in_age_min={Files.Clock.AgeMinutes(DateTimeOffset.UtcNow)?.ToString() ?? "unknown"}");
            if (!File.Exists(Files.SessionFile)) throw new BackendException(BackendFailure.AuthenticationRequired,"Pair your Google Messages account to connect.");
            var oldHelper = helper;
            if (oldHelper is not null && latestSession is { } lastKnown) await PersistSessionAsync(oldHelper,lastKnown,"replaced");
            helper = null;
            latestSession = null;
            if (oldHelper is not null) await oldHelper.DisposeAsync();
            // The old pump is now finished. In particular, a queued Disconnected or
            // AuthenticationRequired must not arrive after the new Connected event:
            // SyncViewModel would remain offline even with a healthy new connection.
            while (realtime.Reader.TryRead(out _)) { }
            helper = await HelperConnection.StartAsync(HelperExecutables.PathFor("gmessages-helper"), null, current.Token, Slot.HelperEnvironment);
            helper.Scope = Models.Qualify("");
            var owner = helper;
            helper.Received += (kind, data) =>
            {
                if (!ReferenceEquals(helper, owner)) return;
                if (kind == "session")
                {
                    var snapshot=new SessionSnapshot(data.Clone());
                    latestSession=snapshot;
                    if (connected) _=PersistSessionAsync(owner,snapshot,"update");
                    return;
                }
                Receive(kind, data);
            };
            helper.Disconnected += () => { if (!ReferenceEquals(helper, owner)) return; GoogleMessagesLog.Write("helper stopped"); connected=false; lock(typingLock) typing.Clear(); Publish(new(RealtimeKind.Disconnected)); };
            var hello = await helper.CallAsync("hello", null, current.Token);
            if (hello.GetProperty("protocol").GetInt32() != 7) throw new BackendException(BackendFailure.Unsupported,HelperStartFailure.OutOfDateSentence);
            GoogleMessagesLog.Write("helper hello protocol=7");
            var bytes = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Files.SessionFile,current.Token));
            try { using var auth = JsonDocument.Parse(bytes); if (ValidateAccount is { } validate) await validate(AccountIdentity(auth.RootElement), current.Token); NoteSessionKind(auth.RootElement); await helper.CallAsync("connect",auth.RootElement,current.Token); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            // Connect starts the library's listener asynchronously. Verify an authenticated
            // read before reporting Connected; a running helper alone does not prove login.
            await helper.CallAsync("ui.chatList", new { count = 1 }, current.Token);
            Interlocked.Increment(ref connectionNumber);
            headsAtConnect.Clear(); freshFrom.Clear();
            // A new helper session has handed nothing out yet (nothing is read before this): what the last one named no
            // longer says a picture is gone.
            NativeBeeper.Presentation.Services.MediaNews.Forget(Models.Qualify(""));
            connected = true;
            authenticationRequired = false;
            NoteConnected();
            GoogleMessagesLog.Write("connect ok");
            if (latestSession is { } refreshed) await PersistSessionAsync(owner,refreshed,"connect");
        }
        catch (BackendException failure) { GoogleMessagesLog.Write($"connect failed reason={GoogleMessagesLog.Reason(failure)}"); throw; }
        // Pairing ended this attempt: the caller hears that Google is busy pairing, not that it was cancelled.
        catch (OperationCanceledException) when (!token.IsCancellationRequested && signingIn)
        { GoogleMessagesLog.Write("connect ended reason=signing_in"); throw new BackendException(BackendFailure.NotReady,"Google Messages is signing in. It connects when that finishes."); }
        catch (OperationCanceledException failure) { GoogleMessagesLog.Write($"connect failed reason={GoogleMessagesLog.Reason(failure)}"); throw; }
        catch (InvalidOperationException ex)
        {
            connected=false;
            switch (GoogleSignInState.ConnectRefusal(ex.Message))
            {
                case GoogleConnectRefusal.PairingLost: MarkPairingLost("connect"); throw GoogleReadFailure.Map("pairing_lost");
                // Google refused the saved sign-in at connect, as it refuses a poll mid-session: the same expiry, renewed
                // the same way. The helper has usually said so already, naming the step (connect_errors.go); this is
                // for a helper that did not, and does nothing the second time.
                case GoogleConnectRefusal.SignInExpired: MarkSignInExpired("connect"); break;
            }
            var failure=GoogleReadFailure.Map(ex.Message);
            authenticationRequired=failure.Failure==BackendFailure.AuthenticationRequired;
            if (!authenticationRequired) NoteConnectFailed();
            GoogleMessagesLog.Write($"connect failed reason={GoogleMessagesLog.Reason(failure)}");
            // The window is renewing it: whoever asked hears that, not to go and renew it themselves.
            throw authenticationRequired && signIn.Held(DateTimeOffset.UtcNow) ? GoogleReadFailure.Renewing() : failure;
        }
        // A helper Windows would not start (no existence check here) is said as that, not as a phone problem; its number is
        // in the line above as reason=win32_<n>.
        catch (Exception failure) { connected=false; GoogleMessagesLog.Write($"connect failed reason={GoogleMessagesLog.Reason(failure)}"); NoteConnectFailed(); throw new BackendException(BackendFailure.Unavailable,HelperStartFailure.Sentence(failure) ?? "Google Messages couldn't connect. Check that your phone is on and connected to the internet, then try again."); }
        finally { if (ReferenceEquals(attempt, current)) attempt = null; gate.Release(); }
    }
    private void Receive(string kind, JsonElement value)
    {
        try
        {
            if (kind=="typing")
            {
                var chat=Models.Qualify(value.GetProperty("chatId").GetString()!);
                lock(typingLock)
                {
                    typingChats.Add(chat);
                    typing.Apply(chat,value.GetProperty("userId").GetString()!,value.GetProperty("active").GetBoolean(),value.GetProperty("expiresAt").GetDateTimeOffset(),DateTimeOffset.UtcNow);
                }
            }
            else if(kind=="connection")
            {
                // Fixed codes from the helper (connection_state.go); still reduced to known characters before they are logged.
                var state=GoogleReconnect.Code(value.TryGetProperty("state",out var named) ? named.GetString() : null);
                var reason=value.TryGetProperty("reason",out var why) && why.GetString() is { Length: > 0 } given ? GoogleReconnect.Code(given) : "none";
                GoogleMessagesLog.Write($"connection state={state} reason={reason}");
                if (state=="pairing_lost") MarkPairingLost(reason);
                else if (state=="signed_out") MarkSignInExpired(reason);
                else if (state=="logged_out") NoteLogoutMarker();
                // The library stops listening for good after a fatal error it does not call a refused sign-in (a 403, a
                // refresh Google answered "not found"), and the app went on showing Google Messages as connected with
                // nothing arriving. A reconnect lets the connect's own refusal say which it was.
                else if (state=="listen_failed" && !signIn.SignInExpired && !signIn.PairingLost)
                {
                    connected=false;
                    lock(typingLock) typing.Clear();
                    GoogleMessagesLog.Write("listen failed action=reconnect");
                    Publish(new(RealtimeKind.Disconnected));
                }
            }
            // The phone's own settings, reduced by the helper to whether Google Messages is its default SMS app.
            else if(kind=="settings") NoteSettings(value);
            // The phone's SIMs when it has more than one, by place, carrier and colour (sims.go), for the composer's chip.
            else if(kind=="sims") NoteSims(value);
            else if(kind=="library")
            {
                // The library's own warnings, which were discarded; the helper passes only a level and a message made of
                // words, and they are checked again here.
                var level=GoogleReconnect.Code(value.TryGetProperty("level",out var severity) ? severity.GetString() : null);
                var said=GoogleReconnect.LibraryMessage(value.TryGetProperty("message",out var text) ? text.GetString() : null);
                var status=value.TryGetProperty("status",out var code) && code.TryGetInt32(out var number) && number is >= 100 and <= 599 ? $" status={number}" : "";
                GoogleMessagesLog.Write($"library level={level} message={said}{status}");
            }
            else if(kind=="cookies" && value.TryGetProperty("merged",out var merged) && merged.ValueKind==JsonValueKind.Object)
            {
                // Cookies the library took from Google's ordinary answers since the helper last looked (cookie_watch.go),
                // by name, never a value. Until 0.1.307 the helper also asked accounts.google.com/RotateCookies every nine
                // minutes, which only ever changed the SIDCC cookies these answers change anyway.
                GoogleMessagesLog.Write($"cookies merged added={GoogleReconnect.CookieNames(merged,"added")} changed={GoogleReconnect.CookieNames(merged,"changed")} removed={GoogleReconnect.CookieNames(merged,"removed")}");
            }
            else if(kind=="chat" && value.Deserialize<GoogleChat>(Json) is { } c)
            {
                // Google still describes a dropped placeholder. That does not bring it back: it would sit in the list refusing every send.
                if (!c.Hidden && placeholders.ContainsKey(c.Id)) { GoogleMessagesLog.Write($"event=chat state=placeholder chat={GoogleMessagesLog.Tag(c.Id)}"); ArchiveChats([c with { Hidden = true }]); return; }
                ArchiveChats([c]);
                if (c.Hidden) { GoogleMessagesLog.Write($"event=chat state=deleted chat={GoogleMessagesLog.Tag(c.Id)}"); RemoveChat(c.Id,out _); ArrivalHold.Discard(c.Id); Publish(new(RealtimeKind.ChatDeleted,Models.Qualify(c.Id))); }
                else { var was=chats.TryGetValue(c.Id,out var held) ? held.Unread : (bool?)null; GoogleMessagesLog.Write($"event=chat state=changed chat={GoogleMessagesLog.Tag(c.Id)} unread={c.Unread} was={(was?.ToString() ?? "unknown")} spam={c.Spam}"); SetChat(c); ArrivalHold.Deliver(c.Id,new(RealtimeKind.ChatChanged,Models.Qualify(c.Id))); }
            }
            else if((kind=="message" || kind=="messageUpdate") && value.Deserialize<GoogleMessage>(Json) is { } m)
            { var from=messages.TryGetValue(m.Id,out var before) && before.ChatId!=m.ChatId ? before.ChatId : null; var isNew=messages.TryAdd(m.Id,m); GoogleMessagesLog.Write($"event=message state={(isNew ? "new" : "updated")} chat={GoogleMessagesLog.Tag(m.ChatId)} message={GoogleMessagesLog.Tag(m.Id)} status={GoogleMessagesLog.Status(m.Status)} chatUnread={(chats.TryGetValue(m.ChatId,out var owner) ? owner.Unread.ToString() : "unknown")}{ReaderCounts([m],owner)}"); if(!isNew) RememberMessage(m); else ArchiveMessages([m]); if(isNew && kind=="message") NoteRealtimeMessage(); if(isNew && kind=="message" && GoogleModels.EndsTyping(m,owner,DateTimeOffset.UtcNow,out var typist)) lock(typingLock) typing.Ended(Models.Qualify(m.ChatId),typist); if(isNew) HoldIfUnsettled(m); ReinstateIfReal(m); ArrivalHold.Deliver(m.ChatId,new(RealtimeKind.MessageChanged,Models.Qualify(m.ChatId),[Models.Qualify(m.Id)],isNew && kind=="message",DateTimeOffset.UtcNow)); ReleaseIfSettled(m.ChatId); if(from is not null) MessageMoved(from,m); }
        }
        catch (Exception) { GoogleMessagesLog.Write("event=invalid action=resync"); Publish(new(RealtimeKind.ResyncRequired)); }
    }
    public async Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken=default) { ClearPause(); await ConnectAsync(cancellationToken); return new("Google Messages","Direct",true); }
    public async Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken=default) { await ConnectAsync(cancellationToken); return [new(Models.Account,"Google Messages","Google Messages","Connected","libgm","direct")]; }
    public async Task<ChatSummary> GetChatAsync(string chatId,CancellationToken cancellationToken=default)
    {
        var id=Models.Native(chatId);
        if(!chats.TryGetValue(id,out var chat)) return Summary(await RefreshChatAsync(chatId,cancellationToken));
        return chat is null ? throw new BackendException(BackendFailure.Unavailable,"That conversation isn't available right now. Refresh and try again.") : Summary(chat);
    }
    // Conversation history uses 50 messages per phone round trip; send-status checks use smaller pages.
    private const int HistoryPageSize = 50, StatusPageSize = 20;
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId,string? olderThan=null,CancellationToken cancellationToken=default)
        => FetchMessagePageAsync(chatId,olderThan,HistoryPageSize,cancellationToken);
    private async Task<Page<ChatMessage>> FetchMessagePageAsync(string chatId,string? olderThan,int count,CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        await GetChatAsync(chatId,cancellationToken);
        using var cursor = olderThan is null ? null : JsonDocument.Parse(olderThan);
        var result=await ReadAsync("ui.messagePage",new {chatId=Models.Native(chatId),cursor=cursor?.RootElement,count},cancellationToken);
        var list=result.GetProperty("messages").Deserialize<GoogleMessage[]>(Json) ?? [];
        GoogleMessagesLog.Write($"history chat={GoogleMessagesLog.Tag(chatId)} returned={list.Length} older={olderThan is not null}{ReaderCounts(list,chats.GetValueOrDefault(Models.Native(chatId)))}");
        foreach(var message in list) if(message.ChatId==Models.Native(chatId)) RememberMessage(message);
        var next=result.GetProperty("cursor").ValueKind==JsonValueKind.Null ? null : result.GetProperty("cursor").GetRawText();
        var more=list.Length>0 && next is not null && next!=olderThan;
        return new(list.Where(m=>m.ChatId==Models.Native(chatId)).Select(m=>Models.Map(m,chats[m.ChatId])).OrderBy(m=>m.Timestamp).ToArray(),more,more?next:null,null);
    }
    public async Task<ChatMessage> ResolveMessageAsync(string chatId,string messageId,CancellationToken cancellationToken=default)
    {
        if (messageId.StartsWith(PendingPrefix, StringComparison.Ordinal)) return await ResolvePendingAsync(chatId,messageId,cancellationToken);
        var native=Models.Native(messageId);
        if(!messages.TryGetValue(native,out var message)) { await FetchMessagePageAsync(chatId,null,StatusPageSize,cancellationToken); messages.TryGetValue(native,out message); }
        if(message is null || message.ChatId!=Models.Native(chatId)) throw new BackendException(BackendFailure.Unavailable,"That message isn't available right now. Refresh and try again.");
        await GetChatAsync(chatId,cancellationToken);
        return Models.Map(message,chats[message.ChatId]);
    }
    // The memory half of ResolveMessageAsync and nothing else: no page read, no conversation read, no pending lookup.
    // A display-only caller (the activity stream) uses it, so a miss is an answer rather than a reason to ask the phone.
    internal ChatMessage? PeekMessage(string chatId,string messageId)
    {
        if (messageId.StartsWith(PendingPrefix, StringComparison.Ordinal) ||
            !chatId.StartsWith("google-direct:", StringComparison.Ordinal) || !messageId.StartsWith("google-direct:", StringComparison.Ordinal)) return null;
        if (!messages.TryGetValue(Models.Native(messageId),out var message) || message.ChatId!=Models.Native(chatId)) return null;
        return chats.TryGetValue(message.ChatId,out var chat) ? Models.Map(message,chat) : null;
    }
    private void RememberMessage(GoogleMessage message)
    {
        var kept = messages.AddOrUpdate(message.Id, message, (_, previous) => GoogleModels.MergeUpdate(previous, message));
        // A copy older than the status already held (GoogleModels.Regresses), most often a page read that crossed an update.
        if (kept.Status != message.Status)
            GoogleMessagesLog.Write($"message status kept chat={GoogleMessagesLog.Tag(message.ChatId)} message={GoogleMessagesLog.Tag(message.Id)} kept={GoogleMessagesLog.Status(kept.Status)} older={GoogleMessagesLog.Status(message.Status)}");
        ArchiveMessages([kept]);
    }
    // How many group readers Google named and how many of them matched exactly one participant. Counts only: the names
    // themselves never reach a log. Nothing is added to a line with no names on it.
    private static string ReaderCounts(IReadOnlyCollection<GoogleMessage> list, GoogleChat? chat)
    {
        var named = list.Sum(m => m.ReadBy?.Length ?? 0);
        return named == 0 ? "" : $" readersNamed={named} readersMatched={(chat is null ? 0 : list.Sum(m => GoogleModels.Readers(m, chat).Count))}";
    }
    // Selection cancellation stops this read independently; replies remain matched by the transport.
    private async Task<JsonElement> ReadAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        NoteForeground();
        var owner=helper;
        var operation = GoogleMessagesLog.Operation(method);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await DrainReadAsync(owner!, method, parameters, cancellationToken);
            GoogleMessagesLog.Write($"read={operation} outcome=ok ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
            return result;
        }
        catch (InvalidOperationException ex)
        {
            var failure = GoogleReadFailure.Map(ex.Message);
            GoogleMessagesLog.Write($"read={operation} outcome=failed reason={GoogleMessagesLog.Reason(failure)} ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
            // The helper ended this read because the phone removed the pairing; unlike a lone authentication failure,
            // that is proof, and reconnecting cannot help.
            if (ex.Message == "pairing_lost") { MarkPairingLost("read"); throw failure; }
            if (failure.Failure == BackendFailure.AuthenticationRequired)
            {
                if (!ReferenceEquals(owner,helper)) throw GoogleReadFailure.Map("connection_closed");
                // A single failed operation is not proof that saved credentials are dead.
                // Reconnect validates them with config + an authenticated chat read.
                connected = false;
                // Once Google refused the sign-in, that refusal is what the notice says, or holds back while the window
                // renews it; "disconnected" on top would replace the one or show under the other.
                if (!signIn.SignInExpired) Publish(new(RealtimeKind.Disconnected));
                // The helper says signed_out before it answers (connection_state.go), so a renewal is usually under way.
                throw signIn.Held(DateTimeOffset.UtcNow) ? GoogleReadFailure.Renewing() : GoogleReadFailure.Map("connection_closed");
            }
            throw failure;
        }
        catch (Exception failure)
        {
            GoogleMessagesLog.Write($"read={operation} outcome=failed reason={GoogleMessagesLog.Reason(failure)} ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
            throw;
        }
    }
    private static async Task<JsonElement> DrainReadAsync(HelperConnection connection, string method, object? parameters, CancellationToken token)
    {
        // libgm allows up to 60s for the phone and the helper bounds requests at
        // 120s. A shorter host timer killed healthy sessions during slow reads.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(135));
        return await connection.CallAsync(method, parameters, timeout.Token);
    }
    // Google Messages carries no preview of a link, so one is made by asking the linked page for the picture it
    // publishes. Only the conversation on screen asks, and only for messages already drawn: the helper does the
    // fetching anonymously and reports back as a change to those messages.
    // A request that was not sent fails, so the conversation offers it again on its next render, which is how a reconnect
    // reaches it (ConversationViewModel.LinkPreviews). Returning quietly before Google had connected read as sent, and a
    // conversation opened at launch never asked for its link cards again until it was reopened.
    public async Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (!connected || helper is not { } connection) { LinkPreviewsDeferred("not_connected"); throw NotConnectedYet(); }
        var ids = messageIds.Where(Models.Owns).Distinct(StringComparer.Ordinal).TakeLast(200).Select(Models.Native).ToArray();
        if (ids.Length == 0) return;
        // Best effort, like typing: a website is never a reason to queue behind history or hold the pipe open. A pipe busy
        // for the moment is asked again a moment later, a few times, and then left to the conversation's next render.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var native = Models.Native(chatId);
        if (await GoogleSkippedCalls.TryAsync(token => connection.CallAsync("ui.linkPreviews", new { chatId = native, messageIds = ids }, token, skipIfBusy: true),
            LinkPreviewBusyRetries, timeout.Token)) return;
        LinkPreviewsDeferred("busy");
        throw new BackendException(BackendFailure.Unavailable, "Google Messages is busy.");
    }
    private const int LinkPreviewBusyRetries = 3;
    private int linkPreviewsDeferred;
    // Counted, and written now and then rather than once per render: fixed words and a running total only.
    private void LinkPreviewsDeferred(string reason)
    {
        var count = Interlocked.Increment(ref linkPreviewsDeferred);
        if (count is 1 or 10 or 100 || count % 1000 == 0) GoogleMessagesLog.Write($"linkpreviews deferred reason={reason} count={count}");
    }
    // Asked before Google Messages connected, or between connections, by a call that never connects by itself (avatars,
    // link cards): nothing was sent, and its readiness (Connected) is what to wait for. Reads that connect wait instead.
    private static BackendException NotConnectedYet() => new(BackendFailure.NotReady, "Google Messages is not connected yet.");
    public async Task SetTypingAsync(string chatId,bool isTyping,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isTyping || !connected || helper is not { } connection) return;
        // Best effort: never queue behind a send/history call or reconnect just to send typing.
        // UI cancellation must not kill the shared helper while its response is being drained.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.CallAsync("ui.typing",new {chatId=Models.Native(chatId),expiresAt=DateTimeOffset.UtcNow.AddSeconds(3)},timeout.Token,skipIfBusy:true);
    }
    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken=default)
    {
        // One per open conversation window, all over the one shared set: the watch tells this one of a conversation
        // another watcher already saw end and dropped.
        var watch=new TypingWatch();
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        while(await timer.WaitForNextTickAsync(cancellationToken))
        {
            List<TypingUpdate> updates;
            lock(typingLock)
            {
                List<TypingUpdate> current=[];
                foreach(var chat in typingChats.ToArray())
                {
                    var users=connected ? typing.Active(chat,DateTimeOffset.UtcNow) : [];
                    chats.TryGetValue(Models.Native(chat),out var detail);
                    var persons=users.Where(id=>detail?.Participants.Any(p=>p.Id==id && p.Self)!=true).Select(id=>new TypingParticipant(id,detail?.Participants.FirstOrDefault(p=>p.Id==id)?.Name ?? "Someone",typing.ExpiresAt(chat,id))).ToArray();
                    current.Add(new(chat,persons));
                    if(users.Count==0) typingChats.Remove(chat);
                }
                updates=watch.Next(current);
            }
            foreach(var update in updates) yield return update;
        }
    }
    public async Task RunAsync(Func<RealtimeEvent,CancellationToken,Task> receive,CancellationToken cancellationToken)
    {
        // Each run says a refusal afresh: a restarted synchronization forgets what the last one was told, and a sign-in
        // still refused after a dialog or a refresh that restarted it otherwise went unsaid.
        signIn.ResetAnnouncement();
        while(!cancellationToken.IsCancellationRequested)
        {
            // Not set up (ThrowIfNotSetUp): nothing to connect, retry or say. Synchronization leaves this loop out until a
            // pairing exists and restarts after one; should it run anyway, it only looks quietly for the pairing.
            if (NeverPaired)
            {
                if (Interlocked.Exchange(ref notPairedRealtimeLogged, 1) == 0) GoogleMessagesLog.Write("realtime skipped reason=not_paired");
                await Task.Delay(TimeSpan.FromSeconds(3),cancellationToken);
                continue;
            }
            try
            {
                await ConnectAsync(cancellationToken);
                await receive(new(RealtimeKind.Connected),cancellationToken);
                GoogleMessagesLog.Write("realtime connected");
                StartHistoryIndexing();
                while(connected && await realtime.Reader.WaitToReadAsync(cancellationToken))
                    while(realtime.Reader.TryRead(out var item)) await receive(item,cancellationToken);
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { break; }
            // Said once, not every three seconds for as long as nobody pairs, and not while the window's invisible refresh
            // may still renew the sign-in. The pass that finds it held only asks this backend's own state, never Google.
            catch(BackendException ex) when(ex.Failure == BackendFailure.AuthenticationRequired)
            {
                connected=false; lock(typingLock) typing.Clear();
                if (signIn.Announce(DateTimeOffset.UtcNow)) { GoogleMessagesLog.Write("realtime authentication_required"); await receive(new(RealtimeKind.AuthenticationRequired, SignInOnly: signIn.SignInExpired),cancellationToken); }
            }
            catch(Exception failure) { GoogleMessagesLog.Write($"realtime disconnected reason={GoogleMessagesLog.Reason(failure)}"); connected=false; lock(typingLock) typing.Clear(); await receive(new(RealtimeKind.Disconnected),cancellationToken); }
            // Not before the pause after a failed attempt, which a call in the meantime would only be refused.
            await Task.Delay(NextAttemptDelay(),cancellationToken);
        }
    }
    public async Task StopAsync()
    {
        stopping=true;
        // A connect holds the lock for its whole attempt, up to three helper calls of minutes each; waited out, it made
        // every quit during a reconnect run past the shutdown bound and have its helper killed without a last snapshot.
        var ending=attempt;
        try { ending?.Cancel(); } catch (ObjectDisposedException) { }
        GoogleMessagesLog.Write($"stopping connect={(ending is null ? "none" : "cancelled")}");
        await StopHistoryIndexingAsync();
        await gate.WaitAsync();
        try
        {
            if(helper is null) return;
            if (latestSession is { } refreshed) await PersistSessionAsync(helper,refreshed,"stop");
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var session=await helper.CallAsync("snapshot",null,timeout.Token);
                var snapshot=new SessionSnapshot(session);
                latestSession=snapshot;
                await PersistSessionAsync(helper,snapshot,"snapshot");
            }
            catch(Exception) { }
            await helper.DisposeAsync(); helper=null; connected=false;
            GoogleMessagesLog.Write("stopped");
        }
        finally { gate.Release(); }
    }
    public void Dispose() { connected=false; }
}



