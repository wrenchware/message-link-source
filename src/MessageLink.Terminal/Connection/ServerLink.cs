using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Terminal.Connection;

/// <summary>
/// A connection to one Message Link Server: the client every proxy calls through, the server's pushed state as it holds it, which server it is (the
/// replica of its shared rows is filed under <see cref="ServerId"/>) and how a person would name it. Connects when first used and again after a loss,
/// like the app's; a call made while it is down fails and is never queued.
/// </summary>
/// <param name="ServerId"><c>local</c> for this PC's own server over the pipe, else the pinned certificate's fingerprint (hex, lower case).</param>
/// <param name="Name">"this PC's server", or the server's address as the person gave it. Never a token.</param>
/// <param name="OnThisMachine">The server runs on this machine (the pipe, or 127.0.0.1 inside the server's own container).</param>
public sealed class ServerLink(ProtocolClient client, HostStateCache state, string serverId, string name, bool onThisMachine) : IAsyncDisposable
{
    public ProtocolClient Client { get; } = client;
    public HostStateCache State { get; } = state;
    public string ServerId { get; } = serverId;
    public string Name { get; } = name;
    public bool OnThisMachine { get; } = onThisMachine;

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}
