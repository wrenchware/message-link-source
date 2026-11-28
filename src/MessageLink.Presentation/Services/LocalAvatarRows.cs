using System.Collections.Concurrent;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The custom photos people choose for a conversation or a person, as rows of the protected state: <c>avatar-v2:&lt;H(profile, id)&gt;</c>, a 256 px picture or the
/// choice to use the provider's (<c>null</c> bytes). The part of <c>LocalAvatarStore</c> that has no Windows API in it, so it is checked in the harness
/// (stage 6 slice 8). For a client of a server these rows are the server's shared state, which other devices write as well; this tells the interface which
/// pictures changed when one does.
/// </summary>
/// <remarks>
/// A row's key is a one-way digest of its id, so a change from another device names a key and not the conversation. The ids this store has been asked about
/// are remembered by their key, which is every picture the interface can be showing (it asks before it draws), and only those are announced: a picture nobody
/// asked for has nothing drawn to redraw. Nothing here reads or writes beyond what the store always did; the in-process app's state shares nothing, so it
/// subscribes to nothing.
/// </remarks>
public sealed class LocalAvatarRows : IDisposable
{
    public const string Category = "avatar-v2";
    private const string LegacyCategory = "avatar";
    // As many ids as pictures there can be on screen over a run; past it the memory starts again, which only costs a redraw missed until the id is asked about again.
    private const int KnownLimit = 20_000;

    private sealed record Saved(byte[]? Bytes);
    private readonly PrivateState state;
    private readonly ISharedRows? shared;
    private readonly ConcurrentDictionary<string, string> known = new(StringComparer.Ordinal);

    public LocalAvatarRows(PrivateState state)
    {
        this.state = state;
        shared = state.Shared;
        if (shared is not null) shared.HostChanged += OnHostChanged;
    }

    public event EventHandler<string>? Changed;

    public async Task<byte[]?> ReadAsync(string? overrideId, string? legacyChatId = null)
    {
        var profile = await state.ProfileAsync();
        if (profile is null || overrideId is null && legacyChatId is null) return null;
        if (overrideId is not null) Note(profile, overrideId);
        var saved = overrideId is null ? null : await state.ReadAsync<Saved>(profile, Category, overrideId);
        if (saved is not null) return saved.Bytes; // A null photo is an intentional "use provider photo" choice.
        if (legacyChatId is null) return null;
        // Migrate a photo chosen by older releases. Once encountered, a direct contact can use it in every thread.
        var legacy = await state.ReadAsync<byte[]>(profile, LegacyCategory, legacyChatId);
        if (legacy is not null && overrideId is not null && Fits(new Saved(legacy))) await state.WriteAsync(profile, Category, overrideId, new Saved(legacy));
        return legacy;
    }

    // Removes photos chosen for these conversations themselves ("chat:" + id, and the older per-chat slot under the bare
    // id); a photo chosen for a person is filed under the person and is not touched. The older slot is counted apart:
    // ReadAsync copies it to whoever the conversation names now, so one found here may already have been copied.
    public async Task<(int Chosen, int Legacy)> ForgetAsync(string profile, IReadOnlyCollection<string> chatIds)
    {
        foreach (var id in chatIds) Note(profile, "chat:" + id);
        var chosen = await state.RemoveIdsAsync(profile, Category, chatIds.Select(id => "chat:" + id));
        var legacy = await state.RemoveIdsAsync(profile, LegacyCategory, chatIds);
        foreach (var id in chosen.Concat(legacy)) Changed?.Invoke(this, id);
        return (chosen.Count, legacy.Count);
    }

    /// <summary>Keeps the chosen picture (null: the provider's) for <paramref name="overrideId"/>, and clears the older per-chat slot so removing a migrated override cannot reveal it again.</summary>
    public async Task StoreAsync(string profile, string overrideId, string legacyChatId, byte[]? bytes)
    {
        if (profile != await state.ProfileAsync()) throw new InvalidOperationException("Connection changed. Choose the photo again.");
        Note(profile, overrideId);
        await state.WriteAsync(profile, Category, overrideId, new Saved(bytes));
        // Clear the old per-chat slot so removing a migrated override cannot reveal it again.
        if (legacyChatId != overrideId) await state.WriteAsync<byte[]?>(profile, LegacyCategory, legacyChatId, null);
        Changed?.Invoke(this, overrideId);
        if (legacyChatId != overrideId) Changed?.Invoke(this, legacyChatId);
    }

    // A photo chosen by an older release was kept as it came, not cut to 256 px, so it can be larger than the host keeps a row (SharedValueLimit). Copied into a
    // shared row it would stay on this device only and be dropped by the next snapshot of the host's rows. It is not re-encoded here (that takes Windows' imaging,
    // which this class has none of) and not lost either: it stays in the older slot, which is never shared, and is read from there each time. Only a picture
    // chosen anew (StoreAsync) is cut down, and shared.
    private bool Fits(Saved value) =>
        shared is null || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length <= shared.SharedValueLimit;

    private void Note(string profile, string id)
    {
        if (shared is null) return;
        if (known.Count >= KnownLimit) known.Clear();
        known[PrivateState.KeyOf(profile, Category, id)] = id;
    }

    // Another device chose, or removed, a picture (or this device's write lost to its): the pictures drawn from the ids it names are announced as changed, as for a
    // choice made here, off the replica's thread.
    private void OnHostChanged(object? sender, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (!key.StartsWith(Category + ":", StringComparison.Ordinal) || !known.TryGetValue(key, out var id)) continue;
            try { Changed?.Invoke(this, id); } catch (Exception) { /* a listener that fails must not stop the replica */ }
        }
    }

    public void Dispose()
    {
        if (shared is not null) shared.HostChanged -= OnHostChanged;
    }
}
