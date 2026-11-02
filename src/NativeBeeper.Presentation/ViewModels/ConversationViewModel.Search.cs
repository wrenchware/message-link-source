using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    // How many older pages a search result may read on its way back to its message.
    internal const int SearchResultPages = 50;
    public const string SearchResultTooOld = "This message is older than the newest 1,000 messages a conversation shows here, so it can't be scrolled to.";
    public const string SearchResultTooFar = "This message is further back than search scrolls to. Scroll up to load earlier messages.";

    public async Task<bool> OpenSearchResultAsync(SearchHit hit)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        windowSkipped = "not_needed";
        await OpenAsync(hit.Chat);
        if (hit.Message is null) return ContainsChat(hit.Chat.Id);
        var version = generation;
        var opened = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Contains(hit.Message)) { TimeSearchOpen("loaded", 0); return true; }
        // Older than anything loaded, where its history is kept on this PC: read around it, at any depth, in one read.
        if (version == generation && ContainsChat(hit.Chat.Id) && !ReachesBack(hit.Message) &&
            await OpenWindowAsync(hit.Message, version) is { } around && Contains(hit.Message))
        { TimeSearchOpen(around, 0); return true; }
        var page = version == generation && ContainsChat(hit.Chat.Id) ? await ReadBackToAsync(hit.Message, version) : 0;
        if (Contains(hit.Message)) { TimeSearchOpen("paged", page); return true; }
        // Said once, plainly, and as what it is. Search now finds Messenger's whole kept history, much of which is further
        // back than a conversation holds (ConversationCache.MaxMessages): that message exists, it is only out of reach.
        if (version == generation)
        {
            var (result, text) = current?.AtLimit == true && !ReachesBack(hit.Message) ? ("too_old", SearchResultTooOld)
                : page >= SearchResultPages && HasMore ? ("too_far", SearchResultTooFar)
                : ("gone", "This result is no longer available in the loaded history.");
            Status = text;
            TimeSearchOpen(result, page);
        }
        return false;

        bool Contains(ChatMessage message) => Messages.Any(m => m.Message.Id == message.Id && m.Message.ChatId == message.ChatId);
        // How far back the message was and what reaching it cost: the open, each older page read in series, and the rows it
        // left loaded. Counts and timings only.
        void TimeSearchOpen(string result, int pages)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var age = DateTimeOffset.UtcNow - hit.Message.Timestamp;
            Timed?.Invoke(this, $"history search_open result={result} network={hit.Chat.Network.ToLowerInvariant()} pages={pages} " +
                $"open_ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started, opened).TotalMilliseconds} " +
                $"paging_ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(opened, now).TotalMilliseconds} rows={Messages.Count} " +
                $"age_days={Math.Max(0, (int)age.TotalDays)} window={windowSkipped ?? "used"}{MemberDetail(hit.Message)}");
        }
    }

    // Why the last search open did not use a window, for its log line; null when it did. Fixed words only.
    private string? windowSkipped;

    // For a merged conversation, what the loaded history holds of the result's own service: its rows, how far back its
    // oldest goes, whether that service stopped answering, and whether a row with the result's time or id is there under
    // another identity. Counts and flags only.
    private string MemberDetail(ChatMessage target)
    {
        if (!IsMerged || current is null) return "";
        var member = current.Messages.Where(m => m.ChatId == target.ChatId).ToArray();
        var oldest = member.Length == 0 ? -1 : Math.Max(0, (int)(DateTimeOffset.UtcNow - member.Min(m => m.Timestamp)).TotalDays);
        var unavailable = current.UnavailableSources?.Contains(target.ChatId) == true;
        var sameTime = current.Messages.Count(m => m.ChatId == target.ChatId && Math.Abs((m.Timestamp - target.Timestamp).TotalSeconds) < 2);
        var idElsewhere = current.Messages.Any(m => m.Id == target.Id && m.ChatId != target.ChatId);
        return $" member={SourceChat(target.ChatId).Network.ToLowerInvariant().Replace(' ', '-')} member_rows={member.Length} " +
            $"member_oldest_days={oldest} member_unavailable={unavailable} same_time={sameTime} id_elsewhere={idElsewhere}";
    }

    // Reads older pages until the message is in the history, and one page more for what led up to it, then draws once.
    // Drawing each page as it came put every one of them on screen and moved the rows under the reader each time; the
    // history is drawn here only when it holds the message, and the view puts it in place before showing it. The extra
    // page is kept only while the message is still within the newest ConversationCache.MaxMessages.
    private async Task<int> ReadBackToAsync(ChatMessage target, long version)
    {
        if (current is null || selection is null || !HasMore || IsBusy) return 0;
        var token = selection.Token;
        IsBusy = true; IsLoadingOlder = true; OlderLoadFailed = false; Advisory("Loading earlier messages…");
        var pages = 0;
        var reached = false;
        string? warning = null;
        try
        {
            while (pages < SearchResultPages && current is { HasMore: true } snapshot)
            {
                var page = await reader.GetMessagesAsync(snapshot.Chat.Id, snapshot.Cursor, token);
                if (version != generation) return pages;
                pages++;
                if (Unanswered(page)) { OlderLoadFailed = true; break; }
                warning = page.Warning ?? warning;
                // Built on the history as it is now: a message that arrived during the read is already in current.
                var latest = current ?? snapshot;
                var next = Merge(latest, page) with { IsRefreshing = latest.IsRefreshing,
                    UnavailableSources = IsMerged ? (latest.UnavailableSources ?? []).Concat(page.UnavailableScopes ?? []).Distinct().ToArray() : page.UnavailableScopes,
                    Warning = latest.Warning ?? page.Warning };
                if (reached && !Holds(next, target)) break;
                current = next;
                if (reached) break;
                reached = Holds(current, target);
            }
            cache.Put(current!); Render(true, needsRecovery);
            if (warning is not null && !(IsMerged && needsRecovery)) Status = warning;
            await cache.SaveAsync(current!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) { Status = Error(ex); OlderLoadFailed = true; } }
        finally { if (version == generation) IsBusy = false; IsLoadingOlder = false; }
        return pages;

        static bool Holds(HistorySnapshot snapshot, ChatMessage message) =>
            snapshot.Messages.Any(m => m.Id == message.Id && m.ChatId == message.ChatId);
    }

    // Whether the loaded history of the message's own conversation reaches back past its moment. A merge is judged by that
    // member's rows alone: one service's rows can reach back months further than another's (a merge's history holds its
    // newest 1,000 across services), and judging by the oldest row of any service called a message gone that was only
    // further back than its own service's rows.
    private bool ReachesBack(ChatMessage message)
    {
        if (current?.Messages is not { Count: > 0 } loaded) return false;
        if (!IsMerged) return loaded[0].Timestamp <= message.Timestamp;
        var own = loaded.Where(m => m.ChatId == message.ChatId).ToArray();
        return own.Length > 0 && own.Min(m => m.Timestamp) <= message.Timestamp;
    }
}
