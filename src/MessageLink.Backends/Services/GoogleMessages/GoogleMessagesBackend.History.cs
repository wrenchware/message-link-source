using MessageLink.Core.Backend;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.GoogleMessages;

// The chat console's history for Google Messages: every message of a span, from the search archive this PC keeps, so it
// asks the phone for nothing. The archive holds every conversation the crawl and the live connection have seen, which
// the conversations' saved pages do not, so for Google it answers the whole span by itself.
internal sealed partial class GoogleMessagesBackend : IRecentMessages
{
    public string Scope => Models.Qualify("");

    public async Task<IReadOnlyList<ConsoleHistoryItem>> RecentAsync(DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken)
    {
        // Not loaded yet, or another account's: the console falls back to each conversation's saved page.
        if (!archive.IsLoaded || archive.Profile is not { } account) throw new BackendException(BackendFailure.NotReady, "The Google Messages archive is still loading.");
        // Dates first, which is all the archive filters by; then the exact span. Newest first. Off the interface thread, as
        // search runs it: it walks every document the archive holds, under the archive's lock.
        var filters = new MessageLink.Core.Services.SearchFilters(From: DateOnly.FromDateTime(since.LocalDateTime), Through: DateOnly.FromDateTime(until.LocalDateTime));
        // Only conversations search would show: the archive still holds ones the user deleted (KnownChats leaves them out).
        // Left out before the limit is counted, and the limit counts what is kept: taken first, a deleted conversation's
        // newest messages filled the limit and the span came back short or empty.
        var known = KnownChats();
        var hits = await Task.Run(() => archive.Search("", filters: filters, cancellationToken: cancellationToken)
            .Where(hit => hit.At >= since && hit.At < until && known.ContainsKey(hit.ChatId)).ToArray(), cancellationToken);
        var items = new List<ConsoleHistoryItem>(Math.Min(hits.Length, Math.Max(limit, 0)));
        foreach (var hit in hits)
        {
            if (items.Count >= limit) break;
            cancellationToken.ThrowIfCancellationRequested();
            var chat = known[hit.ChatId];
            if (await archive.ReadMessageAsync(account, hit.MessageId, cancellationToken) is not { } message) continue;
            items.Add(new(Summary(chat), Models.Map(message, chat)));
        }
        return items;
    }
}
