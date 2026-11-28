using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// host.shutdown (ProtocolMethods.Shutdown.cs). The answer is one fixed word and nothing is logged here: the host's port logs the outcome.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddShutdownRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.ServerShutdown] = Of<IServerShutdown, ServerShutdownResult>(s => s.Shutdown, (port, ct) => port.RequestAsync(ct));
        return routes;
    }
}
