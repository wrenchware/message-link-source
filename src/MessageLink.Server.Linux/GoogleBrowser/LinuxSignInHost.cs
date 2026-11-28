using System.Collections.Concurrent;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.Server;

namespace MessageLink.Server.Linux;

/// <summary>
/// One thread that runs the work it is given in order, and whose awaits come back to it: what the keeper's decisions run on (its state is a single
/// thread's, as the window's interface thread and the Windows server's STA thread give it). Plain .NET: on Linux there is no browser thread to borrow,
/// since each renewal's Chromium is its own process.
/// </summary>
internal sealed class SerialQueue : IDisposable
{
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private readonly Action<string> log;

    public SerialQueue(string name, Action<string> log)
    {
        this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public bool OnThread => Thread.CurrentThread == thread;

    /// <summary>False once the queue is closing, and the work is dropped, as a dispatcher queue that is shutting down drops it.</summary>
    public bool TryEnqueue(Action action)
    {
        try { work.Add(action); return true; }
        // Adding after the queue was closed (ObjectDisposedException is one of these).
        catch (InvalidOperationException) { return false; }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var action in work.GetConsumingEnumerable())
        {
            try { action(); }
            // One piece of work failing does not end the others. The type only.
            catch (Exception failure) { log($"server sign-in queue failed type={failure.GetType().Name}"); }
        }
    }

    public void Dispose() => work.CompleteAdding();

    private sealed class Context(SerialQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (queue.OnThread) d(state);
            else throw new NotSupportedException("send_from_other_thread");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// What <see cref="GoogleSignInKeeper"/> asks of the Linux host: the Windows server's answers (<c>ServerSignInHost</c>, which is tied to its STA queue
/// type), on a <see cref="SerialQueue"/>. A dialog is open when a client said so; closed is the server stopping; the launch's own connect is the
/// restore's; reconnecting connects Google Messages only; pausing sync does nothing (each client's own); and a renewal's end publishes a Google-scoped
/// resync, so every client re-reads after the helper swap.
/// </summary>
internal sealed class LinuxSignInHost(SerialQueue queue, ServerClientState clients, Func<CancellationToken, Task> connect, CancellationToken stop,
    Action<string> log, Action? resync = null) : IGoogleSignInHost
{
    private readonly TaskCompletionSource startupLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int connecting;

    public bool Closed => stop.IsCancellationRequested;
    public bool UserBusy => clients.Busy;
    public Task StartupLoad => startupLoad.Task;
    public void Post(Action action) => queue.TryEnqueue(action);
    public Task PauseSyncAsync() => Task.CompletedTask;

    public void ResumeSync()
    {
        try { resync?.Invoke(); }
        catch (Exception failure) { log($"server sign-in resync failed type={failure.GetType().Name}"); }
    }

    public Task ReconnectAsync() => ConnectAsync();
    public void ReloadAfterRelease() => _ = ConnectAsync();

    /// <summary>Marks the Google connect as running; null when one already is (the restore holds it around its own connect).</summary>
    public IDisposable? BeginConnect() => Interlocked.CompareExchange(ref connecting, 1, 0) == 0 ? new Connecting(this) : null;

    public void EndStartupLoad() => startupLoad.TrySetResult();

    private async Task ConnectAsync()
    {
        if (BeginConnect() is not { } lease) return;
        using (lease)
        {
            try { await connect(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception failure) { log($"server sign-in reconnect failed type={failure.GetType().Name}"); }
        }
    }

    private sealed class Connecting(LinuxSignInHost owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Volatile.Write(ref owner.connecting, 0);
        }
    }
}
