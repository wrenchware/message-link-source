using Microsoft.Extensions.DependencyInjection;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Server.Windows;

/// <summary>
/// The Windows server's administration (as the Linux server's): what the server is, the accounts it holds, and removing one, for its page. Removing an account is that account's own sign-out on this PC, exactly as Settings' Sign out was in the in-process app: nothing is sent to the service, and the
/// Messenger device and the history the helper kept stay where they are, so a later sign-in on this PC reuses them. Google Messages is unpaired: the phone is asked
/// to drop the server when it is connected, and the pairing is forgotten either way (GoogleMessagesBackend.RemoveAsync). Logged as fixed words only.
/// </summary>
internal sealed class WindowsServerAdmin(WindowsServerAdmin.Parts parts) : MessageLink.Server.IComposedServerAdmin
{
    /// <summary>The channel this server names itself by on the page.</summary>
    public const string Channel = "windows";

    // A pairing removed on the phone, or a sign-in Google refused, is Saved ("not connected", with the sign-in to fix it), not Paired: the page said "Paired"
    // beside Settings' "sign in again" after the phone had removed the server (2026-11-22).
    private static string GoogleState(bool connected, Func<bool>? needsSignIn) =>
        connected ? ServerAccountStates.Connected : needsSignIn?.Invoke() == true ? ServerAccountStates.Saved : ServerAccountStates.Paired;

    internal sealed record Parts(string App, DateTimeOffset Started, IMessengerAccount Messenger, IInstagramAccount Instagram, IGoogleVoiceAccounts Voice,
        Func<bool> GooglePaired, Func<bool> GoogleConnected, Action<string> Log, Func<string?>? GoogleNumber = null, Func<string?>? MessengerName = null,
        Func<bool>? GoogleNeedsSignIn = null, Func<CancellationToken, Task<string>>? GoogleRemove = null, Func<string, SemaphoreSlim>? LoginGate = null);

    /// <summary>The administration over a composed server's backends. Its removals of Messenger and Instagram take the start's restore lock.</summary>
    internal static WindowsServerAdmin Over(MessageLink.Server.ServerBackends backends, string version, Action<string> log)
    {
        var google = backends.Google;
        return new(new Parts(version, DateTimeOffset.UtcNow, backends.Services.GetRequiredService<IMessengerAccount>(),
            backends.Services.GetRequiredService<IInstagramAccount>(), backends.Services.GetRequiredService<IGoogleVoiceAccounts>(), () => MessageLink.App.Services.GoogleMessages.GoogleSessionProfile.HasSavedSession,
            backends.GoogleConnected, log, () => google.OwnNumber, () => backends.Messenger.OwnName,
            () => google.PairingLost || google.SignInExpired && !google.SignInPresumed, google.RemoveAsync, backends.LoginGate));
    }

    /// <summary>Told of an account removed here, so what the server kept of it goes too (<c>ProtocolServices.AccountEnded</c>).</summary>
    public Action<string, string?>? Ended { get; set; }

    public Task<ServerStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerStatus(parts.App, ProtocolVersion.Current, ProtocolVersion.Supported, parts.Started, Channel: Channel));

    public Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        var accounts = new List<ServerAccount>();
        if (parts.GooglePaired())
            accounts.Add(new(ServerAccountProviders.GoogleMessages, GoogleState(parts.GoogleConnected(), parts.GoogleNeedsSignIn), Removable: parts.GoogleRemove is not null, Name: parts.GoogleNumber?.Invoke()));
        var messenger = parts.Messenger.State;
        if (messenger.Saved || messenger.Connected)
            accounts.Add(new(ServerAccountProviders.Messenger, messenger.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true, Name: parts.MessengerName?.Invoke()));
        var instagram = parts.Instagram.State;
        if (instagram.Saved || instagram.Connected)
            accounts.Add(new(ServerAccountProviders.Instagram, instagram.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true, Name: instagram.AccountName));
        foreach (var voice in parts.Voice.Accounts)
            accounts.Add(new(ServerAccountProviders.GoogleVoice, voice.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true, voice.Key, voice.Number));
        return Task.FromResult<IReadOnlyList<ServerAccount>>(accounts);
    }

    public async Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default)
    {
        switch (provider)
        {
            case ServerAccountProviders.GoogleMessages:
                if (parts.GoogleRemove is not { } remove)
                {
                    parts.Log("server account remove refused provider=google-messages reason=not_offered");
                    throw new InvalidOperationException(AccountRemoveWords.NotOffered);
                }
                if (!parts.GooglePaired()) { parts.Log("server account remove provider=google-messages outcome=none"); return new(false); }
                // The phone is asked to drop the server when it can be (GoogleMessagesBackend.RemoveAsync); the pairing is forgotten either way.
                var phone = await remove(cancellationToken).ConfigureAwait(false);
                parts.Log($"server account remove provider=google-messages outcome=removed phone={phone}");
                try { Ended?.Invoke("google-reset", null); }
                catch (Exception) { /* a hook that fails must not undo the removal */ }
                return new(true);
            case ServerAccountProviders.GoogleVoice:
                if (string.IsNullOrEmpty(key) || !parts.Voice.Accounts.Any(account => account.Key == key))
                {
                    parts.Log("server account remove provider=google-voice outcome=none");
                    return new(false);
                }
                await parts.Voice.SignOutAsync(key).ConfigureAwait(false);
                return Told("google-voice", "voice", key);
            case ServerAccountProviders.Messenger:
                if (!await UnderLoginGateAsync(RestoreOnce.Messenger, async () =>
                {
                    if (parts.Messenger.State is { Saved: false, Connected: false }) { parts.Log("server account remove provider=messenger outcome=none"); return false; }
                    await parts.Messenger.SignOutAsync().ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false)) return new(false);
                return Told("messenger", "messenger", null);
            case ServerAccountProviders.Instagram:
                if (!await UnderLoginGateAsync(RestoreOnce.Instagram, async () =>
                {
                    if (parts.Instagram.State is { Saved: false, Connected: false }) { parts.Log("server account remove provider=instagram outcome=none"); return false; }
                    await parts.Instagram.SignOutAsync().ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false)) return new(false);
                return Told("instagram", "instagram", null);
            default:
                parts.Log("server account remove refused reason=unknown");
                throw new InvalidOperationException(AccountRemoveWords.Unknown);
        }
    }

    // The provider's lifecycle lock, the one the start's restore takes (as LinuxServerAdmin): whichever comes second finds the account already connected, or
    // gone. The server is told of the removal after the lock is let go.
    private async Task<bool> UnderLoginGateAsync(string provider, Func<Task<bool>> remove, CancellationToken cancellationToken)
    {
        if (parts.LoginGate?.Invoke(provider) is not { } gate) return await remove().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await remove().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private AccountRemoved Told(string word, string account, string? key)
    {
        parts.Log($"server account remove provider={word} outcome=removed");
        try { Ended?.Invoke(account, key); }
        catch (Exception) { /* a hook that fails must not undo the removal */ }
        return new(true);
    }
}
