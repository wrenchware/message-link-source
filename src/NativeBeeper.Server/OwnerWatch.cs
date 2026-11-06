namespace NativeBeeper.Server;

/// <summary>
/// Stops a server whose owner has gone (SWITCHBOARD.md 3.2c). While the owner process lives it does nothing. When it exits it waits
/// <see cref="Grace"/> for a client to come back (an app that was restarted, or one that is reconnecting); if no client is connected at the end,
/// the server stops, as it would for <c>--quit</c>. A client that is connected at the end keeps the server, and the grace starts again the next time
/// none is, so a server whose owner is gone is never left running with nobody using it. A server with no owner never creates one of these.
/// </summary>
internal sealed class OwnerWatch : IDisposable
{
    private readonly Task ownerExited;
    private readonly Func<int> connections;
    private readonly Action<string> stop;
    private readonly Action<string> log;
    private readonly Func<TimeSpan, CancellationToken, Task> wait;
    private readonly CancellationTokenSource cancel = new();
    private readonly SemaphoreSlim changed = new(0);
    private Task? running;

    /// <param name="ownerExited">Completes when the owner process has ended (or was never there to watch).</param>
    /// <param name="connections">How many clients are connected now.</param>
    /// <param name="stop">Starts the server's stop for a reason word.</param>
    /// <param name="wait">How a grace is waited out; the harness gives one it completes by hand.</param>
    public OwnerWatch(Task ownerExited, Func<int> connections, Action<string> stop, Action<string> log, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        this.ownerExited = ownerExited; this.connections = connections; this.stop = stop; this.log = log;
        this.wait = wait ?? Task.Delay;
    }

    /// <summary>How long a returning client is waited for after the owner has gone.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The task that ends when the watch has decided (it stopped the server) or been disposed.</summary>
    public Task Completion => running ?? Task.CompletedTask;

    public void Start() => running = Task.Run(RunAsync);

    /// <summary>The number of connected clients changed.</summary>
    public void ConnectionsChanged() => changed.Release();

    /// <summary>
    /// The owner process, as a task that completes when it ends. A process that is already gone, or cannot be watched, completes at once: the grace then
    /// decides. Process ids are reused, so the process found under the id is trusted as the owner only if it is plausibly the one that started this server:
    /// it began no later than this server did (<paramref name="serverStart"/>, this process's own start by default) and its image is
    /// <paramref name="image"/> (the app's <c>Messages</c>). Anything else, or a start time or image that cannot be read, is an owner that is gone.
    /// </summary>
    public static Task WatchProcess(int processId, Action<string> log, DateTime? serverStart = null, string image = "Messages")
    {
        System.Diagnostics.Process process;
        try { process = System.Diagnostics.Process.GetProcessById(processId); }
        // Whatever it is that cannot be opened, the owner is not one this process can watch.
        catch (Exception)
        {
            log("server owner not found");
            return Task.CompletedTask;
        }
        string? untrusted = null;
        try
        {
            using var self = serverStart is null ? System.Diagnostics.Process.GetCurrentProcess() : null;
            var started = (serverStart ?? self!.StartTime).ToUniversalTime();
            if (process.StartTime.ToUniversalTime() > started) untrusted = "start";
            else if (!string.Equals(process.ProcessName, image, StringComparison.OrdinalIgnoreCase)) untrusted = "image";
        }
        catch (Exception) { untrusted = "unreadable"; }
        if (untrusted is not null)
        {
            process.Dispose();
            // A fixed word, never the name or the id of whatever holds that id now.
            log($"server owner not trusted reason={untrusted}");
            return Task.CompletedTask;
        }
        return Task.Run(async () =>
        {
            using (process)
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (Exception) { log("server owner unwatchable"); }
            }
        });
    }

    private async Task RunAsync()
    {
        try
        {
            await ownerExited.WaitAsync(cancel.Token).ConfigureAwait(false);
            log("server owner gone");
            while (true)
            {
                await wait(Grace, cancel.Token).ConfigureAwait(false);
                if (connections() == 0)
                {
                    stop("owner_gone");
                    return;
                }
                log("server owner gone client_connected=true");
                // A client is using it, so it stays; the grace comes round again the next time nobody is.
                while (connections() > 0) await changed.WaitAsync(cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => cancel.Cancel();
}
