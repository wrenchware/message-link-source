namespace MessageLink.Protocol;

/// <summary>A server's administration as its owner asks for it (<c>host.status</c>, <c>host.accounts</c>, <c>host.removeAccount</c>, protocol 20). Nothing is kept.</summary>
public sealed class ServerAdminProxy(ProtocolClient client) : IServerAdmin
{
    public Task<ServerStatus> StatusAsync(CancellationToken cancellationToken = default) => client.CallAsync<ServerStatus>(ProtocolMethods.ServerStatusRead, cancellationToken);

    public Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<ServerAccount>>(ProtocolMethods.ServerAccountsList, cancellationToken);

    /// <summary>One operation id per call: a removal is one person's one choice, and a repeat is a new choice. An interrupted one is an unknown outcome, never repeated here.</summary>
    public Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountRemoveParams, AccountRemoved>(ProtocolMethods.ServerAccountRemove, new AccountRemoveParams(Guid.NewGuid().ToString("N"), provider, key), cancellationToken);
}

/// <summary>A server's paired devices as a device asks for them (<c>devices.*</c>; <c>devices.rename</c> from protocol 20). Nothing is kept.</summary>
public sealed class PairedDevicesProxy(ProtocolClient client) : IPairedDevices
{
    public Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<PairedDevice>>(ProtocolMethods.DevicesList, cancellationToken);

    public Task<DeviceRevoked> RevokeAsync(string id, CancellationToken cancellationToken = default) =>
        client.CallAsync<DeviceIdParams, DeviceRevoked>(ProtocolMethods.DevicesRevoke, new DeviceIdParams(id), cancellationToken);

    public Task<PairingCodeIssued> PairNewAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<PairingCodeIssued>(ProtocolMethods.DevicesPairNew, cancellationToken);

    public Task<DeviceRenamed> RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        client.CallAsync<DeviceRenameParams, DeviceRenamed>(ProtocolMethods.DevicesRename, new DeviceRenameParams(id, name), cancellationToken);

    public Task<IReadOnlyList<JoinRequest>> JoinRequestsAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<JoinRequest>>(ProtocolMethods.DevicesJoinRequests, cancellationToken);

    public Task<JoinApproved> ApproveJoinAsync(string id, CancellationToken cancellationToken = default) =>
        client.CallAsync<JoinIdParams, JoinApproved>(ProtocolMethods.DevicesApproveJoin, new JoinIdParams(id), cancellationToken);

    public Task<JoinDenied> DenyJoinAsync(string id, CancellationToken cancellationToken = default) =>
        client.CallAsync<JoinIdParams, JoinDenied>(ProtocolMethods.DevicesDenyJoin, new JoinIdParams(id), cancellationToken);

    public Task<JoinLinkIssued> JoinLinkAsync(string? address, CancellationToken cancellationToken = default) =>
        client.CallAsync<JoinLinkParams, JoinLinkIssued>(ProtocolMethods.DevicesJoinLink, new JoinLinkParams(address), cancellationToken);
}
