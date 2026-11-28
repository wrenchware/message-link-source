namespace MessageLink.Protocol;

/// <summary>The server's logs as its owner reads them (<c>host.logs</c>, protocol 18): one call per chunk, nothing kept.</summary>
public sealed class HostLogsProxy(ProtocolClient client) : IHostLogs
{
    public Task<HostLogChunk> ReadAsync(string name, long offset, CancellationToken cancellationToken = default) =>
        client.CallAsync<HostLogReadParams, HostLogChunk>(ProtocolMethods.HostLogsRead, new HostLogReadParams(name, offset), cancellationToken);
}
