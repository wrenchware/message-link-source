using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// The open conversation's header following what another device changed, as its list row does.
public sealed partial class ConversationViewModel
{
    /// <summary>
    /// When <paramref name="row"/> is the conversation open here: its title (when the row knows its names), and the settings
    /// Details shows (mute), taken from it (<see cref="ChatListViewModel.WithShared"/>), with no read of the conversation.
    /// False for any other.
    /// </summary>
    public bool ApplyShared(ReappliedChat row)
    {
        if (CurrentChat is not { } chat || chat.Id != row.Chat.Id) return false;
        if (current is { } held && held.Chat.Id == row.Chat.Id)
        {
            var next = held with { Chat = ChatListViewModel.WithShared(held.Chat, row.Chat, row.Names) };
            // Only where the cache still holds this read, and without counting as a use of it.
            cache.Replace(held, next);
            current = next;
        }
        if (selectedChat is { } selected && selected.Id == row.Chat.Id) selectedChat = ChatListViewModel.WithShared(selected, row.Chat, row.Names);
        Title = CurrentChat!.Title;
        // What Details redraws its title and mute from.
        OnPropertyChanged(nameof(Accounts));
        return true;
    }
}
