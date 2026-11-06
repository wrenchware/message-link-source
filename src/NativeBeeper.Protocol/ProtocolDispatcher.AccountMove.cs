using System.Text.Json;

namespace NativeBeeper.Protocol;

// accounts.move.* (ProtocolMethods.AccountMove.cs): what the server's IAccountMove does, served only to a connection that negotiated version 16 (a client below it never asks,
// and a host answers it as not supported rather than take an account its client cannot read back). The writers are journaled by the caller's operation id, so a repeat of one
// answers the first answer; the history pages are not (each is applied once by its number). A fixed code word from the host (an InvalidOperationException) reaches the caller as
// an account_failed error, as a sign-in's does; nothing else of an exception does.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddAccountMoveRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.AccountMoveAuthorize] = new(typeof(AccountMoveAuthorizeParams), typeof(AccountMoveAuthorized), null, Own: (connection, request, _, cancellationToken) => connection.AuthorizeMoveAsync(request, cancellationToken));
        routes[ProtocolMethods.AccountMoveStatus] = MoveRead<AccountMoveStatusParams, AccountMoveStatus>((port, p, ct) => port.StatusAsync(p, ct));
        routes[ProtocolMethods.AccountMoveHistoryPut] = MoveRead<MoveHistoryPutParams, MoveHistoryPutResult>((port, p, ct) => port.PutHistoryAsync(p, ct));
        routes[ProtocolMethods.AccountMoveRead] = MoveRead<AccountMoveReadParams, AccountMoveChunk>((port, p, ct) => port.ReadAsync(p, ct));
        routes[ProtocolMethods.AccountMoveHistoryGet] = MoveRead<AccountMoveHistoryGetParams, AccountMoveHistoryPage>((port, p, ct) => port.GetHistoryAsync(p, ct));

        routes[ProtocolMethods.AccountMoveStage] = MoveMutation<AccountMoveStageParams, MovePiece>((port, p, ct) => port.StageAsync(p, ct));
        routes[ProtocolMethods.AccountMoveVerify] = MoveMutation<AccountMoveVerifyParams, AccountMoveVerified>((port, p, ct) => port.VerifyAsync(p, ct));
        routes[ProtocolMethods.AccountMoveActivate] = MoveMutation<AccountMoveActivateParams, AccountMoveActivated>((port, p, ct) => port.ActivateAsync(p, ct));
        routes[ProtocolMethods.AccountMoveAbort] = MoveMutation<AccountMoveAbortParams, AccountMoveStatus>((port, p, ct) => port.AbortAsync(p, ct));
        routes[ProtocolMethods.AccountMoveHold] = MoveMutation<AccountMoveHoldParams, AccountMoveStatus>((port, p, ct) => port.HoldAsync(p, ct),
            before: (connection, port, p, ct) => connection.OlderClientMayHoldAsync(port, p, ct));
        routes[ProtocolMethods.AccountMoveRelease] = MoveMutation<AccountMoveReleaseParams, AccountMoveStatus>((port, p, ct) => port.ReleaseAsync(p, ct),
            before: (connection, port, p, ct) => connection.OlderClientMayReadAsync(port, p.MoveId, p.Providers, ct));
        routes[ProtocolMethods.AccountMoveResume] = MoveMutation<AccountMoveResumeParams, AccountMoveStatus>((port, p, ct) => port.ResumeAsync(p, ct), ProtocolVersion.DeviceSchema);
        return routes;
    }

    private static Route MoveRead<TParams, TResult>(Func<IAccountMove, TParams, CancellationToken, Task<TResult>> call) where TParams : class
        => new(typeof(TParams), typeof(TResult), null, Own: (connection, request, _, cancellationToken) => connection.MoveReadAsync(request, call, cancellationToken));

    private async Task<byte[]> MoveReadAsync<TParams, TResult>(RequestFrame request, Func<IAccountMove, TParams, CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
        where TParams : class
    {
        var port = services.AccountMove ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.AccountMove) throw new NotSupportedException();
        var parameters = ReadParams<TParams>(request);
        if (parameters is AccountMoveReadParams piece) parameters = (TParams)(object)(piece with { ClientProtocol = negotiated });
        if (parameters is AccountMoveHistoryGetParams page) parameters = (TParams)(object)(page with { ClientProtocol = negotiated });
        // What the server holds of each account says nothing of them, so any paired device may read it; the rest needs the password.
        if (parameters is not AccountMoveStatusParams) await RequireMovePasswordAsync(cancellationToken).ConfigureAwait(false);
        // A legacy move-back ignores a failed hold if status still says held/stopped. Guard the bytes too, including a hold made by another client version.
        if (parameters is AccountMoveReadParams read) await Coded(async () => { await OlderClientMayReadAsync(port, read.MoveId, [read.Provider], cancellationToken); return true; }).ConfigureAwait(false);
        if (parameters is AccountMoveHistoryGetParams history) await Coded(async () => { await OlderClientMayReadAsync(port, history.MoveId, [history.Provider], cancellationToken); return true; }).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(await Coded(() => call(port, parameters, cancellationToken)).ConfigureAwait(false), ProtocolJson.Options);
    }

    // `since`: the version a writer was added in (resume came with 19); a connection that negotiated less is answered as not supported. `before`: what is looked at first, after the
    // owner check, with the connection in hand; a code word it throws is the answer, and the writer is not reached.
    private static Route MoveMutation<TParams, TResult>(Func<IAccountMove, TParams, CancellationToken, Task<TResult>> call, int since = ProtocolVersion.AccountMove,
        Func<ProtocolDispatcher, IAccountMove, TParams, CancellationToken, Task>? before = null) where TParams : MutationParams
        => new(typeof(TParams), typeof(TResult), null, (connection, services, request) =>
        {
            var parameters = ReadParams<TParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(TParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var port = services.AccountMove ?? throw new NotSupportedException();
            if (connection.negotiated < since) throw new NotSupportedException();
            if (parameters is AccountMoveHoldParams hold) parameters = (TParams)(object)(hold with { ClientProtocol = connection.negotiated });
            if (parameters is AccountMoveReleaseParams release) parameters = (TParams)(object)(release with { ClientProtocol = connection.negotiated });
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                return JsonSerializer.SerializeToUtf8Bytes(await Coded(() => call(port, parameters, cancellationToken)).ConfigureAwait(false), ProtocolJson.Options);
            }, Before: async cancellationToken =>
            {
                await connection.RequireMovePasswordAsync(cancellationToken).ConfigureAwait(false);
                if (before is not null) await Coded(async () => { await before(connection, port, parameters, cancellationToken).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            });
        }, Settles: true);

    // A client below 19 takes home what it is given without checking its format (the check came with 19), and the server updates nightly while the PC waits for a release: a Messenger
    // device database or an Instagram session newer than its helpers read would be installed there and refused, after the server let its own copies go. Its hold is refused instead,
    // at admission and again on the stopped copy (LegacyMoveFormats); a hold that caught an upgrade is undone and its accounts restored. A hold that names no
    // account holds every running or held one (AccountMoveHost.HoldAsync), so every one is looked at. A format the status does not report is let through, as a client of 19
    // lets it through: a Messenger with no device database (encryption never turned on), and an Instagram envelope that names no version, which no newer helper writes
    // (InstagramEnvelope.VersionOf). Any version but 2 is refused, older too, since that release's helper resumes 2 alone.
    // Admission checks the current status on every call, including journal replays; the move host checks again after stopping, under its file/lifecycle locks.
    private Task OlderClientMayHoldAsync(IAccountMove port, AccountMoveHoldParams request, CancellationToken cancellationToken) =>
        OlderClientMayReadAsync(port, request.MoveId, request.Providers, cancellationToken);

    private async Task OlderClientMayReadAsync(IAccountMove port, string moveId, IReadOnlyList<string> providers, CancellationToken cancellationToken)
    {
        if (negotiated >= ProtocolVersion.DeviceSchema) return;
        var status = await port.StatusAsync(new AccountMoveStatusParams(moveId), cancellationToken).ConfigureAwait(false);
        foreach (var provider in status.Providers.Where(provider => providers.Count == 0 || providers.Contains(provider.Provider, StringComparer.Ordinal)))
        {
            var tooNew = provider.Provider == MoveProviders.Messenger && provider.DeviceSchema > LegacyMoveFormats.DeviceSchema
                || provider.Provider == MoveProviders.Instagram && provider.SessionVersion is { } version && version != LegacyMoveFormats.SessionVersion;
            if (!tooNew) continue;
            log?.Invoke($"protocol account move hold refused reason=too_new_for_client provider={provider.Provider} client={negotiated}");
            throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
        }
    }

    // Everything the account move carries is cookies, device keys and message history, and what it hands over outlives removing a device: so the server's password is asked, not just
    // a paired device's token (owner, 2026-11-03). A host with no paired devices (the app's own host, a local pipe, the loopback) has one user. Checked before the host's port is reached.
    private const string MoveRefusal = "Moving accounts needs the server's password. Update Messages if it didn't ask for it.";

    private async Task RequireMovePasswordAsync(CancellationToken cancellationToken)
    {
        if (services.Devices is not { } devices || await devices.MayMoveAsync(cancellationToken).ConfigureAwait(false)) return;
        log?.Invoke("protocol account move refused reason=no_password");
        throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden, MoveRefusal);
    }

    // The password itself, which only the device's port sees: it is not journaled, logged or answered back.
    private async Task<byte[]> AuthorizeMoveAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        if (negotiated < ProtocolVersion.MovePassword) throw new NotSupportedException();
        var parameters = ReadParams<AccountMoveAuthorizeParams>(request);
        var outcome = services.Devices is { } devices ? await devices.AuthorizeMoveAsync(parameters.Password ?? "", cancellationToken).ConfigureAwait(false) : AccountMoveAuthorizeWords.Right;
        return JsonSerializer.SerializeToUtf8Bytes(new AccountMoveAuthorized(outcome), ProtocolJson.Options);
    }

    // What a server's page does, refused to any device on its door (owner, 2026-11-03): `what` is the log line's fixed words, `sentence` the refusal's, and `method`, when given, the
    // route's own name (a constant, never what the peer sent), added to the line. A host with no paired devices has one user, who goes on as before.
    private void RefuseOnServer(string what, string sentence, string? method = null)
    {
        if (services.Devices is null) return;
        log?.Invoke(method is null ? $"protocol {what} refused reason=on_page" : $"protocol {what} refused reason=on_page method={method}");
        throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden, sentence);
    }
}
