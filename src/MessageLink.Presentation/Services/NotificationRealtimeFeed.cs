using System.Threading.Channels;
using MessageLink.Core.Backend;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// Incoming-message work does not depend on UI paging or consume a second provider connection. The host of the client modes runs it over the providers'
// feed with no observer (notifications are each window's), for its incoming unarchive alone (SWITCHBOARD.md stage 6, slice 9).
public sealed class NotificationRealtimeFeed(IRealtimeFeed inner, NotificationObserver? observer,
    IncomingMessageUnarchiver? unarchiver = null) : IRealtimeFeed
{
    private int dropped, reset;

    /// <summary>
    /// Null (the window): a full queue holds the feed until there is room, as it always did. Given (the host): a full queue drops the event from this work
    /// only, never from the feed, so a slow provider cannot hold up every client's events; this is told how many were dropped once there is room again.
    /// After a drop the work starts again from now, as after a reconnect, so nothing that was missed is taken for a new arrival later.
    /// </summary>
    public Action<int>? Skipped { get; init; }

    /// <summary>
    /// When the lookup for a new message's notification fails because the connection to what answers it was lost, the event is looked at again after each of
    /// these pauses, in turn. The feed's cursor moves past the event as soon as the client has handled it, so a reconnect does not bring it back: dropping it here
    /// would be a notification that never comes. Bounded so a connection that stays down is given up on; the identity claim (NotificationObserver) keeps a
    /// retry from showing one twice.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45)];

    /// <summary>How long after an event arrived a retry may still raise its notification; a message that is minutes old by then is no longer news.</summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Where a notification that failed is told, in fixed words: the exception's type and HRESULT, never its message, which can carry a name.</summary>
    public Action<string>? Log { get; init; }

    // One event of the queue. A retry carries what the first look decided (the arrival is returning to the inbox) and does not decide it again.
    private sealed record Work(RealtimeEvent Change, int Attempt = 0, bool LeftToHost = false);

    // The lookup failed because the connection did, not because of what was looked up.
    private static bool IsConnectionFailure(Exception failure) =>
        failure is BackendException { Failure: BackendFailure.Unavailable or BackendFailure.NotReady } or IOException or TimeoutException or System.Net.Http.HttpRequestException;

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queue = Channel.CreateBounded<Work>(128);
        // Looks at a failed arrival again after its next pause, through the queue so it never runs beside another event's look (the observer is one reader).
        void Retry(Work failed)
        {
            if (observer is null || failed.Change is not { Kind: RealtimeKind.MessageChanged, IsNewMessage: not false } change || failed.Attempt >= RetryDelays.Count) return;
            if (DateTimeOffset.UtcNow - (change.ReceivedAt ?? DateTimeOffset.UtcNow) > RetryWindow) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RetryDelays[failed.Attempt], lifetime.Token);
                    queue.Writer.TryWrite(failed with { Attempt = failed.Attempt + 1 });
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
        }
        var worker = Task.Run(async () =>
        {
            await foreach (var work in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                var change = work.Change;
                if (work.Attempt == 0 && Interlocked.Exchange(ref reset, 0) == 1)
                    try
                    {
                        var restart = new RealtimeEvent(RealtimeKind.ResyncRequired, ReceivedAt: DateTimeOffset.UtcNow);
                        if (unarchiver is not null) await unarchiver.DecideAsync(restart, lifetime.Token);
                        if (observer is not null) await observer.ObserveAsync(restart, lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch (Exception) { }
                var outcome = default(UnarchiveOutcome);
                // The archive change is decided once, with the first look; a retry is only of the notification.
                if (work.Attempt == 0)
                    try
                    {
                        if (unarchiver is not null && (outcome = await unarchiver.DecideAsync(change, lifetime.Token)).Unarchived)
                            await receive(new(RealtimeKind.ChatChanged, change.ChatId, Scope: change.Scope), lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch (Exception) { /* Keep the provider's state; never retry an uncertain archive change. */ }
                var leftToHost = work.Attempt == 0 ? outcome.LeftToHost : work.LeftToHost;
                // Read the confirmed archive state before deciding whether to show this arrival's notification. One the host is returning to the inbox
                // (stage 6 slice 9) is still archived here for a moment, and is judged as the inbox will show it.
                try { if (observer is not null) await observer.ObserveAsync(change, lifetime.Token, unarchiving: leftToHost); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception failure)
                {
                    /* A notification failure must not break messaging or retry a send. A lookup the lost connection failed is looked at again (Retry). */
                    var retry = IsConnectionFailure(failure);
                    try { Log?.Invoke($"notification show failed type={failure.GetType().Name} hresult=0x{failure.HResult:X8} retry={(retry ? "yes" : "no")}"); }
                    catch (Exception) { /* a logger that fails must not fail the feed */ }
                    if (retry) Retry(work with { LeftToHost = leftToHost });
                }
            }
        }, CancellationToken.None);
        try
        {
            await inner.RunAsync(async (change, token) =>
            {
                var queued = new Work(change with { ReceivedAt = change.ReceivedAt ?? DateTimeOffset.UtcNow });
                if (Skipped is not { } skipped) await queue.Writer.WriteAsync(queued, token);
                else if (!queue.Writer.TryWrite(queued)) { Interlocked.Increment(ref dropped); Volatile.Write(ref reset, 1); }
                else if (Interlocked.Exchange(ref dropped, 0) is > 0 and var count)
                    try { skipped(count); } catch (Exception) { /* a logger that fails must not fail the feed */ }
                await receive(change, token);
            }, lifetime.Token);
        }
        finally
        {
            lifetime.Cancel(); queue.Writer.TryComplete();
            try { await worker; } catch (OperationCanceledException) { }
        }
    }
}
