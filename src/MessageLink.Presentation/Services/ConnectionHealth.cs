using System.Collections.Concurrent;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

public sealed class ConnectionHealth
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> sends = new(StringComparer.Ordinal);
    public DateTimeOffset? LastEvent(string scope) => events.TryGetValue(scope, out var at) ? at : null;
    public DateTimeOffset? LastConfirmedSend(string scope) => sends.TryGetValue(scope, out var at) ? at : null;
    /// <summary>
    /// Raised with a service's connection scope each time it reports itself connected, on the feed's thread: what was set
    /// aside when it was signed out (reminders handed to Windows) is taken up again. A handler must be quick and not throw.
    /// </summary>
    public event Action<string>? Connected;
    public void Observe(RealtimeEvent change)
    {
        var scope = ServiceScopes.Scope(change.ChatId ?? change.Scope);
        if (scope is not null) events[scope] = DateTimeOffset.UtcNow;
        // A listener's failure must not reach the feed, which would take every service's events down with it.
        if (change.Kind == RealtimeKind.Connected && scope is not null)
        {
            try { Connected?.Invoke(scope); }
            catch (Exception) { /* The listener's own business; the event still goes on. */ }
        }
    }
    public void Confirmed(ChatMessage message)
    {
        if (message.IsOutgoing && message.Delivery.SendState is "SUCCESS" or "SENT" or "DELIVERED" or "READ" && ServiceScopes.Scope(message.ChatId) is { } scope)
            sends[scope] = DateTimeOffset.UtcNow;
    }
}

public sealed class ConnectionHealthFeed(IRealtimeFeed inner, ConnectionHealth health) : IRealtimeFeed
{
    public Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken) =>
        inner.RunAsync((change, token) => { health.Observe(change); return receive(change, token); }, cancellationToken);
}
