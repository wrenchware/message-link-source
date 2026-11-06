namespace NativeBeeper.WindowsPlatform;

/// <summary>
/// The thread WebView2 lives on, as the browser needs to see it: whether the caller is already there, and a way to put work on it. The app
/// supplies one over its interface thread's dispatcher queue and MessagesServer.exe one over its own STA thread (SWITCHBOARD.md 3.1c), so the
/// browser code names neither WinUI nor the Windows App SDK.
/// </summary>
internal interface IStaQueue
{
    /// <summary>The caller is on the queue's thread.</summary>
    bool OnThread { get; }

    /// <summary>Queues <paramref name="work"/> to run on the thread, at the queue's ordinary priority. False when the queue will not take it (it is shutting down).</summary>
    bool TryEnqueue(Action work);

    /// <summary>
    /// Runs <paramref name="action"/> on the thread and hands back its result. Already on the thread it runs as it is, synchronously, as Google
    /// Messages' renewals in the window always did; from anywhere else it is queued. A queue that will not take it (the app is closing) cancels it.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (OnThread) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception failure) { completion.TrySetException(failure); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }
}

/// <summary>
/// Makes a thread's awaits come back to it: the context a UI framework installs on its thread by itself (WinUI does), for a thread that has only a
/// queue. Without it every await in the browser code resumes on the pool, and WebView2's objects belong to the thread that made them
/// (experiments/GoogleBrowserSpike).
/// </summary>
internal sealed class StaSynchronizationContext(IStaQueue queue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));

    // Blocking another thread on this one is how a deadlock starts, and nothing here asks for it.
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (queue.OnThread) d(state);
        else throw new NotSupportedException("send_from_other_thread");
    }

    public override SynchronizationContext CreateCopy() => new StaSynchronizationContext(queue);
}
