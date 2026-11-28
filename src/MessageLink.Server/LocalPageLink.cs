using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessageLink.Server;

/// <summary>
/// The Windows server's page on this PC (SWITCHBOARD.md 10.7): how Messages opens it signed in, with no password to type, and how the page knows the link is the app's.
/// <list type="bullet">
/// <item>The server writes where its page listens, <c>&lt;root&gt;\Server\page.json</c> (the port and its certificate's SHA-256, nothing secret), each time it starts.</item>
/// <item>Messages makes a one-time code, writes its SHA-256 and an expiry to <c>&lt;root&gt;\Server\page-once</c>, and opens <c>https://127.0.0.1:&lt;port&gt;/login?once=&lt;code&gt;</c>
/// in a window of its own that trusts the page's self-signed certificate by that fingerprint alone (<see cref="IsPageCertificate"/>), so no browser asks whether to
/// go on; in the user's browser when the server wrote no fingerprint.</item>
/// <item>The page takes a code once (the file goes with it), within <see cref="Lifetime"/>, compared in fixed time, and signs that browser in as its password would.</item>
/// </list>
/// The data folder is the user's own (only that user and the system can open it), so whoever can write the file could already read everything the server keeps; the
/// code itself is never written anywhere but the browser's address, and never logged.
/// </summary>
internal static class LocalPageLink
{
    public const string PortFile = "page.json";
    public const string OnceFile = "page-once";
    /// <summary>How long a code stays good: long enough for a browser to open, short enough that one left in a history is spent.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Port(int Version, int Value, string? Fingerprint = null);

    /// <summary>Where the page listens, and the SHA-256 of the certificate it serves (lowercase hex; null when the server didn't say).</summary>
    public sealed record Page(int Port, string? Fingerprint);
    private sealed record Once(int Version, string Hash, long Expires);

    public static string PortPath(string dataRoot) => Path.Combine(dataRoot, "Server", PortFile);
    public static string OncePath(string dataRoot) => Path.Combine(dataRoot, "Server", OnceFile);

    /// <summary>The server records the port its page listens on and the fingerprint of the certificate it serves there.</summary>
    public static void WritePort(string dataRoot, int port, string? fingerprint = null) =>
        WriteWhole(PortPath(dataRoot), JsonSerializer.SerializeToUtf8Bytes(new Port(1, port, fingerprint)));

    /// <summary>The port the server last said its page listens on, or null when it never did (or the file cannot be read).</summary>
    public static int? ReadPort(string dataRoot) => ReadPage(dataRoot)?.Port;

    /// <summary>What the server last said of its page, or null when it never did (or the file cannot be read). A fingerprint that isn't 64 hex digits reads as none.</summary>
    public static Page? ReadPage(string dataRoot)
    {
        try
        {
            var path = PortPath(dataRoot);
            if (!File.Exists(path) || new FileInfo(path).Length > 256) return null;
            if (JsonSerializer.Deserialize<Port>(File.ReadAllBytes(path)) is not { Version: 1, Value: > 0 and < 65536 } port) return null;
            var fingerprint = port.Fingerprint is { Length: 64 } hex && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
            return new Page(port.Value, fingerprint);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Whether <paramref name="certificate"/> (DER) is the one whose SHA-256 the server wrote, compared in fixed time. Nothing to compare is never a match.</summary>
    public static bool IsPageCertificate(ReadOnlySpan<byte> certificate, string? fingerprint)
    {
        if (certificate.IsEmpty || fingerprint is not { Length: 64 }) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(fingerprint); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate), expected);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is the page on this PC: https, 127.0.0.1 and exactly <paramref name="port"/>, with no user name. Only that loads in the page's
    /// window; any other link opens in the browser.
    /// </summary>
    public static bool IsPage(string? address, int port) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "127.0.0.1" && uri.Port == port && uri.UserInfo.Length == 0;

    /// <summary>
    /// A new one-time code for the page, replacing any earlier one, and the address that carries it; null when the server never said where its page is. The code is
    /// 32 random bytes in URL-safe base64.
    /// </summary>
    public static string? Issue(string dataRoot, DateTimeOffset now)
    {
        if (ReadPort(dataRoot) is not { } port) return null;
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        WriteWhole(OncePath(dataRoot), JsonSerializer.SerializeToUtf8Bytes(new Once(1, Hash(code), (now + Lifetime).ToUnixTimeMilliseconds())));
        return string.Create(CultureInfo.InvariantCulture, $"https://127.0.0.1:{port}/login?once={code}");
    }

    /// <summary>
    /// Whether <paramref name="code"/> is the one Messages made, still good. A code is taken once: the file is removed whatever the answer, so a wrong guess spends
    /// the real code too (Messages makes a new one each time the page is opened).
    /// </summary>
    public static bool TryRedeem(string dataRoot, string? code, DateTimeOffset now)
    {
        if (code is not { Length: > 0 and <= 64 }) return false;
        var path = OncePath(dataRoot);
        Once? stored;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 512) return false;
            stored = JsonSerializer.Deserialize<Once>(File.ReadAllBytes(path));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { stored = null; }
        try { File.Delete(path); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
        if (stored is not { Version: 1, Hash.Length: 64 } || now.ToUnixTimeMilliseconds() > stored.Expires) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(code)), Encoding.ASCII.GetBytes(stored.Hash));
    }

    /// <summary>
    /// The page as the server says it now, once something listens where it says, read again until then: page.json is the last run's until the server has its door
    /// open again, and may name a port the new run doesn't use (a development copy's is a new one each run). Up to <paramref name="limit"/>; then what it last read,
    /// listening or not, or null when the server never said. A window opened before the server listens showed "can't reach this page" until its own reload (2026-11-22).
    /// </summary>
    public static async Task<(Page? Page, bool Listening)> WaitForListeningPageAsync(string dataRoot, TimeSpan limit, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var page = ReadPage(dataRoot);
            if (page is not null && await WaitForPageAsync(page.Port, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false)) return (page, true);
            if (clock.Elapsed >= limit) return (page, false);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits, up to <paramref name="limit"/>, until something accepts a connection on the page's port on this PC; true when it does.</summary>
    public static async Task<bool> WaitForPageAsync(int port, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(limit);
        while (true)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, bound.Token).ConfigureAwait(false);
                return true;
            }
            catch (System.Net.Sockets.SocketException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
            try { await Task.Delay(100, bound.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }
}
