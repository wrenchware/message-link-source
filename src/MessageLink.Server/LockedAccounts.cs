using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// The Messenger account port as the host serves it, behind the provider's lifecycle lock (<see cref="ProtocolServerOptions.AccountLocks"/>): a sign-in, a restore, a
/// connect, encryption and a sign-out wait for one another, and for what else takes the lock (the start's restore, a removal on the server's page). Without it a
/// sign-in finishing in another tab could replace the session a removal had just read, or reopen the device between its sign-out and setting its files aside.
/// Reads and the state pass through. Code that already holds the lock uses the backend's own port, never this one: the lock is not reentrant.
/// </summary>
internal sealed class LockedMessengerAccount(IMessengerAccount inner, Func<SemaphoreSlim> lifecycle) : IMessengerAccount
{
    /// <summary>The port behind the lock (the harness looks at what is really served).</summary>
    internal IMessengerAccount Inner => inner;

    public MessengerAccountState State => inner.State;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public Task<bool> HasUsableSavedSessionAsync(CancellationToken cancellationToken = default) => inner.HasUsableSavedSessionAsync(cancellationToken);
    public Task<string> DescribeAsync(CancellationToken cancellationToken = default) => inner.DescribeAsync(cancellationToken);
    public Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default) => inner.RefreshEncryptedStateAsync(cancellationToken);
    public Task<int> ConnectAsync(Dictionary<string, string>? cookies, CancellationToken cancellationToken = default) =>
        AccountLock.Under(lifecycle, () => inner.ConnectAsync(cookies, cancellationToken), cancellationToken);
    public Task<AccountRestore> RestoreAsync(Func<bool> stopped) => AccountLock.Under(lifecycle, () => inner.RestoreAsync(stopped));
    public Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default) =>
        AccountLock.Under(lifecycle, () => inner.EnableEncryptedAsync(cancellationToken), cancellationToken);
    public Task<string?> EnableEncryptedAfterSignInAsync() => AccountLock.Under(lifecycle, inner.EnableEncryptedAfterSignInAsync);
    public Task SignOutAsync() => AccountLock.Under(lifecycle, async () => { await inner.SignOutAsync().ConfigureAwait(false); return true; });
}

/// <summary>The same for Instagram (<see cref="LockedMessengerAccount"/>): a resume, a sign-in, a restore and a sign-out wait for one another and for a removal.</summary>
internal sealed class LockedInstagramAccount(IInstagramAccount inner, Func<SemaphoreSlim> lifecycle) : IInstagramAccount
{
    /// <summary>The port behind the lock.</summary>
    internal IInstagramAccount Inner => inner;

    public InstagramAccountState State => inner.State;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public event EventHandler? ReconnectAbandoned { add => inner.ReconnectAbandoned += value; remove => inner.ReconnectAbandoned -= value; }
    public Task<int> ResumeAsync(CancellationToken cancellationToken = default) => AccountLock.Under(lifecycle, () => inner.ResumeAsync(cancellationToken), cancellationToken);
    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default) =>
        AccountLock.Under(lifecycle, () => inner.ConnectAsync(cookies, cancellationToken), cancellationToken);
    public Task<AccountRestore> RestoreAsync(Func<bool> stopped) => AccountLock.Under(lifecycle, () => inner.RestoreAsync(stopped));
    public Task SignOutAsync() => AccountLock.Under(lifecycle, async () => { await inner.SignOutAsync().ConfigureAwait(false); return true; });
    public bool AnswersFromMemory(string chatId, string messageId) => inner.AnswersFromMemory(chatId, messageId);
}

/// <summary>A helper restart reconnects from the saved session, so Messenger's and Instagram's take their provider's lock too; every other scope is the inner restarter's own.</summary>
internal sealed class LockedHelperRestarter(IHelperRestarter inner, Func<string, SemaphoreSlim> locks) : IHelperRestarter
{
    public Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false)
    {
        var provider = scope == ProviderScopes.Messenger ? RestoreOnce.Messenger : scope == ProviderScopes.Instagram ? RestoreOnce.Instagram : null;
        return provider is null ? inner.RestartAsync(scope, signingIn, force, token, settle)
            : AccountLock.Under(() => locks(provider), () => inner.RestartAsync(scope, signingIn, force, token, settle));
    }
}

internal static class AccountLock
{
    public static async Task<T> Under<T>(Func<SemaphoreSlim> lifecycle, Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        var held = lifecycle();
        await held.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await call().ConfigureAwait(false); }
        finally { held.Release(); }
    }
}
