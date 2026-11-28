using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

// Each feed owns its lifetime. A failed helper reports its own scope and never cancels another provider's feed.
internal sealed class CompositeRealtimeFeed(Func<bool> googleMessagesExpected, params (string Scope, IRealtimeFeed Feed)[] feeds) : IRealtimeFeed
{
    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        using var turn = new SemaphoreSlim(1, 1);
        async Task Deliver(RealtimeEvent change, string scope, CancellationToken token)
        {
            await turn.WaitAsync(token);
            try { await receive(change with { Scope = change.Scope ?? scope }, token); }
            finally { turn.Release(); }
        }
        async Task Pump(string scope, IRealtimeFeed feed)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var failure = RealtimeKind.Disconnected;
                try { await feed.RunAsync((change, token) => Deliver(change, scope, token), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (MessageLink.Core.Backend.BackendException error) when (error.Failure == MessageLink.Core.Backend.BackendFailure.AuthenticationRequired)
                { failure = RealtimeKind.AuthenticationRequired; }
                catch (Exception) { }
                await Deliver(new(failure), scope, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        await Task.WhenAll(feeds.Where(feed => feed.Scope != "google-direct:" || googleMessagesExpected())
            .Select(feed => Pump(feed.Scope, feed.Feed)));
    }
}
