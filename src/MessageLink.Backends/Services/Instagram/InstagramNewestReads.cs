namespace MessageLink.App.Services.Instagram;

/// <summary>How a newest-page read was answered: by asking the helper, by sharing a read already asking, or from the page just read.</summary>
internal enum InstagramNewestAnswer { Asked, Shared, Held }

/// <summary>
/// The newest page of a conversation, read once for everyone who asks at about the same time. Each such read is a real request to Instagram (the helper's
/// <c>ui.messages</c> with no cursor is a GetThread), and after a server restart the server's logs showed 7 to 16 of them for one conversation within 30 s
/// (2026-10-14). So reads of the same conversation on the same helper connection that overlap share one call, and one asked again within <see cref="Reuse"/>
/// of the last answer is given that answer, unless anything was announced for the conversation since (<see cref="Announced"/>): then it is read again.
/// </summary>
/// <remarks>
/// Only the page as the helper gave it is held; whoever is answered maps it with the read positions of that moment, so a reuse never shows older receipts.
/// A read that began before an announcement is neither shared with a later asker nor held. Bounded, least recently read out first.
/// </remarks>
internal sealed class InstagramNewestReads(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Reuse = TimeSpan.FromSeconds(5);
    private const int Limit = 64;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object gate = new();

    private sealed class Flight(object connection, long since)
    {
        public object Connection { get; } = connection;
        // The announcement count when the read began: anything announced for its conversation after this makes the read too old to share or hold.
        public long Since { get; } = since;
        public TaskCompletionSource<InstagramMessagePage> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancel { get; } = new();
        public int Waiting;
    }

    private sealed record Held(object Connection, InstagramMessagePage Page, DateTimeOffset At, long Order);

    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Held> held = new(StringComparer.Ordinal);
    // The announcement count at each conversation's last announcement. Bounded: one that falls out leaves its count as the floor every older read is judged by.
    private readonly Dictionary<string, long> announced = new(StringComparer.Ordinal);
    private long announcements, forgotten, order;

    /// <summary>
    /// Something about the conversation was announced (a poll found it moved, an arrival, this app's own send or change in it): the next newest-page read of it
    /// asks the helper, and a read already under way is not shared with anyone who asks after this.
    /// </summary>
    public void Announced(string chatId)
    {
        lock (gate)
        {
            announced[chatId] = ++announcements;
            held.Remove(chatId);
            if (announced.Count > Limit * 4)
            {
                var oldest = announced.MinBy(pair => pair.Value);
                forgotten = Math.Max(forgotten, oldest.Value);
                announced.Remove(oldest.Key);
            }
        }
    }

    public void Clear()
    {
        lock (gate) held.Clear();
    }

    /// <summary>
    /// The conversation's newest page as <paramref name="connection"/> holds it: from <paramref name="read"/>, from a read of it already under way, or from the
    /// last one. A read is ended only when everyone waiting on it stopped waiting; one that fails fails everyone waiting on it, and nothing is held from it.
    /// </summary>
    public async Task<(InstagramMessagePage Page, InstagramNewestAnswer Answer)> ReadAsync(object connection, string chatId,
        Func<CancellationToken, Task<InstagramMessagePage>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight;
        var asks = false;
        lock (gate)
        {
            var at = now();
            if (held.TryGetValue(chatId, out var kept) && ReferenceEquals(kept.Connection, connection) && at >= kept.At && at - kept.At < Reuse)
                return (kept.Page, InstagramNewestAnswer.Held);
            if (!flights.TryGetValue(chatId, out flight!) || !ReferenceEquals(flight.Connection, connection) || LastAnnounced(chatId) > flight.Since)
            {
                flight = new Flight(connection, announcements);
                flights[chatId] = flight;
                asks = true;
            }
            flight.Waiting++;
        }
        if (asks) _ = RunAsync(chatId, flight, read);
        try
        {
            return (await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false), asks ? InstagramNewestAnswer.Asked : InstagramNewestAnswer.Shared);
        }
        finally
        {
            var abandoned = false;
            lock (gate)
            {
                // The last one waiting went before the answer came: the read is stopped, as a caller's own read was before it was shared.
                if (--flight.Waiting == 0 && !flight.Result.Task.IsCompleted)
                {
                    if (flights.TryGetValue(chatId, out var current) && ReferenceEquals(current, flight)) flights.Remove(chatId);
                    abandoned = true;
                }
            }
            // Outside the lock: cancelling runs the read's own callbacks.
            if (abandoned) flight.Cancel.Cancel();
        }
    }

    private async Task RunAsync(string chatId, Flight flight, Func<CancellationToken, Task<InstagramMessagePage>> read)
    {
        try
        {
            var page = await read(flight.Cancel.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (flights.TryGetValue(chatId, out var current) && ReferenceEquals(current, flight)) flights.Remove(chatId);
                if (LastAnnounced(chatId) <= flight.Since)
                {
                    held[chatId] = new(flight.Connection, page, now(), ++order);
                    if (held.Count > Limit) held.Remove(held.MinBy(pair => pair.Value.Order).Key);
                }
            }
            flight.Result.TrySetResult(page);
        }
        catch (Exception failure)
        {
            lock (gate) if (flights.TryGetValue(chatId, out var current) && ReferenceEquals(current, flight)) flights.Remove(chatId);
            if (failure is OperationCanceledException cancelled) flight.Result.TrySetCanceled(cancelled.CancellationToken);
            else flight.Result.TrySetException(failure);
            // Nobody may be waiting any more: a failure nobody reads is not an unobserved one.
            _ = flight.Result.Task.Exception;
        }
    }

    // Caller holds gate.
    private long LastAnnounced(string chatId) => announced.TryGetValue(chatId, out var at) ? at : forgotten;
}
