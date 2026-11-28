namespace MessageLink.Protocol;

// devices.list, devices.revoke and devices.pairNew (ProtocolMethods.Devices.cs); who may revoke or pair is the port's rule. The port logs a revoke by the device's id; nothing is logged here.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddDeviceRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.DevicesList] = Of<IPairedDevices, IReadOnlyList<PairedDevice>>(s => s.Devices, (port, ct) => port.ListAsync(ct));
        routes[ProtocolMethods.DevicesRevoke] = Of<IPairedDevices, DeviceIdParams, DeviceRevoked>(s => s.Devices, (port, p, ct) => port.RevokeAsync(p.Id, ct));
        routes[ProtocolMethods.DevicesPairNew] = Of<IPairedDevices, PairingCodeIssued>(s => s.Devices, (port, ct) => port.PairNewAsync(ct));
        // From protocol 21; who may answer a request is the port's rule (the owner), as for a new code.
        routes[ProtocolMethods.DevicesJoinRequests] = new(null, typeof(IReadOnlyList<JoinRequest>), null, Own: (connection, _, _, ct) => connection.JoinCallAsync(port => port.JoinRequestsAsync(ct)));
        routes[ProtocolMethods.DevicesApproveJoin] = new(typeof(JoinIdParams), typeof(JoinApproved), null,
            Own: (connection, request, _, ct) => connection.JoinCallAsync(port => port.ApproveJoinAsync(ReadParams<JoinIdParams>(request).Id, ct)));
        routes[ProtocolMethods.DevicesDenyJoin] = new(typeof(JoinIdParams), typeof(JoinDenied), null,
            Own: (connection, request, _, ct) => connection.JoinCallAsync(port => port.DenyJoinAsync(ReadParams<JoinIdParams>(request).Id, ct)));
        // From protocol 27; only the owner gets one, as for a new code (the port's rule).
        routes[ProtocolMethods.DevicesJoinLink] = new(typeof(JoinLinkParams), typeof(JoinLinkIssued), null, Own: (connection, request, _, ct) =>
        {
            return connection.JoinCallAsync(port => port.JoinLinkAsync(ReadParams<JoinLinkParams>(request).Address, ct));
        });
        return routes;
    }

    private async Task<byte[]> JoinCallAsync<TResult>(Func<IPairedDevices, Task<TResult>> call)
    {
        var port = services.Devices ?? throw new NotSupportedException();
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await call(port).ConfigureAwait(false), ProtocolJson.Options);
    }
}
