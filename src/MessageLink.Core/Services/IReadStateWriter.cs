namespace MessageLink.Core.Services;

public interface IReadStateWriter
{
    Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default);
    Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A read-state writer whose caller says, as an argument, whether the read came through <c>PrivateReadingGate</c>. In one process the
/// gate admits a read with an <c>AsyncLocal</c> that the router behind it reads; that cannot cross a wire, so a port that stands in
/// for a router over one (the protocol's proxy) takes the admission explicitly, and the server's end applies it before the router
/// looks. A caller that uses the plain <see cref="IReadStateWriter.MarkReadAsync"/> is not admitted. Only the
/// gate passes <paramref name="admitted"/> true.
/// </summary>
/// <remarks>
/// The overload that also says <c>explicitly</c> separates a read the user asked for by name (Send read receipt, a notification's Mark read, the
/// console's /read) from one the app sends on its own because the conversation was read. Only the first must reach a service the host reads
/// privately; the server admits only that one (<c>PrivateReadingServerReadState</c>), so a client whose own view of the choice is out of date cannot
/// tell a service by an automatic read. The default implementation drops <paramref name="explicitly"/>, for a writer with no server to protect.
/// </remarks>
public interface IAdmittedReadStateWriter : IReadStateWriter
{
    Task MarkReadAsync(string chatId, string messageId, bool admitted, CancellationToken cancellationToken = default);

    Task MarkReadAsync(string chatId, string messageId, bool admitted, bool explicitly, CancellationToken cancellationToken = default) =>
        MarkReadAsync(chatId, messageId, admitted, cancellationToken);
}
