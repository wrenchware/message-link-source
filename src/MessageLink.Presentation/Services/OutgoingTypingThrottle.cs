namespace MessageLink.Presentation.Services;

// No timer resends: only fresh editing activity can refresh the remote indicator.
public sealed class OutgoingTypingThrottle
{
    private readonly Dictionary<string, DateTimeOffset> sent = [];
    public bool TryAcquire(string chatId, DateTimeOffset now)
    {
        if (sent.TryGetValue(chatId, out var previous) && now - previous < TimeSpan.FromSeconds(5)) return false;
        foreach (var key in sent.Where(p => now - p.Value > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) sent.Remove(key);
        sent[chatId] = now;
        return true;
    }
}
