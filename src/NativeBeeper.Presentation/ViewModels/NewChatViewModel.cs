using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

/// <summary>A line in New chat: either a section heading or somewhere a message could go.</summary>
public sealed record NewChatRow(string? Header, ChatDestination? Destination)
{
    public bool IsHeader => Header is not null;
}

public sealed partial class NewChatViewModel(IContactBackend backend, IAccessTokenStore tokens, IGroupBackend? groups = null,
    Func<IReadOnlyList<ChatSummary>>? conversations = null, TimeProvider? clock = null) : ObservableObject, IDisposable
{
    /// <summary>
    /// How long the default sections wait for a service's own ranking of frequent people before being drawn without it.
    /// An answer that comes later is never pushed in above rows already on screen; it is there the next time the default
    /// sections are drawn from scratch (the search box emptied, or the next opening, which the backend answers from
    /// what it kept).
    /// </summary>
    public static readonly TimeSpan FrequentWait = TimeSpan.FromSeconds(2);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    // Each service's ranking is asked for at most once per New chat: switching between services here reuses the answer.
    private readonly Dictionary<string, Task<IReadOnlyList<ContactPerson>?>> frequentAsked = [];
    private CancellationTokenSource? request;
    private int generation;
    private string? profile;
    // The service picked outright from the pills. Null means every connected service at once.
    private string? accountId;
    // The service the first recipient settled, while All services is still what is being searched. Searching stays
    // wide afterwards on purpose: somebody on another service should still be findable and clickable, so the refusal
    // can say what is wrong rather than the row silently doing nothing.
    private string? pinnedAccount;
    private bool allServices;
    private ChatAccount[] available = [];
    private readonly Dictionary<string, ContactOptions> options = [];
    // What each service does about groups, read once when the services are chosen rather than when a recipient is
    // picked, so whether a second person could ever be added is known the moment the first one is.
    private readonly Dictionary<string, GroupOptions> groupings = [];
    private readonly RemoteContactCache remoteCache = new();
    private string lastQuery = "";

    public GroupOptions GroupOptions { get; private set; } = new(false, "Choose a service.");
    public ObservableCollection<ContactPerson> Participants { get; } = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canSearch;
    [ObservableProperty] private string status = "Choose a service.";
    /// <summary>Everybody a provider has named for the current query. The set memberships below are checked against it.</summary>
    public ObservableCollection<ContactPerson> Contacts { get; } = [];
    /// <summary>What the pane draws: the default sections before anything is typed, the results afterwards.</summary>
    public ObservableCollection<NewChatRow> Rows { get; } = [];
    public bool IsStarting { get; private set; }
    public bool OutcomeUnknown { get; private set; }
    /// <summary>
    /// A first message was sent or a group was made, whatever came after it. Like an unknown outcome it ends every way of
    /// submitting from this pane: the person is told it was done and the text and recipient are not offered again.
    /// </summary>
    public bool Sent { get; private set; }
    private bool Closed => OutcomeUnknown || Sent;
    private void Close(SendException ex) { OutcomeUnknown |= ex.OutcomeUnknown; Sent |= ex.Done; }
    /// <summary>True while a remote provider has been asked and has not answered.</summary>
    [ObservableProperty] private bool isSearchingRemote;

    private IReadOnlyList<string> Scope => accountId is { } one ? [one]
        : allServices ? [.. available.Select(account => account.Id)] : [];

    /// <summary>The account the user chose for new texts (Settings, or the picker in New chat). Null means the default.</summary>
    public string? TextingPreference { get; set; }
    /// <summary>
    /// Where a phone number found anywhere goes: the texting service picked outright, or, while every service is
    /// searched, the one chosen for new texts. Null for a service that does not text numbers.
    /// </summary>
    public string? TextingAccount => accountId is { } one ? (TextingServices.IsTexting(NetworkOf(one)) ? one : null)
        : allServices ? TextingServices.Default(TextingPreference, available) : null;
    /// <summary>The texting services offered, for the picker. It is worth showing only when there are two.</summary>
    public IReadOnlyList<ChatAccount> TextingAccounts => TextingServices.Accounts(available);
    /// <summary>
    /// The texting service the one person on the To line would be written to through, when another one could take the
    /// text instead: the Start button then names it and offers the others. Only while every service is searched (a
    /// picked card is its own answer), and only for a full number either service can text.
    /// </summary>
    public bool CanSwitchTexting => allServices && accountId is null && !IsStarting && Participants.Count == 1 && TextingAccounts.Count > 1
        && TextingServices.IsTexting(NetworkOf(Participants[0].AccountId))
        && TextingServices.Number(Destination(Participants[0])) is not null;

    /// <summary>The texting service the one person on the To line is on, for the Start button to name. Null otherwise.</summary>
    public ChatAccount? TextingRecipientAccount => Participants.Count == 1 && TextingAccounts.Count > 1
        ? TextingAccounts.FirstOrDefault(account => account.Id == Participants[0].AccountId) : null;

    /// <summary>
    /// Sends this text, and every new one after it, through another texting service: the person on the To line moves to
    /// it (with its conversation with them, when there is one) and it becomes the default. The caller saves the choice
    /// and redraws the list.
    /// </summary>
    public bool SwitchTexting(string id)
    {
        if (!CanSwitchTexting || TextingAccounts.All(account => account.Id != id) || !Allowed(id)) return false;
        TextingPreference = id;
        var person = Participants[0];
        if (person.AccountId == id) return true;
        var moved = TextingServices.Retarget(Destination(person), id, NetworkOf(id), backend is IDirectChatLookup lookup ? lookup.FindDirectChat : null);
        if (moved is not { AccountId: var target, Contact: { } contact } || target != id) return false;
        if (!Contacts.Contains(contact)) Contacts.Add(contact);
        pinnedAccount = null;
        Participants.Clear();
        Participants.Add(contact);
        PinService(id);
        Status = RecipientStatus();
        return true;
    }

    private ChatDestination Retargeted(ChatDestination destination) => TextingAccount is { } target
        ? TextingServices.Retarget(destination, target, NetworkOf(target), backend is IDirectChatLookup lookup ? lookup.FindDirectChat : null)
        : destination;
    /// <summary>The service this chat is going to: the one picked, or the one the first recipient settled.</summary>
    private string? EffectiveAccount => accountId ?? pinnedAccount;
    /// <summary>The service a second recipient would have to be on, once one has been chosen. Null while any will do.</summary>
    public string? LockedNetwork => EffectiveAccount is { } id && Participants.Count > 0 ? NetworkOf(id) : null;
    private bool Allowed(string account) => options.TryGetValue(account, out var o) && o.CanStart;
    private string NetworkOf(string account) =>
        available.FirstOrDefault(candidate => candidate.Id == account)?.Network ?? "";

    public bool CanStart(ContactPerson? contact) => !IsBusy && !Closed && contact is { CannotMessage: false }
        && Scope.Contains(contact.AccountId) && Allowed(contact.AccountId)
        && (Contacts.Contains(contact) || Participants.Contains(contact));
    // An existing one-to-one conversation opens straight from synchronized chats instead of waiting for the phone.
    public ChatSummary? FindExistingChat(ContactPerson contact) => CanStart(contact) && backend is IDirectChatLookup lookup ? lookup.FindDirectChat(contact) : null;

    /// <summary>The services the pane is offering, so results can name the one they came from.</summary>
    public void SetAvailableAccounts(IReadOnlyList<ChatAccount> accounts) => available = [.. accounts];

    // New chat pane: a chosen person becomes a pill that survives further lookups. A second person needs group creation.
    public bool AddRecipient(ContactPerson person)
    {
        if (IsStarting || person.CannotMessage || !Contacts.Contains(person) || Participants.Any(p => p.Id == person.Id && p.AccountId == person.AccountId)) return false;
        if (!Scope.Contains(person.AccountId) || !Allowed(person.AccountId)) return false;
        // A group lives on one service. No provider here can make one that spans two, so somebody on a different
        // service is refused by name rather than by the row appearing to do nothing when clicked.
        if (EffectiveAccount is { } locked && Participants.Count > 0 && person.AccountId != locked)
        {
            var theirs = NetworkOf(person.AccountId);
            var ours = NetworkOf(locked);
            Status = $"{person.Name} is on {theirs}, and this chat is going to {ours}. A group has to be on one service — remove the other people to write to {theirs} instead.";
            return false;
        }
        // Same service, but this one cannot make groups. The provider's own reason says which service and why.
        if (Participants.Count > 0 && !GroupOptions.Allowed)
        { Status = GroupOptions.Reason + " Remove the person on the To line to choose somebody else."; return false; }
        if (GroupOptions.Allowed && Participants.Count >= GroupOptions.Maximum) return false;
        Participants.Add(person);
        // The first recipient settles the service. Searching stays across everything so the next click can still be
        // explained rather than ignored.
        if (Participants.Count == 1 && pinnedAccount is null && accountId is null) PinService(person.AccountId);
        Status = RecipientStatus();
        return true;
    }

    private string RecipientStatus() => !GroupOptions.Allowed ? "Press Enter or the button above to open the conversation."
        : Participants.Count == 1 ? "Start the chat, or add more people on this service for a group." : "Create the group, or add more people.";

    /// <summary>Takes somebody off the To line, and releases the service once nobody is left on it.</summary>
    public void RemoveRecipient(ContactPerson person)
    {
        if (IsStarting) return;
        Participants.Remove(person);
        if (Participants.Count == 0) ReleaseService();
    }

    public void RemoveLastRecipient()
    {
        if (IsStarting || Participants.Count == 0) return;
        Participants.RemoveAt(Participants.Count - 1);
        if (Participants.Count == 0) ReleaseService();
    }

    private void ReleaseService()
    {
        if (pinnedAccount is null) return;
        pinnedAccount = null;
        GroupOptions = new(false, "Choose one person to start, or pick a service for a group.");
        Status = "Search across every connected service.";
        OnPropertyChanged(nameof(GroupOptions));
    }

    private void PinService(string id)
    {
        pinnedAccount = id;
        if (groupings.TryGetValue(id, out var known)) GroupOptions = known;
        OnPropertyChanged(nameof(GroupOptions));
    }

    /// <summary>
    /// Whether a second person could ever join somebody on this service. False means picking one person is the
    /// whole decision — Google Voice cannot make a group at all — so there is nothing left to wait for.
    /// </summary>
    public bool GroupsAllowedOn(string accountId) =>
        groupings.TryGetValue(accountId, out var known) ? known.Allowed : GroupOptions.Allowed;

    /// <summary>
    /// Clears the lookup and puts the default sections back. Both halves together: clearing the lookup on its own
    /// would leave the rows still listed while nobody on them could be picked any more.
    /// </summary>
    public void ResetToDefaults()
    {
        ClearLookup();
        ShowDefaults();
    }

    /// <summary>Search every connected service at once. The default the pane opens in.</summary>
    public async Task SelectAllServicesAsync()
    {
        if (IsStarting) return;
        ClearLookup(); accountId = null; pinnedAccount = null; allServices = true; Participants.Clear();
        GroupOptions = new(false, "Choose one person to start, or pick a service for a group.");
        options.Clear();
        var version = generation; IsBusy = true; request = new();
        var token = request.Token;
        try
        {
            var capturedProfile = await NotificationIdentity.ProfileAsync(tokens);
            if (version != generation) return;
            foreach (var account in available)
            {
                try
                {
                    options[account.Id] = await backend.GetContactOptionsAsync(account.Id, token);
                    if (groups is not null) groupings[account.Id] = await groups.GetGroupOptionsAsync(account.Id, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* A service that cannot answer simply offers nothing. */ }
            }
            if (version != generation) return;
            await LoadFrequentAsync(token);
            if (version != generation) return;
            profile = capturedProfile;
            CanSearch = options.Values.Any(option => option.CanSearch);
            Status = CanSearch ? "Search across every connected service." : "No connected service can be searched.";
            ShowDefaults();
        }
        catch (Exception ex) { if (version == generation) Status = Error(ex); }
        finally { if (version == generation) IsBusy = false; }
    }

    public async Task SelectAccountAsync(string? id)
    {
        if (IsStarting) return;
        ClearLookup(); accountId = id; allServices = false; CanSearch = false;
        Participants.Clear(); GroupOptions = new(false, "Choose a service."); options.Clear();
        if (id is null) { Rows.Clear(); return; }
        var version = generation; IsBusy = true; request = new();
        var token = request.Token;
        try
        {
            var capturedProfile = await NotificationIdentity.ProfileAsync(tokens);
            if (version != generation) return;
            var chosen = await backend.GetContactOptionsAsync(id, token);
            if (version != generation) return;
            var groupOptions = groups is null ? GroupOptions : await groups.GetGroupOptionsAsync(id, token);
            if (version != generation) return;
            GroupOptions = groupOptions;
            profile = capturedProfile;
            options[id] = chosen;
            await LoadFrequentAsync(token);
            if (version != generation) return;
            CanSearch = chosen.CanSearch; Status = groups is null ? chosen.Description : groupOptions.Reason;
            ShowDefaults();
        }
        catch (Exception ex) { if (version == generation) Status = Error(ex); }
        finally { if (version == generation) IsBusy = false; }
    }

    /// <summary>
    /// The sections shown before anything is typed. Both come from conversations already on this PC, so they cost
    /// nothing and are the same every time they are drawn.
    /// </summary>
    public void ShowDefaults()
    {
        lastQuery = "";
        Rows.Clear();
        if (conversations is null) return;
        var chats = InScope(conversations());
        // The service's own ranking first, when it has one: better evidence of who comes next than anything local.
        var ranked = FrequentDestinations();
        var suggested = NewChatDirectory.Suggested(chats, DateTimeOffset.UtcNow, except: ranked);
        var recent = NewChatDirectory.Recent(chats, except: [.. ranked, .. suggested]);
        // A number reached on the other texting service is offered on the one new texts go to, once across every section.
        var offered = new OfferedDestinations();
        Section("Frequent", ranked);
        Section("Suggested", suggested);
        Section("Recent", recent);

        void Section(string header, IReadOnlyList<ChatDestination> destinations)
        {
            var shown = destinations.Select(Retargeted).Where(offered.Add).ToArray();
            if (shown.Length == 0) return;
            Rows.Add(new(header, null));
            foreach (var destination in shown) Offer(destination);
        }
    }

    /// <summary>
    /// Asks each service in scope that keeps one for its ranking of who this account writes to most — at most once per
    /// New chat, only for the pane that draws the default sections, and waits for it no longer than
    /// <see cref="FrequentWait"/>. Only Google Messages answers; for every other service this is nothing.
    /// </summary>
    private async Task LoadFrequentAsync(CancellationToken token)
    {
        if (conversations is null || backend is not IFrequentContacts ranking) return;
        var waiting = new List<Task>();
        foreach (var account in Scope.Where(Allowed))
        {
            if (!frequentAsked.TryGetValue(account, out var asked))
                frequentAsked[account] = asked = AskFrequentAsync(ranking, account);
            if (!asked.IsCompleted) waiting.Add(asked);
        }
        if (waiting.Count > 0)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(Task.WhenAll(waiting), Task.Delay(FrequentWait, time, stop.Token));
            stop.Cancel();
        }
    }

    private static async Task<IReadOnlyList<ContactPerson>?> AskFrequentAsync(IFrequentContacts ranking, string account)
    {
        // Not tied to this pass's token: switching services here would otherwise throw away an answer already on its way.
        try { return await ranking.GetFrequentContactsAsync(account); }
        catch (Exception) { return null; }
    }

    /// <summary>The Frequent rows for the services in scope, in each service's own order.</summary>
    private IReadOnlyList<ChatDestination> FrequentDestinations()
    {
        var lookup = backend as IDirectChatLookup;
        var shown = new List<ChatDestination>();
        foreach (var account in Scope)
        {
            if (!frequentAsked.TryGetValue(account, out var asked) || !asked.IsCompletedSuccessfully || asked.Result is not { } people) continue;
            foreach (var destination in NewChatDirectory.Frequent(people.Where(person => person.AccountId == account),
                         NetworkOf(account), lookup is null ? null : lookup.FindDirectChat))
                if (!shown.Any(row => row.Key == destination.Key)) shown.Add(destination);
        }
        return shown;
    }

    /// <summary>
    /// Puts a destination on screen and makes the person behind it pickable. Both halves matter: a row nobody can
    /// select is a row that does nothing when clicked.
    /// </summary>
    private void Offer(ChatDestination destination)
    {
        if (destination.Contact is { } person && !Contacts.Contains(person)) Contacts.Add(person);
        Rows.Add(new(null, destination));
    }

    /// <summary>
    /// The conversation already on this PC for somebody picked from the list, so starting a chat with them opens it
    /// instead of asking a provider for what is already here.
    /// </summary>
    public ChatSummary? ConversationFor(ContactPerson contact) =>
        Rows.Select(row => row.Destination)
            .FirstOrDefault(destination => destination?.Contact == contact && destination.Conversation is not null)?.Conversation;

    private IReadOnlyList<ChatSummary> InScope(IReadOnlyList<ChatSummary> chats)
    {
        var scope = Scope;
        return scope.Count == 0 ? [] : [.. chats.Where(chat => scope.Contains(chat.AccountId))];
    }

    public void ClearLookup()
    {
        if (IsStarting) return;
        generation++; request?.Cancel(); request?.Dispose(); request = null; Contacts.Clear(); IsBusy = false;
        IsSearchingRemote = false;
    }

    /// <summary>
    /// Everything that can be matched without asking a provider over the network: the people already in a
    /// conversation on any service, plus the providers that search a directory already held here. Runs on every
    /// keystroke, because none of it costs anything.
    /// </summary>
    public async Task SearchLocalAsync(string query)
    {
        if (IsStarting || !CanSearch) return;
        var text = query.Trim();
        if (text.Length == 0) { ClearLookup(); ShowDefaults(); return; }
        ClearLookup();
        lastQuery = text;
        if (text.Length > 1000) { Status = "Enter 1–1,000 characters to look up."; return; }
        var version = generation; request = new(); IsBusy = true;
        var token = request.Token;
        var found = new List<NewChatResult>();
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) throw new SendException(false, "Connection changed. Close and reopen New chat.");
            if (version != generation) return;
            // The service's frequent people who match come first, in the service's own order, ahead of every other
            // match; first in this list also wins when the same person turns up again below.
            var ranked = FrequentDestinations();
            for (var place = 0; place < ranked.Count; place++)
                if (NameMatch.Rank(ranked[place].Name, ranked[place].Phone, text) is not null)
                    found.Add(new(ranked[place], NewChatRank.Frequent, place));
            if (conversations is not null)
                found.AddRange(NewChatDirectory.MatchLocal(InScope(conversations()), text, DateTimeOffset.UtcNow));
            // Providers whose directory is already on this PC. Asking them is a local match, not a round trip.
            foreach (var account in Scope.Where(id => options.TryGetValue(id, out var o) && o is { CanSearch: true, RemoteSearch: false }))
            {
                try
                {
                    foreach (var person in await backend.FindContactsAsync(account, text, token))
                        found.Add(new(Destination(person), NewChatRank.ExistingConversation, 0));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* One service failing does not empty the list. */ }
            }
            // With one texting service picked, the other's people are offered on it too: the phone's contact list can
            // be short, and Google Voice's is the whole of the account's Google Contacts. Both are held on this PC.
            if (accountId is { } picked && TextingAccount == picked)
                foreach (var other in TextingAccounts.Where(account => account.Id != picked))
                {
                    try
                    {
                        foreach (var person in await backend.FindContactsAsync(other.Id, text, token))
                            found.Add(new(Destination(person), NewChatRank.ExistingConversation, 0));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* Only an addition. */ }
                }
            if (version != generation) return;
            Publish(found, remotePending: Scope.Any(Remote) && RemoteSearchRules.Allows(text));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) Status = Error(ex); }
        finally { if (version == generation) IsBusy = false; }
    }

    private bool Remote(string account) => options.TryGetValue(account, out var o) && o is { CanSearch: true, RemoteSearch: true };

    /// <summary>
    /// Asks Messenger and Instagram, which search over the network. Called only once typing has stopped and only for
    /// a query worth spending a call on; a query already answered is served from memory without asking again.
    /// </summary>
    public async Task SearchRemoteAsync(string query)
    {
        if (IsStarting || !CanSearch) return;
        var text = query.Trim();
        if (text != lastQuery || !RemoteSearchRules.Allows(text)) return;
        var remotes = Scope.Where(Remote).ToArray();
        if (remotes.Length == 0) return;
        var version = generation;
        var token = request?.Token ?? CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        var cached = new List<NewChatResult>();
        var ask = new List<string>();
        foreach (var account in remotes)
        {
            if (remoteCache.Lookup(account, text, now) is { } remembered)
            {
                cached.AddRange(remembered.Select(person => new NewChatResult(Destination(person), NewChatRank.CachedRemote, 0)));
                continue;
            }
            if (remoteCache.MayCall(account, now)) ask.Add(account);
        }
        if (cached.Count > 0) Merge(cached);
        if (ask.Count == 0) return;
        IsSearchingRemote = true;
        try
        {
            // Once a remote search has earned its round trip, the providers run together rather than in turn.
            var answers = await Task.WhenAll(ask.Select(async account =>
            {
                remoteCache.Called(account, DateTimeOffset.UtcNow);
                try
                {
                    var people = await backend.FindContactsAsync(account, text, token);
                    remoteCache.Remember(account, text, people, DateTimeOffset.UtcNow);
                    return people;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return (IReadOnlyList<ContactPerson>)[]; }
            }));
            // The words may have moved on while this was in flight; a stale answer is dropped rather than drawn.
            if (version != generation || text != lastQuery) return;
            Merge([.. answers.SelectMany(people => people).Select(person => new NewChatResult(Destination(person), NewChatRank.FreshRemote, 0))]);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Remote results are an addition; failing to get them leaves the local ones standing. */ }
        finally { if (version == generation) IsSearchingRemote = false; }
    }

    private ChatDestination Destination(ContactPerson person) =>
        new(person.AccountId, NetworkOf(person.AccountId), person.Id, person.Name,
            person.AvatarUrl, person.Phone, null, person);

    /// <summary>
    /// Adds results without disturbing what is already on screen. Rows are ordered by where they came from, and
    /// every remote rank sorts below every local one, so an answer arriving late lands underneath rather than
    /// rearranging the list somebody is already reading.
    /// </summary>
    private void Merge(IReadOnlyList<NewChatResult> arriving)
    {
        // The same conversation reached two ways (a phone's ranking names somebody by number, the conversation by its
        // participant) is one row, and so is the same number on the service new texts go to, found by either service.
        // Ordered first, so the row kept is the best-ranked one.
        var offered = new OfferedDestinations(Rows.Select(row => row.Destination).OfType<ChatDestination>());
        var added = arriving.Select(result => result with { Destination = Retargeted(result.Destination) })
            .OrderBy(result => result.Rank)
            // Frequent people keep the service's own order; everything else is alphabetical within its rank.
            .ThenBy(result => result.Rank == NewChatRank.Frequent ? result.Score : 0)
            .ThenBy(result => result.Destination.Name, StringComparer.OrdinalIgnoreCase)
            .Where(result => offered.Add(result.Destination))
            .ToArray();
        foreach (var result in added) Offer(result.Destination);
        if (Rows.Count == 0) Status = $"No results for “{lastQuery}”.";
    }

    private void Publish(IReadOnlyList<NewChatResult> results, bool remotePending)
    {
        Rows.Clear(); Contacts.Clear();
        Merge(results);
        Status = Rows.Count == 0
            ? remotePending ? "Searching…" : $"No results for “{lastQuery}”."
            : remotePending ? $"{Rows.Count} so far — still searching." : $"{Rows.Count} found.";
    }

    /// <summary>Kept for the group dialog, which searches one service and has no separate remote pass.</summary>
    public Task SearchAsync(string query) => SearchLocalAsync(query);

    /// <summary>
    /// Set when the provider cannot open this conversation until something has been said in it. New chat answers by
    /// asking for the first message; it is cleared as soon as anything else changes, so it can never be answered on
    /// behalf of a different person than the one it was raised for.
    /// </summary>
    public ContactPerson? NeedsFirstMessage { get; private set; }
    public int MaxFirstMessageLength => (backend as IFirstMessageStarter)?.MaxFirstMessageLength ?? 0;
    public bool CanSendFirstMessage(ContactPerson? contact, string text) =>
        !IsBusy && !Closed && backend is IFirstMessageStarter starter && contact is not null
        && NeedsFirstMessage == contact && CanStart(contact)
        && !string.IsNullOrWhiteSpace(text) && text.Length <= starter.MaxFirstMessageLength;
    public void ClearFirstMessage() => NeedsFirstMessage = null;

    public async Task<ChatSummary?> StartAsync(ContactPerson contact, bool allowInvite)
    {
        if (!CanStart(contact)) return null;
        NeedsFirstMessage = null;
        IsStarting = true; IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) throw new SendException(false, "Connection changed. Close and reopen New chat.");
            return await backend.StartDirectAsync(contact, allowInvite);
        }
        // Not a failure: the provider is saying this conversation begins with a message rather than with an opening.
        catch (FirstMessageRequiredException ex) when (backend is IFirstMessageStarter)
        { NeedsFirstMessage = contact; Status = ex.Message; return null; }
        catch (SendException ex) { Close(ex); Status = ex.Message; return null; }
        catch (Exception ex) { Status = Error(ex); return null; }
        finally { IsStarting = false; IsBusy = false; }
    }

    /// <summary>
    /// Starts the conversation by sending its first message. The send is never retried here: if the outcome is
    /// unknown the message may already have arrived, and New chat closes itself to the attempt rather than offering
    /// a button that would send it twice.
    /// </summary>
    public async Task<ChatSummary?> SendFirstMessageAsync(ContactPerson contact, string text)
    {
        if (backend is not IFirstMessageStarter starter || !CanSendFirstMessage(contact, text)) return null;
        IsStarting = true; IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) throw new SendException(false, "Connection changed. Close and reopen New chat.");
            var started = await starter.StartDirectWithMessageAsync(contact, text.Trim());
            NeedsFirstMessage = null;
            return started;
        }
        catch (SendException ex) { Close(ex); Status = ex.Message; return null; }
        catch (Exception ex) { Status = Error(ex); return null; }
        finally { IsStarting = false; IsBusy = false; }
    }

    public void AddParticipant(ContactPerson person)
    {
        if (IsBusy || !GroupOptions.Allowed || !Scope.Contains(person.AccountId) || person.CannotMessage || person.IsSelf
            || !Contacts.Contains(person) || Participants.Any(p => p.Id == person.Id) || Participants.Count >= GroupOptions.Maximum) return;
        Participants.Add(person);
    }
    public bool CanCreateGroup(string title) => !IsBusy && !Closed && EffectiveAccount is { } group && GroupOptions.Validate(Participants.ToArray(), group, title) is null;
    public async Task<ChatSummary?> CreateGroupAsync(string title)
    {
        if (groups is null || !CanCreateGroup(title)) return null;
        IsStarting = true; IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) throw new SendException(false, "Connection changed. Reopen group creation.");
            return await groups.CreateGroupAsync(EffectiveAccount!, Participants.ToArray(), title);
        }
        catch (SendException ex) { Close(ex); Status = ex.Message; return null; }
        catch (Exception ex) { Status = Error(ex); return null; }
        finally { IsStarting = false; IsBusy = false; }
    }
    private static string Error(Exception ex) => ex is BackendException or SendException ? ex.Message : "Contact lookup could not complete. Check your connection and try again.";
    public void Dispose() { ClearLookup(); remoteCache.Clear(); }
}
