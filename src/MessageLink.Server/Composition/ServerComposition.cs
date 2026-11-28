using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Backend;
using MessageLink.Core.Services;
using MessageLink.Server.Backup;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// What every server is built from (GLOSSARY.md, "Server composition"): its stores where <see cref="ServerLayout"/> puts them, the backends and the
/// routers over them, the protocol server and its backup, and each account's restore step. The app's <c>OnLaunched</c> without anything that draws: no
/// window, no notifications, no decorators (merges, labels, local preferences, the search catalogue and the conversation cache stay in the client).
/// A platform adds only what differs (<see cref="ServerPlatform"/>). Building it connects nothing: the providers start only
/// when the platform's restore runs the steps of <see cref="ComposedServer"/>.
/// </summary>
internal static class ServerComposition
{
    public static ComposedServer Compose(ServerLayout layout, ServerPlatform platform)
    {
        var log = platform.Log;
        var step = new StepClock(log);
        var collection = new ServiceCollection();
        // What only the platform can do.
        collection.AddSingleton(platform.Protector);
        collection.AddSingleton(platform.Photos);
        if (platform.VideoFrames is { } frames) collection.AddSingleton(frames);
        collection.AddSingleton(platform.GoogleBrowser);
        // The stores the backends keep their state in, at the same paths as the app's: one server per data root, so only one of them is open.
        collection.AddSingleton<IPrivateStateStore>(sp => new MessageLink.Storage.SqlitePrivateStateStore(layout.State, sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<PrivateState>();
        collection.AddSingleton(sp => new MessageLink.Storage.MediaDiskCache(layout.Media, sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new MessageLink.Storage.MessageArchive(layout.Archive, sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<ISavedPhotoStore>(sp => new MessageLink.Storage.SavedPhotoStore(layout.SavedPhotos, sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton(sp => new SavedPhotos(sp.GetRequiredService<ISavedPhotoStore>(), () => sp.GetService<IMediaReader>(), AppLog.Write));
        collection.AddSingleton<ComposerActivity>();
        // The providers and the routers over them.
        collection.AddBackends();
        // What the server knows of its clients (client.state): Instagram's poll, the Google keeper's busy check and the routers' private-reading net read it.
        // With no client connected it says nobody is looking, so a headless server never polls at the "present" pace.
        collection.AddSingleton(sp => new ServerClientState(layout.ClientState, sp.GetRequiredService<IStateProtector>(), AppLog.Write));
        collection.AddSingleton<IClientBusy>(sp => sp.GetRequiredService<ServerClientState>());
        collection.AddSingleton<IAppPresence>(sp => sp.GetRequiredService<ServerClientState>());
        collection.Replace(ServiceDescriptor.Singleton(sp => new MessagingRouter(sp.GetRequiredService<ConversationOwners>(), sp.GetRequiredService<ServerClientState>().Reading)));
        collection.Replace(ServiceDescriptor.Singleton(sp => new TypingRouter(sp.GetRequiredService<ConversationOwners>(), sp.GetRequiredService<ComposerActivity>(),
            sp.GetRequiredService<ServerClientState>().Reading)));
        // The avatar reader AddBackends registers reads chats through the port the window decorates; here nothing decorates it.
        collection.AddSingleton<IChatReader>(sp => sp.GetRequiredService<MessagingRouter>());
        step.Took("registrations");
        var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        step.Took("container");

        var protector = platform.Protector;
        var gates = new LoginGates();
        // What the administration asks of the server itself (Google's connection) exists only once the server does.
        ProtocolServer? built = null;
        var backends = new ServerBackends(services, platform, gates,
            () => built?.StatePublisher.Latest?.State.Scopes.Any(scope => scope.Scope == ProviderScopes.Google && scope.Connected) == true);
        var google = backends.Google;
        var messenger = backends.Messenger;
        var instagram = backends.Instagram;
        var voice = backends.Voice;
        var hermes = services.GetRequiredService<MessageLink.App.Services.Hermes.HermesBackend>();
        step.Took("backends");
        var (feed, scopes) = Feed(platform.Feeds, google, messenger, instagram, services.GetRequiredService<GoogleVoiceBackend>(), hermes);
        backends.Unavailable = platform.Unavailable?.Invoke(backends);
        var remoteSignIn = platform.RemoteSignIn?.Invoke(backends) ?? new RemoteSignIns(null, null, null);
        step.Took("feed");
        // Made on first use, never by the app's in-process server. The server's snoozes are rows of it, and its ChatSnoozes-v1 files (the
        // in-process app's too, on a root they share) are taken in once, at that first use, and left as they are.
        var shared = new SharedStateStore(layout.Shared, protector, log);
        // A space nobody has chosen private reading in reads it off, as the guide says, and not private for every service (no row is private to a client).
        shared.GiveReadingDefaults();
        // Its status, its accounts and their removal, for its page and its owner (protocol 20).
        var admin = platform.Admin(backends);
        var kept = platform.KeptHistory?.Invoke(backends);
        // The server's own backup: its data and identity, never a sign-in, into the folder chosen for it.
        var backup = new ServerBackupService(layout.BackupPaths, protector, platform.BackupFolder(), new BackendBackupMessenger(messenger, backends.Accounts), platform.Version, log);
        var options = new ProtocolServerOptions(services.GetRequiredService<MessagingRouter>(), services.GetRequiredService<DirectoryRouter>(),
            services.GetRequiredService<MediaRouter>(), services.GetRequiredService<TypingRouter>(), google, services.GetRequiredService<GoogleMediaBackend>(),
            messenger, instagram, voice, feed, SharedChatSnoozes.Over(shared, platform.Snoozes),
            platform.Version, log, services.GetRequiredService<ServerDiagnosticsSource>(),
            backends.Keeper, services.GetRequiredKeyedService<IAccessTokenStore>(BackendServices.ServerTokensKey),
            JournalStore: new ProtectedFileJournalStore(layout.Journal, protector),
            KeptHistory: kept,
            IncomingCards: services.GetRequiredService<IncomingCardRequester>(),
            ClientState: services.GetRequiredService<ServerClientState>(),
            UploadFolder: layout.Uploads, Restores: platform.Restores, Unavailable: backends.Unavailable,
            // The incoming unarchive is the server's, once for all its clients. Its watermarks are the server's own state.db rows, the
            // same ones the in-process app keeps on this root: a run of either after the other then never asks again for an arrival the other asked for.
            IncomingUnarchive: new ServerUnarchive(services.GetRequiredKeyedService<PrivateState>(BackendServices.ServerTokensKey)),
            // The originals of the clients' saved photos are the server's to keep: its own store, read through its own media router.
            SavedPhotos: services.GetRequiredService<SavedPhotos>(), SharedState: shared,
            // The scheduled sends are the server's too: kept as rows of the shared state, fired by this server with every client closed.
            Scheduled: new ServerScheduled(),
            // Each provider's lifecycle lock in front of the ports the page and the clients sign in through, shared with the start's restore and a removal.
            AccountLocks: gates.For,
            RemoteSignIn: remoteSignIn.Messages, VoiceRemoteSignIn: remoteSignIn.Voice, MetaRemoteSignIn: remoteSignIn.Meta,
            BrowserOutOfDate: platform.BrowserOutOfDate,
            // The server's own logs, by name, for its owner's copies (protocol 18): never a path from a client, and nothing but the fixed-format logs.
            Logs: platform.Logs, Admin: admin,
            // The conversation list's revisions (protocol 23) in its own protected state.db, so a phone's sync after a restart is a delta, not the whole list again.
            ConversationSyncStore: services.GetRequiredService<IPrivateStateStore>(),
            // Every conversation kept whole (protocol 24), sealed under the state key like the other stores; the app's in-process server keeps none.
            WholeHistory: new MessageLink.Storage.HistoryDatabase(layout.History, protector),
            // Its status to every device, Back up now and its password to the owner (protocol 26).
            Backup: backup, Accounts: backends.Accounts, HelperCells: platform.HelperCells, Hermes: hermes);
        var host = ProtocolServer.Create(options);
        built = host;
        // Each change in how the backup stands is a signal in the server's sequence, so a client of 26 reads the status again.
        backup.Changed += status => host.Services.Events?.AppendSignal(new AccountSignal(AccountSignals.Backup, !status.Stale));
        // A PC backup's choices wait for the key the shared state is filed under, then go in once (SharedStateStore.ImportBundleAsync).
        backup.CurrentKey = shared.Space.CurrentKeyAsync;
        backup.ImportBundle = async (bundle, ct) =>
        {
            var imported = await shared.ImportBundleAsync(bundle, resume: true, ct).ConfigureAwait(false);
            return (imported.Status, imported.Added, imported.Skipped, imported.Refused);
        };
        // An account removed on the server's own page takes what the server kept of it, as a client's sign-out does (the whole history's copies).
        admin.Ended = host.Services.AccountEnded;
        step.Took("host");
        return new ComposedServer(layout, services, host, feed, scopes, backup, shared, admin, options, platform.Restores, gates, log);
    }

    // Messenger's, Instagram's and Hermes' feeds always. Google's, when it runs, is gated on a saved pairing, since let go it connects that pairing by itself;
    // held by its own gate within, so a skipped or held restore can let the others go (GatedRealtimeFeed.OpenPassive). Google Voice's joins them where it
    // runs: left out on Linux, from moving day (2026-10-12) to 2026-10-29 no Voice arrival reached a client, a push or the whole history as it happened.
    private static (GatedRealtimeFeed Feed, IReadOnlyList<string> Scopes) Feed(ServerFeeds feeds, GoogleMessagesBackend google, MessengerBackend messenger,
        InstagramBackend instagram, GoogleVoiceBackend voice, MessageLink.App.Services.Hermes.HermesBackend hermes)
    {
        var connecting = feeds.Google ? new GatedRealtimeFeed(google, runs: () => MessagingRouter.GoogleMessagesExpected) : null;
        var list = new List<(string Scope, IRealtimeFeed Feed)>();
        if (connecting is not null) list.Add((ProviderScopes.Google, connecting));
        list.Add((ProviderScopes.Messenger, messenger));
        if (feeds.Voice) list.Add((ProviderScopes.VoicePrefix, voice));
        list.Add((ProviderScopes.Instagram, instagram));
        list.Add((ProviderScopes.Hermes, hermes));
        // Google's gate answers whether its feed runs; the composite runs every feed it is given.
        return (new GatedRealtimeFeed(new CompositeRealtimeFeed(() => true, [.. list]), connecting), [.. list.Select(item => item.Scope)]);
    }
}

/// <summary>
/// A composed server: its container, host, feed and backup, and the steps a platform's restore is made of. Each step goes through the host's one slot for
/// its provider (<see cref="RestoreOnce"/>), so the restore a client asks for is answered with the server's result or waits for it, and never logs in a second
/// time. Outcomes are logged as words and counts, never a name.
/// </summary>
internal sealed class ComposedServer(ServerLayout layout, ServiceProvider services, ProtocolServer host, GatedRealtimeFeed feed, IReadOnlyList<string> feedScopes,
    ServerBackupService backup, SharedStateStore shared, IComposedServerAdmin admin, ProtocolServerOptions options, RestoreOnce restores, LoginGates gates, Action<string> log) : IDisposable
{
    public ServerLayout Layout => layout;
    public ServiceProvider Services => services;
    public ProtocolServer Host => host;
    /// <summary>The server's own backup, whose schedule the run starts beside the host.</summary>
    public ServerBackupService Backup => backup;
    public SharedStateStore Shared => shared;
    public IComposedServerAdmin Admin => admin;
    /// <summary>What the server was made from, for the harness.</summary>
    internal ProtocolServerOptions Options => options;
    /// <summary>The providers' feeds, by scope, in the order they run.</summary>
    public IReadOnlyList<string> FeedScopes => feedScopes;
    public RestoreOnce Restores => restores;

    /// <summary>The provider's lifecycle lock (<see cref="LoginGates"/>).</summary>
    public SemaphoreSlim LoginGate(string provider) => gates.For(provider);

    /// <summary>Lets every feed go, Google's too: done by the restore once the others have started.</summary>
    public void OpenFeed() => feed.Open();

    /// <summary>The start's restore is skipped or held: every feed but Google's own goes now, so Hermes and an account connected meanwhile are heard.</summary>
    public void OpenPassiveFeed() => feed.OpenPassive();

    /// <summary>The server's restore will not run, or has ended: a client waiting on it is answered skipped rather than left waiting.</summary>
    public void EndRestores() => restores.End();

    /// <summary>
    /// Whether the start's restore would log a Messenger or Instagram account in: one is saved here. The restore ledger counts only those
    /// (<see cref="RestoreLedger.StartAsync"/>); a start that restores only Google, or nothing, does not spend the hour.
    /// </summary>
    public bool RestoresAccounts => services.GetRequiredService<ProviderAccounts>().Added.Any(a => a.Messenger?.SessionSaved == true || a.Instagram?.HasSavedSession == true)
        || services.GetRequiredService<IMessengerAccount>().State.Saved || services.GetRequiredService<IInstagramAccount>().State.Saved;

    /// <summary>One provider's step of the restore, logged by the word <paramref name="provider"/>: nothing is started once the stop has begun.</summary>
    public async Task RestoreStepAsync(string provider, CancellationToken stop, Func<Task<string>> restore)
    {
        // Checked before the step starts anything, since each of them is the first thing that touches its provider.
        if (stop.IsCancellationRequested) { log($"server restore provider={provider} outcome=stopped"); return; }
        try { log($"server restore provider={provider} outcome={await restore().ConfigureAwait(false)}"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { log($"server restore provider={provider} outcome=stopped"); }
        catch (Exception failure) { log($"server restore provider={provider} outcome=error type={failure.GetType().Name}"); }
    }

    /// <summary>Messenger's and Instagram's saved sessions, side by side, each under its lifecycle lock. Instagram's is its own restore, unchanged: at most three tries, none after an answer only the user can change.</summary>
    public Task RestoreMessengerAndInstagramAsync(CancellationToken stop)
    {
        var messenger = services.GetRequiredService<IMessengerAccount>();
        var instagram = services.GetRequiredService<IInstagramAccount>();
        Func<bool> stopped = () => stop.IsCancellationRequested;
        return Task.WhenAll(
            RestoreStepAsync(RestoreOnce.Messenger, stop, () => RestoreAccountAsync(RestoreOnce.Messenger,
                () => messenger.State is { Saved: true, Connected: false }, () => messenger.RestoreAsync(stopped), stop)),
            RestoreStepAsync(RestoreOnce.Instagram, stop, () => RestoreAccountAsync(RestoreOnce.Instagram,
                () => instagram.State is { Saved: true, Connected: false }, () => instagram.RestoreAsync(stopped), stop)));
    }

    /// <summary>The word Google Voice's restore is logged with.</summary>
    public const string VoiceProvider = "gvoice";

    /// <summary>Google Voice's saved accounts, each through its own helper, a stale sign-in first renewed from its own browser profile.</summary>
    public Task RestoreVoiceAsync(CancellationToken stop) =>
        RestoreStepAsync(VoiceProvider, stop, async () => $"failed={(await restores.RunAsync(RestoreOnce.Voice, () => services.GetRequiredService<GoogleVoiceAccounts>().RestoreAsync(stop)).ConfigureAwait(false)).Count}");

    /// <summary>
    /// Google Messages' saved pairing, found and its accounts listed, as the window's Connect command does. <paramref name="beginConnect"/> is held for the
    /// connect, so a sign-in renewal that ends meanwhile does not connect again beside it.
    /// </summary>
    public Task RestoreGoogleAsync(CancellationToken stop, Func<IDisposable?> beginConnect) =>
        RestoreStepAsync(RestoreOnce.Google, stop, () => restores.RunAsync(RestoreOnce.Google, async () =>
        {
            if (!MessagingRouter.GoogleMessagesExpected) return "not_saved";
            using var connecting = beginConnect();
            var backend = services.GetRequiredService<IChatBackend>();
            await backend.DiscoverAsync(stop).ConfigureAwait(false);
            return $"accounts={(await backend.GetAccountsAsync(stop).ConfigureAwait(false)).Count}";
        }));

    // A provider with no saved session, or already connected, is not restored: the answer a client gets for it is "skipped", as the app's own restore says.
    private async Task<string> RestoreAccountAsync(string provider, Func<bool> restorable, Func<Task<AccountRestore>> restore, CancellationToken stop)
    {
        var outcome = "not_saved";
        await restores.RunAsync(provider, async () =>
        {
            // The same lock as a removal on the server's page: whichever comes second finds the account already connected, or gone. A stop while it waits,
            // or by the time it has it, ends the step there (its slot answers skipped): a removal or restart holding the lock never holds up the stop.
            var gate = gates.For(provider);
            await gate.WaitAsync(stop).ConfigureAwait(false);
            try
            {
                stop.ThrowIfCancellationRequested();
                if (!restorable()) return new AccountRestore(null, Skipped: true);
                var restored = await restore().ConfigureAwait(false);
                outcome = Describe(restored);
                return restored;
            }
            finally { gate.Release(); }
        }).ConfigureAwait(false);
        return outcome;
    }

    private static string Describe(AccountRestore restored) =>
        restored.Skipped ? "skipped" : restored.Refusal is not null ? "refused" : restored.Conversations is { } count ? $"connected chats={count}" : "failed";

    public void Dispose()
    {
        host.Dispose();
        services.Dispose();
    }
}
