using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.ViewModels;

public sealed partial class ChatListViewModel
{
    private ChatItemViewModel? newChatDraft;
    private string? promotedChatId;
    public void IncludeCreatedChat(ChatSummary chat)
    {
        if (MessageLink.Presentation.Services.MergedConversations.IsMerged(chat.Id)) { Rebuild(); return; }
        var index = loaded.FindIndex(c => c.Id == chat.Id);
        // Pin and mute are local preferences the backend summary doesn't carry.
        if (index >= 0) loaded[index] = chat with { IsPinned = loaded[index].IsPinned, IsMuted = loaded[index].IsMuted };
        else loaded.Add(chat);
        // A newly created empty conversation may not yet appear in the phone's inbox response.
        Rebuild();
    }
    // A chat opened from New chat goes to the top of the inbox below pinned chats, even when it is old or archived,
    // until a different chat is opened. It then returns to its place by activity.
    public void PromoteChat(ChatSummary chat)
    {
        promotedChatId = merges?.DisplayId(chat.Id) ?? chat.Id;
        IncludeCreatedChat(chat);
    }
    public void ReleasePromotion(string? activeChatId)
    {
        if (promotedChatId is null || activeChatId is null || activeChatId == promotedChatId) return;
        promotedChatId = null;
        Rebuild();
    }
    private string? heldUnreadChatId;
    // Reading a conversation is what removes it from the Unread view, so opening one from that view would otherwise
    // pull the row out from under the pointer and shift everything below it. The opened row keeps its place, by
    // activity like every other row, until the user opens something else or leaves the view. Only a row the Unread
    // view is actually showing is held: arriving from another view with a conversation already open must not add a
    // read conversation to a list of unread ones. The unread badge still counts only unread conversations.
    public void HoldUnreadChat(string? activeChatId)
    {
        var hold = Scope == InboxScope.Unread && activeChatId is not null &&
            (activeChatId == heldUnreadChatId || Chats.Any(c => c.Chat.Id == activeChatId)) ? activeChatId : null;
        if (hold == heldUnreadChatId) return;
        heldUnreadChatId = hold;
        Rebuild();
    }
    // A promoted chat can be missing from the phone's first inbox page (an empty new chat, an old thread);
    // keep it through a full reload so it doesn't vanish and reappear.
    private void ReplaceLoaded(IEnumerable<ChatSummary> chats)
    {
        var promoted = loaded.FirstOrDefault(c => c.Id == promotedChatId);
        var known = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var chat in loaded) known.TryAdd(chat.Id, chat);
        loaded.Clear();
        loaded.AddRange(WithoutHeldRemovals(chats).Select(chat => KeepKnownPreview(chat, known.GetValueOrDefault(chat.Id))));
        if (promoted is not null && !loaded.Any(c => c.Id == promoted.Id)) loaded.Add(promoted);
    }
    public ChatItemViewModel BeginNewChat()
    {
        newChatDraft ??= new(new("draft:new-chat", "", "New chat", "", "single", null, 0,
            false, false, false, false, null, null, [], null, null, ChatCapabilities.Unknown, [], 0));
        Rebuild(); return newChatDraft;
    }
    public void EndNewChat() { newChatDraft = null; Rebuild(); }
}
