namespace MessageLink.Protocol;

// Scheduled sends on the server (docs/research/switchboard-stage6.md S1-S9, protocol version 9). A server holds the
// schedule and fires it itself, so a message goes at its time with every client closed, and goes once however many clients are looking at it. Its clients draw
// the rows (`scheduled:<H(profile, id)>`, a SharedCategories category only the server writes) from their replica and change them only through these four
// journaled methods; none of them ever queues offline, so a client that is not connected is refused as a send is.
//
//   scheduled.create   Keeps a text for a time. The row's id is made from the client's operation id, so a lost answer is looked for in the replica, and the
//                      same operation answers the same row.
//   scheduled.edit     Takes a row back for the composer: it is no longer scheduled, and its words come back in the answer.
//   scheduled.cancel   Removes a row that has not started sending.
//   scheduled.sendNow  Asks a waiting or not-sent row to go now; it goes through the same checks as one whose time came.
//
// Each command on an existing row carries the rev of the row its client drew (`ifRev`); the server runs the commands on one row one at a time, so of two clients
// that act on the same row at once the second finds the row changed and is answered `conflict`, which is no failure (it redraws from its replica). A row that
// has started sending answers `already_sending` to edit, cancel and sendNow: the message is on its way and nothing can take it back. All of this is in the
// answer, not in an error, so the journal keeps it with the operation and a repeat answers the same. A real failure (not connected, the server cannot keep it)
// is an ordinary error.
//
// What a message does at the provider is not here: the server sends it through its OperationJournal under the fixed id `sched:<id>:<attempt>`, with the attempt
// written in the row before the provider is asked, so no command, restart or second client can send the same attempt twice.
public static partial class ProtocolMethods
{
    public const string ScheduledCreate = "scheduled.create";
    public const string ScheduledEdit = "scheduled.edit";
    public const string ScheduledCancel = "scheduled.cancel";
    public const string ScheduledSendNow = "scheduled.sendNow";
    /// <summary>Removes a send of unknown outcome or failed from the shared state (<c>outbox:</c>), for every client. Nothing is sent: it only says the person has seen it.</summary>
    public const string OutboxDismiss = "outbox.dismiss";

    private static Entry[] ScheduledEntries() =>
    [
        new(ScheduledCreate, "IScheduledCommands.CreateAsync", typeof(ScheduledCreateParams), typeof(ScheduledAnswer), ProtocolVersion.ServerScheduled, true),
        new(ScheduledEdit, "IScheduledCommands.EditAsync", typeof(ScheduledCommandParams), typeof(ScheduledAnswer), ProtocolVersion.ServerScheduled, true),
        new(ScheduledCancel, "IScheduledCommands.CancelAsync", typeof(ScheduledCommandParams), typeof(ScheduledAnswer), ProtocolVersion.ServerScheduled, true),
        new(ScheduledSendNow, "IScheduledCommands.SendNowAsync", typeof(ScheduledCommandParams), typeof(ScheduledAnswer), ProtocolVersion.ServerScheduled, true),
        new(OutboxDismiss, "IScheduledCommands.DismissAsync", typeof(OutboxDismissParams), typeof(Done), ProtocolVersion.ServerScheduled, true),
    ];
}

/// <summary>The id a server gives the message a <c>scheduled.create</c> makes: derived from the client's operation id, so a client that lost the answer knows which row to look for.</summary>
public static class ScheduledIds
{
    public const string Prefix = "host:scheduled:";
    public static string Of(string clientOpId) => Prefix + clientOpId;
}

/// <summary>The words an answer of <see cref="ScheduledAnswer.Status"/> carries; plain strings so a word a newer server adds is simply not one this build knows.</summary>
public static class ScheduledStatuses
{
    /// <summary>The command was carried out.</summary>
    public const string Done = "done";
    /// <summary>The row is not at the <c>ifRev</c> the client drew (changed, or gone) and nothing was done: the client redraws from its replica.</summary>
    public const string Conflict = "conflict";
    /// <summary>The row has started sending and cannot be edited, cancelled or sent again.</summary>
    public const string AlreadySending = "already_sending";
    /// <summary>The server will not keep or send it, for the sentence in <see cref="ScheduledAnswer.Message"/> (authored to be shown, never a provider's words).</summary>
    public const string Refused = "refused";
    /// <summary>The id of this create belonged to a message that has since gone from the server (cancelled, taken back, sent): nothing was made, and the same operation id will never make it.</summary>
    public const string Retired = "retired";
}

/// <summary>
/// Keeps <paramref name="Text"/> for <paramref name="Due"/> in the conversation <paramref name="ChatId"/>, answering the quoted message's id and preview when it
/// is a reply. The server checks the time, the text and the limit as the composer's own schedule does.
/// </summary>
public sealed record ScheduledCreateParams(string ClientOpId, string ChatId, string Text, DateTimeOffset Due, string? ReplyToId = null, string? ReplyPreview = null)
    : MutationParams(ClientOpId);

/// <summary>
/// Edit, cancel or send now for the row <paramref name="Id"/>, which the client drew at <paramref name="IfRev"/>. An edit or cancel with <paramref name="IfRev"/> 0 is for a row the client
/// never saw (it holds a stand-in for a create whose outcome it could not tell): the server takes the message away if it made it, and otherwise closes the id for good, so the create can
/// never make it. It never makes a message.
/// </summary>
public sealed record ScheduledCommandParams(string ClientOpId, string Id, long IfRev) : MutationParams(ClientOpId);

/// <summary>Dismisses the unsettled send the operation <paramref name="OperationId"/> (the server's name for the attempt, <c>sched:&lt;id&gt;:&lt;n&gt;</c>) left.</summary>
public sealed record OutboxDismissParams(string ClientOpId, string OperationId) : MutationParams(ClientOpId);

/// <summary>What <c>scheduled.edit</c> hands back: the row's words, for the composer.</summary>
public sealed record ScheduledText(string ChatId, string Text, DateTimeOffset Due, string? ReplyToId = null, string? ReplyPreview = null);

/// <summary>
/// The result of a command. <paramref name="Id"/> and <paramref name="Rev"/> name the row and its rev after a <c>done</c> (a create's id is how the client
/// finds its row in the replica; a cancel or edit leaves no row, so no rev). <paramref name="Text"/> comes with a <c>done</c> edit.
/// </summary>
public sealed record ScheduledAnswer(string Status, string? Id = null, long Rev = 0, string? Message = null, ScheduledText? Text = null);

/// <summary>
/// The server's schedule as the dispatcher serves it: the host's own, or a client's proxy. A host without one (the app's in-process host and the loopback,
/// whose window keeps and fires its own schedule) leaves it null and the methods answer <c>not_supported</c>.
/// </summary>
public interface IScheduledCommands
{
    Task<ScheduledAnswer> CreateAsync(ScheduledCreateParams request, CancellationToken cancellationToken = default);
    Task<ScheduledAnswer> EditAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default);
    Task<ScheduledAnswer> CancelAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default);
    Task<ScheduledAnswer> SendNowAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default);
    /// <summary>Removes the shared record of a send of unknown outcome. Idempotent: one already gone is done. Never sends.</summary>
    Task<Done> DismissAsync(OutboxDismissParams request, CancellationToken cancellationToken = default);
    /// <summary>
    /// <see cref="EditAsync(ScheduledCommandParams, CancellationToken)"/> for a client that may not write to some conversations (below 31: Hermes's): a message
    /// in a conversation <paramref name="refuses"/> names is answered refused with <paramref name="refusal"/>. Looked at under the message's own gate, so no
    /// other command on it (a cancel whose failed save puts it back) runs between the look and the act. A schedule that cannot look there refuses every one.
    /// </summary>
    Task<ScheduledAnswer> EditAsync(ScheduledCommandParams request, Func<string, bool> refuses, string refusal, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScheduledAnswer(ScheduledStatuses.Refused, Message: refusal));
    /// <summary><see cref="SendNowAsync(ScheduledCommandParams, CancellationToken)"/> with the same line as the refusing <c>EditAsync</c>.</summary>
    Task<ScheduledAnswer> SendNowAsync(ScheduledCommandParams request, Func<string, bool> refuses, string refusal, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScheduledAnswer(ScheduledStatuses.Refused, Message: refusal));
}
