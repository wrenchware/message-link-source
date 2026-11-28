namespace MessageLink.Protocol;

/// <summary>
/// The protocol's own integer version, separate from the application's: it changes only when a peer that understands
/// an older one would misread a newer one (see <see cref="ProtocolJson"/>).
/// </summary>
/// <remarks>
/// 2 adds the shared state: <c>state.sync</c>, <c>state.get</c>, <c>state.put</c> and the <c>shared</c> event. It is
/// additive: a version 2 server still serves a version 1 client, which never asks for them and ignores the event kind it does not know, and a version 2
/// client of a version 1 server keeps its shared rows to itself.
/// <para>3 adds four shared categories (<see cref="SharedCategories"/>): the chat preferences (<c>chatpref:</c>), the names kept for
/// people (<c>name:</c>), the snoozes (<c>snooze:</c>) and the pinned order. No method changes: a version 2 peer is simply never given those rows, a version 2
/// client keeps its own files and asks <c>snoozes.*</c> as before, and a version 3 client of a version 2 server stays on its files as it was.</para>
/// <para>4 changes who returns an archived conversation to the inbox when a message arrives in it: a server does it for group
/// conversations, once for all its clients, and a client connected at 4 leaves those to it. No frame and no shared category changes; what moves is a duty,
/// so both sides go by the version: a client below 4, or of a server below 4, does all of it itself as before, and a server leaves it to such a client while one
/// is connected.</para>
/// <para>5 adds the merges (<c>merges:</c>) and the labels (<c>labels:</c>), each one row written by compare-and-set
/// (<see cref="SharedConflictPolicy.CompareAndSet"/>), and gives the host the archived one-to-one conversations outside every merge as well, now that it can
/// read the merges: a client connected at 5 leaves those to it too. A peer of 4 is never given those rows and keeps its merges and labels to itself, and a
/// server leaves one-to-one conversations to its clients while any connection below 5 is open, as it leaves groups to them below 4.</para>
/// <para>6 adds the drafts (<c>draft:</c>) and the rows noting who owns them (<c>draft-owner:</c>), each a chat's own row, last writer wins
/// (the words kept for no account yet, <c>draft-unfiled:</c>, stay on the device). No method changes: a peer of 5 is never given those rows and keeps its drafts
/// on its own device, and a client of 6 of a server of 5 does the same.</para>
/// <para>7 adds the read-here marks (<c>preferences:</c> row <c>read-here</c>, one row written by compare-and-set) and the private reading
/// choice (one row a service, each last writer wins). A peer below 7 is never given them and keeps both to itself: its marks in its state, its choice in
/// <c>app-preferences.json</c>, exactly as before. No method changes.</para>
/// <para>8 adds the saved messages with their reminder times (<c>saved:</c>, a message one row, saved by a create-only write and deleted when unsaved)
/// and the custom avatars (<c>avatar-v2:</c>, last writer wins; a value over 64 KiB is read with <c>state.get</c>). No method changes: a peer below 8 is never given those
/// rows and keeps both on its own device. A server of 8 also keeps the originals of the saved messages' photos itself, and serves them through <c>media.read</c> to
/// whichever device opens one, so a client connected at 8 keeps none of its own; one below 8 does as before.</para>
/// <para>9 moves the firing of scheduled sends to the server (owner D6): a server runs them itself, even with every client closed,
/// and its clients draw them from rows (<c>scheduled:</c>, written by the server alone, <see cref="SharedCategory.HostOwned"/>) and ask it for the four typed,
/// journaled commands <c>scheduled.create</c>, <c>edit</c>, <c>cancel</c> and <c>sendNow</c>. A send of unknown outcome or failed on the server is a host-owned
/// row too (<c>outbox:</c>), drawn by every client as "may not have sent", and <c>outbox.dismiss</c> removes it everywhere.
/// A client below 9, or of a server below 9, keeps its own schedule and fires it itself as before; a client of 9 or later never fires one itself, which is
/// what keeps one message from being sent by both (S2, S3).</para>
/// <para>10 gives <c>scheduled.cancel</c> and <c>scheduled.edit</c> with <c>IfRev</c> 0 a meaning: discard a message the client never saw the row of (its
/// stand-in for a create of unknown outcome). The server takes the message away if it made it and otherwise closes its id for good, so that the create can never make it. A client
/// of 10 does not send it to a server below 10 (it keeps the stand-in and says so). A client below 10 of a server of 10 still answers a stand-in's cancel by asking the create again and
/// then cancelling, which a create arriving just before its time and a cancel just after can turn into a send; that stays until such a client is updated.</para>
/// <para>11 adds <c>state.import</c>, the one method that moves a PC's choices onto a server (a journaled mutation that takes a bundle of
/// shared rows, handed up like a file, into the shared state, adding and never replacing). No category and no frame changes: a client below 11 never calls it, and
/// a server answers it as not supported to a connection that negotiated less.</para>
/// <para>12 adds <c>accounts.google.remoteSignIn</c> and <c>accounts.google.remoteSignInInput</c> and the <c>signInView</c> event: a Google sign-in made in the
/// server's own browser, shown on the server's page (<c>ServerLimits.RemoteGoogleSignIn</c> says whether a host offers it). Additive: a client below 12 never calls them, a host
/// answers them as not supported to a connection that negotiated less, and a client of 12 of a server below 12 (or one that does not say it offers it) signs in as before.</para>
/// <para>13 adds <c>accounts.google.qrPairing</c> and <c>accounts.google.qrPairingCancel</c> and the <c>qrCode</c> event: Google Messages paired by QR code, the phone scanning a code
/// the server shows (<c>ServerLimits.QrPairing</c> says whether a server offers it). Additive in the same way as 12: a client below 13 never calls them, a server answers them as not
/// supported to a connection that negotiated less, and a client of 13 of a server below 13 (or one that does not say it offers it) pairs as before.</para>
/// <para>14 (Chrome on the server) adds <see cref="ServerLimits.ChromeOutOfDate"/>: the server's own Chrome has gone unrenewed past the age the image was meant to keep it. The
/// sign-in window, its methods and the <c>signInView</c> event are as at 12; what the pictures show is now the server's whole virtual display (the page's size in <c>ViewWidth</c> by
/// <c>ViewHeight</c> is that display's, and pointer positions are pixels of it). Additive: a client below 14 ignores the member, and a server below 14 never sets it.</para>
/// <para>15 (Google Voice on the server) adds <c>accounts.voice.remoteSignIn</c> and <c>ServerLimits.RemoteVoiceSignIn</c>: a Google Voice sign-in made by hand in the server's own Chrome, on a
/// Chrome profile of that Voice account's own, shown in the same window and driven by the same <c>accounts.google.remoteSignInInput</c> and <c>signInView</c> as Google Messages'. Additive: a client below 15 never calls
/// it, a server answers it as not supported to a connection that negotiated less, and a client of 15 of a server below 15 (or one that does not say it offers it) signs Voice in as before, or is told the server does not run it.
/// A server that offers it refuses the cookie hand-over <c>accounts.voice.add</c> from a client (<c>sign_in_use_server</c>): cookies copied from a PC are refused by Google at two hours.</para>
/// <para>16 (accounts move with the PC) adds <c>accounts.move.*</c> (<c>accounts.move.status</c> and nine more): Messenger moves as the PC's
/// device (session, encrypted-device database, retained history) and Instagram as the PC's session, over the same connection, staged on the server before anything runs and
/// activated together; a move back holds the accounts and takes them home. A held server answers an old client's account calls with the word <c>account_moved_away</c>. It also adds
/// <c>MessengerEncryption.DeviceMissing</c> to the state a client reads (the encrypted-device keys of a restore are gone and nothing was registered). Additive for the methods: a client below 16 never
/// calls them and a server answers them as not supported to a connection that negotiated less. A server never publishes the new enum word to a connection below 16 (an older client would not be able to read the frame): it
/// sends <c>Down</c> there instead. The account move and every <c>accounts.move.*</c> method were removed on 2026-11-21 (16, 17, 19 and 28 below keep their numbers): a server
/// answers them as unknown methods.</para>
/// <para>17 (the third review) adds the Instagram piece <c>guard</c>: the account's limits travel with its session both ways, so a move is no way round the hour of
/// background reads, and an account check the server was held for comes home with the account. A client of 17 moves Instagram only to a server of 17 (a server below 17 would refuse the piece); a move
/// back from a server below 17 finds no guard there and keeps this PC's own.</para>
/// <para>18 adds <c>host.logs</c> (<see cref="ProtocolMethods.ServerLogsRead"/>): the server's own logs, a chunk at a time, by a name from a fixed list (<see cref="ServerLogNames"/>, never a
/// path) and only to the owner device, so the owner's Messages keeps copies of them and nobody needs a shell on the server to read them. Additive: a client below 18 never calls it, a server
/// answers it as not supported to a connection that negotiated less (or when it has no logs to give), and a client of 18 asks only a server of 18.</para>
/// <para>19 (2026-10-12) guards a move back against a device database the PC cannot read, now that the server updates itself nightly and the PC only when released: a server
/// reports in <c>deviceSchema</c> the schema version recorded in the Messenger device database it would hand back (whatsmeow's own <c>whatsmeow_version</c>) and in
/// <c>sessionVersion</c> the format of Instagram's saved session envelope, and adds
/// <c>accounts.move.resume</c>, which lifts a hold that was not released and runs the account on the server again, for a move back whose downloaded
/// bytes turned out too new after all. Additive: a server below 19 reports no schema, which a client reads as compatible (it was built before this
/// client, and the bytes are checked again once they arrive), and is never asked to resume; a client below 19 asks neither.</para>
/// <para>20 adds the server's administration for its owner device: <c>host.status</c>, <c>host.accounts</c> and <c>host.removeAccount</c> (<see cref="ProtocolMethods.ServerStatusRead"/>),
/// and <c>devices.rename</c>. Additive: a client below 20 never calls them, a server answers them as not supported to a connection that negotiated less, and a client of 20 asks only a
/// server of 20. It also writes the support window down (<see cref="Supported"/>), which changes nothing on the wire.</para>
/// <para>21 (ask to join) lets a new device ask to join with nothing but the server's address: it connects without a token on a join connection
/// (<see cref="NetworkPairing.JoinHeader"/>), the server holds its request, and the owner approves it on the server's page or with <c>devices.joinRequests</c>,
/// <c>devices.approveJoin</c> and <c>devices.denyJoin</c> after matching a short code both ends work out (<see cref="JoinMatch"/>). Additive: a client below 21 never
/// calls them and pairs with a code as before, a server answers them as not supported to a connection that negotiated less, and an older server refuses a join connection at
/// its door, which a client of 21 reads as "use a pairing code".</para>
/// <para>22 (2026-10-14, after an adversarial review) adds the finish code to asking to join: an approval to a join connection of 22 makes a code shown only on the owner's
/// screen (<c>devices.approveJoin</c> answers it in <see cref="JoinApproved.FinishCode"/>; the page shows it), the wait answers its proof (<see cref="PairJoinApproved"/>)
/// instead of the token, and the device asks for its token with <c>pair.joinFinish</c> (<see cref="NetworkPairing.JoinFinishMethod"/>) once its person typed a code that
/// gives the proof (<see cref="JoinMatch.FinishProof"/>); the device is added only then. Additive for the server: a join connection of 21 is answered as before. Not for
/// the device: a device of 22 asks only a server of 22, since anything answering at the address could otherwise say it is older and skip the proof.</para>
/// <para>24 adds the server's whole copy of every conversation: <c>history.sync</c>, <c>history.older</c> and <c>history.changes</c>, and the
/// <c>history</c> event, which carries the changed messages themselves. Additive: a client below 24 never calls them and is never sent the event (its place in the sequence
/// counts as sent), and a server answers them as not supported to a connection that negotiated less. What every version gains is that the server answers its existing reads
/// (<c>messages.page</c>, <c>history.held</c>, <c>messages.resolve</c>, the peeks) from the same copy where it holds the conversation, with page cursors of its own.</para>
/// <para>25 adds one ordered feed of the server's list rows and messages together (<c>history.feed</c>), a client's check of its copies against
/// the server's (<c>history.verify</c>), the server's check of its own copy against the services (<c>history.mirror</c>), and the <c>row</c> event (a list row the
/// server built again by itself). Additive: a client below 25 never calls them and is never sent the event, and a server answers them as not supported to a
/// connection that negotiated less. What every version gains is a list row whose last message is the server's copy's, so
/// the list never says what the thread does not hold.</para>
/// <para>26 adds the server's own backup: <c>server.backup.status</c> to any device, <c>server.backup.run</c> and
/// <c>server.backup.password</c> to the owner device, and the <c>backup</c> account signal (<see cref="AccountSignals.Backup"/>). Additive: a client below 26 never
/// calls them and is never sent the signal (its place in the sequence counts as sent), and a server answers them as not supported to a connection that negotiated less.
/// A restore changes nothing on the wire: every copy a client holds starts over through the new epochs it already reads.</para>
/// <para>27 adds <c>devices.joinLink</c>: a new pairing code as the link a phone scans and the line a PC pastes, to the owner device.
/// Additive: a client below 27 never calls it, and a server answers it as not supported to a connection that negotiated less.</para>
/// <para>27 adds <c>accounts.remoteSignIn</c>: one method for a sign-in in the server's own browser, for any service it names (<see cref="RemoteSignInServices"/>), with
/// the <c>signInView</c> event and the input method of 12 and one new input kind, <see cref="RemoteSignIn.DoneKind"/>, by which the person says a Messenger or Instagram sign-in is finished. It adds
/// <see cref="ServerLimits.RemoteMessengerSignIn"/> and <see cref="ServerLimits.RemoteInstagramSignIn"/>. Additive: a client below 27 never calls it and keeps the methods of 12 and 15, which stay; a host
/// answers it as not supported to a connection that negotiated less; a client of 27 of a server below 27 (or one that does not say it offers a service) signs that service in as before.</para>
/// <para>27 adds <c>push.testShown</c> (a phone showed the server's test push) and <c>push.status</c> (how the server's pushes
/// stand, for Manage server) to any device, and the <c>push</c> account signal (<see cref="AccountSignals.Push"/>: pushes turned on or their files replaced, so an
/// open phone signs up at once). Additive: a client below 27 never calls them and is never sent the signal (its place in the sequence counts as sent), and a server
/// answers them as not supported to a connection that negotiated less. <c>push.register</c> with an empty token and a package the project lacks, which phones
/// already send, is now kept as a report rather than refused, at every version.</para>
/// <para>28 adds <c>accounts.move.authorize</c>: the server's password, which lets this device move accounts for the next hour. Not additive for a move: a client
/// below 28 has no way to give it, so its move is refused in words, and it updates. Everything else that managed a server left the door at this version too, for every client: the
/// page does it.</para>
/// <para>29 (owner, 2026-11-05) adds contacts saved through the phone: <c>phonebook.*</c> (<see cref="ProtocolMethods.PhoneContactsOffer"/> and six more) and the
/// <c>phoneContacts</c> account signal (<see cref="AccountSignals.PhoneContacts"/>). A PC hands the server a new contact, a number for an existing one or a contact's photo,
/// and a phone with the setting on writes it into its Google account. Additive: a client below 29 never calls them and is never sent the signal (its place in the sequence
/// counts as sent), and a server answers them as not supported to a connection that negotiated less, which a PC of 29 reads as "no phone saves contacts".</para>
/// <para>31 (owner, 2026-11-14) adds Hermes Agent, a thread a Hermes profile: <c>ServerState.Hermes</c> (the address and each profile's name, title and state, never a key),
/// the <c>hermes:</c> scope, <c>accounts.hermes.newConversation</c>, <c>accounts.hermes.configure</c> (page-only on a server), <see cref="TypingParticipant.Status"/> (what Hermes is doing while it
/// works), and the repeats: <c>repeats.create</c>, <c>repeats.cancel</c>, the host-owned <c>repeat:</c> rows and <c>ServerLimits.Repeats</c>. Additive: a client below 31 never calls them, is never
/// given the rows, and a server answers the methods as not supported to it. Such a client still lists a Hermes thread, as a service it does not know; the owner updates
/// server, PC and phone together (2026-11-12), so no client below 31 is filtered.</para>
/// <para>32 (owner, 2026-11-16) adds Hermes's approvals: a command Hermes holds for approval arrives in its thread as a message of type <c>approval</c> carrying
/// <see cref="MessageLink.Core.Messaging.ChatMessage.Approval"/> (the command, why it was held, how it stands and until when), and <c>accounts.hermes.answerApproval</c>
/// approves it once or denies it. Additive: a client below 32 reads the message's text (that Hermes needs approval) and never calls the method, and a server answers
/// it as not supported to a connection that negotiated less.</para>
/// <para>33 (owner, 2026-11-19) adds <see cref="HistoryOlderParams.HeldOnly"/>: an older read answered from the server's copy alone, never read further from the
/// service, so Export conversation can count and write what the server holds without asking a service anything. A server below 33 ignores the field and reads on
/// from the service, so a client sends it only to a server of 33.</para>
/// <para>34 (owner, 2026-11-21) adds <see cref="HistorySyncParams.HeldOnly"/>: a sync answered only from a conversation the server already holds, refused as
/// not ready otherwise and never read from the service, so the phone can keep its newest conversations ahead of opening them without asking a service
/// anything. A server below 34 ignores the field and fills the conversation from the service, so a client sends it only to a server of 34.</para>
/// </remarks>
public static class ProtocolVersion
{
    public const int Current = 34;
    /// <summary>The first version that answers a sync from what the server holds alone (<see cref="HistorySyncParams.HeldOnly"/>).</summary>
    public const int HeldSync = 34;
    /// <summary>The first version that answers an older read from the server's copy alone (<see cref="HistoryOlderParams.HeldOnly"/>).</summary>
    public const int HeldHistory = 33;
    /// <summary>The first version that answers Hermes's approvals (<see cref="ProtocolMethods.AccountsHermesAnswerApproval"/>).</summary>
    public const int HermesApprovals = 32;
    /// <summary>The first version with Hermes (<see cref="ProtocolMethods.AccountsHermesNewConversation"/>, <c>ServerState.Hermes</c>) and the repeats (<see cref="ProtocolMethods.RepeatsCreate"/>).</summary>
    public const int Hermes = 31;
    public const int MultipleAccounts = 30;
    /// <summary>The first version with contacts saved through the phone (<see cref="ProtocolMethods.PhoneContactsOffer"/> and the <c>phoneContacts</c> signal).</summary>
    public const int PhoneContacts = 29;
    /// <summary>The first version with the password a move asks for (<c>accounts.move.authorize</c>).</summary>
    public const int MovePassword = 28;
    /// <summary>The first version with the join link (<see cref="ProtocolMethods.DevicesJoinLink"/>).</summary>
    public const int JoinLinks = 27;
    /// <summary>The first version with <c>accounts.remoteSignIn</c> for any service (<c>ProtocolMethods.ServerSignInMethod</c>) and the Done input.</summary>
    public const int ServerSignIn = 27;
    /// <summary>The first version with the test push's answer (<see cref="ProtocolMethods.PushTestShown"/>), <see cref="ProtocolMethods.PushStatusRead"/> and the <c>push</c> signal.</summary>
    public const int PushTest = 27;
    /// <summary>The first version with the server's own backup (<see cref="ProtocolMethods.ServerBackupStatusRead"/> and the <c>backup</c> signal).</summary>
    public const int ServerBackup = 26;
    /// <summary>The first version with the one ordered feed (<see cref="ProtocolMethods.HistoryFeed"/>) and the copy checks.</summary>
    public const int Feed = 25;
    /// <summary>The first version with the server's whole history (<see cref="ProtocolMethods.HistorySync"/> and the <c>history</c> event).</summary>
    public const int WholeHistory = 24;
    /// <summary>23 adds chats.sync and persistent list revisions. Older clients keep chats.list; newer clients of older servers do too. No existing wire changes.</summary>
    public const int ConversationSync = 23;
    /// <summary>The first version with the server's pushes (<see cref="ProtocolMethods.PushOffer"/> and <see cref="ProtocolMethods.PushRegister"/>).</summary>
    public const int Push = 22;
    /// <summary>The first version whose join connections prove the owner's approval with a finish code (<see cref="NetworkPairing.JoinFinishMethod"/>).</summary>
    public const int JoinFinish = 22;
    /// <summary>The first version with asking to join (<see cref="NetworkPairing.JoinHeader"/>) and <c>devices.joinRequests</c>, <c>approveJoin</c> and <c>denyJoin</c>.</summary>
    public const int JoinRequests = 21;
    /// <summary>The first version that reports the Messenger device database's schema in the account-move status (<c>deviceSchema</c>) and can resume a held account (<c>accounts.move.resume</c>).</summary>
    public const int DeviceSchema = 19;
    /// <summary>The first version with the server's administration (<see cref="ProtocolMethods.ServerStatusRead"/> and the rest) and <c>devices.rename</c>.</summary>
    public const int ServerAdmin = 20;
    /// <summary>
    /// The oldest version a server keeps serving in full (the support window): the server updates nightly and its clients only when the
    /// owner installs a release, so every client from here to <see cref="Current"/> is served as it was. The current floor covers 0.1.404 (18), with its wire kept as a
    /// fixture. The owner's compatibility rule (2026-10-13) keeps older clients working within reason; a change that cannot easily support them may raise the floor,
    /// with a clear update request when a client can no longer be served. Each server/protocol step assesses whether a new client build is needed. Versions below it still connect
    /// (<see cref="Minimum"/>), without the promise.
    /// </summary>
    public const int Supported = 18;
    /// <summary>The first version with <c>host.logs</c> (<see cref="ProtocolMethods.ServerLogsRead"/>).</summary>
    public const int ServerLogs = 18;
    /// <summary>The first version that moves Instagram's limits with its session (<c>guard</c>): the account check it is held for and its hour of background reads.</summary>
    public const int AccountGuard = 17;
    /// <summary>The first version with <c>accounts.move.*</c> (<c>accounts.move.status</c>).</summary>
    public const int AccountMove = 16;
    /// <summary>The first version with the Google Voice sign-in in the server's own browser (<c>ProtocolMethods.VoiceRemoteSignInMethod</c>, <c>ServerLimits.RemoteVoiceSignIn</c>).</summary>
    public const int VoiceRemoteSignIn = 15;
    /// <summary>The first version with pairing by QR code (<c>ProtocolMethods.QrPairingMethod</c>).</summary>
    public const int QrPairing = 13;
    /// <summary>The first version with <see cref="ServerLimits.ChromeOutOfDate"/>.</summary>
    public const int ChromeAge = 14;
    /// <summary>The first version with the sign-in in the server's own browser (<c>ProtocolMethods.RemoteSignInMethod</c>).</summary>
    public const int RemoteSignIn = 12;
    /// <summary>The first version whose server reads a <c>scheduled.cancel</c> or <c>scheduled.edit</c> with <c>IfRev</c> 0 as "discard a message the client never saw" (it answers <c>conflict</c> below).</summary>
    public const int ScheduledDiscard = 10;
    /// <summary>The first version with <see cref="ProtocolMethods.StateImport"/>.</summary>
    public const int StateImport = 11;
    public const int Minimum = 1;
    /// <summary>The first protocol: what a method, frame kind or signal from before the version numbers counted anything has as its <c>Since</c>.</summary>
    public const int Original = 1;
    /// <summary>The first version with the shared state (<see cref="ProtocolMethods.StateSync"/>).</summary>
    public const int SharedState = 2;
    /// <summary>The first version that shares the chat preferences, names, snoozes and pinned order (<see cref="SharedCategories"/>).</summary>
    public const int SharedPreferences = 3;
    /// <summary>The first version whose server returns archived group conversations to the inbox itself, so its clients leave them.</summary>
    public const int ServerUnarchives = 4;
    /// <summary>The first version that shares the merges and the labels (<see cref="SharedCategories"/>), each written by compare-and-set.</summary>
    public const int SharedMerges = 5;
    /// <summary>The first version that shares the read-here marks and the private reading choice (<see cref="SharedCategories"/>).</summary>
    public const int SharedReading = 7;
    /// <summary>The first version whose server also returns archived one-to-one conversations outside every merge to the inbox.</summary>
    public const int ServerUnarchivesChats = 5;
    /// <summary>The first version that shares the drafts and who owns them (<see cref="SharedCategories"/>), each chat's draft one row, last writer wins.</summary>
    public const int SharedDrafts = 6;
    /// <summary>The first version whose server fires scheduled sends itself (<see cref="ProtocolMethods.ScheduledCreate"/>), so its clients do not.</summary>
    public const int ServerScheduled = 9;

    /// <summary>
    /// Whether a client connected at <paramref name="negotiated"/> leaves its scheduled sends to its server: true from <see cref="ServerScheduled"/>, false below,
    /// and null while no version is known (never connected). Unknown is neither: such a client fires nothing itself, because a server that may fire the same
    /// message is the one risk this has (S9).
    /// </summary>
    public static bool? ServerFiresScheduledAt(int negotiated) => negotiated <= 0 ? null : negotiated >= ServerScheduled;
    /// <summary>The first version that shares the saved messages and the custom avatars (<see cref="SharedCategories"/>), and whose server keeps the saved photos' originals.</summary>
    public const int SharedSaved = 8;

    /// <summary>
    /// Whether a client connected at <paramref name="negotiated"/> leaves group conversations' incoming unarchive to its server. Not connected counts as yes: a
    /// client that cannot reach its server cannot ask a provider either, and a no could make a second ask beside the server's.
    /// </summary>
    public static bool ServerUnarchivesAt(int? negotiated) => negotiated is not { } version || version >= ServerUnarchives;

    /// <summary>
    /// Whether a client connected at <paramref name="negotiated"/> also leaves the one-to-one conversations outside its merges to its server, which reads the
    /// merges from the shared state. Not connected counts as yes, for the reason <see cref="ServerUnarchivesAt"/> gives.
    /// </summary>
    public static bool ServerUnarchivesChatsAt(int? negotiated) => negotiated is not { } version || version >= ServerUnarchivesChats;

    /// <summary>
    /// Whether a client leaves the one-to-one conversations outside its merges to its server: the server says it takes them (<see cref="ServerLimits.UnarchivesChats"/>,
    /// from the last state it pushed), at a version that has the duty. The version alone is not enough: a server of any version without a shared state or an
    /// identity cannot read the merges, takes groups alone, and says so. Until a state says yes the client takes them itself; should the server take one as
    /// well, both ask under the arrival's one id and its journal makes them one.
    /// </summary>
    public static bool ServerUnarchivesChatsFor(int? negotiated, ServerLimits limits) => limits.UnarchivesChats && ServerUnarchivesChatsAt(negotiated);

    /// <summary>
    /// The highest version both sides speak, or null when there is none: the client speaks <paramref name="clientMinimum"/>
    /// (absent: only <paramref name="clientVersion"/>) up to <paramref name="clientVersion"/>, this server
    /// <see cref="Minimum"/> up to <paramref name="highest"/> (<see cref="Current"/> unless a server is made to speak less).
    /// </summary>
    public static int? Negotiate(int clientVersion, int? clientMinimum = null, int highest = Current)
    {
        var low = Math.Max(clientMinimum ?? clientVersion, Minimum);
        var high = Math.Min(clientVersion, Math.Min(highest, Current));
        return low <= high ? high : null;
    }
}
