using System.Threading.Channels;

namespace MessageLink.Protocol;

/// <summary>
/// One end of a connection that carries whole frames as text, in order. A socket, a pipe and the in-process loopback
/// all look like this, so everything above it serializes and parses exactly as it would on a real wire.
/// </summary>
public interface IFrameChannel
{
    /// <summary>Frames from the other end, oldest first. Completes once the connection is closed and what was sent has been read.</summary>
    ChannelReader<string> Incoming { get; }

    /// <summary>Sends one frame. Throws <see cref="ChannelClosedException"/> when the connection is closed.</summary>
    ValueTask SendAsync(string frame, CancellationToken cancellationToken = default);

    /// <summary>Ends the connection for both ends. Frames already sent are still read by the other end. Safe to call twice.</summary>
    void Close();
}

public static class LoopbackChannel
{
    /// <summary>
    /// Where <see cref="CreatePair"/> gets its ends instead of making them in memory. Only the conformance harness sets it, to run the
    /// same suites over a real pipe; a server never does.
    /// </summary>
    internal static Func<(IFrameChannel Client, IFrameChannel Server)>? Transport { get; set; }

    /// <summary>Two connected ends: in this process, or over <see cref="Transport"/>. Frames cross as strings, never as objects.</summary>
    public static (IFrameChannel Client, IFrameChannel Server) CreatePair() => Transport is { } transport ? transport() : CreateInProcessPair();

    /// <summary>Two connected ends in this process, whatever <see cref="Transport"/> says. Frames cross as strings, never as objects.</summary>
    public static (IFrameChannel Client, IFrameChannel Server) CreateInProcessPair()
    {
        var toServer = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var toClient = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        void Close()
        {
            toServer.Writer.TryComplete();
            toClient.Writer.TryComplete();
        }
        return (new End(toClient.Reader, toServer.Writer, Close), new End(toServer.Reader, toClient.Writer, Close));
    }

    private sealed class End(ChannelReader<string> incoming, ChannelWriter<string> outgoing, Action close) : IFrameChannel
    {
        public ChannelReader<string> Incoming => incoming;
        public ValueTask SendAsync(string frame, CancellationToken cancellationToken = default) => outgoing.WriteAsync(frame, cancellationToken);
        public void Close() => close();
    }
}
