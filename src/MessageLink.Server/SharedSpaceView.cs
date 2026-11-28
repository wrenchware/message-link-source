using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>One live row of a <see cref="SharedSpaceView{T}"/>: its id within the category, its value, its rev, and the bytes it holds.</summary>
internal sealed record SharedSpaceRow<T>(string Id, T Value, long Rev, byte[] Bytes);

/// <summary>
/// One category of the server's shared space as the server's own features read and write it (the scheduled sends, the repeats, the records of unsettled sends,
/// the snoozes, the merges it reads and the Marketplace label): the profile the rows are filed under now (<see cref="SharedStateStore.ProfileAsync"/>), each
/// row's key (<see cref="SharedCategories.KeyOf"/>, the profile and the row's id), its JSON, and one write path, the store's own batch
/// (<see cref="SharedStateStore.WriteOwnBatchAsync"/>), held to a rev where a feature needs it. A feature names rows by id and never sees a key or a profile.
/// </summary>
/// <remarks>
/// <para>A feature reads the profile once for each step (<see cref="FiledAsync"/>) and works under it, so a step never reads under one profile and writes under
/// another. The snoozes alone name the profile themselves (<see cref="Under"/>): a snooze is kept under the profile its client gave.</para>
/// <para>Nothing here changes what is stored: the keys, the values' JSON and the write's commit are the ones the features made themselves before.</para>
/// </remarks>
internal sealed class SharedSpaceView<T> where T : class
{
    private readonly SharedStateStore store;
    private readonly string category;
    private readonly Func<T, string?>? idOf;
    private readonly Func<T, byte[]> encode;
    private readonly Func<byte[], T?> decode;

    /// <param name="idOf">A row's id from its value, or null when the value is not one of the category's: what tells this profile's rows from the rest of the
    /// category (a key is a digest of the profile and the id, which cannot be taken apart). Only <see cref="Filed.RowsAsync"/> needs it.</param>
    /// <param name="encode">The value's JSON; System.Text.Json's default spelling when not given, which is what every client reads.</param>
    /// <param name="decode">A row's value, or null when it cannot be read; System.Text.Json's default when not given.</param>
    internal SharedSpaceView(SharedStateStore store, string category, Func<T, string?>? idOf, Func<T, byte[]>? encode, Func<byte[], T?>? decode)
    {
        this.store = store;
        this.category = category;
        this.idOf = idOf;
        this.encode = encode ?? (value => JsonSerializer.SerializeToUtf8Bytes(value));
        this.decode = decode ?? Decode;
    }

    private static T? Decode(byte[] value)
    {
        try { return JsonSerializer.Deserialize<T>(value); }
        catch (JsonException) { return null; }
    }

    /// <summary>The category under the profile the space files the server's rows under now, or null while there is none.</summary>
    public async Task<Filed?> FiledAsync(CancellationToken cancellationToken = default) =>
        await store.ProfileAsync(cancellationToken).ConfigureAwait(false) is { } profile ? new Filed(this, profile) : null;

    /// <summary>The category under a profile a client named (a snooze, kept under the profile its client gave).</summary>
    public Filed Under(string profile) => new(this, profile);

    /// <summary>Every live row of the category whatever profile it is filed under, that can be read.</summary>
    public async Task<IReadOnlyList<T>> EveryProfileAsync(CancellationToken cancellationToken = default) =>
        [.. (await store.LiveRowsAsync(category + ":", cancellationToken).ConfigureAwait(false)).Select(row => decode(row.Value)).OfType<T>()];

    /// <summary>
    /// A read-only <see cref="PrivateState"/> over the space, filed under the space's profile: for a reader written against the window's own stores (the merges,
    /// read through <see cref="MergedConversations"/>). Nothing can be written through it.
    /// </summary>
    public PrivateState ReadOnlyState() => new(new ReadOnlyRows(store), () => store.ProfileAsync());

    /// <summary>The category under one profile.</summary>
    internal sealed class Filed(SharedSpaceView<T> view, string profile)
    {
        /// <summary>The profile, as a token to tell whether two steps were filed alike: for a store contract that names one (the schedule engine's).</summary>
        public string Profile => profile;

        private string KeyOf(string id) => SharedCategories.KeyOf(profile, view.category, id);

        /// <summary>The value's JSON, as a write keeps it.</summary>
        public byte[] Encode(T value) => view.encode(value);

        /// <summary>Every live row of the category filed under this profile, by key order. A row that cannot be read, or that is another profile's, is passed over.</summary>
        public async Task<IReadOnlyList<SharedSpaceRow<T>>> RowsAsync(CancellationToken cancellationToken = default)
        {
            var idOf = view.idOf ?? throw new InvalidOperationException("This category's rows carry no id.");
            var rows = new List<SharedSpaceRow<T>>();
            foreach (var (key, bytes, rev) in await view.store.LiveRowsWithRevAsync(view.category + ":", cancellationToken).ConfigureAwait(false))
                if (view.decode(bytes) is { } value && idOf(value) is { } id && key == KeyOf(id)) rows.Add(new(id, value, rev, bytes));
            return rows;
        }

        /// <summary>One live row, or null when there is none or it cannot be read.</summary>
        public async Task<SharedSpaceRow<T>?> ReadAsync(string id, CancellationToken cancellationToken = default)
        {
            var (bytes, rev) = await view.store.LiveRowAsync(KeyOf(id), cancellationToken).ConfigureAwait(false);
            return bytes is not null && view.decode(bytes) is { } value ? new(id, value, rev, bytes) : null;
        }

        /// <summary>The rev of one live row, or 0 when there is none: what a command's <c>ifRev</c> is held against.</summary>
        public Task<long> RevAsync(string id, CancellationToken cancellationToken = default) => view.store.LiveRevAsync(KeyOf(id), cancellationToken);

        /// <summary>
        /// Writes rows by id in one commit (a null value removes one). Returns the commit's rev, or 0 when nothing changed. <paramref name="pin"/>,
        /// <paramref name="retire"/> and <paramref name="makes"/> are the store's (<see cref="SharedStateStore.WriteOwnBatchAsync"/>).
        /// </summary>
        public Task<long> WriteAsync(IReadOnlyList<(string Id, T? Value)> writes, bool pin = false, IReadOnlyList<string>? retire = null,
            IReadOnlyList<string>? makes = null, CancellationToken cancellationToken = default) =>
            view.store.WriteOwnBatchAsync([.. writes.Select(write => (KeyOf(write.Id), write.Value is { } value ? view.encode(value) : null))], pin, retire, makes,
                cancellationToken, filedUnder: profile);

        /// <summary>
        /// Writes one row only while it is at <paramref name="ifRev"/> (0: only while there is none), pinning the space's key as any first row does. False when
        /// another write moved it since it was read: nothing was kept, and the caller reads it again.
        /// </summary>
        public async Task<bool> WriteIfAsync(string id, T value, long ifRev, CancellationToken cancellationToken = default)
        {
            try
            {
                await view.store.WriteOwnBatchAsync([(KeyOf(id), view.encode(value))], pin: true, cancellationToken: cancellationToken, filedUnder: profile,
                    ifRevs: [ifRev]).ConfigureAwait(false);
                return true;
            }
            catch (SharedRowMovedException) { return false; }
        }
    }

    // The live rows, read as PrivateState reads state.db. Nothing is ever written through it.
    private sealed class ReadOnlyRows(SharedStateStore store) : IPrivateStateStore
    {
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => store.ReadLiveAsync(key, cancellationToken);
        public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new NotSupportedException("The host never writes this.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException("The host never writes this.");
        public Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
