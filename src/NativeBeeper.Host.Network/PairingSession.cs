using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// A connection that came in with the pairing code (<see cref="NetworkPairing"/>): it may say hello and make one <c>pair.claim</c>, and then it is closed. It
/// never reaches a dispatcher, so nothing else of the protocol is served to it: any other request is answered <c>forbidden</c> and ends it, and so does a
/// claim that names a code other than the one the connection was let in with. It has <see cref="Deadline"/> in all. Logs only
/// <c>server pairing refused reason=code|full|method</c> and the registry's <c>server device paired id=</c>; never the code, the name or the token.
/// </summary>
internal static class PairingSession
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(IFrameChannel channel, byte[] code, DeviceRegistry devices, string version, Action<string> log, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(Deadline);
        var greeted = false;
        try
        {
            while (await channel.Incoming.WaitToReadAsync(limit.Token).ConfigureAwait(false))
                while (channel.Incoming.TryRead(out var text))
                {
                    if (!Frames.TryParse(text, out var frame, out _)) return;
                    switch (frame)
                    {
                        case HelloFrame hello:
                            if (ProtocolVersion.Negotiate(hello.Protocol, hello.MinProtocol) is not { } chosen)
                            {
                                await channel.SendAsync(Frames.Serialize(new HelloFrame(ProtocolVersion.Current, version, Server: HelloFrame.ServerName, MinProtocol: ProtocolVersion.Minimum,
                                    Error: new ProtocolError(ProtocolErrorCodes.ProtocolUnsupported, "This version of Messages cannot talk to that server."))), limit.Token).ConfigureAwait(false);
                                return;
                            }
                            greeted = true;
                            await channel.SendAsync(Frames.Serialize(new HelloFrame(chosen, version, Server: HelloFrame.ServerName)), limit.Token).ConfigureAwait(false);
                            break;
                        case RequestFrame request when greeted && request.Method == NetworkPairing.ClaimMethod:
                            await ClaimAsync(channel, request, code, devices, log, limit.Token).ConfigureAwait(false);
                            return;
                        case RequestFrame request:
                            log("server pairing refused reason=method");
                            await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(request.Id, Forbidden("This connection may only pair."))), limit.Token).ConfigureAwait(false);
                            return;
                        default:
                            return;
                    }
                }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { }
        finally { channel.Close(); }
    }

    private static async Task ClaimAsync(IFrameChannel channel, RequestFrame request, byte[] code, DeviceRegistry devices, Action<string> log, CancellationToken cancellationToken)
    {
        PairClaimParams? claim = null;
        try { claim = request.ParamsAs<PairClaimParams>(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        if (claim is null || !PairingCodes.Same(code, claim.Code))
        {
            log("server pairing refused reason=code");
            await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(request.Id, Forbidden("That pairing code was not accepted."))), cancellationToken).ConfigureAwait(false);
            return;
        }
        (string Id, byte[] Token)? added;
        try { added = await devices.AddAsync(claim.DeviceName).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"server pairing failed type={ex.GetType().Name}");
            await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(request.Id, ProtocolErrors.From(ex))), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (added is not { } device)
        {
            log("server pairing refused reason=full");
            await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(request.Id, Forbidden("This server has as many devices as it keeps. Revoke one first."))), cancellationToken).ConfigureAwait(false);
            return;
        }
        await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(request.Id, new PairClaimed(device.Id, ClientToken.Encode(device.Token)))), cancellationToken).ConfigureAwait(false);
    }

    private static ProtocolError Forbidden(string message) => new(ProtocolErrorCodes.Forbidden, message);
}
