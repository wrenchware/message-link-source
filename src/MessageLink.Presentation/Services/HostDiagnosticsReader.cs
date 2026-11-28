using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A synchronous view of <see cref="IHostDiagnostics"/> for the surfaces that cannot wait: the console's <c>/top</c> asks once a second
/// and its host members are properties. <see cref="Latest"/> returns the newest answer it holds and starts one new read if none is on its
/// way. A port that answers at once (a host in this process) is read afresh on every call, so nothing is stale; one that crosses a wire
/// answers a call later, and the first call after a quiet spell sees the previous answer (or none, before the first). A read that fails
/// leaves the last answer in place. Never blocks on the port.
/// </summary>
public sealed class HostDiagnosticsReader(IHostDiagnostics port)
{
    private static readonly HostDiagnostics None = new([], []);

    private readonly object gate = new();
    private HostDiagnostics latest = None;
    private Task<HostDiagnostics>? pending;

    public HostDiagnostics Latest()
    {
        lock (gate)
        {
            if (pending is null)
            {
                Task<HostDiagnostics> task;
                try { task = port.ReadAsync(); }
                catch (Exception) { return latest; }
                if (task.IsCompleted) Adopt(task);
                else
                {
                    pending = task;
                    _ = task.ContinueWith(Settled, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
            }
            return latest;
        }
    }

    /// <summary>The host's answer now, for the caller that can wait for it (Connection health). A failure is the caller's to handle.</summary>
    public async Task<HostDiagnostics> ReadAsync(CancellationToken cancellationToken = default)
    {
        var read = await port.ReadAsync(cancellationToken).ConfigureAwait(true);
        lock (gate) latest = read;
        return read;
    }

    private void Settled(Task<HostDiagnostics> task)
    {
        lock (gate)
        {
            Adopt(task);
            pending = null;
        }
    }

    // A failed read is observed here and keeps the previous answer.
    private void Adopt(Task<HostDiagnostics> task)
    {
        if (task.IsCompletedSuccessfully) latest = task.Result;
        else _ = task.Exception;
    }
}
