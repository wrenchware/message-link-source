using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

/// <summary>Finds a message only in memory the backend already holds. Never asks a provider, never pages, never
/// reads a conversation; returns null on a miss. Safe to call for display-only purposes.</summary>
public interface IMessagePeek
{
    ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>The same memory, but only a copy as whole as reading the conversation would give: never one a backend
    /// holds only in part, such as the list's words for an Instagram photo or share, which has no picture. History is
    /// built from this one — a conversation kept up to date while it is closed, a quoted message — so a partial copy put
    /// there would draw as something it is not and then change. Null on a miss, and from any backend that has not said
    /// which of its copies are whole: unknown means unavailable.</summary>
    ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ChatMessage?>(null);

    /// <summary>Whether this conversation's backend keeps some messages only in part (Instagram's list words for a photo), so a
    /// copy <see cref="PeekAsync"/> has and <see cref="PeekWholeAsync"/> does not is one of those. Elsewhere a missing whole copy
    /// is only a miss: Messenger's is skipped while its pipe is busy.</summary>
    bool HoldsPartialCopies(string chatId) => false;
}
