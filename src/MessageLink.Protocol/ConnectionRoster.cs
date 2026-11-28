namespace MessageLink.Protocol;

/// <summary>
/// The protocol versions of a server's open connections, as their hellos chose them. The server's incoming unarchive reads it: while a client
/// older than <see cref="ProtocolVersion.ServerUnarchives"/> is connected, that client still returns group conversations to the inbox itself, so the server leaves
/// them to it rather than ask the provider a second time.
/// </summary>
public sealed class ConnectionRoster
{
    private readonly object gate = new();
    private readonly Dictionary<object, int> open = new(ReferenceEqualityComparer.Instance);

    /// <summary>A connection's hello chose <paramref name="version"/>.</summary>
    public void Opened(object connection, int version)
    {
        lock (gate) open[connection] = version;
    }

    /// <summary>The connection ended; one that never greeted changes nothing.</summary>
    public void Closed(object connection)
    {
        lock (gate) open.Remove(connection);
    }

    /// <summary>How many open connections speak a version below <paramref name="version"/>.</summary>
    public int Below(int version)
    {
        lock (gate) return open.Values.Count(value => value < version);
    }
}
