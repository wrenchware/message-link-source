using System.Collections.Concurrent;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The 2.6d methods of the host (ProtocolMethods.Reading.cs): mark read and typing out with the client's admission, link previews, and a
// history window with the call that waits for its refresh. None is journaled; see ProtocolMethods.Reading.cs for why.
public sealed partial class ProtocolDispatcher
{
    /// <summary>The most messages one <see cref="ProtocolMethods.MessagesLoadLinkPreviews"/> names (the app asks for the newest 200).</summary>
    public const int MaxLinkPreviewIds = 512;

    /// <summary>The most rows one side of a <see cref="ProtocolMethods.HistoryWindow"/> may ask for (the app asks for a few dozen).</summary>
    public const int MaxWindowRows = 1000;

    /// <summary>The longest scope <see cref="ProtocolMethods.HostRestartHelper"/> names (the longest real one is a Voice account's, about 60).</summary>
    public const int MaxRestartScope = 128;

    /// <summary>The longest token a <see cref="ProtocolMethods.HostRestartHelper"/> gives back (the host's are two numbers and a dot).</summary>
    public const int MaxRestartToken = 64;

    /// <summary>The most a <see cref="ProtocolMethods.HistoryWindowRefresh"/> waits for the fresher rows before it answers none.</summary>
    public static TimeSpan RefreshWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How many refreshes a connection holds for a client that has not asked for them yet, and for how long.</summary>
    public const int MaxHeldRefreshes = 64;
    public static TimeSpan RefreshLifetime { get; set; } = TimeSpan.FromMinutes(10);

    private readonly PendingRefreshes refreshes = new();

    private static Dictionary<string, Route> AddReadingRoutes(Dictionary<string, Route> routes)
    {
        // The read marker, and whether the client's gate let it through. A port that cannot take the admission is called plain, which is
        // unadmitted: a host that wants a private-reading conversation's read to reach its service on a client's say-so wraps its router
        // in PrivateReadingHostReadState.
        routes[ProtocolMethods.ChatsMarkRead] = Of<IReadStateWriter, MarkReadParams, Done>(s => s.ReadState, async (port, p, ct) =>
        {
            if (port is IAdmittedReadStateWriter admitting) await admitting.MarkReadAsync(p.ChatId, p.MessageId, p.Admitted, p.Explicit, ct).ConfigureAwait(false);
            else await port.MarkReadAsync(p.ChatId, p.MessageId, ct).ConfigureAwait(false);
            return new Done();
        });
        routes[ProtocolMethods.TypingSet] = Of<ITypingService, TypingSetParams, Done>(s => s.Typing, async (port, p, ct) =>
        {
            if (port is IAdmittedTypingService admitting) await admitting.SetTypingAsync(p.ChatId, p.IsTyping, p.Admitted, ct).ConfigureAwait(false);
            else await port.SetTypingAsync(p.ChatId, p.IsTyping, ct).ConfigureAwait(false);
            return new Done();
        });
        // Neither admitted nor refused: an edit only moves a typing stop that is already pending and starts nothing (IComposerActivitySink).
        routes[ProtocolMethods.TypingNoteActivity] = Of<IComposerActivitySink, ChatIdParams, Done>(s => s.ComposerActivity, (port, p, _) =>
        {
            port.NoteComposerActivity(p.ChatId);
            return Task.FromResult(new Done());
        });
        routes[ProtocolMethods.MessagesLoadLinkPreviews] = Of<ILinkPreviewLoader, LinkPreviewsParams, Done>(s => s.Chats as ILinkPreviewLoader, async (port, p, ct) =>
        {
            if (p.MessageIds.Count > MaxLinkPreviewIds || p.MessageIds.Any(id => id is null)) throw Invalid();
            await port.LoadLinkPreviewsAsync(p.ChatId, p.MessageIds, ct).ConfigureAwait(false);
            return new Done();
        });
        // Not journaled (see HelperRestartParams); every refusal and failure is the answer's own outcome and code word, so the host's log line
        // (HelperRestarts) and the window's sentence are the same whoever asked. The flag that says the caller is signing in must be stated.
        routes[ProtocolMethods.HostRestartHelper] = new(typeof(HelperRestartParams), typeof(HelperRestartResult), async (services, _, request, _) =>
        {
            var parameters = ReadParams<HelperRestartParams>(request);
            if (!ParamsShape.HasRequiredValues(typeof(HelperRestartParams), request.Params) || parameters.Scope.Length is 0 or > MaxRestartScope || parameters.Token?.Length > MaxRestartToken) throw Invalid();
            var port = services.HelperRestarts ?? throw new NotSupportedException();
            return JsonSerializer.SerializeToUtf8Bytes(await port.RestartAsync(parameters.Scope, parameters.SigningIn, parameters.Force, parameters.Token, parameters.Settle).ConfigureAwait(false), ProtocolJson.Options);
        });
        routes[ProtocolMethods.HistoryWindow] = new(typeof(HistoryWindowParams), typeof(HistoryWindowReply), null, Own: (connection, request, _, ct) => connection.ReadWindowAsync(request, ct));
        routes[ProtocolMethods.HistoryWindowRefresh] = new(typeof(WindowRefreshParams), typeof(WindowRefreshed), null, Own: (connection, request, _, ct) => connection.RefreshWindowAsync(request, ct));
        return routes;
    }

    private async Task<byte[]> ReadWindowAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<HistoryWindowParams>(request);
        if (!ParamsShape.HasRequiredValues(typeof(HistoryWindowParams), request.Params) || parameters.Older is < 0 or > MaxWindowRows || parameters.Newer is < 0 or > MaxWindowRows)
            throw Invalid();
        var port = services.History ?? throw new NotSupportedException();
        var window = await port.ReadWindowAsync(parameters.ChatId, parameters.At, parameters.Older, parameters.Newer, cancellationToken).ConfigureAwait(false);
        var token = window.Refreshed is { } refreshing ? refreshes.Hold(refreshing) : null;
        return JsonSerializer.SerializeToUtf8Bytes(new HistoryWindowReply(window.Messages, window.MoreOlder, window.MoreNewer, token), ProtocolJson.Options);
    }

    private async Task<byte[]> RefreshWindowAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<WindowRefreshParams>(request);
        IReadOnlyList<ChatMessage> rows = [];
        // A token is spent by the first call that names it, whatever becomes of that call.
        if (refreshes.Claim(parameters.Refresh) is { } refreshing)
        {
            try { rows = await refreshing.WaitAsync(RefreshWait, cancellationToken).ConfigureAwait(false); }
            // The fresher rows did not come in time: the window stands as it was drawn.
            catch (TimeoutException) { }
        }
        return JsonSerializer.SerializeToUtf8Bytes(new WindowRefreshed(rows), ProtocolJson.Options);
    }

    /// <summary>
    /// The refreshes a window's reader has been promised, by token. A token is unguessable, is spent when claimed, and goes unclaimed
    /// only when the client never asks, so the table is bounded: the oldest are dropped past <see cref="MaxHeldRefreshes"/> and
    /// the ones older than <see cref="RefreshLifetime"/> are dropped when the next is held.
    /// </summary>
    private sealed class PendingRefreshes
    {
        private readonly object gate = new();
        // `Order` is the order they were held in: the clock can tie across a burst, and the oldest is the one to drop.
        private readonly Dictionary<string, (Task<IReadOnlyList<ChatMessage>> Task, long At, long Order)> held = new(StringComparer.Ordinal);
        private long order;

        public string Hold(Task<IReadOnlyList<ChatMessage>> refreshing)
        {
            // Whoever never asks never sees a failure either; it is not an unobserved one.
            _ = refreshing.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var token = Guid.NewGuid().ToString("N");
            var now = Environment.TickCount64;
            lock (gate)
            {
                foreach (var old in held.Where(entry => now - entry.Value.At > (long)RefreshLifetime.TotalMilliseconds).Select(entry => entry.Key).ToArray()) held.Remove(old);
                while (held.Count >= MaxHeldRefreshes) held.Remove(held.MinBy(entry => entry.Value.Order).Key);
                held[token] = (refreshing, now, ++order);
            }
            return token;
        }

        public Task<IReadOnlyList<ChatMessage>>? Claim(string token)
        {
            lock (gate) return held.Remove(token, out var found) ? found.Task : null;
        }
    }
}
