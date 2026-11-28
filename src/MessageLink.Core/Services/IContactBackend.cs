using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

public sealed record ContactPerson(string AccountId, string Id, string Name, string? Username, string? Phone, string? Email,
    bool CannotMessage, bool IsSelf = false, string? AvatarUrl = null)
{
    public string SecondaryLabel => (Phone ?? Email ?? Username ?? Id) + (CannotMessage ? " · Cannot message" : "");
    public string Label => Name + " · " + SecondaryLabel;
}
/// <param name="RemoteSearch">
/// Whether searching this account asks the provider over the network, rather than matching a directory already held
/// on this PC. It is the difference between a search that is free to run on every keystroke and one that spends a
/// real API call against the user's own account, so the caller can be sparing with the second kind. Appended with a
/// default, because a provider that has not considered the question is the cheap kind until it says otherwise.
/// </param>
public sealed record ContactOptions(bool CanSearch, bool CanStart, string Description, bool RemoteSearch = false);
/// <summary>
/// The people a service itself ranks as the ones this account writes to most, for New chat to offer first. Only a
/// service that keeps such a ranking answers; Google Messages' phone does (its top contacts).
/// </summary>
public interface IFrequentContacts
{
    /// <summary>
    /// The ranking, best first, or null when this account's service keeps none or it could not be had. Null and empty
    /// both mean nothing is shown. An implementation keeps what it was told for the connection, so asking twice does not
    /// ask the service twice.
    /// </summary>
    Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken = default);
}
// Finds an existing one-to-one conversation for a contact among already synchronized chats, without asking the phone.
public interface IDirectChatLookup
{
    MessageLink.Core.Messaging.ChatSummary? FindDirectChat(ContactPerson contact);
}
public interface IContactBackend
{
    Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default);
    Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a provider cannot open a conversation with somebody until something has been said in it. It is a
/// refusal that carries a way forward rather than a dead end, so it is its own type: New chat answers it by asking
/// for the first message instead of by printing an error.
/// </summary>
/// <remarks>
/// Instagram is the case this exists for. It has no call that creates an empty conversation; the thread comes into
/// being when the first message lands in it. A provider that can open one without sending never raises this.
/// </remarks>
public sealed class FirstMessageRequiredException(string message) : Exception(message);

/// <summary>
/// A provider that can start a conversation by sending its first message, for the providers that cannot start one
/// any other way. Implemented alongside <see cref="IContactBackend"/>, never instead of it.
/// </summary>
public interface IFirstMessageStarter
{
    /// <summary>
    /// Sends <paramref name="text"/> to <paramref name="contact"/>, creating the conversation if that is what it
    /// takes, and returns the conversation the message now lives in.
    /// </summary>
    Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default);

    /// <summary>The longest first message this provider accepts.</summary>
    int MaxFirstMessageLength { get; }
}
