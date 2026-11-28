using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// host.diagnostics (ProtocolMethods.Diagnostics.cs). The answer is built by the port and carries no name, number, text or id of a person
// or a conversation; nothing here adds to it and nothing is logged.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddDiagnosticsRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.HostDiagnosticsRead] = Of<IHostDiagnostics, HostDiagnostics>(s => s.Diagnostics, (port, ct) => port.ReadAsync(ct));
        return routes;
    }
}
