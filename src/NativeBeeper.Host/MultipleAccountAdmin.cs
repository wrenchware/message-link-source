using NativeBeeper.App.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

internal sealed class MultipleAccountAdmin(IServerAdmin original, ProviderAccounts accounts) : IServerAdmin
{
    internal Action<string, string?>? Ended { get; set; }
    public Task<HostStatus> StatusAsync(CancellationToken cancellationToken = default) => original.StatusAsync(cancellationToken);
    public async Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        var result = (await original.AccountsAsync(cancellationToken)).ToList();
        foreach (var account in accounts.Added)
        {
            var service = account.Registration.Service;
            var connected = account.Google?.CanReceive == true || account.Messenger?.IsConnected == true || account.Instagram?.IsConnected == true;
            result.Add(new(Provider(service), connected ? ServerAccountStates.Connected : ServerAccountStates.Saved,
                Removable: true, account.Registration.Key, account.Google?.OwnNumber ?? account.Messenger?.OwnName ?? account.Instagram?.AccountName));
        }
        return result;
    }
    internal static string Provider(string service) => service == "google" ? ServerAccountProviders.GoogleMessages : service;
    internal static string Service(string provider) => provider == ServerAccountProviders.GoogleMessages ? "google" : provider;
    public async Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default)
    {
        if (provider == ServerAccountProviders.GoogleVoice || string.IsNullOrEmpty(key)) return await original.RemoveAccountAsync(provider, key, cancellationToken);
        var account = accounts.ByKey(key, Service(provider));
        await accounts.RemoveAsync(account.Registration.Key, cancellationToken);
        Ended?.Invoke("account-slot", key);
        return new(true);
    }
}
