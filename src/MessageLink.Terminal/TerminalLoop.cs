using System.Collections.Concurrent;

namespace MessageLink.Terminal;

/// <summary>
/// The one thread mlink's console session lives on, as the window's interface thread was for the console window. The session captures
/// <see cref="SynchronizationContext.Current"/> when it is made and brings every event back here, so the session, the feed pump and the terminal's
/// drawing all run on this thread and never race. Keys are read on a thread of their own and posted here.
/// </summary>
public sealed class TerminalLoop : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly int threadId = Environment.CurrentManagedThreadId;

    private TerminalLoop() { }

    /// <summary>Whether the caller is on the loop's thread.</summary>
    public bool OnLoop => Environment.CurrentManagedThreadId == threadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { queue.Add((d, state)); }
        // Posted after the loop ended (a late event as the program quits): there is nothing left to run it for.
        catch (InvalidOperationException) { }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (OnLoop) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (failure is not null) throw new AggregateException(failure);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="main"/> on a new loop on the calling thread until it completes, and returns its result.</summary>
    public static int Run(Func<Task<int>> main)
    {
        var previous = Current;
        var loop = new TerminalLoop();
        SetSynchronizationContext(loop);
        try
        {
            var task = main();
            task.ContinueWith(_ => loop.queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in loop.queue.GetConsumingEnumerable()) callback(state);
            return task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
