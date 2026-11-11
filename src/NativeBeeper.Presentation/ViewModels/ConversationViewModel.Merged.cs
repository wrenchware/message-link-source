using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    private string? sendChatId;
    public bool IsMerged => MergedConversations.IsMerged(ActiveChatId);
    public string? SendChatId => sendChatId;
    public ChatSummary? SendChat => sendChatId is null ? null : SourceChat(sendChatId);
    public Func<bool>? CanChangeAutomaticRoute { get; set; }
    /// <summary>
    /// The rule a conversation view gives <see cref="CanChangeAutomaticRoute"/>: a merged conversation's route moves by
    /// itself (a newer incoming message, the conversation read again) only while nothing is held for the route it is on —
    /// no words typed, no reply, no attachment waiting, and no voice message being recorded or waiting to be added, which
    /// belongs to the route it was recorded for and would otherwise be thrown away.
    /// </summary>
    public static bool AutomaticRouteMayMove(bool composerReady, bool hasText, bool hasReply, bool hasAttachments, bool hasRecording) =>
        composerReady && !hasText && !hasReply && !hasAttachments && !hasRecording;
    public event EventHandler<string?>? SendChatChanged;
    public bool ContainsChat(string? id) => id is not null && (id == ActiveChatId || IsMerged &&
        (CurrentChat?.MemberChatIds.Contains(id) == true || merges?.DisplayId(id) == ActiveChatId));
    public ChatSummary SourceChat(string id) => merges?.Source(id) ?? CurrentChat!;
    public IReadOnlyList<ConversationAccount> Accounts => CurrentChat is not { } chat ? [] :
        ConversationAccounts.Collect(MergedConversations.IsMerged(chat.Id) && merges is not null ? merges.Members(chat.Id) : [chat],
            merges is null ? null : merges.ServiceRouteLabel);
    public IReadOnlyList<ChatMessage> DisplayedReadTargets() => Messages.Select(m => m.Message)
        .Where(m => !m.Id.StartsWith("local:", StringComparison.Ordinal) && !m.IsHidden && current?.UnavailableSources?.Contains(m.ChatId) != true)
        .GroupBy(m => m.ChatId).Select(g => g.Last()).ToArray();
    /// <summary>
    /// What reading this merged conversation marks: each linked conversation's newest shown message, and for one with
    /// unread but nothing shown, its newest incoming message from the list, as /read would. Its unread is above the
    /// loaded page (Mom's missed call from weeks back), and the merged row is read as one conversation (2026-10-07).
    /// Each service's own rules still apply when the marker is written: an Instagram request is never marked.
    /// </summary>
    public IReadOnlyList<ChatMessage> MergedReadTargets(IReadOnlyList<ChatSummary> loaded)
    {
        var shown = DisplayedReadTargets();
        MergedReadNote = "";
        if (!IsMerged || ActiveChatId is not { } id) return shown;
        var covered = shown.Select(m => m.ChatId).ToHashSet(StringComparer.Ordinal);
        // The merge's own members, as its row and the console count them: a Voice call record taken in from the calls
        // stream is one of them without being a list row (Mom's missed call was neither on the page nor in the list, so
        // nothing read it, 2026-10-07). The list's copy where it has one, being the newer.
        var listed = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var row in loaded) listed.TryAdd(row.Id, row);
        IEnumerable<ChatSummary> members = merges?.Members(id) ?? [];
        if (CurrentChat is { } chat) members = members.Concat(chat.MemberChatIds.Select(m => listed.GetValueOrDefault(m)).OfType<ChatSummary>());
        int unread = 0, onPage = 0, unavailable = 0, nothing = 0, fromList = 0;
        var unseen = new List<ChatMessage>();
        foreach (var member in members.DistinctBy(m => m.Id).Select(m => listed.GetValueOrDefault(m.Id) ?? m))
        {
            if (member.UnreadCount <= 0) continue;
            unread++;
            if (listed.ContainsKey(member.Id)) fromList++;
            if (covered.Contains(member.Id)) { onPage++; continue; }
            if (current?.UnavailableSources?.Contains(member.Id) == true) { unavailable++; continue; }
            if (ExplicitRead.Through(member, null) is not { } newest) { nothing++; continue; }
            unseen.Add(newest with { ChatId = member.Id });
        }
        MergedReadNote = $"members_unread={unread} listed={fromList} on_page={onPage} unavailable={unavailable} no_message={nothing} saved_only={merges?.SavedOnly(id) ?? 0}";
        return [.. shown, .. unseen];
    }
    /// <summary>The counts behind the last <see cref="MergedReadTargets"/>, for the merged read's log line.</summary>
    public string MergedReadNote { get; private set; } = "";
    private void SetSendChat(string? id)
    {
        if (sendChatId == id) return;
        sendChatId = id;
        ExpectedTransport = id is null ? null : SourceChat(id)?.ExpectedTransport;
        OnPropertyChanged(nameof(SendChatId)); OnPropertyChanged(nameof(SendChat));
        SelectedMember = Members.FirstOrDefault(m => m.Id == id);
        SendChatChanged?.Invoke(this, id);
    }
    private void RefreshMergedRoutes()
    {
        if (!IsMerged || merges is null) return;
        var choices = merges.Members(ActiveChatId!).Where(m => !MergedConversations.IsCall(m) && !m.IsReadOnly).ToArray();
        var group = merges.Find(ActiveChatId!);
        // Where the person last wrote from, among every route (a pin or a manual choice does not change it): the loaded
        // page's newest incoming message, else the newest conversation whose own last message is theirs.
        var lastIncoming = current?.Messages.Where(m => !m.IsOutgoing && !m.IsDeleted && !m.IsHidden && m.Type != "system" &&
                choices.Any(c => c.Id == m.ChatId)).OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.SortKey, StringComparer.Ordinal)
                .FirstOrDefault()?.ChatId
            ?? choices.Where(c => c.Preview is { IsOutgoing: false }).OrderByDescending(c => c.LastActivity).FirstOrDefault()?.Id;
        // The house order (Google Messages, Messenger, Google Voice, Instagram), so a service is always in the same place.
        var labels = choices.OrderBy(chat => ConversationMember.ServiceOrder(chat.Network))
            .Select(chat => new ConversationMember(chat.Id, merges.ServiceLabel(chat), chat.Network, merges.ServiceRouteLabel(chat),
            group?.PinnedRoute?.ChatId == chat.Id, ConversationHeaderText.Number(chat) ?? ConversationHeaderText.Handle(chat),
            ConversationHeaderText.RouteKind(chat), chat.Id == lastIncoming)).ToArray();
        if (!Members.SequenceEqual(labels))
        {
            Members.Clear();
            foreach (var label in labels) Members.Add(label);
            SelectedMember = Members.FirstOrDefault(m => m.Id == sendChatId);
        }
        HasMembers = true; // Even one available route needs to be explicit on a combined history.
        AnnouncePinEnded(group);
        if (sendChatId is not null && CanChangeAutomaticRoute?.Invoke() == false) return;
        // "Always send with" outranks everything but a reply's own source (above). A pinned conversation that is not a
        // route just now is passed over without being forgotten: a read-only one is not among the choices at all, and one
        // whose service has stopped and waits on the user (MergedConversations.NeedsSignIn) is left out of the usual rule
        // below until it is back. Only the user, its leaving the merge or its service signing out ends a pin.
        var pinned = group?.PinnedRoute is { } pin ? choices.FirstOrDefault(c => c.Id == pin.ChatId) : null;
        if (pinned is not null && !merges.NeedsSignIn(pinned)) { pinWaitingSaid = null; SetSendChat(pinned.Id); return; }
        var usable = pinned is null ? choices : choices.Where(c => !merges.NeedsSignIn(c)).ToArray();
        // Nowhere else to send from: the pin stays the route, and sending waits for its service as it always did.
        if (pinned is not null && usable.Length == 0) { SetSendChat(pinned.Id); return; }
        if (pinned is not null) AnnouncePinWaiting(pinned);
        if (group?.SendingRoute is { } manual && usable.Any(c => c.Id == manual.ChatId))
        { SetSendChat(manual.ChatId); return; }
        var latest = current?.Messages.Where(m => !m.IsOutgoing && !m.IsDeleted && !m.IsHidden && m.Type != "system" &&
            usable.Any(c => c.Id == m.ChatId)).OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.SortKey, StringComparer.Ordinal).FirstOrDefault();
        var target = latest?.ChatId;
        // Look beyond an all-outgoing newest page before picking a default; not while a pin waits, when the route must
        // leave the stopped service now rather than after a read.
        if (target is null && current?.Cursor is not null && pinned is null) return;
        target ??= usable.Where(c => c.Preview is { IsOutgoing: false }).OrderByDescending(c => c.LastActivity).FirstOrDefault()?.Id
            ?? usable.OrderByDescending(c => c.LastActivity).FirstOrDefault()?.Id;
        SetSendChat(target);
    }
    private async Task FindEarlierIncomingRouteAsync(long version, CancellationToken token)
    {
        var manual = IsMerged ? merges?.Find(ActiveChatId!)?.SendingRoute : null;
        if (!IsMerged || sendChatId is not null && manual is null || current is not { Cursor: not null } snapshot) return;
        // A pinned route is not looked past: nothing older can change it.
        if (merges?.Find(ActiveChatId!)?.PinnedRoute is { } pinned && Members.Any(m => m.Id == pinned.ChatId)) return;
        if (manual is not null && snapshot.Messages.Any(m => m.Timestamp <= manual.ChosenAt)) return;
        var cursor = snapshot.Cursor;
        var seen = new HashSet<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (cursor is not null && seen.Add(cursor))
            {
                var page = await reader.GetMessagesAsync(snapshot.Chat.Id, cursor, timeout.Token);
                if (version != generation || manual is null && sendChatId is not null ||
                    manual is not null && merges!.Find(snapshot.Chat.Id)?.SendingRoute is { } changedChoice && changedChoice != manual) return;
                var latest = page.Items.Where(m => !m.IsOutgoing && !m.IsDeleted && !m.IsHidden && m.Type != "system" &&
                    (manual is null || m.EditedAt is null && m.CallKind is null) &&
                    Members.Any(c => c.Id == m.ChatId)).OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.SortKey, StringComparer.Ordinal).FirstOrDefault();
                if (latest is not null)
                {
                    if (manual is null || merges!.Find(snapshot.Chat.Id)?.SendingRoute is null &&
                        latest.Timestamp > manual.ChosenAt && CanChangeAutomaticRoute?.Invoke() != false) SetSendChat(latest.ChatId);
                    return;
                }
                if (manual is not null && page.Items.Any(m => m.Timestamp <= manual.ChosenAt)) return;
                if (page.Warning is not null || page.IsRefreshing) break;
                if (!page.HasMore)
                {
                    if (manual is not null) return;
                    // No incoming messages anywhere in the available history: use the newest text thread.
                    SetSendChat(merges!.Members(snapshot.Chat.Id).Where(m => Members.Any(c => c.Id == m.Id))
                        .OrderByDescending(m => m.LastActivity).FirstOrDefault()?.Id);
                    return;
                }
                cursor = page.OldestCursor;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        if (version == generation && sendChatId is null)
            Status = "Choose a service in Send using. The last incoming message could not be checked yet.";
    }
    public bool SelectSendRoute(string id)
    {
        if (!IsMerged || !Members.Any(m => m.Id == id)) return false;
        // Replies temporarily select their original source without replacing the saved manual choice.
        SetSendChat(id); return true;
    }
    public void RefreshAutomaticSendRoute() => RefreshMergedRoutes();

    /// <summary>What Details offers to add to the open merged conversation ("Also on Instagram"); empty for anything else.</summary>
    public IReadOnlyList<MergeSuggestion> AlsoOn(IEnumerable<ChatSummary> loaded, DateTimeOffset now) =>
        IsMerged && merges is not null && ActiveChatId is { } id ? merges.AlsoOn(id, loaded, now) : [];
    public string ServiceRoute(ChatSummary chat) => merges?.ServiceRouteLabel(chat) ?? chat.Network;
    // Reads the open merged conversation again once the user has added to it. Not through the refresh command, which
    // declines while a batch is being applied; this retires that batch instead. The rows already drawn stay, and paging
    // carries on from where it was, now across the added conversation too (HistoryMerge.KeepOlder).
    // The header and Details name the new membership at once, from the merge itself: they waited for the read to render,
    // and a read that failed or was overtaken left them counting the members as they were.
    public Task ReloadMembershipAsync()
    {
        if (!IsMerged) return Task.CompletedTask;
        if (merges?.Find(ActiveChatId!) is not null)
        {
            (Subtitle, SubtitleDetail) = MergedHeader(ActiveChatId!);
            UpdateHeaderBadge();
            OnPropertyChanged(nameof(Accounts));
        }
        return RefreshAsync();
    }

    /// <summary>
    /// Takes conversations that left the open merged conversation out of it, in one render: their rows go, nothing else
    /// changes text, and paging carries on across the members that stay only. The removal was made by the user; nothing
    /// is read, and nothing is marked read. <see cref="ReloadMembershipAsync"/> follows to read the newest page again.
    /// </summary>
    public void DropMembers(IReadOnlySet<string> chatIds)
    {
        if (!IsMerged || current is null || chatIds.Count == 0) return;
        // The sending choice is worked out again from the members that stay; a draft cannot keep a route that has left.
        if (sendChatId is not null && chatIds.Contains(sendChatId)) SetSendChat(null);
        current = WithoutMembers(current, chatIds);
        cache.Put(current);
        Render(true, needsRecovery);
    }

    /// <summary>
    /// A merged history without some of its members: their messages, their place in the member list and their paging.
    /// The combined paging token loses those conversations' streams, and their rows waiting in the calls stream, so loading
    /// earlier messages can never read them back in. A token this cannot read is kept; the merged reader drops whatever
    /// has left when it next pages.
    /// </summary>
    public static HistorySnapshot WithoutMembers(HistorySnapshot snapshot, IReadOnlySet<string> chatIds)
    {
        var cursor = snapshot.Cursor;
        var more = snapshot.HasMore;
        if (cursor is not null && MergedCursorWithout(cursor, chatIds) is { } trimmed) { cursor = trimmed.Cursor; more &= trimmed.More; }
        return snapshot with
        {
            Chat = snapshot.Chat with { MemberChatIds = [.. snapshot.Chat.MemberChatIds.Where(id => !chatIds.Contains(id))] },
            Messages = [.. snapshot.Messages.Where(m => !chatIds.Contains(m.ChatId))],
            Cursor = more ? cursor : null, HasMore = more,
            UnavailableSources = snapshot.UnavailableSources?.Where(id => !chatIds.Contains(id)).ToArray()
        };
    }
    private static (string? Cursor, bool More)? MergedCursorWithout(string cursor, IReadOnlySet<string> chatIds)
    {
        MergedChatReader.MergeCursor? parsed;
        try { parsed = System.Text.Json.JsonSerializer.Deserialize<MergedChatReader.MergeCursor>(Convert.FromBase64String(cursor)); }
        catch (Exception) { return null; }
        if (parsed?.Streams is null || parsed.Members is null) return null;
        var streams = parsed.Streams.Where(s => !chatIds.Contains(s.ChatId))
            .Select(s => s.ChatId == "calls" ? s with { Buffer = [.. s.Buffer.Where(m => !chatIds.Contains(m.ChatId))] } : s).ToArray();
        var more = streams.Any(s => !s.Complete || s.Buffer.Length > 0);
        var next = parsed with { Members = [.. parsed.Members.Where(id => !chatIds.Contains(id))], Streams = streams };
        return (more ? Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next)) : null, more);
    }
}
