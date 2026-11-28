using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

/// <summary>
/// A conversation's history read around a moment rather than paged back to it: how a search result further back than a
/// conversation holds is shown, and how the reader then moves away from it in either direction. Only what is already on
/// this PC is read (a provider's kept store or archive), never older history asked of the service, so it costs the same
/// at any depth.
/// </summary>
public interface IHistoryWindowReader
{
    /// <summary>Whether this conversation's history can be read around a moment here. False means page back as before.</summary>
    bool CanReadWindow(string chatId);

    /// <summary>
    /// Up to <paramref name="older"/> messages strictly before <paramref name="at"/> and up to <paramref name="newer"/> at
    /// or after it, oldest first. Neither end splits one moment, so a page can hold a few more than asked. Paging older
    /// from a window asks with its oldest message's time and newer = 0; paging newer asks with just past its newest
    /// message's time and older = 0.
    /// </summary>
    Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest messages after <paramref name="after"/> (at most <paramref name="limit"/>, oldest first), from what is
    /// kept on this PC, for drawing an opening conversation before the service's own read comes back. Null unless what is
    /// kept is known to hold every message up to now and to draw each as the service's read would; then the caller waits
    /// for that read, as before.
    /// </summary>
    Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<HeldHistory?>(null);
}

/// <param name="Complete">Every message after the moment asked for is here, not just the newest of them.</param>
public sealed record HeldHistory(IReadOnlyList<ChatMessage> Messages, bool Complete);

/// <param name="MoreOlder">Something lies before the first message.</param>
/// <param name="MoreNewer">Something lies after the last message.</param>
public sealed record HistoryWindow(IReadOnlyList<ChatMessage> Messages, bool MoreOlder, bool MoreNewer)
{
    /// <summary>
    /// Fresher copies of some of these messages, on their way from the service (a kept copy can lack what a photo needs to
    /// be fetched), or null when there is nothing to wait for. The window is drawn at once and these replace their rows
    /// when they come.
    /// </summary>
    public Task<IReadOnlyList<ChatMessage>>? Refreshed { get; init; }
}
