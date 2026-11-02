using System.IO.Pipes;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NativeBeeper.Protocol;

/// <summary>
/// The local transport (SWITCHBOARD.md stage 3): a named pipe that carries one WebSocket, upgraded the way an HTTP client would
/// (<see cref="PipeHandshake"/>). This is the pipe's name, the host's end with the access it grants, and the client's connector for
/// <see cref="ProtocolClient"/>.
/// </summary>
public static class PipeEndpoint
{
    public const string Prefix = "Messages.Server.";

    // With the default (zero) a write waits for the reader; a frame in flight should not.
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// <c>Messages.Server.&lt;first 12 hex of SHA-256 of the user key&gt;&lt;suffix&gt;</c>. The suffix tells copies of the host on one PC apart
    /// (a development copy); it may hold letters, digits, dots, dashes and underscores.
    /// </summary>
    public static string PipeName(string userKey, string instanceSuffix = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(userKey);
        if (instanceSuffix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("An instance suffix may hold only letters, digits, dots, dashes and underscores.", nameof(instanceSuffix));
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userKey)))[..12] + instanceSuffix;
    }

    /// <summary>The pipe name for the user running this process.</summary>
    public static string PipeNameForCurrentUser(string instanceSuffix = "") => PipeName(CurrentUserKey(), instanceSuffix);

    /// <summary>The user's SID on Windows (the name of an account is not unique); the user name elsewhere.</summary>
    public static string CurrentUserKey() =>
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName : Environment.UserName;

    /// <summary>
    /// A pipe instance for the host to listen on. Only the current user may open it, and the NETWORK group is denied outright, because a
    /// named pipe is reachable over SMB unless it says otherwise. <paramref name="first"/> fails if the name is already taken, so another
    /// process cannot have created it ahead of the host.
    /// </summary>
    /// <remarks>
    /// On Windows this is an explicit access list rather than <see cref="PipeOptions.CurrentUserOnly"/>, which the framework refuses to combine
    /// with one; the list says what that option would (the current user, nobody else) and adds the NETWORK deny. The client still sets
    /// <c>CurrentUserOnly</c>, which checks that the pipe's owner is this process's own, so <see cref="CreateSecurity"/> names that owner.
    /// </remarks>
    public static NamedPipeServerStream CreateServer(string pipeName, bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return OperatingSystem.IsWindows()
            ? NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, BufferBytes, BufferBytes, CreateSecurity())
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly, BufferBytes, BufferBytes);
    }

    /// <summary>The access list <see cref="CreateServer"/> applies: full control for the current user, a deny for the NETWORK SID, nobody else.</summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        // The owner a process's new objects get, which is what CurrentUserOnly compares: the user, except under an elevated token, where it is Administrators. Naming the
        // user there had an elevated window hang up on its own elevated server every time (Windows Sandbox, 2026-11-02: reason=handshake, reconnect outcome=refused).
        security.SetOwner(WindowsIdentity.GetCurrent().Owner!);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>
    /// A connector for <see cref="ProtocolClient"/>: opens the pipe (only if it belongs to this user), sends the token with the upgrade and
    /// returns the channel. It fails fast when nothing is listening or the host refuses, which <see cref="ProtocolClient"/> treats as a
    /// connection that is not there.
    /// </summary>
    public static Func<CancellationToken, Task<IFrameChannel>> Connector(string pipeName, ClientToken token, Action<string>? log = null, TimeSpan? connectTimeout = null)
    {
        var connect = connectTimeout ?? TimeSpan.FromSeconds(1);
        return async cancellationToken =>
        {
            var bearer = await token.ReadAsync().ConfigureAwait(false) ?? throw new IOException("There is no client token to present.");
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)connect.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(HandshakeTimeout);
                var stream = await PipeHandshake.ConnectAsync(pipe, ClientToken.HeaderValue(bearer), limit.Token).ConfigureAwait(false);
                var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
                return new WebSocketFrameChannel(socket, log);
            }
            catch (Exception)
            {
                pipe.Dispose();
                throw;
            }
        };
    }

    /// <summary>How long either end waits for the other to finish the upgrade.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
}
