namespace MessageLink.Presentation.Services;

// Schedule on the owning context. Only the newest pending value survives a burst; FlushAsync also drains a write
// already in flight, so closing a window cannot leave its final layout behind an older write.
public sealed class DebouncedAsyncWriter<T>(Func<T, Task> write, TimeSpan delay)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? waiting;
    private T? latest;
    private bool pending;

    public void Schedule(T value)
    {
        latest = value;
        pending = true;
        waiting?.Cancel();
        var next = new CancellationTokenSource();
        waiting = next;
        _ = WaitAsync(next);
    }

    private async Task WaitAsync(CancellationTokenSource operation)
    {
        try { await Task.Delay(delay, operation.Token); await FlushAsync(); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(waiting, operation)) waiting = null; operation.Dispose(); }
    }

    public async Task FlushAsync()
    {
        waiting?.Cancel();
        await gate.WaitAsync();
        try
        {
            while (pending)
            {
                var value = latest!;
                pending = false;
                await write(value);
            }
        }
        finally { gate.Release(); }
    }
}
