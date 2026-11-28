using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

public sealed record SendPermission(bool Allowed, string Reason, int? MaxTextLength = null,
    // Not allowed yet only because the provider is still connecting; its readiness refreshes the permission, so the
    // reason is progress rather than something the user has to act on. Appended so existing call sites are unchanged.
    bool Pending = false,
    // Allowed, but something the user should know before sending, shown as the composer's notice instead of the keyboard
    // hint: Google Messages says so when the phone reports it is not the default SMS app. Appended, as Pending was.
    string? Caution = null);
public sealed record SendReceipt(string ChatId, string PendingMessageId,
    // Some providers submit a photo set as distinct messages. These are the ordered IDs
    // returned by that exact operation, never inferred from nearby history.
    IReadOnlyList<string>? PendingMessageIds = null);
public sealed class SendException(bool outcomeUnknown, string message) : Exception(message)
{
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    // The operation went through and only what came after it failed (a first message that was sent but whose conversation could
    // not be described, a group that was made but not described). Never an invitation to do it again: whatever submitted it must
    // stay closed to a second attempt. OutcomeUnknown stays false, because nothing about the outcome is in doubt.
    public bool Done { get; init; }
    // Set when the send went over a protocol connection that ended before its answer: the name the host's journal knows the
    // operation by, so the app can be told later what became of it (IOperationOutcomes). Never shown.
    public string? OperationId { get; init; }
}

// Failed means only that the host said the operation was not done; it is not read from the words of the refusal, which for a few
// operations that are not sends (a first message whose conversation could not be described, a group that was made but not described)
// say the thing was done. Only a message send, reply or attachment send may be settled from it, as a refused send the composer can
// give back; anything else must treat it as no more than the host's sentence.
public enum OperationVerdict { Done, Failed, Unknown }

// What a host's journal said about an operation whose answer was lost with its connection. Receipt is the stored result of a
// send that was done; Explanation the stored sentence of one that failed. Unknown stays unknown: the operation is never sent again.
public sealed record OperationOutcome(string OperationId, OperationVerdict Verdict, SendReceipt? Receipt = null, string? Explanation = null);

public interface IOperationOutcomes
{
    // Raised from a background thread, once per operation, with the host's answer. A listener that changes interface state applies
    // it on the interface thread (Outbox and InterruptedSends require a dispatcher for this).
    event EventHandler<OperationOutcome>? Reconciled;
}
public interface IMessageSender
{
    Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default);
    Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default);
    Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default);
}
