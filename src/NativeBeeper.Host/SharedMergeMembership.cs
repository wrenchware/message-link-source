using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Host;

/// <summary>
/// Which conversations the clients' merges hold, as a server's host reads them (SWITCHBOARD.md stage 6, slice 5): the one <c>merges:</c> row of its shared
/// state, under the profile its space key gives, read through the window's own <see cref="MergedConversations"/> so a Google Voice person's other
/// conversations and a call's text route count as they count there. Read only: the host never writes the merges, which each client changes itself.
/// </summary>
/// <remarks>The row is read again for each question: one SQLite read and a few KB of JSON, asked once for each arrival in an archived one-to-one conversation.</remarks>
internal sealed class SharedMergeMembership
{
    private readonly MergedConversations merges;

    /// <param name="localKey">The key the host files its shared rows under (its space key once pinned, <see cref="SharedStateStore.AttachSpace"/>), read when asked.</param>
    public SharedMergeMembership(SharedStateStore store, Func<CancellationToken, Task<string?>> localKey) =>
        merges = new MergedConversations(new PrivateState(new Rows(store), new Key(localKey)), GoogleVoiceIds.TextRouteForCall);

    /// <summary>Whether a merge holds <paramref name="chat"/>. Throws when the row cannot be read.</summary>
    public async Task<bool> HoldsAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await merges.ReloadAsync().ConfigureAwait(false);
        merges.Observe([chat]);
        return merges.DisplayId(chat.Id) != chat.Id;
    }

    /// <summary>The id the list shows <paramref name="chat"/> under: its merge's, or its own. Throws when the row cannot be read.</summary>
    public async Task<string> DisplayIdAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await merges.ReloadAsync().ConfigureAwait(false);
        merges.Observe([chat]);
        return merges.DisplayId(chat.Id);
    }

    private sealed class Key(Func<CancellationToken, Task<string?>> localKey) : IAccessTokenStore
    {
        public async ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) => await localKey(cancellationToken).ConfigureAwait(false);
    }

    // The live rows of the shared state, read as PrivateState reads state.db. Nothing is ever written through it.
    private sealed class Rows(SharedStateStore store) : IPrivateStateStore
    {
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => store.ReadLiveAsync(key, cancellationToken);
        public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) => throw new NotSupportedException("The host never writes the merges.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException("The host never writes the merges.");
        public Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
