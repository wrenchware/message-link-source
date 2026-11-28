namespace MessageLink.Protocol;

/// <summary>
/// A client and a dispatcher joined by an in-process <see cref="LoopbackChannel"/>: every call is serialized to text,
/// parsed on the other side, run against <see cref="ProtocolServices"/> and answered the same way, without leaving the
/// process. It proves the protocol with no network (SWITCHBOARD.md 2.4). The host owns it and disposes it at shutdown,
/// which flushes the call measurements.
/// </summary>
public sealed class LoopbackProtocol : IDisposable
{
    private LoopbackProtocol(ProtocolClient client, ProtocolDispatcher dispatcher, Task serving, ProtocolCallStats calls, ProtocolCallStats served)
    {
        Client = client; Dispatcher = dispatcher; Serving = serving; Calls = calls; Served = served;
    }

    public ProtocolClient Client { get; }
    public ProtocolDispatcher Dispatcher { get; }
    public OperationJournal Journal => Dispatcher.Journal;
    /// <summary>Completes when the dispatcher has stopped reading (the connection closed).</summary>
    public Task Serving { get; }
    /// <summary>What the caller waited for, per method (<c>protocol calls</c> lines).</summary>
    public ProtocolCallStats Calls { get; }
    /// <summary>What the host spent inside the port, per method (<c>protocol served</c> lines); the difference from <see cref="Calls"/> is the transport.</summary>
    public ProtocolCallStats Served { get; }

    /// <summary>
    /// <paramref name="journal"/> is the host's, which outlives this connection: pass the same one to a later connection so it
    /// finds what this one started. Left out, the connection gets its own.
    /// </summary>
    public static LoopbackProtocol Start(ProtocolServices services, string appVersion, Action<string>? log = null, OperationJournal? journal = null,
        HostStateCache? state = null)
    {
        var (clientEnd, serverEnd) = LoopbackChannel.CreatePair();
        var calls = new ProtocolCallStats("protocol calls", log);
        var served = new ProtocolCallStats("protocol served", log);
        var dispatcher = new ProtocolDispatcher(serverEnd, services, appVersion, served, log, journal);
        var serving = dispatcher.Start();
        // `state`: the client's cache, which applies a state the moment its frame is read, ahead of the answer that follows it.
        return new LoopbackProtocol(new ProtocolClient(clientEnd, appVersion, stats: calls, log: log, cache: state), dispatcher, serving, calls, served);
    }

    public void Dispose()
    {
        Served.Flush();
        Client.Dispose();
    }
}
