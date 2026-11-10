using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace NativeBeeper.Protocol;

/// <summary>
/// An <see cref="IFrameChannel"/> over a <see cref="WebSocket"/>: one text message per frame. The socket may come from a pipe
/// (<see cref="PipeEndpoint"/>) or, later, from HTTPS; nothing here knows which. A loss of the connection (an abort, a broken pipe, a
/// peer that vanished) ends <see cref="Incoming"/> and fails later sends with <see cref="ChannelClosedException"/>, exactly as a
/// <see cref="LoopbackChannel"/> that was closed does, so <see cref="ProtocolClient"/> treats it the same way.
/// </summary>
/// <remarks>
/// A message is reassembled however the peer fragmented it, up to <see cref="MaxMessageBytes"/>. A bigger one, a binary message or text that
/// is not UTF-8 is a protocol error: the connection is closed (1009, 1003 or 1007) without reading further. A 1 MiB media chunk is about
/// 1.4 MB as base64, so the cap leaves room for the largest frame the protocol makes.
/// </remarks>
public sealed class WebSocketFrameChannel : IFrameChannel
{
    /// <summary>The largest message accepted from the other end (SWITCHBOARD.md stage 3, transport).</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    private const int SmallMessage = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly WebSocket socket;
    private readonly Action<string>? log;
    private readonly Channel<string> incoming;
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task receiving;
    private int closed;

    /// <summary>
    /// How many whole messages wait to be read before the socket itself stops being read. The reader of <see cref="Incoming"/> takes them as fast as
    /// it can parse them, so a queue this deep means it has fallen behind; the socket is then left unread (TCP's own window slows the peer, nothing is
    /// dropped) until it catches up, rather than the queue growing without limit for a peer that sends faster than the host can serve. A stalled socket
    /// also stops answering the WebSocket's pings, so one that never lets the queue drain is ended by the keep-alive timeout.
    /// </summary>
    public const int IncomingCapacity = 64;

    public WebSocketFrameChannel(WebSocket socket, Action<string>? log = null, int incomingCapacity = IncomingCapacity)
    {
        this.socket = socket;
        this.log = log;
        incoming = Channel.CreateBounded<string>(new BoundedChannelOptions(incomingCapacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
        receiving = Task.Run(ReceiveAsync);
    }

    public ChannelReader<string> Incoming => incoming.Reader;

    /// <summary>
    /// The route that carried this connection when a <see cref="ServerDialer"/> opened it (<c>home</c> or <c>tailnet</c>), so a client can say how it is
    /// connected; null for any other socket.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>
    /// Completes once nothing more will be read from the socket: the peer closed it or answered a close, or the connection was lost or dropped. A host whose
    /// connection ends when its request handler returns (Kestrel) waits for this after <see cref="Close"/>, so the close handshake is not cut short.
    /// </summary>
    public Task Completion => receiving;

    public async ValueTask SendAsync(string frame, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref closed) != 0) throw new ChannelClosedException();
        var bytes = Encoding.UTF8.GetBytes(frame);
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref closed) != 0) throw new ChannelClosedException();
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Half a message may be on the wire, and nothing after it could be read: the connection is over.
            Abort();
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Abort();
            throw new ChannelClosedException(ex);
        }
        finally { sending.Release(); }
    }

    /// <summary>Ends the connection with a close handshake after what was sent. Frames already sent are still read by the other end.</summary>
    public void Close() => Begin(WebSocketCloseStatus.NormalClosure);

    /// <summary>Drops the connection at once with no handshake, as a crash would. The other end sees it as lost.</summary>
    public void Abort()
    {
        Volatile.Write(ref closed, 1);
        incoming.Writer.TryComplete();
        stop.Cancel();
        try { socket.Abort(); } catch (Exception) { }
        _ = receiving.ContinueWith(static (_, state) => ((WebSocket)state!).Dispose(), socket, TaskScheduler.Default);
    }

    private void Begin(WebSocketCloseStatus status)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        incoming.Writer.TryComplete();
        _ = Task.Run(() => CloseAsync(status));
    }

    private async Task CloseAsync(WebSocketCloseStatus status)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // After what a sender already queued, never in the middle of a message.
            await sending.WaitAsync(limit.Token).ConfigureAwait(false);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(status, null, limit.Token).ConfigureAwait(false);
            }
            finally { sending.Release(); }
            // The reader consumes the peer's close (the handshake needs it); if the peer never answers, the connection is dropped.
            await receiving.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* the handshake is a courtesy; the connection ends either way */ }
        finally
        {
            stop.Cancel();
            try { socket.Abort(); } catch (Exception) { }
            socket.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        var message = new byte[SmallMessage];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == message.Length)
                    {
                        if (length >= MaxMessageBytes) { Refuse(WebSocketCloseStatus.MessageTooBig, "too_big"); return; }
                        Array.Resize(ref message, Math.Min(message.Length * 4, MaxMessageBytes));
                    }
                    result = await socket.ReceiveAsync(message.AsMemory(length), stop.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text) { Refuse(WebSocketCloseStatus.InvalidMessageType, "binary"); return; }
                    length += result.Count;
                } while (!result.EndOfMessage);

                string text;
                try { text = StrictUtf8.GetString(message.AsSpan(0, length)); }
                catch (DecoderFallbackException) { Refuse(WebSocketCloseStatus.InvalidPayloadData, "encoding"); return; }
                // After a local close nobody reads any more; the loop goes on only to take the peer's close.
                // Waits while the queue is full: the socket is not read meanwhile. A close from this end completes the writer, which ends the wait
                // (ChannelClosedException, below); a message arriving after a local close is not delivered, as before.
                try { await incoming.Writer.WriteAsync(text, stop.Token).ConfigureAwait(false); }
                catch (ChannelClosedException) { }
                if (message.Length > 1024 * 1024) message = new byte[SmallMessage];
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            // The peer closed or the connection broke: answer a close and release the socket.
            Begin(WebSocketCloseStatus.NormalClosure);
        }
    }

    // The connection is closed from this end because of what the other end sent. Never echoes any of it.
    private void Refuse(WebSocketCloseStatus status, string reason)
    {
        log?.Invoke($"protocol connection closed reason={reason}");
        Begin(status);
    }
}
