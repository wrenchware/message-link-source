using System.Collections.Concurrent;
using MessageLink.Core.Messaging;

namespace MessageLink.App.Services.GoogleVoice;

// Which conversation-list reads may fill the Voice summary cache, and which callers may share one.
//
// One incoming text makes several callers miss the cache at once: the notification worker, the chat list and an open
// conversation. They share one read of that account's list rather than making one each. A read that began before a
// change is never shared with a caller reacting to that change, and never puts a summary that change made stale back in
// the cache. Only the changed conversations are left out: the rest of what it read is still current, and the caller still
// gets its answer from the read itself.
internal sealed class GoogleVoiceChatReads(ConcurrentDictionary<string, ChatSummary> chats)
{
    internal sealed class Read(long since)
    {
        public long Since { get; } = since;
        internal int joined;
        internal Task<ChatSummary[]>? result;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Read> running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> accountChanged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> invalidatedAt = new(StringComparer.Ordinal);
    private long changes;

    // The moment a read that is not shared begins, for Remember.
    public long Begin() { lock (gate) return changes; }

    // Joins the account's running read, unless the account changed after it began; otherwise starts one. The read runs
    // on its own, so a caller that gives up only stops waiting for it.
    public Task<ChatSummary[]> Share(string accountKey, Func<Read, Task<ChatSummary[]>> read)
    {
        lock (gate)
        {
            if (running.TryGetValue(accountKey, out var current) && accountChanged.GetValueOrDefault(accountKey) <= current.Since)
            {
                current.joined++;
                return current.result!;
            }
            var started = new Read(changes);
            running[accountKey] = started;
            return started.result = Task.Run(() => read(started));
        }
    }

    // Ends a shared read, so later callers start their own, and says how many callers joined it.
    public int End(string accountKey, Read read)
    {
        lock (gate)
        {
            if (running.GetValueOrDefault(accountKey) == read) running.Remove(accountKey);
            return read.joined;
        }
    }

    // Caches what a read found, except conversations that changed after it began. Returns how many were left out.
    public int Remember(IEnumerable<ChatSummary> found, long since)
    {
        var stale = 0;
        lock (gate)
            foreach (var chat in found)
                if (invalidatedAt.GetValueOrDefault(chat.Id) > since) stale++;
                else chats[chat.Id] = chat;
        return stale;
    }

    // Drops the summaries of conversations that changed. Called before the change is announced, so whoever reacts to it
    // reads afresh.
    public void Invalidate(string accountKey, IEnumerable<string> ids)
    {
        lock (gate)
        {
            accountChanged[accountKey] = ++changes;
            foreach (var id in ids)
            {
                invalidatedAt[id] = changes;
                chats.TryRemove(id, out _);
            }
        }
    }
}
