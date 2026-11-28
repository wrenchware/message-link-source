using System.Security.Cryptography;
using System.Text;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Terminal.Connection;

/// <summary>The server <c>mlink join</c> saved: its socket (<c>wss://host:port/v1/socket</c>) and the SHA-256 of its certificate, which is all that is trusted.</summary>
public sealed record JoinedServer(Uri Socket, byte[] Fingerprint)
{
    /// <summary>The pin as the replica is filed under (<see cref="ServerLink.ServerId"/>): lower-case hex.</summary>
    public string ServerId => Convert.ToHexStringLower(Fingerprint);

    /// <summary>The server as a person reads it: the host, and its port when that is not the usual one.</summary>
    public string Name => NameOf(Socket);

    /// <summary>A one-way tag of the pin for the log: never the pin itself, so two logs can be told to name one server without either naming it.</summary>
    public string Tag => TagOf(Fingerprint);

    public static string NameOf(Uri socket) => socket.Port == NetworkEndpoint.DefaultPort ? socket.Host : $"{socket.Host}:{socket.Port}";

    public static string TagOf(byte[] fingerprint) => Convert.ToHexStringLower(SHA256.HashData(fingerprint))[..8];
}

/// <summary>
/// The saved server in mlink's folder: <see cref="BindingFile"/> (the socket and the pin, sealed) and <see cref="TokenFile"/> (the device token, sealed, through
/// <see cref="ClientToken"/>). The binding is written first, then the token: a stop between them leaves a binding whose old token the server refuses at worst.
/// Neither is ever logged.
/// </summary>
public sealed class JoinedServerStore(string folder, IStateProtector protector)
{
    public const string BindingFile = "server.binding";
    public const string TokenFile = "server.token";
    private const string Version = "v1";

    public string BindingPath { get; } = Path.Combine(folder, BindingFile);
    public string TokenPath { get; } = Path.Combine(folder, TokenFile);

    /// <summary>The device token, as the connector reads it each time it connects.</summary>
    public ClientToken Token { get; } = new(folder, protector, TokenFile);

    /// <summary>Whether anything of a saved server is there, readable or not.</summary>
    public bool Exists => File.Exists(BindingPath) || File.Exists(TokenPath);

    /// <summary>The saved server, or null when there is none or its file cannot be read back (another user's, damaged).</summary>
    public async Task<JoinedServer?> ReadAsync()
    {
        try
        {
            if (!File.Exists(BindingPath)) return null;
            var text = Encoding.UTF8.GetString(await protector.UnprotectAsync(await File.ReadAllBytesAsync(BindingPath).ConfigureAwait(false)).ConfigureAwait(false));
            var parts = text.Split('\n');
            if (parts.Length != 3 || parts[0] != Version || NetworkEndpoint.SocketUri(parts[1]) is not { } socket || NetworkEndpoint.ParseFingerprint(parts[2]) is not { } pin) return null;
            return new JoinedServer(socket, pin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Keeps <paramref name="server"/> and its <paramref name="token"/> in place of whatever was saved.</summary>
    public async Task StoreAsync(JoinedServer server, byte[] token)
    {
        var text = $"{Version}\n{server.Socket.AbsoluteUri}\n{Convert.ToHexStringLower(server.Fingerprint)}";
        await MlinkPaths.WritePrivateAsync(BindingPath, await protector.ProtectAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false)).ConfigureAwait(false);
        await Token.StoreAsync(token).ConfigureAwait(false);
        MlinkPaths.MakePrivate(TokenPath);
    }

    /// <summary>Removes the saved server and its token. Nothing else of the folder is touched.</summary>
    public void Forget()
    {
        foreach (var path in new[] { TokenPath, BindingPath })
            if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The two files as they are now, to put back if what replaces them does not finish (<see cref="RestoreAsync"/>).</summary>
    internal async Task<Snapshot> SnapshotAsync() =>
        new(File.Exists(BindingPath) ? await File.ReadAllBytesAsync(BindingPath).ConfigureAwait(false) : null,
            File.Exists(TokenPath) ? await File.ReadAllBytesAsync(TokenPath).ConfigureAwait(false) : null);

    internal async Task RestoreAsync(Snapshot snapshot)
    {
        foreach (var (path, bytes) in new[] { (BindingPath, snapshot.Binding), (TokenPath, snapshot.Token) })
        {
            if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
            else await MlinkPaths.WritePrivateAsync(path, bytes).ConfigureAwait(false);
        }
    }

    internal sealed record Snapshot(byte[]? Binding, byte[]? Token);
}
