using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The keys and values of the shared rows that were files before protocol 3: the client's
/// replica and the server both write them, so they are spelled once, here. Each key is the file's own digest (<c>H(profile, id)</c>, the name the file had), so a
/// row and the file it replaces name the same thing; each value is the file's own JSON where the file was JSON, and a JSON string for a name.
/// </summary>
public static class SharedRows
{
    // As the snooze files: one record naming its conversation, in System.Text.Json's default spelling (not the protocol's camelCase), so a row and a
    // file hold the same text.
    private sealed record SnoozeEntry(int Version, string Profile, string ChatId, DateTimeOffset Until);

    /// <summary>A snooze's key: <c>snooze:</c> and the digest its <c>ChatSnoozes-v1</c> file is named by.</summary>
    public static string SnoozeKey(string profile, string chatId) => SharedCategories.KeyOf(profile, SharedCategories.Snooze, chatId);

    /// <summary>A chat preference's key: <c>chatpref:</c>, the digest its <c>ChatPreferences-v1</c> file is named by, a dot and the setting's name.</summary>
    public static string ChatPreferenceKey(string profile, string chatId, string setting) =>
        SharedCategories.KeyOf(profile, SharedCategories.ChatPreference, chatId) + "." + setting;

    /// <summary>A name's key: <c>name:</c> and the digest its <c>ContactNames-v1</c> file is named by.</summary>
    public static string ContactNameKey(string profile, string identity) => SharedCategories.KeyOf(profile, SharedCategories.ContactName, identity);

    public static byte[] SnoozeValue(ChatSnooze snooze) => JsonSerializer.SerializeToUtf8Bytes(new SnoozeEntry(1, snooze.Profile, snooze.ChatId, snooze.Until));

    /// <summary>The snooze a row holds, or null when it is not one this version reads (the store's own rule: version 1, naming a conversation).</summary>
    public static ChatSnooze? ReadSnooze(ReadOnlySpan<byte> value)
    {
        try
        {
            return JsonSerializer.Deserialize<SnoozeEntry>(value) is { Version: 1, Profile: { } profile, ChatId: { Length: > 0 } chatId } entry
                ? new ChatSnooze(profile, chatId, entry.Until) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A name as its row holds it: a JSON string.</summary>
    public static byte[] NameValue(string name) => JsonSerializer.SerializeToUtf8Bytes(name);

    /// <summary>The name a row holds, or null when it holds none this version reads.</summary>
    public static string? ReadName(ReadOnlySpan<byte> value)
    {
        try { return JsonSerializer.Deserialize<string>(value); }
        catch (JsonException) { return null; }
    }
}
