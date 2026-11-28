using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// Which conversations the clients' merges hold, as a server reads them: the one <c>merges:</c> row of its shared
/// state, under the profile its space key gives, read through the window's own <see cref="MergedConversations"/> so a Google Voice person's other
/// conversations and a call's text route count as they count there. Read only: the server never writes the merges, which each client changes itself.
/// </summary>
/// <remarks>The row is read again for each question: one SQLite read and a few KB of JSON, asked once for each arrival in an archived one-to-one conversation.</remarks>
internal sealed class SharedMergeMembership
{
    private readonly MergedConversations merges;
    // The unarchiver and the Marketplace labels ask from their own workers, and a reload empties the merges before it reads the row again: unserialized, the
    // other's DisplayId could find none and unarchive a merged member at its service, or file a label under the member's id.
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="store">The server's shared state, read under the profile it files the server's rows under (its space key's once pinned), as asked.</param>
    public SharedMergeMembership(SharedStateStore store) =>
        merges = new MergedConversations(store.View<MergedConversation[]>(SharedCategories.Merges).ReadOnlyState(), GoogleVoiceIds.TextRouteForCall);

    /// <summary>Whether a merge holds <paramref name="chat"/>. Throws when the row cannot be read.</summary>
    public async Task<bool> HoldsAsync(ChatSummary chat, CancellationToken cancellationToken) =>
        await DisplayIdAsync(chat, cancellationToken).ConfigureAwait(false) != chat.Id;

    /// <summary>The id the list shows <paramref name="chat"/> under: its merge's, or its own. Throws when the row cannot be read.</summary>
    public async Task<string> DisplayIdAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await merges.ReloadAsync().ConfigureAwait(false);
            merges.Observe([chat]);
            return merges.DisplayId(chat.Id);
        }
        finally { gate.Release(); }
    }
}
