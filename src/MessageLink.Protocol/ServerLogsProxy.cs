namespace MessageLink.Protocol;

/// <summary>The server's logs as its owner reads them (<c>host.logs</c>, protocol 18): one call per chunk, nothing kept.</summary>
public sealed class ServerLogsProxy(ProtocolClient client) : IServerLogs
{
    public Task<ServerLogChunk> ReadAsync(string name, long offset, CancellationToken cancellationToken = default) =>
        client.CallAsync<ServerLogReadParams, ServerLogChunk>(ProtocolMethods.ServerLogsRead, new ServerLogReadParams(name, offset), cancellationToken);
}
