using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MessageLink.Server.Network;

/// <summary>
/// The relay listener's first line (SWITCHBOARD.md 4.7). The server's door on the owner's tailnet (<c>messages-tailnet</c>, a Tailscale node of its own in the
/// container) passes each device's connection to a Unix socket only it can reach, byte for byte, after one PROXY protocol line (version 1, haproxy.org's
/// proxy-protocol.txt) naming the device's tailnet address. TLS is the server's own end to end. The address read here becomes the connection's own, so the door's
/// per-address places, pairing and join pacing and the page's home-network rule see each tailnet device as itself rather than every one as the door.
/// <para>Only tailnet addresses are taken (100.64.0.0/10 and Tailscale's fd7a:115c:a1e0::/48), in the protocol's exact form; anything else ends the connection.</para>
/// </summary>
internal static partial class TailnetRelay
{
    /// <summary>The protocol's longest line, its CRLF included.</summary>
    public const int MaxLine = 107;

    [GeneratedRegex(@"^PROXY TCP4 (\d{1,3}(?:\.\d{1,3}){3}) (\d{1,3}(?:\.\d{1,3}){3}) ([1-9]\d{0,4}) ([1-9]\d{0,4})\r\n\z", RegexOptions.CultureInvariant)]
    private static partial Regex Four();

    [GeneratedRegex(@"^PROXY TCP6 ([0-9A-Fa-f:]{2,39}) ([0-9A-Fa-f:]{2,39}) ([1-9]\d{0,4}) ([1-9]\d{0,4})\r\n\z", RegexOptions.CultureInvariant)]
    private static partial Regex Six();

    /// <summary>The device's address and port from one whole line (CRLF included), or null for anything that is not a tailnet device's line in the exact form.</summary>
    public static IPEndPoint? Parse(ReadOnlySpan<byte> line)
    {
        if (line.Length > MaxLine) return null;
        foreach (var b in line) if (b is < 0x20 or > 0x7E && b is not ((byte)'\r' or (byte)'\n')) return null;
        var text = Encoding.ASCII.GetString(line);
        var four = Four().Match(text);
        var match = four.Success ? four : Six().Match(text);
        if (!match.Success) return null;
        var family = four.Success ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if (!IPAddress.TryParse(match.Groups[1].Value, out var source) || !IPAddress.TryParse(match.Groups[2].Value, out var destination)) return null;
        if (source.AddressFamily != family || destination.AddressFamily != family || !IsTailnet(source) || !IsTailnet(destination)) return null;
        // IPAddress reads "100.64.1.010" as 100.64.1.10; only the plain form is the protocol's.
        if (family == AddressFamily.InterNetwork && (source.ToString() != match.Groups[1].Value || destination.ToString() != match.Groups[2].Value)) return null;
        if (!int.TryParse(match.Groups[3].Value, out var port) || port > IPEndPoint.MaxPort || !int.TryParse(match.Groups[4].Value, out var to) || to > IPEndPoint.MaxPort) return null;
        return new IPEndPoint(source, port);
    }

    /// <summary>Whether an address is one Tailscale gives a tailnet's devices: 100.64.0.0/10, or fd7a:115c:a1e0::/48.</summary>
    public static bool IsTailnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : !address.IsIPv4MappedToIPv6 && bytes is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    }

    /// <summary>
    /// Reads the first line from <paramref name="input"/> and leaves everything after it (the device's TLS) for the next reader. Null when the connection ends
    /// first, the line is longer than the protocol's, or it is not a tailnet device's.
    /// </summary>
    public static async Task<IPEndPoint?> ReadAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.PositionOf((byte)'\n') is { } newline)
            {
                var end = buffer.GetPosition(1, newline);
                var line = buffer.Slice(0, end);
                var source = line.Length <= MaxLine ? Parse(line.ToArray()) : null;
                input.AdvanceTo(end);
                return source;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
            if (buffer.Length >= MaxLine || result.IsCompleted || result.IsCanceled) return null;
        }
    }

    /// <summary>
    /// Makes the socket's folder, private to this account (the door runs as it too), and removes a socket an earlier run left there: Kestrel will not bind a
    /// path that exists.
    /// </summary>
    public static void Prepare(string socket)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(socket)) ?? throw new ArgumentException("The relay socket has no folder.", nameof(socket));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(folder, owner);
            // One that was already there keeps its mode otherwise.
            if (File.GetUnixFileMode(folder) != owner) File.SetUnixFileMode(folder, owner);
        }
        if (File.Exists(socket)) File.Delete(socket);
    }
}
