using System.Text.Json;

namespace MessageLink.Protocol;

public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddPushRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.PushOffer] = Of<IPushService, PushOffer>(s => s.Push, (port, ct) => port.OfferAsync(ct));
        routes[ProtocolMethods.PushRegister] = Of<IPushService, PushRegisterParams, Done>(s => s.Push, async (port, registration, ct) =>
        {
            await port.RegisterAsync(registration, ct).ConfigureAwait(false);
            return new Done();
        });
        // Protocol 27: served only to a connection that negotiated it; any device may ask either (a test is answered by the phone it went to, and the status
        // names only what devices.list already gives every device).
        routes[ProtocolMethods.PushTestShown] = new(typeof(PushTestShownParams), typeof(Done), null, Own: async (connection, request, _, ct) =>
        {
            var port = connection.PushTestPort();
            var shown = ReadParams<PushTestShownParams>(request);
            if (shown.Id is not { Length: > 0 and <= PushLimits.TestId })
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That isn't a test push's id."));
            await port.TestShownAsync(shown, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
        });
        routes[ProtocolMethods.PushStatusRead] = new(null, typeof(PushStatus), null, Own: async (connection, _, _, ct) =>
            JsonSerializer.SerializeToUtf8Bytes(await connection.PushTestPort().StatusAsync(ct).ConfigureAwait(false), ProtocolJson.Options));
        return routes;
    }

    private IPushService PushTestPort()
    {
        var port = services.Push ?? throw new NotSupportedException();
        return port;
    }
}
