using System.Text.Json;
using MessageLink.Core.Services;
namespace MessageLink.Presentation.Services;
public sealed class PrivateState
{
    private readonly IPrivateStateStore store;
    private readonly Func<Task<string?>> profile;

    public PrivateState(IPrivateStateStore store, IAccessTokenStore tokens) : this(store, () => NotificationIdentity.ProfileAsync(tokens)) { }

    /// <summary>Over a store that knows the profile its rows are filed under without a key to derive it from (a server's shared space).</summary>
    public PrivateState(IPrivateStateStore store, Func<Task<string?>> profile)
    {
        this.store = store;
        this.profile = profile;
    }

    public Task<string?> ProfileAsync() => profile();
    private static string Key(string profile, string category, string id) => category + ":" + NotificationIdentity.Hash(profile + "\n" + id);
    // A row's stored key, as ReadCategoryAsync returns it, so an index read by category can find one conversation's row.
    public static string KeyOf(string profile, string category, string id) => Key(profile, category, id);
    public async Task<T?> ReadAsync<T>(string profile, string category, string id)
    {
        var bytes = await store.ReadAsync(Key(profile, category, id));
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }
    public Task WriteAsync<T>(string profile, string category, string id, T value) => store.WriteAsync(Key(profile, category, id), JsonSerializer.SerializeToUtf8Bytes(value));
    // The rows other devices write as well, when this store is a client's replica of a server's shared state (stage 6 slice 5); null for state.db.
    public ISharedRows? Shared => store as ISharedRows;
    // A change to a row a follower holds (ISharedRows), tagged with its newest change; a store that shares nothing just writes it.
    public Task WriteAsync<T>(string profile, string category, string id, T value, long tag) => store is ISharedRows shared
        ? shared.WriteAsync(Key(profile, category, id), JsonSerializer.SerializeToUtf8Bytes(value), tag)
        : store.WriteAsync(Key(profile, category, id), JsonSerializer.SerializeToUtf8Bytes(value));
    // Reads a row a follower will hold, and makes it that row's follower in the same step (ISharedRows.FollowAsync); a store that shares nothing just reads it.
    public async Task<T?> FollowAsync<T>(string profile, string category, string id, ISharedRowFollower follower)
    {
        var bytes = store is ISharedRows shared ? await shared.FollowAsync(Key(profile, category, id), follower) : await store.ReadAsync(Key(profile, category, id));
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }
    // Whether changes to the row go to a host now, to be made again if they lose (ISharedRows.Shares); never for a store that shares nothing.
    public bool Shares(string profile, string category, string id) => Shared?.Shares(Key(profile, category, id), profile) == true;
    // Without moving the row up among the expendable copies (IPrivateStateStore.WriteInPlaceAsync).
    public Task WriteInPlaceAsync<T>(string profile, string category, string id, T value) => store.WriteInPlaceAsync(Key(profile, category, id), JsonSerializer.SerializeToUtf8Bytes(value));
    public Task RemoveAsync(string profile, string category, string id) => store.DeleteAsync(Key(profile, category, id));
    // Every stored value in a category, by key. A key names its conversation only as a keyed hash, so anything that has
    // to reason about rows the app can no longer match to a conversation starts here. A row that cannot be read is
    // returned with no value rather than skipped, so a caller clearing a category still clears it.
    public async Task<IReadOnlyList<(string Key, T? Value)>> ReadCategoryAsync<T>(string category)
    {
        var keys = await store.KeysAsync(category + ":");
        var values = new List<(string, T?)>(keys.Count);
        foreach (var key in keys)
        {
            T? value = default;
            try { if (await store.ReadAsync(key) is { } bytes) value = JsonSerializer.Deserialize<T>(bytes); }
            catch (Exception) { value = default; }
            values.Add((key, value));
        }
        return values;
    }
    public Task RemoveKeyAsync(string key) => store.DeleteAsync(key);
    // Which of these ids have a row for this profile, matched by digest like RemoveIdsAsync: nothing is read or decrypted.
    public async Task<IReadOnlySet<string>> ExistingIdsAsync(string profile, string category, IEnumerable<string> ids)
    {
        var wanted = await Task.Run(() =>
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ids) keys.TryAdd(Key(profile, category, id), id);
            return keys;
        });
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in await store.KeysAsync(category + ":"))
            if (wanted.TryGetValue(key, out var id)) found.Add(id);
        return found;
    }
    // Removes this profile's rows in a category for these ids, matched by recomputing each id's digest: nothing is read or
    // decrypted, and ids that never had a row cost only the hashing. Returns the ids whose row was there.
    public async Task<IReadOnlyList<string>> RemoveIdsAsync(string profile, string category, IEnumerable<string> ids)
    {
        var wanted = await Task.Run(() =>
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ids) keys.TryAdd(Key(profile, category, id), id);
            return keys;
        });
        var removed = new List<string>();
        foreach (var key in await store.KeysAsync(category + ":"))
        {
            if (!wanted.TryGetValue(key, out var id)) continue;
            await store.DeleteAsync(key);
            removed.Add(id);
        }
        return removed;
    }
}
