using System.Collections.Concurrent;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.App.Services;

/// <summary>
/// Where the stores that were files draw from in a client of a Messages Server (<c>local</c> and <c>remote</c>): the rows
/// of the replica once its server speaks <see cref="ProtocolVersion.SharedPreferences"/>, and otherwise the files exactly as before (a server of an older
/// version keeps no such rows, and nothing kept there is lost). The in-process app and the loopback never make one: they keep their files.
/// </summary>
/// <remarks>
/// Decided per call from <see cref="ReplicatedStateStore.ServerProtocolAsync"/>, which is the connection's version while there is one and the last version
/// heard otherwise, so a launch draws from where the last connection did. The rows of a client that has never met such a server start empty: what this PC kept
/// in its files moves with the migration, not here. A change of source is logged once: <c>shared preferences source=rows|files host_protocol=</c>.
/// </remarks>
internal sealed class ReplicaPreferenceSource(ReplicatedStateStore replica, Action<string>? log = null)
{
    private int last = -1;

    public ReplicatedStateStore Replica => replica;

    public async ValueTask<bool> RowsAsync(CancellationToken cancellationToken = default)
    {
        var version = await replica.ServerProtocolAsync(cancellationToken).ConfigureAwait(false);
        var rows = version >= ProtocolVersion.SharedPreferences;
        if (Interlocked.Exchange(ref last, rows ? 1 : 0) != (rows ? 1 : 0)) log?.Invoke($"shared preferences source={(rows ? "rows" : "files")} host_protocol={version}");
        return rows;
    }
}

/// <summary>
/// One category of the replica (<c>chatpref:</c>, <c>name:</c> or <c>snooze:</c>), held in memory: read whole once, then kept by this PC's own writes and by
/// the replica's <see cref="ReplicatedStateStore.RemoteChanged"/>. The list asks for every conversation's pin, mute, snooze and names on every draw (thousands
/// of reads an evening), which the files answered with a look at the folder; a query each would open the replica's file each time.
/// </summary>
internal sealed class ReplicaRows : IDisposable
{
    private readonly ReplicatedStateStore replica;
    private readonly string prefix;
    // Every change and the first load, one at a time, so a remote change read back can never be overwritten by an older load or write.
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile ConcurrentDictionary<string, byte[]>? rows;

    public ReplicaRows(ReplicatedStateStore replica, string category)
    {
        this.replica = replica;
        prefix = category + ":";
        replica.RemoteChanged += OnRemoteChanged;
    }

    /// <summary>Keys of this category the server changed, once the rows here hold them, with when what changed them arrived (SharedListChanges tells the list).</summary>
    public event EventHandler<SharedRowsChange>? Changed;

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        (await LoadedAsync(cancellationToken).ConfigureAwait(false)).TryGetValue(key, out var value) ? value : null;

    public async ValueTask<IReadOnlyList<KeyValuePair<string, byte[]>>> AllAsync(CancellationToken cancellationToken = default) =>
        [.. await LoadedAsync(cancellationToken).ConfigureAwait(false)];

    /// <summary>Writes a row into the replica (which sends it on), or deletes it when <paramref name="value"/> is null, and then holds it here.</summary>
    public async Task WriteAsync(string key, byte[]? value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var held = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (value is null) await replica.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            else await replica.WriteAsync(key, value, cancellationToken).ConfigureAwait(false);
            if (value is null) held.TryRemove(key, out _);
            else held[key] = value;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<ConcurrentDictionary<string, byte[]>> LoadedAsync(CancellationToken cancellationToken)
    {
        if (rows is { } held) return held;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Under the gate.
    private async Task<ConcurrentDictionary<string, byte[]>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (rows is { } held) return held;
        var loaded = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, value) in await replica.ReadPrefixAsync(prefix, cancellationToken).ConfigureAwait(false)) loaded[key] = value;
        rows = loaded;
        return loaded;
    }

    private void OnRemoteChanged(object? sender, SharedRowsChange change)
    {
        var mine = change.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (mine.Length > 0) _ = RefreshAsync(mine, change.Arrived);
    }

    private async Task RefreshAsync(IReadOnlyList<string> keys, long arrived)
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Not loaded yet: the first load reads them as they are now. Still told: what was drawn before the first read (the saved list at a start)
                // is drawn again through that load.
                if (rows is { } held)
                    foreach (var key in keys)
                    {
                        if (await replica.ReadAsync(key).ConfigureAwait(false) is { } value) held[key] = value;
                        else held.TryRemove(key, out _);
                    }
            }
            finally { gate.Release(); }
        }
        // The replica could not be read now: the whole category is read again at the next use rather than kept half changed, and that read is the change.
        catch (Exception) { rows = null; }
        try { Changed?.Invoke(this, new SharedRowsChange(keys, arrived)); }
        catch (Exception) { /* a listener that fails must not drop the rows */ }
    }

    public void Dispose() => replica.RemoteChanged -= OnRemoteChanged;
}

/// <summary>
/// Pin, mute (timed or not), marked unread, archive on this PC and a service mute set aside, as rows <c>chatpref:&lt;H(profile, chat)&gt;.&lt;setting&gt;</c>
/// holding the files' own JSON (<see cref="LocalChatPreferenceStore"/>), or the files themselves while the server keeps no such rows.
/// </summary>
internal sealed class ReplicaChatPreferences(ReplicaPreferenceSource source, LocalChatPreferenceStore files, Action<string>? log = null) : ILocalChatPreferences, IDisposable
{
    private readonly ReplicaRows rows = new(source.Replica, SharedCategories.ChatPreference);
    private int unreadable;

    /// <summary>The rows, whose <see cref="ReplicaRows.Changed"/> says another device changed one (SharedListChanges).</summary>
    public ReplicaRows Rows => rows;

    private static string Key(string profile, string chatId, ChatSetting setting) => SharedRows.ChatPreferenceKey(profile, chatId, setting.ToString());

    public async Task<LocalChatPreferences> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await files.GetAsync(profile, chatId, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadAsync(profile, chatId, ChatSetting.Pinned, cancellationToken).ConfigureAwait(false);
        var muted = await ReadAsync(profile, chatId, ChatSetting.Muted, cancellationToken).ConfigureAwait(false);
        var unread = await ReadAsync(profile, chatId, ChatSetting.MarkedUnread, cancellationToken).ConfigureAwait(false);
        var archived = await ReadAsync(profile, chatId, ChatSetting.Archived, cancellationToken).ConfigureAwait(false);
        return LocalChatPreferenceStore.Combine(pinned, muted, unread, archived);
    }

    // A row that cannot be read is off, as a file that cannot be read is (LocalChatPreferenceStore says why). A row has no time of its own, and every archive
    // is written with its moment, so there is nothing to fall back on for one without.
    private async Task<LocalChatPreferenceStore.Read> ReadAsync(string profile, string chatId, ChatSetting setting, CancellationToken cancellationToken)
    {
        if (await rows.ReadAsync(Key(profile, chatId, setting), cancellationToken).ConfigureAwait(false) is not { } value) return default;
        try
        {
            if (value.Length > 1024) throw new InvalidDataException("Invalid chat preference.");
            return LocalChatPreferenceStore.Decode(JsonSerializer.Deserialize<LocalChatPreferenceStore.Entry>(value), written: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            if (Interlocked.Increment(ref unreadable) == 1) log?.Invoke($"chat preference unreadable read_as=off source=rows error={ex.GetType().Name}");
            return default;
        }
    }

    public async Task SetAsync(string profile, string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
    {
        if (!LocalChatPreferenceStore.Local.Contains(setting)) throw new ArgumentException("Only pin, mute, marked unread and archive on this PC are local settings.");
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false))
        {
            await files.SetAsync(profile, chatId, setting, value, cancellationToken).ConfigureAwait(false);
            return;
        }
        // As the files: an unmute or an archive lifted removes the row, and the rest are written whole.
        await (setting switch
        {
            ChatSetting.Muted => SetMutedAsync(profile, chatId, value, null, cancellationToken),
            ChatSetting.Archived when !value => rows.WriteAsync(Key(profile, chatId, setting), null, cancellationToken),
            ChatSetting.Archived => WriteAsync(profile, chatId, setting, new(1, true, At: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken),
            _ => WriteAsync(profile, chatId, setting, new(1, value), cancellationToken),
        }).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(string profile, string chatId, bool muted, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false))
        {
            await files.SetMutedAsync(profile, chatId, muted, until, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (muted) await WriteAsync(profile, chatId, ChatSetting.Muted, new(1, true, Until: until?.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        else await rows.WriteAsync(Key(profile, chatId, ChatSetting.Muted), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsideServiceMuteAsync(string profile, string chatId, ServiceMute serviceMute, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false))
        {
            await files.SetAsideServiceMuteAsync(profile, chatId, serviceMute, cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteAsync(profile, chatId, ChatSetting.Muted, new(1, false, Until: serviceMute.Key), cancellationToken).ConfigureAwait(false);
    }

    private Task WriteAsync(string profile, string chatId, ChatSetting setting, LocalChatPreferenceStore.Entry entry, CancellationToken cancellationToken) =>
        rows.WriteAsync(Key(profile, chatId, setting), JsonSerializer.SerializeToUtf8Bytes(entry, LocalChatPreferenceStore.Writing), cancellationToken);

    /// <summary>The Google reset's sweep (<see cref="LocalChatPreferenceStore.Forget"/>): these conversations' settings removed, from wherever they are kept now.</summary>
    public async Task<int> ForgetAsync(string profile, IEnumerable<string> chatIds, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await Task.Run(() => files.Forget(profile, chatIds), cancellationToken).ConfigureAwait(false);
        var held = (await rows.AllAsync(cancellationToken).ConfigureAwait(false)).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var id in chatIds)
            foreach (var setting in LocalChatPreferenceStore.Local)
            {
                var key = Key(profile, id, setting);
                if (!held.Contains(key)) continue;
                await rows.WriteAsync(key, null, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        return removed;
    }

    public void Dispose() => rows.Dispose();
}

/// <summary>Names for people, as rows <c>name:&lt;H(profile, identity)&gt;</c> holding the name as a JSON string, or the files while the server keeps no such rows.</summary>
internal sealed class ReplicaContactNames(ReplicaPreferenceSource source, LocalContactNameStore files) : ILocalContactNames, IDisposable
{
    private readonly ReplicaRows rows = new(source.Replica, SharedCategories.ContactName);

    /// <inheritdoc cref="ReplicaChatPreferences.Rows"/>
    public ReplicaRows Rows => rows;

    public async Task<string?> GetAsync(string profile, string identity, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await files.GetAsync(profile, identity, cancellationToken).ConfigureAwait(false);
        // As a file: a name that is not one is no name, and the conversation keeps the provider's own.
        return await rows.ReadAsync(SharedRows.ContactNameKey(profile, identity), cancellationToken).ConfigureAwait(false) is { } value
            && SharedRows.ReadName(value) is { Length: > 0 and <= LocalContactNameStore.MaxNameLength } name ? name : null;
    }

    public async Task SetAsync(string profile, string identity, string? name, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false))
        {
            await files.SetAsync(profile, identity, name, cancellationToken).ConfigureAwait(false);
            return;
        }
        var trimmed = name?.Trim();
        var key = SharedRows.ContactNameKey(profile, identity);
        if (string.IsNullOrEmpty(trimmed))
        {
            await rows.WriteAsync(key, null, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (trimmed.Length > LocalContactNameStore.MaxNameLength) throw new ArgumentException("That name is too long.", nameof(name));
        await rows.WriteAsync(key, SharedRows.NameValue(trimmed), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Google reset's sweep (<see cref="LocalContactNameStore.Forget"/>), from wherever the names are kept now.</summary>
    public async Task<int> ForgetAsync(string profile, IEnumerable<string> identities, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await Task.Run(() => files.Forget(profile, identities), cancellationToken).ConfigureAwait(false);
        var held = (await rows.AllAsync(cancellationToken).ConfigureAwait(false)).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var key in identities.Select(identity => SharedRows.ContactNameKey(profile, identity)).Distinct(StringComparer.Ordinal).Where(held.Contains))
        {
            await rows.WriteAsync(key, null, cancellationToken).ConfigureAwait(false);
            removed++;
        }
        return removed;
    }

    public void Dispose() => rows.Dispose();
}

/// <summary>
/// Snoozes, as rows <c>snooze:&lt;H(profile, chat)&gt;</c> naming their conversation, read from the replica: no <c>snoozes.get</c> for each conversation the list
/// draws. While the server keeps no such rows, the server's own store over the wire (<c>snoozes.*</c>), exactly as before.
/// </summary>
internal sealed class ReplicaChatSnoozes : IChatSnoozes, IChangingChatSnoozes, IDisposable
{
    private readonly ReplicaPreferenceSource source;
    private readonly IChatSnoozes host;
    private readonly ReplicaRows rows;

    public ReplicaChatSnoozes(ReplicaPreferenceSource source, IChatSnoozes host)
    {
        this.source = source; this.host = host;
        rows = new(source.Replica, SharedCategories.Snooze);
        // Another device's snooze, and the move between the server's store and the rows, reach the reminders (NotificationSnoozeService, through
        // ObservedChatSnoozes) as a snooze set here does: Windows keeps one only while some store says so.
        rows.Changed += (_, _) => OnChanged(this, EventArgs.Empty);
        source.Replica.ServerProtocolChanged += OnChanged;
    }

    /// <inheritdoc />
    public event EventHandler? ChangedElsewhere;

    /// <inheritdoc cref="ReplicaChatPreferences.Rows"/>
    public ReplicaRows Rows => rows;

    private void OnChanged(object? sender, EventArgs e) => ChangedElsewhere?.Invoke(this, EventArgs.Empty);

    public async Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await host.GetAsync(profile, chatId, cancellationToken).ConfigureAwait(false);
        return await rows.ReadAsync(SharedRows.SnoozeKey(profile, chatId), cancellationToken).ConfigureAwait(false) is { } value
            && SharedRows.ReadSnooze(value) is { } snooze ? snooze.Until : null;
    }

    public async Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false))
        {
            await host.SetAsync(profile, chatId, until, cancellationToken).ConfigureAwait(false);
            return;
        }
        // As the stores: a moment already past is still written, so its reminder is still owed; null ends it.
        await rows.WriteAsync(SharedRows.SnoozeKey(profile, chatId), until is { } moment ? SharedRows.SnoozeValue(new ChatSnooze(profile, chatId, moment)) : null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!await source.RowsAsync(cancellationToken).ConfigureAwait(false)) return await host.ListAsync(cancellationToken).ConfigureAwait(false);
        // A row that cannot be read is passed over, as an unreadable file is; it is not removed, since another device may read it.
        return [.. (await rows.AllAsync(cancellationToken).ConfigureAwait(false)).Select(row => SharedRows.ReadSnooze(row.Value)).OfType<ChatSnooze>()];
    }

    public void Dispose()
    {
        source.Replica.ServerProtocolChanged -= OnChanged;
        rows.Dispose();
    }
}
