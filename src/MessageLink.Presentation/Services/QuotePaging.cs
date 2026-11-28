using MessageLink.Core.Messaging;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

/// <summary>What the result of looking for a quoted message was: whether it is now in the history, and how many older
/// pages were read to get there.</summary>
public readonly record struct QuoteSearch(bool Found, int Pages, bool Stopped);

// Clicking a reply's quote goes to the message it quotes. When that message is not in the loaded history, a few older
// pages are read to find it, and only on a user's click: never on Instagram, where every request counts against a real
// account with undocumented limits, and never on a combined conversation that has an Instagram member, whose older page
// would reach Instagram too. Pure, so the contract harness can drive the same loop the conversation view runs.
public static class QuotePaging
{
    public const int MaxPages = 3;

    public static bool IsInstagram(ChatSummary chat) =>
        chat.Network == "Instagram" || chat.Id.StartsWith("instagram:", StringComparison.Ordinal) ||
        chat.MemberChatIds.Any(id => id.StartsWith("instagram:", StringComparison.Ordinal));

    // Unknown means unavailable: no conversation, no paging.
    public static bool Allowed(ChatSummary? chat, IEnumerable<ChatSummary?> members) =>
        chat is not null && !IsInstagram(chat) && !members.Any(member => member is not null && IsInstagram(member));

    // The row the quote leads to: the quoted message itself, or, for a photo shown as part of a set, the set's visible row.
    public static MessageItemViewModel? Target(IEnumerable<MessageItemViewModel> rows, string chatId, string messageId)
    {
        var all = rows as IReadOnlyList<MessageItemViewModel> ?? rows.ToArray();
        var row = all.FirstOrDefault(item => item.Message.ChatId == chatId && item.Message.Id == messageId);
        if (row is not { IsPhotoGroupMember: true }) return row;
        return all.FirstOrDefault(item => !item.IsPhotoGroupMember &&
            item.PhotoGroupMessages.Any(message => message.ChatId == chatId && message.Id == messageId));
    }

    // Reads at most `max` older pages, one at a time, looking again after each. Stops as soon as the message is there, when
    // no older page can be read, or when the reader has scrolled (they have taken the history back).
    public static async Task<QuoteSearch> FindAsync(Func<bool> found, Func<Task<bool>> loadOlder, Func<bool> userScrolled, int max = MaxPages)
    {
        var pages = 0;
        while (!found())
        {
            if (userScrolled()) return new(false, pages, true);
            if (pages >= max) return new(false, pages, false);
            // False is nothing left to read, or a page that failed: either way this search is over. Pages counts the ones read.
            if (!await loadOlder()) return new(found(), pages, userScrolled());
            pages++;
        }
        return new(true, pages, false);
    }

    // The same search with the Instagram rule applied first: not allowed reads nothing at all.
    public static Task<QuoteSearch> FindAsync(ChatSummary? chat, IEnumerable<ChatSummary?> members, Func<bool> found,
        Func<Task<bool>> loadOlder, Func<bool> userScrolled, int max = MaxPages) =>
        Allowed(chat, members) ? FindAsync(found, loadOlder, userScrolled, max) : Task.FromResult(new QuoteSearch(found(), 0, false));

    // One older page of this history, when there is one. A page already on its way (the automatic load near the top) is
    // waited for rather than doubled, within a bound; that wait reads nothing. False when no page was read: nothing more can
    // be read, the page failed, or the search is over.
    // The wait can outlast the click, so everything is looked at again after it, right before a page is asked for: another
    // conversation may be open by then (an Instagram one never pages), `stop` may say the reader has taken the history back,
    // and the page waited for may already hold the message (`found`), when no further page is needed.
    public static async Task<bool> LoadOlderAsync(ConversationViewModel history, Func<bool>? found = null, Func<bool>? stop = null,
        Func<Task>? afterRender = null)
    {
        var chat = history.ActiveChatId;
        var waited = false;
        for (var polls = 0; history.IsBusy && polls < 100; polls++) { waited = true; await Task.Delay(50); }
        if (waited)
        {
            // The page waited for is put in by its own render; looking only after it finds rows where they will stay.
            if (afterRender is not null && history.ActiveChatId == chat) await afterRender();
            if (found?.Invoke() == true) return false;
        }
        if (history.ActiveChatId != chat || stop?.Invoke() == true || !Allowed(history.CurrentChat, [])) return false;
        if (history.IsBusy || !history.HasMore || !history.HasConversation) return false;
        await history.LoadOlderCommand.ExecuteAsync(null);
        if (afterRender is not null) await afterRender();
        return history.ActiveChatId == chat && !history.OlderLoadFailed;
    }
}
