using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed record ConversationMember(string Id, string Label, string Network = "", string? RouteLabel = null,
    // The merged conversation's "Always send with" route. Appended.
    bool IsPinned = false,
    // The other person's number in this conversation, else their handle. Appended.
    string? Detail = null,
    // What else sets the route apart where the number does not: Google's route (RCS, SMS), else "Encrypted". Appended.
    string? Kind = null,
    // Where the person last wrote from, which is where a reply goes unless chosen otherwise; Send using says so. Appended.
    bool IsLastIncoming = false)
{
    /// <summary>The services in the order the app always lists them; anything else after, in the order given.</summary>
    public static int ServiceOrder(string network) => network switch
    {
        "Google Messages" => 0, "Messenger" => 1, "Google Voice" => 2, "Instagram" => 3, _ => 4,
    };

    public string DisplayRoute => RouteLabel ?? (Network.Length > 0 ? Network : Label);

    /// <summary>
    /// The route in words, with the number or handle it goes to when another route has the same name: two Google Voice
    /// conversations with one person, one per number, both read "Google Voice" (owner, 2026-11-10). Two at one number
    /// (Google's RCS and SMS threads, a Messenger chat and its encrypted one) are told apart by their kind instead.
    /// </summary>
    public string RouteTo(IEnumerable<ConversationMember> routes)
    {
        var twins = Twins(routes);
        return DistinctNumber(twins) is { } number ? $"{DisplayRoute} to {number}"
            : DistinctKind(twins) is { } kind ? $"{DisplayRoute} · {kind}" : DisplayRoute;
    }

    /// <summary>Whether another of these routes has the same name.</summary>
    public bool IsAmbiguous(IEnumerable<ConversationMember> routes) => Twins(routes).Length > 0;

    /// <summary>The number or handle telling this route from others of the same name; null when the name is unique or they share it.</summary>
    public string? NumberApart(IEnumerable<ConversationMember> routes) => DistinctNumber(Twins(routes));

    /// <summary>What tells this route from others of the same name: its number or handle, else its kind; null when neither does.</summary>
    public string? DistinctDetail(IEnumerable<ConversationMember> routes)
    {
        var twins = Twins(routes);
        return DistinctNumber(twins) ?? DistinctKind(twins);
    }

    private ConversationMember[] Twins(IEnumerable<ConversationMember> routes) => routes.Where(m => m.Id != Id && m.DisplayRoute == DisplayRoute).ToArray();
    private string? DistinctNumber(ConversationMember[] twins) =>
        Detail is { } detail && twins.Length > 0 && twins.All(m => m.Detail != detail) ? detail : null;
    private string? DistinctKind(ConversationMember[] twins) =>
        Kind is { } kind && twins.Length > 0 && twins.All(m => m.Kind != kind) ? kind : null;
}

// recoveryDelay replaces the automatic recheck schedule; the offline checks use it to keep bounded retries fast. Peek is
// what each service already holds in memory, whole copies only, for quoted messages and closed conversations kept current
// (ConversationViewModel.Kept); without it the batch's own lookup is asked, when it can be.
public sealed partial class ConversationViewModel(IChatReader reader, ConversationCache cache, Func<int, TimeSpan>? recoveryDelay = null, MergedConversations? merges = null,
    ReadHereMarks? readHere = null, IHistoryWindowReader? windows = null, IMessagePeek? peek = null)
    : ObservableObject, INoticeSource, IDisposable
{
    private CancellationTokenSource? selection;
    private long generation;
    private HistorySnapshot? current;
    private ChatSummary? selectedChat;
    // What was unread when this conversation was opened, and when: "New messages" is placed from these (UnreadDivider).
    private IReadOnlyDictionary<string, UnreadDivider.Opened> unreadAtOpen = new Dictionary<string, UnreadDivider.Opened>();
    private DateTimeOffset openedAt;
    private CancellationTokenSource? initialFailureDelay;
    private CancellationTokenSource? recoveryRetry;
    private long recoveryWaitVersion = -1;
    private TimeSpan recoveryWaitGrace;
    // The visible recovery settles after one successful recheck. Messenger may still finish its page refresh later;
    // retain that fact separately so its completion event can replace the history without keeping UI in a loop.
    private bool awaitingProviderRefresh;
    private bool needsRecovery;
    private int mediaReloadVersion;
    private CancellationTokenSource? recoveryReveal;
    private bool recoveryVisible;
    // See ProgressIndicator. This pill only ever appears over a conversation that already has its history on screen,
    // so nothing is hidden by waiting: the messages are readable the whole time, and a refresh that finishes inside
    // the delay was never worth moving anything for. Settable so the offline checks can watch both sides of it.
    public TimeSpan ProgressDelay { get; init; } = ProgressIndicator.Delay;
    // No pill inside this long after launch (see ProgressIndicator.StartupQuiet). Settable for the offline checks, which
    // run within seconds of their own launch.
    public TimeSpan StartupQuiet { get; init; } = ProgressIndicator.StartupQuiet;
    // How long an empty conversation's first failure waits for a recovery before it is shown. Settable for the offline
    // checks, which watch both sides of it.
    public TimeSpan InitialFailureGrace { get; init; } = TimeSpan.FromSeconds(1);
        // Messenger can need several sequential fetches to restore attachment rows for a saved page. Keep the
        // unobtrusive recovery indicator up while that useful cached page is on screen instead of flashing an error.
    public TimeSpan CachedRecoveryGrace { get; init; } = TimeSpan.FromSeconds(30);
    private const string MergedRecoveryFailure = "Some linked history is unavailable. Showing the services that answered; refresh to try again.";
    private bool mergedRecoveryExpired;
    private string? mergedRecoveryWarning;
    [ObservableProperty] private string? expectedTransport;
    [ObservableProperty] private bool isLoadingOlder;
    [ObservableProperty] private bool olderLoadFailed;
    [ObservableProperty] private string title = "Your conversations";
    [ObservableProperty] private string? avatarUrl;
    [ObservableProperty] private string? avatarChatId;
    [ObservableProperty] private string? avatarOverrideId;
    [ObservableProperty] private string subtitle = "Choose a chat to read its history.";
    [ObservableProperty] private string? subtitleDetail;
    /// <summary>The one service the conversation is reached through, the subtitle's tooltip; null for several (ConversationHeaderText.OnlyService).</summary>
    [ObservableProperty] private string? subtitleService;
    /// <summary>How many conversations a merged conversation links (ConversationHeaderText.LinkedCount); 0 for anything else. The header's link badge.</summary>
    [ObservableProperty] private int linkedCount;
    [ObservableProperty] private string status = "";
    // See INoticeSource: shown unless it is progress or success, which the conversation itself already reports.
    public bool StatusIsAdvisory { get; private set; }
    private bool markingAdvisory;
    partial void OnStatusChanging(string value) { if (!markingAdvisory) StatusIsAdvisory = false; }
    private void Advisory(string text)
    {
        var changedKind = !StatusIsAdvisory;
        markingAdvisory = true; StatusIsAdvisory = true;
        try { if (Status == text) { if (changedKind) OnPropertyChanged(nameof(Status)); } else Status = text; }
        finally { markingAdvisory = false; }
    }
    private void Actionable(string text)
    {
        var changedKind = StatusIsAdvisory;
        StatusIsAdvisory = false;
        if (Status == text) { if (changedKind) OnPropertyChanged(nameof(Status)); } else Status = text;
    }
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isWaitingForInitialRecovery;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool hasConversation;
    public bool ShowInitialLoading => HasConversation && (IsBusy || IsWaitingForInitialRecovery) && Messages.Count == 0 && !IsLoadingOlder;
    public bool ShowRecoveryLoading => HasConversation && IsWaitingForInitialRecovery && recoveryVisible && Messages.Count > 0;
    [ObservableProperty] private bool hasMembers;
    [ObservableProperty] private ConversationMember? selectedMember;
    private string? activeChatId;
    public string? ActiveChatId
    {
        get => activeChatId;
        private set { if (activeChatId == value) return; activeChatId = value; cache.Showing(this, value); ExpectedTransport=null; OnPropertyChanged(); OnPropertyChanged(nameof(IsMerged));
            if (!IsMerged) SetSendChat(value); else SetSendChat(null);
            UpdateSourceChoices();
            ActiveChatChanged?.Invoke(this, value); }
    }
    public ChatSummary? CurrentChat => current?.Chat ?? selectedChat;
    public ObservableCollection<ConversationMember> Members { get; } = [];
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];
    public event EventHandler<bool>? HistoryChanged; // true = preserve the current reading anchor
    public bool IsContentOnlyUpdate { get; private set; }
    // Counts messages that arrived live at the end, so the view can tell a scroll for a new message (which glides) from
    // one for opening or refreshing (which does not).
    public long LiveArrivals { get; private set; }
    public DateTimeOffset LastArrivalAt { get; private set; }
    // The rendered history is the saved copy from disk while the phone's current history is still loading; it may be
    // missing recent messages.
    public bool IsShowingSavedHistory { get; private set; }
    private bool arrivingAfterSaved;

    private bool CanRefresh() => HasConversation && !IsBusy;
    private bool CanLoadOlder() => HasMore && !IsBusy;
    partial void OnIsBusyChanged(bool value) { RefreshCommand.NotifyCanExecuteChanged(); LoadOlderCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowInitialLoading)); }
    partial void OnIsWaitingForInitialRecoveryChanged(bool value)
    {
        // The one place recovery starts and stops, so the wait before the pill appears is timed from here rather
        // than from each of the several callers that put the conversation into this state.
        if (value) BeginRecoveryReveal(); else EndRecoveryReveal();
        OnPropertyChanged(nameof(ShowInitialLoading)); OnPropertyChanged(nameof(ShowRecoveryLoading));
    }
    private void BeginRecoveryReveal()
    {
        if (recoveryVisible || recoveryReveal is not null) return;
        // Recovery that begins inside the launch quiet period is the start-up catch-up: every provider is restoring, and
        // the messages are readable throughout. The user asked for no pill for it at all, however long it runs (Messenger
        // has taken nine seconds); a failure still reaches its actionable notice after the bounded grace.
        if (ProgressIndicator.QuietRemaining(StartupQuiet) > TimeSpan.Zero) return;
        if (ProgressDelay <= TimeSpan.Zero) { recoveryVisible = true; return; }
        var reveal = new CancellationTokenSource();
        recoveryReveal = reveal;
        _ = RevealRecoveryAsync(reveal, ProgressDelay, generation);
    }
    private async Task RevealRecoveryAsync(CancellationTokenSource reveal, TimeSpan wait, long version)
    {
        try { await Task.Delay(wait, reveal.Token); }
        catch (OperationCanceledException) { return; }
        // Recovery that ended, or a different conversation opened while this was waiting, has nothing to announce.
        if (version != generation || !ReferenceEquals(recoveryReveal, reveal) || !IsWaitingForInitialRecovery) return;
        recoveryVisible = true;
        OnPropertyChanged(nameof(ShowRecoveryLoading));
    }
    private void EndRecoveryReveal()
    {
        var reveal = recoveryReveal;
        recoveryReveal = null;
        if (reveal is not null) { reveal.Cancel(); reveal.Dispose(); }
        recoveryVisible = false;
    }
    partial void OnIsLoadingOlderChanged(bool value) { OnPropertyChanged(nameof(ShowInitialLoading)); OnPropertyChanged(nameof(ShowFilteredLoadMore)); }
    partial void OnHasMoreChanged(bool value) { LoadOlderCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowFilteredLoadMore)); }
    partial void OnOlderLoadFailedChanged(bool value) => OnPropertyChanged(nameof(ShowFilteredLoadMore));
    partial void OnHasConversationChanged(bool value) { RefreshCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowInitialLoading)); }

    public async Task OpenAsync(ChatSummary chat)
    {
        var (version, token) = BeginSelection();
        if (merges is not null)
        {
            try { await merges.EnsureLoadedAsync(); }
            catch (Exception) { if (version == generation) { Status = "Couldn't open this conversation's linked chats. Try again."; IsBusy = false; } return; }
            if (version != generation) return;
            if (merges.DisplayId(chat.Id) is { } displayId && displayId != chat.Id) chat = merges.Summary(displayId);
        }
        selectedChat = chat;
        openedAt = DateTimeOffset.UtcNow;
        // A conversation read here privately still has the service's older watermark; the line goes after what was read here.
        IEnumerable<ChatSummary> sources = merges is not null && MergedConversations.IsMerged(chat.Id) ? merges.Members(chat.Id) : [chat];
        unreadAtOpen = UnreadDivider.AtOpen(readHere is null ? sources : sources.Select(readHere.Apply));
        AvatarUrl = chat.AvatarUrl;
        AvatarChatId = chat.Id;
        AvatarOverrideId = AvatarOverrideIdentity.ForChat(chat);
        current = null;
        // A service filter belongs to one visit to one merged conversation; opening anything, this one included, ends it.
        ResetSourceFilter(); ClearRouteNotice();
        Messages.Clear(); Members.Clear(); SelectedMember = null; HasMembers = false; HasMore = false; OlderLoadFailed = false;
        Title = chat.Title;
        // A merged conversation names its services from the first frame. Its summary's "N conversations" belongs to the
        // list, and the header showed it until the first render replaced it.
        if (merges is not null && MergedConversations.IsMerged(chat.Id)) (Subtitle, SubtitleDetail) = MergedHeader(chat.Id);
        else { Subtitle = ConversationHeaderText.Subtitle(chat, chat.ExpectedTransport); SubtitleDetail = null; }
        UpdateHeaderBadge();
        HasConversation = true; Advisory("Loading history…");
        OnPropertyChanged(nameof(Accounts));
        try
        {
            if (MergedConversations.IsMerged(chat.Id))
            {
                // Opened again while something is held for its route — words typed, a reply, an attachment, a recording —
                // keeps that route rather than letting go of it for the read: the route would otherwise be none for a
                // moment and then whatever the history said, and what was held for it would go somewhere else or be lost.
                var keep = ActiveChatId == chat.Id && sendChatId is { } held && CanChangeAutomaticRoute?.Invoke() == false &&
                    merges?.Members(chat.Id).Any(m => m.Id == held && !MergedConversations.IsCall(m) && !m.IsReadOnly) == true;
                ActiveChatId = chat.Id;
                if (!keep) SetSendChat(null);
                // A pinned route does not wait for the history: it is the route whatever the history says, so the composer
                // names it from the first frame instead of naming nothing and then it. Not while its service needs signing
                // in again, when the history decides as it would without the pin.
                if (merges?.Find(chat.Id)?.PinnedRoute is { } pin &&
                    merges.Members(chat.Id).Any(m => m.Id == pin.ChatId && !MergedConversations.IsCall(m) && !m.IsReadOnly && !merges.NeedsSignIn(m)))
                    RefreshMergedRoutes();
                UpdateSourceChoices();
                await LoadInitialAsync(chat, false, version, token);
                if (version == generation) { RefreshMergedRoutes(); await FindEarlierIncomingRouteAsync(version, token); }
            }
            else if (chat.MemberChatIds.Count > 0)
            {
                // A merged parent has no send target until its member details choose one.
                ActiveChatId = null;
                var details = new List<ChatSummary>();
                foreach (var id in chat.MemberChatIds)
                {
                    var member = Clean(id)?.Chat ?? await reader.GetChatAsync(id, token);
                    if (version != generation) return;
                    details.Add(member);
                }
                foreach (var member in details) Members.Add(new(member.Id, $"{member.Network} · {member.Title}", member.Network));
                HasMembers = Members.Count > 1;
                var first = details.FirstOrDefault(d => d.Id == chat.DefaultMemberChatId) ?? details[0];
                ActiveChatId = first.Id;
                SelectedMember = Members.First(m => m.Id == first.Id);
                await LoadInitialAsync(first, false, version, token);
            }
            else
            {
                // Switch directly from the old target to the new one. Publishing a transient null made the composer
                // collapse for a frame while uncached history/draft I/O yielded, which looked like the whole bar blinked.
                ActiveChatId = chat.Id;
                ExpectedTransport = chat.ExpectedTransport;
                await LoadInitialAsync(chat, false, version, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) ReportLoadError(ex, version); }
        finally { if (version == generation) IsBusy = false; }
    }

    public async Task OpenMemberAsync(ConversationMember member)
    {
        if (IsMerged)
        {
            if (merges is null || ActiveChatId is not { } id || !Members.Any(m => m.Id == member.Id)) return;
            var choiceVersion = generation;
            var pinned = merges.PinnedRoute(id) is not null;
            try
            {
                await merges.RememberSendingRouteAsync(id, member.Id);
                if (choiceVersion == generation && ActiveChatId == id) SetSendChat(member.Id);
                // Choosing another service ended a pin: the picker's pin mark goes with it.
                if (pinned && choiceVersion == generation && ActiveChatId == id && merges.PinnedRoute(id) is null) RefreshMergedRoutes();
            }
            catch (Exception) { if (choiceVersion == generation) Status = "Could not save the sending service. Try choosing it again."; }
            return;
        }
        if (member.Id == ActiveChatId || !Members.Any(m => m.Id == member.Id)) return;
        var (version, token) = BeginSelection();
        ActiveChatId = member.Id; current = null; Messages.Clear(); OnPropertyChanged(nameof(ShowInitialLoading)); HasMore = false; Advisory("Loading history…");
        try
        {
            var chat = Clean(member.Id)?.Chat ?? await reader.GetChatAsync(member.Id, token);
            if (version != generation) return;
            await LoadInitialAsync(chat, false, version, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) ReportLoadError(ex, version); }
        finally { if (version == generation) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (selectedChat is null) return;
        if (ActiveChatId is null) { await OpenAsync(selectedChat); return; }
        var (version, token) = BeginSelection();
        Advisory("Refreshing history…");
        try
        {
            var detail = await reader.GetChatAsync(ActiveChatId, token);
            if (version != generation) return;
            await LoadInitialAsync(detail, true, version, token);
            if (version == generation) await FindEarlierIncomingRouteAsync(version, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) ReportLoadError(ex, version); }
        finally { if (version == generation) IsBusy = false; }
    }

    // How long a fresh read took, and how long after it the history was drawn and then written away. Timings only;
    // reported rather than logged, since this view model knows nothing about where a log lives.
    public event EventHandler<string>? Timed;
    // One page of earlier (or windowed) history, said the way "history open" is: the read, then the draw after it, and how
    // many rows the read brought. readTicks is the time spent reading, in Stopwatch ticks, summed over a load's pages;
    // readDone is when the last read finished. Called only after the page was drawn, so a cancelled or failed load is silent.
    private void TimedPage(string kind, long readTicks, long readDone, int rows, int pages = 1) =>
        Timed?.Invoke(this, $"history {kind} read_ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(0, readTicks).TotalMilliseconds} " +
            $"render_after_read_ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(readDone).TotalMilliseconds} rows={rows} pages={pages}");
    private async Task LoadInitialAsync(ChatSummary chat, bool refresh, long version, CancellationToken token)
    {
        IsShowingSavedHistory = false;
        cardsKept = 0;
        var snapshot = refresh ? null : cache.Get(chat.Id);
        // A merge kept up to date from its members' events while it was closed (RefreshKeptMergesAsync): drawn at once as a
        // saved copy is, without saying it is cached since it is as current as the events, and then read as one is.
        var kept = snapshot is { NeedsRead: true } ? snapshot : null;
        if (kept is not null) snapshot = null;
        // A list summary can have newer identity metadata than this conversation's warm history. Reusing its messages
        // must not restore the old name/handle/number on a switch back. Merged identities come from live source rows.
        if (snapshot is not null && !MergedConversations.IsMerged(chat.Id)) snapshot = snapshot with { Chat = chat };
        // A fresh read is drawn first and written to the protected store after. The write waits on the store's one gate —
        // shared with the list, drafts, labels and merges — then DPAPI and a SQLite transaction, and every cold open, member
        // switch and refresh used to wait for all of it before anything new reached the screen. It is still awaited here,
        // inside the busy span, so writes keep their order. Serializing the snapshot still happens on this thread as the
        // write begins, right after the render.
        HistorySnapshot? toSave = null;
        // What was on screen while the read ran: a kept merge, a saved copy, or nothing (the open timing line says which).
        var firstDrawn = "none";
        var heldRows = 0;
        long readStarted = 0, readDone = 0;
        if (snapshot is null)
        {
            HistorySnapshot? saved = refresh ? current : kept;
            if (!refresh)
            {
                if (kept is null) saved = await cache.ReadSavedAsync(chat.Id);
                if (version != generation) return;
            }
            // Asked before the first drawing is put together, so adding what this PC holds to it costs the read nothing.
            readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var reading = ReadNewestAsync(chat, refresh, version, token);
            if (!refresh)
            {
                // A copy saved before the user added a conversation to this merge would draw without it, then gain its rows
                // a moment later. It still keeps what was loaded before, below; it just is not shown first.
                var drawable = saved is not null && merges?.AddedSince(chat.Id, saved.Chat.MemberChatIds) != true ? saved : null;
                HistorySnapshot? first;
                try { (first, heldRows) = await WithHeldAsync(chat, drawable, token); }
                catch { Observe(reading); throw; }
                // What its replies quote, while the read runs, so a quote is not drawn as unloaded and then changed.
                if (first is not null && version == generation) await PeekQuotesAsync(first.Messages, null, version, token);
                if (version != generation) { Observe(reading); return; }
                if (first is not null)
                { current = MergedConversations.IsMerged(chat.Id) ? first : first with { Chat = chat };
                    IsShowingSavedHistory = true; Render(false); if (drawable is not null && kept is null) Advisory("Cached history · checking for updates…"); }
                firstDrawn = first is null ? "none" : drawable is null ? "archive" : kept is null ? "saved" : "kept";
            }
            var (detail, page) = await reading;
            if (version != generation) return;
            readDone = System.Diagnostics.Stopwatch.GetTimestamp();
            snapshot = HistoryMerge.KeepOlder(saved, WithCards(saved, Merge(new(detail, [], null, true, new HashSet<string>()), page)), page.HasMore);
            cache.Put(snapshot);
            toSave = snapshot;
        }
        await PeekQuotesAsync(snapshot.Messages, null, version, token);
        // Saved even when the user has moved on, as it always was.
        if (version != generation) { if (toSave is not null) await cache.SaveAsync(toSave); return; }
        current = snapshot;
        var providerRefreshing = snapshot.IsRefreshing;
        needsRecovery = providerRefreshing;
        awaitingProviderRefresh = providerRefreshing;
        if (IsMerged) UpdateMergedRecoveryState();
        // Messages the saved copy was missing arrive at the end like live ones.
        arrivingAfterSaved = IsShowingSavedHistory; IsShowingSavedHistory = false;
        try { Render(false, providerRefreshing); } finally { arrivingAfterSaved = false; }
        if (providerRefreshing)
            BeginRecoveryWait(IsMerged ? mergedRecoveryWarning! : "Could not finish updating this conversation. Try refreshing or check your connection.", version, CachedRecoveryGrace);
        if (toSave is null) return;
        var rendered = System.Diagnostics.Stopwatch.GetTimestamp();
        await cache.SaveAsync(toSave);
        // Cards: rows that kept a link card a helper made, which its fresh copy came without (HistoryMerge.KeepCard).
        var cards = cardsKept; cardsKept = 0;
        Timed?.Invoke(this, $"history open read_ms={Ms(readStarted, readDone)} render_after_read_ms={Ms(readDone, rendered)} save_ms={Ms(rendered, System.Diagnostics.Stopwatch.GetTimestamp())} cards={cards} refresh={refresh} first={firstDrawn} held={heldRows}");
        static long Ms(long from, long to) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
    }

    private async Task<(ChatSummary Detail, Page<ChatMessage> Page)> ReadNewestAsync(ChatSummary chat, bool refresh, long version, CancellationToken token)
    {
        if (!refresh && MergedConversations.IsMerged(chat.Id))
        {
            // A merge's details and its newest rows are each one read per member, and neither needs the other, so
            // they go together: in series they doubled the slowest member's time before the newest rows appeared.
            var details = reader.GetChatAsync(chat.Id, token);
            var rows = reader.GetMessagesAsync(chat.Id, cancellationToken: token);
            try { await Task.WhenAll(details, rows); }
            catch { _ = details.Exception; _ = rows.Exception; throw; }
            return (details.Result, rows.Result);
        }
        var detail = refresh ? chat : await reader.GetChatAsync(chat.Id, token);
        if (version != generation) throw new OperationCanceledException();
        return (detail, await reader.GetMessagesAsync(chat.Id, cancellationToken: token));
    }
    private static void Observe(Task task) => _ = task.ContinueWith(static done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted);

    // The newest messages a provider holds on this PC that the first drawing would otherwise lack (Google's archive, once
    // it is known to hold everything to now): added to the saved copy, member by member from where its own rows end, or
    // the whole first drawing of a conversation with no saved copy. Messages that came while the app was closed are then
    // there at once instead of a second later, behind older rows (2026-10-02). Rows are only added, never replaced, and
    // the read that follows decides what stays. A merge with no saved copy waits as before: its other members would
    // arrive a moment later among these rows.
    private const int HeldRows = 100, ColdHeldRows = 50;
    private async Task<(HistorySnapshot? First, int Added)> WithHeldAsync(ChatSummary chat, HistorySnapshot? saved, CancellationToken token)
    {
        if (windows is null) return (saved, 0);
        try
        {
            if (saved is null)
            {
                if (MergedConversations.IsMerged(chat.Id) ||
                    await windows.ReadHeldAsync(chat.Id, DateTimeOffset.MinValue, ColdHeldRows, token) is not { Messages.Count: > 0 } cold) return (null, 0);
                return (new(chat, cold.Messages, null, true, new HashSet<string>()), cold.Messages.Count);
            }
            var rows = saved.Messages.ToDictionary(MessageIdentity.Key);
            var added = 0;
            foreach (var member in saved.Messages.GroupBy(message => message.ChatId))
            {
                // Only when every message since the copy's newest is held: some of them alone would leave a gap above.
                if (await windows.ReadHeldAsync(member.Key, member.Max(message => message.Timestamp), HeldRows, token) is not { Complete: true } held) continue;
                foreach (var message in held.Messages) if (rows.TryAdd(MessageIdentity.Key(message), message)) added++;
            }
            return added == 0 ? (saved, 0) : (saved with { Messages = OrderMerged(rows.Values) }, added);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (saved, 0); }
    }

    [RelayCommand(CanExecute = nameof(CanLoadOlder))]
    private async Task LoadOlderAsync()
    {
        if (current is null || selection is null) return;
        if (window is not null) { await LoadWindowOlderAsync(); return; }
        var version = generation; var snapshot = current;
        IsBusy = true; IsLoadingOlder = true; OlderLoadFailed = false; Advisory("Loading earlier messages…");
        try
        {
            // One service shown on its own (SourceFilter) can have none of its rows in a combined page, so a load reads on,
            // a bounded number of pages (one with Instagram in the merge), until it has brought some of them; otherwise it
            // is one page, as it always was.
            var shownBefore = ShownCount(snapshot);
            Page<ChatMessage> page;
            long readTicks = 0, readDone, rowsRead = 0;
            int pageCount;
            for (var pages = 1; ; pages++)
            {
                var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                page = await reader.GetMessagesAsync(snapshot.Chat.Id, snapshot.Cursor, selection.Token);
                readDone = System.Diagnostics.Stopwatch.GetTimestamp();
                readTicks += readDone - readStarted; rowsRead += page.Items.Count; pageCount = pages;
                if (version != generation) return;
                // Built on the history as it is now, not as it was before the read: a message that arrived meanwhile is
                // already in current, and the copy taken before the await would drop it.
                var latest = current ?? snapshot;
                // An older page's refresh state says nothing about the newest page, which is what recovery tracks.
                current = Merge(latest, page) with { IsRefreshing = latest.IsRefreshing,
                    // An older-page success cannot verify cached newest rows from a failed source.
                    UnavailableSources = IsMerged ? (latest.UnavailableSources ?? []).Concat(page.UnavailableScopes ?? []).Distinct().ToArray() : page.UnavailableScopes,
                    Warning = latest.Warning ?? page.Warning };
                if (!ReadOnForFilter(current, shownBefore, pages, page)) break;
                cache.Put(current);
                snapshot = current;
            }
            cache.Put(current);
            var loaded = current;
            await PeekQuotesAsync(loaded.Messages, null, version, selection.Token);
            if (version == generation)
            {
                Render(true, needsRecovery);
                TimedPage("older", readTicks, readDone, (int)rowsRead, pageCount);
                if (page.Warning is not null && !(IsMerged && needsRecovery)) Status = page.Warning;
                OlderLoadFailed = Unanswered(page);
            }
            await cache.SaveAsync(loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) { Status = Error(ex); OlderLoadFailed = true; } }
        finally { if (version == generation) IsBusy = false; IsLoadingOlder = false; }
    }

    // A page that says there is more and brings nothing. The server answers so while the service is still refreshing the page it would read, and a later
    // ask finds it (2026-11-01: half a second after a refresh began, in a group whose history the server held). It is not the end of paging: the place is
    // kept, and the list's link tries again. Taken for a repeated cursor, it ended paging and was saved with the conversation, so every open said so.
    private static bool Unanswered(Page<ChatMessage> page) => page.HasMore && page.Items.Count == 0 && page.OldestCursor is null;

    private static HistorySnapshot Merge(HistorySnapshot snapshot, Page<ChatMessage> page)
    {
        if (Unanswered(page)) return snapshot;
        var byId = snapshot.Messages.ToDictionary(MessageIdentity.Key);
        foreach (var message in page.Items) byId[MessageIdentity.Key(message)] = message;
        var messages = byId.Values.OrderBy(m => m.Timestamp).ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var atLimit = messages.Length >= ConversationCache.MaxMessages;
        var seen = snapshot.SeenCursors.Select(SeenKey).ToHashSet();
        var stalled = page.HasMore && (page.OldestCursor is null || !seen.Add(SeenKey(page.OldestCursor)));
        // An older page says nothing about the newest rows: a copy still to be read, or holding rows it took in while it was
        // closed, stays so (Load earlier after an opening whose read failed).
        return new(snapshot.Chat, messages.TakeLast(ConversationCache.MaxMessages).ToArray(), page.OldestCursor,
            page.HasMore && !stalled && !atLimit, seen, atLimit, stalled, page.IsRefreshing, page.UnavailableScopes, page.Warning,
            snapshot.NeedsRead, snapshot.KeptAfter);
    }
    // The stall test only asks whether a cursor came round again. A merged conversation's cursor carries every service's
    // unread page, tens of kilobytes each, and every page's was kept and written out with the history on every save; a
    // long one is remembered by its digest instead. Keys kept whole before this are made digests as they are read.
    private static string SeenKey(string cursor) => cursor.Length <= 256 ? cursor
        : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cursor)));

    private (string Subtitle, string Detail) MergedHeader(string chatId) =>
        ConversationHeaderText.Merged(merges!.Members(chatId), merges.ServiceRouteLabel, sourceFilter is null ? null : SourceFilterLabel);
    // Set properties raise nothing when unchanged, so a render that changes nothing redraws nothing.
    private void UpdateHeaderBadge()
    {
        if (CurrentChat is not { } chat) { SubtitleService = null; LinkedCount = 0; return; }
        var merged = merges is not null && MergedConversations.IsMerged(chat.Id);
        var sources = merged ? merges!.Members(chat.Id) : [chat];
        SubtitleService = ConversationHeaderText.OnlyService(sources);
        LinkedCount = merged ? ConversationHeaderText.LinkedCount(sources) : 0;
    }

    // Sent messages whose receipt a click put on show, by MessageIdentity.Key: a row is drawn afresh on every render and its
    // container recycled, so the choice is kept here. Another click puts it away.
    private readonly HashSet<string> receiptsShown = [];

    public void ToggleReceipt(MessageItemViewModel row)
    {
        if (!row.Message.IsOutgoing || row.Message.IsDeleted) return;
        var key = MessageIdentity.Key(row.Message);
        if (!receiptsShown.Remove(key)) receiptsShown.Add(key);
        Render(preserveAnchor: true);
    }

    private void Render(bool preserveAnchor, bool preserveRecovery = false)
    {
        var rendering = UiActivity.Start();
        try { RenderCore(preserveAnchor, preserveRecovery); }
        finally { UiActivity.History(rendering); }
    }
    // One source conversation's encryption as far as its own loaded messages and saved switch show it. Unknown stays
    // null, so Message details says nothing rather than "not encrypted".
    private static ChatSummary WithEncryption(ChatSummary source, IEnumerable<ChatMessage> messages) =>
        source with { IsEncrypted = ConversationEncryption.Decide(source, messages).Encrypted };
    private void RenderCore(bool preserveAnchor, bool preserveRecovery)
    {
        if (current is null) return;
        OnPropertyChanged(nameof(Accounts));
        if (!preserveRecovery) CancelInitialFailureDelay();
        RefreshMergedRoutes();
        UpdateSourceChoices();
        ExpectedTransport = IsMerged ? SendChat?.ExpectedTransport : current.Chat.ExpectedTransport;
        if (IsMerged && merges is not null) (Subtitle, SubtitleDetail) = MergedHeader(current.Chat.Id);
        else { Subtitle = ConversationHeaderText.Subtitle(current.Chat, current.Chat.ExpectedTransport); SubtitleDetail = null; }
        UpdateHeaderBadge();
        // A provider that says who read each message but not where each reader stopped gets its faces placed here. One
        // service shown on its own (SourceFilter) leaves every other member's rows out, system lines included.
        var shown = ShownChatIds();
        var visible = ReceiptRules.DeriveReadMarks(DrawnMessages(current).Where(m => !m.IsHidden && (shown is null || shown.Contains(m.ChatId)))
            .Select(WithPendingChange).OfType<ChatMessage>().Select(WithPendingReaction).ToArray());
        var lookups = visible.GroupBy(m => m.ChatId).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, ChatMessage>)g.ToDictionary(m => m.Id));
        // Reactions, receipts and edits must not be treated as newly arriving messages.
        IsContentOnlyUpdate = IsLiveRender && Messages.Select(m => MessageIdentity.Key(m.Message)).SequenceEqual(visible.Select(MessageIdentity.Key));
        HistoryChanged?.Invoke(this, preserveAnchor);
        ChatMessage? previous = null;
        var desired = new List<MessageItemViewModel>();
        var newest=visible.LastOrDefault(m=>!m.IsDeleted && m.Type!="system" && !ScheduledSendRows.Is(m));
        var newestKey = newest is null ? null : MessageIdentity.Key(newest);
        // The lock goes under the newest message, as Google shows it, and only when encryption is positively known —
        // for that message's own conversation, since each linked service has its own answer.
        var chat=WithEncryption(current.Chat, current.Messages);
        var sourceChats = IsMerged ? visible.Select(m => m.ChatId).Distinct().ToDictionary(id => id, id =>
            WithEncryption(SourceChat(id), current.Messages.Where(m => m.ChatId == id))) : null;
        // One receipt, on your newest sent message (owner, 2026-10-26; the phone's too). A reply after it retires it once
        // it is known to have arrived; while it says only Sent or Sending it stays, since the reply proves nothing of it. A
        // merged conversation is one conversation on screen: one receipt across every linked service, in that message's own
        // service's terms. A click on a bubble shows its own (ToggleReceipt); older ones are also in the tooltip and Message details.
        var lastSent = visible.LastOrDefault(m => m.IsOutgoing && !m.IsDeleted && m.Type != "system" && !ScheduledSendRows.Is(m));
        var lastSentKey = lastSent is null ? null : MessageIdentity.Key(lastSent);
        var replied = lastSent is not null && visible.SkipWhile(m => !ReferenceEquals(m, lastSent)).Skip(1).Any(m => !m.IsOutgoing && !m.IsDeleted && m.Type != "system");
        if (lastSent is not null && replied && ReceiptRules.IsDelivered(lastSent, sourceChats?.GetValueOrDefault(lastSent.ChatId) ?? chat)) lastSentKey = null;
        for(var index=0;index<visible.Length;index++)
        {
            var message=visible[index];
            var source = sourceChats?.GetValueOrDefault(message.ChatId) ?? chat;
            // By identity, not id: two linked services can use the same id for different messages.
            var key = MessageIdentity.Key(message);
            var isNewest = newestKey is not null && key == newestKey;
            var carries = key == lastSentKey || message.IsOutgoing && receiptsShown.Contains(key);
            desired.Add(new(message,previous,source,lookups[message.ChatId],index+1<visible.Length?visible[index+1]:null,
                carries, isNewest, IsMerged, quotes));
            previous=message;
        }
        PhotoGroups.Apply(desired, IsMerged ? null : chat.Network, exactPhotoBatches);
        PlaceUnreadDivider(visible, desired);
        var wanted = desired.Select(m => MessageIdentity.Key(m.Message)).ToHashSet();
        var removed = false;
        for (var i = Messages.Count - 1; i >= 0; i--)
            if (!wanted.Contains(MessageIdentity.Key(Messages[i].Message))) { Messages.RemoveAt(i); removed = true; }
        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            row.SetMediaReloadVersion(mediaReloadVersion);
            if (i < Messages.Count && MessageIdentity.Key(Messages[i].Message) == MessageIdentity.Key(row.Message))
            {
                if (!Messages[i].HasSameContent(row)) Messages[i].UpdateFrom(row);
                continue;
            }
            var existing = -1;
            for (var j = i + 1; j < Messages.Count; j++)
                if (MessageIdentity.Key(Messages[j].Message) == MessageIdentity.Key(row.Message)) { existing = j; break; }
            if (existing >= 0)
            {
                Messages.Move(existing, i);
                if (!Messages[i].HasSameContent(row)) Messages[i].UpdateFrom(row);
            }
            else
            {
                // Only a message arriving live at the end slides in. History loads, older inserts and a pending
                // message replaced by its echo (a removal in the same render) don't.
                row.AnimateEntrance = (IsLiveRender || arrivingAfterSaved) && !removed && i == Messages.Count;
                if (row.AnimateEntrance) { LiveArrivals++; LastArrivalAt = DateTimeOffset.UtcNow; }
                Messages.Insert(i, row);
            }
        }
        HasMore = window?.MoreOlder ?? current.HasMore;
        // A count of what loaded is useful when debugging and meaningless to anyone reading a conversation, so it never
        // reaches the notice bar. A history that will not advance, or that has nothing in it, still does.
        if (IsMerged && mergedRecoveryExpired) Actionable(mergedRecoveryWarning ?? MergedRecoveryFailure);
        else if (IsMerged && needsRecovery) Advisory("Updating conversation…");
        else if (window is not null) Advisory($"{visible.Length} older messages shown · Latest returns to the newest");
        else if (current.PaginationStalled) Status = "Couldn't load earlier messages. Refresh to try again.";
        else if (current.AtLimit) Advisory("1,000 messages loaded. Refresh to return to recent history.");
        else if (visible.Length == 0 && shown is not null)
        {
            // Earlier pages are being read for it, or can be (the list's own link); only a history with none at all says so.
            if (HasMore) Advisory($"No {SourceFilterLabel} messages loaded yet · Earlier messages available");
            else Status = $"No {SourceFilterLabel} messages in this conversation's history.";
        }
        else if (visible.Length == 0) Status = HasMore ? "No messages to show here yet. Load earlier messages." : "No messages available in this conversation.";
        else Advisory($"{visible.Length} messages loaded" + (HasMore ? " · Earlier messages available" : " · End of available history"));
        LoadVisibleLinkPreviews(visible);
        OnPropertyChanged(nameof(ShowInitialLoading)); OnPropertyChanged(nameof(ShowRecoveryLoading));
    }

    // "New messages" on the row of the first message unread at the open; on a photo set's first photo when that message is
    // a later one in the set, which draws as the set.
    private void PlaceUnreadDivider(IReadOnlyList<ChatMessage> visible, List<MessageItemViewModel> desired)
    {
        var target = UnreadDivider.Anchor(visible, unreadAtOpen, openedAt) is { } anchor
            ? desired.FindIndex(row => MessageIdentity.Key(row.Message) == anchor) : -1;
        while (target > 0 && desired[target].IsPhotoGroupMember) target--;
        for (var index = 0; index < desired.Count; index++) desired[index].SetUnreadDivider(index == target);
    }

    private (long Version, CancellationToken Token) BeginSelection()
    {
        CancelInitialFailureDelay();
        mergedRecoveryExpired = false; mergedRecoveryWarning = null; needsRecovery = false; awaitingProviderRefresh = false;
        // A quote memory could not give is asked again on the next opening; the service may hold it by then.
        quoteMisses.Clear();
        // A refresh or another conversation ends a window of older history; the view covers the change as Latest does.
        if (window is not null) { WindowLeaving?.Invoke(this, EventArgs.Empty); window = null; }
        selection?.Cancel(); selection?.Dispose(); selection = new();
        IsBusy = true;
        return (++generation, selection.Token);
    }
    private void ReportLoadError(Exception ex, long version)
    {
        var message = Error(ex);
        var cached = current is not null || Messages.Count > 0;
        if (IsNotReady(ex))
        {
            // A provider that is still connecting announces readiness; keep the useful page and recheck it quietly.
            needsRecovery = true;
            // With nothing to show yet the pane's own ring says it is loading, so a reconnect is waited out as long as over
            // history: after a second, "Instagram is reconnecting." stood as a notice over a launch-time conversation that
            // connected by itself two seconds later (2026-10-02).
            BeginRecoveryWait(message, version, CachedRecoveryGrace);
            return;
        }
        // Any other failure is reported as before: at once over existing history, after a short grace otherwise.
        if (cached) { Status = message; return; }
        BeginRecoveryWait(message, version, InitialFailureGrace, retry: false);
    }
    // A sign-in renewed invisibly is waited out the same way, and as long with nothing on screen as with history there:
    // its "renewing" notice appeared at launch and was gone again a few seconds later.
    private static bool IsNotReady(Exception ex) => ex is BackendException { Waits: true };
    private static bool IsTransient(Exception ex) => ex is BackendException { Transient: true };
    // One recovery episode covers all partial pages during startup. A later event must neither restart its
    // deadline nor erase a terminal notice until every source has answered with its current history.
    private void UpdateMergedRecoveryState()
    {
        if (current is null) return;
        awaitingProviderRefresh = current.IsRefreshing;
        if (!current.IsRefreshing)
        {
            if (needsRecovery || mergedRecoveryExpired) mediaReloadVersion++;
            needsRecovery = false; mergedRecoveryExpired = false; mergedRecoveryWarning = null;
            return;
        }
        mergedRecoveryWarning = current.Warning ?? MergedRecoveryFailure;
        needsRecovery = !mergedRecoveryExpired;
    }
    private void BeginRecoveryWait(string message, long version, TimeSpan grace, bool retry = true)
    {
        // A short initial-failure timer is replaced when usable cached content arrives and the provider reports that
        // its background refresh is still running. Repeated stale pages keep the existing deadline and retry loop.
        if (initialFailureDelay is null || recoveryWaitVersion != version || recoveryWaitGrace < grace)
        {
            CancelInitialFailureDelay();
            var delay = new CancellationTokenSource();
            initialFailureDelay = delay;
            recoveryWaitVersion = version;
            recoveryWaitGrace = grace;
            _ = RevealInitialFailureAsync(message, version, delay, grace);
        }
        IsWaitingForInitialRecovery = true;
        var cached = current is not null || Messages.Count > 0;
        Advisory(cached ? "Updating conversation…" : "Waiting for conversation…");
        if (retry) EnsureRecoveryRetry(version);
    }
    private async Task RevealInitialFailureAsync(string message, long version, CancellationTokenSource delay, TimeSpan grace)
    {
        try { await Task.Delay(grace, delay.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            while (IsBusy && version == generation && ReferenceEquals(initialFailureDelay, delay))
                await Task.Delay(TimeSpan.FromMilliseconds(100), delay.Token);
        }
        catch (OperationCanceledException) { return; }
        if (version != generation || !ReferenceEquals(initialFailureDelay, delay)) return;
        initialFailureDelay = null;
        delay.Dispose();
        IsWaitingForInitialRecovery = false;
        if (IsMerged)
        {
            mergedRecoveryExpired = true;
            needsRecovery = false;
            CancelInitialFailureDelay();
            Actionable(mergedRecoveryWarning ?? Refusal(message, version));
        }
        else Actionable(Refusal(message, version));
    }
    // The newest outright refusal a recovery retry met in this selection, said instead of the waiting words it began with.
    private (long Version, string Text)? recoveryRefusal;
    private string Refusal(string waiting, long version) => recoveryRefusal is { } refused && refused.Version == version ? refused.Text : waiting;
    private void CancelInitialFailureDelay()
    {
        var delay = initialFailureDelay;
        initialFailureDelay = null;
        if (delay is not null) { delay.Cancel(); delay.Dispose(); }
        var retry = recoveryRetry;
        recoveryRetry = null;
        retry?.Cancel();
        recoveryWaitVersion = -1;
        recoveryWaitGrace = default;
        IsWaitingForInitialRecovery = false;
    }
    private string Error(Exception ex) => (ex is BackendException backend ? backend.Message : "Could not load this conversation. Try refreshing or check your connection.") + (current is null ? "" : " Previously loaded history may be out of date.");
    // Leaves the conversation, keeping the history cache other windows share and this view's send confirmations.
    public void Deselect()
    {
        generation++; CancelInitialFailureDelay(); selection?.Cancel(); current = null; window = null; selectedChat = null; ActiveChatId = null; IsShowingSavedHistory = false; needsRecovery = false; awaitingProviderRefresh = false;
        mergedRecoveryExpired = false; mergedRecoveryWarning = null;
        ResetSourceFilter(); ClearRouteNotice();
        Messages.Clear(); Members.Clear(); SelectedMember = null; HasMembers = false; HasMore = false;
        HasConversation = false; IsBusy = false; AvatarUrl = AvatarChatId = AvatarOverrideId = null; Title = "Your conversations"; Subtitle = "Choose a chat to read its history."; SubtitleDetail = null; SubtitleService = null; LinkedCount = 0; Status = "";
        OnPropertyChanged(nameof(Accounts));
    }
    // There is deliberately no Clear: the history cache is shared with every other window's conversation, so leaving
    // one only deselects it, and a deleted conversation forgets just its own sends (ForgetSends).
    public void Dispose() { if (outbox is not null) { outbox.Changed -= OutboxChanged; outbox.Resolved -= OutboxResolved; outbox.BatchResolved -= OutboxBatchResolved; } if (scheduled is not null) scheduled.Changed -= ScheduledChanged; Deselect(); selection?.Dispose(); }
}
