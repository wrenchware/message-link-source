using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

/// <summary>One service of a merged conversation, as the history's "Show" choice offers it: a network and account.</summary>
public sealed record MergedSourceChoice(string Key, string Label, string Network);

// Showing one service of a merged conversation on its own: a local filter over the combined timeline. It only chooses
// which loaded rows are drawn. Paging, the sending route, drafts and the read rule are those of the whole conversation —
// the route is still worked out from every service's messages — and "reaching the bottom acknowledges displayed history"
// then acknowledges only what is displayed, never a hidden service's rows. It ends when the conversation is left or
// opened again, and when this conversation's own composer sends through a service it hides, so the message just sent is
// never out of sight.
//
// Choosing a service reads nothing. Earlier history is read only when asked for — scrolling up, or the list's own "Load
// earlier messages" — because a combined page reads every member's history, Instagram's included, and Instagram's limits
// are undocumented with a checkpoint on the real account as the price of finding them.
public sealed partial class ConversationViewModel
{
    // A load of earlier messages under a filter reads on until it has brought this many of the shown service's rows, or
    // read FilteredPagesPerLoad pages; one page when an Instagram conversation is in the merge (FilteredPagesPerInstagramLoad).
    public const int FilteredPageFill = 20, FilteredPagesPerLoad = 3, FilteredPagesPerInstagramLoad = 1;
    // Pages one load of earlier messages may read under the filter.
    private int FilteredPageBound => merges is not null && ActiveChatId is { } id && merges.Members(id).Any(m => m.Network == "Instagram")
        ? FilteredPagesPerInstagramLoad : FilteredPagesPerLoad;
    private string? sourceFilter;
    private MergedSourceChoice[] sourceChoices = [];

    public IReadOnlyList<MergedSourceChoice> SourceChoices => sourceChoices;
    /// <summary>A choice is worth offering: a merged conversation with two services or more.</summary>
    public bool HasSourceChoices => sourceChoices.Length >= 2;
    /// <summary>The key of the one service shown, or null for all of them.</summary>
    public string? SourceFilter => sourceFilter;
    public string SourceFilterLabel => sourceChoices.FirstOrDefault(c => c.Key == sourceFilter)?.Label ?? "All services";
    /// <summary>The list's own "Load earlier messages": a filtered history can be too short to scroll, and scrolling is what loads.</summary>
    public bool ShowFilteredLoadMore => sourceFilter is not null && HasMore && !IsLoadingOlder && !OlderLoadFailed;

    public static string SourceKey(ChatSummary chat) => chat.Network + "\n" + chat.AccountId;

    /// <summary>
    /// Shows one service (a key from <see cref="SourceChoices"/>) or, with null, all of them. Drawn at once from what is
    /// loaded, reading nothing, and drawn as a history that follows its newest message. A service with few rows loaded
    /// shows what it has, and <see cref="ShowFilteredLoadMore"/> offers to read further.
    /// </summary>
    public bool SetSourceFilter(string? key)
    {
        if (!IsMerged || key == sourceFilter || key is not null && !sourceChoices.Any(c => c.Key == key)) return false;
        sourceFilter = key;
        NotifySourceFilter();
        if (current is null) return true;
        Render(false, needsRecovery);
        return true;
    }

    private void ResetSourceFilter()
    {
        if (sourceFilter is null) return;
        sourceFilter = null;
        NotifySourceFilter();
    }
    private void NotifySourceFilter()
    {
        OnPropertyChanged(nameof(SourceFilter)); OnPropertyChanged(nameof(SourceFilterLabel)); OnPropertyChanged(nameof(ShowFilteredLoadMore));
    }

    // The services a merged conversation can show on its own, in the order its members are listed. A Voice account's call
    // records and its text route are one service; so are two conversations on one account. A filter whose service has
    // left the merge ends.
    private void UpdateSourceChoices()
    {
        var next = IsMerged && merges is not null && ActiveChatId is { } id
            ? merges.Members(id).GroupBy(SourceKey).Select(g => new MergedSourceChoice(g.Key, merges.ServiceRouteLabel(g.First()), g.First().Network)).ToArray()
            : [];
        if (!next.SequenceEqual(sourceChoices))
        {
            sourceChoices = next;
            OnPropertyChanged(nameof(SourceChoices)); OnPropertyChanged(nameof(HasSourceChoices)); OnPropertyChanged(nameof(SourceFilterLabel));
        }
        if (sourceFilter is not null && (!HasSourceChoices || !sourceChoices.Any(c => c.Key == sourceFilter))) ResetSourceFilter();
    }

    // The conversations whose rows are drawn; null draws every row.
    private HashSet<string>? ShownChatIds() =>
        sourceFilter is null || merges is null || ActiveChatId is not { } id ? null
            : merges.Members(id).Where(m => SourceKey(m) == sourceFilter).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

    private int ShownCount(HistorySnapshot snapshot) => ShownChatIds() is { } shown ? snapshot.Messages.Count(m => !m.IsHidden && shown.Contains(m.ChatId)) : 0;

    // Whether a load of earlier messages reads another page before drawing: only under a filter, only while there is more,
    // the page came back whole, and it has not yet brought FilteredPageFill of the shown rows in FilteredPageBound pages.
    private bool ReadOnForFilter(HistorySnapshot loaded, int shownBefore, int pages, Page<ChatMessage> page) =>
        sourceFilter is not null && !Unanswered(page) && loaded.HasMore && loaded.Cursor is not null && page.Warning is null && !loaded.AtLimit && !loaded.PaginationStalled &&
        pages < FilteredPageBound && ShownCount(loaded) - shownBefore < FilteredPageFill;

    /// <summary>
    /// A send from this conversation's own composer through a service the filter hides shows everything again, so the
    /// message is not sent out of sight. Only that composer's: a Send later firing, or a line from the chat console, goes
    /// through the outbox as well, and those leave alone a view the user set up and did not send from. Called by the view
    /// as its composer hands the message over. Drawn as a history rather than as arrivals: the rows the filter hid are not
    /// new, and must not slide in as if they were.
    /// </summary>
    public void ShowAllForOwnSend(string chatId)
    {
        if (sourceFilter is null || ShownChatIds()?.Contains(chatId) != false) return;
        ResetSourceFilter();
        if (current is not null) Render(false, needsRecovery);
    }
}
