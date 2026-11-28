namespace MessageLink.Presentation.Services;

/// <summary>
/// Which messages a provider's helper has handed out in its current session, as far as this app has seen it: a page it
/// read, a message that came live, a conversation a catch-up read. The Instagram, Google Messages and Messenger helpers
/// resolve a picture's id only once its message has been handed out that way, and answer "unavailable" for anything
/// else, so the same answer means "not yet" before and "gone" after. Live evidence 2026-10-02: a reel card in the
/// conversation opened at launch asked while Instagram was still reconnecting, was told "unavailable" in 32 ms, and its
/// picture stayed hidden for the whole session; a gallery's sixty-six older pictures were all "unavailable" in 1-2 ms.
/// A picture is only called gone when its own message had been named here before its read began
/// (<see cref="NamedBefore"/>). Thread-safe; <see cref="Arrived"/> is raised on whichever thread named the messages.
/// </summary>
public static class MediaNews
{
    // Enough for a long session's arrivals and every page read in it; the oldest names go first.
    private const int Limit = 4096;
    private static readonly object gate = new();
    private static readonly Dictionary<string, long> named = new(StringComparer.Ordinal);
    private static readonly Queue<string> order = new();
    private static long sequence;

    /// <summary>A conversation's messages were handed out: the conversation and the messages' ids, or null for a read
    /// of the conversation that did not say which messages it held.</summary>
    public static event Action<string, IReadOnlyList<string>?>? Arrived;

    /// <summary>Taken when a picture's read begins, and given back to <see cref="NamedBefore"/> when it fails.</summary>
    public static long Sequence { get { lock (gate) return sequence; } }

    public static void Read(string chatId, IEnumerable<string>? messageIds)
    {
        if (string.IsNullOrEmpty(chatId)) return;
        var ids = messageIds?.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        lock (gate)
        {
            sequence++;
            foreach (var id in ids ?? [])
            {
                var key = Key(chatId, id);
                if (!named.ContainsKey(key)) order.Enqueue(key);
                named[key] = sequence;
            }
            while (named.Count > Limit && order.TryDequeue(out var oldest)) named.Remove(oldest);
        }
        Arrived?.Invoke(chatId, ids);
    }

    /// <summary>Whether any of these messages had been handed out by the time a read that took <paramref name="before"/>
    /// began. A name that came while the read was under way does not count: the helper may have answered first.</summary>
    public static bool NamedBefore(string chatId, IEnumerable<string> messageIds, long before)
    {
        lock (gate)
            return messageIds.Any(id => named.TryGetValue(Key(chatId, id), out var at) && at <= before);
    }

    /// <summary>A helper that connected again knows nothing it was told before: what it handed out under these
    /// conversations (all of them for null) no longer counts.</summary>
    public static void Forget(string? chatPrefix = null)
    {
        lock (gate)
        {
            sequence++;
            if (string.IsNullOrEmpty(chatPrefix)) { named.Clear(); order.Clear(); return; }
            foreach (var key in named.Keys.Where(key => key.StartsWith(chatPrefix, StringComparison.Ordinal)).ToArray()) named.Remove(key);
            var kept = order.Where(named.ContainsKey).ToArray();
            order.Clear();
            foreach (var key in kept) order.Enqueue(key);
        }
    }

    /// <summary>How many messages are remembered as handed out; for the offline checks.</summary>
    public static int Count { get { lock (gate) return named.Count; } }

    private static string Key(string chat, string message) => chat + "\n" + message;
}
