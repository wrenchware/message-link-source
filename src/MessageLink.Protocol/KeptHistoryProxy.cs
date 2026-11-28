using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The host's kept history for the client's universal search (SWITCHBOARD.md 3.0b): <c>search.query</c> with the kept source, and a warm-up
/// that is sent without waiting. The store is read only on the host, so the matches, their order and their number are the host's own; a host
/// that cannot answer fails the call, which the universal search already treats as no kept results.
/// </summary>
public sealed class KeptHistoryProxy(ProtocolClient client) : IKeptHistorySearch
{
    // What the kept source holds is Messenger's own store (SearchQueryParams.KeptSource); the scope line says so.
    public string Covers => "Messenger";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
        (await client.CallAsync<SearchQueryParams, Page<SearchHit>>(ProtocolMethods.SearchQuery,
            new(request, null, SearchQueryParams.KeptSource), cancellationToken).ConfigureAwait(false)).Items;

    // Best effort: the first search simply waits for the read itself when this did not get there.
    public void Warm() => _ = Task.Run(async () =>
    {
        try { await client.CallAsync<Done>(ProtocolMethods.SearchWarmKept).ConfigureAwait(false); }
        catch (Exception) { /* a warm-up that did not start is only slower */ }
    });
}
