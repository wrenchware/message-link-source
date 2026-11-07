using NativeBeeper.App.Services;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host;

internal sealed class MultipleAccountIdentity(IHostIdentity original, ProviderAccounts accounts) : IHostIdentity
{
    public bool GooglePaired => original.GooglePaired;
    public event EventHandler? Changed
    {
        add { original.Changed += value; if (value is not null) { Action relay = () => value(this, EventArgs.Empty); lock (relays) relays[value] = relay; accounts.Changed += relay; } }
        remove { original.Changed -= value; if (value is not null) lock (relays) if (relays.Remove(value, out var relay)) accounts.Changed -= relay; }
    }
    private readonly Dictionary<EventHandler, Action> relays = [];
    public Task RefreshAsync(CancellationToken cancellationToken = default) => original.RefreshAsync(cancellationToken);
    public async Task<HostIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        var before = await original.ReadAsync(cancellationToken);
        var added = await accounts.CatalogAsync(cancellationToken);
        return before with { SavedScopes = before.SavedScopes.Concat(added.Select(a => new AccountScope(a.Id, a.Id))).ToArray(),
            LocalKey = before.LocalKey ?? (added.Count > 0 ? "accounts:shared" : null) };
    }
}

internal sealed class MultipleAccountBackend(IBeeperBackend original, ProviderAccounts accounts, Action<string>? log = null) : IBeeperBackend
{
    public async Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var added = await accounts.CatalogAsync(cancellationToken);
        // Connect also clears Google's reconnect pause, even while another service is available.
        try { return await original.DiscoverAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && added.Count > 0)
        {
            log?.Invoke("accounts original discovery outcome=failed");
            return new BackendInfo("Message Link", "Direct", true);
        }
    }
    public async Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var added = await accounts.CatalogAsync(cancellationToken);
        IReadOnlyList<ChatAccount> first;
        try { first = await original.GetAccountsAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && added.Count > 0)
        {
            log?.Invoke("accounts original list outcome=failed");
            first = [];
        }
        return first.Concat(added).ToArray();
    }
}
