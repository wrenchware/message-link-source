using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Storage;
using NativeBeeper.Core.Services;
using Kind = NativeBeeper.Storage.MessageArchive.Kind;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed record GoogleCrawlState(string ChatId, string? Cursor, bool Done, int Fetched, long Total, DateTimeOffset UpdatedAt,
    // The provider id at the newest edge of the last contiguous crawl. Catch-up continues until it reaches this exact
    // boundary; unrelated foreground/realtime archive writes are never treated as proof that the interval is complete.
    string? NewestBoundary = null, string? CatchUpCursor = null, string? CatchUpHead = null);

internal static class GoogleCrawlProgress
{
    public static (GoogleCrawlState State, bool Complete) CatchUp(GoogleCrawlState state, DateTimeOffset chatActivity,
        IReadOnlyList<string> messageIds, string? cursor, string? next, bool reachedLegacyKnown = false)
    {
        var head = state.CatchUpHead ?? (cursor is null ? messageIds.FirstOrDefault() : null);
        // Crawl rows written before NewestBoundary existed use the old safe stop once. Completion below records
        // the current head, so every later catch-up uses the exact contiguous boundary.
        var reached = state.NewestBoundary is { } boundary
            ? messageIds.Contains(boundary, StringComparer.Ordinal)
            : reachedLegacyKnown;
        var complete = reached || messageIds.Count == 0 || next is null || next == cursor;
        return complete
            ? (state with { NewestBoundary = head ?? state.NewestBoundary, CatchUpCursor = null, CatchUpHead = null, UpdatedAt = chatActivity }, true)
            : (state with { CatchUpCursor = next, CatchUpHead = head }, false);
    }
}
// WholeList: read by the paged request (0.1.379). A list saved before then holds only the phone's first 50, so it answers at
// once but is read again rather than trusted for six hours.
internal sealed record GoogleSavedContacts(GoogleContact[] Contacts, DateTimeOffset SavedAt, bool WholeList = false);
internal sealed record GoogleArchiveHit(string MessageId, string ChatId, DateTimeOffset At);
// The message an attachment came from, and the next newer saved message of its conversation, if any.
internal sealed record GoogleMediaSource(string ChatId, string MessageId, DateTimeOffset At, string? NewerId, DateTimeOffset? NewerAt)
{
    // What the helper's ui.mediaRelearn takes: the page cursor in the shape the helper's pageCursor makes (milliseconds), and the message the page must hold.
    // With no newer saved message the page is anchored at the message itself, a millisecond after it (2026-10-12, code review): a null cursor read the
    // conversation's newest page, which misses it once Google holds a page of messages newer than the archive does. Which part of a cursor Google's phone
    // pages by is not known, so that anchor is a guess: the helper checks the page holds the message and does not spend the attachment's five minutes when not.
    public object Parameters(string mediaId) => new
    {
        id = mediaId, chatId = ChatId, messageId = MessageId,
        anchor = Anchor,
        cursor = NewerId is { } newer && NewerAt is { } at
            ? new { lastItemID = newer, lastItemTimestamp = at.ToUnixTimeMilliseconds() }
            : new { lastItemID = MessageId, lastItemTimestamp = At.ToUnixTimeMilliseconds() + 1 },
    };

    /// <summary>What the page is anchored at: the next newer saved message, or the message itself (<c>self</c>, a guess the helper verifies).</summary>
    public string Anchor => NewerId is not null && NewerAt is not null ? "newer" : "self";
}

// Encrypted local copy of chats, messages and contacts for search. Everything here is
// expendable: the phone stays authoritative and the archive can always be rebuilt.
// Only word postings and (chat, time) metadata stay in memory; message bodies are read
// back from disk for the result page being shown.
internal sealed class GoogleArchive(MessageArchive store)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> Unsearchable = ["OUTGOING_DELETED", "INCOMING_DELETED", "OUTGOING_DRAFT", "OUTGOING_SCHEDULED"];
    // Once per process: Enum.GetValues returns a fresh array on every call, and this runs for every message indexed.
    private static readonly SearchContent[] ContentKinds = Enum.GetValues<SearchContent>();
    private readonly object sync = new();
    private readonly TextSearchIndex index = new();
    private readonly Dictionary<string, (string ChatId, DateTimeOffset At, int Content, bool Searchable)> documents = new(StringComparer.Ordinal);
    // Which message each photo or video came from, by the helper's attachment id: ids only, filled as messages are
    // indexed. The helper resolves an attachment only from a reference held in its memory, and on 2026-10-12 a video sent
    // before the Messages Server existed answered media_unavailable from it. This is what lets the helper re-learn one
    // (MediaSource): the saved message is the lasting record, so nothing new is stored.
    private readonly Dictionary<string, string> mediaOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GoogleChat> chats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GoogleCrawlState> crawl = new(StringComparer.Ordinal);
    // Each conversation's newest encryption switch line: when, and which way. Metadata like documents, filled as messages
    // are indexed, so answering it reads no message from disk.
    private readonly Dictionary<string, (DateTimeOffset At, bool Encrypted)> switches = new(StringComparer.Ordinal);
    private readonly List<(string Profile, MessageArchive.Entry Entry)> pending = [];
    // What each message was last queued as, by a hash of its bytes, so one remembered again unchanged (the newest page of a conversation, every time it is
    // read) is not encrypted and written again (2026-10-14 audit). Bounded, oldest first out; one that falls out is written once more, as before.
    private readonly Dictionary<string, UInt128> written = new(StringComparer.Ordinal);
    private readonly Queue<string> writtenOrder = new();
    internal const int WrittenLimit = 8192;
    // Conversations whose last write failed: written again when next remembered, even unchanged, since a failed write means the data is written again later.
    private readonly HashSet<string> unwrittenChats = new(StringComparer.Ordinal);
    private GoogleSavedContacts? contacts;
    private string? profile;
    private Task<bool>? loading;
    // Whether a delayed flush is on its way, cleared under sync in the same step that finds nothing left to write, so a
    // write can never land after the last look and before the flag drops. Every flush, and the reset's clear, takes the
    // writer in turn, so batches reach disk in the order they were queued and nothing lands after a reset.
    private bool flushQueued;
    private readonly SemaphoreSlim writer = new(1, 1);
    internal (bool Queued, int Pending) FlushState { get { lock (sync) return (flushQueued, pending.Count); } }

    public string? Profile { get { lock (sync) return profile; } }
    public int MessageCount => index.Count;
    public bool IsLoaded => loading is { IsCompletedSuccessfully: true, Result: true };

    // Loads once per profile; a different paired account resets the in-memory view.
    public Task<bool> LoadAsync(string account, Func<Task>? beforeMessageBatch = null)
    {
        lock (sync)
        {
            if (profile == account && loading is not null && !loading.IsFaulted && !loading.IsCanceled) return loading;
            if (profile != account) { documents.Clear(); mediaOwners.Clear(); chats.Clear(); crawl.Clear(); switches.Clear(); contacts = null; index.Clear(); ForgetWritten(); chatsReadFor = null; }
            profile = account;
            return loading = Task.Run(() => LoadCoreAsync(account, beforeMessageBatch));
        }
    }

    // Whether this account's saved conversations are here, which a send asks before trusting that one is not hidden.
    private string? chatsReadFor;
    public bool ChatsRead(string account) { lock (sync) return chatsReadFor == account; }

    // The saved conversations alone, ahead of the paced message load, as LoadCrawlStatesAsync reads its records. Rows already here are newer and stay.
    public async Task<bool> LoadChatsAsync(string account)
    {
        try
        {
            foreach (var bytes in await store.ReadAllAsync(account, Kind.Chat))
                if (Deserialize<GoogleChat>(bytes) is { Id.Length: > 0 } chat)
                    lock (sync) if (profile == account) chats.TryAdd(chat.Id, chat);
            lock (sync)
            {
                if (profile != account) return false;
                chatsReadFor = account;
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<bool> LoadCoreAsync(string account, Func<Task>? beforeMessageBatch)
    {
        try
        {
            if (!await LoadChatsAsync(account)) return false;
            await using (var batches = store.ReadBatchesAsync(account, Kind.Message).GetAsyncEnumerator())
            {
                while (true)
                {
                    // Neither the archive lock nor the storage gate is held while typing pauses warm-up.
                    if (beforeMessageBatch is not null) await beforeMessageBatch();
                    lock (sync) if (profile != account) return false;
                    if (!await batches.MoveNextAsync()) break;
                    foreach (var bytes in batches.Current)
                        if (Deserialize<GoogleMessage>(bytes) is { Id.Length: > 0 } message)
                            lock (sync) if (profile == account && !documents.ContainsKey(message.Id)) IndexCore(message);
                }
            }
            foreach (var bytes in await store.ReadAllAsync(account, Kind.CrawlState))
                if (Deserialize<GoogleCrawlState>(bytes) is { ChatId.Length: > 0 } state)
                    lock (sync) if (profile == account) crawl.TryAdd(state.ChatId, state);
            if ((await store.ReadAllAsync(account, Kind.Contacts)).FirstOrDefault() is { } saved && Deserialize<GoogleSavedContacts>(saved) is { } list)
                lock (sync) if (profile == account) contacts ??= list;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; } // Unavailable disk: search falls back to live session data.
    }

    // Each conversation's crawl record, read ahead of the paced message load (a few hundred small rows, against tens of
    // thousands of messages indexed for search), so the catch-up at connect knows which conversations had messages while
    // the app was closed. Records already here are newer and stay; the full load's TryAdd leaves these the same way.
    public async Task<bool> LoadCrawlStatesAsync(string account)
    {
        try
        {
            foreach (var bytes in await store.ReadAllAsync(account, Kind.CrawlState))
                if (Deserialize<GoogleCrawlState>(bytes) is { ChatId.Length: > 0 } state)
                    lock (sync) if (profile == account) crawl.TryAdd(state.ChatId, state);
            lock (sync) return profile == account;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static T? Deserialize<T>(byte[] bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    // Caller holds sync.
    private void IndexCore(GoogleMessage message)
    {
        var searchable = !Unsearchable.Contains(message.Status);
        // Only classification metadata and word postings stay in memory, including attachment-only messages.
        var mapped = GoogleModels.Map(message, chats.GetValueOrDefault(message.ChatId) ??
            new GoogleChat(message.ChatId, "", [], "", message.At, false, false));
        var content = 0;
        foreach (var kind in ContentKinds)
            if (kind != SearchContent.All && MessageSearch.HasContent(mapped, kind)) content |= 1 << (int)kind;
        documents[message.Id] = (message.ChatId, message.At, content, searchable && !mapped.IsHidden && !mapped.IsDeleted);
        // A link card is the helper's own, fetched from the website; only what came through the phone is re-learned.
        foreach (var media in message.Media ?? [])
            if (media.Id.Length > 0 && media.Link is null && !media.Id.StartsWith("link-preview:", StringComparison.Ordinal))
                mediaOwners[media.Id] = message.Id;
        // Two lines at the same moment that disagree leave encryption off: that can only hide a lock.
        if (mapped.EncryptionSwitch is { } switched && (!switches.TryGetValue(message.ChatId, out var known) || message.At > known.At ||
            message.At == known.At && !switched))
            switches[message.ChatId] = (message.At, switched);
        if (!searchable) index.Remove(message.Id);
        else index.Set(message.Id, MessageSearch.SearchableText(mapped));
    }

    /// <summary>
    /// The newest encryption switch line saved for a conversation, and how far the saved history runs on from it without
    /// a gap. Nothing until everything saved has been read, because part way through a newer line the other way may still
    /// be on disk.
    /// </summary>
    /// <remarks>
    /// A finished crawl saved everything from the start of the conversation up to its boundary: the newest message when
    /// it began, or when it last caught up. Messages saved after that, live or from an opened conversation, can have gaps
    /// between them, so they vouch for nothing beyond themselves.
    /// </remarks>
    public SavedEncryptionSwitch? Switch(string chatId)
    {
        if (!IsLoaded) return null;
        lock (sync)
        {
            if (!switches.TryGetValue(chatId, out var line)) return null;
            DateTimeOffset? through = crawl.GetValueOrDefault(chatId) is { Done: true, NewestBoundary: { } head } &&
                documents.TryGetValue(head, out var boundary) && boundary.At >= line.At ? boundary.At : null;
            return new(line.Encrypted, line.At, through);
        }
    }

    public void RememberMessages(string account, IEnumerable<GoogleMessage> messages)
    {
        lock (sync)
        {
            if (profile != account) return;
            foreach (var message in messages.Where(m => m.Id.Length > 0))
            {
                IndexCore(message);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
                var hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(System.Security.Cryptography.SHA256.HashData(bytes));
                if (written.TryGetValue(message.Id, out var last) && last == hash) continue;
                if (!written.ContainsKey(message.Id)) writtenOrder.Enqueue(message.Id);
                written[message.Id] = hash;
                while (writtenOrder.Count > WrittenLimit) written.Remove(writtenOrder.Dequeue());
                pending.Add((account, new(Kind.Message, message.Id, bytes)));
            }
        }
        ScheduleFlush();
    }

    public void RememberChats(string account, IEnumerable<GoogleChat> list)
    {
        lock (sync)
        {
            if (profile != account) return;
            foreach (var chat in list.Where(c => c.Id.Length > 0))
            {
                if (chats.TryGetValue(chat.Id, out var known) && SameChat(known, chat) && !unwrittenChats.Contains(chat.Id)) continue;
                chats[chat.Id] = chat;
                unwrittenChats.Remove(chat.Id);
                pending.Add((account, new(Kind.Chat, chat.Id, JsonSerializer.SerializeToUtf8Bytes(chat))));
            }
        }
        ScheduleFlush();
    }

    /// <summary>
    /// Whether two copies of a conversation say the same. A record compares its array by reference, so two copies from two reads of the list were never equal and
    /// every list rewrote every row (2026-10-14 audit): here it is every field equal and the same people in the same order (a person holds no array of its own).
    /// </summary>
    internal static bool SameChat(GoogleChat known, GoogleChat chat) =>
        known with { Participants = chat.Participants } == chat && (known.Participants ?? []).SequenceEqual(chat.Participants ?? []);

    // Caller holds sync.
    private void ForgetWritten()
    {
        written.Clear(); writtenOrder.Clear(); unwrittenChats.Clear();
    }

    public GoogleSavedContacts? Contacts { get { lock (sync) return contacts; } }
    public void SaveContacts(string account, GoogleContact[] list)
    {
        var saved = new GoogleSavedContacts(list, DateTimeOffset.UtcNow, WholeList: true);
        lock (sync)
        {
            if (profile != account) return;
            contacts = saved;
            pending.Add((account, new(Kind.Contacts, "all", JsonSerializer.SerializeToUtf8Bytes(saved))));
        }
        ScheduleFlush();
    }

    // Forgets this profile's archive in memory and on disk. Unloading first makes every later write for the profile a
    // no-op until LoadAsync runs again, so nothing queued before the reset lands after it; the next load starts empty.
    public async Task ResetAsync(string account)
    {
        lock (sync)
        {
            profile = null; loading = null; chatsReadFor = null;
            documents.Clear(); mediaOwners.Clear(); chats.Clear(); crawl.Clear(); switches.Clear(); contacts = null; index.Clear(); pending.Clear(); ForgetWritten();
        }
        // A batch already taken is written first; its rows are cleared below.
        await writer.WaitAsync();
        try { await store.ClearAsync(account); }
        finally { writer.Release(); }
    }

    public GoogleCrawlState? CrawlState(string chatId) { lock (sync) return crawl.GetValueOrDefault(chatId); }
    // One conversation's archived messages in history order, by id and time only, for reading a window around a moment.
    // A walk of the in-memory metadata; no message is read from disk.
    public (string Id, DateTimeOffset At)[] Timeline(string chatId)
    {
        lock (sync)
            return [.. documents.Where(entry => entry.Value.ChatId == chatId).Select(entry => (entry.Key, entry.Value.At))
                .OrderBy(entry => entry.At).ThenBy(entry => entry.Key, StringComparer.Ordinal)];
    }
    public (int Done, int Known) CrawlProgress() { lock (sync) return (crawl.Values.Count(s => s.Done), Math.Max(chats.Count, crawl.Count)); }
    public void SetCrawlState(string account, GoogleCrawlState state)
    {
        lock (sync)
        {
            if (profile != account) return;
            crawl[state.ChatId] = state;
            pending.Add((account, new(Kind.CrawlState, state.ChatId, JsonSerializer.SerializeToUtf8Bytes(state))));
        }
        ScheduleFlush();
    }

    // Where a photo or video came from, and the page that reads it again: the next newer saved message of its
    // conversation is the cursor (as a history page continues from its oldest message), or the newest page when none is
    // saved. A walk of the in-memory metadata; no message is read from disk.
    public GoogleMediaSource? MediaSource(string mediaId)
    {
        lock (sync)
        {
            if (!mediaOwners.TryGetValue(mediaId, out var messageId) || !documents.TryGetValue(messageId, out var owner)) return null;
            string? newer = null;
            DateTimeOffset newerAt = default;
            foreach (var (id, doc) in documents)
                if (doc.ChatId == owner.ChatId && doc.At > owner.At && (newer is null || doc.At < newerAt || doc.At == newerAt && string.CompareOrdinal(id, newer) < 0))
                    (newer, newerAt) = (id, doc.At);
            return new(owner.ChatId, messageId, owner.At, newer, newer is null ? null : newerAt);
        }
    }

    public bool Contains(string messageId) { lock (sync) return documents.ContainsKey(messageId); }
    public GoogleChat? Chat(string id) { lock (sync) return chats.GetValueOrDefault(id); }
    public GoogleChat[] ChatsSnapshot() { lock (sync) return [.. chats.Values]; }

    // Newest first. chatId limits results to one conversation.
    public IReadOnlyList<GoogleArchiveHit> Search(string query, string? chatId = null, SearchFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            var ids = string.IsNullOrWhiteSpace(query) ? documents.Keys.ToArray() : index.Query(query, cancellationToken);
            return ids.Select(id => documents.TryGetValue(id, out var doc) && doc.Searchable &&
                    MessageSearch.MatchesDate(doc.At, filters) && (filters?.Content is not { } kind || kind == SearchContent.All || (doc.Content & 1 << (int)kind) != 0)
                    ? new GoogleArchiveHit(id, doc.ChatId, doc.At) : null)
                .OfType<GoogleArchiveHit>().Where(hit => chatId is null || hit.ChatId == chatId)
                .OrderByDescending(hit => hit.At).ThenBy(hit => hit.MessageId, StringComparer.Ordinal).ToArray();
        }
    }

    public async Task<GoogleMessage?> ReadMessageAsync(string account, string id, CancellationToken token)
    {
        lock (sync)
        {
            // Unwritten entries are newest; the disk copy may not exist yet.
            var queued = pending.LastOrDefault(p => p.Profile == account && p.Entry.Kind == Kind.Message && p.Entry.Id == id);
            if (queued.Entry is not null) return Deserialize<GoogleMessage>(queued.Entry.Payload);
        }
        try { return await store.ReadAsync(account, Kind.Message, id, token) is { } bytes ? Deserialize<GoogleMessage>(bytes) : null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // How long remembered entries gather before the delayed write. The offline checks shorten it to see more of those
    // writes interleave with their writers in less time.
    internal TimeSpan FlushDelay { get; init; } = TimeSpan.FromMilliseconds(1500);

    private void ScheduleFlush()
    {
        lock (sync)
        {
            if (flushQueued || pending.Count == 0) return;
            flushQueued = true;
        }
        _ = Task.Run(async () => { await Task.Delay(FlushDelay); await FlushCoreAsync(scheduled: true); });
    }

    // Returns once everything queued before the call is on disk.
    public Task FlushAsync() => FlushCoreAsync(scheduled: false);

    private async Task FlushCoreAsync(bool scheduled)
    {
        await writer.WaitAsync();
        try
        {
            while (true)
            {
                (string Profile, MessageArchive.Entry Entry)[] batch;
                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        if (scheduled) flushQueued = false;
                        return;
                    }
                    batch = [.. pending.Take(500)];
                    pending.RemoveRange(0, batch.Length);
                }
                foreach (var group in batch.GroupBy(p => p.Profile))
                {
                    // Last write wins within a batch, e.g. repeated status updates for one message.
                    var entries = group.Select(p => p.Entry).GroupBy(e => (e.Kind, e.Id)).Select(g => g.Last()).ToArray();
                    try { await store.UpsertAsync(group.Key, entries); }
                    catch
                    {
                        // Expendable: a failed write only means that data is fetched or indexed again later. So what it held is not taken for written, and is
                        // written when it is next remembered, unchanged or not.
                        lock (sync)
                            if (profile == group.Key)
                                foreach (var entry in entries)
                                {
                                    if (entry.Kind == Kind.Message) written.Remove(entry.Id);
                                    else if (entry.Kind == Kind.Chat) unwrittenChats.Add(entry.Id);
                                }
                    }
                }
            }
        }
        finally { writer.Release(); }
    }
}
