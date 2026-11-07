using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// The Messenger account behind IMessengerAccount, so a window (or a remote client, later) manages it without holding
// MessengerBackend (SWITCHBOARD.md 2.6a). Every call is the backend's own; nothing here decides anything.
internal sealed class MessengerAccount(MessengerBackend messenger) : IMessengerAccount
{
    public MessengerAccountState State =>
        new(messenger.IsConnected, messenger.SessionSaved, messenger.EncryptionEnabled, messenger.EncryptedState);

    public event EventHandler? Changed
    {
        add => messenger.ChatsChanged += value;
        remove => messenger.ChatsChanged -= value;
    }

    public async Task<bool> HasUsableSavedSessionAsync(CancellationToken cancellationToken = default)
    {
        var cookies = await messenger.ReadSavedCookiesAsync();
        var usable = cookies is not null;
        cookies?.Clear();
        return usable;
    }

    // A saved session is adopted as the helper's own (adoptLegacy); a new sign-in's cookies are not.
    public async Task<int> ConnectAsync(Dictionary<string, string>? cookies, CancellationToken cancellationToken = default)
    {
        if (cookies is not null) return await messenger.ConnectAsync(cookies, cancellationToken, adoptLegacy: false);
        var saved = await messenger.ReadSavedCookiesAsync() ?? throw new InvalidOperationException("missing_cookies");
        return await messenger.ConnectAsync(saved, cancellationToken, adoptLegacy: true);
    }

    public async Task<AccountRestore> RestoreAsync(Func<bool> stopped)
    {
        var outcome = await MessengerRestore.RunAsync(messenger, stopped);
        return new(outcome.Conversations, outcome.Refusal?.Message, outcome.Skipped);
    }

    public Task<string> DescribeAsync(CancellationToken cancellationToken = default) => messenger.DescribeAsync(cancellationToken);
    public Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default) => messenger.RefreshEncryptedStateAsync(cancellationToken);
    public Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default) => messenger.EnableEncryptedAsync(cancellationToken);
    public Task<string?> EnableEncryptedAfterSignInAsync() => messenger.EnableEncryptedAfterSignInAsync();
    public Task SignOutAsync() => messenger.SignOutAsync();
}
