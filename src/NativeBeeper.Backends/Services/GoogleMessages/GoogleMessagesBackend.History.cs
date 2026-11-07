using NativeBeeper.Core.Backend;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

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
        var filters = new NativeBeeper.Core.Services.SearchFilters(From: DateOnly.FromDateTime(since.LocalDateTime), Through: DateOnly.FromDateTime(until.LocalDateTime));
        var hits = await Task.Run(() => archive.Search("", filters: filters, cancellationToken: cancellationToken)
            .Where(hit => hit.At >= since && hit.At < until).Take(limit).ToArray(), cancellationToken);
        // Only conversations search would show: the archive still holds ones the user deleted (KnownChats leaves them out).
        var known = KnownChats();
        var items = new List<ConsoleHistoryItem>(hits.Length);
        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.TryGetValue(hit.ChatId, out var chat)) continue;
            if (await archive.ReadMessageAsync(account, hit.MessageId, cancellationToken) is not { } message) continue;
            items.Add(new(Summary(chat), Models.Map(message, chat)));
        }
        return items;
    }
}
