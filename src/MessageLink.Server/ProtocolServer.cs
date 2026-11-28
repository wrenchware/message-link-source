using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// What a server is given: the backends it serves, and the few things only its platform can supply. What is Windows-only
/// (the state protector, the photo re-encoder) is already inside the backends as a port; <see cref="Snoozes"/> is the one store
/// the server serves directly, and how it is protected is the platform's business.
/// </summary>
/// <param name="ProviderFeed">Every provider's events as one feed, which the server runs once into its event log.</param>
/// <param name="Snoozes">The store behind <c>snoozes.*</c>.</param>
/// <param name="Version">Offered in the handshake; a client refuses a server that is not its own version.</param>
/// <param name="Log">The server's log; every line any part of the server writes goes through it.</param>
/// <param name="Diagnostics">The server's helpers and cache sizes, served as <c>host.diagnostics</c>.</param>
/// <param name="ServerTokens">The store the backends read the Google profile through (<c>BackendServices.ServerTokensKey</c>); with it the server puts the identities (Google paired, the local key, the saved-message scopes) in its state.</param>
/// <param name="JournalStore">Where the operation journal keeps itself across a restart. Null (the app's in-process server, which dies with its client) keeps it in memory only.</param>
/// <param name="KeptHistory">The kept history behind <c>search.query</c>'s kept source and <c>search.warmKept</c>; null where the server keeps none.</param>
/// <param name="IncomingCards">What <c>media.requestCard</c> asks (it needs the Google archive, which the composition root holds). Null answers not supported.</param>
/// <param name="UploadFolder">Where files on their way to a send spill, encrypted under a key that lives only in this run (<see cref="UploadStore"/>). Null keeps them in memory, as the app's in-process server does.</param>
/// <param name="Restores">The server's own launch restore, when it has one (a server started with <c>--restore</c>): a client's restore of a provider it covers answers the server's. Null for a server that restores nothing itself.</param>
/// <param name="ClientState">What the host knows of its clients (<c>client.state</c>); the same object the backends read presence, busy, private reading and link previews through. Without one the host serves no <c>client.state</c>.</param>
/// <param name="Unavailable">What the server serves in place of the providers it does not run. Null runs all four.</param>
/// <param name="SharedState">The shared state behind <c>state.*</c>: a server's <c>Server\shared.db</c>, filed under the key pinned beside it in
/// <c>Server\space.protected</c>, which the server then serves as its <c>LocalKey</c>. Null (the app's in-process server, whose window keeps its own state) serves
/// none, makes neither file, and serves the live key.</param>
/// <param name="IncomingUnarchive">Given (the two servers), the server returns an archived conversation to the inbox when a message arrives in it, once for all its
/// clients, and they do not (<see cref="ServerIncomingUnarchive"/>). Null (the app's in-process server, whose window does it) runs none.</param>
/// <param name="Scheduled">Given (the two servers), with a shared state and an identity, the server keeps its clients' scheduled sends and fires them itself, with every
/// client closed, and serves <c>scheduled.*</c> (owner D6, <see cref="ServerScheduledSends"/>). Null (the app's in-process host and the loopback, whose
/// window keeps and fires its own schedule) fires none.</param>
/// <param name="RemoteSignIn">Given (the Linux server run with Google Messages and a headed browser), the host signs Google Messages in in a browser of its own, shown on the server's page.
/// Null offers none.</param>
/// <param name="BrowserOutOfDate">Given (the Linux server run with its own Chrome), asked every time the state is built whether that Chrome has gone unrenewed past its age (protocol 14), whether or not the sign-in in it
/// is offered. Null, and the sign-in port's own answer, say nothing.</param>
/// <param name="VoiceRemoteSignIn">Given (the Linux server run with its own Chrome), the host signs Google Voice in in that Chrome on a profile of the account's own, shown on the server's page (<c>accounts.voice.remoteSignIn</c>, protocol 15), and says so in
/// its state. Null serves no such method and says nothing.</param>
/// <param name="SavedPhotos">Given with a shared state (the two servers), the host keeps the originals of its clients' saved messages' photos itself and serves them to
/// whichever device opens one (<see cref="ServerSavedPhotos"/>). Null (the app's in-process server, whose window keeps its own) keeps none.</param>
/// <param name="AccountLocks">Given (the Linux server), each provider's lifecycle lock (<see cref="RestoreOnce.Messenger"/>, <see cref="RestoreOnce.Instagram"/>), the one its start's restore and a removal on the page take: the Messenger and Instagram ports and their helper restarts are served behind it (LockedAccounts). Null serves them as they are.</param>
/// <param name="Logs">Given (the Linux server), the server gives its own logs, by a name from a fixed list, to any paired device (<c>host.logs</c>, protocol 18). Null serves none.</param>
/// <param name="Admin">Given (the Linux server), the server serves its status, its accounts and their removal (<c>host.status</c>, <c>host.accounts</c>, <c>host.removeAccount</c>, protocol 20). Null serves none.</param>
/// <param name="WholeHistory">Given (the two servers), the server keeps every conversation whole in this store (<see cref="ConversationHistory"/>), answers its
/// clients' history reads from it and serves <c>history.*</c> (protocol 24). Null (the app's in-process host and the loopback) keeps none and reads the services as before.</param>
/// <param name="Backup">Given (the Linux server), the server serves its own backup (<c>server.backup.*</c>, protocol 26). Null serves none.</param>
/// <param name="MetaRemoteSignIn">Given (the Linux server run with its own Chrome), the host signs Messenger and Instagram in in that Chrome on a throwaway profile, shown on the server's page
/// (<c>accounts.remoteSignIn</c>, protocol 27), and says which in its state. Null serves neither and says nothing.</param>
internal sealed record ProtocolServerOptions(
    MessagingRouter Messaging, DirectoryRouter Directory, MediaRouter Media, TypingRouter Typing,
    GoogleMessagesBackend Google, GoogleMediaBackend GoogleMedia, MessengerBackend Messenger, InstagramBackend Instagram, GoogleVoiceAccounts VoiceAccounts,
    IRealtimeFeed ProviderFeed, IChatSnoozes Snoozes, string Version, Action<string> Log, IServerDiagnostics Diagnostics, GoogleSignInKeeper Keeper,
    IAccessTokenStore? ServerTokens = null, IOperationJournalStore? JournalStore = null, IKeptHistorySearch? KeptHistory = null, IIncomingCards? IncomingCards = null,
    ServerClientState? ClientState = null, string? UploadFolder = null, RestoreOnce? Restores = null, ServerUnavailablePorts? Unavailable = null,
    SharedStateStore? SharedState = null, ServerUnarchive? IncomingUnarchive = null, SavedPhotos? SavedPhotos = null, ServerScheduled? Scheduled = null,
    IRemoteSignInHost? RemoteSignIn = null, Func<bool>? BrowserOutOfDate = null, IRemoteSignInHost? VoiceRemoteSignIn = null,
    Func<string, SemaphoreSlim>? AccountLocks = null, IServerLogs? Logs = null, IServerAdmin? Admin = null, IPrivateStateStore? ConversationSyncStore = null,
    MessageLink.Storage.HistoryDatabase? WholeHistory = null, IServerBackup? Backup = null, IRemoteCookieSignInHost? MetaRemoteSignIn = null, ProviderAccounts? Accounts = null, IHelperCellUnprotector? HelperCells = null,
    MessageLink.App.Services.Hermes.HermesBackend? Hermes = null);

/// <summary>
/// The ports a server serves in place of the providers it does not run (the Linux server: Google Messages, Google Voice and Instagram).
/// The server's state, the account routes, the Google connect (<c>backend.discover</c>, <c>accounts.list</c>) and helper restarts use these
/// instead of the backends. Each one left null is the backend's own. <paramref name="GoogleRuns"/> marks a <paramref name="Google"/> that is a working
/// account served in the backend's place (the Linux server with Google Messages on), not a refusal, so the state does not call Google absent. <paramref name="VoiceRuns"/> is the same
/// for Google Voice (the Linux server with its own Chrome): its <paramref name="Voice"/> is the real accounts behind a port that refuses the cookie hand-over.
/// </summary>
internal sealed record ServerUnavailablePorts(IGoogleMessagesAccount? Google = null, IInstagramAccount? Instagram = null, IGoogleVoiceAccounts? Voice = null,
    MessageLink.Core.Backend.IChatBackend? Backend = null, IHelperRestarter? HelperRestarts = null, bool GoogleRuns = false, bool VoiceRuns = false)
{
    public bool GoogleAbsent => Google is not null && !GoogleRuns;
    public bool VoiceAbsent => Voice is not null && !VoiceRuns;
}

/// <summary>
/// The server side of the protocol: the real routers and stores behind a <see cref="ProtocolServices"/>, the event log that serves
/// the providers' feed, the server's pushed state, the journal that makes a send happen at most once, and the upload store. A
/// transport (the app's loopback now, a pipe later) starts a dispatcher over <see cref="Services"/> and <see cref="Journal"/>.
/// </summary>
internal sealed class ProtocolServer : IDisposable
{
    private readonly UploadStore uploads;
    private readonly TypingHub typingHub;
    private readonly BackendCaughtUp caughtUp;

    private readonly IServerIdentity? identity;

    private readonly ServerScheduledSends? scheduled;

    private ServerRepeats? repeats;
    /// <summary>Hermes as the server's page sets it up: the backend itself, since a server's clients may not (ServerHermesAccount). Null where Hermes does not run.</summary>
    internal IHermesAccount? HermesSetup { get; private init; }
    private ProtocolServer(ProtocolServices services, string version, EventLog eventLog, StatePublisher statePublisher, OperationJournal journal, UploadStore uploads, TypingHub typingHub,
        BackendCaughtUp caughtUp, IServerIdentity? identity, ServerScheduledSends? scheduled, ServerSavedPhotos? savedPhotos, ConversationHistory? history)
    {
        Services = services; Version = version; EventLog = eventLog; StatePublisher = statePublisher; Journal = journal; this.uploads = uploads; this.typingHub = typingHub; this.caughtUp = caughtUp;
        this.identity = identity; this.scheduled = scheduled; this.savedPhotos = savedPhotos; this.history = history;
    }

    private readonly ServerSavedPhotos? savedPhotos;

    private readonly ConversationHistory? history;

    /// <summary>
    /// Reads the identity again now and puts it in the state before this returns, for a client in this process whose window just saw a sign-in
    /// finish or a service come or go (the server also follows its files, but only at its next look). Returns what the state now says, or none
    /// when this server has no identity.
    /// </summary>
    public async Task<ServerIdentity?> RefreshIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (identity is null) return null;
        await identity.RefreshAsync(cancellationToken);
        // On the pool: the build reads the Google profile from a file, and the caller may be the interface thread.
        await Task.Run(() => StatePublisher.Refresh(), cancellationToken);
        return StatePublisher.Latest?.State.Identity;
    }

    public ProtocolServices Services { get; private set; }
    internal Func<string, string?, CancellationToken, Task<NativeAuthenticationTarget>>? NativeAuthentication { get; set; }

    internal void ConfigureAccountSignIn(Func<string, string, CancellationToken, Task<ProtocolServices>> select) => Services = Services with { AccountSignIn = select };
    public string Version { get; }
    public EventLog EventLog { get; }
    public StatePublisher StatePublisher { get; }
    /// <summary>The journal belongs to the host, not to a connection: it is what makes a send, and a sign-in, happen at most once.</summary>
    public OperationJournal Journal { get; }

    private static IHelperRestarter LockRestarts(IHelperRestarter restarter, Func<string, SemaphoreSlim>? locks) => locks is null ? restarter : new LockedHelperRestarter(restarter, locks);

    public static ProtocolServer Create(ProtocolServerOptions options)
    {
        var messaging = options.Messaging;
        var directory = options.Directory;
        var media = options.Media;
        var google = options.Google;
        var accounts = options.Accounts;
        if (accounts is not null)
        {
            accounts.LoadAsync().GetAwaiter().GetResult();
            messaging.Accounts = accounts; directory.Accounts = accounts; media.Accounts = accounts; options.Typing.Accounts = accounts;
        }
        var log = options.Log;
        // The account ports as BackendServices registers them. The window's own registrations are replaced by proxies over these
        // (App.Protocol.cs), so they are made here rather than asked of the container; the adapters hold no state of their own.
        var unavailable = options.Unavailable;
        var messengerAccount = new MessengerAccount(options.Messenger);
        IInstagramAccount instagramAccount = unavailable?.Instagram ?? new InstagramAccountLifecycle(options.Instagram);
        IGoogleMessagesAccount googleAccount = unavailable?.Google ?? new GoogleMessagesAccountPort(google, options.GoogleMedia);
        IGoogleVoiceAccounts voiceAccounts = unavailable?.Voice ?? new GoogleVoiceAccountsPort(options.VoiceAccounts);
        // The host's state: built from the real ports, appended to the log whenever it changes, and put in the log ahead of the
        // connection event that announced it. The publisher and the log refer to each other, so the hook is set through a local.
        StatePublisher? publisher = null;
        // The server runs the providers' feed once, into its log; the client's feed is a subscription to that log.
        // Made ahead of the log: a server's own incoming unarchive asks through it, as a client's chats.set would.
        var journal = new OperationJournal(log: log, store: options.JournalStore);
        // Each connection's version: the incoming unarchive leaves groups to a connected client older than the version that gave them to the server.
        var roster = new ConnectionRoster();
        // A server runs the incoming unarchive in the log's source, so it sees every provider event once however many clients read the log.
        var source = accounts?.Over(options.ProviderFeed) ?? options.ProviderFeed;
        // A server's whole history sees each event first, nearest the providers: the messages it names are in the copy before anything else reads them.
        var history = options.WholeHistory is { } historyStore
            ? new ConversationHistory(historyStore, messaging, messaging, messaging, messaging, messaging, messaging, log, messaging)
            : null;
        if (history is not null) source = history.Over(source);
        // Whether this server returns the one-to-one conversations too, put in its state (ServerLimits.UnarchivesChats) for its clients to split by.
        var unarchivesChats = false;
        if (options.IncomingUnarchive is { } unarchive)
        {
            // With a shared state and an identity the merges are a row the server can read, and it takes the one-to-one conversations outside them
            // too; without, groups alone.
            if (options.SharedState is { } rows && options.ServerTokens is not null && unarchive.Merges is null)
                unarchive = unarchive with
                {
                    // Read under the profile the store files the server's rows under, once the identity below is attached to it; before that nothing can be told
                    // of the merges, and the unarchive then leaves the conversation (it throws).
                    Merges = new SharedMergeMembership(rows),
                };
            unarchivesChats = unarchive.Merges is not null;
            source = ServerIncomingUnarchive.Over(source, messaging, messaging, journal, roster, unarchive, log);
            log("incoming unarchive via=host");
            // The Marketplace label (owner, 2026-11-12): one labeller and one memory for every device, writing the shared book as a client would.
            if (options.SharedState is { } labelRows && options.ServerTokens is not null)
            {
                source = ServerMarketplaceLabels.Over(source, messaging, labelRows, unarchive.State, unarchive.Merges, log);
                log("marketplace label via=host");
            }
        }
        // With a whole history each message event has its history change beside it: twice the ring, so a phone that resumes reaches back as far as before.
        var eventLog = new EventLog(source, history is null ? EventLog.DefaultCapacity : EventLog.DefaultCapacity * 2,
            history is null ? EventLog.DefaultSubscriberQueue : EventLog.DefaultSubscriberQueue * 2, log: log) { BeforeSourceEvent = change => publisher?.Watch(change) };
        history?.Attach(eventLog);
        // Not told of every sign-in the way the window's own port is, so it follows the files the key is read from itself.
        IServerIdentity? identity = options.ServerTokens is { } tokens ? ServerIdentityPort.Over(tokens, options.Messenger, options.Instagram, options.VoiceAccounts, followsFiles: true) : null;
        if (identity is not null && accounts is not null) identity = new MultipleAccountIdentity(identity, accounts);
        // A server's shared state is filed under its space key (Server\space.protected), pinned at the first write, and that key is what the server serves as
        // LocalKey from then on, so a later Google pairing on the server cannot orphan the rows. Only a server given a shared state (the two
        // servers) does this; the app's in-process and loopback hosts serve the live key exactly as before and never touch the file.
        if (options.SharedState is { } shared && identity is not null) identity = shared.AttachSpace(change => eventLog.AppendShared(change), identity);
        if (options.SharedState is { } unsettledRows && identity is not null)
        {
            journal.UnsettledSendRecorder = new ServerUnsettledSends(unsettledRows).RecordAsync;
            _ = journal.PublishUnsettledSendsAsync();
        }
        // A server keeps and fires its clients' scheduled sends: the rows are its shared state's, filed under the key it serves, and a send goes
        // through its one journal. Started below, once the services stand.
        ServerScheduledSends? scheduled = null;
        if (options.Scheduled is { } scheduledOptions && options.SharedState is { } rowsStore && identity is { } scheduledIdentity)
        {
            // Through the whole history, so a scheduled send is read back into the copy as one sent now is.
            scheduled = new ServerScheduledSends(rowsStore,
                history is null ? messaging : history, history is null ? messaging : history, messaging, scheduledIdentity, journal, scheduledOptions, log);
            log("scheduled sends via=host");
        }
        // Hermes (protocol 31): its account as clients see it. A server takes the address and keys on its own page (CanConfigure false), as every account; an app
        // running its own services takes them from its Settings. Repeats need the shared state, so only a server keeps them.
        IHermesAccount? hermesAccount = options.Hermes is { } hermesBackend ? new ServerHermesAccount(hermesBackend, canConfigure: options.SharedState is null) : null;
        ServerRepeats? repeats = null;
        if (options.Hermes is { } prompts && options.SharedState is { } repeatRows && identity is not null)
            repeats = new ServerRepeats(repeatRows, prompts, log: log);
        var builder = new ServerStateBuilder(new ServerStatePorts(messaging, messaging, messaging, media, media, messaging, directory, directory, messengerAccount,
            instagramAccount, googleAccount, voiceAccounts, options.Typing, identity,
            GoogleAbsent: unavailable?.GoogleAbsent == true, VoiceAbsent: unavailable?.VoiceAbsent == true, InstagramAbsent: unavailable?.Instagram is not null,
            UnarchivesChats: unarchivesChats, FiresScheduled: scheduled is not null,
            ChromeOutOfDate: () => options.RemoteSignIn?.BrowserOutOfDate == true || options.BrowserOutOfDate?.Invoke() == true,
            AdditionalScopes: accounts is null ? null : () => accounts.Added.Select(ProviderAccounts.Scope).ToArray(),
            Hermes: hermesAccount, Repeats: repeats is not null),
            AttachmentBatchRules.ItemLimit);
        var statePublisher = new StatePublisher(eventLog, builder.Build, write: log);
        publisher = statePublisher;
        if (accounts is not null) accounts.Changed += () => statePublisher.Refresh();
        if (hermesAccount is not null) hermesAccount.Changed += (_, _) => statePublisher.Refresh();
        statePublisher.Attach(messengerAccount, instagramAccount, googleAccount, voiceAccounts);
        var step = new StepClock(log);
        statePublisher.Start();
        step.Took("host.first_state");
        // Files on their way to a send wait here, in memory only unless the server gives a folder: everything else this app keeps on disk is
        // protected, and an in-process server has no reason to write a plaintext copy. A separate server process spills under a per-run key (UploadCipher).
        var uploads = new UploadStore(folder: options.UploadFolder, log: log);
        // A read marker and a typing start arrive with the client's private-reading admission as an argument, since the gate's AsyncLocal cannot
        // cross; the server applies it around the router, which still refuses what is unadmitted.
        var typingHub = new TypingHub(options.Typing, log);
        // Made below, once the services exist; a sign-out tells it which account went.
        ServerSavedPhotos? savedPhotos = null;
        // With a whole history, the reads it can answer (pages, held messages, resolves, peeks, windows) and the changes it reads back go through it.
        // Files and a first message go around the history's own ports, so they are read back into it.
        var readBack = history is null ? null : new HistoryReadBack(media, media, directory, history);
        // chats.sync lists the rows as the history builds them, their last message from its copy, as chats.list does.
        var multiAdmin = accounts is not null && options.Admin is not null ? new MultipleAccountAdmin(options.Admin, accounts) : null;
        if (options.IncomingCards is IncomingCardRequester incomingCards) incomingCards.Accounts = accounts;
        var services = new ProtocolServices(Backend: accounts is null ? unavailable?.Backend ?? google : new MultipleAccountBackend(unavailable?.Backend ?? google, accounts, log), Chats: history is null ? messaging : history,
            ConversationSync: new ConversationSync(history is null ? messaging : history, options.ConversationSyncStore, log), Sender: history is null ? messaging : history,
            Peek: history is null ? messaging : history, Actions: history is null ? messaging : history, History: history is null ? messaging : history, WholeHistory: history,
            Contacts: directory, FrequentContacts: directory, Groups: directory, Search: accounts is null ? google : new ProviderAccountSearch(accounts, google), ReadState: new PrivateReadingServerReadState(messaging), Settings: messaging,
            Attachments: readBack ?? (IAttachmentBackend)media, Snoozes: options.Snoozes, Mutations: history is null ? messaging : history, Deletion: messaging,
            GroupManagement: directory, FirstMessage: readBack ?? (IFirstMessageStarter)directory,
            Events: eventLog, Media: media, AttachmentBatches: readBack ?? (IAttachmentBatchBackend)media, Uploads: uploads,
            MessengerAccount: options.AccountLocks is { } locks ? new LockedMessengerAccount(messengerAccount, () => locks(RestoreOnce.Messenger)) : messengerAccount,
            InstagramAccount: options.AccountLocks is { } instagramLocks ? new LockedInstagramAccount(instagramAccount, () => instagramLocks(RestoreOnce.Instagram)) : instagramAccount,
            GoogleAccount: googleAccount, VoiceAccounts: voiceAccounts, State: statePublisher, Typing: new PrivateReadingServerTyping(options.Typing), TypingFeed: typingHub, ComposerActivity: options.Typing,
            Diagnostics: options.Diagnostics,
            HelperRestarts: LockRestarts(unavailable?.HelperRestarts ?? new ServerHelperRestarter(google, options.Keeper, options.Messenger, options.Instagram, options.VoiceAccounts, log) { Accounts = accounts }, options.AccountLocks), KeptHistory: accounts is not null && options.HelperCells is { } cells ? new ProviderAccountKeptHistory(accounts, options.KeptHistory ?? new MessengerKeptHistory(() => options.Messenger.SelfId is { } self ? MessengerKeptAccount.For(self) : null, options.Messenger.KnownChat, log, cells), cells, log) : options.KeptHistory,
            MediaPreparer: media, IncomingCards: options.IncomingCards, ClientState: options.ClientState, Restores: options.Restores,
            GoogleSignedIn: options.Keeper.SignedIn, Logs: options.Logs, Admin: multiAdmin ?? options.Admin, Backup: options.Backup, SharedState: options.SharedState, Connections: roster, Scheduled: scheduled, HermesAccount: hermesAccount, Repeats: repeats, RemoteSignIn: options.RemoteSignIn, VoiceRemoteSignIn: options.VoiceRemoteSignIn, MetaRemoteSignIn: options.MetaRemoteSignIn,
            AccountEnded: (account, key) =>
            {
                savedPhotos?.Ended(account, key);
                history?.Ended(account, key);
            },
            // A PC's choices taken into the shared state once: only a server with a shared state and an identity, and a key it adopts is put in its state.
            Imports: options.SharedState is { } importing && identity is not null
                ? new ServerStateImport(importing, uploads, () => Task.Run(() => statePublisher.Refresh()), log) : null);
        services = services with { RequiredProtocol = () => accounts?.RequiresAccountProtocol == true ? ProtocolVersion.MultipleAccounts : ProtocolVersion.Minimum };
        if (multiAdmin is not null) multiAdmin.Ended = services.AccountEnded;
        // Each commit to the shared state goes into the log in rev order, and the keys it may hold are judged under the profile the server files them under
        // now: the one its clients take as theirs from the state (IdentityAccessTokens), which with an identity is the space's (attached above). Without an
        // identity no single row is shared.
        if (identity is null) options.SharedState?.Attach(change => eventLog.AppendShared(change), null);
        // What the providers' catch-ups name goes into the log in the order they happen, ahead of the events that follow them.
        var caughtUp = new BackendCaughtUp(google, options.Messenger, options.Instagram, accounts);
        caughtUp.CaughtUp += notice => eventLog.AppendCaughtUp(notice);
        // A server keeps its clients' saved photos' originals, from the rows they write.
        savedPhotos = options.SharedState is { } sharedRows && options.SavedPhotos is { } kept ? new ServerSavedPhotos(sharedRows, kept, log) : null;
        step.Took("host.rest");
        // Loaded and started off this thread: a message found sending is carried as interrupted, one whose window passed while the server was down is held, and
        // the rest wait for their time. A start that fails is tried again (ServerScheduledSends.StartWithRetryAsync); nothing is sent by a schedule that has not loaded.
        if (scheduled is not null) _ = scheduled.StartWithRetryAsync();
        repeats?.Start();

        // The whole history is kept whether or not a client is connected: its store opens, and the providers' feed runs from now on.
        if (history is not null)
        {
            history.Start();
            eventLog.Start();
        }

        return new ProtocolServer(services, options.Version, eventLog, statePublisher, journal, uploads, typingHub, caughtUp, identity, scheduled, savedPhotos, history) { repeats = repeats, HermesSetup = options.Hermes };
    }

    /// <summary>Stops publishing state. A transport calls it before it closes its clients, and <see cref="Dispose"/> repeats it harmlessly.</summary>
    public void StopPublishing() => StatePublisher.Dispose();

    public void Dispose()
    {
        // First: nothing starts sending once the server is closing. A message only waiting stays for the next start (the engine's own Stop).
        scheduled?.Dispose();
        repeats?.Dispose();
        savedPhotos?.Dispose();
        caughtUp.Dispose();
        StatePublisher.Dispose();
        typingHub.Dispose();
        EventLog.Dispose();
        history?.Dispose();
        uploads.Dispose();
        Journal.Dispose();
    }
}
