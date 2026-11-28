using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// Provider search remains unchanged outside a merged conversation. Inside one, page the same
// chronological history the user can open, including services without a provider search API.
public sealed class MergedSearchBackend(ISearchBackend source, IChatReader reader, MergedConversations merges) : ISearchBackend, ISearchScopeInfo
{
    public string SearchScopeDescription => source is ISearchScopeInfo scope ? scope.SearchScopeDescription : "Searches each service's messages.";
    public async Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (!MergedConversations.IsMerged(request.ChatId)) return await source.SearchAsync(request, cursor, cancellationToken);
        if (!request.CanSearch) return new([], false, null, null);
        await merges.EnsureLoadedAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var page = await reader.GetMessagesAsync(request.ChatId!, cursor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var hits = page.Items.Select(m => new SearchHit(merges.Source(m.ChatId) ?? merges.Summary(request.ChatId!), m))
            .Where(hit => MessageSearch.Matches(hit.Chat, hit.Message!, request with { ChatId = null }))
            .OrderByDescending(hit => hit.Message!.Timestamp).ToArray();
        return new(hits, page.HasMore, page.OldestCursor, null, page.IsRefreshing, page.Warning, page.UnavailableScopes);
    }
}
