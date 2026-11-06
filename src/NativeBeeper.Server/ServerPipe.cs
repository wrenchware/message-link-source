using System.Collections.Concurrent;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server;

/// <summary>
/// The server's end of the local transport (SWITCHBOARD.md 3.2c): one <see cref="PipeHost"/> on the user's pipe, and a <see cref="ProtocolDispatcher"/>
/// for each client it admits, every one over the same <see cref="ProtocolServices"/> and the same <see cref="OperationJournal"/>. The host's state,
/// its event log and its journal are therefore one thing however many clients are connected, and a client that comes back finds what it started.
/// It is plain C# so the harness runs it against a pipe of its own name; the real pipe is opened only by <c>MessagesServer.exe</c>.
/// </summary>
internal sealed class ServerPipe(string pipeName, ClientToken token, ProtocolServices services, OperationJournal journal, string version, Action<string> log,
    Func<bool>? stopping = null) : IAsyncDisposable
{
    private readonly ProtocolCallStats served = new("protocol served", log);
    private readonly ConcurrentDictionary<ProtocolDispatcher, bool> dispatchers = new();
    private PipeHost? host;
    private int stopped;
    private bool draining;

    /// <summary>The most clients that may be in the door at once, set before <see cref="StartAsync"/> (see <see cref="PipeHost.MaxPending"/>).</summary>
    public int MaxPending { get; set; } = 8;

    /// <summary>How many admitted clients are connected now.</summary>
    public int Connections => dispatchers.Count;

    /// <summary>Raised when <see cref="Connections"/> changes, from the thread that changed it.</summary>
    public event Action? ConnectionsChanged;

    public PipeHost Host => host ?? throw new InvalidOperationException("The pipe has not started.");

    /// <summary>Makes the client token if there is none and takes the pipe. False when another process has the name; that is logged as <c>server start refused reason=pipe_taken</c> and nothing was left open.</summary>
    public async Task<bool> StartAsync()
    {
        var listening = new PipeHost(pipeName, token, Serve, log) { MaxPending = MaxPending };
        try { await listening.StartAsync().ConfigureAwait(false); }
        catch (PipeNameTakenException)
        {
            log("server start refused reason=pipe_taken");
            await listening.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        host = listening;
        log("server listening");
        return true;
    }

    private async Task Serve(IFrameChannel channel)
    {
        var dispatcher = new ProtocolDispatcher(channel, services, version, served, log, journal);
        dispatchers[dispatcher] = true;
        // After it is in the set, so that either the stop's snapshot holds it or this sees the stop: a connection accepted around the moment of the stop
        // is never served. Draining it refuses its requests (unavailable); the connection itself is closed with the others.
        if (Volatile.Read(ref draining) || stopping?.Invoke() == true) dispatcher.BeginDraining();
        // Only a connection that presented the token gets here: PipeHost has logged and closed the others.
        log("protocol connection accepted");
        ConnectionsChanged?.Invoke();
        try { await dispatcher.Start().ConfigureAwait(false); }
        finally
        {
            dispatchers.TryRemove(dispatcher, out _);
            ConnectionsChanged?.Invoke();
        }
    }

    /// <summary>
    /// From now on every connection, the ones open and any accepted later, refuses its requests (<c>unavailable</c>); what each already accepted goes
    /// on to its answer. The stop begins with this, ahead of waiting for the restore, and <see cref="StopAsync"/> repeats it harmlessly.
    /// </summary>
    public void BeginDraining()
    {
        // The flag first, then the snapshot: a connection added after the snapshot sees the flag (Serve).
        Volatile.Write(ref draining, true);
        foreach (var dispatcher in dispatchers.Keys.ToArray()) dispatcher.BeginDraining();
    }

    /// <summary>
    /// Closes the pipe the way a stopping server needs it: no new connection, then each connected client is given up to <paramref name="drain"/> to
    /// have the answers to what it already asked written (new requests on those connections are refused <c>unavailable</c>), then every connection is
    /// closed. Safe to call twice and before <see cref="StartAsync"/>.
    /// </summary>
    public async Task StopAsync(TimeSpan drain)
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1 || host is not { } listening) return;
        // Draining first, so a client that connects while the listener is being stopped is refused too.
        BeginDraining();
        await listening.StopListeningAsync().ConfigureAwait(false);
        var open = dispatchers.Keys.ToArray();
        foreach (var dispatcher in open) dispatcher.BeginDraining();
        try { await Task.WhenAll(open.Select(dispatcher => dispatcher.WhenAnsweredAsync())).WaitAsync(drain).ConfigureAwait(false); }
        catch (TimeoutException) { log("server stop clients_still_asking"); }
        await listening.DisposeAsync().ConfigureAwait(false);
        served.Flush();
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
}
