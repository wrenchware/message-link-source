using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The server's end of private reading's explicit admission. A client's <see cref="PrivateReadingGate"/> decides
/// whether a read marker or a typing start may go to a service; the request reaches the server on a task of its own, where the gate's
/// <c>AsyncLocal</c> is not set. This wraps the server's real read-state writer: a read the client says the person asked for by name
/// (<c>explicit</c>) runs inside the gate's admitted scope, so the router's own refusal (<see cref="PrivateReadingGate.Refuses"/>) lets it
/// through; every other read runs as it is, and the router refuses it for a conversation the server reads privately, exactly as it does in one
/// process.
/// </summary>
/// <remarks>
/// <para><b>Trust boundary.</b> What the server trusts its authenticated client for is the one thing only the person can say: that this read was
/// asked for by name (a notification's Mark read, Send read receipt, the console's /read). <c>admitted</c> alone is not enough. It says only
/// that the client's gate, by the client's own view of the choice, did not withhold the read, and that view can be behind: private reading is one
/// setting for every device (owner, 2026-10-09), so another device can have turned it on while this client has not heard.
/// An automatic read from that client must not reach the service, so the server decides it by its own view (<c>ServerClientState</c>: the most
/// private of what its connected clients state), which is what the routers behind this read. A client that sends only <c>admitted</c> (one
/// from before <c>explicit</c>) is therefore treated as automatic; the worst that costs is a receipt asked for by name from such a client,
/// withheld for a conversation the server reads privately.</para>
/// <para>A server with no <c>ServerClientState</c> has routers with no private reading of their own, and then nothing here refuses a read; the
/// client's gate is the only enforcement, as before. A client that sends <c>explicit: true</c> for a conversation the user reads privately can
/// still make the server tell the service; that is the person's authenticated client acting as the person.</para>
/// </remarks>
public sealed class PrivateReadingServerReadState(IReadStateWriter reads) : IAdmittedReadStateWriter
{
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) => reads.CanMarkReadAsync(chatId, cancellationToken);

    /// <summary>Not admitted: the router decides.</summary>
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default) => reads.MarkReadAsync(chatId, messageId, cancellationToken);

    /// <summary>Admitted by the client's gate but not said to be asked for by name: an automatic read, which the router decides.</summary>
    public Task MarkReadAsync(string chatId, string messageId, bool admitted, CancellationToken cancellationToken = default) =>
        MarkReadAsync(chatId, messageId, admitted, false, cancellationToken);

    public Task MarkReadAsync(string chatId, string messageId, bool admitted, bool explicitly, CancellationToken cancellationToken = default) =>
        admitted && explicitly ? PrivateReadingGate.RunAdmittedAsync(() => reads.MarkReadAsync(chatId, messageId, cancellationToken)) : reads.MarkReadAsync(chatId, messageId, cancellationToken);
}

/// <summary>
/// Typing, the same way as <see cref="PrivateReadingServerReadState"/>. The typing the server sees from others is the wrapped service's own. Typing has no
/// way to be asked for by name, so a start is never admitted on the client's say-so: the router decides it by the server's own view. A stop is admitted,
/// as the router never refuses one.
/// </summary>
public sealed class PrivateReadingServerTyping(ITypingService typing) : IAdmittedTypingService
{
    public bool CanReceive => typing.CanReceive;
    public bool CanSend => typing.CanSend;
    public IAsyncEnumerable<TypingUpdate> WatchAsync(CancellationToken cancellationToken = default) => typing.WatchAsync(cancellationToken);

    /// <summary>Not admitted: the router decides.</summary>
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default) => typing.SetTypingAsync(chatId, isTyping, cancellationToken);

    public Task SetTypingAsync(string chatId, bool isTyping, bool admitted, CancellationToken cancellationToken = default) =>
        admitted && !isTyping ? PrivateReadingGate.RunAdmittedAsync(() => typing.SetTypingAsync(chatId, isTyping, cancellationToken)) : typing.SetTypingAsync(chatId, isTyping, cancellationToken);
}
