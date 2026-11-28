using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.ViewModels;

public sealed partial class ChatListViewModel
{
    private readonly Dictionary<string, DateTimeOffset?> previewAttempts = [];
    private readonly Dictionary<string, ChatMessage> previewFallbacks = [];
    private readonly Dictionary<string, int> chatLoadingCounts = [];
    // The rows whose swirl is actually on screen, which is a smaller set than the rows doing work: see BeginChatLoading.
    private readonly HashSet<string> chatLoadingShown = [];
    private readonly Dictionary<string, LocalRead> localReads = [];
    private long localReadAttempt;
    private readonly SemaphoreSlim previewGate = new(3);
    private sealed record LocalRead(DateTimeOffset? Boundary, long Attempt, bool Confirmed);

    // Provider unread state can trail a successful read marker. Keep the local row responsive through the exact
    // activity boundary that was visible; any newer activity falls outside this override and becomes unread at once.
    private ChatSummary Present(ChatSummary chat)
    {
        // Read here while private reading kept the service from being told: the service still says unread, this PC does not.
        if (readHere is not null) chat = readHere.Apply(chat);
        // Marked unread by the user counts as one unread conversation everywhere (row, Unread view, taskbar and tray) until
        // it is read here, whatever the provider or the latest preview says.
        if (chat.IsMarkedUnread) return chat.UnreadCount > 0 ? chat : chat with { UnreadCount = 1 };
        if (chat.Preview?.IsOutgoing == true) return chat.UnreadCount == 0 ? chat : chat with { UnreadCount = 0 };
        if (!localReads.TryGetValue(chat.Id, out var read) || chat.LastActivity is null || read.Boundary is null ||
            chat.LastActivity > read.Boundary) return chat;
        return chat.UnreadCount == 0 ? chat : chat with { UnreadCount = 0 };
    }
    // A row says it is updating only once the update has been running long enough to be worth saying — see
    // ProgressIndicator. Nearly all of this work is a few hundred milliseconds of reading one conversation's summary
    // back after a receipt, and a swirl that appears and disappears inside a second reads as a fault in that row
    // rather than as progress. Which row it was is exactly what it fails to explain, so it is worse than nothing.
    private void BeginChatLoading(string chatId)
    {
        var count = chatLoadingCounts.GetValueOrDefault(chatId) + 1;
        chatLoadingCounts[chatId] = count;
        if (count == 1) _ = RevealChatLoadingAsync(chatId, generation);
    }
    private async Task RevealChatLoadingAsync(string chatId, int version)
    {
        if (ProgressDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(ProgressDelay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        // Still running, still this list, and not already saying so.
        if (version != generation || !chatLoadingCounts.ContainsKey(chatId) || !chatLoadingShown.Add(chatId)) return;
        Chats.FirstOrDefault(item => item.Chat.Id == chatId)?.SetLoading(true);
    }
    private void EndChatLoading(string chatId)
    {
        if (!chatLoadingCounts.TryGetValue(chatId, out var count)) return;
        if (count > 1) { chatLoadingCounts[chatId] = count - 1; return; }
        chatLoadingCounts.Remove(chatId);
        if (chatLoadingShown.Remove(chatId)) Chats.FirstOrDefault(item => item.Chat.Id == chatId)?.SetLoading(false);
    }
    // A provider that sends no snippet for a conversation whose last message was a photo or an audio clip leaves its row
    // without a second line until a history read fills one in (below). Each launch replaced the saved copy's filled line
    // with the provider's empty one and read the history again, so the row sat a line short and then grew "You: Photo"
    // after everything else had settled. Nothing has happened in a conversation whose activity is unchanged, so the line
    // already known for that activity is kept.
    private static ChatSummary KeepKnownPreview(ChatSummary fresh, ChatSummary? known) =>
        fresh.Preview is null or { IsHidden: true } && known is { Preview: { IsHidden: false } preview } && known.LastActivity == fresh.LastActivity
            ? fresh with { Preview = preview } : fresh;
    private async Task FillMissingPreviewsAsync(CancellationToken token)
    {
        var version = generation;
        var targets = loaded.Where(c => c.Preview is null || c.Preview.IsHidden).ToArray();
        var filled = false;
        async Task FillAsync(ChatSummary target)
        {
            if (previewAttempts.TryGetValue(target.Id, out var attempted) && attempted == target.LastActivity)
            {
                var cachedIndex = loaded.FindIndex(c => c.Id == target.Id);
                if (cachedIndex >= 0 && previewFallbacks.TryGetValue(target.Id, out var fallback))
                { loaded[cachedIndex] = loaded[cachedIndex] with { Preview = fallback }; UpdateRow(loaded[cachedIndex]); BatchRebuild(); }
                return;
            }
            previewFallbacks.Remove(target.Id);
            previewAttempts[target.Id] = target.LastActivity;
            var entered = false;
            BeginChatLoading(target.Id);
            try
            {
                await previewGate.WaitAsync(token); entered = true;
                if (version != generation) return;
                var page = await reader.GetMessagesAsync(target.Id, cancellationToken: token);
                if (version != generation || token.IsCancellationRequested) return;
                var latest = page.Items.Where(m => !m.IsHidden && !m.IsDeleted &&
                    (!string.IsNullOrWhiteSpace(m.Text) || m.Attachments.Count > 0)).OrderByDescending(m => m.Timestamp).FirstOrDefault();
                var index = loaded.FindIndex(c => c.Id == target.Id);
                if (index < 0 || loaded[index].LastActivity != target.LastActivity || loaded[index].Preview is { IsHidden: false }) return;
                // A filled preview cannot move the row — the guard above required its activity to be unchanged — so the
                // text goes straight into the row it belongs to and the rebuild it asks for joins the batch.
                if (latest is not null) { previewFallbacks[target.Id] = latest; loaded[index] = loaded[index] with { Preview = latest }; UpdateRow(loaded[index]); BatchRebuild(); filled = true; }
            }
            catch { if (version == generation) previewAttempts.Remove(target.Id); }
            finally { if (entered) previewGate.Release(); EndChatLoading(target.Id); }
        }
        // Three fills run at once and each one finished by rebuilding the whole list. They share one now, drawn at most
        // once per RedrawFlush while they run and once more as they end. Nothing else waits for them: these reads have
        // no time limit, and holding the list's redraw across them held every other change to the list as well.
        Begin(previewBatches);
        try { await Task.WhenAll(targets.Select(FillAsync)); }
        catch { /* A missing preview must not interrupt chat synchronization. */ }
        finally { End(previewBatches); }
        if (rebuildWanted) Rebuild();
        // Saved, so the next launch starts with the line rather than reading it again (see KeepKnownPreview).
        if (filled && version == generation) SaveLoadedSoon();
    }

    // Raised with a row's id when reading clears the user's Mark unread, so the saved preference can be cleared too.
    public event EventHandler<string>? MarkedUnreadCleared;
    private void ClearMarkedUnread(string chatId)
    {
        var cleared = false;
        for (var i = 0; i < loaded.Count; i++)
        {
            var chat = loaded[i];
            if (!chat.IsMarkedUnread || chat.Id != chatId && !chat.MemberChatIds.Contains(chatId)) continue;
            loaded[i] = chat with { IsMarkedUnread = false }; cleared = true;
            MarkedUnreadCleared?.Invoke(this, chat.Id);
        }
        if (cleared) Rebuild();
    }

    // Raised with a conversation id when reading ends a snooze, so the saved snooze can be cleared too. A merged
    // row reports its member conversations as well: the snooze may have been set on any of them.
    public event EventHandler<string>? SnoozeCleared;
    private void ClearSnooze(string chatId)
    {
        var cleared = false;
        for (var i = 0; i < loaded.Count; i++)
        {
            var chat = loaded[i];
            if (chat.SnoozedUntil is null || (chat.Id != chatId && !chat.MemberChatIds.Contains(chatId))) continue;
            loaded[i] = chat with { SnoozedUntil = null }; cleared = true;
            // A merged row shows the latest snooze among its conversations without saying which one holds it, so
            // every id behind the row is reported. Clearing one that was never snoozed costs nothing.
            SnoozeCleared?.Invoke(this, chat.Id);
            foreach (var member in chat.MemberChatIds) SnoozeCleared?.Invoke(this, member);
        }
        if (cleared) Rebuild();
    }

    /// <summary>What seeing a conversation answers, read or not: Mark unread is undone and a snooze's reminder is no longer owed. The row's unread stays as it is.</summary>
    public void ApplySeen(string chatId)
    {
        using var held = HoldRebuild();
        ClearMarkedUnread(chatId);
        ClearSnooze(chatId);
    }

    public long ApplyPendingRead(string chatId, DateTimeOffset? activityAtRead)
    {
        // Up to three changes below, drawn once on the way out rather than three times over.
        using var held = HoldRebuild();
        // Reading the conversation is what undoes Mark unread.
        ClearMarkedUnread(chatId);
        // Reading it is also the answer a snooze was waiting to give, so the reminder is no longer owed.
        ClearSnooze(chatId);
        var index = loaded.FindIndex(c => c.Id == chatId);
        if (index < 0 || activityAtRead is null || loaded[index].LastActivity != activityAtRead) return 0;
        if (localReads.TryGetValue(chatId, out var existing) && existing.Confirmed && existing.Boundary == activityAtRead) return 0;
        var attempt = ++localReadAttempt;
        localReads[chatId] = new(activityAtRead, attempt, false);
        Rebuild();
        return attempt;
    }

    public void CancelPendingRead(string chatId, DateTimeOffset? activityAtRead, long attempt)
    {
        if (attempt == 0 || !localReads.TryGetValue(chatId, out var current) || current.Confirmed ||
            current.Attempt != attempt || current.Boundary != activityAtRead) return;
        localReads.Remove(chatId);
        Rebuild();
    }

    public void ApplyAcknowledgedRead(string chatId, DateTimeOffset? activityAtRead, long attempt = 0)
    {
        var index = loaded.FindIndex(c => c.Id == chatId);
        // A newer activity event may have arrived while the bounded read marker was submitted.
        if (index < 0 || activityAtRead is null || loaded[index].LastActivity != activityAtRead) return;
        if (attempt != 0 && (!localReads.TryGetValue(chatId, out var pending) || pending.Attempt != attempt)) return;
        var kept = ++localReadAttempt;
        localReads[chatId] = new(activityAtRead, kept, true);
        // A read its service was not told of is kept past this run: the service will go on saying it is unread.
        if (readHere?.Wants(chatId) == true) readHere.Mark(chatId, activityAtRead.Value);
        Rebuild();
        _ = TrustServiceAfterGraceAsync(chatId, kept);
    }

    /// <summary>
    /// How long a read the service took is shown read here whatever the service says next. Long enough for a service's own
    /// unread to catch up with a read it took (Voice lists again at most every 10 s); after it, the service's answer is shown
    /// again (owner, 2026-10-22): a read the service took and then lost showed read here for the rest of the run while
    /// Messenger's web and Instagram's app kept it unread. Settable for the offline checks.
    /// </summary>
    public static TimeSpan ReadTrustGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised with a conversation's id when its service still said unread once a read's grace ended.</summary>
    public event EventHandler<string>? ReadNotKept;

    private async Task TrustServiceAfterGraceAsync(string chatId, long kept)
    {
        try { await Task.Delay(ReadTrustGrace, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        bool Current() => localReads.TryGetValue(chatId, out var read) && read.Confirmed && read.Attempt == kept;
        if (!Current()) return;
        // The row may be the copy from before the read, if the service announced nothing after taking it. Asked once more, so a read the
        // service kept is not shown unread from that old copy. Not Instagram's: asking is a request to Instagram, and its read already
        // announced the conversation, so the list holds what Instagram said after it.
        if (!chatId.StartsWith("instagram:", StringComparison.Ordinal))
        {
            ChatSummary fresh;
            try { fresh = await reader.GetChatAsync(chatId, lifetime.Token); }
            catch (Exception) { return; }
            if (!Current()) return;
            var index = loaded.FindIndex(c => c.Id == chatId);
            if (index >= 0 && fresh.Id == chatId && (fresh.LastActivity ?? DateTimeOffset.MinValue) >= (loaded[index].LastActivity ?? DateTimeOffset.MinValue))
                loaded[index] = KeepKnownPreview(fresh, loaded[index]);
        }
        var boundary = localReads[chatId].Boundary;
        localReads.Remove(chatId);
        var row = loaded.FirstOrDefault(c => c.Id == chatId);
        if (row is { UnreadCount: > 0 } && row.LastActivity <= boundary) ReadNotKept?.Invoke(this, chatId);
        Rebuild();
    }

    /// <summary>
    /// Reads conversations this list does not hold back from their services into it, as a change event naming them would.
    /// A merged conversation's member that has dropped off its service's recent listing is told it was read, and its
    /// service's copy already said so: it announced no change, and the merged row went on showing the member as it was
    /// (2026-10-14). Returns how many were read back and how many could not be.
    /// </summary>
    public async Task<(int Read, int Failed)> ReadBackAsync(IReadOnlyList<string> chatIds)
    {
        var version = generation;
        int read = 0, failed = 0;
        foreach (var id in chatIds)
        {
            ChatSummary chat;
            try { chat = await reader.GetChatAsync(id, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception) { failed++; continue; }
            if (version != generation) break;
            if (IsHeldRemoved(id)) continue;
            // One a change event brought in meanwhile is kept: it can be newer than this answer (a message arriving).
            if (loaded.Any(c => c.Id == id)) { read++; continue; }
            if (loaded.Count >= 2000) continue;
            loaded.Add(chat);
            read++;
        }
        if (read > 0 && version == generation) { Rebuild(); SaveLoadedSoon(); }
        return (read, failed);
    }

    /// <summary>
    /// The conversations behind a row (itself, or a merged row's members) that were read here without their service
    /// being told and that the service still has unread: what the conversation menu's Send read receipt would tell.
    /// </summary>
    public IReadOnlyList<string> ReadHereOnly(ChatSummary row)
    {
        if (readHere is null) return [];
        IEnumerable<string> ids = row.MemberChatIds.Count > 0 ? row.MemberChatIds : [row.Id];
        return [.. ids.Where(id => readHere.Through(id) is not null && loaded.Any(chat => chat.Id == id && chat.UnreadCount > 0))];
    }
}
