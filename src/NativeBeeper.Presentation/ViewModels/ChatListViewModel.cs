using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public enum InboxScope { Inbox, Unread, Archived, Spam, Drafts }
public sealed record ChatFilter(string Label, string? AccountId = null, string? Network = null);

// What a conversation is, rather than which service it came from. Google Voice threads every call and every voicemail
// the way it threads a conversation, so they sit in the inbox among ordinary chats with no way to see only one sort.
// It is a second choice beside the service rather than more service rows, because the useful question is usually a
// combination: this account's voicemail, or calls across everything.
public enum ChatKind { All, Messages, Calls, Voicemails }

// Why the list is empty, as ChatListViewModel.EmptyState decides it; the names are also the log's words.
public enum EmptyListReason { NoService, Filtered, LoadedOnly, Empty }
public sealed record EmptyListState(string Text, EmptyListReason Reason, bool OffersAddAccount, bool OffersClearFilters);

public sealed partial class ChatListViewModel(IChatReader reader, PrivateState? state = null, MergedConversations? merges = null, ChatLabels? labels = null,
    ComposerDrafts? drafts = null, ReadHereMarks? readHere = null) : ObservableObject, IDisposable, INoticeSource
{
    // See ProgressIndicator: how long a row works before it says so. Settable so the offline checks can watch both
    // sides of it without waiting out the real delay.
    public TimeSpan ProgressDelay { get; init; } = ProgressIndicator.Delay;
    // How long batch work must have waited before a redraw inside it is reported (NoteWaitingRedraw). Settable for the
    // offline checks, like ProgressDelay.
    public TimeSpan WaitingRedrawReport { get; init; } = TimeSpan.FromSeconds(1);
    private readonly List<ChatSummary> loaded = [];
    private readonly HashSet<string> cursors = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request;
    private string? cursor;
    private int generation;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isListLoading;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string status = "Connect to load conversations.";
    private bool markingAdvisory;
    public bool StatusIsAdvisory { get; private set; } = true;
    partial void OnStatusChanging(string value) { if (!markingAdvisory) StatusIsAdvisory = false; }
    private void Advisory(string value)
    {
        markingAdvisory = true; StatusIsAdvisory = true;
        try { Status = value; }
        finally { markingAdvisory = false; }
    }
    [ObservableProperty] private string title = "Chats";
    [ObservableProperty] private InboxScope scope;
    public FillableCollection<ChatItemViewModel> Chats { get; } = new();
    public event EventHandler? NewMessagesReceived;
    public event EventHandler? PinnedChatsChanged;
    private string pinnedSignature = "";
    private readonly List<string> pinnedOrder = [];
    private string? pinnedOrderProfile;
    // Pinned inbox chats in list order, regardless of the current view or account filter (Ctrl+1–9, jump list).
    public IReadOnlyList<ChatSummary> PinnedChats { get; private set; } = [];
    // Every conversation the inbox shows, pinned or not, whatever the current view or filter: what the taskbar jump list's
    // suggestions are chosen from (ConversationSuggestions). Updated with PinnedChats, without an event of its own.
    public IReadOnlyList<ChatSummary> InboxChats { get; private set; } = [];
    /// <summary>
    /// Every conversation loaded, before the inbox's own filters narrow it. New chat suggests people from this
    /// rather than from <see cref="Chats"/>, so an unread-only or per-service view of the inbox does not quietly
    /// decide who New chat is allowed to offer.
    /// </summary>
    public IReadOnlyList<ChatSummary> AllLoadedChats => [.. loaded];
    public void RebuildMerged() => Rebuild();

    /// <summary>
    /// The account this PC is signed in as on a service, as that service itself names it. Every provider already
    /// marks the account's own participant in a conversation — Google Messages from <c>IsMe</c>, Messenger and
    /// Instagram from their <c>self</c> flag — so this needs no call, no new protocol field and nothing kept in
    /// sync with a sign-in. What it cannot do is answer before that service has loaded a conversation, and a
    /// service with none returns null rather than a guess.
    /// </summary>
    public ChatParticipant? SelfOn(string network)
    {
        foreach (var chat in loaded)
        {
            if (!string.Equals(chat.Network, network, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var person in chat.Participants)
                if (person.IsSelf == true) return person;
        }
        return null;
    }

    public event EventHandler? UnreadChatsChanged;
    // Unread conversations that aren't muted or archived, in every view (taskbar and tray badge).
    public int UnreadChatCount { get; private set; }
    /// <summary>
    /// The conversations <see cref="UnreadChatCount"/> counts, newest first, at most <see cref="UnreadChatsShown"/> of
    /// them (the tray menu lists these). The same rows by the same test, so the list and the number never disagree.
    /// </summary>
    public IReadOnlyList<ChatSummary> UnreadChats { get; private set; } = [];
    public const int UnreadChatsShown = 10;
    // The last rebuild's presented rows and merge parents, for UnreadCountFor: the Services menu asks as it opens.
    private ChatSummary[] lastPresented = [];
    private HashSet<string> lastParentIds = [];
    public ObservableCollection<ChatFilter> Filters { get; } = [new("All services")];
    private IReadOnlyList<ChatFilter> selectedFilters = Array.Empty<ChatFilter>();
    public IReadOnlyList<ChatFilter> SelectedFilters => selectedFilters;
    // Single-choice callers (Commands, New chat) retain their existing behavior. Multiple choices have no preferred account.
    public ChatFilter? SelectedFilter
    {
        get => selectedFilters.Count == 1 ? selectedFilters[0] : Filters.FirstOrDefault();
        set => SetServiceFilters(value is null ? [] : [value]);
    }
    public void SetServiceFilters(IEnumerable<ChatFilter> choices)
    {
        var next = choices.Where(f => f.AccountId is not null || f.Network is not null).Distinct().ToArray();
        if (selectedFilters.SequenceEqual(next)) return;
        selectedFilters = Array.AsReadOnly(next);
        OnPropertyChanged(nameof(SelectedFilters)); OnPropertyChanged(nameof(SelectedFilter));
        OnPropertyChanged(nameof(FilterDescription)); OnPropertyChanged(nameof(HasAccountFilter));
        OnPropertyChanged(nameof(AccountFilterLabel)); Rebuild();
    }
    public void ToggleServiceFilter(ChatFilter filter)
    {
        if (filter.AccountId is null && filter.Network is null) SetServiceFilters([]);
        else SetServiceFilters(selectedFilters.Contains(filter) ? selectedFilters.Where(f => f != filter) : selectedFilters.Append(filter));
    }
    public string FilterDescription
    {
        get
        {
            var filter = HasAccountFilter ? AccountFilterLabel : null;
            var scopeLabel = Scope == InboxScope.Inbox ? null : Scope.ToString();
            var kindLabel = Kind == ChatKind.All ? null : Kind.ToString();
            return string.Join(" · ", new[] { scopeLabel, filter, kindLabel, LabelFilterName }.OfType<string>()) is { Length: > 0 } label ? label : "All";
        }
    }
    public bool HasAccountFilter => selectedFilters.Count > 0;
    /// <summary>
    /// Whether anything narrows the list: a view other than the inbox, a service, a kind or a label. Exactly what
    /// ClearFilters(InboxScope.Inbox) would change, so a Clear filters link shown on this does something when pressed.
    /// </summary>
    public bool HasActiveFilters => Scope != InboxScope.Inbox || HasAccountFilter || Kind != ChatKind.All || HasLabelFilter;

    /// <summary>
    /// What an empty list says, and which ways out it offers. With no service set up there is nothing to filter or load:
    /// one line and a way to add an account. A first run showed "No matches in loaded chats." with Clear filters and
    /// Load more links over a list that had nothing to match (2026-10-02 clean-PC run). Otherwise as before, except that
    /// Clear filters is offered only while a filter is on.
    /// </summary>
    public static EmptyListState EmptyState(bool noServiceSetUp, bool hasMore, bool filtered) => noServiceSetUp
        ? new("No accounts connected yet.", EmptyListReason.NoService, OffersAddAccount: true, OffersClearFilters: false)
        : new(hasMore ? "No matches in loaded chats." : "No conversations in this view.",
            filtered ? EmptyListReason.Filtered : hasMore ? EmptyListReason.LoadedOnly : EmptyListReason.Empty,
            OffersAddAccount: false, OffersClearFilters: filtered);
    // The service switcher appears only when there is a meaningful choice. Account rows are added only for a
    // provider that actually exposes more than one account, so two filters means "all" plus one service.
    public bool HasMultipleSources => Filters.Count > 2;
    private bool showServiceBadges = true;
    public bool ShowServiceBadges
    {
        get => showServiceBadges;
        set
        {
            if (showServiceBadges == value) return;
            showServiceBadges = value;
            foreach (var item in Chats) item.ShowNetwork = multipleSources && value;
        }
    }
    // Whether the list holds more than one real account, as of the last rebuild; the badge setting and the rebuild share
    // it. A merged row's "local" account is not a service, so one account with a merge in it still shows no badges —
    // the setter counted that way and the rebuild did not, and turning badges on hid them until the next rebuild.
    private bool multipleSources;
    private bool CountSources() => loaded.Select(c => c.AccountId).Where(account => account != MergedAccountId)
        .Distinct(StringComparer.Ordinal).Skip(1).Any();
    private const string MergedAccountId = "local";
    public string AccountFilterLabel => string.Join(", ", selectedFilters.Select(f => f.Label));
    [ObservableProperty] private ChatKind kind;
    partial void OnKindChanged(ChatKind value) { OnPropertyChanged(nameof(FilterDescription)); Rebuild(); }
    // A call thread of any sort — missed, answered, outgoing, or one nobody picked up. Voicemail is its own kind,
    // because a voicemail is something to listen to rather than a record of a call that happened.
    public static bool Matches(ChatKind kind, string? callKind) => kind switch
    {
        ChatKind.Messages => callKind is null,
        ChatKind.Calls => callKind is "missed" or "incoming" or "outgoing" or "unanswered",
        ChatKind.Voicemails => callKind == "voicemail",
        _ => true,
    };
    // Google Voice keeps every call as a thread of its own, and a business that called twice was two rows of "Voice call"
    // (2026-10-27). A call answered, made or not picked up leaves nothing to do, so it has no row, unread or badge of its
    // own: Calls lists it with the rest. A missed call and a voicemail keep theirs. The phone's list follows the same rule.
    public static bool IsQuietCall(ChatSummary chat) => chat.IsCallHistory && chat.Preview?.CallKind is "incoming" or "outgoing" or "unanswered";
    private bool Listed(ChatSummary chat) => Kind == ChatKind.Calls || !IsQuietCall(chat);
    public void SetAccounts(IEnumerable<ChatAccount> accounts)
    {
        var prior = selectedFilters;
        var items = accounts.ToArray();
        Filters.Clear(); Filters.Add(new("All services"));
        var networks = items.GroupBy(a => a.Network, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var network in networks)
            Filters.Add(new(network.Key, Network: network.Key));
        // Hermes profiles are one assistant's personas, not separate inboxes: the single Hermes row lists them all (owner,
        // 2026-11-19), and each conversation's title already says which profile it is.
        foreach (var network in networks.Where(group => group.Count() > 1 &&
                     !string.Equals(group.Key, HermesThreads.Network, StringComparison.OrdinalIgnoreCase)))
            foreach (var account in network.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
                Filters.Add(new($"{network.Key} · {account.DisplayName}", account.Id));
        SetServiceFilters(prior.Select(old => Filters.FirstOrDefault(f => f.AccountId == old.AccountId &&
            string.Equals(f.Network, old.Network, StringComparison.OrdinalIgnoreCase))).OfType<ChatFilter>());
        OnPropertyChanged(nameof(HasMultipleSources));
    }

    // Leaving the Unread view ends the hold: the row was kept only so the list would not move while it was being read.
    partial void OnScopeChanged(InboxScope value) { heldUnreadChatId = null; heldDraftChatId = null; Title = value == InboxScope.Inbox ? "Chats" : value.ToString(); OnPropertyChanged(nameof(FilterDescription)); Rebuild(); }
    private bool CanLoad() => !IsBusy;
    private bool CanLoadMore() => !IsBusy && HasMore;
    partial void OnIsBusyChanged(bool value) { RefreshCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged(); }
    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    // The app refreshes the list on its own at launch and whenever a service connects, and a bar moving over a list that
    // is already on screen told the user nothing. Only a refresh they chose says it is loading, or one with no list to
    // show yet (see LoadAsync).
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task RefreshAsync() => LoadAsync(true, announce: false);
    public Task RefreshChosenAsync() => IsBusy ? Task.CompletedTask : LoadAsync(true);
    // A service that hands back a page it already gave has nothing more to give from where paging stands, so Load more
    // conversations then starts again from the newest page. That was the "Refresh to try again" the menu's Refresh
    // conversations answered before it moved to Commands (2026-11-14).
    private bool restartPaging;
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => LoadAsync(restartPaging);

    // announce: whether the list says it is loading. A refresh somebody asked for should; one the app decided to
    // do on its own should not — the bar appearing over a conversation list nobody touched reads as something being
    // wrong, and the reconciliation that needs this is invisible when it finds nothing, which is most of the time.
    private async Task LoadAsync(bool refresh, bool announce = true)
    {
        if (IsBusy) return;
        var version = generation;
        request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = request.Token;
        IsBusy = true;
        IsListLoading = announce;
        Advisory(refresh ? "Loading conversations…" : "Loading more conversations…");
        try
        {
            if (merges is not null) await merges.EnsureLoadedAsync();
            if (labels is not null) await labels.EnsureLoadedAsync();
            await EnsurePinnedOrderAsync(token);
            // Before the cached rows are drawn, so a conversation read here privately is never drawn unread first.
            if (readHere is not null) await readHere.EnsureLoadedAsync();
            if (refresh && loaded.Count == 0 && state is not null)
            {
                try
                {
                    var profile = await state.ProfileAsync();
                    // Before the cached rows are drawn, so the first rows drawn already say "Draft: …" where they should.
                    var draftsMs = profile is null ? null : await LoadDraftsAsync(profile);
                    var saved = profile is null ? null : await state.ReadAsync<ChatSummary[]>(profile, "cache", "chats");
                    if (version != generation) return;
                    if (saved is not null) { loaded.AddRange(saved.Take(2000).Select(ReceiptRules.FromSaved)); Rebuild(); Advisory("Cached conversations · checking for updates…"); }
                    if (draftsMs is { } ms) ReportDrafts(ms);
                }
                catch { }
            }
            // A list that was not read from the cache (no profile then, or rows already loaded) still gets its drafts once.
            if (drafts is not null && drafts.Profile is null && state is not null && await state.ProfileAsync() is { } signedIn)
            {
                var draftsMs = await LoadDraftsAsync(signedIn);
                if (version != generation) return;
                if (drafts.HasAny) Rebuild();
                if (draftsMs is { } ms) ReportDrafts(ms);
            }
            // Nothing on screen to read meanwhile: an empty list is worth explaining whoever started the load.
            if (!announce && loaded.Count == 0) IsListLoading = true;
            var page = await reader.GetChatsAsync(refresh ? null : cursor, token);
            if (version != generation) return;
            // What each row already says, for KeepKnownPreview: a refresh empties the list before adding the page back.
            var known = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
            foreach (var chat in loaded) known.TryAdd(chat.Id, chat);
            if (refresh) { ReplaceLoaded(PartialChatPages.Preserve(loaded, page).ToArray()); cursors.Clear(); restartPaging = false; }
            foreach (var chat in WithoutHeldRemovals(page.Items))
            {
                var index = loaded.FindIndex(c => c.Id == chat.Id);
                if (index >= 0) loaded[index] = KeepKnownPreview(chat, loaded[index]);
                else if (loaded.Count < 2000) loaded.Add(KeepKnownPreview(chat, known.GetValueOrDefault(chat.Id)));
            }
            var repeated = page.HasMore && (page.OldestCursor is null || !cursors.Add(page.OldestCursor));
            cursor = page.OldestCursor;
            restartPaging = repeated;
            HasMore = page.HasMore && loaded.Count < 2000;
            Rebuild();
            await ClaimUnownedDraftsAsync();
            if (page.Warning is { Length: > 0 }) Status = page.Warning;
            _ = FillMissingPreviewsAsync(token);
            if (repeated && HasMore) Status = "Couldn't load more conversations. Choose Load more conversations to try again.";
            else if (loaded.Count >= 2000) Advisory("Showing the 2,000 most recent conversations.");
            await SaveLoadedAsync();
        }
        catch (OperationCanceledException) { }
        catch (BackendException ex) { if (version == generation) Status = ex.Message + (loaded.Count > 0 ? " Previously loaded conversations may be out of date." : ""); }
        catch (Exception) { if (version == generation) Status = "Could not load conversations. Check the connection and try again."; }
        finally { if (version == generation) { IsBusy = false; IsListLoading = false; } }
    }

    // How many conversations of one network the list holds, and how many of those the current view shows. The two
    // differ for archived conversations and whenever a filter is on; reporting both tells a missing conversation from
    // a hidden one.
    public (int Loaded, int Shown, int Archived) NetworkCounts(string network) => (
        loaded.Count(chat => string.Equals(chat.Network, network, StringComparison.OrdinalIgnoreCase)),
        Chats.Count(item => string.Equals(item.Chat.Network, network, StringComparison.OrdinalIgnoreCase)),
        loaded.Count(chat => string.Equals(chat.Network, network, StringComparison.OrdinalIgnoreCase) && chat.IsArchived));

    // Recomputing the list presents every loaded conversation, sorts them and reconciles the bound collection, and a
    // batch of realtime events used to do all of that once per changed conversation. Batch work — a realtime batch, the
    // preview fill — asks through BatchRebuild, which draws at most once per RedrawFlush and never later than that.
    //
    // It used to be a scope shared by every caller that flushed only when the next rebuild happened to arrive, so a
    // preview fill waiting on a slow history read held everybody's redraw: a new conversation, a delete, a pin, a
    // conversation revealed from a notification all sat undrawn until that unrelated read came back. Everything else
    // now draws at once, except inside a synchronous hold (ClearFilters, RevealChat), which draws once on its way out.
    private int holdDepth;
    private bool rebuildWanted, flushScheduled, disposed;
    private long lastRebuild;
    private static readonly TimeSpan RedrawFlush = TimeSpan.FromMilliseconds(300);
    // The interface thread this list was made on, which the trailing redraw comes back to whoever asked for it. Without
    // one — the offline checks have no interface thread — there is no timer, and batch work is drawn as its batch ends.
    private readonly SynchronizationContext? home = SynchronizationContext.Current;
    private void Rebuild()
    {
        if (holdDepth > 0) { rebuildWanted = true; return; }
        // The old shared scope would have left this one undrawn; see NoteWaitingRedraw.
        if (System.Diagnostics.Stopwatch.GetElapsedTime(lastRebuild) < RedrawFlush) NoteWaitingRedraw();
        RebuildNow();
    }
    private void RebuildNow()
    {
        rebuildWanted = false;
        var building = UiActivity.Start();
        try { RebuildCore(); }
        finally { lastRebuild = System.Diagnostics.Stopwatch.GetTimestamp(); UiActivity.Rebuild(building); }
    }
    private void BatchRebuild()
    {
        var since = System.Diagnostics.Stopwatch.GetElapsedTime(lastRebuild);
        if (holdDepth == 0 && since >= RedrawFlush) { RebuildNow(); return; }
        rebuildWanted = true;
        if (flushScheduled || disposed || home is null) return;
        flushScheduled = true;
        _ = FlushAsync(RedrawFlush - since);
    }
    private async Task FlushAsync(TimeSpan wait)
    {
        try { await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // Posted rather than resumed, so the redraw is on the interface thread even if a batch was started off it.
        home!.Post(_ =>
        {
            flushScheduled = false;
            if (!rebuildWanted || disposed) return;
            NoteWaitingRedraw();
            RebuildNow();
        }, null);
    }
    private HeldRebuild HoldRebuild() => new(this);
    private readonly struct HeldRebuild : IDisposable
    {
        private readonly ChatListViewModel owner;
        public HeldRebuild(ChatListViewModel owner) { this.owner = owner; owner.holdDepth++; }
        public void Dispose() { if (--owner.holdDepth == 0 && owner.rebuildWanted) owner.RebuildNow(); }
    }

    // Batch work under way, by kind: how many are running and when the earliest of them began. Several preview fills can
    // run at once, so one start time per kind would be overwritten by a later fill while an earlier one still waited.
    private sealed class RunningBatches(string kind)
    {
        public readonly string Kind = kind;
        public int Count;
        public long Since;
        public bool Reported;
    }
    private readonly RunningBatches eventBatches = new("events"), previewBatches = new("previews");
    private static void Begin(RunningBatches batches)
    {
        if (batches.Count++ > 0) return;
        batches.Since = System.Diagnostics.Stopwatch.GetTimestamp(); batches.Reported = false;
    }
    private static void End(RunningBatches batches) { if (batches.Count > 0) batches.Count--; }
    // A redraw inside RedrawFlush of the last one, while batch work has waited a second or more, is one the old shared
    // scope left undrawn until that work finished. Said once per waiting stretch, with how long it had waited: fixed text
    // and a timing only, which the app writes to its log.
    private void NoteWaitingRedraw()
    {
        foreach (var batches in (ReadOnlySpan<RunningBatches>)[eventBatches, previewBatches])
        {
            if (batches.Count == 0 || batches.Reported) continue;
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(batches.Since);
            if (waited < WaitingRedrawReport) continue;
            batches.Reported = true;
            Allocated?.Invoke(this, $"list redraw flushed during a waiting batch kind={batches.Kind} waited_ms={(long)waited.TotalMilliseconds}");
        }
    }
    // One row's content, without recomputing and reconciling the whole list for it. What a conversation says can be
    // shown the moment it is read; where it belongs in the order and what it does to the counts can wait for the batch.
    private void UpdateRow(ChatSummary chat)
    {
        foreach (var item in Chats)
            if (item.Chat.Id == chat.Id) { UpdateItem(item, Present(chat)); return; }
    }
    private void RebuildCore()
    {
        SyncLabels();
        var existing = Chats.ToDictionary(c => c.Chat.Id);
        var desired = new List<ChatItemViewModel>();
        if (newChatDraft is not null) desired.Add(newChatDraft);
        // Presented once and handed on. The unread count and the pinned list used to each walk the loaded
        // conversations again and present them again, so a rebuild read every conversation three times over.
        // The loaded rows are the services' own, so they say which conversations read here the service has caught up with.
        readHere?.Settle(loaded);
        var presented =SettleMutes((merges?.Project(loaded.Select(Present)) ?? loaded.Select(Present).ToArray()).ToArray());
        // Keep members visible if the parent has not yet been paged in, so no history becomes unreachable.
        var parentIds = presented.Where(c => c.MemberChatIds.Count > 0).Select(c => c.Id).ToHashSet();
        var filtered = HasAccountFilter || Kind != ChatKind.All || HasLabelFilter;
        multipleSources = CountSources();
        foreach (var chat in presented.Where(c => MergedConversations.IsMerged(c.Id) ||
                !MergedConversations.IsMerged(c.MergedIntoChatId) && (filtered ? c.MemberChatIds.Count == 0 : c.MergedIntoChatId is null || !parentIds.Contains(c.MergedIntoChatId)))
            .Where(MatchesFilters).Where(Listed)
            .Where(c => Scope == InboxScope.Inbox && c.Id == promotedChatId || Scope switch { InboxScope.Spam => c.IsSpam, InboxScope.Archived => c.IsArchived && !c.IsSpam, InboxScope.Unread => !c.IsArchived && !c.IsSpam && (c.UnreadCount > 0 || c.Id == heldUnreadChatId),
                InboxScope.Drafts => !c.IsSpam && (HasDraft(c) || c.Id == heldDraftChatId), _ => !c.IsArchived && !c.IsSpam })
            .OrderByDescending(c => c.IsPinned).ThenBy(c => c.IsPinned ? PinnedRank(c.Id) : int.MaxValue)
            .ThenByDescending(c => c.Id == promotedChatId).ThenByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(chat.Id, out var item)) UpdateItem(item, chat);
            else item = new(chat);
            // Before a new row is added, so it is first drawn with its draft line and typing as they are.
            item.SetDraft(DraftFor(chat));
            item.SetTyping(TypingFor(chat));
            item.SetLoading(chatLoadingShown.Contains(chat.Id));
            item.SetLabels(LabelsOf(chat));
            item.ShowNetwork = multipleSources && ShowServiceBadges;
            desired.Add(item);
        }
        // Preserve item identity and selection when adding pages or changing a scope.
        var ids = desired.Select(c => c.Chat.Id).ToHashSet();
        for (var i = Chats.Count - 1; i >= 0; i--) if (!ids.Contains(Chats[i].Chat.Id)) Chats.RemoveAt(i);
        var reordered = CollectionReordering.TrySingleMove(Chats,desired);
        // A list filled from nothing (the cached inbox at launch) goes in as one change; see FillableCollection.
        if (!reordered && Chats.Count == 0 && desired.Count > 1)
        {
            Chats.Fill(desired);
            reordered = true;
            Allocated?.Invoke(this, $"list filled in one batch rows={desired.Count}");
        }
        // Everything below i is already in its final place, so the search for the next row starts there rather than
        // rescanning the whole collection for each of up to two thousand rows.
        for (var i = 0; !reordered && i < desired.Count; i++)
        {
            if (i < Chats.Count && ReferenceEquals(Chats[i], desired[i])) continue;
            var index = -1;
            for (var j = i; j < Chats.Count; j++) if (ReferenceEquals(Chats[j], desired[i])) { index = j; break; }
            if (index >= 0) Chats.Move(index, i);
            else Chats.Insert(i, desired[i]);
        }
        while (Chats.Count > desired.Count) Chats.RemoveAt(Chats.Count - 1);
        UpdatePinned(presented, parentIds);
        UpdateUnread(presented, parentIds);
        UpdateMuteExpiry(presented);
        if (lapsedGroupMutes.Count > 0) MergedMutesLapsed?.Invoke(this, [.. lapsedGroupMutes]);
        var summary = $"{Chats.Count} shown · {loaded.Count} loaded" + (HasMore ? " · More available" : "");
        if (filtered) summary += " · Filter applies to loaded chats";
        if (Chats.Count == 0) summary = HasMore ? "No matches in loaded chats. Load more conversations." : "No conversations in this view.";
        Advisory(summary);
    }
    private bool MatchesFilters(ChatSummary chat)
    {
        // A label is matched on the row itself, a merged one included: it is the row the user labelled, not its members.
        if (!MatchesLabel(chat)) return false;
        bool Match(ChatSummary source) => (!HasAccountFilter || selectedFilters.Any(filter =>
            (filter.AccountId is not { } account || source.AccountId == account) &&
            (filter.Network is not { } network || string.Equals(source.Network, network, StringComparison.OrdinalIgnoreCase)))) && Matches(Kind, source.Preview?.CallKind);
        return MergedConversations.IsMerged(chat.Id) && merges is not null ? merges.Members(chat.Id).Any(Match) : Match(chat);
    }
    private void UpdateUnread(ChatSummary[] presented, HashSet<string> parentIds)
    {
        lastPresented = presented; lastParentIds = parentIds;
        var unread = presented.Where(c => c.UnreadCount > 0 && !c.IsMuted && !c.IsArchived && !c.IsSpam && !IsQuietCall(c) && (c.MergedIntoChatId is null || !parentIds.Contains(c.MergedIntoChatId))).ToArray();
        var shown = unread.OrderByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal).Take(UnreadChatsShown).ToArray();
        // A conversation replacing another in the list with the count unchanged is still a change to what the tray shows.
        var same = unread.Length == UnreadChatCount && shown.Length == UnreadChats.Count && shown.Select(c => c.Id).SequenceEqual(UnreadChats.Select(c => c.Id), StringComparer.Ordinal);
        UnreadChats = shown;
        if (same) return;
        UnreadChatCount = unread.Length;
        UnreadChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// How many conversations from one service (or one account) are waiting to be read, by the test
    /// <see cref="UnreadChatCount"/> uses, for the Services menu. A merged conversation's members count under their own
    /// service, as the filtered list shows them, and are muted or archived when their merged row is. A filter naming no
    /// service or account is the whole inbox, and gives <see cref="UnreadChatCount"/> itself.
    /// </summary>
    public int UnreadCountFor(ChatFilter filter)
    {
        if (filter.AccountId is null && filter.Network is null) return UnreadChatCount;
        var parents = lastPresented.Where(c => lastParentIds.Contains(c.Id)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var count = 0;
        foreach (var chat in lastPresented)
        {
            if (chat.MemberChatIds.Count > 0 || IsQuietCall(chat)) continue;
            if (filter.AccountId is { } account && chat.AccountId != account) continue;
            if (filter.Network is { } network && !string.Equals(chat.Network, network, StringComparison.OrdinalIgnoreCase)) continue;
            var parent = chat.MergedIntoChatId is { } parentId ? parents.GetValueOrDefault(parentId) : null;
            // A member's mute is its merged row's, where mute is offered: the phone's mute on one member (Messenger) left the
            // filtered count without it while the merged row still counted (2026-10-04).
            if (chat.UnreadCount > 0 && !(parent?.IsMuted ?? chat.IsMuted) && !chat.IsArchived && !chat.IsSpam && parent is not { IsArchived: true }) count++;
        }
        return count;
    }
    private void UpdatePinned(IReadOnlyList<ChatSummary> presented, HashSet<string> parentIds)
    {
        InboxChats = presented.Where(c => !c.IsArchived && !c.IsSpam && !IsQuietCall(c) && (c.MergedIntoChatId is null || !parentIds.Contains(c.MergedIntoChatId))).ToArray();
        PinnedChats = InboxChats.Where(c => c.IsPinned)
            .OrderBy(c => PinnedRank(c.Id)).ThenByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var signature = string.Join("\n", PinnedChats.Select(c => c.Id + "\t" + c.Title));
        if (signature == pinnedSignature) return;
        pinnedSignature = signature;
        PinnedChatsChanged?.Invoke(this, EventArgs.Empty);
    }
    private int PinnedRank(string id)
    {
        var index = pinnedOrder.IndexOf(id);
        return index < 0 ? int.MaxValue : index;
    }
    private async Task EnsurePinnedOrderAsync(CancellationToken token, bool reload = false)
    {
        if (state is null) return;
        var profile = await state.ProfileAsync();
        if (profile is null || profile == pinnedOrderProfile && !reload) return;
        try
        {
            var saved = await state.ReadAsync<string[]>(profile, "preferences", "pinned-order") ?? [];
            token.ThrowIfCancellationRequested();
            pinnedOrder.Clear();
            pinnedOrder.AddRange(saved.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal));
            pinnedOrderProfile = profile;
        }
        catch (OperationCanceledException) { throw; }
        catch { pinnedOrder.Clear(); pinnedOrderProfile = profile; }
    }
    /// <summary>
    /// After a restore added saved rows under the profile already loaded: merged conversations, labels and the pinned
    /// order are read again, because each is held in memory once loaded and its next change would write that copy over
    /// what was restored. A restore adds merges and labels independently, so labels on conversations that are now
    /// merged move to the merged row, as merging by hand moves them. Returns how many merged rows took labels over.
    /// </summary>
    public async Task<int> ReloadRestoredAsync()
    {
        if (merges is not null) await merges.ReloadAsync();
        if (labels is not null) await labels.ReloadAsync();
        var relabelled = 0;
        if (labels is { IsAvailable: true } && merges is not null)
            foreach (var id in merges.GroupIds)
                if (labels.Merge(id, merges.Members(id).Select(member => member.Id))) relabelled++;
        await EnsurePinnedOrderAsync(CancellationToken.None, reload: true);
        Rebuild();
        return relabelled;
    }
    private async Task SavePinnedOrderAsync()
    {
        if (state is null || pinnedOrderProfile is null) return;
        try { await state.WriteAsync(pinnedOrderProfile, "preferences", "pinned-order", pinnedOrder.ToArray()); }
        catch { }
    }
    /// <summary>The saved pin order, by conversation id, including ids not loaded now.</summary>
    public IReadOnlyList<string> PinnedOrderIds => [.. pinnedOrder];

    /// <summary>
    /// Takes the conversations the test matches out of the saved pin order, which is the one copy of it: changed here,
    /// saved and shown. Returns how many ids went.
    /// </summary>
    public async Task<int> ForgetPinnedAsync(Func<string, bool> matches)
    {
        await EnsurePinnedOrderAsync(CancellationToken.None);
        var removed = pinnedOrder.RemoveAll(id => matches(id));
        if (removed == 0) return 0;
        Rebuild();
        await SavePinnedOrderAsync();
        return removed;
    }

    /// <summary>
    /// Drops the loaded rows the test matches, with everything remembered about them, and saves the list. A load or event
    /// batch under way is abandoned like <see cref="Clear"/> does, so it cannot put a dropped row back from an older read.
    /// Returns how many rows went.
    /// </summary>
    public int ForgetRows(Func<string, bool> matches)
    {
        generation++;
        request?.Cancel(); IsBusy = false; IsListLoading = false;
        var removed = loaded.RemoveAll(chat => matches(chat.Id));
        foreach (var id in previewAttempts.Keys.Where(matches).ToArray()) previewAttempts.Remove(id);
        foreach (var id in previewFallbacks.Keys.Where(matches).ToArray()) previewFallbacks.Remove(id);
        foreach (var id in localReads.Keys.Where(matches).ToArray()) localReads.Remove(id);
        readHere?.Forget(matches);
        foreach (var id in heldRemovals.Keys.Where(matches).ToArray()) heldRemovals.Remove(id);
        foreach (var id in chatLoadingCounts.Keys.Where(matches).ToArray()) chatLoadingCounts.Remove(id);
        chatLoadingShown.RemoveWhere(id => matches(id));
        if (promotedChatId is { } promoted && matches(promoted)) promotedChatId = null;
        if (heldUnreadChatId is { } held && matches(held)) heldUnreadChatId = null;
        if (heldDraftChatId is { } heldDraft && matches(heldDraft)) heldDraftChatId = null;
        foreach (var id in typingTexts.Keys.Where(matches).ToArray()) typingTexts.Remove(id);
        cursors.Clear(); cursor = null; HasMore = false; restartPaging = false;
        Rebuild();
        SaveLoadedSoon();
        return removed;
    }

    public bool MovePinnedChat(string chatId, string targetId, bool after)
    {
        if (chatId == targetId) return false;
        var ordered = (merges?.Project(loaded) ?? loaded).Where(c => c.IsPinned && !c.IsArchived && !c.IsSpam && c.MergedIntoChatId is null)
            .OrderBy(c => PinnedRank(c.Id)).ThenByDescending(c => c.LastActivity).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
        if (!ordered.Remove(chatId)) return false;
        var target = ordered.IndexOf(targetId);
        if (target < 0) return false;
        ordered.Insert(target + (after ? 1 : 0), chatId);
        foreach (var unseen in pinnedOrder.Where(id => !ordered.Contains(id, StringComparer.Ordinal)).ToArray()) ordered.Add(unseen);
        pinnedOrder.Clear(); pinnedOrder.AddRange(ordered);
        Rebuild();
        _ = SavePinnedOrderAsync();
        return true;
    }
    public async Task ApplyEventsAsync(IReadOnlyList<RealtimeEvent> events, bool full, CancellationToken token)
    {
        var version = generation;
        IsBusy = true;
        try { await ApplyEventsCoreAsync(events, full, token); }
        finally { if (version == generation) IsBusy = false; }
        // Events name the provider's conversation, and a merged one is listed under its own row, so a member's id counts.
        if (version == generation && !full && events.Any(e => e.Kind == RealtimeKind.MessageChanged && e.IsNewMessage == true &&
                e.ChatId is { } id && Chats.Any(c => c.Chat.Id == id || c.Chat.MemberChatIds.Contains(id))))
            NewMessagesReceived?.Invoke(this, EventArgs.Empty);
    }
    private async Task ApplyEventsCoreAsync(IReadOnlyList<RealtimeEvent> events, bool full, CancellationToken token)
    {
        var version = generation;
        // The whole of a synchronization's allocation turned out to be this method — nine to thirteen megabytes of
        // it, and thirteen for a resync carrying no events at all. "The list" is four different things though, so
        // each says its own share: reading the page, replacing what is loaded, applying the events one at a time,
        // and writing the list back to the protected store.
        var mark = GC.GetAllocatedBytesForCurrentThread();
        long Since() { var now = GC.GetAllocatedBytesForCurrentThread(); var grew = now - mark; mark = now; return grew / 1024; }
        var page = full ? await reader.GetChatsAsync(cancellationToken: token) : null;
        var fetchKb = Since();
        if (version != generation) return;
        if (page is not null)
        {
            var retained = PartialChatPages.Preserve(loaded, page).ToArray();
            ReplaceLoaded(page.Items.Concat(retained).DistinctBy(chat => chat.Id).ToArray()); cursors.Clear(); restartPaging = false; cursor = page.OldestCursor;
            if (cursor is not null) cursors.Add(cursor);
            HasMore = page.HasMore && cursor is not null;
        }
        // One rebuild for the batch instead of one per conversation in it. A burst of twenty changed conversations used
        // to recompute and reconcile the whole list twenty-one times over, on the interface thread.
        Begin(eventBatches);
        try
        {
            if (page is null)
            {
                var changes = new Dictionary<string, bool>();
                foreach (var change in events)
                {
                    // Existing-message reactions and receipts do not change chat ordering or previews, and refreshing the
                    // authoritative summary for every one of them was a burst of reads for nothing.
                    //
                    // Except for a conversation this list is still showing as unread. Reading it on the phone reaches
                    // this app as a receipt against the messages in it, and skipping those is why a conversation read on
                    // the phone did not reliably go read here — it stayed unread until something else happened to
                    // refresh it. The cost stays bounded because the exception applies only to rows that are unread
                    // right now, which is the only set where the answer can change. Messenger and Instagram mark their
                    // receipt-only changes the same way, so the same shortcut serves all three.
                    //
                    // Instagram's receipts are only other people's reading; this account's own comes with the poll's
                    // changed conversations. So one never changes a row, and re-reading an unread row for it was a request
                    // to Instagram for nothing.
                    if (change.ReceiptOnly && change.ChatId?.StartsWith("instagram:", StringComparison.Ordinal) == true) continue;
                    if (change.Kind == RealtimeKind.MessageChanged && change.IsNewMessage == false &&
                        (change.ChatId?.StartsWith("google-direct:", StringComparison.Ordinal) == true ||
                         change.ChatId?.StartsWith("messenger:", StringComparison.Ordinal) == true ||
                         change.ChatId?.StartsWith("instagram:", StringComparison.Ordinal) == true) &&
                        !ShowsProviderUnread(change.ChatId)) continue;
                    if (change.Kind is RealtimeKind.ChatDeleted or RealtimeKind.ChatChanged)
                        foreach (var id in change.Ids ?? []) changes[id] = change.Kind == RealtimeKind.ChatDeleted;
                    if (change.ChatId is not null) changes[change.ChatId] = change.Kind == RealtimeKind.ChatDeleted;
                }
                foreach (var (id, deleted) in changes)
                {
                    if (deleted) { ReleaseRemoval(id); previewAttempts.Remove(id); previewFallbacks.Remove(id); loaded.RemoveAll(c => c.Id == id); BatchRebuild(); continue; }
                    // What this conversation's preview should say is remembered against the activity it was read at, so
                    // nothing is forgotten here: a genuinely new message moves that activity and the fill refetches by
                    // itself. Forgetting it on every change meant a conversation whose provider sends no snippet — the
                    // last thing in it was a photo, or an audio message — fetched a whole page of history again for
                    // every read receipt against it, and swirled for the second that took. One such conversation can
                    // do that all day.
                    // One conversation that cannot be read right now leaves its existing row alone. Failing the whole
                    // batch would put the app into a resync loop over a single unavailable chat.
                    BeginChatLoading(id);
                    try
                    {
                        ChatSummary chat;
                        try { chat = await reader.GetChatAsync(id, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception) { continue; }
                        if (version != generation) return;
                        if (IsHeldRemoved(id))
                        {
                            // Reported through the list's diagnostic line, which the app writes to its log.
                            Allocated?.Invoke(this, "list kept a deleted conversation out against a stale provider read");
                            continue;
                        }
                        var index = loaded.FindIndex(c => c.Id == id);
                        if (index >= 0) loaded[index] = chat = KeepKnownPreview(chat, loaded[index]);
                        else if (loaded.Count < 2000) loaded.Add(chat);
                        // The row shows what was just read before it stops saying it is reading. Where it belongs in the
                        // order, and what it does to the unread count, settle when the batch rebuilds.
                        UpdateRow(chat);
                        BatchRebuild();
                    }
                    finally { EndChatLoading(id); }
                }
            }
        }
        finally { End(eventBatches); }
        // Always drawn now, so a batch ends with the list current whatever else is still reading.
        Rebuild();
        var applyKb = Since();
        if (page?.Warning is { Length: > 0 }) Status = page.Warning;
        _ = FillMissingPreviewsAsync(token);
        SaveLoadedSoon();
        var saveKb = Since();
        if (fetchKb + applyKb + saveKb >= SlowApplyKb)
            Allocated?.Invoke(this, $"list alloc fetch_kb={fetchKb} apply_kb={applyKb} save_kb={saveKb} loaded={loaded.Count} events={events.Count} resync={full}");
    }

    // Low enough to catch an ordinary batch as well as a resync: the ordinary ones turned out to cost two and a
    // half megabytes apiece, which the synchronization's own threshold was stepping over.
    private const long SlowApplyKb = 1024;
    // Reported rather than logged: this view model is shared presentation and knows nothing about where a log lives.
    public event EventHandler<string>? Allocated;
    // Asks the provider again when this list is showing anything as unread.
    //
    // Google Messages does not push the reading of a conversation: a conversation read on the phone produces nothing
    // this app can see, and the badge stands until something re-reads the list. Since there is no event to wait for,
    // the app asks — when the window comes forward, which is when reading something on the phone has most likely
    // just happened.
    //
    // It asks the whole list, not the unread rows one at a time, and that is not for want of trying the cheaper
    // thing: a single conversation read is answered from the backend's own map of conversations, which only a list
    // read refreshes. Asking per row therefore returned the same stale answer for nothing, and silently — no helper
    // call, no log line, no change. The list read is the question that actually reaches the phone.
    //
    // The cost is paid only when there is an answer worth having: nothing unread, nothing asked. A conversation
    // marked unread on this PC does not count, because that mark is this person's own and no answer from the
    // provider is them changing their mind about it.
    public async Task ReconcileUnreadAsync()
    {
        if (IsBusy || !loaded.Any(chat => chat.UnreadCount > 0 && !chat.IsMarkedUnread)) return;
        await LoadAsync(true, announce: false);
    }

    // Whether the list is currently showing this conversation as unread because the provider says so. The local
    // "mark as unread" is deliberately not counted: it is this person's own mark on this PC, and a receipt from the
    // phone is not them changing their mind about it.
    private bool ShowsProviderUnread(string chatId) =>
        loaded.Any(chat => chat.Id == chatId && chat.UnreadCount > 0);

    // The list written back to the protected store, at most once a second however often it changes.
    //
    // This is a cache of what was last seen, read at the next launch so the inbox has something to draw before any
    // provider connects. It is not the source of anything, and nothing waits on it — but it copies and seals all
    // three hundred-odd conversations each time, a measured megabyte, and it ran after every realtime event. Ten
    // messages arriving in a burst wrote the same list ten times.
    //
    // Writing the newest state a moment later is exactly as correct as writing every intermediate one, because only
    // the last of them was ever going to be read. A save already in flight is left to finish and the next state is
    // written after it, so the copy on disk always ends up being the last one there was.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);
    private DateTimeOffset lastSave = DateTimeOffset.MinValue;
    // Set before the write is started, not after. A write with nothing to wait for finishes synchronously, and
    // assigning the task afterwards then overwrote the "finished" the write itself had already recorded — leaving it
    // permanently in flight and every later save skipped for good.
    private bool saving;
    // Something changed while a save was already on its way. Noting it rather than dropping it is what makes the
    // last state the one that lands: a save captures the list at the moment it runs, so one already in flight knows
    // nothing about a change made after it started, and without this the final change would simply never be written.
    private bool saveWanted;
    private void SaveLoadedSoon()
    {
        if (state is null) return;
        if (saving) { saveWanted = true; return; }
        saving = true;
        var wait = lastSave + SaveInterval - DateTimeOffset.UtcNow;
        _ = WriteAfterAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
    }
    private async Task WriteAfterAsync(TimeSpan wait)
    {
        try
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, lifetime.Token);
            await SaveLoadedAsync();
        }
        catch (OperationCanceledException) { }
        finally { lastSave = DateTimeOffset.UtcNow; saving = false; }
        // Whatever arrived while that was running gets its own turn, which ends as soon as changes do.
        if (!saveWanted || lifetime.IsCancellationRequested) return;
        saveWanted = false;
        SaveLoadedSoon();
    }

    private async Task SaveLoadedAsync()
    {
        if (state is null) return;
        var snapshot = loaded.ToArray();
        try { var profile = await state.ProfileAsync(); if (profile is not null) await state.WriteAsync(profile, "cache", "chats", snapshot); }
        catch { }
    }
    public void Clear()
    {
        generation++;
        newChatDraft = null; promotedChatId = null; heldUnreadChatId = null; heldDraftChatId = null;
        typingTexts.Clear();
        previewAttempts.Clear();
        previewFallbacks.Clear();
        localReads.Clear();
        request?.Cancel(); IsBusy = false; IsListLoading = false;
        chatLoadingCounts.Clear();
        chatLoadingShown.Clear();
        pinnedOrder.Clear(); pinnedOrderProfile = null;
        loaded.Clear(); cursors.Clear(); Chats.Clear(); cursor = null; HasMore = false;
        UpdatePinned([], []);
        UpdateUnread([], []);
        Advisory("Connect to load conversations.");
    }
    // A snooze is a local moment rather than a setting, so it has its own applier. Null ends it.
    public void ApplySnooze(string chatId, DateTimeOffset? until)
    {
        var changed = false;
        for (var i = 0; i < loaded.Count; i++)
        {
            var chat = loaded[i];
            if (chat.Id != chatId && !chat.MemberChatIds.Contains(chatId)) continue;
            loaded[i] = chat with { SnoozedUntil = until }; changed = true;
        }
        if (changed) Rebuild();
    }
    // A mute set from this PC, with the moment a timed one ends (null: until turned off). Unmuting clears the moment.
    public void ApplyMute(string chatId, bool muted, DateTimeOffset? until)
    {
        var changed = false;
        for (var i = 0; i < loaded.Count; i++)
        {
            if (loaded[i].Id != chatId) continue;
            loaded[i] = loaded[i] with { IsMuted = muted, MutedUntil = muted ? until : null }; changed = true;
        }
        if (changed) Rebuild();
    }

    // The latest moment ExpireMutes was told had come. A rebuild reads the clock itself too; this is for a caller whose
    // moment runs ahead of it (the timer fires on time, the offline checks pass a moment of their own).
    private DateTimeOffset mutesSettledThrough = DateTimeOffset.MinValue;
    private DateTimeOffset MuteNow() { var now = DateTimeOffset.UtcNow; return now > mutesSettledThrough ? now : mutesSettledThrough; }
    // A timed mute whose moment has come is no mute, whether it was read before its moment (the saved list, a row read
    // an hour ago) or belongs to a merged conversation, whose moment is carried onto its row here.
    private ChatSummary[] SettleMutes(ChatSummary[] presented)
    {
        var now = MuteNow();
        lapsedGroupMutes.Clear();
        for (var i = 0; i < presented.Length; i++)
        {
            var chat = presented[i];
            var merged = MergedConversations.IsMerged(chat.Id) && merges is not null;
            var until = merged ? merges!.MutedUntil(chat.Id) : chat.MutedUntil;
            if (until is { } end && end <= now)
            {
                presented[i] = chat with { IsMuted = false, MutedUntil = null };
                if (merged) lapsedGroupMutes.Add(chat.Id);
            }
            else if (!chat.IsMuted) { if (chat.MutedUntil is not null) presented[i] = chat with { MutedUntil = null }; }
            else if (chat.MutedUntil != until) presented[i] = chat with { MutedUntil = until };
        }
        return presented;
    }

    // Merged conversations the last rebuild showed unmuted because their timed mute had ended while their saved group
    // still says muted.
    private readonly List<string> lapsedGroupMutes = [];
    /// <summary>
    /// Raised after a rebuild that showed merged conversations unmuted because their timed mute had ended while their saved
    /// group still says muted: at the moment itself, or at the first rebuild after a restart when it ended while the app was
    /// closed. The row reads the end, but the chat console, its typing line and repeat reminders read the group's own Muted,
    /// so the handler settles the group (<see cref="MergedConversations.SetAsync"/> clears an ended mute). Raised again at
    /// each rebuild until the group is settled.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? MergedMutesLapsed;
    /// <summary>The nearest moment a timed mute on a loaded conversation ends; null when none is waiting to.</summary>
    public DateTimeOffset? NextMuteExpiry { get; private set; }
    private void UpdateMuteExpiry(ChatSummary[] presented)
    {
        DateTimeOffset? next = null;
        foreach (var chat in presented)
            if (chat is { IsMuted: true, MutedUntil: { } until } && (next is null || until < next)) next = until;
        if (next == NextMuteExpiry) return;
        NextMuteExpiry = next;
        OnPropertyChanged(nameof(NextMuteExpiry));
    }

    /// <summary>
    /// Shows every timed mute whose moment has come as unmuted, which also counts its unread conversation again, and
    /// returns how many there were. The ids of single conversations among them are added to <paramref name="expired"/>,
    /// for their saved mute to be cleared; merged ones are reported by <see cref="MergedMutesLapsed"/> from the rebuild.
    /// </summary>
    public int ExpireMutes(DateTimeOffset now, ICollection<string>? expired = null)
    {
        if (now > mutesSettledThrough) mutesSettledThrough = now;
        var count = 0;
        for (var i = 0; i < loaded.Count; i++)
        {
            if (loaded[i] is not { IsMuted: true, MutedUntil: { } until } chat || until > now) continue;
            loaded[i] = chat with { IsMuted = false, MutedUntil = null };
            if (!MergedConversations.IsMerged(chat.Id)) expired?.Add(chat.Id);
            count++;
        }
        // A merged conversation's moment lives in its group; its row was presented muted at the last rebuild.
        foreach (var chat in lastPresented)
            if (MergedConversations.IsMerged(chat.Id) && chat is { IsMuted: true, MutedUntil: { } end } && end <= now) count++;
        if (count > 0 || NextMuteExpiry <= now) Rebuild();
        return count;
    }

    public void ApplyLocalSetting(string chatId, ChatSetting setting, bool value)
    {
        for (var i = 0; i < loaded.Count; i++)
        {
            var chat = loaded[i];
            if (chat.Id != chatId) continue;
            // Muted from here without a moment is muted until turned off, and unmuted is no mute at all: either way no end.
            loaded[i] = setting switch { ChatSetting.Pinned => chat with { IsPinned = value }, ChatSetting.Muted => chat with { IsMuted = value, MutedUntil = null }, ChatSetting.Archived => chat with { IsArchived = value },
                ChatSetting.MarkedUnread => chat with { IsMarkedUnread = value }, ChatSetting.Spam => chat with { IsSpam = value }, _ => chat };
        }
        if (setting == ChatSetting.Pinned)
        {
            pinnedOrder.Remove(chatId);
            if (value) pinnedOrder.Insert(0, chatId);
            _ = SavePinnedOrderAsync();
        }
        Rebuild();
    }
    public void RemoveChat(string chatId)
    {
        HoldRemoval(chatId);
        if (loaded.RemoveAll(c => c.Id == chatId) == 0) return;
        Rebuild();
        _ = SaveLoadedAsync();
    }
    // A deferred write that has not happened yet is not worth keeping the app open for: the cache is a
    // convenience for the next launch and the state it would have written is a second old at most.
    // Marked disposed first, so a late BatchRebuild never asks the disposed lifetime for its token.
    public void Dispose()
    {
        disposed = true; generation++; lifetime.Cancel(); request?.Dispose(); lifetime.Dispose();
        if (draftsHooked && drafts is not null) drafts.Changed -= DraftChanged;
        if (followed is not null) { followed.ActiveChatChanged -= FollowedChanged; followed = null; drafts?.Holding(openHold, null); }
    }

    // ---- The row's second line: "Draft: …" and "typing…" ----
    //
    // Both are set on the row by the rebuild, before a new row is first added, and changed one row at a time between
    // rebuilds. Neither is ever put on a ChatSummary, so the saved list cache holds no draft text.

    private bool draftsHooked;
    // The conversation open in the Drafts view, kept listed while it is open (as heldUnreadChatId is in Unread), so
    // sending the draft does not pull the row out from under the reader.
    private string? heldDraftChatId;

    // Reads the saved drafts; returns how long that took, or null when nothing was read (already read, or it failed).
    private async Task<long?> LoadDraftsAsync(string profile)
    {
        if (drafts is null || state is null || drafts.Profile == profile) return null;
        HookDrafts();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await drafts.LoadAsync(state, profile);
            return (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception failure)
        {
            // Rows show their previews as before; the next load reads the drafts again.
            Allocated?.Invoke(this, $"drafts load failed error={failure.GetType().Name} ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
            return null;
        }
    }

    // Drafts saved before Messenger's were filed by account go to the account whose conversations these are (DraftOwners). A
    // failure leaves them where they are, hidden, and is tried again with the next load.
    private async Task ClaimUnownedDraftsAsync()
    {
        if (drafts is null || state is null) return;
        try
        {
            if (await state.ProfileAsync() is not { } profile) return;
            if (await drafts.ClaimUnownedAsync(state, profile, loaded.Select(chat => chat.Id).ToArray())) Rebuild();
        }
        catch (Exception failure) { Allocated?.Invoke(this, $"drafts migrate failed error={failure.GetType().Name}"); }
    }

    // Said once the rows are on screen, counting this profile's conversations with a draft among those loaded. The stored
    // rows cannot say which profile they belong to (their keys are keyed hashes), so counting them would include every
    // profile that ever saved a draft on this PC.
    private void ReportDrafts(long ms)
    {
        var rows = 0;
        if (drafts is not null && drafts.HasAny) foreach (var chat in loaded) if (drafts.For(chat.Id) is not null) rows++;
        Allocated?.Invoke(this, $"drafts loaded rows={rows} ms={ms}");
    }

    private void HookDrafts()
    {
        if (draftsHooked || drafts is null) return;
        draftsHooked = true;
        drafts.Changed += DraftChanged;
    }

    // What a row's draft line shows: nothing for a conversation open in any composer, whose text belongs to whoever is
    // writing it; a merged row shows a member's draft, and nothing while any member (or the row itself) is open.
    private string? DraftFor(ChatSummary chat)
    {
        if (drafts is null) return null;
        if (drafts.IsOpen(chat.Id) || chat.MergedIntoChatId is { } parent && drafts.IsOpen(parent)) return null;
        if (chat.MemberChatIds.Count == 0) return drafts.For(chat.Id);
        string? found = null;
        foreach (var member in chat.MemberChatIds)
        {
            if (drafts.IsOpen(member)) return null;
            found ??= drafts.For(member);
        }
        return found ?? drafts.For(chat.Id);
    }

    // Whether the Drafts view lists a conversation: it has a saved draft, open or not.
    private bool HasDraft(ChatSummary chat) =>
        drafts is not null && (drafts.For(chat.Id) is not null || chat.MemberChatIds.Any(member => drafts.For(member) is not null));

    // A draft saved, sent, cleared, or a conversation opened or left in a composer. Only the rows it names change, and each
    // only if its line does; the Drafts view is recomputed, since whether a row is listed there can change too.
    private void DraftChanged(object? sender, string chatId)
    {
        if (home is not null && SynchronizationContext.Current != home) { home.Post(_ => DraftChanged(sender, chatId), null); return; }
        if (disposed) return;
        if (Scope == InboxScope.Drafts) { Rebuild(); return; }
        foreach (var item in Chats)
            if (chatId.Length == 0 || item.Chat.Id == chatId || item.Chat.MergedIntoChatId == chatId || item.Chat.MemberChatIds.Contains(chatId))
                item.SetDraft(DraftFor(item.Chat));
    }

    // The main window's open conversation, as a holder in the draft index (see FollowOpenConversation).
    private readonly object openHold = new();
    private ConversationViewModel? followed;

    /// <summary>
    /// Follows the main window's open conversation. It counts as open for the draft line from the moment it is chosen,
    /// not only once a composer holds it: a merged conversation's composer holds nothing until a service is chosen to
    /// send through, which can take the whole history load or never happen. It is also kept listed in the Drafts view
    /// while it stays open (<see cref="HoldDraftChat"/>).
    /// </summary>
    public void FollowOpenConversation(ConversationViewModel history)
    {
        if (followed is not null) followed.ActiveChatChanged -= FollowedChanged;
        followed = history;
        history.ActiveChatChanged += FollowedChanged;
        FollowedChanged(history, history.ActiveChatId);
    }

    private void FollowedChanged(object? sender, string? id)
    {
        // A merged parent passes through no active id while its member is chosen; what it opened is already named.
        HoldOpenConversation(id ?? followed?.CurrentChat?.Id);
        HoldDraftChat(id);
    }

    /// <summary>
    /// The conversation the main window has open, or is about to reopen (a conversation window handing its conversation
    /// back, before the main window has read it), counts as open: its row shows no draft line.
    /// </summary>
    public void HoldOpenConversation(string? chatId) => drafts?.Holding(openHold, chatId);

    /// <summary>
    /// Keeps the conversation just opened listed in the Drafts view while it stays open, as <see cref="HoldUnreadChat"/>
    /// does for Unread. Only a row the view is showing is held.
    /// </summary>
    public void HoldDraftChat(string? activeChatId)
    {
        var hold = Scope == InboxScope.Drafts && activeChatId is not null &&
            (activeChatId == heldDraftChatId || Chats.Any(c => c.Chat.Id == activeChatId)) ? activeChatId : null;
        if (hold == heldDraftChatId) return;
        heldDraftChatId = hold;
        Rebuild();
    }

    // Row id -> what its second line says while someone types there. Kept here rather than only on the row, so a row
    // rebuilt or added while someone types is drawn with it.
    private readonly Dictionary<string, string> typingTexts = new(StringComparer.Ordinal);

    /// <summary>How many rows show typing now.</summary>
    public int TypingRows => typingTexts.Count;

    /// <summary>
    /// Raised with a row's id when its time or preview changed while it shows typing: the message typed has arrived, so
    /// the caller can end the typing line in the same pass (see ListTyping.Activity).
    /// </summary>
    public event EventHandler<string>? RowActivity;

    /// <summary>
    /// Shows or clears the typing line on the list row with this id, only where it changes, and on no other row. A merged
    /// conversation's members are never rows of their own (a service filter lists the merged row when a member matches),
    /// so their typing, mapped to the merged row by ListTyping, shows there only.
    /// </summary>
    public void SetTyping(string rowId, string? text)
    {
        if (string.IsNullOrEmpty(text)) { if (!typingTexts.Remove(rowId)) return; text = null; }
        else if (typingTexts.GetValueOrDefault(rowId) == text) return;
        else typingTexts[rowId] = text;
        foreach (var item in Chats)
            if (item.Chat.Id == rowId) item.SetTyping(text);
    }

    private string? TypingFor(ChatSummary chat) => typingTexts.Count == 0 ? null : typingTexts.GetValueOrDefault(chat.Id);

    // A row's summary replaced. Where it shows typing and its time or preview moved, the typed message is here.
    private void UpdateItem(ChatItemViewModel item, ChatSummary chat)
    {
        if (TypingFor(chat) is null) { item.Update(chat); return; }
        var (time, preview) = (item.Chat.LastActivity, item.Preview);
        item.Update(chat);
        if (item.Chat.LastActivity != time || item.Preview != preview) RowActivity?.Invoke(this, chat.Id);
    }
}
