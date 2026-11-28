using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The server's diagnostics as the client reads them (<c>host.diagnostics</c>): one call per read, nothing kept. A
/// caller that wants a synchronous view of it (the console's <c>/top</c>, which asks once a second) reads through
/// <c>ServerDiagnosticsReader</c>, which holds the last answer.
/// </summary>
public sealed class ServerDiagnosticsProxy(ProtocolClient client) : IServerDiagnostics
{
    public Task<ServerDiagnostics> ReadAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<ServerDiagnostics>(ProtocolMethods.ServerDiagnosticsRead, cancellationToken);
}
