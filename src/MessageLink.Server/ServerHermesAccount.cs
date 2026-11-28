using MessageLink.Core.Services;

namespace MessageLink.Server;

/// <summary>
/// Hermes's account as a host serves it: the backend's, with whether this host takes the address and keys from a client. A server does not
/// (its page has the form, as for every account); an app running its own services does.
/// </summary>
internal sealed class ServerHermesAccount(IHermesAccount backend, bool canConfigure) : IHermesAccount
{
    public HermesAccountState State => backend.State with { CanConfigure = canConfigure };
    public event EventHandler? Changed
    {
        add => backend.Changed += value;
        remove => backend.Changed -= value;
    }
    public Task ConfigureAsync(string? address, IReadOnlyList<HermesProfileInput> profiles, CancellationToken cancellationToken = default) =>
        canConfigure ? backend.ConfigureAsync(address, profiles, cancellationToken) : throw new NotSupportedException();
    public Task NewConversationAsync(string chatId, CancellationToken cancellationToken = default) => backend.NewConversationAsync(chatId, cancellationToken);
    // Any of the owner's devices may answer: the request is in a thread every one of them shows.
    public Task<string> AnswerApprovalAsync(string chatId, string messageId, bool approve, CancellationToken cancellationToken = default) =>
        backend.AnswerApprovalAsync(chatId, messageId, approve, cancellationToken);
}
