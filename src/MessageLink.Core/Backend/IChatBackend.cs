namespace MessageLink.Core.Backend;

public interface IChatBackend
{
    Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
}

public sealed record BackendInfo(string Name, string Version, bool HasEventEndpoint);
public sealed record ChatAccount(string Id, string Network, string DisplayName, string Status,
    string BridgeId, string Provider);

public enum BackendFailure { Unavailable, AuthenticationRequired, Forbidden, Unsupported, RateLimited, InvalidResponse, ServerError,
    // The provider is still connecting and will announce when it is ready: repeat the request rather than report it.
    // Appended so existing values keep their numbers; only providers with a readiness event may use it.
    NotReady }

// Safe to display. Never include response bodies or credentials in exception messages.
public sealed class BackendException(BackendFailure failure, string message) : Exception(message)
{
    public BackendFailure Failure { get; } = failure;
    // The provider is putting this right by itself and says so when it has (a sign-in being renewed invisibly). Waited
    // out like NotReady wherever a notice would otherwise appear and vanish; the Failure is kept for everything else.
    public bool Transient { get; init; }
    // Whether to wait and try again rather than report it: still connecting, or putting itself right.
    public bool Waits => Failure == BackendFailure.NotReady || Transient;
}
