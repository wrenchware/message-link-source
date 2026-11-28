using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.ViewModels;

// A conversation just deleted here, kept out of the list against the provider's own stale answers for a moment.
//
// Deleting on Google Messages is confirmed by the call, and the phone then echoes the conversation back as it was:
// a "changed" event whose read still returns it, and only half a second later the removal. Applying that echo
// re-added the row, so the user's recording shows the conversation leave, return and leave again. Until the provider
// reports it removed, or the hold runs out, a read that still returns it is not applied. Bounded, so a conversation
// that genuinely comes back — a new message from the same person — is never kept out for long.
public sealed partial class ChatListViewModel
{
    private static readonly TimeSpan RemovalHold = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, long> heldRemovals = new(StringComparer.Ordinal);

    private void HoldRemoval(string chatId) => heldRemovals[chatId] = Environment.TickCount64 + (long)RemovalHold.TotalMilliseconds;
    // The provider has caught up; later reads are its real answer.
    private void ReleaseRemoval(string chatId) => heldRemovals.Remove(chatId);

    private bool IsHeldRemoved(string chatId)
    {
        if (!heldRemovals.TryGetValue(chatId, out var until)) return false;
        if (Environment.TickCount64 <= until) return true;
        heldRemovals.Remove(chatId);
        return false;
    }

    private IEnumerable<ChatSummary> WithoutHeldRemovals(IEnumerable<ChatSummary> chats)
        => heldRemovals.Count == 0 ? chats : chats.Where(chat => !IsHeldRemoved(chat.Id));
}
