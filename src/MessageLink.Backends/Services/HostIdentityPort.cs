using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services;

// The identities a client keys what it keeps by, computed from the backends and the Google session files. These are the rules the window
// used on its own (App.SavedMessages, ProfileAsync, GoogleSessionProfile.HasSavedSession), moved to the host so that a client of another
// process reads the same answer from the pushed state (SWITCHBOARD.md 3.0b).
//
// Provider aliases are not account identities: Messenger and Google both reuse their alias after sign-out. Resolve the current owner before
// exposing any saved content from that alias.
internal sealed class HostIdentityPort : IHostIdentity
{
    private readonly IAccessTokenStore tokens;
    private readonly Func<bool> paired;
    private readonly Func<string?> messengerScope;
    private readonly Func<IReadOnlyList<string>> voiceKeys;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> instagramAccounts;
    private readonly bool followsFiles;
    private readonly Lock gate = new();
    private string? stamp;
    private volatile bool keyUnreadable;

    /// <param name="followsFiles">
    /// True for the host's own port: a host that is not told of every sign-in (the window used to say so with <c>Invalidate</c>) looks at the
    /// files the key is read from and reads it again when they changed. The window's own port is left exactly as it was.
    /// </param>
    public HostIdentityPort(IAccessTokenStore tokens, Func<bool> paired, Func<string?> messengerScope, Func<IReadOnlyList<string>> voiceKeys,
        Func<CancellationToken, Task<IReadOnlyList<string>>> instagramAccounts, bool followsFiles = false)
    {
        this.tokens = tokens; this.paired = paired; this.messengerScope = messengerScope; this.voiceKeys = voiceKeys;
        this.instagramAccounts = instagramAccounts; this.followsFiles = followsFiles;
    }

    public static HostIdentityPort Over(IAccessTokenStore tokens, MessengerBackend messenger, InstagramBackend instagram, GoogleVoiceAccounts voice, bool followsFiles = false) =>
        new(tokens, () => GoogleSessionProfile.HasSavedSession, () => messenger.LocalAccountScope, () => voice.KnownAccountKeys,
            async token => instagram.HasSavedSession ? [.. (await instagram.GetAccountsAsync(token)).Select(account => account.Id)] : [], followsFiles);

    public bool GooglePaired => paired();

    // The window refreshes itself, as it always did, and the host follows its files; nothing here changes by itself that a listener must hear.
    public event EventHandler? Changed { add { } remove { } }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (tokens is GoogleSessionProfile profile) profile.Invalidate();
        return Task.CompletedTask;
    }

    public async Task<HostIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (followsFiles) FollowFiles();
        var scopes = new List<AccountScope>();
        // Whether a pairing is saved is a file check of its own, as the window made it: a key that cannot be read does not unpair Google
        // (Settings would offer a first pairing over a saved one) and does not take the other accounts' scopes with it.
        var isPaired = paired();
        string? key = null;
        try { key = await tokens.GetAsync(cancellationToken); keyUnreadable = false; }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // Said once per failing spell, since the state is looked at every few seconds.
            if (!keyUnreadable) GoogleMessagesLog.Write($"identity key=unreadable reason={GoogleMessagesLog.Reason(failure)}");
            keyUnreadable = true;
        }
        if (isPaired && NotificationIdentity.ProfileOf(key) is { } google) scopes.Add(new(GoogleModels.Account, "google:" + google));
        if (messengerScope() is { } messenger) scopes.Add(new(MessengerModels.Account, messenger));
        foreach (var voiceKey in voiceKeys()) scopes.Add(new(GoogleVoiceModels.Account(voiceKey), GoogleVoiceModels.Account(voiceKey)));
        foreach (var account in await instagramAccounts(cancellationToken)) scopes.Add(new(account, account));
        // A fixed order, so the same identity is the same JSON and a state that has not changed appends nothing.
        return new HostIdentity(isPaired, string.IsNullOrWhiteSpace(key) ? null : key,
            [.. scopes.DistinctBy(scope => scope.AccountId, StringComparer.Ordinal).OrderBy(scope => scope.AccountId, StringComparer.Ordinal)]);
    }

    private void FollowFiles()
    {
        if (tokens is not GoogleSessionProfile profile) return;
        var now = GoogleSessionProfile.IdentityStamp();
        lock (gate)
        {
            // Only when what the key is derived from changed, not on every rewrite of the session (GoogleSessionProfile.IdentityStamp).
            if (stamp is not null && !string.Equals(stamp, now, StringComparison.Ordinal)) profile.Refresh();
            stamp = now;
        }
    }
}
