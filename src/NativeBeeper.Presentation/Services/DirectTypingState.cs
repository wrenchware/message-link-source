namespace NativeBeeper.Presentation.Services;

// Per-backend native conversation IDs only; never matches Beeper chats by title.
public sealed class DirectTypingState
{
    private readonly Dictionary<(string Chat, string User), DateTimeOffset> entries = [];
    public void Apply(string chat, string user, bool active, DateTimeOffset expires, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(user)) return;
        if (!active || expires <= now) entries.Remove((chat, user));
        else entries[(chat, user)] = expires < now.AddSeconds(15) ? expires : now.AddSeconds(15);
    }
    public IReadOnlyList<string> Active(string chat, DateTimeOffset now)
    {
        foreach (var key in entries.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) entries.Remove(key);
        return entries.Where(p => p.Key.Chat == chat).Select(p => p.Key.User).Order(StringComparer.Ordinal).ToArray();
    }
    // Their message arrived: whatever they were typing has gone. Nobody named ends everyone in the conversation.
    public void Ended(string chat, string? user)
    {
        foreach (var key in entries.Keys.Where(key => key.Chat == chat && (user is null || key.User == user)).ToArray()) entries.Remove(key);
    }
    public void Clear() => entries.Clear();
    public DateTimeOffset ExpiresAt(string chat, string user) => entries.GetValueOrDefault((chat, user));
}
