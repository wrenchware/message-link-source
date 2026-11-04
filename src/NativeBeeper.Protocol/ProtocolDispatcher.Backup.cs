using System.Text.Json;

namespace NativeBeeper.Protocol;

// server.backup.* (ProtocolMethods.Backup.cs): served only to a connection that negotiated version 26, by a host that has the port. The status goes to any device;
// a run and a password are the page's, refused to every device on a server before the port is reached. A password outside the limits is answered invalid_request, and nothing of it is
// logged here or written to the journal (these routes are not journaled).
public sealed partial class ProtocolDispatcher
{
    private const string BackupRefusal = "Backups are managed on the server's page.";

    private static Dictionary<string, Route> AddBackupRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.ServerBackupStatusRead] = new(null, typeof(ServerBackupStatus), null, Own: async (connection, _, _, ct) =>
        {
            var port = connection.BackupPort();
            return JsonSerializer.SerializeToUtf8Bytes(await port.StatusAsync(ct).ConfigureAwait(false), ProtocolJson.Options);
        });
        routes[ProtocolMethods.ServerBackupRun] = new(null, typeof(ServerBackupStarted), null, Own: async (connection, _, _, ct) =>
        {
            var port = connection.BackupPort();
            connection.RefuseOnServer("server backup", BackupRefusal);
            return JsonSerializer.SerializeToUtf8Bytes(await port.RunAsync(ct).ConfigureAwait(false), ProtocolJson.Options);
        });
        routes[ProtocolMethods.ServerBackupPassword] = new(typeof(ServerBackupPasswordParams), typeof(Done), null, Own: async (connection, request, _, ct) =>
        {
            var port = connection.BackupPort();
            var parameters = ReadParams<ServerBackupPasswordParams>(request);
            if (parameters.Password is not { Length: >= ServerBackupLimits.MinPassword and <= ServerBackupLimits.MaxPassword })
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The password must be 8 to 256 characters."));
            connection.RefuseOnServer("server backup", BackupRefusal);
            await port.SetPasswordAsync(parameters.Password, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
        });
        return routes;
    }

    private IServerBackup BackupPort()
    {
        var port = services.Backup ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.ServerBackup) throw new NotSupportedException();
        return port;
    }
}
