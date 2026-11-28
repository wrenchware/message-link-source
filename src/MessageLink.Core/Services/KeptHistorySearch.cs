namespace MessageLink.Core.Services;

// History a provider's own helper keeps on this PC, searched here without the network: Messenger's messages.db today.
// It answers every match at once, newest first, and the universal search merges those with what this session loaded.
// A source that cannot be read at the moment (busy, missing, another account's) answers with nothing rather than an
// error, so the other services' results are never held up or replaced by a failure notice.
public interface IKeptHistorySearch
{
    // Which services this covers, for the search scope line ("Messenger").
    string Covers { get; }
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    // Starts the first read in the background, so the first search does not wait for the whole of it. Best effort.
    void Warm() { }
}
