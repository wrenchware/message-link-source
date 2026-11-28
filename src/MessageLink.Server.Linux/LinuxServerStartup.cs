using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;
using MessageLink.Server;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// The Linux server: the server composition (<see cref="ServerComposition"/>) without Windows. The protector is the key file's, the stores sit at the data
/// root, and only Messenger and Instagram run (<see cref="UnavailableProviders"/>) unless Google Messages was asked for. Building it connects nothing; only
/// <see cref="RestoreAsync"/> does, and only when the server was started to restore.
/// </summary>
internal sealed partial class LinuxServerStartup : IDisposable
{
    private readonly ComposedServer server;
    private readonly Action<string> log;
    // Google Messages, when the server was started to run it (LinuxServerStartup.Google.cs); null runs Messenger alone.
    private readonly LinuxGoogle? google;

    /// <summary>The provider's lifecycle lock: the start's restore and a removal on the server's page take it, so an account is never logged in while it is removed.</summary>
    internal SemaphoreSlim LoginGate(string provider) => server.LoginGate(provider);

    private LinuxServerStartup(ComposedServer server, Action<string> log, LinuxGoogle? google)
    {
        this.server = server; this.log = log; this.google = google;
    }

    public ServiceProvider Services => server.Services;
    public ProtocolServer Host => server.Host;
    /// <summary>The server's own backup, whose schedule the run starts beside the history walk.</summary>
    public MessageLink.Server.Backup.ServerBackupService Backup => server.Backup;
    /// <summary>The composed server, for the harness.</summary>
    internal ComposedServer Composed => server;

    /// <param name="root">The data root; every store of the server's own is under it.</param>
    /// <param name="keys">The server's sealing keys (<see cref="LinuxServerKeys.Prepare"/>).</param>
    /// <param name="restore">Whether <see cref="RestoreAsync"/> will run, so a client's restore of Messenger waits for the server's.</param>
    /// <param name="withGoogle">Google Messages and its renewal browser (<see cref="LinuxGoogle.Prepare"/>), or null to refuse it.</param>
    /// <param name="backups">Where the server's daily backup goes (<see cref="LinuxServerOptions.Backups"/>), or null for none.</param>
    public static LinuxServerStartup Compose(string root, LinuxServerKeys keys, Action<string> log, bool restore = false, LinuxGoogle? withGoogle = null, string? backups = null)
    {
        var version = typeof(LinuxServerStartup).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var server = ServerComposition.Compose(ServerLayout.Linux(root, keys.Folder), Platform(root, keys, log, restore, withGoogle, backups, version));
        // The Google sign-in keeper is never started without Google Messages: it renews through a browser this host then does not have. With it, the
        // restore starts it (LinuxServerStartup.Google.cs), as the Windows server's does.
        var startup = new LinuxServerStartup(server, log, withGoogle);
        startup.ConfigureAccounts();
        return startup;
    }

    /// <summary>What only the Linux image gives the composition.</summary>
    internal static ServerPlatform Platform(string root, LinuxServerKeys keys, Action<string> log, bool restore, LinuxGoogle? withGoogle, string? backups, string version)
    {
        var protector = new FileKeyStateProtector(keys.StateKey);
        return new(protector,
            // Asked for only by Google Voice and the Google renewal. The re-encoder is the real one, ready for when Voice runs here; the browser is
            // the real one only when Google Messages runs (MESSAGES_GOOGLE=1, LinuxGoogle), and otherwise says it cannot.
            new SkiaPhotoReencoder(), withGoogle is null ? new UnavailableGoogleBrowser() : withGoogle.KeeperBrowser, new HelperKeyFileCells(keys.HelperKey),
            // The owner's administration (protocol 20).
            backends => new LinuxServerAdmin(new LinuxServerAdmin.Parts(version, ProcessStarted(), ServerImage.FromEnvironment(Environment.GetEnvironmentVariable),
                () => withGoogle?.Sandbox?.Word, new MessengerAccount(backends.Messenger), () => MessengerKeyAsync(protector),
                backends.Unavailable?.Instagram ?? new InstagramAccountLifecycle(backends.Instagram), backends.Unavailable?.Voice ?? backends.VoicePort,
                withGoogle?.VoiceRuns == true, withGoogle is not null, () => GoogleSessionProfile.HasSavedSession, backends.GoogleConnected, backends.LoginGate,
                keys.HelperFolder, TimeProvider.System, log, Path.Combine(Path.GetDirectoryName(MessengerBackend.SessionPath)!, "encrypted.on"),
                () => backends.Google.OwnNumber, () => backends.Messenger.OwnName,
                () => backends.Google.PairingLost || backends.Google.SignInExpired && !backends.Google.SignInPresumed, backends.Google.RemoveAsync)),
            // Messenger's and Instagram's feeds alone: Google's would connect a saved pairing the moment anyone subscribed. Instagram's polls only while its
            // helper is connected, at the "nobody looking" pace while no client is (ServerClientState), as on Windows. With Google Messages its feed joins
            // them, and Google Voice's when Voice runs here.
            new ServerFeeds(Google: withGoogle is not null, Voice: withGoogle?.VoiceRuns == true),
            // Both slots exist whenever the server restores, so a client's restore of either waits for (or is answered skipped by) the paced one, never logs in
            // beside it or ahead of the ledger; and Google's too when it runs.
            !restore ? RestoreOnce.None : withGoogle is null ? new RestoreOnce([RestoreOnce.Messenger, RestoreOnce.Instagram]) : GoogleRestores(withGoogle.VoiceRuns),
            new SealedChatSnoozeStore(Path.Combine(root, SealedChatSnoozeStore.FolderName), protector),
            // The server's own logs, by name, for its owner's copies (protocol 18): never a path from a client, and nothing but the fixed-format logs.
            new ServerLogFiles(LinuxServerLogs.Paths(root, keys.HelperFolder)), version, () => backups, log)
        {
            // Google's backend is left out of the real restarter, which then answers its scope not_started; the wrapper answers it first.
            Unavailable = backends => withGoogle is null
                ? UnavailableProviders.Ports(new ServerHelperRestarter(null, backends.Keeper, backends.Messenger, backends.Instagram, backends.Voice, log) { Accounts = backends.Accounts })
                : GooglePorts(withGoogle, backends.GoogleAccount, backends.Google, backends.Keeper, backends.Messenger, backends.Instagram, backends.Voice, backends.VoicePort, log, backends.Accounts),
            // One object for the browser sign-in of Google Messages, Google Voice and Messenger or Instagram, so that only one runs at a time (LinuxServerStartup.Google.cs).
            RemoteSignIn = backends => RemoteSignIn(withGoogle, withGoogle is null ? null : backends.GoogleAccount, backends.VoicePort, log),
            // Everything Messenger's helper keeps in the signed-in account's messages.db, searched read-only for search.query's kept source and warmed by
            // search.warmKept, as the Windows app searches its own (MessengerKeptHistory). The store is in the keys folder, its cells sealed with the helper's key.
            KeptHistory = backends =>
            {
                var messenger = backends.Messenger;
                var kept = new MessengerKeptHistory(() => messenger.SelfId is { } self ? MessengerKeptAccount.In(keys.HelperFolder, self) : null,
                    messenger.KnownChat, log, new HelperKeyFileCells(keys.HelperKey));
                // Signing out lets go of the index at once, rather than at the next search.
                messenger.ChatsChanged += (_, _) => { if (messenger.SelfId is null) kept.Warm(); };
                return kept;
            },
            // How old the server's own Chrome is, asked each time the state is built (protocol 14), whether or not the sign-in in it is offered.
            BrowserOutOfDate = withGoogle?.Chrome is { } chromeAge ? () => chromeAge.Build.OutOfDate(DateTimeOffset.UtcNow) : null,
        };
    }

    /// <summary>
    /// The start's restore: Messenger's and Instagram's saved sessions, each once (<see cref="RestoreOnce"/>) and side by side as the Windows server
    /// restores them, and then the feed is let go. Logged as words and counts only.
    /// </summary>
    public async Task RestoreAsync(CancellationToken stop)
    {
        try
        {
            // Inside the try: a stop during the added accounts' restore still answers a client waiting on the server's, rather than holding the stop's drain.
            await Services.GetRequiredService<ProviderAccounts>().RestoreAsync(stop);
            if (google is not null) { await RestoreWithGoogleAsync(stop).ConfigureAwait(false); return; }
            if (stop.IsCancellationRequested)
            {
                foreach (var provider in Restoring) log($"server restore provider={provider} outcome=stopped");
                return;
            }
            var restoring = server.RestoreMessengerAndInstagramAsync(stop);
            // Let go while the restore runs, as the Windows server does once the others have started: a client then sees the connection as it comes up.
            if (!stop.IsCancellationRequested) server.OpenFeed();
            await restoring;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            foreach (var provider in Restoring) log($"server restore provider={provider} outcome=stopped");
        }
        // However it ended, a client waiting on the server's restore is answered (skipped) rather than left waiting.
        finally { server.EndRestores(); }
    }

    /// <summary>The providers this server restores, by the words its log names them with.</summary>
    public static readonly string[] Providers = [RestoreOnce.Messenger, RestoreOnce.Instagram];

    /// <summary>The providers this server's restore reaches: <see cref="Providers"/>, and Google Messages when it runs here.</summary>
    public IReadOnlyList<string> Restoring => google is null ? Providers : google.VoiceRuns ? [.. Providers, RestoreOnce.Google, VoiceProvider] : [.. Providers, RestoreOnce.Google];

    /// <summary>Whether the start's restore would log a Messenger or Instagram account in (<see cref="ComposedServer.RestoresAccounts"/>).</summary>
    public bool RestoresAccounts => server.RestoresAccounts;

    /// <summary>The server's restore will not run: a client waiting on it is answered skipped.</summary>
    public void EndRestores() => server.EndRestores();

    /// <summary>The start's restore is skipped or held: every feed but Google's own goes now, so Hermes and an account connected meanwhile are heard.</summary>
    public void OpenPassiveFeed() => server.OpenPassiveFeed();

    // When this process started, for host.status; now, should the system not say.
    private static DateTimeOffset ProcessStarted()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { return DateTimeOffset.UtcNow; }
    }

    // The helper's key for the Messenger account saved here, from the saved session's Facebook id (unsealed, read and dropped): the account a sign-out would end, read
    // from what is saved rather than from the conversation list in memory. Null when no session names one. Never logged.
    private static async Task<string?> MessengerKeyAsync(IStateProtector protector)
    {
        byte[]? session = null;
        try
        {
            if (!File.Exists(MessengerBackend.SessionPath)) return null;
            session = await protector.UnprotectAsync(await File.ReadAllBytesAsync(MessengerBackend.SessionPath).ConfigureAwait(false)).ConfigureAwait(false);
            return MessengerSessionShape.ViewerOf(session) is { } viewer ? MessengerBackend.AccountKey(viewer) : null;
        }
        // A session not in the key file's format (cut short, or another format) names no device: the removal still signs out, and leaves the device folder where it is.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidDataException) { return null; }
        // The session holds the cookies.
        finally { if (session is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(session); }
    }

    public void Dispose()
    {
        server.Dispose();
        signInQueue?.Dispose();
        accountSignInQueue?.Dispose();
    }
}
