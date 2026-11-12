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
// SenderPhoto (appended): "Show the sender's photo", on unless turned off (owner, 2026-11-12). Windows takes a notification's picture only as a
// file, so while it is on a small copy of the picture this PC already has sits on disk as long as the notification does
// (NotificationPhotos). Never with No details.
public sealed record NotificationOptions(IReadOnlySet<string> Off, IReadOnlyDictionary<string, NotificationSound> Sounds,
    NotificationDetail Detail, bool SenderPhoto = true, IReadOnlyDictionary<string, string>? CustomSounds = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoCustomSounds = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> Customs => CustomSounds ?? NoCustomSounds;
    public IEnumerable<string> CustomSoundFiles => Customs.Values;

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

    public const NotificationSound DefaultSound = NotificationSound.Pulse;

    public NotificationSound SoundFor(string serviceKey) => Sounds.TryGetValue(serviceKey, out var sound) ? sound : DefaultSound;

    public string? CustomSoundFor(string serviceKey) => SoundFor(serviceKey) == NotificationSound.Custom && Customs.TryGetValue(serviceKey, out var file) ? file : null;

    // A file name alone, so a saved value can never reach outside the folder the app keeps these in.
    public static bool IsValidCustomSound(string? file) => file is { Length: > 4 and <= 100 } && file.EndsWith(".wav", StringComparison.Ordinal) && file.IndexOfAny(['/', '\\', ':', (char)0]) < 0;

    public static string? SoundUri(NotificationSound sound) => sound switch
    {
        NotificationSound.TextMessage => "ms-winsoundevent:Notification.SMS",
        NotificationSound.Mail => "ms-winsoundevent:Notification.Mail",
        NotificationSound.Reminder => "ms-winsoundevent:Notification.Reminder",
        NotificationSound.WindowsDefault => "ms-winsoundevent:Notification.Default",
        _ => null,
    };

    // A sound with no Windows event: its file in Windows' Media folder, which a notification can't name, so the app plays it.
    public static string? SoundFile(NotificationSound sound) => sound switch
    {
        NotificationSound.Balloon => "Windows Balloon.wav",
        NotificationSound.Chimes => "chimes.wav",
        NotificationSound.Chord => "chord.wav",
        NotificationSound.Ding => "ding.wav",
        NotificationSound.Notify => "Windows Notify.wav",
        NotificationSound.Nudge => "Windows Message Nudge.wav",
        NotificationSound.Proximity => "Windows Proximity Notification.wav",
        NotificationSound.Ring => "Windows Ringin.wav",
        NotificationSound.Tada => "tada.wav",
        _ => null,
    };

    // One of Message Link's own sounds: its file in the app's Assets\Sounds (tools\Make-NotificationSounds.ps1).
    public static string? AppSoundFile(NotificationSound sound) => sound switch
    {
        NotificationSound.Wake => "wake.wav",
        NotificationSound.Acknowledge => "acknowledge.wav",
        NotificationSound.Signal => "signal.wav",
        NotificationSound.Dock => "dock.wav",
        NotificationSound.Transmission => "transmission.wav",
        NotificationSound.Swell => "swell.wav",
        NotificationSound.Descent => "descent.wav",
        NotificationSound.Resonance => "resonance.wav",
        NotificationSound.Pulse => "pulse.wav",
        NotificationSound.Blip => "blip.wav",
        NotificationSound.Beacon => "beacon.wav",
        NotificationSound.Glow => "glow.wav",
        NotificationSound.Hum => "hum.wav",
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
        if (sound == DefaultSound) sounds.Remove(serviceKey); else sounds[serviceKey] = sound;
        var custom = new Dictionary<string, string>(Customs, StringComparer.Ordinal);
        custom.Remove(serviceKey);
        return this with { Sounds = sounds, CustomSounds = custom };
    }

    public NotificationOptions WithCustomSound(string serviceKey, string file)
    {
        var sounds = new Dictionary<string, NotificationSound>(Sounds, StringComparer.Ordinal) { [serviceKey] = NotificationSound.Custom };
        var custom = new Dictionary<string, string>(Customs, StringComparer.Ordinal) { [serviceKey] = file };
        return this with { Sounds = sounds, CustomSounds = custom };
    }

    // What a notification says, by the choice made here. Sender only keeps the conversation's name; No details keeps
    // nothing but the service line under it.
    public static (string Title, string Preview) Shape(string title, string preview, NotificationDetail detail) => detail switch
    {
        NotificationDetail.SenderOnly => (title, "New message"),
        NotificationDetail.None => ("Message Link", "New message"),
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
        (NotificationSound.TextMessage, "text_message"), (NotificationSound.Mail, "mail"),
        (NotificationSound.Reminder, "reminder"), (NotificationSound.WindowsDefault, "windows_default"), (NotificationSound.None, "none"),
        (NotificationSound.Balloon, "balloon"), (NotificationSound.Chimes, "chimes"), (NotificationSound.Chord, "chord"), (NotificationSound.Ding, "ding"),
        (NotificationSound.Notify, "notify"), (NotificationSound.Nudge, "nudge"), (NotificationSound.Proximity, "proximity"), (NotificationSound.Ring, "ring"), (NotificationSound.Tada, "tada"),
        (NotificationSound.Custom, "custom"),
        (NotificationSound.Wake, "wake"), (NotificationSound.Acknowledge, "acknowledge"), (NotificationSound.Signal, "signal"), (NotificationSound.Dock, "dock"),
        (NotificationSound.Transmission, "transmission"), (NotificationSound.Swell, "swell"), (NotificationSound.Descent, "descent"), (NotificationSound.Resonance, "resonance"),
        (NotificationSound.Pulse, "pulse"), (NotificationSound.Blip, "blip"), (NotificationSound.Beacon, "beacon"), (NotificationSound.Glow, "glow"), (NotificationSound.Hum, "hum"),
    ];

    // {"off":[...],"sounds":{key:word},"custom_sounds":{key:file},"detail":word,"sender_photo":bool}. Keys, words, a switch and the
    // names of sound files the person chose; nothing else is written.
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
            writer.WriteStartObject("custom_sounds");
            foreach (var (key, file) in Customs.Where(pair => IsValidKey(pair.Key) && IsValidCustomSound(pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(key, file);
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
                    match.Sound != DefaultSound)
                    sounds[entry.Name] = match.Sound;
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("custom_sounds", out var customMap) && customMap.ValueKind == JsonValueKind.Object)
            foreach (var chosen in customMap.EnumerateObject())
                if (sounds.GetValueOrDefault(chosen.Name) == NotificationSound.Custom && chosen.Value.ValueKind == JsonValueKind.String &&
                    chosen.Value.GetString() is { } file && IsValidCustomSound(file))
                    custom[chosen.Name] = file;
        // A custom sound with no usable file is no choice at all.
        foreach (var unfiled in sounds.Where(pair => pair.Value == NotificationSound.Custom && !custom.ContainsKey(pair.Key)).Select(pair => pair.Key).ToList())
            sounds.Remove(unfiled);
        var detail = NotificationDetail.SenderAndMessage;
        if (root.TryGetProperty("detail", out var word) && word.ValueKind == JsonValueKind.String)
            detail = word.GetString() switch { "sender_only" => NotificationDetail.SenderOnly, "none" => NotificationDetail.None, _ => detail };
        // Only an explicit false turns it off; anything else, or nothing (a file written before it), leaves it on, the default.
        var photo = !(root.TryGetProperty("sender_photo", out var switched) && switched.ValueKind == JsonValueKind.False);
        return new(off, sounds, detail, photo, custom);
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
