using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessageLink.Protocol;

/// <summary>One row of a move bundle: the key a PC filed it under, and the row's own JSON as the PC held it.</summary>
public sealed record MoveRow(string Key, JsonElement Value);

/// <summary>
/// What a PC hands a Messages Server when its choices move there: every row of the shared state it holds, filed under the key
/// it has always used (<paramref name="LocalKey"/>), which the server adopts verbatim so no row has to be renamed. It crosses as an upload, never in a frame,
/// and holds names and drafts, so it is never logged. <paramref name="Counts"/> and <paramref name="Digest"/> are of <paramref name="Rows"/>'s keys
/// (<see cref="MoveCounts"/>): the server checks them against the rows it received, and answers with the same two for the rows it now holds, so the PC can tell a
/// move that is whole from one that is not.
/// </summary>
public sealed record MoveBundle(int Version, string LocalKey, IReadOnlyList<MoveRow> Rows, IReadOnlyDictionary<string, int> Counts, string Digest)
{
    public const int CurrentVersion = 1;
    /// <summary>The type the bundle's upload is declared with.</summary>
    public const string MimeType = "application/vnd.messages.move+json";
    /// <summary>Well under the upload store's largest upload (64 MiB), so the bundle is never refused for its size after the work of making it.</summary>
    public const int MaxBytes = 48 * 1024 * 1024;

    public static MoveBundle Make(string localKey, IEnumerable<MoveRow> rows)
    {
        var list = rows.ToList();
        var keys = list.Select(row => row.Key).ToList();
        return new MoveBundle(CurrentVersion, localKey, list, MoveCounts.Of(keys), MoveCounts.DigestOf(keys));
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, ProtocolJson.Options);

    /// <summary>The bundle in <paramref name="bytes"/>, or null when they are not one this version reads.</summary>
    public static MoveBundle? Read(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize<MoveBundle>(bytes, ProtocolJson.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// How many rows each kind has, and a digest of their keys: the two things a move compares. A kind is the key's category word (<c>chatpref</c>, <c>name</c>, <c>snooze</c>,
/// <c>saved</c>, <c>draft</c>, <c>merges</c>, <c>labels</c>, <c>preferences</c>, <c>avatar-v2</c>, <c>draft-owner</c>). Keys are one-way digests already, and are
/// still never logged.
/// </summary>
public static class MoveCounts
{
    public static string CategoryOf(string key) => key.IndexOf(':') is var colon and > 0 ? key[..colon] : "";

    public static IReadOnlyDictionary<string, int> Of(IEnumerable<string> keys)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var category = CategoryOf(key);
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        return counts;
    }

    /// <summary>SHA-256 (lower-case hex) of the distinct keys in ordinal order, one per line: the same for the same keys however they came.</summary>
    public static string DigestOf(IEnumerable<string> keys)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ordered))));
    }

    /// <summary>
    /// SHA-256 (lower-case hex) of each distinct key with the SHA-256 of its value, in key order: the same for the same rows however they came, and different when any
    /// value is. <paramref name="rows"/> hold the value as its JSON text in UTF-8. A move compares it so that a server holding the right keys with other values (an
    /// older copy, another device's edit) is told from one that holds what was sent.
    /// </summary>
    public static string ValuesDigestOf(IEnumerable<(string Key, byte[] Value)> rows)
    {
        var lines = rows.DistinctBy(row => row.Key, StringComparer.Ordinal).OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => row.Key + "\n" + Convert.ToHexStringLower(SHA256.HashData(row.Value)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary><see cref="ValuesDigestOf"/> of a bundle's rows, with each value as the server stores it (the row's JSON text).</summary>
    public static string ValuesDigestOf(IEnumerable<MoveRow> rows) =>
        ValuesDigestOf(rows.Select(row => (row.Key, Encoding.UTF8.GetBytes(row.Value.GetRawText()))));

    public static bool Same(IReadOnlyDictionary<string, int>? a, IReadOnlyDictionary<string, int>? b) =>
        a is not null && b is not null && a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);
}
