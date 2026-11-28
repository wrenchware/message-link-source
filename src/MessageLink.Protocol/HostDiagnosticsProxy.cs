using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The host's diagnostics as the client reads them (SWITCHBOARD.md 3.0b, <c>host.diagnostics</c>): one call per read, nothing kept. A
/// caller that wants a synchronous view of it (the console's <c>/top</c>, which asks once a second) reads through
/// <c>HostDiagnosticsReader</c>, which holds the last answer.
/// </summary>
public sealed class HostDiagnosticsProxy(ProtocolClient client) : IHostDiagnostics
{
    public Task<HostDiagnostics> ReadAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<HostDiagnostics>(ProtocolMethods.HostDiagnosticsRead, cancellationToken);
}
