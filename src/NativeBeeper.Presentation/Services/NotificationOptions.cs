using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

public enum NotificationDetail { SenderAndMessage, SenderOnly, None }

// Notification options: which services notify, each one's sound, and how much a notification shows. Local only; turning
// a service off here mutes, archives and marks nothing on it. Keys are service names ("Google Messages", "Messenger",
// "Instagram") or, for each Google Voice account, "Google Voice/" and a one-way hash of the account, so the saved file
// holds neither a phone number nor an account id.
// SenderPhoto (appended): "Show the sender's photo", off unless turned on. Windows takes a notification's picture only as a
// file, so while it is on a small copy of the picture this PC already has sits on disk as long as the notification does
// (NotificationPhotos). Never with No details.
public sealed record NotificationOptions(IReadOnlySet<string> Off, IReadOnlyDictionary<string, NotificationSound> Sounds,
    NotificationDetail Detail, bool SenderPhoto = false)
{
    public const string VoicePrefix = "Google Voice/";
    public static NotificationOptions Default { get; } = new(new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, NotificationSound>(StringComparer.Ordinal), NotificationDetail.SenderAndMessage);

    // The service a message arrived on: a merged conversation is judged by the member that received it.
    public static string ServiceKey(ChatSummary source) =>
        source.Network == "Google Voice" ? VoiceKey(source.AccountId) : source.Network;

    public static string VoiceKey(string accountId) => VoicePrefix + NotificationIdentity.Hash(accountId);

    // Only these are ever saved or loaded, which is what keeps a number or an id out of the file.
    public static bool IsValidKey(string? key) => key is "Google Messages" or "Messenger" or "Instagram" ||
        key is not null && key.Length == VoicePrefix.Length + 64 && key.StartsWith(VoicePrefix, StringComparison.Ordinal) &&
        key.AsSpan(VoicePrefix.Length).IndexOfAnyExcept("0123456789ABCDEF") < 0;

    public bool IsOn(string serviceKey) => !Off.Contains(serviceKey);

    public NotificationSound SoundFor(string serviceKey) => Sounds.TryGetValue(serviceKey, out var sound) ? sound : NotificationSound.TextMessage;

    public static string? SoundUri(NotificationSound sound) => sound switch
    {
        NotificationSound.TextMessage => "ms-winsoundevent:Notification.SMS",
        NotificationSound.InstantMessage => "ms-winsoundevent:Notification.IM",
        NotificationSound.Mail => "ms-winsoundevent:Notification.Mail",
        NotificationSound.Reminder => "ms-winsoundevent:Notification.Reminder",
        NotificationSound.WindowsDefault => "ms-winsoundevent:Notification.Default",
        _ => null,
    };

    public NotificationOptions WithService(string serviceKey, bool on)
    {
        var off = new HashSet<string>(Off, StringComparer.Ordinal);
        if (on) off.Remove(serviceKey); else off.Add(serviceKey);
        return this with { Off = off };
    }

    public NotificationOptions WithSound(string serviceKey, NotificationSound sound)
    {
        var sounds = new Dictionary<string, NotificationSound>(Sounds, StringComparer.Ordinal);
        if (sound == NotificationSound.TextMessage) sounds.Remove(serviceKey); else sounds[serviceKey] = sound;
        return this with { Sounds = sounds };
    }

    // What a notification says, by the choice made here. Sender only keeps the conversation's name; No details keeps
    // nothing but the service line under it.
    public static (string Title, string Preview) Shape(string title, string preview, NotificationDetail detail) => detail switch
    {
        NotificationDetail.SenderOnly => (title, "New message"),
        NotificationDetail.None => ("Messages", "New message"),
        _ => (title, preview),
    };

    public static string DetailWord(NotificationDetail detail) => detail switch
    {
        NotificationDetail.SenderOnly => "sender_only",
        NotificationDetail.None => "none",
        _ => "sender_and_message",
    };

    private static readonly (NotificationSound Sound, string Word)[] SoundWords =
    [
        (NotificationSound.TextMessage, "text_message"), (NotificationSound.InstantMessage, "instant_message"), (NotificationSound.Mail, "mail"),
        (NotificationSound.Reminder, "reminder"), (NotificationSound.WindowsDefault, "windows_default"), (NotificationSound.None, "none"),
    ];

    // {"off":[...],"sounds":{key:word},"detail":word,"sender_photo":bool}. Keys, words and a switch; nothing else is written.
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("off");
            foreach (var key in Off.Where(IsValidKey).Order(StringComparer.Ordinal)) writer.WriteStringValue(key);
            writer.WriteEndArray();
            writer.WriteStartObject("sounds");
            foreach (var (key, sound) in Sounds.Where(pair => IsValidKey(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(key, SoundWords.First(entry => entry.Sound == sound).Word);
            writer.WriteEndObject();
            writer.WriteString("detail", DetailWord(Detail));
            writer.WriteBoolean("sender_photo", SenderPhoto);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Unknown fields, keys and words are passed over. Text that is not a JSON object throws JsonException, and the
    // caller loads the defaults.
    public static NotificationOptions FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Notification options are not an object.");
        var off = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("off", out var offList) && offList.ValueKind == JsonValueKind.Array)
            foreach (var item in offList.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } key && IsValidKey(key)) off.Add(key);
        var sounds = new Dictionary<string, NotificationSound>(StringComparer.Ordinal);
        if (root.TryGetProperty("sounds", out var soundMap) && soundMap.ValueKind == JsonValueKind.Object)
            foreach (var entry in soundMap.EnumerateObject())
                if (IsValidKey(entry.Name) && entry.Value.ValueKind == JsonValueKind.String &&
                    SoundWords.FirstOrDefault(word => word.Word == entry.Value.GetString()) is { Word: not null } match &&
                    match.Sound != NotificationSound.TextMessage)
                    sounds[entry.Name] = match.Sound;
        var detail = NotificationDetail.SenderAndMessage;
        if (root.TryGetProperty("detail", out var word) && word.ValueKind == JsonValueKind.String)
            detail = word.GetString() switch { "sender_only" => NotificationDetail.SenderOnly, "none" => NotificationDetail.None, _ => detail };
        // Only an explicit true turns it on; anything else, or nothing (every file written before it), leaves it off.
        var photo = root.TryGetProperty("sender_photo", out var switched) && switched.ValueKind == JsonValueKind.True;
        return new(off, sounds, detail, photo);
    }
}

// Unread repeats are raised by NotificationRepeatService straight to the presenter, not through the observer, so they
// pass through here on their way: a repeat takes the service's sound and the current "Show in notifications" choice, and
// a service turned off since the first notification repeats no more. Everything else passes through untouched.
public sealed class NotificationOptionsPresenter(INotificationPresenter inner, Func<NotificationObserver?> observer) : INotificationPresenter
{
    public Task ShowMessageAsync(NotificationContent content)
    {
        if (!content.IsRepeat || observer() is not { } source) return inner.ShowMessageAsync(content);
        return source.ForRepeat(content) is { } shaped ? inner.ShowMessageAsync(shaped) : Task.CompletedTask;
    }
    public Task ShowResultAsync(NotificationRecord record, string status) => inner.ShowResultAsync(record, status);
    public Task RemoveAsync(string id) => inner.RemoveAsync(id);
}
