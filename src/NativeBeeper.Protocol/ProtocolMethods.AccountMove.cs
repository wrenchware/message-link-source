using System.Text.Json;

namespace NativeBeeper.Protocol;

// The accounts move with the PC (SWITCHBOARD.md stage 6 slice 13, "accounts move with the PC"; protocol version 16). On moving day Messenger moves as this PC's device
// (its session, the encrypted-device database with its keys, and the retained history) and Instagram as this PC's saved session: no new device is registered and no new
// login is made. Google Messages and Google Voice are never carried; the server signs those in itself. The PC drives, and the server keeps one record per provider,
// sealed with its own key: none -> installing -> installed -> active, and held after a move back. A provider runs on the server only when it is active.
//
//   accounts.move.authorize    Not journaled, from protocol 28. The server's password, which lets this device use the methods below (all but status) for the next hour, whichever
//                              connection it uses. Never logged, kept or put in an answer.
//   accounts.move.status       Read. What the server holds of each provider (state, move id, the pieces staged and their digests, how much history, whether it is connected).
//   accounts.move.stage        Journaled. One piece of one provider (the session, the encrypted-device database, the encrypted flag, Instagram's session envelope), handed up
//                              as an upload (media.upload*) and written to a staging place nothing runs from. Its SHA-256 must match what the PC says it sent.
//   accounts.move.history.put  Not journaled: a page of Messenger's retained history (the helper's own export format), by page number. A page the server already holds is
//                              answered again and applied once, so a lost answer is simply asked again.
//   accounts.move.verify       Journaled. The PC says what it sent (digests, history counts); the server compares with what it holds and, when they match, calls the
//                              provider installed.
//   accounts.move.activate     Journaled. All of the named providers at once: their staged pieces are put where the server runs them from, each provider becomes active, and
//                              the server logs each in once. Nothing is activated unless every named provider is installed.
//   accounts.move.abort        Journaled. Throws away what was staged for a move that has not been activated.
//   accounts.move.hold         Journaled. A move back begins: the provider is recorded as held (before anything else) and its helper is closed. A held provider refuses to be
//                              restored, connected, given encryption or signed in, until a new move or an offline clear on the server.
//   accounts.move.read         Read, held only. A piece of a held provider, a chunk at a time, with its SHA-256.
//   accounts.move.history.get  Read, held only. A page of the held Messenger history, by the cursor the page before it gave.
//   accounts.move.release      Journaled. The PC has installed what it took: the server deletes its copies and keeps only the hold.
//   accounts.move.resume       Journaled, from protocol 19. A move back that cannot finish gives the accounts back: an unreleased hold of this move is lifted and the server runs them
//                              again (a device database too new for the PC, found only once its bytes arrived).
//
// Credentials. The session and the envelope are cookies; they cross only the pinned, authenticated connection, as the pieces of an upload, and are never logged, journaled or
// put in a state or an answer. A log line carries a fixed word and counts. The history pages hold message bodies and cross as a request's params; they are never logged.
public static partial class ProtocolMethods
{
    public const string AccountMoveStatus = "accounts.move.status";
    public const string AccountMoveStage = "accounts.move.stage";
    public const string AccountMoveHistoryPut = "accounts.move.history.put";
    public const string AccountMoveVerify = "accounts.move.verify";
    public const string AccountMoveActivate = "accounts.move.activate";
    public const string AccountMoveAbort = "accounts.move.abort";
    public const string AccountMoveHold = "accounts.move.hold";
    public const string AccountMoveRead = "accounts.move.read";
    public const string AccountMoveHistoryGet = "accounts.move.history.get";
    public const string AccountMoveRelease = "accounts.move.release";
    public const string AccountMoveResume = "accounts.move.resume";
    public const string AccountMoveAuthorize = "accounts.move.authorize";

    private static Entry[] AccountMoveEntries() =>
    [
        new(AccountMoveStatus, "IAccountMove.StatusAsync", typeof(AccountMoveStatusParams), typeof(AccountMoveStatus)),
        new(AccountMoveStage, "IAccountMove.StageAsync", typeof(AccountMoveStageParams), typeof(MovePiece), true),
        new(AccountMoveHistoryPut, "IAccountMove.PutHistoryAsync", typeof(MoveHistoryPutParams), typeof(MoveHistoryPutResult)),
        new(AccountMoveVerify, "IAccountMove.VerifyAsync", typeof(AccountMoveVerifyParams), typeof(AccountMoveVerified), true),
        new(AccountMoveActivate, "IAccountMove.ActivateAsync", typeof(AccountMoveActivateParams), typeof(AccountMoveActivated), true),
        new(AccountMoveAbort, "IAccountMove.AbortAsync", typeof(AccountMoveAbortParams), typeof(AccountMoveStatus), true),
        new(AccountMoveHold, "IAccountMove.HoldAsync", typeof(AccountMoveHoldParams), typeof(AccountMoveStatus), true),
        new(AccountMoveRead, "IAccountMove.ReadAsync", typeof(AccountMoveReadParams), typeof(AccountMoveChunk)),
        new(AccountMoveHistoryGet, "IAccountMove.GetHistoryAsync", typeof(AccountMoveHistoryGetParams), typeof(AccountMoveHistoryPage)),
        new(AccountMoveRelease, "IAccountMove.ReleaseAsync", typeof(AccountMoveReleaseParams), typeof(AccountMoveStatus), true),
        new(AccountMoveResume, "IAccountMove.ResumeAsync", typeof(AccountMoveResumeParams), typeof(AccountMoveStatus), true),
        new(AccountMoveAuthorize, "IPairedDevices.AuthorizeMoveAsync", typeof(AccountMoveAuthorizeParams), typeof(AccountMoveAuthorized)),
    ];
}

/// <summary>The providers that move with the PC, by the words the wire uses.</summary>
public static class MoveProviders
{
    public const string Messenger = "messenger", Instagram = "instagram";
    public static IReadOnlyList<string> All { get; } = [Messenger, Instagram];
    public static bool IsKnown(string? provider) => provider is Messenger or Instagram;
}

/// <summary>The pieces of an account, by the words the wire uses.</summary>
public static class MovePieces
{
    /// <summary>Messenger's saved session (its cookies), as the JSON the app saves; Instagram has the envelope instead.</summary>
    public const string Session = "session";
    /// <summary>Messenger's encrypted-device database (<c>e2ee.db</c>): the device identity and its keys, byte for byte.</summary>
    public const string Device = "device";
    /// <summary>Messenger's flag that encrypted conversations are on.</summary>
    public const string Flag = "flag";
    /// <summary>Instagram's saved session, as the helper handed it back.</summary>
    public const string Envelope = "envelope";
    /// <summary>Instagram's limits as its guard file keeps them (a refusal code and the times of the last hour's background reads, fixed words and numbers only), from protocol 17.</summary>
    public const string Guard = "guard";

    public static IReadOnlyList<string> Of(string provider) => provider switch
    {
        MoveProviders.Messenger => [Session, Device, Flag],
        MoveProviders.Instagram => [Envelope, Guard],
        _ => [],
    };
}

/// <summary>The words a provider's state is told in.</summary>
public static class MoveStates
{
    /// <summary>The server holds nothing of this provider and nothing is signed in.</summary>
    public const string None = "none";
    /// <summary>The server's own sign-in (made on the server, not moved): a move refuses until it is signed out there.</summary>
    public const string Own = "own";
    /// <summary>A move is handing pieces up. Nothing runs.</summary>
    public const string Installing = "installing";
    /// <summary>Every piece is staged and verified. Nothing runs.</summary>
    public const string Installed = "installed";
    /// <summary>The pieces are being put where the server runs them from.</summary>
    public const string Activating = "activating";
    /// <summary>The server runs this provider.</summary>
    public const string Active = "active";
    /// <summary>The account moved away (a move back): refused until a new move or an offline clear.</summary>
    public const string Held = "held";
    /// <summary>The record cannot be read: treated as held, and never guessed.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>The fixed words an account move refuses with (<c>account_failed</c>, as a code word).</summary>
public static class AccountMoveWords
{
    /// <summary>The server holds this account's hold: an old client's account call is answered with this word.</summary>
    public const string MovedAway = AccountFailureWords.AccountMovedAway;
    public const string NotInstalled = "move_not_installed", OtherMove = "move_other_id", Mismatch = "move_mismatch", PieceInvalid = "move_piece_invalid", DeviceMissing = "device_missing",
        AccountPresent = "account_present", NotHeld = "move_not_held", DeviceBusy = "device_busy", Busy = "move_busy", NotAllowed = "move_not_allowed", PageOrder = "move_page_order",
        HistoryFailed = "move_history_failed", NoRecord = "move_record_unreadable", Files = "move_files",
        /// <summary>A hold was recorded but the server could not confirm its helpers are stopped: the account stays held (nothing starts it), and nothing may be read from it yet.</summary>
        NotStopped = "move_not_stopped",
        /// <summary>
        /// A hold refused to a client below <see cref="ProtocolVersion.DeviceSchema"/>: what it would take home is in a newer format than its helpers read (<see cref="LegacyMoveFormats"/>).
        /// Nothing was held, and the account keeps running on the server.
        /// </summary>
        TooNewForClient = "move_too_new_for_client";
}

/// <summary>
/// What a client below <see cref="ProtocolVersion.DeviceSchema"/> takes home on a move back without checking it (main's check of the formats came with 19): its Messenger helper
/// opens a device database recorded at schema 15 at most, and its Instagram helper resumes envelope version 2 only. Every release from 0.1.311 to 0.1.404 shipped the same helpers
/// (whatsmeow fb386f152837, whose upgrade list ends at v15; the Instagram helper's resume refuses any version but 2). The host refuses such a client's hold when what it would hand
/// back is newer (ProtocolDispatcher.AccountMove.cs): 2026-10-13, the server updates nightly from main and its helpers with it, while the PC waits for a release.
/// </summary>
public static class LegacyMoveFormats
{
    public const int DeviceSchema = 15;
    public const int SessionVersion = 2;
}

/// <summary>The type the pieces' uploads are declared with. The server accepts a larger upload for it than for a photo (a device database).</summary>
public static class AccountMoveUpload
{
    public const string MimeType = "application/vnd.messages.account-move";
    /// <summary>The largest piece (an encrypted-device database).</summary>
    public const long MaxPieceBytes = 256L * 1024 * 1024;
    /// <summary>The most a history page may be: the helper keeps a page under a megabyte (a 256 KiB budget and one unit of at most 704 KiB), so this has margin, and a frame stays under the transport's 4 MiB.</summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;
    /// <summary>The most one <see cref="AccountMoveReadParams"/> asks for.</summary>
    public const int MaxReadBytes = 1024 * 1024;
}

public sealed record AccountMoveStatusParams(string? MoveId = null);

/// <summary>A piece the server holds: its name, size and the SHA-256 of its plaintext (hex, lower case).</summary>
public sealed record MovePiece(string Name, long Bytes, string Sha256);

/// <summary>
/// How much history the server holds of a move: <paramref name="Pages"/> pages applied (the next one it expects is this number), and the rows now present per section
/// (<paramref name="Sections"/>: added plus already there), <paramref name="Refused"/> the rows the helper left out.
/// </summary>
public sealed record MoveHistory(int Pages, int Refused, IReadOnlyDictionary<string, int> Sections, bool Complete = false);

/// <summary>
/// One provider as the server sees it. <paramref name="MoveId"/> is the move that staged or activated it (null for none or the server's own). <paramref name="Connected"/>
/// is whether the server is signed in to the service now. <paramref name="Unreleased"/> is a held provider whose copies the server still keeps. <paramref name="AccountKey"/> is the
/// one-way key of the account (no id). <paramref name="Ready"/> is stricter than connected: signed in, and for Messenger with encrypted conversations on, its encrypted connection up on the reused device. A PC deletes its copy only when it is ready.
/// <paramref name="Stopped"/> is true only for a held provider whose hold is saved on disk <b>and</b> whose helper the server has confirmed stopped: a PC reads a held account's pieces only then.
/// <paramref name="DeviceSchema"/> (protocol 19, Messenger only) is the schema version recorded in the device database the server runs (or holds) for the move, as whatsmeow records it in
/// <c>whatsmeow_version</c>: a move back compares it with what the PC's own helper reads before anything is held. Null when there is no such database, it cannot be read, or the host is below 19.
/// <paramref name="SessionVersion"/> (protocol 19, Instagram only) is the format of the saved session envelope the server would hand back (its <c>version</c>), compared the same way.
/// </summary>
public sealed record AccountMoveProvider(string Provider, string State, string? MoveId, bool Connected, IReadOnlyList<MovePiece> Pieces, MoveHistory History, bool Unreleased = false,
    string? AccountKey = null, bool Ready = false, bool Stopped = false, int? DeviceSchema = null, int? SessionVersion = null);

public sealed record AccountMoveStatus(IReadOnlyList<AccountMoveProvider> Providers)
{
    public AccountMoveProvider? Of(string provider) => Providers.FirstOrDefault(entry => entry.Provider == provider);
}

/// <summary>Hands up one piece. <paramref name="Sha256"/> is that of the plaintext, and the host checks it against what it received.</summary>
public sealed record AccountMoveStageParams(string ClientOpId, string MoveId, string Provider, string Piece, UploadRef Upload, string Sha256, string AccountKey) : MutationParams(ClientOpId);

/// <summary>A page of Messenger's retained history by number from 0, in the order the helper exported them; <paramref name="Data"/> is the page as it came.</summary>
public sealed record MoveHistoryPutParams(string MoveId, string Provider, int Page, JsonElement Data);

/// <summary>What the page did. <paramref name="Repeated"/>: the server already held it, and applied nothing. <paramref name="Next"/> is the page it expects now.</summary>
public sealed record MoveHistoryPutResult(int Page, int Added, int Skipped, int Refused, bool Repeated, int Next);

/// <summary>What the PC says it sent: each piece's digest, and the rows per section its export held (the last page's own tally).</summary>
public sealed record AccountMoveVerifyParams(string ClientOpId, string MoveId, string Provider, IReadOnlyList<MovePiece> Pieces, IReadOnlyDictionary<string, int> Expected) : MutationParams(ClientOpId);

/// <summary><paramref name="Matches"/>: what the server holds is what the PC sent, and the provider is now installed. <paramref name="Differs"/> names what is not (pieces and sections).</summary>
public sealed record AccountMoveVerified(bool Matches, IReadOnlyList<string> Differs, AccountMoveProvider Provider);

public sealed record AccountMoveActivateParams(string ClientOpId, string MoveId, IReadOnlyList<string> Providers) : MutationParams(ClientOpId);

/// <summary>
/// How the activation of one provider ended: <c>connected</c> (the server logged in), <c>not_connected</c> (it is active but the login failed; <paramref name="Reason"/> is a code word),
/// <c>pending</c> (still logging in) or <c>paced</c> (the server's restore pacing holds it for now).
/// </summary>
public sealed record AccountMoveActivation(string Provider, string State, string Outcome, string? Reason = null);

public sealed record AccountMoveActivated(IReadOnlyList<AccountMoveActivation> Providers);

public sealed record AccountMoveAbortParams(string ClientOpId, string MoveId) : MutationParams(ClientOpId);

/// <summary>A move back begins for the named providers (none named: all that are active), of the move whose id this is; the hold keeps that id, and read, history.get and release must name it.</summary>
public sealed record AccountMoveHoldParams(string ClientOpId, string MoveId, IReadOnlyList<string> Providers) : MutationParams(ClientOpId)
{
    // Host-only admission metadata, overwritten from the greeting by the dispatcher. An older client cannot supply or spoof it on the wire.
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClientProtocol { get; init; } = ProtocolVersion.Current;
}

public sealed record AccountMoveReadParams(string MoveId, string Provider, string Piece, long Offset, int Count)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClientProtocol { get; init; } = ProtocolVersion.Current;
}

/// <summary>Up to the asked count of bytes (base64) from <paramref name="Total"/>'s piece, with the piece's SHA-256.</summary>
public sealed record AccountMoveChunk(string Data, long Total, string Sha256);

public sealed record AccountMoveHistoryGetParams(string MoveId, string Provider, string? Cursor = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClientProtocol { get; init; } = ProtocolVersion.Current;
}

/// <summary><paramref name="Page"/> is the helper's page as it came; its <c>next</c> is the cursor for the page after it ("" on the last), and the last carries the export's own tally.</summary>
public sealed record AccountMoveHistoryPage(JsonElement Page);

public sealed record AccountMoveReleaseParams(string ClientOpId, string MoveId, IReadOnlyList<string> Providers) : MutationParams(ClientOpId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClientProtocol { get; init; } = ProtocolVersion.Current;
}

/// <summary>A move back gives the named providers back (protocol 19): each must be held by this move and not released (its copies are still on the server), and the server runs it again.</summary>
public sealed record AccountMoveResumeParams(string ClientOpId, string MoveId, IReadOnlyList<string> Providers) : MutationParams(ClientOpId);

/// <summary>The server's password, for <see cref="ProtocolMethods.AccountMoveAuthorize"/>.</summary>
public sealed record AccountMoveAuthorizeParams(string Password)
{
    /// <summary>Never the password, should the record ever be written anywhere.</summary>
    public override string ToString() => nameof(AccountMoveAuthorizeParams);
}

/// <summary>How the password went (<see cref="AccountMoveAuthorizeWords"/>).</summary>
public sealed record AccountMoveAuthorized(string Outcome);

/// <summary>The words <see cref="AccountMoveAuthorized.Outcome"/> takes.</summary>
public static class AccountMoveAuthorizeWords
{
    /// <summary>The right password: this device may move accounts for the next hour.</summary>
    public const string Right = "right";
    public const string Wrong = "wrong";
    /// <summary>Too many tries: wait, then try again. The try was not looked at.</summary>
    public const string Paced = "paced";
    /// <summary>The server's page has no password yet, so nothing can be moved until it does.</summary>
    public const string NoPassword = "no_password";
    public static IReadOnlyList<string> All { get; } = [Right, Wrong, Paced, NoPassword];
}

/// <summary>
/// The server's end of an account move, as the dispatcher serves it: the Linux server's own, or a client's proxy. A host that cannot take accounts (the Windows server, the app's
/// in-process host) leaves it null and every method answers <c>not_supported</c>.
/// </summary>
public interface IAccountMove
{
    Task<AccountMoveStatus> StatusAsync(AccountMoveStatusParams request, CancellationToken cancellationToken = default);
    Task<MovePiece> StageAsync(AccountMoveStageParams request, CancellationToken cancellationToken = default);
    Task<MoveHistoryPutResult> PutHistoryAsync(MoveHistoryPutParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveVerified> VerifyAsync(AccountMoveVerifyParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveActivated> ActivateAsync(AccountMoveActivateParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveStatus> AbortAsync(AccountMoveAbortParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveStatus> HoldAsync(AccountMoveHoldParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveChunk> ReadAsync(AccountMoveReadParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveHistoryPage> GetHistoryAsync(AccountMoveHistoryGetParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveStatus> ReleaseAsync(AccountMoveReleaseParams request, CancellationToken cancellationToken = default);
    Task<AccountMoveStatus> ResumeAsync(AccountMoveResumeParams request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether a provider may run on this host now: false while a move is being staged or the account has moved away. The host's account ports ask it before they connect, restore, turn
/// encryption on or sign in; a host with none runs every provider as before.
/// </summary>
public interface IAccountGate
{
    bool Allows(string provider);

    /// <summary>The provider's lifecycle lock, shared by account mutations, restores, administration and move file operations; null on a host with no such lock.</summary>
    SemaphoreSlim? LifecycleGate(string provider) => null;
}
