using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server;

/// <summary>
/// The Windows server's administration (SWITCHBOARD.md 10.7, as the Linux host's 4.6): what the server is, the accounts it holds, and removing one, for its page. Removing an account is that account's own sign-out on this PC, exactly as Settings' Sign out was in the in-process app: nothing is sent to the service, and the
/// Messenger device and the history the helper kept stay where they are, so a later sign-in on this PC reuses them. Google Messages has no sign-out here: it is removed by
/// unpairing on the phone. Logged as fixed words only.
/// </summary>
internal sealed class WindowsServerAdmin(WindowsServerAdmin.Parts parts) : IServerAdmin
{
    /// <summary>The channel this host names itself by on the page.</summary>
    public const string Channel = "windows";

    internal sealed record Parts(string App, DateTimeOffset Started, IMessengerAccount Messenger, IInstagramAccount Instagram, IGoogleVoiceAccounts Voice,
        Func<bool> GooglePaired, Func<bool> GoogleConnected, Action<string> Log);

    /// <summary>Told of an account removed here, so what the host kept of it goes too (<c>ProtocolServices.AccountEnded</c>).</summary>
    public Action<string, string?>? Ended { get; set; }

    public Task<HostStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostStatus(parts.App, ProtocolVersion.Current, ProtocolVersion.Supported, parts.Started, Channel: Channel));

    public Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        var accounts = new List<ServerAccount>();
        if (parts.GooglePaired())
            accounts.Add(new(ServerAccountProviders.GoogleMessages, parts.GoogleConnected() ? ServerAccountStates.Connected : ServerAccountStates.Paired, Removable: false));
        var messenger = parts.Messenger.State;
        if (messenger.Saved || messenger.Connected)
            accounts.Add(new(ServerAccountProviders.Messenger, messenger.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true));
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
                parts.Log("server account remove refused provider=google-messages reason=not_offered");
                throw new InvalidOperationException(AccountRemoveWords.NotOffered);
            case ServerAccountProviders.GoogleVoice:
                if (string.IsNullOrEmpty(key) || !parts.Voice.Accounts.Any(account => account.Key == key))
                {
                    parts.Log("server account remove provider=google-voice outcome=none");
                    return new(false);
                }
                await parts.Voice.SignOutAsync(key).ConfigureAwait(false);
                return Told("google-voice", "voice", key);
            case ServerAccountProviders.Messenger:
                if (parts.Messenger.State is { Saved: false, Connected: false }) { parts.Log("server account remove provider=messenger outcome=none"); return new(false); }
                await parts.Messenger.SignOutAsync().ConfigureAwait(false);
                return Told("messenger", "messenger", null);
            case ServerAccountProviders.Instagram:
                if (parts.Instagram.State is { Saved: false, Connected: false }) { parts.Log("server account remove provider=instagram outcome=none"); return new(false); }
                await parts.Instagram.SignOutAsync().ConfigureAwait(false);
                return Told("instagram", "instagram", null);
            default:
                parts.Log("server account remove refused reason=unknown");
                throw new InvalidOperationException(AccountRemoveWords.Unknown);
        }
    }

    private AccountRemoved Told(string word, string account, string? key)
    {
        parts.Log($"server account remove provider={word} outcome=removed");
        try { Ended?.Invoke(account, key); }
        catch (Exception) { /* a hook that fails must not undo the removal */ }
        return new(true);
    }
}
