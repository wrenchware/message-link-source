namespace NativeBeeper.Protocol;

// The server's own administration (SWITCHBOARD.md 4.6), for its page and for any paired device's reads: what the server is, the accounts it holds,
// and removing one of them from it, which is the page's alone on a server. Only on a connection that negotiated ProtocolVersion.ServerAdmin, and only by a host that has an IServerAdmin. The status and the list are reads; a removal is a journaled
// mutation, at most once per operation id and never retried by the host. The answers carry fixed words, counts and times, and the list carries the account
// names a client already shows in Settings (never logged on either end). Pairing devices, renaming and revoking them are devices.* (ProtocolMethods.Devices.cs).
public static partial class ProtocolMethods
{
    /// <summary>What the server is: its version, the protocol window it serves, when it started, its Chrome's sandbox and where its updates come from.</summary>
    public const string HostStatusRead = "host.status";
    /// <summary>Every account the server holds, each with its state and whether it can be removed now.</summary>
    public const string HostAccountsList = "host.accounts";
    /// <summary>Removes the server's copy of one account (<see cref="ServerAccountProviders"/>): its sign-in and what the server kept of it. Nothing on another device, and nothing at the service.</summary>
    public const string HostAccountRemove = "host.removeAccount";

    private static Entry[] AdminEntries() =>
    [
        new(HostStatusRead, "IServerAdmin.StatusAsync", null, typeof(HostStatus)),
        new(HostAccountsList, "IServerAdmin.AccountsAsync", null, typeof(IReadOnlyList<ServerAccount>)),
        new(HostAccountRemove, "IServerAdmin.RemoveAccountAsync", typeof(AccountRemoveParams), typeof(AccountRemoved), true),
    ];
}

/// <summary>
/// What a server says about itself to its owner. <paramref name="App"/> is the application version it was built from, <paramref name="Protocol"/> the newest protocol it
/// speaks and <paramref name="Supported"/> the oldest it keeps serving in full (<see cref="ProtocolVersion.Supported"/>). <paramref name="ChromeSandbox"/> is one of
/// <see cref="ChromeSandboxWords"/>, or null for a server that runs no Chrome. <paramref name="Channel"/> says where its image came from (<c>ghcr:&lt;variant&gt;</c>, <c>plain</c> or <c>google</c>, for the
/// image GitHub built, whichever tag the server follows; <c>local</c> for one built on the server), and <paramref name="Revision"/> the source commit; both are null when the
/// image did not say. <paramref name="Built"/> is when its image was built, when it says.
/// </summary>
public sealed record HostStatus(string App, int Protocol, int Supported, DateTimeOffset Started, string? ChromeSandbox = null, string? Channel = null, string? Revision = null,
    DateTimeOffset? Built = null);

/// <summary>The words <see cref="HostStatus.ChromeSandbox"/> takes: the server's own reading of its Chrome (<c>google chrome sandbox=</c> in its log), or <c>off</c> when it was started without one.</summary>
public static class ChromeSandboxWords
{
    public const string Active = "active", Inactive = "inactive", Unknown = "unknown", Off = "off";
    public static IReadOnlyList<string> All { get; } = [Active, Inactive, Unknown, Off];
}

/// <summary>The services as <see cref="ServerAccount.Provider"/> and <see cref="AccountRemoveParams.Provider"/> name them.</summary>
public static class ServerAccountProviders
{
    public const string GoogleMessages = "google-messages", Messenger = "messenger", Instagram = "instagram", GoogleVoice = "google-voice";
    public static IReadOnlyList<string> All { get; } = [GoogleMessages, Messenger, Instagram, GoogleVoice];
}

/// <summary>The words <see cref="ServerAccount.State"/> takes.</summary>
public static class ServerAccountStates
{
    /// <summary>Signed in and connected now.</summary>
    public const string Connected = "connected";
    /// <summary>Signed in (a sign-in is kept) but not connected now: reconnecting, refused, or waiting for a new sign-in.</summary>
    public const string Saved = "saved";
    /// <summary>Moved back to a PC: the server holds a copy it will not sign in until the account moves here again.</summary>
    public const string MovedAway = "moved_away";
    /// <summary>Part of a move to this server that has not finished.</summary>
    public const string Moving = "moving";
    /// <summary>Google Messages paired, or being paired, on this server.</summary>
    public const string Paired = "paired";
    public static IReadOnlyList<string> All { get; } = [Connected, Saved, MovedAway, Moving, Paired];
}

/// <summary>
/// One account a server holds. <paramref name="Key"/> names a Google Voice account among several (the one-way key its conversations carry); the others have one account each
/// and no key. <paramref name="Name"/> is what the person knows the account by (an Instagram name, a Voice number), where the server knows it. <paramref name="Removable"/> is
/// whether <c>host.removeAccount</c> takes it now: not while it is part of a move, and never Google Messages, which is removed by unpairing it on the phone.
/// </summary>
public sealed record ServerAccount(string Provider, string State, bool Removable, string? Key = null, string? Name = null);

/// <summary>Which account to remove: a provider from <see cref="ServerAccountProviders"/>, and for Google Voice the account's <paramref name="Key"/>.</summary>
public sealed record AccountRemoveParams(string ClientOpId, string Provider, string? Key = null) : MutationParams(ClientOpId);

/// <summary>Whether anything was removed: false when the server held no such account (already removed, or never there).</summary>
public sealed record AccountRemoved(bool Removed);

/// <summary>The fixed words a removal is refused with (as <c>account_failed</c>), never a sentence: the client words them itself.</summary>
public static class AccountRemoveWords
{
    /// <summary>Not a provider this server knows, or a Voice removal with no key.</summary>
    public const string Unknown = "remove_unknown";
    /// <summary>The account is part of a move (staged here, or moved back to a PC and held): what is here belongs to that move.</summary>
    public const string Moving = "remove_moving";
    /// <summary>Google Messages: unpair it on the phone instead.</summary>
    public const string NotOffered = "remove_not_offered";
    public static IReadOnlyList<string> All { get; } = [Unknown, Moving, NotOffered];
}

/// <summary>
/// The host's side of the administration methods. Only the Linux server has one; every other host answers not supported. Who may ask is the dispatcher's business and the
/// page's; this port does what it is asked.
/// </summary>
public interface IServerAdmin
{
    Task<HostStatus> StatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default);
}
