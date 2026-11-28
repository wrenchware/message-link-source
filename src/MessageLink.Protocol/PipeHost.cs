using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.WebSockets;

namespace MessageLink.Protocol;

/// <summary>The pipe's name belongs to another process already (<see cref="PipeHost.StartAsync"/>). Says nothing else: the inner exception is for a debugger, not a log.</summary>
public sealed class PipeNameTakenException(Exception inner) : IOException("The pipe's name is already in use.", inner);

/// <summary>
/// The host's end of the local transport: listens on the pipe, admits a connection only if it presents the client token (checked before any
/// frame is read), and hands the channel it makes to <c>serve</c>, which for a host is a <see cref="ProtocolDispatcher"/> (see
/// <see cref="Dispatching"/>). A connection that is refused is logged as <c>protocol connection refused reason=token|handshake</c> and
/// never reaches <c>serve</c>; neither the token nor anything the peer sent is logged.
/// </summary>
public sealed class PipeHost : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly ClientToken token;
    private readonly Func<IFrameChannel, Task> serve;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<WebSocketFrameChannel, Task> serving = new();
    private Task? accepting;
    private int refused;
    private int pending;
    private int dropped;

    /// <summary>How many connections may be in the door at once (admitted by the pipe, not yet through the handshake). The 5 s handshake limit bounds how long one holds a place.</summary>
    public int MaxPending { get; set; } = 8;

    /// <summary>How long each of the three steps of closing waits for the listener or the connections (stopping the listener, then closing it, then the connections), so a stop's worst case is knowable: <see cref="WorstStop"/>.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    /// <summary>The longest <c>StopListeningAsync</c> followed by <c>DisposeAsync</c> can take: the listener once, then the listener and the connections again.</summary>
    public static TimeSpan WorstStop => StopWait * 3;

    /// <summary>How many connections were let go unread because <see cref="MaxPending"/> others were already in the door.</summary>
    public int Dropped => Volatile.Read(ref dropped);

    /// <summary>How many connections are in the door now.</summary>
    public int Pending => Volatile.Read(ref pending);

    public PipeHost(string pipeName, ClientToken token, Func<IFrameChannel, Task> serve, Action<string>? log = null)
    {
        this.pipeName = pipeName; this.token = token; this.serve = serve; this.log = log;
    }

    /// <summary>A host that gives each admitted connection its own <see cref="ProtocolDispatcher"/>, the way <see cref="LoopbackProtocol"/> does. Share one <paramref name="journal"/> so a client that reconnects finds what it started.</summary>
    public static PipeHost Dispatching(string pipeName, ClientToken token, ProtocolServices services, string appVersion, ProtocolCallStats? served = null,
        Action<string>? log = null, OperationJournal? journal = null) =>
        new(pipeName, token, channel => new ProtocolDispatcher(channel, services, appVersion, served, log, journal).Start(), log);

    /// <summary>How many connections have been refused at the door.</summary>
    public int Refused => Volatile.Read(ref refused);

    /// <summary>
    /// Makes the token if there is none, takes the pipe name and starts admitting connections. Throws <see cref="PipeNameTakenException"/> when
    /// another process already has the name (a second server, or something squatting on it).
    /// </summary>
    public async Task StartAsync()
    {
        var secret = await token.GetOrCreateAsync().ConfigureAwait(false);
        NamedPipeServerStream first;
        try { first = PipeEndpoint.CreateServer(pipeName, first: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new PipeNameTakenException(ex); }
        accepting = Task.Run(() => AcceptAsync(first, secret));
    }

    private async Task AcceptAsync(NamedPipeServerStream listening, byte[] secret)
    {
        var failures = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await listening.WaitForConnectionAsync(stop.Token).ConfigureAwait(false); }
                // A peer that connected and left before it was taken spends the instance, not the listener: a loop that ended here would leave the
                // name unanswered for as long as the server runs. A listener that keeps failing is given up on, and says so.
                catch (IOException) when (++failures < 100 && !stop.IsCancellationRequested)
                {
                    listening.Dispose();
                    listening = PipeEndpoint.CreateServer(pipeName, first: false);
                    continue;
                }
                failures = 0;
                var connected = listening;
                // The next instance first, so a client arriving while this one is admitted finds the pipe.
                listening = PipeEndpoint.CreateServer(pipeName, first: false);
                if (Interlocked.Increment(ref pending) > MaxPending)
                {
                    Interlocked.Decrement(ref pending);
                    // Unread and unanswered: whoever fills the door does not get to take the rest of the server's time. One line to say it happened, then one in a hundred.
                    var count = Interlocked.Increment(ref dropped);
                    if (count == 1 || count % 100 == 0) log?.Invoke($"protocol connection dropped reason=pending count={count}");
                    connected.Dispose();
                    continue;
                }
                _ = Task.Run(() => AdmitAsync(connected, secret));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (ex is IOException && !stop.IsCancellationRequested) log?.Invoke("protocol listener failed reason=io");
        }
        finally { listening.Dispose(); }
    }

    private async Task AdmitAsync(NamedPipeServerStream pipe, byte[] secret)
    {
        WebSocketFrameChannel? channel = null;
        var counted = true;
        void LeaveDoor()
        {
            if (counted) { counted = false; Interlocked.Decrement(ref pending); }
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            limit.CancelAfter(PipeEndpoint.HandshakeTimeout);
            var (verdict, stream) = await PipeHandshake.AcceptAsync(pipe, secret, limit.Token).ConfigureAwait(false);
            if (verdict != PipeHandshake.Verdict.Accepted)
            {
                // The first, then one in a hundred, as the drops: a peer that keeps knocking does not fill the log (or turn it over, which would push the
                // lines that matter out of it). The count is the total either way.
                var count = Interlocked.Increment(ref refused);
                if (count == 1 || count % 100 == 0)
                    log?.Invoke($"protocol connection refused reason={(verdict == PipeHandshake.Verdict.Token ? "token" : "handshake")}{(count == 1 ? "" : $" count={count}")}");
                // The status line is written; the pipe stays until the peer has read it and hung up (or the wait runs out).
                try { await pipe.ReadAsync(new byte[16], limit.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                pipe.Dispose();
                return;
            }
            LeaveDoor();
            channel = new WebSocketFrameChannel(WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero }), log);
            var running = serve(channel);
            serving[channel] = running;
            await running.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            pipe.Dispose();
        }
        finally
        {
            LeaveDoor();
            if (channel is not null) serving.TryRemove(channel, out _);
        }
    }

    /// <summary>
    /// Stops admitting connections and frees the pipe's name; the connections already open stay open (<see cref="DisposeAsync"/> closes them), so a
    /// host that is stopping can finish answering what it accepted.
    /// </summary>
    public async Task StopListeningAsync()
    {
        stop.Cancel();
        try { if (accepting is not null) await accepting.WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Stops listening and closes the connections that are open.</summary>
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var channel in serving.Keys) channel.Close();
        try { if (accepting is not null) await accepting.WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
        try { await Task.WhenAll(serving.Values).WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
