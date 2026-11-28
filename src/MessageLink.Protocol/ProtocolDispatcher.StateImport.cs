namespace MessageLink.Protocol;

// state.import (ProtocolMethods.StateImport.cs): a journaled mutation like any other, so a repeat of one operation id answers the first answer and a lost answer is
// settled with ops.status. Only a connection that negotiated version 11 may ask: a client below it never does, and a host answers it as not supported rather
// than take in rows its client cannot read back. What it does is the host's (HostStateImport); nothing is logged here.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddStateImportRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.StateImport] = new(typeof(StateImportParams), typeof(StateImportResult), null, (connection, services, request) =>
        {
            var parameters = ReadParams<StateImportParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(StateImportParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var port = services.Imports ?? throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await port.ImportAsync(parameters, cancellationToken).ConfigureAwait(false), ProtocolJson.Options));
        });
        return routes;
    }
}
