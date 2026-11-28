using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

// 2026-10-26: someone's first message in an SMS group opened a conversation of its own while it downloaded and moved to the
// group about a second later (GoogleDirectory.IsLeftPlaceholder), so the phone showed, and could notify for, a thread with
// only the sender that then left. A conversation never seen before whose first message is still downloading, or which opens
// with Google's "one-on-one SMS conversation created" marker, is held here until it is settled: released once a message in
// it is whole (a real new conversation: in the logs, a picture from someone new finished 1.2 s later), discarded when its
// message moves on, and released anyway after GoogleMessagesBackend.ArrivalHoldLimit. Over two days of logs every MMS
// download took 1.9 s or less. A plain text from someone new arrives whole and is never held.
internal sealed class GoogleArrivalHold(Action<RealtimeEvent> publish)
{
    private readonly object sync = new();
    private readonly Dictionary<string, (int Token, List<RealtimeEvent> Events)> held = new(StringComparer.Ordinal);
    private int tokens;

    public static bool Unsettled(GoogleMessage message) =>
        message.Status.Contains("DOWNLOADING", StringComparison.Ordinal) || message.Status.Contains("TOMBSTONE", StringComparison.Ordinal);

    // Begins holding a conversation; its token is what Expire names, so a later hold of the same conversation is not cut short.
    public int? Begin(string chatId)
    {
        lock (sync)
        {
            if (held.ContainsKey(chatId)) return null;
            var token = ++tokens;
            held[chatId] = (token, []);
            return token;
        }
    }

    public bool IsHeld(string chatId) { lock (sync) return held.ContainsKey(chatId); }

    // Published now, or kept in order behind the hold. Publishing only writes to a channel, so it happens under the lock and a
    // release cannot be overtaken by a later event of the same conversation.
    public void Deliver(string chatId, RealtimeEvent evt)
    {
        lock (sync)
        {
            if (held.TryGetValue(chatId, out var hold)) hold.Events.Add(evt);
            else publish(evt);
        }
    }

    public int Release(string chatId)
    {
        lock (sync)
        {
            if (!held.Remove(chatId, out var hold)) return -1;
            foreach (var evt in hold.Events) publish(evt);
            return hold.Events.Count;
        }
    }

    public int Expire(string chatId, int token)
    {
        lock (sync) return held.TryGetValue(chatId, out var hold) && hold.Token == token ? Release(chatId) : -1;
    }

    public int Discard(string chatId)
    {
        lock (sync) return held.Remove(chatId, out var hold) ? hold.Events.Count : -1;
    }

    public void Clear() { lock (sync) held.Clear(); }
}
