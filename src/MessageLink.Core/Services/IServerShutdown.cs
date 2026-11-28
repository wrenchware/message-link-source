namespace MessageLink.Core.Services;

/// <summary>
/// A client's request that the host it is connected to stop (<c>host.shutdown</c>): what the app's Quit asks of a server it
/// started. Only a server that has an owner honours it; one started on its own (headless) answers <see cref="ServerShutdownOutcomes.Refused"/>.
/// </summary>
public interface IServerShutdown
{
    /// <summary>Answers once the stop has begun, not once it has ended: the connection the answer travels on is one the stop closes.</summary>
    Task<ServerShutdownResult> RequestAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IServerShutdown"/> answers: one of <see cref="ServerShutdownOutcomes"/>.</summary>
public sealed record ServerShutdownResult(string Outcome);

public static class ServerShutdownOutcomes
{
    /// <summary>The stop has begun (or already had); the server ends shortly after this answer.</summary>
    public const string Stopping = "stopping";

    /// <summary>A send is still waiting for its answer, so the server would cut off a message whose outcome would then be unknown. Wait and ask again.</summary>
    public const string Busy = "busy";

    /// <summary>This server does not stop on a client's say-so (it has no owner). Nothing changed.</summary>
    public const string Refused = "refused";
}
