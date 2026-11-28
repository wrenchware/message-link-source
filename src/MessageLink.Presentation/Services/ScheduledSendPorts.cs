using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// What ScheduledSendEngine acts through (S3). The in-process app gives it PrivateState, the one
// Outbox and the backends' send check (ScheduledSends); a server that fires for its clients gives it its own.

/// <summary>Where scheduled messages are kept: one list per profile, written whole.</summary>
public interface IScheduledSendStore
{
    /// <summary>The profile the list is kept under now; null while nothing may be kept (no account signed in).</summary>
    Task<string?> ProfileAsync();
    Task<ScheduledSend[]?> ReadAsync(string profile);
    /// <summary>Keeps exactly these for the profile; an empty list removes it.</summary>
    Task WriteAsync(string profile, ScheduledSend[] items);
}

/// <summary>
/// A store refused to make a message because its id has gone for good (a server keeps the ids of the messages it let go of, so that none is ever made twice). The store kept nothing, so
/// there is nothing to put back; the engine hands this on as it is for its caller to answer.
/// </summary>
public sealed class ScheduledIdRetiredException(string message, IReadOnlyCollection<string> ids) : IOException(message)
{
    /// <summary>Every id of the commit that is retired. Only a caller whose own message is among them was refused for it; the commit kept nothing for anyone, so the rest failed to save like any other failure.</summary>
    public IReadOnlyCollection<string> Ids { get; } = ids;
}

/// <summary>The send path a due message is handed to, under its own id, and what became of it there.</summary>
public interface IScheduledSendOutlet
{
    /// <summary>
    /// Hands the message over, once. The task may stay unfinished while the send is under way; what it ends as arrives
    /// through <see cref="Changed"/>. Faulted while <see cref="StateOf"/> knows nothing of the id, it was refused before
    /// anything was submitted.
    /// </summary>
    Task SendAsync(ScheduledSend item);
    /// <summary>The state of the send under this id; null once the send path holds nothing for it.</summary>
    OutgoingState? StateOf(string id);
    /// <summary>A send's id, each time its state changes and once more when it is gone.</summary>
    event EventHandler<string>? Changed;
    /// <summary>Records a message found sending at load as an interrupted send: shown as unknown, never sent again.</summary>
    Task CarryInterruptedAsync(InterruptedSend send);
    /// <summary>
    /// <see cref="CarryInterruptedAsync(InterruptedSend)"/> with the message as it was saved (its words and its attempt), for an outlet that must keep more than the id and the time: a
    /// server's, whose record is shown to every client. The default is the one above, which is all the in-process outbox keeps.
    /// </summary>
    Task CarryInterruptedAsync(ScheduledSend item, InterruptedSend send) => CarryInterruptedAsync(send);
}

/// <summary>What is asked before a due message goes: its account, its service's connection and the service's send check.</summary>
public interface IScheduledSendConditions
{
    /// <summary>False where conversations name their account already: then no message is checked against one.</summary>
    bool TracksAccounts { get; }
    /// <summary>
    /// Whether a conversation belongs to a service whose account must be known to keep or send a message for it (Messenger: its ids name no account). Where it does and the account
    /// cannot be told, a message is refused instead of being kept with none, since one kept with none is sent from whichever account is signed in later. False by default: the in-process
    /// app, as it was, keeps one.
    /// </summary>
    bool ExpectsAccount(string chatId) => false;
    /// <summary>
    /// The signed-in account the conversation belongs to now, from what is listed here (never a service request); null
    /// while that is not known yet.
    /// </summary>
    Task<string?> AccountOfAsync(string chatId);
    /// <summary>False while the conversation's service is known to be away; true when that cannot be told.</summary>
    bool IsConnected(string chatId);
    Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken);
}

/// <summary>
/// Whether this engine holds the schedule (S9). Only one firer may ever act on a message, so an engine whose schedule has
/// been handed to a server, or is being moved there, fires nothing.
/// </summary>
public interface IScheduledSendCustody
{
    ValueTask<bool> IsLocalAsync();
}

public static class ScheduledSendCustody
{
    /// <summary>Always this engine's: the in-process app, until a schedule can move to a server (slices 11 and 13).</summary>
    public static readonly IScheduledSendCustody Local = new Always();
    private sealed class Always : IScheduledSendCustody { public ValueTask<bool> IsLocalAsync() => ValueTask.FromResult(true); }
}
