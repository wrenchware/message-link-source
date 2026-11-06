using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// A join connection (<see cref="NetworkPairing.JoinHeader"/>, protocol 21): it may say hello and then ask to join (<see cref="NetworkPairing.JoinStartMethod"/>), send its
/// nonce (<see cref="NetworkPairing.JoinNonceMethod"/>), wait for the owner (<see cref="NetworkPairing.JoinWaitMethod"/>) and say it kept its token
/// (<see cref="NetworkPairing.JoinAckMethod"/>), in that order, and nothing else; any other request is answered <c>forbidden</c> and ends it. Each step but the wait has
/// <see cref="StepDeadline"/> (an honest device takes moments), so a peer cannot sit in one of the door's few places; the wait lasts until the request ends. An approval
/// pairs the device here and hands it its token on this connection, and the device counts as joined only once it says it kept it: otherwise the device just added is
/// removed again, so an approval never leaves a device nobody holds the token of. Closing it withdraws the request. Logs nothing of what the device sent.
/// </summary>
internal static class JoinSession
{
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(IFrameChannel channel, JoinRequests.Request request, JoinRequests joins, DeviceRegistry devices, byte[] certificateHash, string version,
        Action<string> log, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(StepDeadline);
        var step = 0;
        try
        {
            while (await channel.Incoming.WaitToReadAsync(limit.Token).ConfigureAwait(false))
                while (channel.Incoming.TryRead(out var text))
                {
                    if (!Frames.TryParse(text, out var frame, out _)) return;
                    switch (frame)
                    {
                        case HelloFrame hello when step == 0:
                            // Only a client that knows asking to join gets further; an older one would never have opened this connection.
                            if (ProtocolVersion.Negotiate(hello.Protocol, hello.MinProtocol) is not { } chosen || chosen < ProtocolVersion.JoinRequests)
                            {
                                await channel.SendAsync(Frames.Serialize(new HelloFrame(ProtocolVersion.Current, version, Server: HelloFrame.ServerName, MinProtocol: ProtocolVersion.JoinRequests,
                                    Error: new ProtocolError(ProtocolErrorCodes.ProtocolUnsupported, "This version of Messages cannot ask this server to join."))), limit.Token).ConfigureAwait(false);
                                return;
                            }
                            request.Protocol = chosen;
                            step = 1;
                            await channel.SendAsync(Frames.Serialize(new HelloFrame(chosen, version, Server: HelloFrame.ServerName)), limit.Token).ConfigureAwait(false);
                            break;
                        case RequestFrame asked when step == 1 && asked.Method == NetworkPairing.JoinStartMethod:
                            joins.Start(request, Read<PairJoinParams>(asked)?.DeviceName);
                            step = 2;
                            await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(asked.Id, new PairJoinStarted(request.Id, JoinMatch.Commit(request.ServerNonce)))), limit.Token)
                                .ConfigureAwait(false);
                            break;
                        case RequestFrame nonce when step == 2 && nonce.Method == NetworkPairing.JoinNonceMethod:
                            if (Read<PairJoinNonceParams>(nonce) is not { } given || given.RequestId != request.Id || JoinMatch.ParseNonce(given.ClientNonce) is not { } clientNonce)
                            {
                                await RefuseAsync(channel, nonce, joins, "nonce", limit.Token).ConfigureAwait(false);
                                return;
                            }
                            joins.Match(request, clientNonce, certificateHash);
                            step = 3;
                            // The wait is asked for at once by a device that means it: the place is not held for one that never does.
                            limit.CancelAfter(StepDeadline);
                            var left = (int)Math.Max(0, Math.Ceiling((request.Expires - joins.Now).TotalSeconds));
                            await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(nonce.Id, new PairJoinNonce(Convert.ToHexStringLower(request.ServerNonce), left))), limit.Token)
                                .ConfigureAwait(false);
                            break;
                        case RequestFrame wait when step == 3 && wait.Method == NetworkPairing.JoinWaitMethod:
                            if (Read<PairJoinWaitParams>(wait)?.RequestId != request.Id)
                            {
                                await RefuseAsync(channel, wait, joins, "wait", limit.Token).ConfigureAwait(false);
                                return;
                            }
                            joins.Wait(request);
                            // From now on the connection lasts as long as the request does (and a little more for the answer to cross).
                            limit.CancelAfter(JoinRequests.Lifetime + TimeSpan.FromSeconds(30));
                            await WaitAsync(channel, wait, request, joins, devices, log, limit, limit.Token).ConfigureAwait(false);
                            return;
                        case RequestFrame other:
                            await RefuseAsync(channel, other, joins, "method", limit.Token).ConfigureAwait(false);
                            return;
                        default:
                            return;
                    }
                }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { }
        finally
        {
            joins.Leave(request);
            channel.Close();
        }
    }

    // Waits for the owner, or for the device to leave (its connection closing), whichever comes first; then, approved, pairs it and waits for it to say it kept its token.
    // A device of protocol 22 is first sent the finish code's proof, and paired only once it asks for its token after its person typed that code.
    private static async Task WaitAsync(IFrameChannel channel, RequestFrame wait, JoinRequests.Request request, JoinRequests joins, DeviceRegistry devices, Action<string> log,
        CancellationTokenSource limit, CancellationToken cancellationToken)
    {
        var decided = request.Decided.Task;
        Task first;
        // Read and dropped while it waits: a reader's Completion waits for what is queued to be read, so a frame sent now (a cancel) would hide the device leaving. Stopped
        // before the answer goes, so the device's acknowledgement after it is read below and not here.
        using (var draining = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var gone = DrainAsync(channel, draining.Token);
            first = await Task.WhenAny(decided, gone, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            draining.Cancel();
            await gone.ConfigureAwait(false);
        }
        if (first != decided)
        {
            log("server join outcome=withdrawn");
            return;
        }
        switch (decided.Result)
        {
            case JoinRequests.Outcome.Declined:
                log("server join outcome=declined");
                await Answer(wait, JoinWords.Declined).ConfigureAwait(false);
                return;
            case JoinRequests.Outcome.Expired:
                log("server join outcome=expired");
                await Answer(wait, JoinWords.Expired).ConfigureAwait(false);
                return;
            case JoinRequests.Outcome.Withdrawn:
                log("server join outcome=withdrawn");
                return;
        }
        var answering = wait;
        if (request.Protocol >= ProtocolVersion.JoinFinish)
        {
            if (await FinishAsync().ConfigureAwait(false) is not { } finish) return;
            answering = finish;
            // A code typed at the last moment still leaves the hand-over and its acknowledgement their own time.
            limit.CancelAfter(StepDeadline + StepDeadline);
        }
        (string Id, byte[] Token)? added;
        try { added = devices.BeginJoin(request.Name); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"server join outcome=failed type={ex.GetType().Name}");
            request.Delivered.TrySetResult(null);
            await Answer(answering, JoinWords.Failed).ConfigureAwait(false);
            return;
        }
        if (added is not { } device)
        {
            log("server join outcome=failed reason=full");
            request.Delivered.TrySetResult(null);
            await Answer(answering, JoinWords.Failed).ConfigureAwait(false);
            return;
        }
        try
        {
            // The approval stopped waiting while the device was added: the owner was told it did not join, so it does not.
            if (request.Delivered.Task.IsCompleted)
            {
                await RemoveAsync("late").ConfigureAwait(false);
                await Answer(answering, JoinWords.Failed).ConfigureAwait(false);
                return;
            }
            RequestFrame? ack = null;
            try
            {
                await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(answering.Id, new PairClaimed(device.Id, ClientToken.Encode(device.Token)))), cancellationToken).ConfigureAwait(false);
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                patience.CancelAfter(StepDeadline);
                if (await channel.Incoming.WaitToReadAsync(patience.Token).ConfigureAwait(false) && channel.Incoming.TryRead(out var text) && Frames.TryParse(text, out var frame, out _)
                    && frame is RequestFrame { Method: NetworkPairing.JoinAckMethod } said && Read<PairJoinAckParams>(said)?.RequestId == request.Id)
                    ack = said;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or System.Net.WebSockets.WebSocketException) { }
            // Not kept (gone, or could not save it), or kept after the approval stopped waiting: nobody is to hold that token, so the device goes again.
            if (ack is null)
            {
                await RemoveAsync("not_kept").ConfigureAwait(false);
                return;
            }
            try
            {
                if (!await devices.AcknowledgeJoinAsync(device.Id).ConfigureAwait(false))
                {
                    await RemoveAsync("not_kept").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"server join outcome=unknown reason=save type={ex.GetType().Name}");
                request.Delivered.TrySetResult(null);
                await Answer(ack, "Couldn't tell whether the server kept this device. Check the server's device list before trying again.").ConfigureAwait(false);
                return;
            }
            if (!request.Delivered.TrySetResult(device.Id))
            {
                var removed = await RemoveAsync("late").ConfigureAwait(false);
                await Answer(ack, removed ? JoinWords.Failed : "Couldn't tell whether the device was removed. Check the server's device list before trying again.").ConfigureAwait(false);
                return;
            }
            log($"server join outcome=approved id={device.Id}");
            try { await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(ack.Id, new Done())), cancellationToken).ConfigureAwait(false); }
            catch (Exception) { }
        }
        finally { devices.DropJoin(device.Id); }

        // The proof in place of the token; the device's own request for the token, or null when it never came (not typed in time, or the device left), and then nothing
        // is paired. The connection's limit grows by the time a person has to type.
        async Task<RequestFrame?> FinishAsync()
        {
            if (request.FinishCode is not { } code || request.CertificateHash is not { } certificate || request.ClientNonce is not { } nonce)
            {
                log("server join outcome=failed reason=no_code");
                request.Delivered.TrySetResult(null);
                await Answer(wait, JoinWords.Failed).ConfigureAwait(false);
                return null;
            }
            limit.CancelAfter(JoinRequests.FinishWait + StepDeadline);
            try
            {
                var proof = JoinMatch.FinishProof(code, certificate, nonce, request.ServerNonce, request.Id);
                await channel.SendAsync(Frames.Serialize(ResponseFrame.Success(wait.Id, new PairJoinApproved(proof, (int)JoinRequests.FinishWait.TotalSeconds))), cancellationToken)
                    .ConfigureAwait(false);
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                patience.CancelAfter(JoinRequests.FinishWait);
                if (await channel.Incoming.WaitToReadAsync(patience.Token).ConfigureAwait(false) && channel.Incoming.TryRead(out var text) && Frames.TryParse(text, out var frame, out _)
                    && frame is RequestFrame { Method: NetworkPairing.JoinFinishMethod } said && Read<PairJoinFinishParams>(said)?.RequestId == request.Id)
                    return said;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or System.Net.WebSockets.WebSocketException) { }
            log("server join outcome=unfinished");
            request.Delivered.TrySetResult(null);
            return null;
        }

        async Task<bool> RemoveAsync(string reason)
        {
            devices.DropJoin(device.Id);
            try { await devices.RevokeAsync(device.Id).ConfigureAwait(false); }
            catch (Exception)
            {
                log($"server join outcome=unknown reason=remove_{reason}");
                request.Delivered.TrySetResult(null);
                return false;
            }
            log($"server join outcome=failed reason={reason}");
            request.Delivered.TrySetResult(null);
            return true;
        }

        async Task Answer(RequestFrame asked, string word)
        {
            try { await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(asked.Id, new ProtocolError(ProtocolErrorCodes.AccountFailed, word))), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or System.Net.WebSockets.WebSocketException) { }
        }
    }

    private static async Task DrainAsync(IFrameChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.Incoming.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (channel.Incoming.TryRead(out _)) { }
        }
        catch (Exception) { }
    }

    private static async Task RefuseAsync(IFrameChannel channel, RequestFrame request, JoinRequests joins, string reason, CancellationToken cancellationToken)
    {
        joins.Refused(reason);
        await channel.SendAsync(Frames.Serialize(ResponseFrame.Failure(request.Id, new ProtocolError(ProtocolErrorCodes.Forbidden, "This connection may only ask to join."))),
            cancellationToken).ConfigureAwait(false);
    }

    // Params as sent, or null when there are none or they are not that shape.
    private static T? Read<T>(RequestFrame request) where T : class
    {
        if (!request.HasParams) return null;
        try { return request.ParamsAs<T>(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }
}
