using CommunityToolkit.Mvvm.Input;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// A window of older history around a search result, shown instead of the conversation's newest messages while the reader
// is there. The conversation itself (current) goes on exactly as it does otherwise: arrivals, edits, receipts, sends and
// the saved copy all keep to it, and nothing about the window is cached or saved. The window only changes what is drawn.
// It is read from what is kept on this PC (IHistoryWindowReader), so it costs the same at any depth: there is no paging
// back to it. Scrolling up or down from it reads further from the same place; scrolling down until the rows on screen are
// in the conversation's own history goes back to it where the reader is, and Latest, a send or opening anything else
// goes back to its newest message.
public sealed partial class ConversationViewModel
{
    // Sends: the local ids of the sends under way when the window opened (SendsUnderWay).
    private sealed record WindowState(string ChatId, ChatMessage[] Messages, bool MoreOlder, bool MoreNewer, IReadOnlySet<string> Sends);
    // How much each read of a window asks for on a side, and how much a window holds before its far end is let go.
    private const int WindowSide = 60, WindowPage = 60, WindowLimit = 600;
    private WindowState? window;

    /// <summary>Whether a window of older history is drawn instead of the conversation's newest messages.</summary>
    public bool IsInWindow => window is not null;
    /// <summary>Whether the window has more after its last row (in the window, not yet the conversation's own history).</summary>
    public bool HasNewer => window is { } shown && Overlap(shown) is var overlap && overlap < WindowPage &&
        (shown.MoreNewer || overlap > 0 && NewerFromHistory(shown).Length > 0);
    /// <summary>Raised just before the window gives way to the newest messages, so the view can cover the change.</summary>
    public event EventHandler? WindowLeaving;
    /// <summary>The rows arrivals are counted against: the conversation's own, also while a window is drawn.</summary>
    public IEnumerable<ChatMessage> LiveMessages => window is null ? Messages.Select(row => row.Message) : current?.Messages ?? [];

    // The rows a render draws: the window's, or the conversation's with its sends in flight.
    private IEnumerable<ChatMessage> DrawnMessages(HistorySnapshot snapshot) => window?.Messages ?? WithOutgoing(snapshot.Messages);

    // Reads the window around a message the loaded history does not reach. When the window's newer side already reaches
    // the loaded history, the two are one run of messages and simply become the history; otherwise the window is drawn.
    // Null when it could not be read, and the caller pages back instead.
    private async Task<string?> OpenWindowAsync(ChatMessage target, long version)
    {
        if (windows is null || current is null || selection is null || !windows.CanReadWindow(current.Chat.Id))
        { windowSkipped = windows is null ? "no_reader" : current is null || selection is null ? "closed" : "not_kept"; return null; }
        windowSkipped = null;
        var token = selection.Token;
        IsBusy = true; IsLoadingOlder = true; OlderLoadFailed = false; Advisory("Loading earlier messages…");
        try
        {
            var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var read = await windows.ReadWindowAsync(current.Chat.Id, target.Timestamp, WindowSide, WindowSide * 3, token);
            var readDone = System.Diagnostics.Stopwatch.GetTimestamp();
            if (version != generation || current is null) { windowSkipped = "stale"; return null; }
            // A merge reads only the services that keep their history here: a result from one that does not is not in it.
            if (!read.Messages.Any(m => m.Id == target.Id && m.ChatId == target.ChatId))
            { windowSkipped = read.Messages.Count == 0 ? "empty" : "not_in_window"; return null; }
            var loaded = current.Messages.Select(MessageIdentity.Key).ToHashSet();
            if (read.Messages.Any(m => loaded.Contains(MessageIdentity.Key(m))) && WindowsEveryMember(current.Chat.Id) &&
                Union(read.Messages, current.Messages) is { Length: <= ConversationCache.MaxMessages } joined)
            {
                current = current with { Messages = joined };
                cache.Put(current); Render(true, needsRecovery);
                TimedPage("window_open", readDone - readStarted, readDone, read.Messages.Count);
                WatchRefresh(read, version);
                await cache.SaveAsync(current);
                return "joined";
            }
            window = new(current.Chat.Id, [.. read.Messages], read.MoreOlder, read.MoreNewer, SendsUnderWay());
            WatchRefresh(read, version);
            Render(true, needsRecovery);
            TimedPage("window_open", readDone - readStarted, readDone, read.Messages.Count);
            return "window";
        }
        catch (OperationCanceledException) { windowSkipped = "cancelled"; return null; }
        catch (Exception error) { windowSkipped = "failed_" + error.GetType().Name; return null; }
        finally { if (version == generation) IsBusy = false; IsLoadingOlder = false; }
    }

    // Older rows above the window, read as the reader nears its top. The newest rows go once the window is full; the
    // reader is at the other end of it.
    private async Task LoadWindowOlderAsync()
    {
        if (window is not { MoreOlder: true } shown || windows is null || selection is null) return;
        var version = generation;
        IsBusy = true; IsLoadingOlder = true; OlderLoadFailed = false;
        try
        {
            var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var read = await windows.ReadWindowAsync(shown.ChatId, shown.Messages[0].Timestamp, WindowPage, 0, selection.Token);
            var readDone = System.Diagnostics.Stopwatch.GetTimestamp();
            if (version != generation || !ReferenceEquals(window, shown)) return;
            var rows = Union(read.Messages, shown.Messages);
            var moreNewer = shown.MoreNewer;
            if (rows.Length > WindowLimit) { rows = rows[..WindowLimit]; moreNewer = true; }
            window = shown with { Messages = rows, MoreOlder = read.MoreOlder, MoreNewer = moreNewer };
            WatchRefresh(read, version);
            Render(true, needsRecovery);
            TimedPage("window_older", readDone - readStarted, readDone, read.Messages.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) { Status = Error(ex); OlderLoadFailed = true; } }
        finally { if (version == generation) IsBusy = false; IsLoadingOlder = false; }
    }

    /// <summary>Newer rows below the window, read as the reader nears its bottom. The oldest go once the window is full.</summary>
    [RelayCommand]
    private async Task LoadNewerAsync()
    {
        if (window is not { } shown || !HasNewer || windows is null || selection is null || IsBusy) return;
        if (!shown.MoreNewer)
        {
            // Nothing newer is kept here, but the window already reaches the loaded history: a page of the conversation's own
            // rows carries it on, with nothing to read.
            var joined = Union(shown.Messages, NewerFromHistory(shown));
            window = shown with { Messages = joined.Length > WindowLimit ? joined[^WindowLimit..] : joined, MoreOlder = shown.MoreOlder || joined.Length > WindowLimit };
            Render(true, needsRecovery);
            return;
        }
        var version = generation;
        IsBusy = true;
        try
        {
            var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // Just past it, no further: Google's times are finer than a millisecond, so the next message can be less than one away.
            var read = await windows.ReadWindowAsync(shown.ChatId, shown.Messages[^1].Timestamp.AddTicks(1), 0, WindowPage, selection.Token);
            var readDone = System.Diagnostics.Stopwatch.GetTimestamp();
            if (version != generation || !ReferenceEquals(window, shown)) return;
            var rows = Union(shown.Messages, read.Messages);
            var moreOlder = shown.MoreOlder;
            if (rows.Length > WindowLimit) { rows = rows[^WindowLimit..]; moreOlder = true; }
            window = shown with { Messages = rows, MoreOlder = moreOlder, MoreNewer = read.MoreNewer };
            WatchRefresh(read, version);
            Render(true, needsRecovery);
            TimedPage("window_newer", readDone - readStarted, readDone, read.Messages.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) Status = Error(ex); }
        finally { if (version == generation) IsBusy = false; }
    }

    /// <summary>
    /// Once the first row on screen is one the conversation's own history holds, the window has nothing left to add: the
    /// history is drawn in its place, around that same row, and the reader carries on scrolling down to the newest
    /// message without a seam. True when it did.
    /// </summary>
    public bool JoinHistoryAt(string firstShownKey)
    {
        if (window is null || current is null || !WindowMeetsHistory(window) ||
            !current.Messages.Any(m => MessageIdentity.Key(m) == firstShownKey)) return false;
        window = null;
        Render(true, needsRecovery);
        Timed?.Invoke(this, "history window joined");
        return true;
    }

    /// <summary>Back to the conversation's newest messages from a window (Latest).</summary>
    public void LeaveWindow()
    {
        if (window is null) return;
        WindowLeaving?.Invoke(this, EventArgs.Empty);
        window = null;
        Render(false, needsRecovery);
    }

    // The window's newest rows are already in the conversation's own history.
    private bool WindowMeetsHistory(WindowState shown) => Overlap(shown) > 0;

    // Whether a window of this conversation holds all of its history from then, so it can join the history. A merge's
    // holds only its members that keep their history here (MergedHistoryWindows): joined, it put their older rows in the
    // history with none of the others' from then, a gap that paging filled only until the history was full.
    private bool WindowsEveryMember(string chatId) => !MergedConversations.IsMerged(chatId) ||
        merges is not null && windows is not null && merges.Members(chatId).Where(m => !MergedConversations.IsCall(m)).All(m => windows.CanReadWindow(m.Id));

    // How many of the window's rows the conversation's own history also holds. Newer rows go on loading until a page of
    // them does, so the reader can always scroll far enough into them for the history to take over.
    private int Overlap(WindowState shown)
    {
        if (current is null || current.Messages.Count == 0) return 0;
        var loaded = current.Messages.Select(MessageIdentity.Key).ToHashSet();
        return shown.Messages.Count(m => loaded.Contains(MessageIdentity.Key(m)));
    }

    // The conversation's own rows after the window's newest, a page of them. Where what is kept here ends inside the loaded
    // history (Google's archive a few messages behind the service), the window has nothing newer to read, and too few of
    // its rows could be in the history for the reader to scroll far enough into them to join it: they waited at the
    // window's bottom with Latest the only way on (2026-10-04). Only once the window reaches the history (Overlap) is there
    // no gap between the two.
    private ChatMessage[] NewerFromHistory(WindowState shown)
    {
        if (current is null || shown.Messages.Length == 0) return [];
        var newest = shown.Messages[^1].Timestamp;
        var have = shown.Messages.Select(MessageIdentity.Key).ToHashSet();
        return [.. current.Messages.Where(m => m.Timestamp >= newest && !have.Contains(MessageIdentity.Key(m)))
            .OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).Take(WindowPage)];
    }

    // The sends under way when the window opened. A later change to one of them (a held send going, its echo coming back)
    // is not a send from the window: the window's rows never hold it, so it looked new and took the reader out to the
    // newest messages, from a search result they had just opened (2026-10-04). Only a send made from the window does.
    private HashSet<string> SendsUnderWay()
    {
        var targets = IsMerged ? CurrentChat?.MemberChatIds ?? [] : ActiveChatId is null ? [] : new[] { ActiveChatId };
        return targets.SelectMany(id => outbox?.ForChat(id) ?? []).Select(item => item.LocalId).ToHashSet(StringComparer.Ordinal);
    }
    // Whether a send waiting in the outbox has not been drawn yet: a new one, which the conversation goes to its end for.
    private bool IsNewSend(OutgoingMessage item) =>
        window is { } shown ? !shown.Sends.Contains(item.LocalId) : !Messages.Any(row => row.Message.Id == item.LocalId);

    // When a read's fresher copies come (WindowRead.Refreshed), they replace their rows wherever those rows now are: in the
    // window, or in the history a near window joined. Nothing else is added, and the photos are asked for again.
    private void WatchRefresh(HistoryWindow read, long version)
    {
        if (read.Refreshed is not { } refreshed) return;
        _ = ApplyAsync();
        async Task ApplyAsync()
        {
            IReadOnlyList<ChatMessage> fresh;
            try { fresh = await refreshed; }
            catch (Exception) { return; }
            if (version != generation || fresh.Count == 0) return;
            var byKey = fresh.ToDictionary(m => MessageIdentity.Key(m), m => m);
            ChatMessage[] Replace(IEnumerable<ChatMessage> rows, out int replaced)
            {
                var result = rows.ToArray();
                replaced = 0;
                for (var index = 0; index < result.Length; index++)
                    if (byKey.TryGetValue(MessageIdentity.Key(result[index]), out var copy)) { result[index] = copy; replaced++; }
                return result;
            }
            if (window is { } shown)
            {
                var rows = Replace(shown.Messages, out var replaced);
                if (replaced == 0) return;
                window = shown with { Messages = rows };
            }
            else if (current is { } live)
            {
                var rows = Replace(live.Messages, out var replaced);
                if (replaced == 0) return;
                current = live with { Messages = rows };
                cache.Put(current);
            }
            else return;
            mediaReloadVersion++;
            Render(true, needsRecovery);
        }
    }

    // Two runs of rows as one, in history order, each message once (the later run's copy wins).
    private static ChatMessage[] Union(IEnumerable<ChatMessage> first, IEnumerable<ChatMessage> second)
    {
        var rows = new Dictionary<string, ChatMessage>();
        foreach (var message in first.Concat(second)) rows[MessageIdentity.Key(message)] = message;
        return [.. rows.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal).ThenBy(m => MessageIdentity.Key(m), StringComparer.Ordinal)];
    }
}
