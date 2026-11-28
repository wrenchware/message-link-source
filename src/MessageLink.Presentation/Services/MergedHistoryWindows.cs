using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Windows around a moment for a merged conversation: the window of each member that keeps its history on this PC, read
/// together, put in one order and cut to the size asked for. A member that does not (Instagram and Google Voice, whose
/// history is only what the service sends) has nothing to read around a moment, so its messages from then are not in the
/// window; they are there again once the reader is back in the conversation's own history. Voice calls come from their
/// previews, as the merged reader takes them. A result from a member that cannot be windowed is not in the window, and
/// the caller pages back to it as before. Anything else goes straight to the source.
/// </summary>
public sealed class MergedHistoryWindows(IHistoryWindowReader source, MergedConversations merges, Action<string>? log = null) : IHistoryWindowReader
{
    public bool CanReadWindow(string chatId) => !MergedConversations.IsMerged(chatId) ? source.CanReadWindow(chatId)
        : Readable(merges.Members(chatId)).Any(member => source.CanReadWindow(member.Id));

    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        if (!MergedConversations.IsMerged(chatId)) return await source.ReadWindowAsync(chatId, at, older, newer, cancellationToken);
        await merges.EnsureLoadedAsync();
        var members = merges.Members(chatId);
        var kept = Readable(members).Where(member => source.CanReadWindow(member.Id)).ToList();
        log?.Invoke($"history window merged members={Readable(members).Count} kept={kept.Count} services={string.Join("|", kept.Select(m => m.Network.ToLowerInvariant().Replace(' ', '-')))}");
        var windows = await Task.WhenAll(kept.Select(member => source.ReadWindowAsync(member.Id, at, older, newer, cancellationToken)));
        var calls = members.Where(MergedConversations.IsCall).Select(member => member.Preview).OfType<ChatMessage>();
        var all = windows.SelectMany(window => window.Messages).Concat(calls)
            .DistinctBy(m => MessageIdentity.Key(m))
            .OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).ThenBy(m => MessageIdentity.Key(m), StringComparer.Ordinal)
            .ToArray();
        var cut = Cut(all, at, older, newer);
        var refreshing = windows.Select(window => window.Refreshed).OfType<Task<IReadOnlyList<ChatMessage>>>().ToArray();
        return new(cut.Messages,
            cut.MoreOlder || windows.Any(window => window.MoreOlder),
            cut.MoreNewer || windows.Any(window => window.MoreNewer))
        { Refreshed = refreshing.Length == 0 ? null : AllRefreshed(refreshing) };
    }

    // A merge is asked member by member (ConversationViewModel), each from where its own rows in the saved copy end.
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default) =>
        MergedConversations.IsMerged(chatId) ? Task.FromResult<HeldHistory?>(null) : source.ReadHeldAsync(chatId, after, limit, cancellationToken);

    // Every member's fresher rows together; one member's failure leaves the others'.
    private static async Task<IReadOnlyList<ChatMessage>> AllRefreshed(Task<IReadOnlyList<ChatMessage>>[] refreshing)
    {
        try { await Task.WhenAll(refreshing); } catch (Exception) { }
        return [.. refreshing.Where(task => task.IsCompletedSuccessfully).SelectMany(task => task.Result)];
    }

    // The members whose history is read as pages; call records are not.
    private static List<ChatSummary> Readable(IReadOnlyList<ChatSummary> members) => members.Where(member => !MergedConversations.IsCall(member)).ToList();

    /// <summary>
    /// Up to <paramref name="older"/> before <paramref name="at"/> and <paramref name="newer"/> from it, of rows already in
    /// order, never splitting one moment at either end; and whether anything was left out at each end.
    /// </summary>
    public static (ChatMessage[] Messages, bool MoreOlder, bool MoreNewer) Cut(IReadOnlyList<ChatMessage> ordered, DateTimeOffset at, int older, int newer)
    {
        var split = 0;
        while (split < ordered.Count && ordered[split].Timestamp < at) split++;
        var start = Math.Max(0, split - older);
        while (start > 0 && start < split && ordered[start - 1].Timestamp == ordered[start].Timestamp) start--;
        var end = Math.Min(ordered.Count, split + newer);
        while (end > split && end < ordered.Count && ordered[end].Timestamp == ordered[end - 1].Timestamp) end++;
        return (ordered.Skip(start).Take(end - start).ToArray(), start > 0, end < ordered.Count);
    }
}
