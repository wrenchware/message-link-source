using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// <c>host.shutdown</c> as a client asks it: one request, never repeated here. A connection that ends under it is not an
/// error to word: the server closes it as part of stopping, so a client that wants to know checks that the server is gone.
/// </summary>
public sealed class ServerShutdownProxy(ProtocolClient client) : IServerShutdown
{
    public Task<ServerShutdownResult> RequestAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<ServerShutdownResult>(ProtocolMethods.ServerShutdown, cancellationToken);
}
