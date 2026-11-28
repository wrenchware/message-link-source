namespace MessageLink.Presentation.Services;

/// <summary>
/// When to tell the other end this account stopped typing: once, after a pause in editing. Not a fixed time after the
/// start — that sent a stop five seconds into every burst of typing however long it went on, so the other person's dots
/// vanished and came back every few seconds while the words were still being written.
/// </summary>
/// <remarks>
/// A stop is pending for a conversation from the start that went out until it is sent or superseded, and every edit in
/// the meantime pushes it back. Starts stay throttled where they were (<see cref="OutgoingTypingThrottle"/>), so no
/// more go out than before, and a stop goes out at most once per pause. Not thread-safe: the owner locks.
/// </remarks>
public sealed class OutgoingTyping(TimeSpan idle)
{
    private readonly Dictionary<string, DateTimeOffset> stops = new(StringComparer.Ordinal);

    public TimeSpan Idle => idle;

    /// <summary>A start went out. True when no stop was pending, so the caller has no timer running for it yet.</summary>
    public bool Started(string chat, DateTimeOffset now)
    {
        var fresh = !stops.ContainsKey(chat);
        stops[chat] = now + idle;
        return fresh;
    }

    /// <summary>
    /// The composer was edited. Only a conversation with a stop already pending moves: an edit is not typing that was
    /// sent, and an edit the app made itself arrives with no conversation at all.
    /// </summary>
    public void Activity(string? chat, DateTimeOffset now)
    {
        if (chat is not null && stops.ContainsKey(chat)) stops[chat] = now + idle;
    }

    /// <summary>How long until the stop is due, or null when none is pending.</summary>
    public TimeSpan? Remaining(string chat, DateTimeOffset now) => stops.TryGetValue(chat, out var due) ? due - now : null;

    /// <summary>Takes the stop if it is due now; only one caller ever gets it.</summary>
    public bool TakeDue(string chat, DateTimeOffset now)
    {
        if (!stops.TryGetValue(chat, out var due) || due > now) return false;
        stops.Remove(chat);
        return true;
    }

    /// <summary>Takes the stop whenever it was due, because it is being sent now: a message went, or typing ended.</summary>
    public bool Take(string chat) => stops.Remove(chat);

    public void Clear() => stops.Clear();
}
