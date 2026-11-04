using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Core.Services;

/// <summary>
/// What Settings, the conversation header and the launch know of the Google Messages account, read without a call.
/// </summary>
/// <param name="PairingLost">The phone removed this computer's pairing; only pairing as a new device brings it back.</param>
/// <param name="SignInExpired">Google stopped accepting this computer's sign-in while the phone keeps the pairing; renewing fixes it.</param>
/// <param name="SignInHeld">An invisible refresh may still bring the sign-in back, so nothing says it needs renewing yet.</param>
/// <param name="SignInSettling">A renewal is under way, or has just finished and its reconnect has not.</param>
/// <param name="LastSignInFailure">Why the last sign-in or pairing attempt failed, as the helper's fixed code; null when it did not.</param>
/// <param name="DefaultSmsLine">Settings' line while the phone says Google Messages is not its default SMS app; null otherwise.</param>
/// <param name="HistoryIndexing">Older history is being downloaded into the search archive (the Settings toggle).</param>
public sealed record GoogleMessagesAccountState(bool PairingLost, bool SignInExpired, bool SignInHeld, bool SignInSettling,
    string? LastSignInFailure, string? DefaultSmsLine, bool HistoryIndexing);

/// <summary>One of a dual-SIM phone's SIMs as the composer's chip names it: "SIM 2 · Carrier", and its colour (#RRGGBB).</summary>
public sealed record GoogleSimChip(string Label, string? Color)
{
    /// <summary>What the chip's tooltip and screen readers say.</summary>
    public string Hint => $"Sends from {Label}. Change the SIM for this conversation on your phone.";
}

/// <summary>
/// One cookie of the Google sign-in browser, as that browser held it, so a host can rebuild a profile of its own (SWITCHBOARD.md 5.4). <paramref name="Domain"/> has no
/// leading dot; <paramref name="HostOnly"/> says whether the cookie belongs to that host alone (accounts.google.com's own, every <c>__Host-</c> cookie) or to the
/// domain and its subdomains. <paramref name="SameSite"/> is None, Lax or Strict, or null when the browser did not say; <paramref name="Expires"/> is Unix seconds,
/// or null for a session cookie.
/// <para>The value is a credential. <see cref="ToString"/> leaves it out, so a record that reaches a log or an exception message by mistake shows only a name.</para>
/// </summary>
public sealed record GoogleProfileCookie(string Name, string Value, string Domain, bool HostOnly, string Path, bool Secure, bool HttpOnly, string? SameSite = null, long? Expires = null)
{
    public override string ToString() => $"GoogleProfileCookie {{ Name = {Name} }}";
}

/// <summary>One person in a conversation this PC knows, as the local-data reset files their pictures and names.</summary>
public sealed record GoogleKnownPerson(string Id, string NativeId, bool Self);

/// <summary>A Google Messages conversation this PC knows, live or saved, by its own number (no "google-direct:" prefix).</summary>
public sealed record GoogleKnownChat(string Id, bool Group, IReadOnlyList<GoogleKnownPerson> Participants);

/// <summary>
/// The Google Messages account, for any client (SWITCHBOARD.md 2.6). The Google sign-in window and its WebView2 are the
/// client's: it hands the cookies it captured, and a progress sink for the pairing emoji, to <see cref="SignInAsync"/>, and
/// the host owns the session from then on. When to renew, and how, stays in the host's GoogleSignInKeeper.
/// </summary>
public interface IGoogleMessagesAccount
{
    /// <summary>The conversations were renumbered by the phone; the notice Google sends are refused with until a reset.</summary>
    const string RenumberedNotice = "Google Messages renumbered its conversations on your phone. Open Settings → Troubleshooting and reset Google Messages local data.";

    /// <summary>The account as it is now. Time passes in some of it (settling ends on its own), so read it again rather than keep it.</summary>
    GoogleMessagesAccountState State { get; }

    /// <summary>
    /// What the sign-in state says to the user may have changed: expired, held, released, cleared, lost or renewed. Raised
    /// from whatever thread noticed.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>The phone's SIMs changed. Raised off the interface thread.</summary>
    event EventHandler? SimsChanged;

    /// <summary>What is known of the phone's default SMS app changed. Raised off the interface thread.</summary>
    event EventHandler? DefaultSmsAppChanged;

    /// <summary>The renumbered state was first read, or changed. Raised on any thread; true when renumbered.</summary>
    event EventHandler<bool>? RenumberedChanged;

    /// <summary>
    /// Renews the saved pairing from a Google sign-in's cookies, or with <paramref name="asNewDevice"/> (or none saved) pairs
    /// this computer afresh; <paramref name="pairingEmoji"/> receives the emoji the phone asks to be matched. A failure
    /// throws <see cref="NativeBeeper.Core.Backend.BackendException"/>; <see cref="GoogleMessagesAccountState.LastSignInFailure"/> keeps its code.
    /// </summary>
    Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice = false);

    /// <summary>
    /// The same sign-in, also handing over every Google cookie the sign-in browser held (<see cref="GoogleProfileCookie"/>), for a host that keeps a browser
    /// profile of its own to renew from (SWITCHBOARD.md 5.4). A port that keeps no profile ignores them, which is what this default does: the pairing needs only
    /// <paramref name="cookies"/>. Only a client of a server sends any.
    /// </summary>
    Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice,
        IReadOnlyList<GoogleProfileCookie>? profileCookies) => SignInAsync(cookies, pairingEmoji, cancellationToken, asNewDevice);


    /// <summary>The SIM a conversation sends from, or null for a single-SIM phone, an unknown conversation or another service.</summary>
    GoogleSimChip? SimFor(string? chatId);

    /// <summary>Turns downloading older history into the search archive on or off; what is already saved stays searchable. <see cref="State"/> shows it when this returns.</summary>
    Task SetHistoryIndexingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Reads the saved renumbered state, so the window can say so at launch before any list read or send.</summary>
    Task LoadRenumberedAsync();

    /// <summary>Search is about to be used: the archive stops pacing its warm-up.</summary>
    void WarmArchive();

    /// <summary>Every conversation this PC knows, hidden ones excluded, for the merge picker. Reads nothing from the phone.</summary>
    IReadOnlyList<ChatSummary> KnownConversations { get; }

    /// <summary>Every conversation known, live or saved, hidden ones included: what a local-data reset must find before it deletes anything.</summary>
    Task<IReadOnlyList<GoogleKnownChat>> KnownChatsForResetAsync(CancellationToken cancellationToken = default);

    /// <summary>The cache keys under which Google's avatars (and their markers) are kept for these conversation or person numbers.</summary>
    Task<IReadOnlyList<string>> AvatarCacheKeysAsync(IReadOnlyList<string> nativeIds, CancellationToken cancellationToken = default);

    /// <summary>Forgets every avatar remembered in memory, after a reset: each was filed under a conversation number.</summary>
    Task ForgetAvatarsAsync(CancellationToken cancellationToken = default);

    /// <summary>Empties the search archive and what is held in memory, and the renumbering with it; the pairing and session are untouched.</summary>
    Task ResetLocalAsync(string profile);
}
