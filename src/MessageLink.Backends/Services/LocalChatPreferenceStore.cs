using System.Text.Json;
using System.Text.Json.Serialization;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
namespace MessageLink.App.Services;
// Each file contains only a version, a boolean and, for a timed mute or an archive kept on this PC, one moment in UTC
// milliseconds; no token, contact ID or message content.
public sealed class LocalChatPreferenceStore : ILocalChatPreferences
{
    private readonly string directory;
    public static string DefaultDirectory => MessageLink.App.Services.AppPaths.Combine("ChatPreferences-v1");
    public LocalChatPreferenceStore() : this(DefaultDirectory) { }
    public LocalChatPreferenceStore(string directory) => this.directory = directory;
    private string PathFor(string profile, string id, ChatSetting setting) => Path.Combine(directory, NotificationIdentity.Hash(profile + "\n" + id) + "." + setting + ".json");
    // Still version 1: At and Until are added fields, so an older file reads as before and an older build reads a newer
    // file by ignoring them. A timed mute then reads there as a mute until turned off, which is the safe direction.
    // Internal, with Writing, Local, Read, Decode and Combine: a client of a Messages Server keeps the same entries as rows of its replica
    // (ReplicaChatPreferences, SWITCHBOARD.md stage 6 slice 3), which must read and write exactly what a file holds.
    internal sealed record Entry(int Version, bool Value, long? At = null, long? Until = null);
    // A permanent setting's file stays exactly {"Version":1,"Value":true}, as before.
    internal static readonly JsonSerializerOptions Writing = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly ChatSetting[] Local = [ChatSetting.Pinned, ChatSetting.Muted, ChatSetting.MarkedUnread, ChatSetting.Archived];
    public async Task<LocalChatPreferences> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
    {
        var pinned = await ReadAsync(PathFor(profile, chatId, ChatSetting.Pinned), cancellationToken);
        var muted = await ReadAsync(PathFor(profile, chatId, ChatSetting.Muted), cancellationToken);
        var unread = await ReadAsync(PathFor(profile, chatId, ChatSetting.MarkedUnread), cancellationToken);
        var archived = await ReadAsync(PathFor(profile, chatId, ChatSetting.Archived), cancellationToken);
        return Combine(pinned, muted, unread, archived);
    }
    internal static LocalChatPreferences Combine(Read pinned, Read muted, Read unread, Read archived) =>
        new(pinned.On, muted.On, unread.On, muted.On ? muted.Until : null,
            // An archive saved without its moment (none is written that way) counts from when its file was written.
            archived.On ? archived.At ?? archived.Written : null,
            ServiceMuteSetAside: muted.On ? null : muted.SetAside);
    internal readonly record struct Read(bool On, DateTimeOffset? At = null, DateTimeOffset? Until = null, DateTimeOffset? Written = null,
        long? SetAside = null);
    // A file that cannot be read counts as the setting being off, as an unreadable snooze or name already does. It threw,
    // and one pin left full of zeros by a power cut failed every load of the conversation list and every notification
    // for that conversation, with no way to find the file behind a digest name. Off is the safe reading: a conversation
    // that notifies when it should have been muted is noticed and fixed; one silently muted is not. The first one a run
    // meets is logged; a list load reads every file again, so each one is not.
    public static int Unreadable => Volatile.Read(ref unreadable);
    private static int unreadable;
    private static async Task<Read> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var info = new FileInfo(path);
            if (info.Length > 1024) throw new InvalidDataException("Invalid chat preference.");
            return Decode(JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, token)), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Increment(ref unreadable) == 1) AppLog.Write($"chat preference unreadable read_as=off error={ex.GetType().Name}");
            return default;
        }
    }
    // One entry as read; `written` is when it was written, for an archive saved without its moment (a file's own time; a row has none). Throws for an
    // entry this version cannot read, which the caller reads as off.
    internal static Read Decode(Entry? entry, DateTimeOffset? written)
    {
        if (entry?.Version != 1) throw new InvalidDataException("Unsupported chat preference version.");
        // Unmuted here over the service's own mute: Value false, and Until naming the service mute it set aside
        // (ServiceMute.Key: its end, or -1 for a mute until turned off). Nothing else is written with Value false.
        if (!entry.Value) return entry.Until is { } setAside ? new(false, SetAside: setAside) : default;
        return new(true, Moment(entry.At), Moment(entry.Until), written);
    }
    private static DateTimeOffset? Moment(long? milliseconds) => milliseconds switch
    {
        null => null,
        // Outside what a moment can be is a damaged file, read as off like any other.
        < -62135596800000 or > 253402300799999 => throw new InvalidDataException("Invalid chat preference moment."),
        { } value => DateTimeOffset.FromUnixTimeMilliseconds(value),
    };
    // Removes the pin, mute, marked-unread and archive files of these conversations and returns how many there were. The
    // folder is listed once and the names compared, so a long sweep of ids that were never set touches the disk only for
    // real files. A timed mute is the same file as any other mute, so it goes too.
    public int Forget(string profile, IEnumerable<string> chatIds)
    {
        if (!Directory.Exists(directory)) return 0;
        var present = Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var id in chatIds)
            foreach (var setting in Local)
            {
                var path = PathFor(profile, id, setting);
                if (!present.Contains(Path.GetFileName(path))) continue;
                try { File.Delete(path); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        return removed;
    }
    // Archived is local only for a conversation whose service cannot archive from here (Instagram); the reader applies
    // it to those alone, and a file for any other conversation is ignored there.
    public Task SetAsync(string profile, string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
    {
        if (!Local.Contains(setting)) throw new ArgumentException("Only pin, mute, marked unread and archive on this PC are local settings.");
        return setting switch
        {
            ChatSetting.Muted => SetMutedAsync(profile, chatId, value, null, cancellationToken),
            // Taken off by removing the file, so an archive lifted by an arrival leaves nothing a later read could
            // mistake for a newer one.
            ChatSetting.Archived when !value => RemoveAsync(PathFor(profile, chatId, setting)),
            ChatSetting.Archived => WriteAsync(PathFor(profile, chatId, setting), new Entry(1, true, At: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken),
            _ => WriteAsync(PathFor(profile, chatId, setting), new Entry(1, value), cancellationToken),
        };
    }
    // Unmuted, the file goes: a lapsed timed mute leaves nothing behind once it is cleared.
    public Task SetMutedAsync(string profile, string chatId, bool muted, DateTimeOffset? until, CancellationToken cancellationToken = default)
        => muted
            ? WriteAsync(PathFor(profile, chatId, ChatSetting.Muted), new Entry(1, true, Until: until?.ToUnixTimeMilliseconds()), cancellationToken)
            : RemoveAsync(PathFor(profile, chatId, ChatSetting.Muted));
    // The same file as the mute it replaces, so muting here again simply writes over it; an older build reads it as no
    // mute at all, which is what it shows. The backup carries it as it carries any mute file (Version, Value, Until).
    public Task SetAsideServiceMuteAsync(string profile, string chatId, ServiceMute serviceMute, CancellationToken cancellationToken = default)
        => WriteAsync(PathFor(profile, chatId, ChatSetting.Muted), new Entry(1, false, Until: serviceMute.Key), cancellationToken);
    private static Task RemoveAsync(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
    private async Task WriteAsync(string path, Entry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // On the disk before the rename, so a power cut cannot leave the renamed file full of zeros.
            await MessageLink.Storage.DurableFile.WriteAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entry, Writing), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
