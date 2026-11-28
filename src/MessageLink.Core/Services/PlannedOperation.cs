namespace MessageLink.Core.Services;

/// <summary>
/// The name a caller has already chosen for the mutation it is about to make. The outbox gives each send its operation id when the send is drawn, not when
/// the answer is lost, so what it saves for the next launch (<c>InterruptedSends</c>) can ask the host's journal what became of a send that was in flight when the
/// app was killed. A port that stands in for a host over a connection (the protocol's proxy) uses it as the clientOpId of the one mutation it then makes; a port
/// that runs in this process never looks. Taken once: a second mutation made under the same plan gets a name of its own, so two mutations never share one.
/// </summary>
public static class PlannedOperation
{
    private sealed class Plan(string id) { public string? Id = id; }

    private static readonly AsyncLocal<Plan?> current = new();

    /// <summary>A new operation id, in the form the protocol uses (a Guid in N format).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Plans <paramref name="operationId"/> for the mutation made while the returned scope is open, in this call and what it awaits. Dispose it when the call has been made.</summary>
    public static IDisposable Begin(string? operationId) => new Scope(operationId);

    /// <summary>The planned id, once; null when nothing is planned or it was already taken.</summary>
    public static string? Take() => current.Value is { } plan ? Interlocked.Exchange(ref plan.Id, null) : null;

    private sealed class Scope : IDisposable
    {
        private readonly Plan? before = current.Value;

        public Scope(string? operationId) => current.Value = operationId is null ? null : new Plan(operationId);

        public void Dispose() => current.Value = before;
    }
}
