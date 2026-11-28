namespace MessageLink.Core.Services;

/// <summary>One account's saved-message scope: the opaque string saved messages, reminders and scheduled sends are keyed by, under the account's id.</summary>
/// <param name="AccountId">The conversation account the scope belongs to (<c>GoogleModels.Account</c>, <c>MessengerModels.Account</c>, a Voice account's, Instagram's).</param>
/// <param name="Scope">A one-way derived value ("google:&lt;hash&gt;", "messenger:&lt;hash&gt;") or the account's own key; never a raw id, number or credential.</param>
public sealed record AccountScope(string AccountId, string Scope);

/// <summary>
/// What a client needs of the accounts' identities to draw and to key what it keeps, and cannot read from its own disk once the backends
/// live in another process (SWITCHBOARD.md 3.0b).
/// </summary>
/// <param name="GooglePaired">Google Messages has a saved pairing on this host (the static file check the window made).</param>
/// <param name="LocalKey">
/// The name everything this PC keeps locally is filed under (pins, mutes, drafts, read progress, and the rest), which
/// <c>NotificationIdentity.ProfileAsync</c> hashes into the profile the window uses. Itself already a one-way hash of the paired device's
/// identity (or the fixed unpaired name); null with nothing signed in anywhere. Never logged.
/// </param>
/// <param name="SavedScopes">One entry per account whose saved messages may be shown now, sorted by account id.</param>
public sealed record HostIdentity(bool GooglePaired, string? LocalKey, IReadOnlyList<AccountScope> SavedScopes)
{
    /// <summary>What is known before anything is: nothing paired, no key, no account. The conservative answer.</summary>
    public static HostIdentity Unknown { get; } = new(false, null, []);
}

/// <summary>
/// The accounts' identities, for any client. In process it is computed from the backends and the Google session files; across the
/// protocol it is read from the host's pushed state, with the last state known kept protected so a first frame never waits on the host.
/// </summary>
public interface IHostIdentity
{
    /// <summary>Google Messages is paired, as the last answer says; read without a call.</summary>
    bool GooglePaired { get; }

    /// <summary>The current identity. In process this reads the host's files; across the protocol it answers from the last state, awaiting only this PC's own memory of it.</summary>
    Task<HostIdentity> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A sign-in finished or a service was added or removed here: the identity is read again now rather than at the host's next look. In process
    /// this drops what the Google profile had kept; across the protocol it asks the host to, and returns once this client's answer has caught up
    /// (or a short wait has passed). A host that follows its own files also does this by itself, so a failure here only costs the wait.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>The identity this client answers with changed, or was first heard. Raised on any thread. Never raised by the in-process port, which the window refreshes itself.</summary>
    event EventHandler? Changed;
}
