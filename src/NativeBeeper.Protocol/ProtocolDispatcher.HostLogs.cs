using System.Text.Json;

namespace NativeBeeper.Protocol;

// host.logs (ProtocolMethods.HostLogs.cs): served only to a connection that negotiated version 18, by a host that keeps logs to give, and only to the owner
// device (checked before the port is reached, as for the account move). A name off the fixed list never reaches the port at all. Nothing is logged but a refusal.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddHostLogsRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.HostLogsRead] = new(typeof(HostLogReadParams), typeof(HostLogChunk), null, Own: (connection, request, _, ct) => connection.ReadHostLogAsync(request, ct));
        return routes;
    }

    private async Task<byte[]> ReadHostLogAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var port = services.Logs ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.ServerLogs) throw new NotSupportedException();
        var parameters = ReadParams<HostLogReadParams>(request);
        var chunk = HostLogNames.IsKnown(parameters.Name) && parameters.Offset >= 0
            ? await port.ReadAsync(parameters.Name, parameters.Offset, cancellationToken).ConfigureAwait(false)
            : HostLogChunk.Missing;
        return JsonSerializer.SerializeToUtf8Bytes(chunk, ProtocolJson.Options);
    }
}
