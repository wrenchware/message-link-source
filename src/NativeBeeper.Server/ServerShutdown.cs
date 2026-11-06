using NativeBeeper.App.Services;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server;

/// <summary>
/// How long a stop on <see cref="ShutdownBudget.Normal"/> can take at worst, which is what <c>MessagesServer.exe --quit</c> waits for and what
/// <c>Stop-Messages.ps1</c> allows a server before it stops it by force: the restore's wait (the sends bound), the pipe (its listener, the clients' drain on
/// the sends bound, and the closes: <see cref="PipeHost.WorstStop"/>), the providers' shutdown (<see cref="ShutdownBudget.Total"/>), the event log's close
/// (2 s) and 5 s of slack. Forty-five seconds; a harness check fails if a bound changes without this.
/// </summary>
internal static class ServerStopBounds
{
    public static TimeSpan WorstCase { get; } = ShutdownBudget.Normal.Sends + PipeHost.WorstStop + ShutdownBudget.Normal.Sends + ShutdownBudget.Normal.Total + TimeSpan.FromSeconds(7);
}

/// <summary>
/// <c>host.shutdown</c> for the server (SWITCHBOARD.md 3.2c): the app's Quit asking the server it started to stop. It is <c>MessagesServer.exe --quit</c>
/// for a client on the pipe, so the same rule holds: a send still waiting for its answer is not cut off (<see cref="HostShutdownOutcomes.Busy"/>; the
/// client waits and asks again). A server started on its own (no <c>--owner</c>) is nobody's to stop from the pipe, and says so.
/// </summary>
internal sealed class ServerShutdown(bool owned, Func<string?> refuse, Action<string> stop, Action<string> log) : IHostShutdown
{
    public Task<HostShutdownResult> RequestAsync(CancellationToken cancellationToken = default)
    {
        if (!owned)
        {
            log("shutdown request refused reason=headless");
            return Task.FromResult(new HostShutdownResult(HostShutdownOutcomes.Refused));
        }
        if (refuse() is { } reason)
        {
            log($"shutdown request refused reason={reason}");
            return Task.FromResult(new HostShutdownResult(HostShutdownOutcomes.Busy));
        }
        log("shutdown request outcome=closing");
        // Begun, not finished: the stop closes the connection this answer goes out on, and waits for the answer to be written before it does.
        stop("host_shutdown");
        return Task.FromResult(new HostShutdownResult(HostShutdownOutcomes.Stopping));
    }
}
