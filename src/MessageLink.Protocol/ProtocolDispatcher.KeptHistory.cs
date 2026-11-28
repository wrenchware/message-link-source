using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// search.query's two sources (ProtocolMethods.KeptHistory.cs): the provider's archive, and the server's kept history. A source this server does
// not have is not_supported, and one nobody knows is invalid_request.
public sealed partial class ProtocolDispatcher
{
    private static Route SearchRoute() => new(typeof(SearchQueryParams), typeof(Page<SearchHit>), async (services, _, request, cancellationToken) =>
    {
        var parameters = ReadParams<SearchQueryParams>(request);
        Page<SearchHit> page;
        if (parameters.Source is null)
            page = await (services.Search ?? throw new NotSupportedException()).SearchAsync(parameters.Request, parameters.Cursor, cancellationToken).ConfigureAwait(false);
        else if (parameters.Source == SearchQueryParams.KeptSource && parameters.Cursor is null)
            page = new(await (services.KeptHistory ?? throw new NotSupportedException()).SearchAsync(parameters.Request, cancellationToken).ConfigureAwait(false), false, null, null);
        else throw Invalid();
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options);
    });

    private static Route WarmKeptRoute() => Of<IKeptHistorySearch, Done>(s => s.KeptHistory, (port, _) =>
    {
        port.Warm();
        return Task.FromResult(new Done());
    });
}
