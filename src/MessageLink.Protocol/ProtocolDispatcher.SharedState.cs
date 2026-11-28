namespace MessageLink.Protocol;

// state.sync, state.get and state.put (ProtocolMethods.SharedState.cs). Reads as far as the dispatcher is concerned: state.put is deduplicated by the store
// itself, by its clientOpId, and never through the OperationJournal, which is kept for what reaches a provider. They belong to the connection because the
// store is told the version this connection negotiated, and serves only the categories that version knows. Which keys may be stored is the store's rule
// (SharedCategories); nothing is logged here, and no key or value is. The keys are named as the connection's version names them (SharedCategories.ToWire:
// below 35 the refused sends are outbox:), here and in the shared event, so the store files every key by this build's names alone.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddSharedStateRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.StateSync] = new(typeof(StateSyncParams), typeof(StateSyncResult), null,
            Own: (connection, request, _, ct) => connection.SharedAsync<StateSyncParams, StateSyncResult>(request, async (port, p, version) =>
                await port.SyncAsync(p, version, ct).ConfigureAwait(false) is var result && SharedCategories.ToWire(result.Rows, version) is var rows && !ReferenceEquals(rows, result.Rows)
                    ? result with { Rows = rows } : result));
        routes[ProtocolMethods.StateGet] = new(typeof(StateGetParams), typeof(StateGetResult), null,
            Own: (connection, request, _, ct) => connection.SharedAsync<StateGetParams, StateGetResult>(request, async (port, p, version) =>
            {
                // Below 35 a key is read as that client means it: outbox: is a refused send. A refused: key names nothing it was ever answered for, so it is left out
                // before the store reads anything (the order, the first value and the byte budget are as they were). A batch the store would refuse goes to it as sent,
                // and a key at the length limit is passed on as sent (renamed it would be one character over).
                if (version >= ProtocolVersion.RefusedRows || p.Keys is not { Count: > 0 and <= SharedLimits.GetKeys } keys
                    || keys.Any(key => key is not { Length: > 0 and <= SharedLimits.KeyLength })) return await port.GetAsync(p, version, ct).ConfigureAwait(false);
                List<string> meant = [.. keys.Where(key => !key.StartsWith(SharedCategories.Refused + ":", StringComparison.Ordinal))
                    .Select(key => key.Length >= SharedLimits.KeyLength ? key : SharedCategories.FromWire(key, version))];
                // Nothing left to ask: answered as the store answers, with none of the rows it found.
                if (meant.Count == 0) return (await port.GetAsync(p, version, ct).ConfigureAwait(false)) with { Rows = [] };
                var result = await port.GetAsync(p with { Keys = meant }, version, ct).ConfigureAwait(false);
                return result.Rows is null ? result : result with { Rows = SharedCategories.ToWire(result.Rows, version) };
            }));
        // Not renamed: the refused sends are the host's alone, so a put naming one is refused under either name (the older one is no category now) with the key it
        // was sent, and the store's deduplication fingerprints the batch exactly as the client sent it, so a repeat after an update still finds its first answer.
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
    // knows, under the names it knows them by. A commit with none of them still goes out, empty, so the client's position follows the server's revs without a gap.
    private SharedChange SharedFor(SharedChange change)
    {
        var known = change.Rows.All(row => row.Since <= negotiated) ? change : change with { Rows = [.. change.Rows.Where(row => row.Since <= negotiated)] };
        var rows = SharedCategories.ToWire(known.Rows, negotiated);
        return ReferenceEquals(rows, known.Rows) ? known : known with { Rows = rows };
    }
}
