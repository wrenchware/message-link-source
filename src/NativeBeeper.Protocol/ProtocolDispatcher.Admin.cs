using System.Text.Json;

namespace NativeBeeper.Protocol;

// host.status, host.accounts, host.removeAccount (ProtocolMethods.Admin.cs) and devices.rename: served only to a connection that negotiated version 20, by a host
// that has the port. The two reads to any paired device, who reads every conversation anyway; a removal is the page's, and is refused to every device on a server. Who may rename
// a device is the devices port's rule, as for a revoke. A removal is journaled by its operation id and its answer follows the state it changed. A fixed code word from
// the host (AccountRemoveWords) reaches the caller as an account_failed error; nothing else of an exception does. Nothing is logged here but a refusal.
public sealed partial class ProtocolDispatcher
{
    private const string AdminRefusal = "Accounts are connected and removed on the server's page.";

    private static Dictionary<string, Route> AddAdminRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.HostStatusRead] = new(null, typeof(HostStatus), null, Own: (connection, _, _, ct) => connection.AdminReadAsync((port, token) => port.StatusAsync(token), ct));
        routes[ProtocolMethods.HostAccountsList] = new(null, typeof(IReadOnlyList<ServerAccount>), null,
            Own: (connection, _, _, ct) => connection.AdminReadAsync((port, token) => port.AccountsAsync(token), ct));
        routes[ProtocolMethods.HostAccountRemove] = new(typeof(AccountRemoveParams), typeof(AccountRemoved), null, (connection, services, request) =>
        {
            var parameters = ReadParams<AccountRemoveParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(AccountRemoveParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var port = services.Admin ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.ServerAdmin) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                return JsonSerializer.SerializeToUtf8Bytes(await Coded(() => port.RemoveAccountAsync(parameters.Provider, parameters.Key, cancellationToken)).ConfigureAwait(false),
                    ProtocolJson.Options);
            }, Before: _ =>
            {
                connection.RefuseOnServer("server admin", AdminRefusal);
                return Task.CompletedTask;
            });
        }, Settles: true);
        routes[ProtocolMethods.DevicesRename] = new(typeof(DeviceRenameParams), typeof(DeviceRenamed), null, Own: (connection, request, _, ct) => connection.RenameDeviceAsync(request, ct));
        return routes;
    }

    private async Task<byte[]> AdminReadAsync<TResult>(Func<IServerAdmin, CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
    {
        var port = services.Admin ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.ServerAdmin) throw new NotSupportedException();
        return JsonSerializer.SerializeToUtf8Bytes(await call(port, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
    }

    private async Task<byte[]> RenameDeviceAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var port = services.Devices ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.ServerAdmin) throw new NotSupportedException();
        var parameters = ReadParams<DeviceRenameParams>(request);
        return JsonSerializer.SerializeToUtf8Bytes(await port.RenameAsync(parameters.Id, parameters.Name, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
    }
}
