namespace MessageLink.Core.Services;

/// <summary>
/// A client's request that the host it is connected to stop (SWITCHBOARD.md 3.2c, <c>host.shutdown</c>): what the app's Quit asks of a server it
/// started. Only a host that has an owner honours it; one started on its own (headless) answers <see cref="HostShutdownOutcomes.Refused"/>.
/// </summary>
public interface IHostShutdown
{
    /// <summary>Answers once the stop has begun, not once it has ended: the connection the answer travels on is one the stop closes.</summary>
    Task<HostShutdownResult> RequestAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IHostShutdown"/> answers: one of <see cref="HostShutdownOutcomes"/>.</summary>
public sealed record HostShutdownResult(string Outcome);

public static class HostShutdownOutcomes
{
    /// <summary>The stop has begun (or already had); the host ends shortly after this answer.</summary>
    public const string Stopping = "stopping";

    /// <summary>A send is still waiting for its answer, so the host would cut off a message whose outcome would then be unknown. Wait and ask again.</summary>
    public const string Busy = "busy";

    /// <summary>This host does not stop on a client's say-so (it has no owner). Nothing changed.</summary>
    public const string Refused = "refused";
}
