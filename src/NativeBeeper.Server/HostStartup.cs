using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;
using NativeBeeper.WindowsPlatform;

namespace NativeBeeper.Server;

/// <summary>
/// The server's composition: the backends and the Windows ports they use in a container, and the protocol host over them. This is the app's
/// <c>OnLaunched</c> without anything that draws: no window, no notifications, no decorators (merges, labels, local preferences, the search
/// catalogue and the conversation cache stay in the client, SWITCHBOARD.md stage 3). Building it connects nothing; the providers are started
/// only by <see cref="RestoreAsync"/>, and only when the server was started with <c>--restore</c>.
/// </summary>
internal sealed partial class HostStartup : IDisposable
{
    // The conversation-id prefix Google Messages' events and conversations carry (as the app's connection health and the feed's composite name it).
    private const string GoogleScope = "google-direct:";

    private readonly GatedRealtimeFeed feed;
    private readonly Action<string> log;
    private readonly IStaQueue sta;
    private readonly RestoreOnce restores;

    private HostStartup(ServiceProvider services, ProtocolHost host, GatedRealtimeFeed feed, IStaQueue sta, RestoreOnce restores, Action<string> log)
    {
        Services = services; Host = host; this.feed = feed; this.sta = sta; this.restores = restores; this.log = log;
    }

    public ServiceProvider Services { get; }
    public ProtocolHost Host { get; }

    /// <summary>The server's own backup (SWITCHBOARD.md 10.2, on Windows 10.7): its schedule runs beside the host, into the folder the person chose.</summary>
    public NativeBeeper.Host.Backup.ServerBackupService? Backup { get; private init; }

    /// <summary>
    /// Where the Windows server's backup keeps what it reads and writes: the data root, its keys folder (<c>Server\Keys</c>, the network door's), and the whole history
    /// in <c>Server\history.db</c>, where this host keeps it.
    /// </summary>
    public static NativeBeeper.Host.Backup.ServerBackupPaths BackupPaths(string root) =>
        new(root, ServerNetwork.KeysFolder(root)) { HistoryFile = Path.Combine(root, "Server", "history.db"), TlsKeySealed = true };

    /// <param name="log">Where the composition's words go.</param>
    /// <param name="sta">The shell's STA thread (<see cref="ServerShell.Sta"/>), where the Google renewal's browser runs.</param>
    /// <param name="restore">Whether <see cref="RestoreAsync"/> will run (the server was started with <c>--restore</c>), so that a client's restore calls wait for it.</param>
    public static HostStartup Compose(Action<string> log, IStaQueue sta, bool restore = false)
    {
        var step = new StepClock(log);
        var collection = new ServiceCollection();
        // What only Windows can do, as the app registers it.
        collection.AddSingleton<IStateProtector, WindowsStateProtector>();
        collection.AddSingleton<IHelperCellUnprotector, MessengerSealedCells>();
        collection.AddSingleton<IPhotoReencoder, WindowsPhotoReencoder>();
        collection.AddSingleton<IVideoFrameExtractor, WindowsVideoFrameExtractor>();
        // The stores the backends keep their state in, at the same paths as the app's: one host per data root, so only one of them is open.
        collection.AddSingleton<IPrivateStateStore>(sp => new NativeBeeper.Storage.SqlitePrivateStateStore(AppPaths.Combine("state.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<PrivateState>();
        collection.AddSingleton(sp => new NativeBeeper.Storage.MediaDiskCache(AppPaths.Combine("media.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new NativeBeeper.Storage.MessageArchive(AppPaths.Combine("archive.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<ISavedPhotoStore>(sp => new NativeBeeper.Storage.SavedPhotoStore(AppPaths.Combine("saved-photos.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new SavedPhotos(sp.GetRequiredService<ISavedPhotoStore>(), () => sp.GetService<IMediaReader>(), AppLog.Write));
        collection.AddSingleton<ComposerActivity>();
        // The providers and the routers over them.
        collection.AddBackends();
        // What the host knows of its clients (client.state): Instagram's poll, the Google keeper's busy check and the routers' private-reading net read it.
        // With no client connected it says nobody is looking, so a headless host never polls at the "present" pace.
        collection.AddSingleton(sp => new HostClientState(AppPaths.Combine("LastKnownClient-v1", "state.protected"), sp.GetRequiredService<IStateProtector>(), AppLog.Write));
        collection.AddSingleton<IClientBusy>(sp => sp.GetRequiredService<HostClientState>());
        collection.AddSingleton<IAppPresence>(sp => sp.GetRequiredService<HostClientState>());
        collection.Replace(ServiceDescriptor.Singleton(sp => new MessagingRouter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<MessengerBackend>(),
            sp.GetRequiredService<GoogleVoiceBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<HostClientState>().Reading)));
        collection.Replace(ServiceDescriptor.Singleton(sp => new TypingRouter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<MessengerBackend>(),
            sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<ComposerActivity>(), sp.GetRequiredService<HostClientState>().Reading)));
        // The avatar reader AddBackends registers reads chats through the port the window decorates; here nothing decorates it.
        collection.AddSingleton<IChatReader>(sp => sp.GetRequiredService<MessagingRouter>());
        // The renewal's browser is WebView2 on the shell's STA thread, the app's own code (NativeBeeper.Windows.Browser). Building it loads nothing:
        // WebView2 starts only when something asks for a refresh, and nothing does until the keeper starts, which only a restore does.
        collection.AddSingleton<IGoogleBrowser>(new WebViewGoogleBrowser(sta));
        step.Took("registrations");
        var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        step.Took("container");

        var protector = services.GetRequiredService<IStateProtector>();
        step.Took("protector");
        var google = services.GetRequiredService<GoogleMessagesBackend>();
        step.Took("google");
        var messenger = services.GetRequiredService<MessengerBackend>();
        step.Took("messenger");
        var instagram = services.GetRequiredService<InstagramBackend>();
        step.Took("instagram");
        var voice = services.GetRequiredService<GoogleVoiceBackend>();
        step.Took("voice");
        var feed = new GatedRealtimeFeed(new CompositeRealtimeFeed(() => MessagingRouter.GoogleMessagesExpected,
            ("google-direct:", google), ("messenger:", messenger), ("gvoice:", voice), ("instagram:", instagram)));
        // The host restores its own accounts only with --restore. The slots exist from here, before the pipe opens, so a client that asks for a restore
        // ahead of the host's own begins waits for it (RestoreOnce).
        var restores = restore ? RestoreOnce.ForAll() : RestoreOnce.None;
        step.Took("feed");
        // Made on first use, never by the app's in-process host (stage 6). The host's snoozes are rows of it (slice 3), and its ChatSnoozes-v1 files (the
        // in-process app's too, on a root they share) are taken in once, at that first use, and left as they are.
        var shared = new SharedStateStore(AppPaths.Combine("Server", SharedStateStore.FileName), protector, AppLog.Write);
        var version = typeof(HostStartup).Assembly.GetName().Version?.ToString(3) ?? "dev";
        // The PC's own server is administered as the Linux one is (SWITCHBOARD.md 10.7): its status, its accounts and their removal, for its page and its owner.
        ProtocolHost? built = null;
        var admin = new WindowsServerAdmin(new WindowsServerAdmin.Parts(version, DateTimeOffset.UtcNow, services.GetRequiredService<IMessengerAccount>(),
            services.GetRequiredService<IInstagramAccount>(), services.GetRequiredService<IGoogleVoiceAccounts>(), () => GoogleSessionProfile.HasSavedSession,
            () => built?.StatePublisher.Latest?.State.Scopes.Any(scope => scope.Scope == ProviderScopes.Google && scope.Connected) == true, log, () => google.OwnNumber, () => messenger.OwnName));
        // The server's own backup (stage 10, D7): into the folder the person chose on the page, the PC backup's folder and password taken over the first time.
        var paths = BackupPaths(AppPaths.DataRoot);
        var folderChoice = Path.Combine(paths.Backup, "folder.txt");
        PcBackupHandover.Take(AppPaths.DataRoot, paths, folderChoice, log);
        var backup = new NativeBeeper.Host.Backup.ServerBackupService(paths, protector, NativeBeeper.Host.Backup.ServerBackupService.ReadFolderChoice(folderChoice),
            new NativeBeeper.Host.Backup.BackendBackupMessenger(messenger, services.GetRequiredService<ProviderAccounts>()), version, log);
        var host = ProtocolHost.Create(new ProtocolHostOptions(services.GetRequiredService<MessagingRouter>(), services.GetRequiredService<DirectoryRouter>(),
            services.GetRequiredService<MediaRouter>(), services.GetRequiredService<TypingRouter>(), google, services.GetRequiredService<GoogleMediaBackend>(),
            messenger, instagram, services.GetRequiredService<GoogleVoiceAccounts>(), feed,
            SharedChatSnoozes.Over(shared, new ProtectedChatSnoozeStore(AppPaths.Combine(ProtectedChatSnoozeStore.FolderName))),
            version, AppLog.Write, services.GetRequiredService<HostDiagnosticsSource>(),
            services.GetRequiredService<GoogleSignInKeeper>(), services.GetRequiredKeyedService<IAccessTokenStore>(BackendServices.HostTokensKey),
            JournalStore: new ProtectedFileJournalStore(AppPaths.Combine("Server", "journal.protected"), protector),
            IncomingCards: services.GetRequiredService<IncomingCardRequester>(),
            ClientState: services.GetRequiredService<HostClientState>(),
            UploadFolder: AppPaths.Combine("Server", "uploads"), Restores: restores,
            // The incoming unarchive is the server's, once for all its clients (stage 6 slice 9). Its watermarks are the host's own state.db rows, the
            // same ones the in-process app keeps on this root: a run of either after the other then never asks again for an arrival the other asked for.
            IncomingUnarchive: new HostUnarchive(services.GetRequiredKeyedService<PrivateState>(BackendServices.HostTokensKey)),
            // The originals of the clients' saved photos are the server's to keep (stage 6 slice 8): its own store, read through its own media router.
            SavedPhotos: services.GetRequiredService<SavedPhotos>(), SharedState: shared,
            // The scheduled sends are the server's too (stage 6 slice 11): kept as rows of the shared state, fired by this host with every client closed.
            Scheduled: new HostScheduled(), ConversationSyncStore: services.GetRequiredService<IPrivateStateStore>(),
            // Every conversation kept whole (SWITCHBOARD.md stage 8, protocol 24), in the server's own folder: the app's in-process host keeps none.
            WholeHistory: new NativeBeeper.Storage.HistoryDatabase(AppPaths.Combine("Server", "history.db"), protector),
            // Its status, accounts and their removal (protocol 20), its logs by name for the page (protocol 18), and its backup (protocol 26), as the Linux host's.
            Admin: admin, Logs: new HostLogFiles(ServerLogs.Paths(AppPaths.DataRoot)), Backup: backup, Accounts: services.GetRequiredService<ProviderAccounts>(), HelperCells: services.GetRequiredService<IHelperCellUnprotector>()));
        built = host;
        backup.Changed += status => host.Services.Events?.AppendSignal(new AccountSignal(AccountSignals.Backup, !status.Stale));
        backup.CurrentKey = shared.Space.CurrentKeyAsync;
        backup.ImportBundle = async (bundle, ct) =>
        {
            var imported = await shared.ImportBundleAsync(bundle, resume: true, ct).ConfigureAwait(false);
            return (imported.Status, imported.Added, imported.Skipped, imported.Refused);
        };
        admin.Ended = host.Services.AccountEnded;
        step.Took("host");
        // The sign-in keeper is not started here: it listens for Google's refusals and renews, so it starts with the restore and only with --restore
        // (RestoreAsync), the one thing that lets a provider touch the network.
        var startup = new HostStartup(services, host, feed, sta, restores, log) { Backup = backup };
        startup.ConfigureAccounts();
        return startup;
    }

    /// <summary>
    /// The launch's restore, in the app's order (SWITCHBOARD.md 1.7d): the other providers start first and are not awaited before the phone, so a stale
    /// Google session never holds them up, and each is restored once, so Messenger and Instagram never log in twice. Then the feed is let go. Every
    /// outcome is logged as a word and a count, never a name.
    /// </summary>
    public async Task RestoreAsync(CancellationToken stop)
    {
        await Services.GetRequiredService<ProviderAccounts>().RestoreAsync(stop);
        try
        {
            // The window starts the keeper as it is built, ahead of the launch's connect, so a refusal that connect hears is renewed. Same here.
            var signIn = StartSignInKeeper(stop);
            var messenger = Services.GetRequiredService<IMessengerAccount>();
            var instagram = Services.GetRequiredService<IInstagramAccount>();
            var voice = Services.GetRequiredService<IGoogleVoiceAccounts>();
            Func<bool> stopped = () => stop.IsCancellationRequested;
            // Each provider's restore goes through the host's one slot for it (RestoreOnce), so the restore a window asks for when it launches against
            // this server is answered with this one's result, or waits for it, and never logs in a second time.
            var others = Task.WhenAll(
                Restored("messenger", stop, () => RestoreAccountAsync(RestoreOnce.Messenger, messenger.State is { Saved: true, Connected: false }, () => messenger.RestoreAsync(stopped))),
                Restored("instagram", stop, () => RestoreAccountAsync(RestoreOnce.Instagram, instagram.State is { Saved: true, Connected: false }, () => instagram.RestoreAsync(stopped))),
                Restored("gvoice", stop, async () => $"failed={(await restores.RunAsync(RestoreOnce.Voice, () => voice.RestoreAsync(stop))).Count}"));
            try
            {
                await Restored("google", stop, () => restores.RunAsync(RestoreOnce.Google, async () =>
                {
                    if (!MessagingRouter.GoogleMessagesExpected) return "not_saved";
                    // Held for the connect, as the window's Connect command is busy for its own: a renewal that ends meanwhile does not connect again beside it.
                    using var connecting = signIn.BeginConnect();
                    var backend = Services.GetRequiredService<IBeeperBackend>();
                    await backend.DiscoverAsync(stop);
                    return $"accounts={(await backend.GetAccountsAsync(stop)).Count}";
                }));
            }
            // However it ended, or never began: a renewal that waited for the launch's connect goes on.
            finally { signIn.EndStartupLoad(); }
            // A feed let go connects Google's saved pairing by itself, so a stop that has begun leaves it shut.
            if (!stop.IsCancellationRequested) feed.Open();
            await others;
        }
        // However the restore ended, a client waiting on a provider it never reached is answered (skipped) rather than left waiting.
        finally { restores.End(); }
    }

    /// <summary>The host's restore will not run (the server was stopped first): a client waiting on it is answered skipped.</summary>
    public void EndRestores() => restores.End();

    /// <summary>
    /// Whether the start's restore would log a Messenger or Instagram account in: the restore ledger counts only those (<see cref="RestoreLedger.StartAsync"/>), as the
    /// Linux host's does, so a start with only Google or nothing saved does not spend the hour.
    /// </summary>
    public bool RestoresAccounts => Services.GetRequiredService<ProviderAccounts>().Added.Any(a => a.Messenger?.SessionSaved == true || a.Instagram?.HasSavedSession == true) || Services.GetRequiredService<IMessengerAccount>().State.Saved || Services.GetRequiredService<IInstagramAccount>().State.Saved;

    // A provider with no saved session, or already connected, is not restored: the answer a client gets for it is "skipped", as the app's own restore says.
    private async Task<string> RestoreAccountAsync(string provider, bool restorable, Func<Task<AccountRestore>> restore)
    {
        var outcome = "not_saved";
        await restores.RunAsync(provider, async () =>
        {
            if (!restorable) return new AccountRestore(null, Skipped: true);
            var restored = await restore();
            outcome = Describe(restored);
            return restored;
        });
        return outcome;
    }

    // Google's sign-in renewal: the keeper (age timer, early renewal, the checks after two hours, the refusal-driven refresh) with the server as its host.
    // It is started once, by the restore, so a server without --restore opens no browser and makes no request.
    private ServerSignInHost StartSignInKeeper(CancellationToken stop)
    {
        // A renewal's end tells every client to re-read Google Messages, in the host's own event order (ServerSignInHost.ResumeSync).
        var signIn = new ServerSignInHost(sta, Services.GetRequiredService<HostClientState>(), ConnectGoogleAsync, stop, log,
            () => Host.EventLog.Append(new RealtimeEvent(RealtimeKind.ResyncRequired, Scope: GoogleScope)));
        Services.GetRequiredService<GoogleSignInKeeper>().Start(signIn);
        log("server sign-in keeper started");
        return signIn;
    }

    // What the window's Connect command does for Google Messages: find the saved pairing and list its accounts.
    private async Task ConnectGoogleAsync(CancellationToken stop)
    {
        var backend = Services.GetRequiredService<IBeeperBackend>();
        await backend.DiscoverAsync(stop);
        await backend.GetAccountsAsync(stop);
    }

    private static string Describe(AccountRestore restored) =>
        restored.Skipped ? "skipped" : restored.Refusal is not null ? "refused" : restored.Conversations is { } count ? $"connected chats={count}" : "failed";

    private async Task Restored(string provider, CancellationToken stop, Func<Task<string>> restore)
    {
        // Checked before the step starts anything, since each of them is the first thing that touches its provider.
        if (stop.IsCancellationRequested) { log($"server restore provider={provider} outcome=stopped"); return; }
        try { log($"server restore provider={provider} outcome={await restore()}"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { log($"server restore provider={provider} outcome=stopped"); }
        catch (Exception failure) { log($"server restore provider={provider} outcome=error type={failure.GetType().Name}"); }
    }

    public void Dispose()
    {
        Host.Dispose();
        Services.Dispose();
    }
}
