using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// Session-only index for services without local archive search. Observes reads the app already makes, and the copies of
// recent conversations this PC already saved (SavedSearchSeeding); searching never starts a history crawl, downloads
// media or writes message bodies to disk.
public sealed class LocalSearchCatalog
{
    private readonly object sync = new();
    private readonly Dictionary<string, ChatSummary> chats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatMessage> messages = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = [];
    private readonly Dictionary<string, LinkedListNode<string>> nodes = new(StringComparer.Ordinal);
    private readonly TextSearchIndex index = new();
    private string? profile;
    private int generation;
    private const int Capacity = 20000;
    public int MessageCount { get { lock (sync) return messages.Count; } }

    public int SetProfile(string? value)
    {
        lock (sync)
        {
            if (profile == value) return generation;
            profile = value; generation++; chats.Clear(); messages.Clear(); recency.Clear(); nodes.Clear(); index.Clear(); seeded.Clear();
            return generation;
        }
    }

    // The conversations the list has shown, as it shows them; raised outside the lock, on whatever thread observed them.
    // Saved copies are put in from it (SavedSearchSeeding).
    public event Action<IReadOnlyList<ChatSummary>>? ChatsObserved;

    public void Observe(IEnumerable<ChatSummary> incoming, int expected)
    {
        List<ChatSummary>? seen = null;
        lock (sync)
        {
            if (expected != generation) return;
            foreach (var chat in incoming)
            {
                if (MergedConversations.IsMerged(chat.Id)) continue;
                if (chat.IsSpam) { RemoveChat(chat.Id); continue; }
                chats[chat.Id] = chat;
                (seen ??= []).Add(chat);
                // Voice call previews are the complete event; ordinary previews may have invented ids.
                if (chat.IsCallHistory && chat.Preview is { } call) Observe([call], expected);
            }
        }
        if (seen is not null) ChatsObserved?.Invoke(seen);
    }

    // ---- Saved copies (SavedSearchSeeding) ----

    // The conversations whose saved copy has been put in since their service last connected, each with the token its rows
    // are accepted under. A reconnect clears that service's rows here, these with them, so its copies go in again after it.
    private readonly Dictionary<string, long> seeded = new(StringComparer.Ordinal);
    private long seeds;
    public const int SeededChatsLimit = 60;
    // Set where saved copies are put in, so the search scope line can say so (UniversalSearchBackend).
    public bool HoldsSavedCopies { get; set; }

    /// <summary>Up to <paramref name="limit"/> of these conversations not yet put in, in the order given, each marked as
    /// taken; never more than <see cref="SeededChatsLimit"/> at once.</summary>
    public IReadOnlyList<(string ChatId, long Token)> TakeUnseeded(IEnumerable<string> chatIds, int limit)
    {
        lock (sync)
        {
            var taken = new List<(string, long)>();
            foreach (var id in chatIds)
            {
                if (taken.Count >= limit || seeded.Count >= SeededChatsLimit) break;
                if (seeded.ContainsKey(id)) continue;
                seeded[id] = ++seeds;
                taken.Add((id, seeds));
            }
            return taken;
        }
    }

    /// <summary>Whether rows taken under this token would still be accepted: no reconnect, deletion or profile change since.</summary>
    public bool StillSeeding(string chatId, long token)
    {
        lock (sync) return seeded.TryGetValue(chatId, out var current) && current == token;
    }

    /// <summary>
    /// A saved copy's rows, behind everything this session loaded: a row the session holds stays as it has it, and saved
    /// rows are the first to make room, never the cause of a loaded one going. Returns how many went in.
    /// </summary>
    public int Seed(string chatId, long token, IEnumerable<ChatMessage> saved)
    {
        lock (sync)
        {
            if (!seeded.TryGetValue(chatId, out var current) || current != token) return 0;
            var added = 0;
            foreach (var message in saved)
            {
                if (messages.Count >= Capacity) break;
                if (message.ChatId != chatId || message.IsHidden || message.IsDeleted || message.ChatId.StartsWith("google-direct:", StringComparison.Ordinal)) continue;
                var key = MessageIdentity.Key(message);
                if (messages.ContainsKey(key)) continue;
                messages[key] = message;
                index.Set(key, MessageSearch.SearchableText(message));
                nodes[key] = recency.AddFirst(key);
                added++;
            }
            return added;
        }
    }

    public void Observe(IEnumerable<ChatMessage> incoming, int expected)
    {
        lock (sync)
        {
            if (expected != generation) return;
            foreach (var message in incoming)
            {
                // Google has the full protected archive and its own incremental index.
                if (message.ChatId.StartsWith("google-direct:", StringComparison.Ordinal)) continue;
                var key = MessageIdentity.Key(message);
                if (message.IsHidden || message.IsDeleted) { RemoveMessage(key); continue; }
                if (messages.TryGetValue(key, out var previous) && previous == message) continue;
                messages[key] = message;
                index.Set(key, MessageSearch.SearchableText(message));
                if (nodes.Remove(key, out var node)) recency.Remove(node);
                nodes[key] = recency.AddLast(key);
            }
            while (messages.Count > Capacity) RemoveMessage(recency.First!.Value);
        }
    }

    // Whether this session has loaded that message (MessageIdentity.Key), matching a search or not.
    public bool Holds(string key)
    {
        lock (sync) return messages.ContainsKey(key);
    }

    // The conversation as the list last showed it, with this PC's own names and preferences applied; null when the
    // list has not shown it this session.
    public ChatSummary? Chat(string id)
    {
        lock (sync) return chats.GetValueOrDefault(id);
    }

    public IReadOnlyList<SearchHit> Search(SearchRequest request, CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (request.Kind != SearchKind.Messages)
                return chats.Values.Where(c => MessageSearch.MatchesChat(c, request) &&
                    (request.Kind == SearchKind.Participants
                        ? c.Participants.Any(p => p.IsSelf != true && MessageSearch.WordsMatch(p.DisplayName, request.Query))
                        : MessageSearch.WordsMatch(c.Title + " " + c.Network, request.Query)))
                    .OrderByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal)
                    .Select(c => new SearchHit(c, null)).ToArray();
            var candidates = string.IsNullOrWhiteSpace(request.Query) ? messages.Keys.ToArray() : index.Query(request.Query, token);
            var found = new List<SearchHit>();
            foreach (var key in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (messages.TryGetValue(key, out var message) && chats.TryGetValue(message.ChatId, out var chat) &&
                    MessageSearch.Matches(chat, message, request, matchWords: false)) found.Add(new(chat, message));
            }
            return found.OrderByDescending(h => h.Message!.Timestamp).ThenBy(h => h.Chat.Id, StringComparer.Ordinal)
                .ThenBy(h => h.Message!.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public void Invalidate(RealtimeEvent change)
    {
        // The client's link to its host dropped, not a provider's to its service (RealtimeEvent.ClientLink): the host and its
        // accounts ran on and the resumed feed replays what was missed, so nothing here can be another account's. It cleared
        // the whole catalog, again every few seconds while the server could not be reached.
        if (change.Kind == RealtimeKind.Disconnected && change.Scope is null && change.ClientLink == true) return;
        lock (sync)
        {
            // A reconnect can be a different account behind a fixed provider id. Rebuild that service from
            // fresh reads; a cached result must never survive sign-out into another account's session. A resync for everything
            // is the host saying the feed could not resume (it restarted, or this reader fell too far behind): what it missed,
            // a sign-out or an unsend among it, never reaches this catalog, so it goes as the link's drop once made it go.
            if (change.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired ||
                change.Kind == RealtimeKind.ResyncRequired && change.Scope is null)
            {
                generation++;
                // Only the summaries here: the messages go in one pass below. RemoveChat walked every message once per
                // conversation, which is millions of checks under this lock on each reconnect, holding up the
                // Connected event itself and every search meanwhile.
                foreach (var id in chats.Keys.Where(id => change.Scope is null || id.StartsWith(change.Scope, StringComparison.Ordinal)).ToArray()) chats.Remove(id);
                // A message read can finish before its conversation summary, so this removes those unbound rows as well.
                foreach (var key in messages.Where(pair => change.Scope is null || pair.Value.ChatId.StartsWith(change.Scope, StringComparison.Ordinal))
                             .Select(pair => pair.Key).ToArray()) RemoveMessage(key);
                // Their saved copies went with them, and go in again once the list shows them after this.
                foreach (var id in seeded.Keys.Where(id => change.Scope is null || id.StartsWith(change.Scope, StringComparison.Ordinal)).ToArray()) seeded.Remove(id);
            }
            else if (change.Kind == RealtimeKind.ChatDeleted)
            {
                foreach (var deletedId in (change.Ids ?? []).Concat(change.ChatId is { } singleId ? [singleId] : [])) { RemoveChat(deletedId); seeded.Remove(deletedId); }
            }
            // A receipt leaves the text as it was, so the message stays findable. Dropping it until the conversation was
            // read again made sent messages vanish from search once somebody read them.
            else if (change.Kind is RealtimeKind.MessageDeleted or RealtimeKind.MessageChanged && !change.ReceiptOnly && change.ChatId is { } chatId)
            {
                // The ordinary refresh replaces edits/new arrivals. Until then, omit stale message text.
                foreach (var id in change.Ids ?? []) RemoveMessage(MessageIdentity.Key(chatId, id));
            }
        }
    }
    private void RemoveChat(string id)
    {
        chats.Remove(id);
        foreach (var key in messages.Where(pair => pair.Value.ChatId == id).Select(pair => pair.Key).ToArray()) RemoveMessage(key);
    }
    private void RemoveMessage(string key)
    {
        messages.Remove(key); index.Remove(key);
        if (nodes.Remove(key, out var node)) recency.Remove(node);
    }
}

public sealed class LocalSearchChatReader(IChatReader source, LocalSearchCatalog catalog, IAccessTokenStore tokens) : IChatReader, ILinkPreviewLoader
{
    private async Task<int> GenerationAsync(CancellationToken token) => catalog.SetProfile(await NotificationIdentity.ProfileAsync(tokens, token));
    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var generation = await GenerationAsync(cancellationToken);
        var page = await source.GetChatsAsync(olderThan, cancellationToken);
        if (generation == await GenerationAsync(cancellationToken))
            await Task.Run(() => catalog.Observe(page.Items, generation), cancellationToken);
        return page;
    }
    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var generation = await GenerationAsync(cancellationToken);
        var chat = await source.GetChatAsync(chatId, cancellationToken);
        if (generation == await GenerationAsync(cancellationToken))
            await Task.Run(() => catalog.Observe([chat], generation), cancellationToken);
        return chat;
    }
    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var generation = await GenerationAsync(cancellationToken);
        var page = await source.GetMessagesAsync(chatId, olderThan, cancellationToken);
        if (generation == await GenerationAsync(cancellationToken))
            await Task.Run(() => catalog.Observe(page.Items, generation), cancellationToken);
        return page;
    }
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        source is ILinkPreviewLoader previews ? previews.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : Task.CompletedTask;
}

public sealed class SearchObservingRealtimeFeed(IRealtimeFeed source, LocalSearchCatalog catalog) : IRealtimeFeed
{
    public Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken) =>
        source.RunAsync((change, token) => { catalog.Invalidate(change); return receive(change, token); }, cancellationToken);
}
