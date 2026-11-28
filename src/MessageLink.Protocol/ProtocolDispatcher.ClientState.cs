using System.Text.Json;

namespace MessageLink.Protocol;

// client.state (ProtocolMethods.ClientState.cs). Nothing it carries is logged here: the sink logs the combined view as flags and service words.
public sealed partial class ProtocolDispatcher
{
    /// <summary>The most services a client may name; there are four.</summary>
    public const int MaxClientStateServices = 16;

    /// <summary>The longest service word a client may state (the real ones are under 16).</summary>
    public const int MaxClientStateWord = 64;

    private static Dictionary<string, Route> AddClientStateRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.ClientStateSet] = new(typeof(ClientStateParams), typeof(Done), null, Own: (connection, request, _, _) => connection.SetClientState(request));
        return routes;
    }

    internal Task<byte[]> SetClientState(RequestFrame request)
    {
        var parameters = ReadParams<ClientStateParams>(request);
        if (!ParamsShape.HasRequiredValues(typeof(ClientStateParams), request.Params) || parameters.PrivateReading.Count > MaxClientStateServices
            || parameters.PrivateReading.Any(word => word is not { Length: > 0 and <= MaxClientStateWord }))
            throw Invalid();
        var sink = services.ClientState ?? throw new NotSupportedException();
        // Requests run on their own tasks, so this can land after the read loop has ended the connection and told the sink it is gone;
        // applying then would count a connection that no longer exists, for good.
        lock (clientStateGate)
        {
            if (!clientStateEnded) sink.Apply(this, parameters);
        }
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options));
    }

    private readonly object clientStateGate = new();
    private bool clientStateEnded;

    // What this connection stated no longer counts, and nothing it still has in flight can make it count again.
    internal void EndClientState()
    {
        lock (clientStateGate)
        {
            clientStateEnded = true;
            services.ClientState?.Disconnected(this);
        }
    }
}
