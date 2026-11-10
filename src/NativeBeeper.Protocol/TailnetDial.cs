using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NativeBeeper.Protocol;

/// <summary>The one byte the tailnet node's local door answers a connection with (<c>android/tailnet/door.go</c>, which the PC's helper shares).</summary>
public enum TailnetAnswer : byte
{
    /// <summary>The connection is open: from here on the door passes bytes both ways.</summary>
    Open = 0,
    /// <summary>The node is not started (or the door is not there, or it closed without a word).</summary>
    Off = 1,
    /// <summary>The node needs signing in to Tailscale: first use, or its key expired.</summary>
    SignedOut = 2,
    /// <summary>No device of that name on the tailnet.</summary>
    NoDevice = 3,
    /// <summary>The device is there, but the connection to its port did not open.</summary>
    NoConnection = 4,
    /// <summary>Every place in the door is taken.</summary>
    Busy = 5,
    /// <summary>The device with the id asked for is not on the tailnet, and another answers to its name.</summary>
    OtherDevice = 6,
}

/// <summary>The tailnet door did not open the connection. Says only the answer: never the target, which names a device on the owner's tailnet.</summary>
public sealed class TailnetDoorException(TailnetAnswer answer, Exception? inner = null)
    : IOException($"The Tailscale connection did not open ({TailnetDial.Word(answer)}).", inner)
{
    public TailnetAnswer Answer { get; } = answer;
}

/// <summary>
/// The client's end of the tailnet node's local door (the phone's <c>PhoneTailnet</c>, and the PC's <c>tailnet-helper.exe</c>): a plain TCP connection to
/// 127.0.0.1 that opens with this run's secret, one byte giving the length of the target and the target, and is answered with one byte. After
/// <see cref="TailnetAnswer.Open"/> the connection is the device's port, and TLS to the server runs over it end to end, pinned as at home.
/// </summary>
public static class TailnetDial
{
    public const int SecretLength = 32;
    /// <summary>The longest target the door's one length byte can say.</summary>
    public const int MaxTarget = 255;
    /// <summary>
    /// How long the door may take to answer: it bounds finding the device and opening the connection through the node at 8 s (<c>connectWait</c>), so a
    /// device that does not answer is said as such rather than as a timeout here.
    /// </summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(9.5);

    /// <summary>
    /// The target the door is asked for: <c>name:port</c>, or <c>name/id:port</c> for only the device whose stable node id is <paramref name="nodeId"/>.
    /// An IPv6 literal is in brackets (<c>[fd7a::1]:port</c>, <c>[fd7a::1/id]:port</c>), as the door splits it with Go's <c>net.SplitHostPort</c>.
    /// </summary>
    public static string Target(string name, string? nodeId, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        name = name.Trim();
        var literal6 = IPAddress.TryParse(name.Trim('[', ']'), out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        if (literal6) name = name.Trim('[', ']');
        else if (name.Contains('/') || name.Contains(':') || name.Contains('[') || name.Contains(']')) throw new ArgumentException("Not a device name.", nameof(name));
        if (nodeId is not null && !NodeId(nodeId)) throw new ArgumentException("Not a stable node id.", nameof(nodeId));
        var host = nodeId is null ? name : name + "/" + nodeId;
        return (literal6 ? "[" + host + "]" : host) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What the door takes as a stable node id (<c>nodeID</c> in door.go): letters, digits, '-', '_' and '.', at most 64.</summary>
    public static bool NodeId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The answer as a log word: <c>off</c>, <c>signedout</c>, <c>nodevice</c>, <c>noconnection</c>, <c>busy</c>, <c>otherdevice</c>.</summary>
    public static string Word(TailnetAnswer answer) => answer.ToString().ToLowerInvariant();

    /// <summary>
    /// Opens a connection through the door on 127.0.0.1:<paramref name="doorPort"/> to <paramref name="target"/> (<see cref="Target"/>). Returns the open
    /// stream once the door answers <see cref="TailnetAnswer.Open"/>; throws <see cref="TailnetDoorException"/> with its answer otherwise. A door that is not
    /// there, or closes without a word (a wrong secret is answered with silence), is <see cref="TailnetAnswer.Off"/>; one that says nothing within
    /// <paramref name="answerWait"/> is <see cref="TailnetAnswer.NoConnection"/>, as the phone reads it.
    /// </summary>
    public static async Task<Stream> OpenAsync(int doorPort, byte[] secret, string target, CancellationToken cancellationToken, TimeSpan? answerWait = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(target);
        if (secret.Length != SecretLength) throw new ArgumentException("The door's secret is 32 bytes.", nameof(secret));
        if (target.Length is 0 or > MaxTarget || !target.All(char.IsAscii)) throw new ArgumentException("The target is 1 to 255 ASCII characters.", nameof(target));
        ArgumentOutOfRangeException.ThrowIfLessThan(doorPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(doorPort, 65535);

        // By number: "localhost" may resolve to ::1 first, where the door does not listen.
        Socket? socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, doorPort), cancellationToken).ConfigureAwait(false); }
            catch (SocketException refused) { throw new TailnetDoorException(TailnetAnswer.Off, refused); }

            var preamble = new byte[SecretLength + 1 + target.Length];
            secret.CopyTo(preamble, 0);
            preamble[SecretLength] = (byte)target.Length;
            Encoding.ASCII.GetBytes(target, 0, target.Length, preamble, SecretLength + 1);
            var answer = new byte[1];
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(answerWait ?? AnswerWait);
                int read;
                try
                {
                    await socket.SendAsync(preamble, SocketFlags.None, limit.Token).ConfigureAwait(false);
                    read = await socket.ReceiveAsync(answer, SocketFlags.None, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException silent) when (!cancellationToken.IsCancellationRequested) { throw new TailnetDoorException(TailnetAnswer.NoConnection, silent); }
                // A door that read a wrong secret closes with the target unread, which a peer may see as a reset rather than an end.
                catch (SocketException closed) { throw new TailnetDoorException(TailnetAnswer.Off, closed); }
                if (read == 0) throw new TailnetDoorException(TailnetAnswer.Off);
            }
            if (answer[0] != (byte)TailnetAnswer.Open)
                throw new TailnetDoorException(Enum.IsDefined((TailnetAnswer)answer[0]) ? (TailnetAnswer)answer[0] : TailnetAnswer.NoConnection);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally { socket?.Dispose(); }
    }
}
