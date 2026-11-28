using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// What a platform gives the <see cref="ServerComposition"/>: only what differs between the PC's server and the Linux image. Everything else (the stores,
/// the backends, the server and its backup, each account's restore step) is the composition's own.
/// </summary>
/// <param name="Protector">What seals the stores: DPAPI on Windows, the key file's on Linux.</param>
/// <param name="Photos">Google Voice's photo re-encoder.</param>
/// <param name="GoogleBrowser">The browser Google's sign-in renewal runs in, or one that says it cannot.</param>
/// <param name="HelperCells">What opens the cells Messenger's helper seals in its kept history.</param>
/// <param name="Admin">The server's administration for its page, made once the backends exist.</param>
/// <param name="Feeds">Which providers' feeds beyond Messenger's, Instagram's and Hermes' the server runs.</param>
/// <param name="Restores">The restore slots, from the start (a client's restore of a provider waits for the server's).</param>
/// <param name="Snoozes">The files a server's snoozes were kept in before the shared state, taken in once.</param>
/// <param name="Logs">The server's own logs, by name, for the page.</param>
/// <param name="Version">The program's version, as clients and the page see it.</param>
/// <param name="BackupFolder">Where the server's backup goes, or null for none chosen: asked just before the backup is made.</param>
/// <param name="Log">Where the composition's words go.</param>
internal sealed record ServerPlatform(IStateProtector Protector, IPhotoReencoder Photos, IGoogleBrowser GoogleBrowser, IHelperCellUnprotector HelperCells,
    Func<ServerBackends, IComposedServerAdmin> Admin, ServerFeeds Feeds, RestoreOnce Restores, IChatSnoozeFiles Snoozes, IServerLogs Logs, string Version, Func<string?> BackupFolder,
    Action<string> Log)
{
    /// <summary>The still for a video that came without one; none (Linux) leaves its play button, as the backends allow.</summary>
    public IVideoFrameExtractor? VideoFrames { get; init; }

    // What only the Linux image has. Null on Windows.

    /// <summary>The ports served in place of the providers this host does not run (<see cref="ServerUnavailablePorts"/>).</summary>
    public Func<ServerBackends, ServerUnavailablePorts>? Unavailable { get; init; }
    /// <summary>Sign-in in the server's own browser, shown on its page.</summary>
    public Func<ServerBackends, RemoteSignIns>? RemoteSignIn { get; init; }
    /// <summary>Messenger's kept history in the helper's own store; null lets the server search its default one.</summary>
    public Func<ServerBackends, IKeptHistorySearch>? KeptHistory { get; init; }
    /// <summary>How old the server's own Chrome is (protocol 14).</summary>
    public Func<bool>? BrowserOutOfDate { get; init; }
}

/// <summary>Which providers' feeds a server runs besides Messenger's, Instagram's and Hermes': Google Messages' (held by its own gate) and Google Voice's.</summary>
internal sealed record ServerFeeds(bool Google, bool Voice)
{
    public static ServerFeeds All { get; } = new(true, true);
    public static ServerFeeds MetaOnly { get; } = new(false, false);
}

/// <summary>The server's own browser sign-ins: Google Messages, Google Voice and Messenger or Instagram, all through one window.</summary>
internal sealed record RemoteSignIns(IRemoteSignInHost? Messages, IRemoteSignInHost? Voice, IRemoteCookieSignInHost? Meta);

/// <summary>The server's administration as the composition holds it: told of an account removed on the page, so what the server kept of it goes too.</summary>
internal interface IComposedServerAdmin : IServerAdmin
{
    Action<string, string?>? Ended { get; set; }
}

/// <summary>The composed backends, handed to the platform's parts that need them (its administration and the Linux image's ports).</summary>
internal sealed class ServerBackends(ServiceProvider services, ServerPlatform platform, LoginGates gates, Func<bool> googleConnected)
{
    private GoogleMessagesAccountPort? googleAccount;
    private GoogleVoiceAccountsPort? voicePort;

    public ServiceProvider Services => services;
    public IStateProtector Protector => platform.Protector;
    public string Version => platform.Version;
    public GoogleMessagesBackend Google { get; } = services.GetRequiredService<GoogleMessagesBackend>();
    public MessengerBackend Messenger { get; } = services.GetRequiredService<MessengerBackend>();
    public InstagramBackend Instagram { get; } = services.GetRequiredService<InstagramBackend>();
    public GoogleVoiceAccounts Voice { get; } = services.GetRequiredService<GoogleVoiceAccounts>();
    public GoogleSignInKeeper Keeper { get; } = services.GetRequiredService<GoogleSignInKeeper>();
    public ProviderAccounts Accounts { get; } = services.GetRequiredService<ProviderAccounts>();
    /// <summary>Google Messages as an account port over the backend, one for the whole host.</summary>
    public GoogleMessagesAccountPort GoogleAccount => googleAccount ??= new(Google, services.GetRequiredService<GoogleMediaBackend>());
    /// <summary>Google Voice's accounts as a port, one for the whole host.</summary>
    public IGoogleVoiceAccounts VoicePort => voicePort ??= new(Voice);
    /// <summary>Whether Google Messages is connected now, as the server's state says.</summary>
    public Func<bool> GoogleConnected => googleConnected;
    /// <summary>Each provider's lifecycle lock (<see cref="LoginGates"/>).</summary>
    public Func<string, SemaphoreSlim> LoginGate => gates.For;
    /// <summary>The ports in place of the providers this host does not run, once made; null where it runs them all.</summary>
    public ServerUnavailablePorts? Unavailable { get; internal set; }
}

/// <summary>
/// Messenger's and Instagram's lifecycle locks: the start's restore, a removal on the server's page and the ports the clients sign in through take the
/// provider's, so an account is never logged in while it is removed.
/// </summary>
internal sealed class LoginGates
{
    private readonly SemaphoreSlim messenger = new(1, 1), instagram = new(1, 1);

    public SemaphoreSlim For(string provider) => provider == RestoreOnce.Messenger ? messenger : instagram;
}
