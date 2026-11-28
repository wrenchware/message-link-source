using MessageLink.Core.Backend;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>A started feed: where the server said it begins, and the items as they arrive, in order.</summary>
public sealed class ClientEvents
{
    private readonly EventQueue queue;

    internal ClientEvents(EventsSubscribed answer, EventQueue queue)
    {
        Epoch = answer.Epoch;
        Head = answer.Head;
        Resync = answer.Resync;
        this.queue = queue;
    }

    public string Epoch { get; }
    public long Head { get; }
    /// <summary>The first item is a resync: the server could not resume this reader from where it was.</summary>
    public bool Resync { get; }

    /// <summary>
    /// The next item, waiting for one. Null when the feed was replaced or ended on purpose. When the connection is lost it first
    /// returns what had arrived, then throws an unavailable <see cref="BackendException"/>.
    /// </summary>
    public Task<EventItem?> ReadAsync(CancellationToken cancellationToken = default) => queue.ReadAsync(cancellationToken);
}

/// <summary>
/// The realtime feed over the wire: the client's end of <c>events.subscribe</c>. It hands each event to <c>receive</c> in the
/// server's order and waits for it to finish before handing over the next. A resync from the server (it could not resume this
/// reader, or the reader fell behind) is delivered as <see cref="RealtimeKind.ResyncRequired"/>, which is what makes the list read
/// everything again. It remembers the epoch and sequence number of the last event <c>receive</c> finished with, so a new
/// subscription, on the same client or one that reconnected, resumes there; an event whose handling failed is delivered again.
/// </summary>
/// <remarks>
/// <para>Like the composite of providers it stands in for, this feed does not end because the connection did: a lost connection is a
/// <see cref="RealtimeKind.Disconnected"/> for everything (no scope), then, after a pause, a new subscription. <see cref="StreamAsync"/>
/// is the part that fails the way a provider's feed does, by throwing.
/// </para>
/// <para>The server's state (<c>state</c> events) goes to <paramref name="state"/>, not to <c>receive</c>: the
/// application never sees it as an event, and a client given no cache ignores it. A state is applied before the event that follows it
/// is delivered, so a reader told "connected" already holds a state that says so. A lost connection marks the cache lost, since
/// nothing can be known to be connected through a server that cannot be reached.</para>
/// </remarks>
public sealed class RealtimeFeedProxy(ProtocolClient client, ServerStateCache? state = null) : IRealtimeFeed
{
    // The composite's pause after a provider fails.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    private string? epoch;
    private long? since;

    /// <summary>The epoch and sequence number of the last event delivered, or none yet.</summary>
    public (string? Epoch, long? Since) Position => (epoch, since);

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var failure = RealtimeKind.Disconnected;
            try { await StreamAsync(receive, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (BackendException error) when (error.Failure == BackendFailure.AuthenticationRequired) { failure = RealtimeKind.AuthenticationRequired; }
            catch (Exception) { }
            // Before the application hears "disconnected", so what it then reads agrees with it.
            state?.MarkLost();
            await receive(new RealtimeEvent(failure, ClientLink: failure == RealtimeKind.Disconnected ? true : null), cancellationToken).ConfigureAwait(false);
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One subscription's worth of the feed. Never returns normally: it ends by throwing, an unavailable <see cref="BackendException"/> when
    /// the connection is lost or the server stopped the feed, the server's own error if it refused, or an
    /// <see cref="OperationCanceledException"/> when cancelled (after telling the server to stop).
    /// </summary>
    public async Task StreamAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        // A client with a cache is the application's, which shows who is typing; it asks for that beside the events (TypingProxy hears it).
        var events = await client.SubscribeEventsAsync(since, epoch, cancellationToken, typing: state is not null).ConfigureAwait(false);
        try
        {
            state?.Resubscribed(events.Resync);
            while (true)
            {
                var item = await events.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new BackendException(BackendFailure.Unavailable, ProtocolClient.LostSentence);
                // A state is not an event the application handles; it is the cache's, and the position moves past it like any item.
                if (item.State is { } pushed) state?.Apply(pushed, events.Epoch, item.Seq);
                // Nor is an account signal (SimsChanged and the like): it is raised on the cache, which is where the account proxies listen.
                else if (item.Signal is { } signal) state?.Signal(signal);
                // Nor is a catch-up: it goes to whoever prepares pictures ahead (CaughtUpProxy).
                else if (item.CaughtUp is { } caughtUp) state?.NoteCaughtUp(caughtUp);
                else await receive(item.Event ?? new RealtimeEvent(RealtimeKind.ResyncRequired), cancellationToken).ConfigureAwait(false);
                (epoch, since) = (events.Epoch, item.Seq);
            }
        }
        finally
        {
            // Awaited, so a subscription made next on this client cannot be undone by this one arriving late. A server that does not
            // answer does not hold the cancel up for long.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await client.UnsubscribeEventsAsync(patience.Token).ConfigureAwait(false); } catch (Exception) { }
        }
    }
}
