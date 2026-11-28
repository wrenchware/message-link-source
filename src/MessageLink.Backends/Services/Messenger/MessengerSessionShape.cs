using System.Text.Json;

namespace MessageLink.App.Services.Messenger;

/// <summary>What a saved Messenger session looks like from outside, read without keeping anything.</summary>
internal static class MessengerSessionShape
{
    /// <summary>The Facebook id in a Messenger session (<c>{"cookies": {"c_user": ...}}</c>), or null when it names none. Never logged.</summary>
    public static string? ViewerOf(ReadOnlyMemory<byte> session)
    {
        try
        {
            using var document = JsonDocument.Parse(session);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Object
                || !cookies.TryGetProperty("c_user", out var user) || user.ValueKind != JsonValueKind.String) return null;
            var id = user.GetString();
            return id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) ? id : null;
        }
        catch (JsonException) { return null; }
    }
}
