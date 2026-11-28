using System.Text.Json;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

// Ordinary sends use the same shared warning and dismissal as scheduled sends, without keeping their words.
internal sealed class ServerUnsettledSends(SharedStateStore store, TimeProvider? time = null)
{
    private readonly SharedSpaceView<SharedUnsettledSend> rows = Rows(store);

    internal static string RetirementId(string operationId) => "outbox:" + operationId;

    /// <summary>A send's warning as its row holds it, in System.Text.Json's default spelling, which every client reads it in (ScheduledServerLink): the support window holds the bytes.</summary>
    internal static byte[] RowValue(SharedUnsettledSend send) => JsonSerializer.SerializeToUtf8Bytes(send);

    /// <summary>The <c>outbox:</c> rows, one a send, by its operation id.</summary>
    internal static SharedSpaceView<SharedUnsettledSend> Rows(SharedStateStore store) =>
        store.View<SharedUnsettledSend>(SharedCategories.Outbox, send => send is { OperationId.Length: > 0 } ? send.OperationId : null, RowValue);

    public async Task RecordAsync(UnsettledSendOperation operation)
    {
        var filed = await rows.FiledAsync().ConfigureAwait(false) ?? throw new IOException("There is no key to file the send under.");
        var send = new SharedUnsettledSend(operation.OperationId, operation.ChatId, null,
            operation.OutcomeUnknown ? UnsettledStates.Unknown : UnsettledStates.Failed, operation.Started,
            RecordedAt: (time ?? TimeProvider.System).GetUtcNow());
        try
        {
            await filed.WriteAsync([(operation.OperationId, send)], pin: true, makes: [RetirementId(operation.OperationId)]).ConfigureAwait(false);
        }
        // A crash between publishing and flushing the journal can replay publication. Dismissal retired the id in the same commit as its row,
        // so that replay cannot resurrect a warning the user already dismissed.
        catch (RetiredIdException) { }
    }
}
