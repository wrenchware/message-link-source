using System.Text.Json;

namespace MessageLink.App.Services.Messenger;

// Where the chat list continues when more than one network pages: Google Messages' own cursor and each Google Voice
// account's page token, together in one opaque string. When no Voice account has more, the cursor is Google's own and
// unchanged, so Google Messages pages exactly as it did before Voice joined.
internal sealed record CombinedChatCursor(string? Google, IReadOnlyDictionary<string, string> Voice, int Retry = 0)
{
    private const string Prefix = "multi:";
    private const int MaxLength = 16 * 1024;

    public static bool IsCombined(string? cursor) => cursor?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    // The cursor for the page after this one.
    public static string? Next(string? google, bool googleMore, IReadOnlyDictionary<string, string> voice, int retry = 0) =>
        voice.Count == 0 && retry == 0 ? google : new CombinedChatCursor(googleMore ? google : null, voice, retry).Encode();

    public string Encode() => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Wire(Google, new(Voice), Retry)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Null when the text is not a cursor this app wrote: wrong prefix, damaged, or naming something no account could be.
    public static CombinedChatCursor? Decode(string? cursor)
    {
        if (!IsCombined(cursor) || cursor!.Length > MaxLength) return null;
        var text = cursor[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(Convert.FromBase64String(text));
            if (wire is null || wire.R is < 0 or > 1_000_000) return null;
            var voice = wire.V ?? [];
            if (voice.Any(entry => !IsAccountKey(entry.Key) || string.IsNullOrEmpty(entry.Value) || entry.Value.Length > 2000)) return null;
            return new(wire.G, voice, wire.R);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private static bool IsAccountKey(string key) => key.Length == 16 && key.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private sealed record Wire(string? G, Dictionary<string, string>? V, int R = 0);
}
