namespace MessageLink.Protocol;

// The server's own administration, for its page and for any paired device's reads: what the server is, the accounts it holds,
// and removing one of them from it, which is the page's alone on a server. Only on a connection that negotiated ProtocolVersion.ServerAdmin, and only by a server that has an IServerAdmin. The status and the list are reads; a removal is a journaled
// mutation, at most once per operation id and never retried by the server. The answers carry fixed words, counts and times, and the list carries the account
// names a client already shows in Settings (never logged on either end). Pairing devices, renaming and revoking them are devices.* (ProtocolMethods.Devices.cs).
public static partial class ProtocolMethods
{
    /// <summary>What the server is: its version, the protocol window it serves, when it started, its Chrome's sandbox and where its updates come from.</summary>
    public const string ServerStatusRead = "host.status";
    /// <summary>Every account the server holds, each with its state and whether it can be removed now.</summary>
    public const string ServerAccountsList = "host.accounts";
    /// <summary>Removes the server's copy of one account (<see cref="ServerAccountProviders"/>): its sign-in and what the server kept of it. Nothing on another device, and nothing at the service.</summary>
    public const string ServerAccountRemove = "host.removeAccount";

    private static Entry[] AdminEntries() =>
    [
        new(ServerStatusRead, "IServerAdmin.StatusAsync", null, typeof(ServerStatus), ProtocolVersion.ServerAdmin),
        new(ServerAccountsList, "IServerAdmin.AccountsAsync", null, typeof(IReadOnlyList<ServerAccount>), ProtocolVersion.ServerAdmin),
        new(ServerAccountRemove, "IServerAdmin.RemoveAccountAsync", typeof(AccountRemoveParams), typeof(AccountRemoved), ProtocolVersion.ServerAdmin, true),
    ];
}

/// <summary>
/// What a server says about itself to its owner. <paramref name="App"/> is the application version it was built from, <paramref name="Protocol"/> the newest protocol it
/// speaks and <paramref name="Supported"/> the oldest it keeps serving in full (<see cref="ProtocolVersion.Supported"/>). <paramref name="ChromeSandbox"/> is one of
/// <see cref="ChromeSandboxWords"/>, or null for a server that runs no Chrome. <paramref name="Channel"/> says where its image came from (<c>ghcr:&lt;variant&gt;</c>, <c>plain</c> or <c>google</c>, for the
/// image GitHub built, whichever tag the server follows; <c>local</c> for one built on the server), and <paramref name="Revision"/> the source commit; both are null when the
/// image did not say. <paramref name="Built"/> is when its image was built, when it says.
/// </summary>
public sealed record ServerStatus(string App, int Protocol, int Supported, DateTimeOffset Started, string? ChromeSandbox = null, string? Channel = null, string? Revision = null,
    DateTimeOffset? Built = null);

/// <summary>The words <see cref="ServerStatus.ChromeSandbox"/> takes: the server's own reading of its Chrome (<c>google chrome sandbox=</c> in its log), or <c>off</c> when it was started without one.</summary>
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
    /// <summary>Google Messages paired, or being paired, on this server.</summary>
    public const string Paired = "paired";
    public static IReadOnlyList<string> All { get; } = [Connected, Saved, Paired];
}

/// <summary>
/// One account a server holds. <paramref name="Key"/> names a Google Voice account among several (the one-way key its conversations carry); the others have one account each
/// and no key. <paramref name="Name"/> is what the person knows the account by (an Instagram name, a Voice number), where the server knows it. <paramref name="Removable"/> is
/// whether <c>host.removeAccount</c> takes it: Google Messages too since 2026-11-22, on a server that can unpair it (GoogleMessagesBackend.RemoveAsync).
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
    /// <summary>Google Messages: unpair it on the phone instead.</summary>
    public const string NotOffered = "remove_not_offered";
    public static IReadOnlyList<string> All { get; } = [Unknown, NotOffered];
}

/// <summary>
/// The server's side of the administration methods. Only the Linux server has one; every other server answers not supported. Who may ask is the dispatcher's business and the
/// page's; this port does what it is asked.
/// </summary>
public interface IServerAdmin
{
    Task<ServerStatus> StatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default);
}
