using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

public enum SearchKind { Conversations, Participants, Messages }
public enum SearchContent { All, Photos, Videos, Links, Files, Audio, Calls, Voicemail }
public sealed record SearchFilters(string? Person = null, string? Service = null, DateOnly? From = null,
    DateOnly? Through = null, SearchContent Content = SearchContent.All, IReadOnlyList<string>? LinkedChatIds = null)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(Person) || !string.IsNullOrWhiteSpace(Service) ||
        From is not null || Through is not null || Content != SearchContent.All;
}
public sealed record SearchRequest(string Query, SearchKind Kind, string? ChatId = null, SearchFilters? Filters = null)
{
    public bool CanSearch => Query.Length <= 1000 && (!string.IsNullOrWhiteSpace(Query) ||
        Kind == SearchKind.Messages && (ChatId is not null || Filters?.IsActive == true));
}
public sealed record SearchHit(ChatSummary Chat, ChatMessage? Message);

public interface ISearchScopeInfo
{
    string SearchScopeDescription { get; }
}

public interface ISearchBackend
{
    Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default);
}
