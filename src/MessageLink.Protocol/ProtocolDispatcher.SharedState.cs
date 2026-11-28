namespace MessageLink.Protocol;

// state.sync, state.get and state.put (ProtocolMethods.SharedState.cs). Reads as far as the dispatcher is concerned: state.put is deduplicated by the store
// itself, by its clientOpId, and never through the OperationJournal, which is kept for what reaches a provider. They belong to the connection because the
// store is told the version this connection negotiated, and serves only the categories that version knows. Which keys may be stored is the store's rule
// (SharedCategories); nothing is logged here, and no key or value is.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddSharedStateRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.StateSync] = new(typeof(StateSyncParams), typeof(StateSyncResult), null,
            Own: (connection, request, _, ct) => connection.SharedAsync<StateSyncParams, StateSyncResult>(request, (port, p, version) => port.SyncAsync(p, version, ct)));
        routes[ProtocolMethods.StateGet] = new(typeof(StateGetParams), typeof(StateGetResult), null,
            Own: (connection, request, _, ct) => connection.SharedAsync<StateGetParams, StateGetResult>(request, (port, p, version) => port.GetAsync(p, version, ct)));
        routes[ProtocolMethods.StatePut] = new(typeof(StatePutParams), typeof(StatePutResult), null,
            Own: (connection, request, _, ct) => connection.SharedAsync<StatePutParams, StatePutResult>(request, (port, p, version) => port.PutAsync(p, version, ct)));
        return routes;
    }

    private async Task<byte[]> SharedAsync<TParams, TResult>(RequestFrame request, Func<ISharedState, TParams, int, Task<TResult>> call) where TParams : class
    {
        var parameters = ReadParams<TParams>(request);
        var port = services.SharedState ?? throw new NotSupportedException();
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await call(port, parameters, negotiated).ConfigureAwait(false), ProtocolJson.Options);
    }

    // A commit as this connection may see it (none below version 2, which has no shared kind: EventKinds.Since): only the rows of the categories its version
    // knows. A commit with none of them still goes out, empty, so the client's position follows the server's revs without a gap.
    private SharedChange SharedFor(SharedChange change) =>
        change.Rows.All(row => row.Since <= negotiated) ? change
        : change with { Rows = [.. change.Rows.Where(row => row.Since <= negotiated)] };
}
