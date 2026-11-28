using System.Threading.Channels;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// What a provider has changed that nobody has read yet, for its realtime feed. Bounded and dropping the oldest, so a
// backlog nobody is reading never holds up the helper's own event pump. A dropped event is a change the list never hears
// of, so the next event the reader gets is a ResyncRequired, once however many were dropped: the app then re-reads, as it
// does for Google Messages when its queue overflows. The newest events follow it, in order.
internal sealed class RealtimeBuffer
{
    private readonly Channel<RealtimeEvent> channel;
    private readonly Action<string>? log;
    private int dropped;

    // `log` takes a line without the provider's own prefix: counts and fixed words only.
    public RealtimeBuffer(int capacity, Action<string>? log = null)
    {
        this.log = log;
        channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref dropped));
    }

    public ChannelWriter<RealtimeEvent> Writer => channel.Writer;

    public bool TryWrite(RealtimeEvent change) => channel.Writer.TryWrite(change);

    // Delivers events in order until cancelled or the buffer is completed. The overflow is looked for after each read, so the
    // resync comes ahead of the first event the drop left behind.
    public async Task DrainAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
            while (channel.Reader.TryRead(out var change))
            {
                if (Interlocked.Exchange(ref dropped, 0) is > 0 and var count)
                {
                    try { log?.Invoke($"realtime overflow dropped={count} action=resync"); } catch (Exception) { /* a logger must not stop the feed */ }
                    await receive(new(RealtimeKind.ResyncRequired), cancellationToken);
                }
                await receive(change, cancellationToken);
            }
    }
}
