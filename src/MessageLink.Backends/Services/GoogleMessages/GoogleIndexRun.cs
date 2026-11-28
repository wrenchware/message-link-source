namespace MessageLink.App.Services.GoogleMessages;

internal enum GoogleIndexStart { Started, Queued, AlreadyRunning, Refused }

// The history crawl, one run at a time. A stopped run can still be inside a helper call it lets finish, or in archive
// warm-up, so turning indexing off and on again queues a fresh run behind it rather than doing nothing until the next
// reconnect.
internal sealed class GoogleIndexRun
{
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task task = Task.CompletedTask;

    public bool IsRunning { get { lock (gate) return !task.IsCompleted; } }

    // allowed is asked under the lock, so a stop that has begun cannot be followed by a start.
    public GoogleIndexStart Start(Func<bool> allowed, Func<CancellationToken, Task> run)
    {
        lock (gate)
        {
            if (!allowed()) return GoogleIndexStart.Refused;
            if (!task.IsCompleted)
            {
                // A live run already covers it. The cancelled one's source is not disposed, because that run may still be
                // looking at its token.
                if (stop is not { IsCancellationRequested: true }) return GoogleIndexStart.AlreadyRunning;
                stop = new CancellationTokenSource();
                var next = stop.Token;
                task = task.ContinueWith(_ => run(next), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                return GoogleIndexStart.Queued;
            }
            stop?.Dispose();
            stop = new CancellationTokenSource();
            var token = stop.Token;
            task = Task.Run(() => run(token));
            return GoogleIndexStart.Started;
        }
    }

    // Asks the current run to stop and returns it, to wait on.
    public Task Cancel() { lock (gate) { stop?.Cancel(); return task; } }
}
