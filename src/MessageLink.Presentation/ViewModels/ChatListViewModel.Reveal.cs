using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.ViewModels;

// The list's own filters, as flags, so a caller can say which of them had to change.
[Flags]
public enum ChatListFilters { None = 0, Scope = 1, Service = 2, Kind = 4, Label = 8 }

public sealed partial class ChatListViewModel
{
    /// <summary>
    /// Puts every filter the list has back to showing everything in one scope, and says which ones were on. This is
    /// the one place that knows the whole set: a filter left out here is one that can hide a conversation somebody
    /// asked to open, and Calls or Voicemails once did exactly that.
    /// </summary>
    public ChatListFilters ClearFilters(InboxScope scope)
    {
        var cleared = ChatListFilters.None;
        // Each filter asks for a rebuild as it changes; they are drawn once, and always before this returns.
        using (HoldRebuild())
        {
            if (Scope != scope) { Scope = scope; cleared |= ChatListFilters.Scope; }
            if (HasAccountFilter) cleared |= ChatListFilters.Service;
            SelectedFilter = Filters.FirstOrDefault();
            if (Kind != ChatKind.All) { Kind = ChatKind.All; cleared |= ChatListFilters.Kind; }
            if (HasLabelFilter) { SetLabelFilters([]); cleared |= ChatListFilters.Label; }
        }
        return cleared;
    }

    /// <summary>
    /// A conversation opened from outside the list — a notification, a pinned-chat key, the jump list, Commands —
    /// is listed and selectable whatever the list was showing: the view becomes the one it lives in, every other
    /// filter is cleared, and it is added if no page has loaded it yet. Returns its row, or null if it still cannot
    /// be listed, and which filters were cleared to show it.
    /// </summary>
    public (ChatItemViewModel? Row, ChatListFilters Cleared) RevealChat(ChatSummary chat)
    {
        ChatListFilters cleared;
        using (HoldRebuild())
        {
            cleared = ClearFilters(chat.IsSpam ? InboxScope.Spam : chat.IsArchived ? InboxScope.Archived : InboxScope.Inbox);
            IncludeCreatedChat(chat);
        }
        return (Chats.FirstOrDefault(row => row.Chat.Id == chat.Id), cleared);
    }
}
