using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// The Voice accounts behind IGoogleVoiceAccounts, so a window (or a remote client, later) manages them without holding
// GoogleVoiceAccounts (SWITCHBOARD.md 2.6a). Every call is the manager's own; nothing here decides anything.
internal sealed class GoogleVoiceAccountsPort(GoogleVoiceAccounts voice) : IGoogleVoiceAccounts
{
    public IReadOnlyList<GoogleVoiceAccountState> Accounts => [.. voice.Accounts.Select(Snapshot)];
    public IReadOnlyList<string> KnownAccountKeys => voice.KnownAccountKeys;

    public event EventHandler? Changed
    {
        add => voice.Changed += value;
        remove => voice.Changed -= value;
    }

    public Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default) => voice.RestoreAsync(cancellationToken);

    public async Task<GoogleVoiceAccountState> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default) =>
        Snapshot(await voice.AddAsync(cookies, profileId, cancellationToken));

    // Every generation of the account is retired by its key (GoogleVoiceAccounts.SignOutAsync); an account no longer listed has nothing left to sign out.
    public async Task SignOutAsync(string key)
    {
        if (voice.Accounts.FirstOrDefault(account => account.Key == key) is { } listed) await voice.SignOutAsync(listed);
    }

    private static GoogleVoiceAccountState Snapshot(GoogleVoiceAccount account) =>
        new(account.Key, account.Number, account.State, account.ConversationCount, account.IsConnected);
}
