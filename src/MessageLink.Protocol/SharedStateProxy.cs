using System.Text.Json;
using MessageLink.Core.Backend;

namespace MessageLink.Protocol;

/// <summary>
/// The server refused a <c>state.put</c> at the door (<c>invalid_request</c>): an operation id already used for other writes, or a batch of the wrong shape.
/// Nothing was written. A key the server does not keep is not this: that write is answered <see cref="SharedWriteStatus.Refused"/>.
/// </summary>
public sealed class SharedStateRefusedException() : Exception("The server did not take that request.");

/// <summary>
/// The shared state over the wire (<c>state.*</c>): what a client's replica reads and writes through. A server without a shared state
/// answers <see cref="NotSupportedException"/>; anything else fails as the client's other calls do. A put is a plain call that may be made again with the same
/// operation id, which the server answers with its first answer, so a put whose connection ended is simply sent again later (it is never a provider mutation).
/// The version argument is ignored: the server reads the one its connection negotiated. Keys are named as this build files them: a server below 35 names the refused sends
/// <c>outbox:</c>, which is read and asked for as <c>refused:</c> here (<see cref="SharedCategories.FromWire(string, int)"/>), whichever version answers.
/// </summary>
public sealed class SharedStateProxy(ProtocolClient client) : ISharedState
{
    // A server of 35 or later never names a refused send outbox:, so what a server sends is read as a server below 35 would mean it, whichever version answered: no
    // reconnect to another version between asking and reading can name a row wrongly.
    private const int AnyServer = ProtocolVersion.RefusedRows - 1;

    public async Task<StateSyncResult> SyncAsync(StateSyncParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var result = await client.CallAsync<StateSyncParams, StateSyncResult>(ProtocolMethods.StateSync, request, cancellationToken).ConfigureAwait(false);
        return result?.Rows is null ? result! : result with { Rows = SharedCategories.FromWire(result.Rows, AnyServer) };
    }

    // The keys go under the names of the version the connection speaks, opened first if this is the first call. A call that went to a server of another version (a
    // reconnect between choosing the names and sending them) is asked again under that version's names: a refused send's row can come by size (a sync page's
    // budget, or a scheduled send's words), and a miss under the wrong name would read as no row.
    public async Task<StateGetResult> GetAsync(StateGetParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        if (client.Version is null) await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var speaks = client.Version ?? ProtocolVersion.Current;
            var asked = request.Keys is null ? request : request with { Keys = [.. request.Keys.Select(key => SharedCategories.ToWire(key, speaks))] };
            var result = await client.CallAsync<StateGetParams, StateGetResult>(ProtocolMethods.StateGet, asked, cancellationToken).ConfigureAwait(false);
            var answered = client.Version ?? ProtocolVersion.Current;
            var named = (speaks >= ProtocolVersion.RefusedRows) == (answered >= ProtocolVersion.RefusedRows);
            if (named || attempt >= 2) return result?.Rows is null ? result! : result with { Rows = SharedCategories.FromWire(result.Rows, AnyServer) };
        }
    }

    public async Task<StatePutResult> PutAsync(StatePutParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var answer = await client.AnswerAsync(ProtocolMethods.StatePut, request, cancellationToken).ConfigureAwait(false);
        if (answer.Error is { } error)
        {
            if (error.Code == ProtocolErrorCodes.InvalidRequest) throw new SharedStateRefusedException();
            throw ProtocolErrors.ToException(error) switch
            {
                // As the client's other calls: a protocol error is shown as a fixed failure, never in the server's words.
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
