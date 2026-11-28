using System.Collections.Concurrent;
using MessageLink.Presentation.Services;

namespace MessageLink.Server;

/// <summary>
/// One thread that runs the server's scheduled-send engine, as the interface thread runs it in the app. <see cref="ScheduledSendEngine"/> is
/// written for one thread that interleaves only at an await (its lists are plain, and every check after an await re-reads the message it was about): the app
/// gives it the interface thread, and a server has none, so it is given this. Its passes (<see cref="LocalAlarm"/>), every command a client makes of it and the
/// outlet's answers all run here, one step at a time, so a Send now, a Cancel and the alarm meeting on one message meet as they do in the app.
/// </summary>
internal sealed class ServerSerialContext : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

    public ServerSerialContext()
    {
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "Messages scheduled sends" };
        thread.Start();
    }

    public Task InvokeAsync(Func<Task> action) => Run(async () => { await action().ConfigureAwait(true); return true; });

    /// <summary>
    /// Runs <paramref name="action"/> on the thread. Cancelled, never run, once the context is disposed, and when <paramref name="cancellationToken"/> is already
    /// cancelled by the time its turn comes; once it has begun it is not cancelled by the token (a command that began is the server's to finish, and its journal
    /// settles what a caller that left was told).
    /// </summary>
    public Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(async _ =>
            {
                if (cancellationToken.IsCancellationRequested) { done.SetCanceled(cancellationToken); return; }
                try { done.SetResult(await action().ConfigureAwait(true)); }
                catch (Exception failure) { done.SetException(failure); }
            }, null))
            done.SetCanceled();
        return done.Task;
    }

    // After Dispose nothing runs: a continuation still arriving from a send that outlived the server is dropped.
    private bool Post(SendOrPostCallback callback, object? state)
    {
        try { queue.Add((callback, state)); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose() => queue.CompleteAdding();

    private sealed class Context(ServerSerialContext owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(callback, state);
        public override SynchronizationContext CreateCopy() => this;
    }
}
