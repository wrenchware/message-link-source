using System.Threading.Channels;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

public enum ActivityKind { Message, Edited, Unsent, Reaction, Read, Call, Voicemail, Typing, Connected, Disconnected, SignInNeeded }

// Where an entry's message came from: memory a backend already held, the list's newest-message copy, nowhere (a fixed
// "new message" line), the outbox's own confirmation, or a connection or typing state with no message at all.
public enum ActivitySource { Peek, ListPreview, Generic, Outbox, State,
    // A message from before this session, read from what this PC keeps for the console's history; never appended. Appended.
    History }

/// <summary>
/// One thing that happened, across every service. SenderName is whoever did it: the sender of a message, the reactor,
/// the reader, the typist. Detail is kind-specific: "+❤" or "-❤" for a reaction, the call kind for a call, a voicemail's
/// transcript, how many people read when more than one did, <see cref="SignInOnly"/> for a sign-in that only needs
/// renewing. Nothing here is ever persisted or logged.
/// </summary>
public sealed record ActivityEntry(long Sequence, ActivityKind Kind, DateTimeOffset At, string? ChatId, string? DisplayChatId,
    string Network, string? AccountId, string? ConversationTitle, bool IsGroup, string? SenderName, bool IsOutgoing,
    ChatMessage? Message, string? Detail, bool IsBackfill, bool IsMuted, bool IsArchived, ActivitySource Source)
{
    public const string SignInOnly = "sign_in_only";
}

/// <summary>
/// A bounded, in-memory list of recent events across every service, fed by <see cref="SyncViewModel.BatchApplied"/>,
/// the outbox and typing. Messages are looked up only through <see cref="IMessagePeek"/> — memory each backend already
/// holds — so the stream costs no provider request of any kind, Instagram's included. What an entry says is decided once,
/// when it is appended, and never changed afterwards.
/// </summary>
/// <remarks>
/// <see cref="Offer"/>, <see cref="ObserveSent"/> and <see cref="ObserveTyping"/> only copy and queue. One worker,
/// <see cref="RunAsync"/>, resolves and appends in the order things were offered; the list itself is changed, and
/// <see cref="Appended"/>, <see cref="Reset"/> raised, only through the worker's onUiThread, so a consumer on the interface
/// thread sees <see cref="Snapshot"/> and the events agree.
/// </remarks>
public sealed class ActivityStream(IMessagePeek peek, IAccessTokenStore tokens, MergedConversations? merges = null,
    Func<string, HistorySnapshot?>? cached = null, TimeProvider? clock = null, int capacity = 1000,
    // When the process started, as the clock tells it; the connection quiet period is measured from here. Null reads it
    // from ProgressIndicator, which is what the app wants; the checks pass their fake clock's start.
    DateTimeOffset? launchedAt = null)
{
    // Voice's cache and Messenger's store can lag the event that names a message, so a miss is asked again twice.
    private static readonly TimeSpan[] RetryOffsets = [TimeSpan.Zero, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(2)],
        Once = [TimeSpan.Zero];
    private static readonly TimeSpan HeadWait = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(60);
    private const int Parallel = 8;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly int limit = Math.Max(1, capacity);
    private readonly DateTimeOffset launched = launchedAt ?? (clock ?? TimeProvider.System).GetUtcNow() - ProgressIndicator.SinceLaunch;
    private readonly SemaphoreSlim peeks = new(Parallel);
    // Written from the interface thread, the typing loop and the outbox; made once whichever arrives first.
    private Channel<Work>? queue;
    private Channel<Work> Queue => LazyInitializer.EnsureInitialized(ref queue, () => Channel.CreateBounded<Work>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite },
        _ => Count(Counter.Dropped)));

    public event EventHandler<ActivityEntry>? Appended;
    public event EventHandler? Reset;
    /// <summary>On the worker, just before a profile change zeroes the counts: the <see cref="Summary"/> they came to.</summary>
    public event EventHandler<string>? Clearing;

    // ---- The list. Changed only inside the worker's onUiThread actions. ----
    private readonly object gate = new();
    private readonly LinkedList<ActivityEntry> ring = new();

    public IReadOnlyList<ActivityEntry> Snapshot() { lock (gate) return [.. ring]; }

    /// <summary>The newest incoming message, call or voicemail, optionally narrowed further.</summary>
    public ActivityEntry? LastIncoming(Func<ActivityEntry, bool>? filter = null)
    {
        lock (gate)
            for (var node = ring.Last; node is not null; node = node.Previous)
                if (!node.Value.IsOutgoing && node.Value.Kind is ActivityKind.Message or ActivityKind.Call or ActivityKind.Voicemail &&
                    (filter is null || filter(node.Value)))
                    return node.Value;
        return null;
    }

    // ---- Counts. The only thing that reaches a log. ----
    private enum Counter { Messages, Peek, Preview, Generic, Sent, Edits, Unsends, Reactions, Reads, Calls, Typing, Connections, Dropped }
    private readonly long[] counts = new long[13];
    private void Count(Counter counter) => Interlocked.Increment(ref counts[(int)counter]);
    private long Read(Counter counter) => Interlocked.Read(ref counts[(int)counter]);

    /// <summary>Counts only: never a name, a text or an id.</summary>
    public string Summary() =>
        $"activity summary messages={Read(Counter.Messages)} peek={Read(Counter.Peek)} preview={Read(Counter.Preview)} " +
        $"generic={Read(Counter.Generic)} sent={Read(Counter.Sent)} edits={Read(Counter.Edits)} unsends={Read(Counter.Unsends)} " +
        $"reactions={Read(Counter.Reactions)} reads={Read(Counter.Reads)} calls={Read(Counter.Calls)} typing={Read(Counter.Typing)} " +
        $"connections={Read(Counter.Connections)} dropped={Read(Counter.Dropped)}";

    // ---- Input. Cheap and synchronous: copy, queue, return. ----
    private abstract record Work(DateTimeOffset At);
    // Earlier: the conversation cache's copy of each named message, the base an edit, unsend, reaction or read is measured
    // against. Newest: the newest outgoing message that cache holds for each conversation the batch names.
    private sealed record BatchWork(DateTimeOffset At, RealtimeEvent[] Events, IReadOnlyDictionary<string, ChatSummary> Chats,
        IReadOnlyDictionary<string, Place> Places, IReadOnlyDictionary<string, ChatMessage> Earlier,
        IReadOnlyDictionary<string, (DateTimeOffset At, string Id)> Newest) : Work(At);
    private sealed record SentWork(DateTimeOffset At, ChatMessage Message) : Work(At);
    private sealed record TypingWork(DateTimeOffset At, TypingUpdate Update) : Work(At);
    // Where a conversation is shown: its merged conversation when it belongs to one, with that group's own title and flags.
    private sealed record Place(string DisplayId, string? Title, bool? Muted, bool? Archived);

    /// <summary>
    /// Takes one applied batch, on the interface thread, at the moment <see cref="SyncViewModel.BatchApplied"/> raises it.
    /// Besides copying, it takes the earlier copy of each named message from the conversation cache, because this is the
    /// one moment that cache is sure to hold the copy from before the batch, and the cache belongs to this thread. The
    /// cached copy is only ever a comparison base: whether a message is new is decided from the event and the stream's own
    /// tables, because an open conversation may already have re-read a message whose own batch is still to come.
    /// </summary>
    public void Offer(AppliedBatch batch)
    {
        if (batch.Events.Count == 0) return;
        var events = batch.Events.ToArray();
        var places = new Dictionary<string, Place>(StringComparer.Ordinal);
        var earlier = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        var newest = new Dictionary<string, (DateTimeOffset At, string Id)>(StringComparer.Ordinal);
        foreach (var change in events)
        {
            if (change.ChatId is not { } chatId) continue;
            var place = PlaceOf(chatId, places);
            if (cached is null || change.Kind is not (RealtimeKind.MessageChanged or RealtimeKind.MessageDeleted) || change.Ids is not { Count: > 0 } ids) continue;
            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
            foreach (var key in place.DisplayId == chatId ? new[] { chatId } : new[] { chatId, place.DisplayId })
            {
                if (cached(key)?.Messages is not { Count: > 0 } messages) continue;
                foreach (var message in messages)
                {
                    if (message.ChatId != chatId) continue;
                    if (message.IsOutgoing && (!newest.TryGetValue(chatId, out var known) || message.Timestamp >= known.At))
                        newest[chatId] = (message.Timestamp, message.Id);
                    if (wanted.Contains(message.Id)) earlier.TryAdd(message.Id, message);
                }
            }
        }
        Queue.Writer.TryWrite(new BatchWork(time.GetUtcNow(), events, batch.Chats, places, earlier, newest));
    }

    /// <summary>A send the outbox has confirmed. The provider's own echo of it is then not recorded a second time.</summary>
    public void ObserveSent(ChatMessage message) => Queue.Writer.TryWrite(new SentWork(time.GetUtcNow(), message));

    /// <summary>Who is typing where. Only a start is an entry, at most one per conversation a minute.</summary>
    public void ObserveTyping(TypingUpdate update) => Queue.Writer.TryWrite(new TypingWork(time.GetUtcNow(), update));

    private Place PlaceOf(string chatId, Dictionary<string, Place>? places = null)
    {
        if (places?.TryGetValue(chatId, out var known) == true) return known;
        var display = merges?.DisplayId(chatId) ?? chatId;
        var group = display != chatId ? merges?.Find(display) : null;
        var place = new Place(display, group?.Title, group is null ? null : MergedConversations.MutedAt(group, time.GetUtcNow()), group?.Archived);
        if (places is not null) places[chatId] = place;
        return place;
    }

    // ---- The worker's own state. Touched only by the worker. ----
    private readonly Lru<ChatMessage> copies = new(Math.Max(1, capacity));
    private readonly Lru<bool> recorded = new(Math.Max(1, capacity));
    private readonly Lru<ChatSummary> summaries = new(Math.Max(1, capacity));
    private readonly Dictionary<string, (DateTimeOffset At, string Id)> newestOutgoing = new(StringComparer.Ordinal);
    // NotificationObserver's since: when each scope last connected, disconnected, needed a resync or needed signing in.
    // It describes the connections, not a profile, so a profile change keeps it.
    private readonly Dictionary<string, DateTimeOffset> connectedAt = new(StringComparer.Ordinal);
    private DateTimeOffset connectedEverywhere = DateTimeOffset.MaxValue;
    private readonly Dictionary<string, ActivityKind> connection = new(StringComparer.Ordinal);
    // Scopes whose SignInNeeded only needs the sign-in renewed; one that turns into a pairing to make again is a new line.
    private readonly HashSet<string> renewOnly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Typing, DateTimeOffset? Entered)> typing = new(StringComparer.Ordinal);
    private bool profileRead;
    private string? profile;
    private long sequence;

    /// <summary>The worker. Drains everything offered, in order, until cancelled.</summary>
    public async Task RunAsync(Func<Action, Task> onUiThread, CancellationToken token)
    {
        try
        {
            await foreach (var work in Queue.Reader.ReadAllAsync(token))
            {
                try { await StepAsync(work, onUiThread, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                // One step that fails loses its own entries and nothing else.
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task StepAsync(Work work, Func<Action, Task> onUiThread, CancellationToken token)
    {
        var current = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profileRead && current != profile)
        {
            // Another profile's activity is not this one's: everything goes, the counts too.
            copies.Clear(); recorded.Clear(); summaries.Clear(); newestOutgoing.Clear(); connection.Clear(); renewOnly.Clear(); typing.Clear();
            try { Clearing?.Invoke(this, Summary()); } catch (Exception) { }
            for (var index = 0; index < counts.Length; index++) Interlocked.Exchange(ref counts[index], 0);
            await onUiThread(() => { lock (gate) ring.Clear(); Reset?.Invoke(this, EventArgs.Empty); });
        }
        profileRead = true; profile = current;
        var output = new List<Output>();
        switch (work)
        {
            case BatchWork batch: await BatchAsync(batch, output, token); break;
            case SentWork sent: Sent(sent, output); break;
            case TypingWork update: Typing(update, output); break;
        }
        if (output.Count > 0) await onUiThread(() => Apply(output));
    }

    // What one step does to the list, applied in one go on the interface thread.
    private abstract record Output;
    private sealed record Append(ActivityEntry Entry) : Output;
    private sealed record Forget(IReadOnlySet<string> ChatIds) : Output;
    private void Apply(IReadOnlyList<Output> output)
    {
        foreach (var item in output)
        {
            if (item is Forget forget)
            {
                lock (gate)
                    for (var node = ring.First; node is not null;)
                    {
                        var next = node.Next;
                        if (node.Value.ChatId is { } id && forget.ChatIds.Contains(id) ||
                            node.Value.DisplayChatId is { } display && forget.ChatIds.Contains(display)) ring.Remove(node);
                        node = next;
                    }
                continue;
            }
            var entry = ((Append)item).Entry;
            lock (gate)
            {
                ring.AddLast(entry);
                while (ring.Count > limit) { ring.RemoveFirst(); Count(Counter.Dropped); }
            }
            Appended?.Invoke(this, entry);
        }
    }

    private async Task BatchAsync(BatchWork batch, List<Output> output, CancellationToken token)
    {
        foreach (var chat in batch.Chats.Values) summaries.Put(chat.Id, chat);
        // Every lookup starts at once and is taken in order; the head of the queue waits at most HeadWait.
        var started = time.GetUtcNow();
        var lookups = new Dictionary<(string Chat, string Id), Task<ChatMessage?>>();
        foreach (var change in batch.Events)
        {
            if (change.Kind != RealtimeKind.MessageChanged || change.ChatId is not { } chatId || change.Ids is null) continue;
            foreach (var id in change.Ids)
            {
                if (lookups.ContainsKey((chatId, id))) continue;
                // Only a message that may be new is worth waiting for; a change to one already known is either in memory
                // or not worth an entry.
                lookups[(chatId, id)] = LookUpAsync(chatId, id, MayBeNew(change, id), batch.At, started, token);
            }
        }
        // Every lookup is in hand before any read is judged, so the newest outgoing message is the newest of everything
        // known, the cache's page and this batch included, and one receipt naming several messages reads once.
        await Task.WhenAll(lookups.Values);
        foreach (var (chatId, known) in batch.Newest) NoteOutgoing(chatId, known.At, known.Id);
        foreach (var copy in batch.Earlier.Values) if (copy.IsOutgoing) NoteOutgoing(copy.ChatId, copy.Timestamp, copy.Id);
        foreach (var lookup in lookups.Values) if (lookup.Result is { IsOutgoing: true } found) NoteOutgoing(found.ChatId, found.Timestamp, found.Id);
        foreach (var change in batch.Events)
        {
            switch (change.Kind)
            {
                case RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired or RealtimeKind.ResyncRequired:
                    Connection(change, batch.At, output);
                    break;
                case RealtimeKind.ChatDeleted:
                    var gone = new HashSet<string>(change.Ids ?? [], StringComparer.Ordinal);
                    if (change.ChatId is { } deleted) gone.Add(deleted);
                    if (gone.Count > 0) output.Add(new Forget(gone));
                    break;
                case RealtimeKind.MessageDeleted when change.ChatId is { } chatId:
                    foreach (var id in change.Ids ?? []) Removed(batch, chatId, id, output, change.IsHistory);
                    break;
                case RealtimeKind.MessageChanged when change.ChatId is { } chatId:
                    foreach (var id in change.Ids ?? [])
                        if (lookups.Remove((chatId, id), out var lookup)) Changed(batch, change, chatId, id, await lookup, output);
                    break;
            }
        }
    }

    // Memory only, through IMessagePeek. A miss on a message that may be new is asked again at +750 ms and +2 s from when
    // it was offered, never past HeadWait from when this batch began.
    private async Task<ChatMessage?> LookUpAsync(string chatId, string id, bool patient, DateTimeOffset offered, DateTimeOffset started, CancellationToken token)
    {
        var deadline = started + HeadWait;
        foreach (var offset in patient ? RetryOffsets : Once)
        {
            if (offset > TimeSpan.Zero)
            {
                var due = offered + offset;
                if (due > deadline) due = deadline;
                var wait = due - time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, token);
            }
            await peeks.WaitAsync(token);
            try
            {
                if (await peek.PeekAsync(chatId, id, token) is { } found && found.Id == id) return found;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* A failed peek is a miss. */ }
            finally { peeks.Release(); }
            if (time.GetUtcNow() >= deadline) break;
        }
        return null;
    }

    private void Changed(BatchWork batch, RealtimeEvent change, string chatId, string id, ChatMessage? message, List<Output> output)
    {
        var chat = batch.Chats.GetValueOrDefault(chatId) ?? summaries.Get(chatId);
        if (chat?.IsSpam == true) { if (message is not null) Remember(message); return; }
        var earlier = copies.Get(id) ?? batch.Earlier.GetValueOrDefault(id);
        // Whether it is new is the event's word and the stream's own memory, never the cache's: the cache may already hold
        // an arrival that an open conversation re-read before the arrival's own batch got here.
        var mayBeNew = MayBeNew(change, id);
        if (message is null)
        {
            // Unknown means unavailable: only an arrival has a line of its own without the message in hand, and only when
            // what is known of its time says it belongs to this session.
            if (!mayBeNew) return;
            var preview = chat?.Preview is { } listed && listed.Id == id ? listed : null;
            if ((preview?.Timestamp ?? earlier?.Timestamp ?? chat?.Preview?.Timestamp ?? chat?.LastActivity) is not { } when || !IsArrival(when)) return;
            recorded.Put(id, true);
            if (preview is not null)
            {
                Count(Counter.Preview);
                Arrival(batch, chatId, chat, preview, ActivitySource.ListPreview, output);
            }
            else
            {
                Count(Counter.Generic); Count(Counter.Messages);
                output.Add(new Append(Entry(ActivityKind.Message, change.ReceivedAt ?? batch.At, chatId, batch, chat, Sender(chat, "", null, false),
                    false, null, null, when < Since(chatId), ActivitySource.Generic)));
            }
            return;
        }
        Remember(message);
        // History can fill in reactions, edits and receipts missing from a saved copy. Keep the new baseline, but
        // observing that state now does not tell us when it changed on the service.
        if (change.IsHistory) return;
        // A message from before this session is history, not an arrival, however the event labels it: a reaction, an edit
        // or a link preview on an older message, and every row a history page stores. It is measured as a change instead.
        var hermesLine = HermesLine(change, id, message);
        if ((mayBeNew || hermesLine) && IsArrival(message.Timestamp))
        {
            recorded.Put(id, true);
            // A system line, a hidden row, a message already taken back or one first seen edited says nothing new.
            if (message.IsHidden || message.IsDeleted || message.Type == "system" && !hermesLine || message.EditedAt is not null) return;
            Count(Counter.Peek);
            Arrival(batch, chatId, chat, message, ActivitySource.Peek, output);
            return;
        }
        if (earlier is null) return;
        Derived(batch, chatId, chat, earlier, message, output);
    }

    // A change that may announce a new message: the event says so, and the stream has neither recorded nor seen it.
    private bool MayBeNew(RealtimeEvent change, string id) =>
        !change.IsHistory && !change.ReceiptOnly && change.IsNewMessage != false && !recorded.Contains(id) && copies.Get(id) is null;

    // Hermes announces its own notes and the prompts the server's repeats send as changes, not arrivals, and a reply streams in under one id whose
    // copy is already held when the change that says it is whole comes: each of those is still a line of its own, once.
    private bool HermesLine(RealtimeEvent change, string id, ChatMessage message) =>
        HermesIds.Owns(message.ChatId) && !change.IsHistory && !change.ReceiptOnly && !recorded.Contains(id) &&
        (message.Type is "system" or "scheduled" || change.IsNewMessage == true && !message.IsOutgoing);

    // Sent during this session, after the app launched. Before its connection's since it is backfill, replayed after a
    // reconnect or a resync; before launch it is history, for which the stream has no line.
    private bool IsArrival(DateTimeOffset timestamp) => timestamp >= launched;

    private void Remember(ChatMessage message)
    {
        copies.Put(message.Id, message);
        if (message.IsOutgoing) NoteOutgoing(message.ChatId, message.Timestamp, message.Id);
    }

    private void NoteOutgoing(string chatId, DateTimeOffset at, string id)
    {
        if (!newestOutgoing.TryGetValue(chatId, out var newest) || at >= newest.At) newestOutgoing[chatId] = (at, id);
    }

    private void Arrival(BatchWork batch, string chatId, ChatSummary? chat, ChatMessage message, ActivitySource source, List<Output> output)
    {
        var (kind, detail) = KindOf(message);
        Count(kind == ActivityKind.Message ? Counter.Messages : Counter.Calls);
        output.Add(new Append(Entry(kind, message.Timestamp, chatId, batch, chat, Sender(chat, message.SenderId, message.SenderName, message.IsOutgoing),
            message.IsOutgoing, message, detail, message.Timestamp < Since(chatId), source)));
    }

    private static (ActivityKind Kind, string? Detail) KindOf(ChatMessage message)
    {
        var kind = message.CallKind is null ? ActivityKind.Message
            : message.CallKind.Contains("voicemail", StringComparison.OrdinalIgnoreCase) ? ActivityKind.Voicemail : ActivityKind.Call;
        return (kind, kind switch
        {
            ActivityKind.Voicemail => string.IsNullOrWhiteSpace(message.Text) ? null : message.Text,
            ActivityKind.Call => message.CallKind,
            _ => null,
        });
    }

    /// <summary>When this process started, as the stream measures it: anything older is history, which it has no line for.</summary>
    public DateTimeOffset Launched => launched;

    /// <summary>
    /// A message from before this session as the console's history shows it: the entry its arrival would have made, with
    /// the same sender, place and flags, but never appended, counted or remembered. Null for what an arrival would not
    /// show either: a spam conversation, a hidden or taken-back message, a system line. On the interface thread, like
    /// <see cref="Offer"/>, which reads the merges the same way.
    /// </summary>
    public ActivityEntry? Earlier(ChatSummary chat, ChatMessage message)
    {
        if (chat.IsSpam || message.IsHidden || message.IsDeleted || message.Type == "system" && !HermesIds.Owns(message.ChatId)) return null;
        var (kind, detail) = KindOf(message);
        return Entry(kind, message.Timestamp, message.ChatId, PlaceOf(message.ChatId), chat,
            Sender(chat, message.SenderId, message.SenderName, message.IsOutgoing), message.IsOutgoing, message, detail, false, ActivitySource.History);
    }

    // Edited, taken back, reacted to or read, each against the copy from before this change. Delivery states and
    // anything else that moved record nothing.
    private void Derived(BatchWork batch, string chatId, ChatSummary? chat, ChatMessage earlier, ChatMessage message, List<Output> output)
    {
        var at = batch.At;
        if (!earlier.IsDeleted && message.IsDeleted)
        {
            Count(Counter.Unsends);
            output.Add(new Append(Entry(ActivityKind.Unsent, at, chatId, batch, chat, Sender(chat, earlier.SenderId, earlier.SenderName, earlier.IsOutgoing),
                earlier.IsOutgoing, earlier, null, false, ActivitySource.Peek)));
            return;
        }
        if (message.EditedAt is { } edited && earlier.EditedAt != edited && !string.Equals(earlier.Text, message.Text, StringComparison.Ordinal))
        {
            Count(Counter.Edits);
            output.Add(new Append(Entry(ActivityKind.Edited, at, chatId, batch, chat, Sender(chat, message.SenderId, message.SenderName, message.IsOutgoing),
                message.IsOutgoing, message, null, false, ActivitySource.Peek)));
        }
        var before = earlier.Reactions.GroupBy(r => (r.Key, r.ParticipantId)).ToDictionary(g => g.Key, g => g.Count());
        var after = message.Reactions.GroupBy(r => (r.Key, r.ParticipantId)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var ((key, participant), number) in after)
            for (var extra = number - before.GetValueOrDefault((key, participant)); extra > 0; extra--)
                Reaction(batch, chatId, chat, message, "+" + key, participant, output);
        foreach (var ((key, participant), number) in before)
            for (var fewer = number - after.GetValueOrDefault((key, participant)); fewer > 0; fewer--)
                Reaction(batch, chatId, chat, message, "-" + key, participant, output);
        if (message.IsOutgoing && newestOutgoing.GetValueOrDefault(message.ChatId).Id == message.Id)
        {
            var readers = new List<string>();
            foreach (var (person, seen) in message.Delivery.SeenBy)
                if (seen == true && earlier.Delivery.SeenBy.GetValueOrDefault(person) != true) readers.Add(person);
            foreach (var person in message.Delivery.ReadMark ?? [])
                if (earlier.Delivery.ReadMark?.Contains(person) != true && !readers.Contains(person)) readers.Add(person);
            var seenNow = message.Delivery.Seen == true && earlier.Delivery.Seen != true;
            if (readers.Count > 0 || seenNow)
            {
                Count(Counter.Reads);
                var reader = readers.Count > 0 ? Sender(chat, readers[0], null, false) : Sender(chat, "", null, false);
                output.Add(new Append(Entry(ActivityKind.Read, at, chatId, batch, chat, reader, true, message,
                    readers.Count > 1 ? readers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, false, ActivitySource.Peek)));
            }
        }
    }

    private void Reaction(BatchWork batch, string chatId, ChatSummary? chat, ChatMessage message, string detail, string participant, List<Output> output)
    {
        Count(Counter.Reactions);
        var self = chat?.Participants.FirstOrDefault(p => p.Id == participant)?.IsSelf == true;
        output.Add(new Append(Entry(ActivityKind.Reaction, batch.At, chatId, batch, chat,
            chat?.Participants.FirstOrDefault(p => p.Id == participant)?.DisplayName is { Length: > 0 } name ? name : "Someone",
            self, message, detail, false, ActivitySource.Peek)));
    }

    private void Removed(BatchWork batch, string chatId, string id, List<Output> output, bool isHistory)
    {
        var chat = batch.Chats.GetValueOrDefault(chatId) ?? summaries.Get(chatId);
        if (chat?.IsSpam == true) return;
        if ((copies.Get(id) ?? batch.Earlier.GetValueOrDefault(id)) is not { IsDeleted: false } earlier) return;
        copies.Put(id, earlier with { IsDeleted = true });
        if (isHistory) return;
        Count(Counter.Unsends);
        output.Add(new Append(Entry(ActivityKind.Unsent, batch.At, chatId, batch, chat, Sender(chat, earlier.SenderId, earlier.SenderName, earlier.IsOutgoing),
            earlier.IsOutgoing, earlier, null, false, ActivitySource.Peek)));
    }

    private void Sent(SentWork sent, List<Output> output)
    {
        var message = sent.Message;
        Remember(message);
        if (recorded.Contains(message.Id)) return;
        recorded.Put(message.Id, true);
        var chat = summaries.Get(message.ChatId);
        if (chat?.IsSpam == true) return;
        Count(Counter.Sent);
        var place = PlaceOf(message.ChatId);
        output.Add(new Append(Entry(ActivityKind.Message, message.Timestamp, message.ChatId, place, chat, Sender(chat, message.SenderId, message.SenderName, true),
            true, message, null, false, ActivitySource.Outbox)));
    }

    private void Typing(TypingWork work, List<Output> output)
    {
        var update = work.Update;
        var now = update.Participants.Count > 0;
        var (was, entered) = typing.GetValueOrDefault(update.ChatId);
        if (!now) { typing[update.ChatId] = (false, entered); return; }
        typing[update.ChatId] = (true, entered);
        if (was || entered is { } last && work.At - last < TypingInterval) return;
        var chat = summaries.Get(update.ChatId);
        if (chat?.IsSpam == true) return;
        typing[update.ChatId] = (true, work.At);
        Count(Counter.Typing);
        var person = update.Participants[0];
        output.Add(new Append(Entry(ActivityKind.Typing, work.At, update.ChatId, PlaceOf(update.ChatId), chat,
            Sender(chat, person.UserId, person.DisplayName, false), false, null, null, false, ActivitySource.State)));
    }

    // Recorded only on a real change of a scope's state, and never while the app is still starting.
    private void Connection(RealtimeEvent change, DateTimeOffset at, List<Output> output)
    {
        var kind = change.Kind switch
        {
            RealtimeKind.Connected => ActivityKind.Connected,
            RealtimeKind.Disconnected => ActivityKind.Disconnected,
            _ => ActivityKind.SignInNeeded,
        };
        // The same rule as NotificationObserver, on the same four kinds: a reconnect or a resync makes what it replays
        // backfill on that connection only.
        if (change.Scope is { } connected) connectedAt[connected] = at;
        else { connectedAt.Clear(); connectedEverywhere = at; }
        // A resync moves the since and is not a line of its own.
        if (change.Kind == RealtimeKind.ResyncRequired) return;
        var scope = change.Scope ?? "";
        var signInOnly = kind == ActivityKind.SignInNeeded && change.SignInOnly;
        // "Needs its sign-in renewed" becoming "needs pairing again" is the same kind with a different detail, and the
        // console went on showing the first after the second was known.
        var changed = !connection.TryGetValue(scope, out var previous) || previous != kind ||
            kind == ActivityKind.SignInNeeded && renewOnly.Contains(scope) != signInOnly;
        connection[scope] = kind;
        if (signInOnly) renewOnly.Add(scope); else renewOnly.Remove(scope);
        if (!changed || at - launched < ProgressIndicator.StartupQuiet) return;
        Count(Counter.Connections);
        // A sign-in that only needs renewing (Google Messages) is told apart from a pairing to make again.
        var detail = kind == ActivityKind.SignInNeeded && change.SignInOnly ? ActivityEntry.SignInOnly : null;
        output.Add(new Append(new ActivityEntry(Interlocked.Increment(ref sequence), kind, at, null, null,
            change.Scope is { } named ? ServiceScopes.Name(named) : "", null, null, false, null, false, null, detail, false, false, false, ActivitySource.State)));
    }

    private DateTimeOffset Since(string chatId) =>
        connectedAt.Where(pair => chatId.StartsWith(pair.Key, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).DefaultIfEmpty(connectedEverywhere).First();

    // Whoever did it. In a one-to-one conversation that is the conversation's own title, which carries the local
    // contact name; elsewhere the message's own name, then the participant's, then "Someone".
    private static string? Sender(ChatSummary? chat, string personId, string? named, bool outgoing)
    {
        if (outgoing) return named ?? chat?.Participants.FirstOrDefault(p => p.IsSelf == true)?.DisplayName;
        if (chat is { Type: "single" } && !string.IsNullOrWhiteSpace(chat.Title)) return chat.Title;
        if (!string.IsNullOrWhiteSpace(named)) return named;
        return chat?.Participants.FirstOrDefault(p => p.Id == personId)?.DisplayName is { Length: > 0 } name ? name : "Someone";
    }

    private ActivityEntry Entry(ActivityKind kind, DateTimeOffset at, string chatId, BatchWork batch, ChatSummary? chat, string? sender,
        bool outgoing, ChatMessage? message, string? detail, bool backfill, ActivitySource source) =>
        Entry(kind, at, chatId, batch.Places.GetValueOrDefault(chatId) ?? PlaceOf(chatId), chat, sender, outgoing, message, detail, backfill, source);

    private ActivityEntry Entry(ActivityKind kind, DateTimeOffset at, string chatId, Place place, ChatSummary? chat, string? sender,
        bool outgoing, ChatMessage? message, string? detail, bool backfill, ActivitySource source) =>
        new(Interlocked.Increment(ref sequence), kind, at, chatId, place.DisplayId, chat?.Network ?? ServiceScopes.Name(chatId), chat?.AccountId,
            place.Title ?? chat?.Title, chat?.Type == "group", sender, outgoing, message, detail, backfill,
            place.Muted ?? chat?.IsMuted ?? false, place.Archived ?? chat?.IsArchived ?? false, source);

    // Least recently used first out. Worker-only, so unlocked.
    private sealed class Lru<T>(int capacity)
    {
        private readonly Dictionary<string, (T Value, LinkedListNode<string> Node)> entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> order = new();
        public bool Contains(string key) => entries.ContainsKey(key);
        public T? Get(string key)
        {
            if (!entries.TryGetValue(key, out var entry)) return default;
            order.Remove(entry.Node); order.AddLast(entry.Node);
            return entry.Value;
        }
        public void Put(string key, T value)
        {
            if (entries.Remove(key, out var old)) order.Remove(old.Node);
            entries[key] = (value, order.AddLast(key));
            while (entries.Count > capacity) { entries.Remove(order.First!.Value); order.RemoveFirst(); }
        }
        public void Clear() { entries.Clear(); order.Clear(); }
    }
}
