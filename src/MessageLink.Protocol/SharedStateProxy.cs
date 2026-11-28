using System.Text.Json;
using MessageLink.Core.Backend;

namespace MessageLink.Protocol;

/// <summary>
/// The host refused a <c>state.put</c> at the door (<c>invalid_request</c>): an operation id already used for other writes, or a batch of the wrong shape.
/// Nothing was written. A key the host does not keep is not this: that write is answered <see cref="SharedWriteStatus.Refused"/>.
/// </summary>
public sealed class SharedStateRefusedException() : Exception("The server did not take that request.");

/// <summary>
/// The shared state over the wire (<c>state.*</c>, SWITCHBOARD.md stage 6): what a client's replica reads and writes through. A host without a shared state
/// answers <see cref="NotSupportedException"/>; anything else fails as the client's other calls do. A put is a plain call that may be made again with the same
/// operation id, which the host answers with its first answer, so a put whose connection ended is simply sent again later (it is never a provider mutation).
/// The version argument is ignored: the host reads the one its connection negotiated.
/// </summary>
public sealed class SharedStateProxy(ProtocolClient client) : ISharedState
{
    public Task<StateSyncResult> SyncAsync(StateSyncParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default) =>
        client.CallAsync<StateSyncParams, StateSyncResult>(ProtocolMethods.StateSync, request, cancellationToken);

    public Task<StateGetResult> GetAsync(StateGetParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default) =>
        client.CallAsync<StateGetParams, StateGetResult>(ProtocolMethods.StateGet, request, cancellationToken);

    public async Task<StatePutResult> PutAsync(StatePutParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var answer = await client.AnswerAsync(ProtocolMethods.StatePut, request, cancellationToken).ConfigureAwait(false);
        if (answer.Error is { } error)
        {
            if (error.Code == ProtocolErrorCodes.InvalidRequest) throw new SharedStateRefusedException();
            throw ProtocolErrors.ToException(error) switch
            {
                // As the client's other calls: a protocol error is shown as a fixed failure, never in the host's words.
                ProtocolException => new BackendException(BackendFailure.InvalidResponse, ProtocolClient.FailedSentence),
                OperationCanceledException when cancellationToken.IsCancellationRequested => new OperationCanceledException(cancellationToken),
                var exception => exception,
            };
        }
        StatePutResult? result = null;
        try { result = answer.ResultAs<StatePutResult>(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return result is { Results: not null } ? result : throw new BackendException(BackendFailure.InvalidResponse, ProtocolClient.UnreadableSentence);
    }
}
