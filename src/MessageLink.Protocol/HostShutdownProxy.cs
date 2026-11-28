using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// <c>host.shutdown</c> as a client asks it (SWITCHBOARD.md 3.2c): one request, never repeated here. A connection that ends under it is not an
/// error to word: the host closes it as part of stopping, so a client that wants to know checks that the host is gone.
/// </summary>
public sealed class HostShutdownProxy(ProtocolClient client) : IHostShutdown
{
    public Task<HostShutdownResult> RequestAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<HostShutdownResult>(ProtocolMethods.HostShutdown, cancellationToken);
}
