using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Server;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// The providers this host does not run, refused rather than half-working (SWITCHBOARD.md stage 4, "First Unraid test"):
/// <list type="bullet">
/// <item>Google Messages: Google refuses a copied sign-in at two hours and only a kept browser profile renews it (<c>PROVIDERS.md</c>). The Linux
/// renewal (step 4.3, <see cref="ChromiumGoogleBrowser"/>) runs only with the hidden <c>MESSAGES_GOOGLE=1</c> (<see cref="PortsWithGoogle"/>) until
/// 5.4 has shown live that Google renews a profile seeded on the server; without it a session here would die within two hours of every sign-in.</item>
/// <item>Google Voice: its renewal needs a browser profile of each account's own, which only the server's own Chrome keeps (<see cref="PortsWithGoogle"/> runs it beside that Chrome; the photo re-encoder is
/// Skia's, step 4.4). Everywhere else it is refused.</item>
/// </list>
/// Each account action and restore answers <c>account_failed</c> with <see cref="AccountFailureWords.NotOnThisHost"/>; the Google connect answers
/// <see cref="BackendFailure.Unsupported"/> with a sentence; a helper restart is refused with the same word. The reads say "nothing here" (not
/// signed in, no accounts), the state a client already shows as a service not set up, never as one that is failing. Behind all of this, the
/// host starts no helper but Messenger's and Instagram's (<see cref="Runs"/>, <c>HelperConnection.Allowed</c>; and Google Messages' when it runs), so a route missed here still cannot
/// reach those services.
/// <para>
/// Messenger and Instagram run. Instagram's helper keeps nothing on disk (the session it hands back is sealed by the host, under the host's key) and
/// dies with the server as Messenger's does (<c>parentwatch_linux.go</c>). Its pacing is the backends' own, unchanged from Windows, and the host's
/// restore of it is held by the same <c>RestoreLedger</c> as Messenger's.
/// </para>
/// </summary>
internal static class UnavailableProviders
{
    /// <summary>The helpers this host starts, by file name.</summary>
    public const string MessengerHelper = "messenger-helper", InstagramHelper = "instagram-helper";

    /// <summary>Whether this host may start the helper of that file name (<c>HelperConnection.Allowed</c>).</summary>
    public static bool Runs(string name) => name is MessengerHelper or InstagramHelper;

    // Instagram's port is left out, so the host builds the real one.
    public static HostUnavailablePorts Ports(IHelperRestarter restarts) =>
        new(new UnavailableGoogleAccount(), null, new UnavailableVoiceAccounts(), new UnavailableGoogleBackend(), new HostedRestarts(restarts));

    /// <summary>
    /// The same with Google Messages run (<c>MESSAGES_GOOGLE=1</c>, <see cref="LinuxGoogle"/>): its account is the one given (the backend's, keeping a
    /// browser profile at each sign-in), its connect and restart are the backend's own. Instagram runs as without it. Google Voice stays refused unless <paramref name="voice"/> is given (the
    /// server's own Chrome is there to sign it in and renew it, SWITCHBOARD.md 5.4): its accounts are then the real ones, behind a port that refuses the cookie hand-over from a client, and
    /// their restart is the real one's.
    /// </summary>
    public static HostUnavailablePorts PortsWithGoogle(IHelperRestarter restarts, IGoogleMessagesAccount google, IGoogleVoiceAccounts? voice = null) =>
        voice is null ? new(google, null, new UnavailableVoiceAccounts(), null, new HostedRestarts(restarts, google: true), GoogleRuns: true)
            : new(google, null, new ServerSignInVoiceAccounts(voice), null, new HostedRestarts(restarts, google: true, voice: true), GoogleRuns: true, VoiceRuns: true);

    internal static InvalidOperationException Refused() => new(AccountFailureWords.NotOnThisHost);
}

internal sealed class UnavailableGoogleAccount : IGoogleMessagesAccount
{
    public GoogleMessagesAccountState State { get; } = new(false, false, false, false, null, null, false);
    public event EventHandler? Changed { add { } remove { } }
    public event EventHandler? SimsChanged { add { } remove { } }
    public event EventHandler? DefaultSmsAppChanged { add { } remove { } }
    public event EventHandler<bool>? RenumberedChanged { add { } remove { } }
    public Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice = false) =>
        Task.FromException(UnavailableProviders.Refused());
    public GoogleSimChip? SimFor(string? chatId) => null;
    public Task SetHistoryIndexingAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromException(UnavailableProviders.Refused());
    public Task LoadRenumberedAsync() => Task.CompletedTask;
    public void WarmArchive() { }
    public IReadOnlyList<ChatSummary> KnownConversations => [];
    public Task<IReadOnlyList<GoogleKnownChat>> KnownChatsForResetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GoogleKnownChat>>([]);
    public Task<IReadOnlyList<string>> AvatarCacheKeysAsync(IReadOnlyList<string> nativeIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task ForgetAvatarsAsync(CancellationToken cancellationToken = default) => Task.FromException(UnavailableProviders.Refused());
    public Task ResetLocalAsync(string profile) => Task.FromException(UnavailableProviders.Refused());
}

internal sealed class UnavailableVoiceAccounts : IGoogleVoiceAccounts
{
    public IReadOnlyList<GoogleVoiceAccountState> Accounts => [];
    public IReadOnlyList<string> KnownAccountKeys => [];
    public event EventHandler? Changed { add { } remove { } }
    public Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<string>>(UnavailableProviders.Refused());
    public Task<GoogleVoiceAccountState> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default) =>
        Task.FromException<GoogleVoiceAccountState>(UnavailableProviders.Refused());
    public Task SignOutAsync(string key) => Task.FromException(UnavailableProviders.Refused());
}

/// <summary>
/// Google Voice's accounts on a server that signs it in in its own Chrome (protocol 15): the real accounts, except that the cookie hand-over from a client's window is refused with a fixed word
/// (<c>sign_in_use_server</c>). A sign-in copied from a PC is refused by Google at two hours, and the server holds no browser profile it could renew it from; the account is added by the
/// server's own sign-in (<see cref="ChromeRemoteSignIn"/>), which calls the real accounts' add itself.
/// </summary>
internal sealed class ServerSignInVoiceAccounts(IGoogleVoiceAccounts inner) : IGoogleVoiceAccounts
{
    public IReadOnlyList<GoogleVoiceAccountState> Accounts => inner.Accounts;
    public IReadOnlyList<string> KnownAccountKeys => inner.KnownAccountKeys;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default) => inner.RestoreAsync(cancellationToken);
    public Task<GoogleVoiceAccountState> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default)
    {
        // Cleared as the real add clears what it is given: a refused hand-over keeps nothing of the client's cookies.
        cookies.Clear();
        return Task.FromException<GoogleVoiceAccountState>(new InvalidOperationException("sign_in_use_server"));
    }
    public Task SignOutAsync(string key) => inner.SignOutAsync(key);
}

// backend.discover and accounts.list are the Google connect; they are not account routes, so they carry a backend failure the window shows as a sentence.
internal sealed class UnavailableGoogleBackend : IChatBackend
{
    public const string Sentence = "Google Messages is not available on this server.";
    public Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromException<BackendInfo>(new BackendException(BackendFailure.Unsupported, Sentence));
    public Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ChatAccount>>(new BackendException(BackendFailure.Unsupported, Sentence));
}

// Messenger's and Instagram's restarts are the real ones (Instagram's spends the backend's own recovery budget, as on Windows), and Google Messages'
// when it runs here, and Google Voice's when its accounts do; a scope no helper serves gets the real one's own refusal; Google Voice and Google Messages when they do not run are refused here.
internal sealed class HostedRestarts(IHelperRestarter inner, bool google = false, bool voice = false) : IHelperRestarter
{
    public Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false) =>
        scope is ProviderScopes.Messenger or ProviderScopes.Instagram || google && scope == ProviderScopes.Google || voice && HelperRestartScope.Service(scope) == "gvoice"
            || HelperRestartScope.Service(scope) is null
            ? inner.RestartAsync(scope, signingIn, force, token, settle)
            : Task.FromResult(new HelperRestartResult("refused", AccountFailureWords.NotOnThisHost, 0));
}

// The browser only Google Voice and the Google renewal ask of a host. Registered because the container builds those backends (their routes are refused
// above); nothing that runs here calls it, and if something did, it would answer "cannot" rather than pretend. (Voice's photo re-encoder is real:
// SkiaPhotoReencoder.)
internal sealed class UnavailableGoogleBrowser : MessageLink.App.Services.IGoogleBrowser
{
    public Task<MessageLink.App.Services.GoogleMessages.GoogleRefreshResult> RefreshAsync(string service, string profileId, CancellationToken cancellationToken) =>
        Task.FromResult(new MessageLink.App.Services.GoogleMessages.GoogleRefreshResult(null, AccountFailureWords.NotOnThisHost));
    public Task<MessageLink.App.GoogleSignInResult?> CreateProfileAsync(IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken) =>
        Task.FromResult<MessageLink.App.GoogleSignInResult?>(null);
    // A kept profile is never forgotten for being unreadable here: a Voice account's profile id is dropped only when its profile is known to be gone.
    public bool HasProfile(string profileId, string service = MessageLink.App.GoogleSignInProfile.VoiceService) => true;
    public Task DeleteProfileAsync(string profileId, string service = MessageLink.App.GoogleSignInProfile.VoiceService) => Task.FromException(UnavailableProviders.Refused());
}
