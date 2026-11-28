using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MessageLink.Core.Messaging;

namespace MessageLink.Protocol;

/// <summary>
/// One fingerprint of a run of messages, the same on every side: the server compares a service's newest page with its copy by it,
/// and a client compares its copy with the server's. It covers what makes two copies of a message differ for a person (its id, time, edit mark, removal,
/// reactions, how many attachments, and how long its words are), never the words. The Android client computes it the same way (<c>HistoryDigest.kt</c>):
/// a line per message, the lines in the ordinal order of the ids, each field separated by a tab and each line ended by a newline, SHA-256 over the UTF-8,
/// and the first 16 bytes in lower-case hex.
/// </summary>
public static class HistoryDigest
{
    /// <summary>The fingerprint of these messages, in whatever order they come.</summary>
    public static string Of(IEnumerable<ChatMessage> messages)
    {
        var lines = messages.DistinctBy(message => message.Id).Select(Line).OrderBy(line => line.Id, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var (_, line) in lines) text.Append(line).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>One message's line, for a comparison that wants to say which messages differ rather than whether any does.</summary>
    public static string Mark(ChatMessage message) => Line(message).Text;

    private static (string Id, string Text) Line(ChatMessage message)
    {
        var reactions = string.Join(",", (message.Reactions ?? []).Select(reaction => reaction.Key + ":" + reaction.ParticipantId).Order(StringComparer.Ordinal));
        var text = string.Join('\t',
            message.Id,
            message.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            message.EditedAt?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "",
            message.IsDeleted ? "1" : "0",
            message.IsHidden ? "1" : "0",
            reactions,
            (message.Attachments?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            (message.Text?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
        return (message.Id, text);
    }
}
