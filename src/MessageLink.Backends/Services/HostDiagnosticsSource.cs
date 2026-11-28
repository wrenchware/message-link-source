using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;
using MessageLink.GoogleMessages.Spike;

namespace MessageLink.App.Services;

// The host's half of the console's /top and /diag, the memory line's cache sizes and Connection health's helper lines
// (IHostDiagnostics): what the helper registry and the backends' own caches say, in this process. A server process serves the same
// object over host.diagnostics, so a client reads the host's helpers and caches, not its own empty copies of them.
//
// Counts, sizes, times and fixed words only. The helper registry already keeps nothing else (HelperConnection.Snapshot), and a holder
// reports a number. The services are looked up when asked, so a host that registers fewer of them (a synthetic provider) leaves a holder out.
internal sealed class HostDiagnosticsSource(IServiceProvider services) : IHostDiagnostics
{
    // Answers at once: nothing here waits, so a caller on the interface thread (Connection health) is not held up.
    public Task<HostDiagnostics> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Read());

    internal HostDiagnostics Read() => new(Helpers(), Holdings());

    private static List<HostHelper> Helpers()
    {
        IReadOnlyList<HelperStatus> statuses;
        try { statuses = HelperConnection.Snapshot(); } catch (Exception) { statuses = []; }
        var helpers = new List<HostHelper>(statuses.Count);
        foreach (var status in statuses)
        {
            int? processId = null;
            long? workingSet = null, cpu = null;
            if (status.Running && status.ProcessId is { } pid)
            {
                processId = pid;
                // The helper may have ended since the snapshot; then it has no numbers.
                try
                {
                    using var process = Process.GetProcessById(pid);
                    workingSet = process.WorkingSet64;
                    cpu = (long)process.TotalProcessorTime.TotalMilliseconds;
                }
                catch (Exception) { }
            }
            helpers.Add(new HostHelper(status.Scope, status.StartedAt, status.Starts,
                status.LastExit is { } exit ? new HostHelperExit(exit.How, exit.Code, exit.At) : null, status.Running, processId, workingSet, cpu));
        }
        return helpers;
    }

    // The long-lived holdings a cache survey found unbounded (Google's in-memory messages and archive index, Messenger's kept-history
    // snapshot, loaded on the first search and kept) and the largest bounded one, in the order the memory line prints them.
    private List<HostHolding> Holdings()
    {
        var holdings = new List<HostHolding>();
        if (services.GetService<GoogleMessagesBackend>() is { } google) holdings.Add(new("google_messages", google.HeldMessages));
        if (services.GetService<GoogleArchive>() is { } archive) holdings.Add(new("google_archive", archive.MessageCount));
        if (services.GetService<GoogleMediaBackend>() is { } media)
        { var (count, bytes) = media.HeldImages; holdings.Add(new("google_media", count, bytes)); }
        if (services.GetService<MessengerKeptHistory>() is { } kept) holdings.Add(new("messenger_kept", kept.HeldMessages));
        return holdings;
    }
}
