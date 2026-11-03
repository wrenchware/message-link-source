using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// What a push says once opened (the phone's push/Push.kt reads the same): a new message to show, or a conversation read elsewhere whose notification goes.
/// Written in the protocol's JSON. Short enough, cut where it must be, to fit the one padded size every push has. <paramref name="Whole"/>: the text is the
/// message's own words, uncut, and it carries nothing else, so a phone may put it in its kept copy of the conversation before it reads the message itself
/// (a photo's "Photo", or words cut short, would otherwise be drawn as the message until then).
/// </summary>
/// <para><c>Quiet</c>: someone is at a PC where Messages shows them the message itself, so the phone shows it without a sound (left out otherwise).</para>
/// <para><c>CallKind</c>: "missed" or "voicemail" for a call record, so the phone files it under its own notification category; left out for a message.</para>
public sealed record PushMessage(int V, string Kind, string ChatId, string? MessageId = null, string? Title = null, bool Group = false, string? Sender = null,
    string? Text = null, DateTimeOffset? At = null, bool Whole = false, bool? Quiet = null, string? CallKind = null);

/// <summary>
/// Which of a device's connections said it is in front (client.state's presence): a phone in the background keeps its connection for a minute, and a
/// message in that minute must still reach it as a push. A connection that has said nothing yet counts as in front, so a phone opening is not sent what it shows.
/// </summary>
internal sealed class DevicePresence
{
    private readonly ConcurrentDictionary<object, (string Device, bool Present, bool Active)> connections = new();

    public IClientStateSink For(string deviceId, IClientStateSink? inner) => new Sink(this, deviceId, inner);

    public void Opened(object connection, string deviceId) => connections.TryAdd(connection, (deviceId, true, false));

    public void Closed(object connection) => connections.TryRemove(connection, out _);

    /// <summary>Whether any connection of <paramref name="deviceId"/> is open and in front.</summary>
    public bool InFront(string deviceId) => connections.Values.Any(c => c.Device == deviceId && c.Present);

    /// <summary>
    /// Whether someone is at another device than <paramref name="deviceId"/> where Messages shows them a new message itself (client.state's active, which only a
    /// PC states): a phone's notification then arrives without a sound, as decided 2026-10-16.
    /// </summary>
    public bool AtAnotherDevice(string deviceId) => connections.Values.Any(c => c.Device != deviceId && c.Active);

    private sealed class Sink(DevicePresence presence, string deviceId, IClientStateSink? inner) : IClientStateSink
    {
        public void Apply(object connection, ClientStateParams state)
        {
            presence.connections[connection] = (deviceId, state.Presence, state.Active == true);
            inner?.Apply(connection, state);
        }

        public void Disconnected(object connection)
        {
            presence.Closed(connection);
            inner?.Disconnected(connection);
        }
    }
}

/// <summary>
/// A connection's push service: what the server offers, and the registration of the device that connection belongs to. The setup is read at each call, so pushes
/// turned on from the server's page are offered to a connection opened before.
/// </summary>
/// <param name="health">How pushes are doing, which the offer, a test's answer and the status read and feed (10.9); none on a host without the page's card.</param>
/// <param name="devices">The paired devices by name, for the status.</param>
internal sealed class DevicePush(Func<PushSetup?> setupNow, PushRegistry registry, string deviceId, PushHealth? health = null, Func<IReadOnlyList<PairedDevice>>? devices = null,
    TimeProvider? time = null) : IPushService
{
    public DevicePush(PushSetup? setup, PushRegistry registry, string deviceId) : this(() => setup, registry, deviceId) { }

    public Task<PushOffer> OfferAsync(CancellationToken cancellationToken = default)
    {
        var setup = setupNow();
        // Only a phone asks: one that then never signs up is named on the page.
        if (setup is not null) health?.Asked(deviceId);
        return Task.FromResult(setup?.Offer ?? new PushOffer(false));
    }

    public async Task RegisterAsync(PushRegisterParams registration, CancellationToken cancellationToken = default)
    {
        if (setupNow() is not { } setup) throw new NotSupportedException();
        // A phone saying the project has no app for its package (10.9): a report kept beside the device for the page, never an address to send to, and never in
        // place of a registration it has. Only for one of Messages' own packages, and one the project really lacks.
        if (string.IsNullOrEmpty(registration.Token))
        {
            if (!FirebaseTransport.Packages.Contains(registration.Package) || setup.Offer.Apps?.Any(app => app.Package == registration.Package) == true) throw Invalid();
            if (!await registry.SetMissingAsync(deviceId, registration.Package, (time ?? TimeProvider.System).GetUtcNow()).ConfigureAwait(false))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.Forbidden, "This device is no longer paired."));
            return;
        }
        byte[] key, auth;
        try { key = Convert.FromBase64String(registration.PublicKey); auth = Convert.FromBase64String(registration.Auth); }
        catch (FormatException) { throw Invalid(); }
        // A key of the right length that is not on the curve would fail every push sealed to it: refused here, where the phone hears of it.
        if (!WebPushEncryption.Accepts(key) || auth is not { Length: 16 } || string.IsNullOrWhiteSpace(registration.Token) || registration.Token.Length > 4096 ||
            setup.Offer.Apps?.Any(app => app.Package == registration.Package) != true) throw Invalid();
        // Refused for a device removed while it was registering: its connection is being closed.
        if (!await registry.SetAsync(new PushRegistry.Registration(deviceId, registration.Token, registration.Package, key, auth, DateTimeOffset.UtcNow, registration.BackgroundSync)).ConfigureAwait(false))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.Forbidden, "This device is no longer paired."));
    }

    public Task TestShownAsync(PushTestShownParams shown, CancellationToken cancellationToken = default)
    {
        if (health is null) throw new NotSupportedException();
        health.Shown(deviceId, shown.Id);
        return Task.CompletedTask;
    }

    public Task<PushStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (health is null) throw new NotSupportedException();
        return Task.FromResult(health.Status(setupNow() is not null, registry, devices?.Invoke() ?? []));
    }

    private static ProtocolException Invalid() => new(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The push registration is not valid."));
}

/// <summary>
/// The server's notifications to phones (SWITCHBOARD.md 7.5): each new incoming message, judged as the PC's NotificationObserver judges one, goes to every
/// registered device paired now and not in front as a push sealed to that device's key, padded to one size; and a conversation read elsewhere that a phone
/// was told of is withdrawn there by a quiet push. Logged by counts and outcomes only.
/// </summary>
/// <remarks>
/// <para><b>Judged as the PC judges.</b> Through the PC's own readers over the rows its clients share: LocalPreferenceChatReader (a mute set in Messages, or
/// at the service as Messenger reports one unless it was set aside in Messages; a snooze; an Instagram archive kept in Messages, which an arrival lifts) and
/// MergedConversations (a merge's mute, archive and snooze over its members'). A conversation archived at its service, or a merge, is judged as the inbox
/// will show it once the arrival returns it there (the host's incoming unarchive, or the client that holds the merge), as the PC judges one its host is
/// returning. Not for an outgoing, hidden, deleted, edited or system message, spam, a merged row, or one older than a few minutes. A row that cannot be read
/// leaves the push to go.</para>
/// <para><b>Off the log's reading.</b> Pushes wait in one bounded queue for Firebase, each send up to its timeout, so the log is read at its own pace;
/// one that does not fit is dropped and counted. A resync after the feed opened (this reader fell behind) is logged.</para>
/// </remarks>
internal sealed class PushNotifier : IAsyncDisposable
{
    /// <summary>Every push opens to this many bytes before it is sealed: one size whatever a message says.</summary>
    public const int PaddedSize = 1536;
    private const int TextLimit = 600, NameLimit = 100;
    // Pushes waiting for Firebase. Past this a new one is dropped rather than the reading of the log held up.
    private const int QueueLimit = 256;
    // Conversations remembered as told, per device; past it the memory starts again, which costs at most a withdrawal not sent.
    private const int ToldLimit = 1000;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    private sealed record Outgoing(string DeviceId, PushMessage Push, bool Urgent);

    private readonly EventLog events;
    private readonly IChatReader chats;
    private readonly IChatReader judged;
    private readonly MergedConversations merges;
    private readonly IMessageSender messages;
    private readonly PushRegistry registry;
    private readonly IPushTransport transport;
    private readonly Func<string, bool> inFront;
    private readonly Func<string, bool>? quietFor;
    private readonly PushHealth? health;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stopping = new();
    // Two writers: the log's reader, and a burst's wake once the owner has stopped sending (BurstAsync).
    private readonly Channel<Outgoing> outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(QueueLimit) { SingleReader = true });
    // A burst of the owner's own sends, per device: when the last came, in which conversation, when the last urgent wake went, and whether one is waiting.
    private sealed class Burst { public DateTimeOffset LastSend, LastWake = DateTimeOffset.MinValue; public string Chat = ""; public bool Waiting; }
    private readonly Dictionary<string, Burst> bursts = new(StringComparer.Ordinal);
    private readonly Task reading, sending;
    // Conversations a phone was told of and not yet told were read, by device. Only the log's reader touches it.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> told = new(StringComparer.Ordinal);
    // Outgoing echoes can be announced again as their send settles. A receipt must not become a new phone wake for every status transition.
    private readonly HashSet<(string Chat, string Message)> synced = [];
    private int sent, failed, dropped, resyncs;
    private long through, queued, finished;

    /// <param name="localKey">The key the clients file their shared rows under (the host's state's identity), read when a conversation is judged.</param>
    /// <param name="quietFor">Whether a device's message pushes go quiet now (<see cref="DevicePresence.AtAnotherDevice"/>); none are without it.</param>
    /// <param name="health">Where each push's outcome is told, for the page's card (10.9).</param>
    public PushNotifier(EventLog events, IChatReader chats, IMessageSender messages, PushRegistry registry, IPushTransport transport, Func<string, bool> inFront, Action<string> log,
        ISharedState? shared = null, Func<string?>? localKey = null, Func<string, bool>? quietFor = null, PushHealth? health = null)
    {
        this.events = events; this.chats = chats; this.messages = messages; this.registry = registry; this.transport = transport; this.inFront = inFront; this.log = log;
        this.quietFor = quietFor; this.health = health;
        var key = new LocalKey(localKey ?? (() => events.LatestState?.Identity?.LocalKey));
        judged = new LocalPreferenceChatReader(new Arriving(chats), SharedRowReads.Preferences(shared), key, snoozes: SharedRowReads.Snoozes(shared));
        merges = new MergedConversations(new PrivateState(SharedRowReads.Rows(shared), key), GoogleVoiceIds.TextRouteForCall);
        reading = Task.Run(() => ReadAsync(stopping.Token));
        sending = Task.Run(() => SendAsync(stopping.Token));
    }

    /// <summary>
    /// How long the owner must have stopped sending before a burst's one urgent wake (2026-10-19): normal-priority syncs are held by Android on the
    /// owner's Galaxy, so a run of sends from the PC otherwise reaches the phone only when it is opened. The harness shortens it.
    /// </summary>
    internal TimeSpan BurstQuiet { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The least time between two urgent wakes for a device's own sends: Firebase demotes an app whose urgent pushes show nothing.</summary>
    internal TimeSpan BurstSpacing { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The sequence number of the last item of the log this has finished with (judged, and its pushes queued); the harness waits on it.</summary>
    internal long Through => Volatile.Read(ref through);

    /// <summary>Whether every push queued so far has been sent, or has failed; the harness waits on it.</summary>
    internal bool Settled => Volatile.Read(ref finished) == Volatile.Read(ref queued);

    /// <summary>The reading of the log, which ends with it; the harness waits on it.</summary>
    internal Task Reading => reading;

    private async Task ReadAsync(CancellationToken token)
    {
        using var feed = events.Subscribe();
        var opened = false;
        while (!token.IsCancellationRequested)
        {
            EventItem? item;
            try { item = await feed.ReadAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ChannelClosedException) { return; }
            // The log has ended: the host is stopping.
            if (item is null) return;
            if (item.IsResync)
            {
                // Every feed opens with one. A later one is events this fell behind on and never read: a push, or a withdrawal, may be missing.
                if (opened) log($"push events resync count={Interlocked.Increment(ref resyncs)}");
                opened = true;
            }
            else if (item.Event is { ChatId: { } chatId } change && !change.IsHistory)
            {
                try
                {
                    if (change.Kind == RealtimeKind.MessageChanged && !change.ReceiptOnly &&
                        (change.IsNewMessage == true || registry.All().Any(r => r.BackgroundSync)))
                    {
                        await NewMessagesAsync(chatId, change.Ids ?? [], change.IsNewMessage == true, token).ConfigureAwait(false);
                        if (change.IsNewMessage != true) await MaybeReadAsync(chatId, token).ConfigureAwait(false);
                    }
                    else if (change.Kind is RealtimeKind.ChatChanged or RealtimeKind.MessageChanged) await MaybeReadAsync(chatId, token).ConfigureAwait(false);
                    else if (change.Kind == RealtimeKind.ChatDeleted) Withdraw(chatId);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception failure)
                {
                    log($"push notify failed type={failure.GetType().Name}");
                }
            }
            Volatile.Write(ref through, item.Seq);
        }
    }

    private async Task NewMessagesAsync(string chatId, IReadOnlyList<string> ids, bool notifyIncoming, CancellationToken token)
    {
        var registered = registry.All();
        // A device removed since it was told of a conversation is not remembered for it.
        foreach (var device in told.Keys)
            if (!registered.Any(r => r.DeviceId == device)) told.TryRemove(device, out _);
        var devices = registered.Where(r => !inFront(r.DeviceId)).ToArray();
        if (devices.Length == 0) return;
        ChatSummary? chat = null;
        foreach (var id in ids)
        {
            var message = await messages.ResolveMessageAsync(chatId, id, token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (message.ChatId != chatId || message.IsHidden || message.IsDeleted || message.Type == "system" || message.EditedAt is not null ||
                message.Timestamp < now - Fresh || message.Timestamp > now.AddMinutes(1)) continue;
            if (message.IsOutgoing)
            {
                // The PC's own sends are not incoming alerts. Still sync muted or snoozed chats: those choices govern interruptions, not history.
                var targets = devices.Where(d => d.BackgroundSync).ToArray();
                if (targets.Length == 0 || synced.Contains((chatId, message.Id))) continue;
                var queuedSync = false;
                foreach (var device in targets) queuedSync |= Enqueue(new(device.DeviceId, new PushMessage(1, "sync", chatId), Urgent: false));
                if (queuedSync)
                {
                    if (synced.Count >= ToldLimit) synced.Clear();
                    synced.Add((chatId, message.Id));
                    foreach (var device in targets) NoteBurst(device.DeviceId, chatId);
                }
                continue;
            }
            if (!notifyIncoming) continue;
            chat ??= await JudgedAsync(chatId, token).ConfigureAwait(false);
            if (chat is null) continue;
            var push = new PushMessage(1, "message", chatId, message.Id, Cut(chat.Title, NameLimit), chat.Type == "group", Cut(message.SenderName, NameLimit),
                Cut(Words(message), TextLimit), message.Timestamp,
                Whole: message.Attachments.Count == 0 && !string.IsNullOrWhiteSpace(message.Text) && message.Text.Length <= TextLimit,
                CallKind: message.CallKind is "missed" or "voicemail" ? message.CallKind : null);
            foreach (var device in devices)
            {
                // Judged as it is queued: the owner at the PC now is what decides, not when Firebase gets to it.
                if (!Enqueue(new(device.DeviceId, quietFor?.Invoke(device.DeviceId) == true ? push with { Quiet = true } : push, Urgent: true))) continue;
                // Told as it is queued, so a read that comes before the push has gone is withdrawn after it, in the same queue.
                var waiting = told.GetOrAdd(device.DeviceId, _ => new(StringComparer.Ordinal));
                if (waiting.Count >= ToldLimit) waiting.Clear();
                waiting[chatId] = 0;
            }
        }
    }

    // The conversation as the PC's NotificationObserver judges an arrival in it (see the remarks above), or null when it stays quiet.
    private async Task<ChatSummary?> JudgedAsync(string chatId, CancellationToken token)
    {
        var chat = await judged.GetChatAsync(chatId, token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (await MergeOfAsync(chat).ConfigureAwait(false) is { } group)
            // A timed mute that has run out notifies again; the merge's archive goes for this arrival as a member's does (the client holding it returns it).
            chat = chat with { IsMuted = merges.IsMuted(group.Id, now), IsArchived = false, Title = group.Title, SnoozedUntil = group.SnoozedUntil };
        return chat.IsMuted || chat.IsArchived || chat.IsSpam || chat.MemberChatIds.Count != 0 || chat.SnoozedUntil > now ? null : chat;
    }

    // The merge that shows this conversation, read from the merges row as it is now; none when the row cannot be read.
    private async Task<MergedConversation?> MergeOfAsync(ChatSummary chat)
    {
        try
        {
            await merges.ReloadAsync().ConfigureAwait(false);
            merges.Observe([chat]);
            var group = merges.Find(merges.DisplayId(chat.Id));
            // Kept only for the asking: the merges are read again for each arrival, and every conversation seen would otherwise stay.
            merges.Forget(chat.Id);
            return group;
        }
        catch (Exception failure) when (failure is not OperationCanceledException) { return null; }
    }

    // A conversation a phone was told of, read elsewhere since (the service says nothing is unread): its notification goes, quietly.
    private async Task MaybeReadAsync(string chatId, CancellationToken token)
    {
        var waiting = told.Where(pair => pair.Value.ContainsKey(chatId)).Select(pair => pair.Key).ToArray();
        if (waiting.Length == 0) return;
        var chat = await chats.GetChatAsync(chatId, token).ConfigureAwait(false);
        // Filed as spam after its arrival was told (Google's filter takes seconds): nothing about it notifies, so that one goes too.
        if (chat.IsSpam) { Withdraw(chatId); return; }
        if (chat.UnreadCount > 0) return;
        foreach (var device in registry.All().Where(r => waiting.Contains(r.DeviceId)))
        {
            if (told.TryGetValue(device.DeviceId, out var chatsTold)) chatsTold.TryRemove(chatId, out _);
            // A phone in front reads the change itself.
            if (!inFront(device.DeviceId)) Enqueue(new(device.DeviceId, new PushMessage(1, "read", chatId), Urgent: false));
        }
    }

    // A conversation a phone was told of, gone from the list: a notification still offering Reply goes too (audit, 2026-11-01). Sent to a
    // phone in front as well, whose list takes the change but leaves the notification.
    private void Withdraw(string chatId)
    {
        foreach (var device in registry.All())
            if (told.TryGetValue(device.DeviceId, out var chatsTold) && chatsTold.TryRemove(chatId, out _))
                Enqueue(new(device.DeviceId, new PushMessage(1, "read", chatId), Urgent: false));
    }

    // A send in a burst: the device's wake waits until the sends stop for BurstQuiet.
    private void NoteBurst(string deviceId, string chatId)
    {
        lock (bursts)
        {
            if (!bursts.TryGetValue(deviceId, out var burst)) bursts[deviceId] = burst = new Burst();
            burst.LastSend = DateTimeOffset.UtcNow;
            burst.Chat = chatId;
            if (burst.Waiting) return;
            burst.Waiting = true;
        }
        _ = Task.Run(() => BurstAsync(deviceId, stopping.Token));
    }

    // One urgent sync once the owner has stopped sending, so the phone catches the whole burst up while Android would hold the normal ones; none if this
    // device had one less than BurstSpacing ago (its normal syncs still went), or is in front now and reads the change live.
    private async Task BurstAsync(string deviceId, CancellationToken token)
    {
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (bursts) wait = bursts[deviceId].LastSend + BurstQuiet - DateTimeOffset.UtcNow;
                if (wait <= TimeSpan.Zero) break;
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            string chatId;
            lock (bursts)
            {
                var burst = bursts[deviceId];
                burst.Waiting = false;
                var now = DateTimeOffset.UtcNow;
                if (now - burst.LastWake < BurstSpacing || inFront(deviceId) || registry.Get(deviceId) is not { BackgroundSync: true }) return;
                burst.LastWake = now;
                chatId = burst.Chat;
            }
            Enqueue(new(deviceId, new PushMessage(1, "sync", chatId), Urgent: true));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Tells every phone signed up for notifications that the server has made no good backup for two days (stage 10): kind <c>backup</c>, with no conversation, at normal
    /// priority. A phone built before it opens the push and shows nothing (it acts only on the kinds it knows), so it is sent as a quiet wake would be, never urgent.
    /// Returns how many were queued.
    /// </summary>
    public int BackupStale()
    {
        var queued = registry.All().Where(device => registry.Paired(device.DeviceId)).Count(device => Enqueue(new(device.DeviceId, new PushMessage(1, "backup", ""), Urgent: false)));
        log($"push backup queued devices={queued}");
        return queued;
    }

    /// <summary>
    /// A scheduled message held as not sent (audit, 2026-11-01: it waited in its conversation, and nobody found out until the moment had gone): kind <c>scheduled</c>, naming only the
    /// conversation, so the phone words it and finds the name it keeps. A phone built before it shows nothing, as for <see cref="BackupStale"/>. Quiet for one whose owner is at a PC.
    /// </summary>
    public int ScheduledHeld(string chatId)
    {
        var push = new PushMessage(1, "scheduled", chatId);
        var queued = registry.All().Where(device => registry.Paired(device.DeviceId))
            .Count(device => Enqueue(new(device.DeviceId, quietFor?.Invoke(device.DeviceId) == true ? push with { Quiet = true } : push, Urgent: true)));
        log($"push scheduled held queued devices={queued}");
        return queued;
    }

    private bool Enqueue(Outgoing next)
    {
        Interlocked.Increment(ref queued);
        if (!outgoing.Writer.TryWrite(next))
        {
            Interlocked.Decrement(ref queued);
            // Firebase slower than the messages (each send can take up to its timeout): this push goes rather than the log falling behind.
            if (Interlocked.Increment(ref dropped) == 1) log("push queue full");
            return false;
        }
        if (Interlocked.Exchange(ref dropped, 0) is > 0 and var count) log($"push queue dropped count={count}");
        return true;
    }

    private async Task SendAsync(CancellationToken token)
    {
        try
        {
            await foreach (var next in outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try { await SendOneAsync(next, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception failure)
                {
                    // One device's key that cannot be sealed to, or a transport or store that throws, costs that device this push and no other device theirs.
                    log($"push send failed kind={next.Push.Kind} type={failure.GetType().Name} failed={Interlocked.Increment(ref failed)}");
                }
                finally { Interlocked.Increment(ref finished); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendOneAsync(Outgoing next, CancellationToken token)
    {
        // As it is registered now: one removed since this push was queued gets nothing, and one registered again gets it at its new address.
        if (registry.Get(next.DeviceId) is not { } device) return;
        // Paired now, not only when it registered: a row can outlive its device (PushRegistry).
        if (!registry.Paired(device.DeviceId))
        {
            await registry.RemoveAsync(device.DeviceId, "device_unpaired", device.Token).ConfigureAwait(false);
            return;
        }
        if (Seal(next.Push, device.PublicKey, device.Auth) is not { } sealedPush)
        {
            log($"push skipped kind={next.Push.Kind} reason=too_long");
            return;
        }
        var payload = Convert.ToBase64String(sealedPush);
        var result = await transport.SendWithStatusAsync(device.Token, payload, next.Urgent, sync: next.Push.Kind == "sync", token).ConfigureAwait(false);
        health?.Report(device.DeviceId, result);
        var outcome = result.Outcome;
        if (outcome == PushOutcome.Gone) await registry.RemoveAsync(device.DeviceId, "token_gone", device.Token).ConfigureAwait(false);
        if (outcome == PushOutcome.Sent) Interlocked.Increment(ref sent); else Interlocked.Increment(ref failed);
        log($"push sent kind={next.Push.Kind} outcome={outcome.ToString().ToLowerInvariant()} total={Volatile.Read(ref sent)} failed={Volatile.Read(ref failed)}{(next.Push.Quiet == true ? " quiet=yes" : "")}{(next.Push.Kind == "sync" && next.Urgent ? " urgent=yes" : "")}");
    }

    /// <summary>
    /// A push as sealed for one phone: its JSON, cut further if it must be, padded to <see cref="PaddedSize"/>. Null when even its ids do not fit, never an
    /// exception for its length.
    /// </summary>
    internal static byte[]? Seal(PushMessage push, byte[] publicKey, byte[] auth)
    {
        var bytes = Json(push);
        // Words that fit by count can still run long: an emoji is written as two escaped halves, twelve bytes. The text is shortened first, a hundred
        // characters at a time down to a hundred, then the names, then the text goes, then the names.
        if (push.Text is { } text)
            for (var limit = Math.Min(text.Length, TextLimit); bytes.Length >= PaddedSize && limit > 100;)
            {
                limit = Math.Max(100, limit - 100);
                bytes = Json(push = push with { Text = Cut(text, limit), Whole = false });
            }
        var (title, sender) = (push.Title, push.Sender);
        foreach (var limit in (int[])[50, 20])
            if (bytes.Length >= PaddedSize) bytes = Json(push = push with { Title = Cut(title, limit), Sender = Cut(sender, limit) });
        if (bytes.Length >= PaddedSize) bytes = Json(push = push with { Text = null, Whole = false });
        if (bytes.Length >= PaddedSize) bytes = Json(push = push with { Title = null, Sender = null });
        return bytes.Length < PaddedSize ? WebPushEncryption.Encrypt(bytes, publicKey, auth, PaddedSize) : null;
    }

    private static byte[] Json(PushMessage push) => JsonSerializer.SerializeToUtf8Bytes(push, ProtocolJson.Options);

    // What the message says, as a notification would: its words, or what it carries.
    private static string Words(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text)) return message.Text;
        var attachment = message.Attachments.FirstOrDefault();
        return attachment switch
        {
            null => "",
            { IsVoiceNote: true } => "Voice message",
            { Type: "img" } => "Photo",
            { Type: "video" } => "Video",
            { Type: "audio" } => "Audio",
            _ => "File: " + (attachment.Name ?? "file"),
        };
    }

    private static string? Cut(string? text, int limit)
    {
        if (text is null || text.Length <= limit) return text;
        var cut = text[..Math.Max(0, limit)];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut + "…";
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        outgoing.Writer.TryComplete();
        try { await reading.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await sending.ConfigureAwait(false); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }

    // An arrival returns a conversation archived at its service to the inbox, a moment after this reads it: the host's incoming unarchive
    // (HostIncomingUnarchive), or a client's. Judged as the inbox will show it, as the PC's NotificationObserver judges an arrival its host is returning
    // (UnarchiveOutcome.LeftToHost): should that fail, it was still shown, the safer mistake.
    private sealed class Arriving(IChatReader inner) : IChatReader
    {
        public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default) =>
            (await inner.GetChatAsync(chatId, cancellationToken).ConfigureAwait(false)) with { IsArchived = false };

        public Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default) => inner.GetChatsAsync(olderThan, cancellationToken);

        public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default) =>
            inner.GetMessagesAsync(chatId, olderThan, cancellationToken);
    }

    private sealed class LocalKey(Func<string?> key) : IAccessTokenStore
    {
        public ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(key());
    }
}
