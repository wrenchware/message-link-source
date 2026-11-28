using System.Security.Cryptography;
using System.Text;

namespace MessageLink.Protocol;

/// <summary>
/// The upgrade at the start of a connection, shaped like the HTTP request a WebSocket client sends (<c>GET /v1/socket</c> with
/// <c>Authorization: Bearer</c>) so the same check can sit in front of Kestrel later. A request is read whole and checked before the host
/// reads a single frame; a refusal is a status line, then the connection closes.
/// </summary>
internal static class PipeHandshake
{
    public const string Target = "/v1/socket";
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHead = 8 * 1024;

    public enum Verdict { Accepted, Token, Handshake }

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static string AcceptFor(string key) => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));

    public static string Request(string key, string authorization) =>
        $"GET {Target} HTTP/1.1\r\nHost: messages.local\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
        $"Authorization: {authorization}\r\n\r\n";

    /// <summary>The host's side: reads the request, answers it, and says whether the connection may go on. On <see cref="Verdict.Accepted"/> the stream is ready for frames.</summary>
    public static async Task<(Verdict Verdict, Stream Stream)> AcceptAsync(Stream pipe, byte[] token, CancellationToken cancellationToken)
    {
        string head;
        Stream stream;
        try
        {
            (head, var rest) = await ReadHeadAsync(pipe, cancellationToken).ConfigureAwait(false);
            stream = rest.Length == 0 ? pipe : new ReplayStream(pipe, rest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            return (Verdict.Handshake, pipe);
        }
        var lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (lines[0] != $"GET {Target} HTTP/1.1") return (await RefuseAsync(stream, "404 Not Found", Verdict.Handshake, cancellationToken).ConfigureAwait(false), pipe);
        headers.TryGetValue("Authorization", out var authorization);
        if (!ClientToken.Matches(token, authorization))
            return (await RefuseAsync(stream, "401 Unauthorized", Verdict.Token, cancellationToken).ConfigureAwait(false), pipe);
        if (!(headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Connection", out var connection) && connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Sec-WebSocket-Version", out var version) && version == "13"
            && headers.TryGetValue("Sec-WebSocket-Key", out var key) && IsKey(key)))
            return (await RefuseAsync(stream, "400 Bad Request", Verdict.Handshake, cancellationToken).ConfigureAwait(false), pipe);
        var reply = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {AcceptFor(key)}\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return (Verdict.Handshake, pipe); }
        return (Verdict.Accepted, stream);
    }

    private static bool IsKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch (FormatException) { return false; }
    }

    private static async Task<Verdict> RefuseAsync(Stream stream, string status, Verdict verdict, CancellationToken cancellationToken)
    {
        try
        {
            var challenge = verdict == Verdict.Token ? "WWW-Authenticate: Bearer\r\n" : "";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{challenge}Content-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        return verdict;
    }

    /// <summary>The client's side: sends the request and checks the answer. Throws <see cref="UnauthorizedAccessException"/> for a refused token and <see cref="InvalidDataException"/> for anything else it does not like.</summary>
    public static async Task<Stream> ConnectAsync(Stream pipe, string authorization, CancellationToken cancellationToken)
    {
        var key = NewKey();
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(Request(key, authorization)), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        var (head, rest) = await ReadHeadAsync(pipe, cancellationToken).ConfigureAwait(false);
        var lines = head.Split("\r\n");
        if (lines[0].StartsWith("HTTP/1.1 401", StringComparison.Ordinal)) throw new UnauthorizedAccessException("The host refused the client token.");
        if (!lines[0].StartsWith("HTTP/1.1 101", StringComparison.Ordinal)) throw new InvalidDataException("The host refused the connection.");
        var accepted = lines.Skip(1).Any(line => line.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase)
            && line[(line.IndexOf(':') + 1)..].Trim() == AcceptFor(key));
        if (!accepted) throw new InvalidDataException("The host did not answer the upgrade.");
        return rest.Length == 0 ? pipe : new ReplayStream(pipe, rest);
    }

    // Reads up to the blank line that ends the head. Whatever arrived after it is returned, because it belongs to the frames.
    private static async Task<(string Head, byte[] Extra)> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, Math.Min(1024, MaxHead - length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("The connection ended before the request did.");
            var from = Math.Max(0, length - 3);
            length += read;
            var end = buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var headEnd = from + end;
                return (Encoding.ASCII.GetString(buffer, 0, headEnd), buffer.AsSpan(headEnd + 4, length - headEnd - 4).ToArray());
            }
            if (length >= MaxHead) throw new InvalidDataException("The request head is too long.");
        }
    }

    // A stream that gives back bytes already read past the head before reading on from the pipe.
    private sealed class ReplayStream(Stream inner, byte[] first) : Stream
    {
        private int taken;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (taken < first.Length)
            {
                var count = Math.Min(buffer.Length, first.Length - taken);
                first.AsSpan(taken, count).CopyTo(buffer);
                taken += count;
                return count;
            }
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (taken < first.Length) return ValueTask.FromResult(Read(buffer.Span));
            return inner.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
