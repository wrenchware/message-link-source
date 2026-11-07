using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

// What the host itself is doing to an account that a restart must not race: a sign-in it runs (Messenger's or
// Instagram's Connect, adding a Voice account). Google's invisible refresh is the keeper's, so it needs no host.
internal interface IRestartGuard
{
    // service: google_messages, messenger, instagram or gvoice, the names the restart log uses.
    bool SigningIn(string service);
    // Held while a restart runs, so the host's own Connect and Sign out for that service do nothing meanwhile.
    IDisposable Hold(string service);
}

// Each service's own restore path behind one shape, for Connection health's Restart in any host. A sign-in the host is
// running refuses it the same way the backend's own state does.
internal static class HelperRestartTargets
{
    public static IHelperRestartTarget? For(string scope, GoogleMessagesBackend? google, GoogleSignInKeeper keeper,
        MessengerBackend messenger, InstagramBackend instagram, GoogleVoiceAccounts voice, IRestartGuard guard)
    {
        switch (scope)
        {
            case "google-direct:":
                if (google is null) return null;
                return new HelperRestartTarget("google_messages", () => keeper.Recovering ? "signing_in" : google.RestartRefusal(),
                    () => google.MutationsInFlight, () => google.MutationMark, google.RestartAsync);
            case "messenger:":
                return new HelperRestartTarget("messenger", () => guard.SigningIn("messenger") ? "signing_in" : messenger.RestartRefusal(),
                    () => messenger.MutationsInFlight, () => messenger.MutationMark, async (force, through) =>
                    {
                        using (guard.Hold("messenger")) return await messenger.RestartAsync(force, through);
                    });
            case "instagram:":
                return new HelperRestartTarget("instagram", () => guard.SigningIn("instagram") ? "signing_in" : instagram.RestartRefusal(),
                    () => instagram.MutationsInFlight, () => instagram.MutationMark, async (force, through) =>
                    {
                        using (guard.Hold("instagram")) return await instagram.RestartAsync(force, through);
                    });
        }
        if (!scope.StartsWith("gvoice:", StringComparison.Ordinal) || scope.Length <= "gvoice:".Length + 1) return null;
        var key = scope["gvoice:".Length..^1];
        return new HelperRestartTarget("gvoice", () => guard.SigningIn("gvoice") ? "signing_in" : voice.RestartRefusal(key),
            () => voice.MutationsInFlight(key), () => voice.MutationMark(key), (force, through) => voice.RestartAsync(key, force, through));
    }
}

// Connection health's Restart as the host serves it (IHelperRestarter): the targets above under one HelperRestarts, which keeps
// the one-at-a-time rule and the restart log across every client of this host. Only the client knows whether a sign-in window of its own
// is open, so that arrives as a flag; the host's own refusals (the backend's state, the keeper's recovery) are asked as before. The Hold
// that keeps the client's Connect and Sign out still while a restart runs is the client's to take: its calls reach this host through
// their own (journaled) methods, and the backend refuses them while it restarts.
internal sealed class HostHelperRestarter(GoogleMessagesBackend? google, GoogleSignInKeeper keeper, MessengerBackend messenger,
    InstagramBackend instagram, GoogleVoiceAccounts voice, HelperRestarts restarts) : IHelperRestarter
{
    // The scope is a client's string and goes no further than the table and the running set, so its size is all that is checked.
    internal const int MaxScopeLength = 128;
    internal ProviderAccounts? Accounts { get; set; }

    public HostHelperRestarter(GoogleMessagesBackend? google, GoogleSignInKeeper keeper, MessengerBackend messenger,
        InstagramBackend instagram, GoogleVoiceAccounts voice, Action<string> log)
        : this(google, keeper, messenger, instagram, voice, new HelperRestarts(TimeProvider.System, log)) { }

    public async Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false)
    {
        IHelperRestartTarget? target;
        if (scope.Length <= MaxScopeLength && NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(scope) is { } key && Accounts is { } accounts)
        {
            var account = accounts.ByKey(key);
            if (scope != ProviderAccounts.Scope(account)) return new("refused", "unknown_scope", 0);
            var native = account.Slot.Ids.Native(scope);
            target = HelperRestartTargets.For(native, account.Google ?? google, account.Keeper ?? keeper, account.Messenger ?? messenger, account.Instagram ?? instagram, voice, new ClientGuard(signingIn));
        }
        else target = scope.Length > MaxScopeLength ? null : HelperRestartTargets.For(scope, google, keeper, messenger, instagram, voice, new ClientGuard(signingIn));
        // A scope no helper serves, or Google's backend missing from a host that was composed without it: said as the refusals the window already words.
        if (target is null) return new("refused", HelperRestartScope.Service(scope) is null ? "unknown_scope" : "not_started", 0);
        return await restarts.RunAsync(scope, target, settle, force, token).ConfigureAwait(false);
    }

    private sealed class ClientGuard(bool signingIn) : IRestartGuard
    {
        public bool SigningIn(string service) => signingIn;
        public IDisposable Hold(string service) => NoHold.Instance;

        private sealed class NoHold : IDisposable
        {
            public static readonly NoHold Instance = new();
            public void Dispose() { }
        }
    }
}
