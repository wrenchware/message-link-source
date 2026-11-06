using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>
/// The real implementations a host serves. A host that lacks one leaves it null, and every method that needs it answers
/// <c>not_supported</c>. Each port is the object the Core interface names; one object may fill several. <paramref name="Events"/> is the
/// host's one event log, shared by all its connections. So is <paramref name="Uploads"/>, where the files of sends not yet made wait
/// (a send needs it as well as <paramref name="Attachments"/>); <paramref name="Media"/> serves <c>media.read</c>, with
/// <paramref name="Attachments"/> for the copies that are saved. The four account ports serve the <c>accounts.*</c> methods, and
/// <paramref name="State"/> is the publisher whose newest state a host appends before it answers an account action (it may be left out,
/// and the answer then follows nothing). <paramref name="Typing"/> serves <c>typing.set</c> (a host in this process wraps its router in
/// <c>PrivateReadingHostTyping</c>, which applies a request's admission) and <paramref name="TypingFeed"/> carries the typing of others to
/// the connections that ask for it. <paramref name="ComposerActivity"/> serves <c>typing.noteActivity</c>, <paramref name="Diagnostics"/>
/// <c>host.diagnostics</c>, <paramref name="HelperRestarts"/> <c>host.restartHelper</c> and <paramref name="KeptHistory"/> the kept source of
/// <c>search.query</c> and <c>search.warmKept</c>. <paramref name="MediaPreparer"/> serves <c>media.prepare</c> and
/// <paramref name="IncomingCards"/> <c>media.requestCard</c>; <paramref name="ClientState"/> takes what each connection states with <c>client.state</c>.
/// <paramref name="Shutdown"/> serves <c>host.shutdown</c>; only a host that can be stopped by its owner has one (the app's in-process host and
/// a headless server answer not supported or refused). <paramref name="Restores"/> is the host's own launch restore, when it has one: a client's restore
/// of a provider the host restores itself answers the host's (<see cref="RestoreOnce"/>), so no account is logged in twice.
/// <paramref name="GoogleSignedIn"/> is what the host does after a Google sign-in by hand has been accepted (it tells its renewal keeper).
/// <paramref name="Devices"/> serves <c>devices.*</c>; only a host on the network has paired devices, and it gives each connection its own view (which device is asking).
/// <paramref name="SharedState"/> serves <c>state.*</c> (stage 6); only a server keeps one, and the app's in-process host, whose client keeps its own state, has none.
/// <paramref name="Connections"/> is told each connection's negotiated version and its end, for a host whose duties depend on its clients' versions (slice 9).
/// <paramref name="AccountEnded"/> is told, once it has been done, of an account signed out (<c>messenger</c>, <c>instagram</c>, <c>voice</c> with the account's key) or
/// of Google Messages' local data reset (<c>google-reset</c>), for a host that keeps something of its own for the account (slice 8: the saved photos' originals).
/// <paramref name="Scheduled"/> serves <c>scheduled.*</c> and <c>outbox.dismiss</c> (slice 11); only a server fires its clients' scheduled sends, and the app's in-process host and the loopback leave it null.
/// <paramref name="Imports"/> serves <c>state.import</c> (slice 13, protocol 11): a PC's choices taken into the shared state once; only a server with a shared state has one.
/// <paramref name="RemoteSignIn"/> serves <c>accounts.google.remoteSignIn*</c> (protocol 12): a Google sign-in made in the host's own browser; only the Linux server run with Google Messages and a headed browser has one.
/// <paramref name="AccountMove"/> serves <c>accounts.move.*</c> (protocol 16): Messenger and Instagram moving with the PC; only the Linux server has one.
/// <paramref name="VoiceRemoteSignIn"/> serves <c>accounts.voice.remoteSignIn</c> (protocol 15): a Google Voice sign-in made in the host's own browser, shown and driven as the Google Messages one is; only the Linux server run with Google Chrome has one.
/// <paramref name="Logs"/> serves <c>host.logs</c> (protocol 18): the host's own logs, by name, to any paired device; only a server has one.
/// <paramref name="Admin"/> serves <c>host.status</c>, <c>host.accounts</c> and <c>host.removeAccount</c> (SWITCHBOARD.md 4.6): the reads to any paired device, the removal to the page; only a server has one.
/// <paramref name="Backup"/> serves <c>server.backup.*</c> (protocol 26): the server's own backup; only the Linux server has one.
/// <paramref name="MetaRemoteSignIn"/> serves Messenger and Instagram on <c>accounts.remoteSignIn</c> (protocol 27): their sign-in in the host's own browser; only the Linux server run with its own Chrome has one.
/// </summary>
public sealed record ProtocolServices(
    IBeeperBackend? Backend = null,
    IChatReader? Chats = null,
    IMessageSender? Sender = null,
    IMessagePeek? Peek = null,
    IMessageActions? Actions = null,
    IHistoryWindowReader? History = null,
    IContactBackend? Contacts = null,
    IFrequentContacts? FrequentContacts = null,
    IGroupBackend? Groups = null,
    ISearchBackend? Search = null,
    IReadStateWriter? ReadState = null,
    IChatSettingsBackend? Settings = null,
    IAttachmentBackend? Attachments = null,
    IChatSnoozes? Snoozes = null,
    IMessageMutations? Mutations = null,
    IChatDeletion? Deletion = null,
    IGroupManagement? GroupManagement = null,
    IFirstMessageStarter? FirstMessage = null,
    EventLog? Events = null,
    IMediaReader? Media = null,
    IAttachmentBatchBackend? AttachmentBatches = null,
    UploadStore? Uploads = null,
    IMessengerAccount? MessengerAccount = null,
    IInstagramAccount? InstagramAccount = null,
    IGoogleMessagesAccount? GoogleAccount = null,
    IGoogleVoiceAccounts? VoiceAccounts = null,
    StatePublisher? State = null,
    ITypingService? Typing = null,
    TypingHub? TypingFeed = null,
    IComposerActivitySink? ComposerActivity = null,
    IHostDiagnostics? Diagnostics = null, IHelperRestarter? HelperRestarts = null,
    IKeptHistorySearch? KeptHistory = null, IMediaPreparer? MediaPreparer = null, IIncomingCards? IncomingCards = null, IClientStateSink? ClientState = null,
    IHostShutdown? Shutdown = null, RestoreOnce? Restores = null, Action? GoogleSignedIn = null, IPairedDevices? Devices = null,
    ISharedState? SharedState = null, ConnectionRoster? Connections = null,
    Action<string, string?>? AccountEnded = null, IScheduledCommands? Scheduled = null, IStateImport? Imports = null, IRemoteSignInHost? RemoteSignIn = null,
    IRemoteSignInHost? VoiceRemoteSignIn = null, IAccountMove? AccountMove = null, IHostLogs? Logs = null, IServerAdmin? Admin = null,
    IPushService? Push = null, ConversationSync? ConversationSync = null, IWholeHistory? WholeHistory = null, IServerBackup? Backup = null,
    IRemoteCookieSignInHost? MetaRemoteSignIn = null, IPhoneContacts? PhoneContacts = null)
{
    /// <summary>
    /// The host's sign-ins in its own browser, one at a time across every face that drives them (SWITCHBOARD.md 10.6). Made with the services, so every connection's copy of them (<c>with</c>) and
    /// the server's page share the one.
    /// </summary>
    public RemoteSignInSessions SignIns { get; init; } = new();
}
