using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Storage;
using Position = NativeBeeper.Protocol.HistoryPosition;

namespace NativeBeeper.Host;

/// <summary>The whole history's small shared parts: how a message is kept as a row and read back, ordering, and what its log lines may say.</summary>
internal static class HistoryParts
{
    // Under the store's limit for one row, with room for its seal.
    private const int MaxRowBytes = 4 * 1024 * 1024 - 1024;

    public static byte[] Bytes(ChatMessage message)
    {
        // Who has seen it in one order, so the same message read twice is the same bytes and takes no change number.
        var canonical = message.Delivery.SeenBy.Count > 1
            ? message with { Delivery = message.Delivery with { SeenBy = new SortedDictionary<string, bool?>(message.Delivery.SeenBy.ToDictionary(), StringComparer.Ordinal) } }
            : message;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, ProtocolJson.Options);
        // Past what a row holds (megabytes of text): kept with its words cut short rather than left out, which would be a hole in the middle of the copy.
        return bytes.Length <= MaxRowBytes ? bytes
            : JsonSerializer.SerializeToUtf8Bytes(canonical with { Text = canonical.Text?[..Math.Min(canonical.Text.Length, 65_536)] }, ProtocolJson.Options);
    }

    public static ChatMessage? Message(HistoryRow row)
    {
        if (row.Removed) return null;
        try { return JsonSerializer.Deserialize<ChatMessage>(row.Payload, ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    public static List<ChatMessage> Sorted(IEnumerable<ChatMessage> messages) =>
        messages.DistinctBy(message => message.Id).OrderBy(message => Position.Of(message)).ToList();

    public static Position? Max(Position? a, Position b) => a is { } known && known.CompareTo(b) > 0 ? known : b;
    public static Position? Max(Position? a, Position? b) => b is { } other ? Max(a, other) : a;

    /// <summary>
    /// The service a conversation's background reads are paced by and its mirror check is reported under: the prefix of the service that owns it without its
    /// colon (an added account's ids go with their service's), or for an id no service owns, the text before its first colon as before.
    /// </summary>
    public static string Service(string chatId) =>
        ServiceTags.Of(chatId) is { } tag ? tag.Prefix.TrimEnd(':') : chatId.IndexOf(':') is > 0 and var colon ? chatId[..colon] : chatId;

    public static string Tag(string chatId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + chatId)).AsSpan(0, 3)).ToLowerInvariant();

    public static long Ms(long clock) => (long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
}
