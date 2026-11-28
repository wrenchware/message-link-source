namespace MessageLink.Core.Services;

public enum RealtimeKind { Connected, Disconnected, AuthenticationRequired, ResyncRequired, ChatChanged, ChatDeleted, MessageChanged, MessageDeleted }
// ReceivedAt: when the backend received the event, used only to measure notification delay.
public sealed record RealtimeEvent(RealtimeKind Kind, string? ChatId = null, IReadOnlyList<string>? Ids = null, bool? IsNewMessage = null, DateTimeOffset? ReceivedAt = null,
    // Conversation-id prefix owned by the connection. Voice uses its account prefix; an unscoped event is global.
    string? Scope = null,
    // Only who has received or read the named messages moved, not what they say. Set by a provider that can tell the
    // two apart; a change without it may be anything.
    bool ReceiptOnly = false,
    // On AuthenticationRequired: only the sign-in was refused and the pairing stands, so renewing it is enough (Google
    // Messages). Decides what the chat console says when the event arrives.
    bool SignInOnly = false,
    // A fetched history snapshot, including its reactions and receipts, rather than a live change. It refreshes the
    // local baseline without announcing old state as new activity. Providers set this only when origin is known.
    bool IsHistory = false,
    // On Disconnected: the client lost its connection to the host that serves its feed (RealtimeFeedProxy), not a provider its connection to its service. The host
    // and its providers may have run all the while, and a resumed feed replays what the client missed, so a reader that treats a provider's disconnect as "what
    // follows may be backfill" (NotificationObserver) must not treat this one so. Set only by the proxy, never by a host, so it is not on the wire.
    bool? ClientLink = null)
{
    public bool Affects(string chatId) => ChatId == chatId ||
        Kind is RealtimeKind.ChatChanged or RealtimeKind.ChatDeleted && Ids?.Contains(chatId) == true ||
        Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired or RealtimeKind.ResyncRequired &&
        (Scope is null || chatId.StartsWith(Scope, StringComparison.Ordinal));
}

// Availability is authoritative per conversation; one provider's outage must not gate another provider's composer.
public interface IProviderConnectionState
{
    bool AnyConnected { get; }
    bool IsConnected(string chatId);
    // Exact provider/account prefix from a realtime event, not a conversation id.
    bool IsScopeConnected(string scope);
    bool IsConfigured(string scope) => true;
    // The service refused only its sign-in and keeps its pairing, so renewing the sign-in brings it back (Google Messages).
    bool NeedsSignInOnly(string scope) => false;
}
public interface IRealtimeFeed
{
    Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken);
}
