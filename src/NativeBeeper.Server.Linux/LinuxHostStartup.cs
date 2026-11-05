using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Server;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// The Linux host's composition: the Windows server's (<c>src/NativeBeeper.Server/HostStartup.cs</c>) without Windows. The protector is the key
/// file's, the stores sit at the data root, and only Messenger and Instagram run (<see cref="UnavailableProviders"/>). Building it connects nothing; only
/// <see cref="RestoreAsync"/> does, and only when the host was started to restore.
/// </summary>
internal sealed partial class LinuxHostStartup : IDisposable
{
    private readonly GatedRealtimeFeed feed;
    private readonly RestoreOnce restores;
    private readonly Action<string> log;
    // Google Messages, when the host was started to run it (LinuxHostStartup.Google.cs); null runs Messenger alone.
    private readonly LinuxGoogle? google;

    private LinuxHostStartup(ServiceProvider services, ProtocolHost host, GatedRealtimeFeed feed, RestoreOnce restores, Action<string> log, LinuxGoogle? google = null)
    {
        Services = services; Host = host; this.feed = feed; this.restores = restores; this.log = log; this.google = google;
    }

    public ServiceProvider Services { get; }
    public ProtocolHost Host { get; }
    /// <summary>The server's own backup, whose schedule the run starts beside the history walk.</summary>
    public NativeBeeper.Host.Backup.ServerBackupService Backup { get; private set; } = null!;

    /// <param name="root">The data root; every store of the host's own is under it.</param>
    /// <param name="keys">The host's sealing keys (<see cref="HostKeys.Prepare"/>).</param>
    /// <param name="restore">Whether <see cref="RestoreAsync"/> will run, so a client's restore of Messenger waits for the host's.</param>
    /// <param name="withGoogle">Google Messages and its renewal browser (<see cref="LinuxGoogle.Prepare"/>), or null to refuse it.</param>
    /// <param name="backups">Where the server's daily backup goes (<see cref="LinuxServerOptions.Backups"/>), or null for none.</param>
    public static LinuxHostStartup Compose(string root, HostKeys keys, Action<string> log, bool restore = false, LinuxGoogle? withGoogle = null, string? backups = null)
    {
        var step = new StepClock(log);
        var collection = new ServiceCollection();
        collection.AddSingleton<IStateProtector>(new FileKeyStateProtector(keys.StateKey));
        // Asked for only by Google Voice and the Google renewal. The re-encoder is the real one (4.4), ready for when Voice runs here; the browser is
        // the real one (4.3) only when Google Messages runs (MESSAGES_GOOGLE=1, LinuxGoogle), and otherwise says it cannot.
        collection.AddSingleton<IPhotoReencoder, SkiaPhotoReencoder>();
        if (withGoogle is null) collection.AddSingleton<IGoogleBrowser, UnavailableGoogleBrowser>();
        else collection.AddSingleton<IGoogleBrowser>(withGoogle.KeeperBrowser);
        // No video frame extractor: a Messenger video that came with no still keeps its play button, as the backends allow.
        collection.AddSingleton<IPrivateStateStore>(sp => new NativeBeeper.Storage.SqlitePrivateStateStore(Path.Combine(root, "state.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<PrivateState>();
        collection.AddSingleton(sp => new NativeBeeper.Storage.MediaDiskCache(Path.Combine(root, "media.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new NativeBeeper.Storage.MessageArchive(Path.Combine(root, "archive.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<ISavedPhotoStore>(sp => new NativeBeeper.Storage.SavedPhotoStore(Path.Combine(root, "saved-photos.db"), sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new SavedPhotos(sp.GetRequiredService<ISavedPhotoStore>(), () => sp.GetService<IMediaReader>(), AppLog.Write));
        collection.AddSingleton<ComposerActivity>();
        collection.AddBackends();
        // With no client connected it says nobody is looking, so a headless host never polls at the "present" pace.
        collection.AddSingleton(sp => new HostClientState(Path.Combine(root, "LastKnownClient-v1", "state.protected"), sp.GetRequiredService<IStateProtector>(), AppLog.Write));
        collection.AddSingleton<IClientBusy>(sp => sp.GetRequiredService<HostClientState>());
        collection.AddSingleton<IAppPresence>(sp => sp.GetRequiredService<HostClientState>());
        collection.Replace(ServiceDescriptor.Singleton(sp => new MessagingRouter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<MessengerBackend>(),
            sp.GetRequiredService<GoogleVoiceBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<HostClientState>().Reading)));
        collection.Replace(ServiceDescriptor.Singleton(sp => new TypingRouter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<MessengerBackend>(),
            sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<ComposerActivity>(), sp.GetRequiredService<HostClientState>().Reading)));
        collection.AddSingleton<IChatReader>(sp => sp.GetRequiredService<MessagingRouter>());
        step.Took("registrations");
        var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        step.Took("container");

        var protector = services.GetRequiredService<IStateProtector>();
        var google = services.GetRequiredService<GoogleMessagesBackend>();
        var messenger = services.GetRequiredService<MessengerBackend>();
        var instagram = services.GetRequiredService<InstagramBackend>();
        var voice = services.GetRequiredService<GoogleVoiceAccounts>();
        var voicePort = new GoogleVoiceAccountsPort(voice);
        var keeper = services.GetRequiredService<GoogleSignInKeeper>();
        step.Took("backends");
        // Messenger's and Instagram's feeds alone: Google's would connect a saved pairing the moment anyone subscribed. Instagram's polls only while its
        // helper is connected, at the "nobody looking" pace while no client is (HostClientState), as on Windows. With Google Messages, its feed joins
        // them (LinuxHostStartup.Google.cs).
        var feed = withGoogle is null
            ? new GatedRealtimeFeed(new CompositeRealtimeFeed(() => false, (ProviderScopes.Messenger, messenger), (ProviderScopes.Instagram, instagram)))
            : GoogleFeed(google, messenger, instagram, services.GetRequiredService<GoogleVoiceBackend>(), withGoogle.VoiceRuns);
        // Both slots exist whenever the host restores, so a client's restore of either waits for (or is answered skipped by) the paced one, never logs in
        // beside it or ahead of the ledger; and Google's too when it runs.
        var restores = !restore ? RestoreOnce.None : withGoogle is null ? new RestoreOnce([RestoreOnce.Messenger, RestoreOnce.Instagram]) : GoogleRestores(withGoogle.VoiceRuns);
        // Google's backend is left out of the real restarter, which then answers its scope not_started; the wrapper answers it first.
        IGoogleMessagesAccount? googleAccount = withGoogle is null ? null : new GoogleMessagesAccountPort(google, services.GetRequiredService<GoogleMediaBackend>());
        var unavailable = withGoogle is null ? UnavailableProviders.Ports(new HostHelperRestarter(null, keeper, messenger, instagram, voice, log))
            : GooglePorts(withGoogle, googleAccount!, google, keeper, messenger, instagram, voice, voicePort, log);
        // One object for the browser sign-in of Google Messages and Google Voice, so that only one runs at a time (LinuxHostStartup.Google.cs).
        var remoteSignIn = RemoteSignIn(withGoogle, googleAccount, voicePort, log);
        step.Took("feed");
        // The accounts that moved here: its records are read, and an activation a stop left is finished, before anything can restore.
        var runner = new LinuxAccountRunner(services);
        UploadStore? movedUploads = null;
        var move = new AccountMoveHost(new AccountMovePaths(root, keys.Folder), protector, runner, new MessengerOfflineHistoryStarter(), () => movedUploads ?? throw new InvalidOperationException("The host is not composed yet."), log: log);
        move.RecoverAsync().GetAwaiter().GetResult();
        // A recovery that finished an activation put a moved account's limits beside its session after the backend had read the file: read again before anything restores it.
        instagram.ReloadGuard();
        // Made on first use (stage 6). The host's snoozes are rows of it (slice 3), and its ChatSnoozes-v1 files are taken in once, at that first use.
        var shared = new SharedStateStore(Path.Combine(root, "Server", SharedStateStore.FileName), protector, log);
        // The owner's administration (SWITCHBOARD.md 4.6, protocol 20). What it asks of the host itself (Google's connection, the login lock) exists only once the host does.
        ProtocolHost? built = null;
        LinuxHostStartup? made = null;
        var version = typeof(LinuxHostStartup).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var admin = new LinuxServerAdmin(new LinuxServerAdmin.Parts(version, ProcessStarted(), ServerImage.FromEnvironment(Environment.GetEnvironmentVariable),
            // Removal already owns the same lifecycle lock as the gated ports and a hold; use their inner ports to avoid taking that lock twice.
            () => withGoogle?.Sandbox?.Word, new MessengerAccount(messenger), () => MessengerKeyAsync(protector),
            unavailable.Instagram ?? new InstagramAccountLifecycle(instagram), unavailable.Voice ?? voicePort, withGoogle?.VoiceRuns == true, withGoogle is not null,
            () => GoogleSessionProfile.HasSavedSession,
            () => built?.StatePublisher.Latest?.State.Scopes.Any(scope => scope.Scope == ProviderScopes.Google && scope.Connected) == true,
            move, move, provider => (made ?? throw new InvalidOperationException("The host is not composed yet.")).LoginGate(provider), keys.HelperFolder, TimeProvider.System, log,
            Path.Combine(Path.GetDirectoryName(MessengerBackend.SessionPath)!, "encrypted.on"), () => google.OwnNumber, () => messenger.OwnName));
        // Everything Messenger's helper keeps in the signed-in account's messages.db, searched read-only for search.query's kept source and warmed by
        // search.warmKept, as the Windows app searches its own (MessengerKeptHistory). The store is in the keys folder, its cells sealed with the helper's key.
        var kept = new MessengerKeptHistory(() => messenger.SelfId is { } self ? MessengerKeptAccount.In(keys.HelperFolder, self) : null,
            messenger.KnownChat, log, new HelperKeyFileCells(keys.HelperKey));
        // Signing out lets go of the index at once, rather than at the next search.
        messenger.ChatsChanged += (_, _) => { if (messenger.SelfId is null) kept.Warm(); };
        // The server's own backup (SWITCHBOARD.md stage 10): its data and identity, never a sign-in, once a day into the folder its host maps.
        var backup = new NativeBeeper.Host.Backup.ServerBackupService(new NativeBeeper.Host.Backup.ServerBackupPaths(root, keys.Folder), protector, backups,
            new NativeBeeper.Host.Backup.BackendBackupMessenger(messenger), version, log);
        var host = ProtocolHost.Create(new ProtocolHostOptions(services.GetRequiredService<MessagingRouter>(), services.GetRequiredService<DirectoryRouter>(),
            services.GetRequiredService<MediaRouter>(), services.GetRequiredService<TypingRouter>(), google, services.GetRequiredService<GoogleMediaBackend>(),
            messenger, instagram, voice, feed, SharedChatSnoozes.Over(shared, new SealedChatSnoozeStore(Path.Combine(root, SealedChatSnoozeStore.FolderName), protector)),
            version, log, services.GetRequiredService<HostDiagnosticsSource>(),
            keeper, services.GetRequiredKeyedService<IAccessTokenStore>(BackendServices.HostTokensKey),
            JournalStore: new ProtectedFileJournalStore(Path.Combine(root, "Server", "journal.protected"), protector),
            IncomingCards: services.GetRequiredService<IncomingCardRequester>(),
            ClientState: services.GetRequiredService<HostClientState>(),
            UploadFolder: Path.Combine(root, "Server", "uploads"), Restores: restores, Unavailable: unavailable,
            // The incoming unarchive is the server's, once for all its clients (stage 6 slice 9), with its watermarks in the host's own state.db.
            IncomingUnarchive: new HostUnarchive(services.GetRequiredKeyedService<PrivateState>(BackendServices.HostTokensKey)),
            // The originals of the clients' saved photos are the server's to keep (stage 6 slice 8): its own store, read through its own media router.
            SavedPhotos: services.GetRequiredService<SavedPhotos>(), SharedState: shared,
            // The scheduled sends are the server's too (stage 6 slice 11): kept as rows of the shared state, fired by this host with every client closed.
            Scheduled: new HostScheduled(), KeptHistory: kept,
            // Google Messages signed in in the server's own browser, shown on the server's page (SWITCHBOARD.md 5.4): with MESSAGES_GOOGLE=1 and a headed browser only.
            RemoteSignIn: remoteSignIn.Messages, VoiceRemoteSignIn: remoteSignIn.Voice,
            // How old the server's own Chrome is, asked each time the state is built (SWITCHBOARD.md 5.4, protocol 14), whether or not the sign-in in it is offered.
            BrowserOutOfDate: withGoogle?.Chrome is { } chromeAge ? () => chromeAge.Build.OutOfDate(DateTimeOffset.UtcNow) : null,
            // Messenger and Instagram move with the PC (SWITCHBOARD.md stage 6 slice 13): staged, activated together, held after a move back, and gated until then.
            AccountMove: move, AccountGate: move,
            // The host's own logs, by name, for its owner's copies (protocol 18): never a path from a client, and nothing but the fixed-format logs.
            Logs: new HostLogFiles(LinuxHostLogs.Paths(root, keys.HelperFolder)),
            // Its status, its accounts and their removal (protocol 20): the page's, and the reads for any device.
            Admin: admin,
            // The conversation list's revisions (protocol 23) in its own protected state.db, so a phone's sync after the nightly restart is a delta,
            // not the whole list again.
            ConversationSyncStore: services.GetRequiredService<IPrivateStateStore>(),
            // Every conversation kept whole (SWITCHBOARD.md stage 8, protocol 24): history.db beside the other stores, sealed under the state key like them.
            WholeHistory: new NativeBeeper.Storage.HistoryDatabase(Path.Combine(root, "history.db"), protector),
            // Its status to every device, Back up now and its password to the owner (protocol 26).
            Backup: backup,
            // Messenger and Instagram signed in in the server's own Chrome too (protocol 27, SWITCHBOARD.md 10.5), wherever Google's sign-in in it is offered.
            MetaRemoteSignIn: remoteSignIn.Meta));
        built = host;
        // Each change in how the backup stands is a signal in the host's sequence, so a client of 26 reads the status again.
        backup.Changed += status => host.Services.Events?.AppendSignal(new AccountSignal(AccountSignals.Backup, !status.Stale));
        // A PC backup's choices wait for the key the shared state is filed under, then go in as a move's would.
        backup.CurrentKey = shared.Space.CurrentKeyAsync;
        backup.ImportBundle = async (bundle, ct) =>
        {
            var imported = await shared.ImportBundleAsync(bundle, resume: true, ct).ConfigureAwait(false);
            return (imported.Status, imported.Added, imported.Skipped, imported.Refused);
        };
        // An account removed on the server's own page takes what the host kept of it, as a client's sign-out does (the whole history's copies).
        admin.Ended = host.Services.AccountEnded;
        movedUploads = host.Services.Uploads;
        step.Took("host");
        // The Google sign-in keeper is never started without Google Messages: it renews through a browser this host then does not have. With it, the
        // restore starts it (LinuxHostStartup.Google.cs), as the Windows server's does.
        var startup = new LinuxHostStartup(services, host, feed, restores, log, withGoogle);
        startup.Backup = backup;
        made = startup;
        startup.AttachMove(move, runner);
        return startup;
    }

    /// <summary>
    /// The start's restore: Messenger's and Instagram's saved sessions, each once (<see cref="RestoreOnce"/>) and side by side as the Windows server
    /// restores them, and then the feed is let go. Instagram's is its own restore, unchanged: at most three tries, none after an answer only the user can
    /// change (<c>InstagramRestore</c>). Logged as words and counts only.
    /// </summary>
    public async Task RestoreAsync(CancellationToken stop)
    {
        if (google is not null) { await RestoreWithGoogleAsync(stop).ConfigureAwait(false); return; }
        try
        {
            if (stop.IsCancellationRequested)
            {
                foreach (var provider in Restoring) log($"server restore provider={provider} outcome=stopped");
                return;
            }
            var restoring = RestoreOthersAsync(stop);
            // Let go while the restore runs, as the Windows server does once the others have started: a client then sees the connection as it comes up.
            if (!stop.IsCancellationRequested) feed.Open();
            await restoring;
        }
        // However it ended, a client waiting on the host's restore is answered (skipped) rather than left waiting.
        finally { restores.End(); }
    }

    /// <summary>The providers this host restores, by the words its log names them with.</summary>
    public static readonly string[] Providers = [RestoreOnce.Messenger, RestoreOnce.Instagram];

    /// <summary>The providers this host's restore reaches: <see cref="Providers"/>, and Google Messages when it runs here.</summary>
    public IReadOnlyList<string> Restoring => google is null ? Providers : google.VoiceRuns ? [.. Providers, RestoreOnce.Google, VoiceProvider] : [.. Providers, RestoreOnce.Google];

    /// <summary>
    /// Whether the start's restore would log a Messenger or Instagram account in: one is saved here and not held by a move. The restore ledger counts only those
    /// (<see cref="RestoreLedger.StartAsync"/>); a start that restores only Google, or nothing, does not spend the hour a moved account's first login needs.
    /// </summary>
    public bool RestoresAccounts =>
        Services.GetRequiredService<IMessengerAccount>().State.Saved && Move?.Allows(RestoreOnce.Messenger) != false
        || Services.GetRequiredService<IInstagramAccount>().State.Saved && Move?.Allows(RestoreOnce.Instagram) != false;

    /// <summary>The host's restore will not run: a client waiting on it is answered skipped.</summary>
    public void EndRestores() => restores.End();

    // Messenger's and Instagram's saved sessions, side by side, each through the host's one slot for it. With or without Google Messages.
    private Task RestoreOthersAsync(CancellationToken stop)
    {
        var messenger = Services.GetRequiredService<IMessengerAccount>();
        var instagram = Services.GetRequiredService<IInstagramAccount>();
        Func<bool> stopped = () => stop.IsCancellationRequested;
        return Task.WhenAll(
            Restored(RestoreOnce.Messenger, stop, () => RestoreAccountAsync(RestoreOnce.Messenger,
                () => messenger.State is { Saved: true, Connected: false }, () => messenger.RestoreAsync(stopped))),
            Restored(RestoreOnce.Instagram, stop, () => RestoreAccountAsync(RestoreOnce.Instagram,
                () => instagram.State is { Saved: true, Connected: false }, () => instagram.RestoreAsync(stopped))));
    }

    // A provider with no saved session, or already connected, is not restored: the answer a client gets for it is "skipped", as the app's own restore says.
    private async Task<string> RestoreAccountAsync(string provider, Func<bool> restorable, Func<Task<AccountRestore>> restore)
    {
        var outcome = "not_saved";
        await restores.RunAsync(provider, async () =>
        {
            // The same lock as a moved account's first login: whichever comes second finds the account already connected, or held back.
            await LoginGate(provider).WaitAsync();
            try
            {
                // An account being moved here, or one that moved away, is not this restore's to log in.
                if (Move is { } move && !move.Allows(provider)) { outcome = "held"; return new AccountRestore(null, Skipped: true); }
                if (!restorable()) return new AccountRestore(null, Skipped: true);
                var restored = await restore();
                outcome = restored.Skipped ? "skipped" : restored.Refusal is not null ? "refused" : restored.Conversations is { } count ? $"connected chats={count}" : "failed";
                return restored;
            }
            finally { LoginGate(provider).Release(); }
        });
        return outcome;
    }

    private async Task Restored(string provider, CancellationToken stop, Func<Task<string>> restore)
    {
        try { log($"server restore provider={provider} outcome={await restore()}"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { log($"server restore provider={provider} outcome=stopped"); }
        catch (Exception failure) { log($"server restore provider={provider} outcome=error type={failure.GetType().Name}"); }
    }

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

    // The helper's key for the Messenger account saved here, from the saved session's Facebook id (unsealed, read and dropped): the account a sign-out would end. Not
    // from the conversation list in memory, which a hold's stop and a move back leave as another account's. Null when no session names one. Never logged.
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
        Host.Dispose();
        Services.Dispose();
        signInQueue?.Dispose();
    }
}
