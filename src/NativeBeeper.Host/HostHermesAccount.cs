using NativeBeeper.Core.Services;

namespace NativeBeeper.Host;

/// <summary>
/// Hermes's account as a host serves it: the backend's, with whether this host takes the address and keys from a client. A server does not
/// (its page has the form, as for every account); an app running its own services does.
/// </summary>
internal sealed class HostHermesAccount(IHermesAccount backend, bool canConfigure) : IHermesAccount
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
}
