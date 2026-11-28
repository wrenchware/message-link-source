namespace MessageLink.Presentation.Services;

/// <summary>
/// When each conversation's last typing ping went out from here, so a send can say how long before it the last one left
/// (2026-10-04: does a typing ping delay a send?). The measurement comes first: each provider's send line carries
/// since_typing_ms, the time from the last typing start this account sent in that conversation to the send, or none.
/// </summary>
/// <remarks>
/// Noted where every outgoing start passes (TypingRouter), after the private-reading gate, so a start that was refused
/// is not counted. Times and conversation ids only, in memory; nothing about the words. Read from the providers' threads,
/// so it locks.
/// </remarks>
public sealed class TypingPings(TimeProvider? clock = null)
{
    public static TypingPings Shared { get; } = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, long> sent = new(StringComparer.Ordinal);

    public void Sent(string chatId)
    {
        var now = clock.GetTimestamp();
        lock (sent) sent[chatId] = now;
    }

    /// <summary>Whole milliseconds since this conversation's last typing ping, or null when none went out this run.</summary>
    public long? SinceMilliseconds(string chatId)
    {
        long at;
        lock (sent) if (!sent.TryGetValue(chatId, out at)) return null;
        return (long)clock.GetElapsedTime(at).TotalMilliseconds;
    }

    /// <summary>The send line's field: since_typing_ms=&lt;n&gt;, or since_typing_ms=none.</summary>
    public string Field(string chatId) => "since_typing_ms=" + (SinceMilliseconds(chatId)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
}
