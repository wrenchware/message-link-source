using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed record SearchResultViewModel(SearchHit Hit)
{
    public string Title => Hit.Chat.Title;
    public string Detail => Hit.Chat.Network + (Hit.Message is { } m ? $" · {m.SenderName ?? (m.IsOutgoing ? "You" : "Message")} · {m.Timestamp.ToLocalTime():g}" : "");
    public string Text => MessageText.PlainText((Hit.Message ?? Hit.Chat.Preview) is { IsHidden: false, IsDeleted: false } m ? MessageText.Markup(m) : "");
    public string Preview => Text.Length > 220 ? Text[..220] + "…" : Text.Length == 0 ? "Attachment or conversation" : Text;
    // The words searched for, so a result can show which of its words matched.
    public string Query { get; init; } = "";
    public string AvatarChatId => Hit.Chat.Id;
    public string AvatarOverrideId => AvatarOverrideIdentity.ForChat(Hit.Chat);
    public string? AvatarUrl => Hit.Chat.AvatarUrl;
    public string? AvatarColor => Hit.Chat.AvatarColor;
    public IReadOnlyList<NativeBeeper.Core.Messaging.ChatParticipant> Participants => Hit.Chat.Participants;
    public bool IsGroup => Hit.Chat.Type == "group";
}

public sealed partial class SearchViewModel(ISearchBackend backend) : ObservableObject, IDisposable
{
    private CancellationTokenSource? request;
    private SearchRequest? active;
    private string? cursor;
    private int generation;
    private readonly HashSet<string> cursors = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusLead)), NotifyPropertyChangedFor(nameof(StatusRest))]
    private string status = "Search the history available in the connected service.";
    // The status in two parts for display: the count or outcome that leads it, then what the search covered. A status
    // with nothing after it (Searching…) has no lead, so it is not drawn as a heading.
    public string StatusLead => Status.IndexOf(" · ", StringComparison.Ordinal) is var split and >= 0 ? Status[..split] : "";
    public string StatusRest => Status.IndexOf(" · ", StringComparison.Ordinal) is var split and >= 0 ? Status[split..] : Status;
    public ObservableCollection<SearchResultViewModel> Results { get; } = [];
    partial void OnIsBusyChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();
    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();
    private bool CanLoadMore() => HasMore && !IsBusy;

    public async Task SearchAsync(SearchRequest search)
    {
        Clear();
        if (search.Query.Length > 1000) { Status = "Keep your search under 1,001 characters."; return; }
        if (!search.CanSearch) { Status = "Enter words or choose a message filter to search."; return; }
        if (search.Filters is { From: { } from, Through: { } through } && from > through)
        { Status = "Choose an end date on or after the start date."; return; }
        active = search with { Query = search.Query.Trim() };
        await LoadAsync();
    }
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => LoadAsync();
    private async Task LoadAsync()
    {
        if (active is null || IsBusy) return;
        var version = generation;
        request?.Dispose(); request = new();
        IsBusy = true; Status = "Searching…";
        var query = active.Query;
        try
        {
            var page = await backend.SearchAsync(active, cursor, request.Token);
            if (version != generation) return;
            foreach (var hit in page.Items)
            {
                var existing = Results.FirstOrDefault(r => r.Hit.Chat.Id == hit.Chat.Id && r.Hit.Message?.Id == hit.Message?.Id);
                if (existing is not null) Results[Results.IndexOf(existing)] = new(hit) { Query = query };
                else if (Results.Count < 500) Results.Add(new(hit) { Query = query });
            }
            var stalled = page.HasMore && (page.OldestCursor is null || !cursors.Add(page.OldestCursor));
            cursor = page.OldestCursor;
            HasMore = page.HasMore && !stalled && Results.Count < 500;
            Status = stalled ? "Couldn't load more results. Search again to refresh." : Results.Count >= 500
                ? "Showing 500 results. Narrow your search to find more." : Results.Count == 0
                ? (HasMore ? "No results to show here yet. Load more." : "No results in the available history.")
                : $"{Results.Count} {(Results.Count == 1 ? "result" : "results")}" + (HasMore ? " · More available" : "");
            if (backend is ISearchScopeInfo scope) Status += " · " + scope.SearchScopeDescription;
            if (page.Warning is not null) Status += " · " + page.Warning;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) Status = ex is BackendException b ? b.Message : "Search failed. Check the connection and try again."; }
        finally { if (version == generation) IsBusy = false; }
    }
    public void Clear()
    {
        generation++; request?.Cancel(); active = null; cursor = null; cursors.Clear();
        Results.Clear(); IsBusy = false; HasMore = false;
        Status = "Search the history available in the connected service.";
    }
    public void Dispose() { Clear(); request?.Dispose(); }
}


