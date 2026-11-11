using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.Presentation.Services;

// Membership and group preferences belong to this PC, never to a provider. Member snapshots keep
// a linked service reachable while it is offline or its inbox page has not been loaded yet.
public sealed record MergedConversation(string Id, string Title, ChatSummary[] Members,
    bool Archived = false, bool Pinned = false, bool Muted = false, bool MarkedUnread = false, DateTimeOffset? SnoozedUntil = null,
    ManualSendingRoute? SendingRoute = null,
    // When a timed mute ends. Muted with no end lasts until the user turns it off, which is also how a group saved before
    // this field existed reads back: the safe direction for a downgrade.
    DateTimeOffset? MutedUntil = null,
    // "Always send with" this conversation (MergedConversations.Pins.cs): kept until the user clears it, the conversation
    // leaves the merge or its service signs out. Null, the default and how a group saved before this reads back, is the
    // ordinary rule.
    PinnedSendingRoute? PinnedRoute = null,
    // Why a pin ended by itself, kept until the conversation has said so once.
    EndedPin? PinEnded = null);
public sealed record ManualSendingRoute(string ChatId, DateTimeOffset ChosenAt);

public sealed partial class MergedConversations(PrivateState state, Func<ChatSummary, ChatSummary?>? resolveVoiceText = null, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? profile;
    private MergedConversation[] groups = [];
    // The store and row names, shared with a server's other clients from protocol 5 (SharedCategories), and kept on this PC under the same key before that.
    private const string Category = "merges";
    private const string Row = "groups";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChatSummary> known = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> accountNames = new();
    public void SetAccounts(IEnumerable<ChatAccount> accounts)
    {
        accountNames.Clear();
        foreach (var account in accounts) accountNames[account.Id] = account.DisplayName;
    }
    public string? AccountName(string accountId) => accountNames.GetValueOrDefault(accountId);
    // The members whose service gave its newest page but was still refreshing it (a Messenger restored page) at the last
    // read of each merged conversation's newest page (MergedChatReader). That page says only that something is refreshing,
    // and a service that has not answered says so too, so this tells the two apart. Memory only.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> refreshing = new(StringComparer.Ordinal);
    public void ObserveRefreshing(string id, IReadOnlyCollection<string> members)
    {
        if (members.Count == 0) refreshing.TryRemove(id, out _);
        else refreshing[id] = members.ToArray();
    }
    public IReadOnlyList<string> StillRefreshing(string id) => refreshing.TryGetValue(id, out var members) ? members : [];
    public string ServiceRouteLabel(ChatSummary chat)
    {
        var accounts = KnownChats().Where(c => c.Network == chat.Network).Select(c => c.AccountId).Append(chat.AccountId).Distinct().Order(StringComparer.Ordinal).ToArray();
        var account = accounts.Length > 1 ? accountNames.GetValueOrDefault(chat.AccountId) ?? "Account " + (Array.IndexOf(accounts, chat.AccountId) + 1) : null;
        // A Voice account is named "Google Voice (202) 555-0100"; after the service it is only the number, not
        // "Google Voice · Google Voice (202) 555-0100" (owner, 2026-11-11).
        if (account is not null && account.StartsWith(chat.Network + " ", StringComparison.Ordinal) && account.Length > chat.Network.Length + 1)
            account = account[(chat.Network.Length + 1)..];
        return chat.Network + (account is null ? "" : " · " + account);
    }
    public string ServiceLabel(ChatSummary chat) => ServiceRouteLabel(chat) + " · " + chat.Title;
    public event EventHandler? Changed;
    public static bool IsMerged(string? id) => id?.StartsWith("merged:", StringComparison.Ordinal) == true;
    public static bool IsCall(ChatSummary chat) => chat.IsCallHistory || chat.Network == "Google Voice" && chat.Preview?.CallKind is not null;
    public static bool CanMerge(ChatSummary chat) => !IsMerged(chat.Id) && chat.MemberChatIds.Count == 0 &&
        // Providers classify direct chats; Google may list the same person under several identities.
        chat.Type == "single" && chat.Participants.Any(p => p.IsSelf != true) && !chat.IsSpam;
    // Whether a conversation is in no merge, without knowing the merges: every way in (CanMerge, a Voice person's other conversations, a call's text
    // route) takes one-to-one conversations only, so a group never is. Only a type that says group counts; an unknown or missing one might be a member.
    // The host of the client modes takes the conversations this says yes to whatever it knows of the merges, and the rest only once it reads them from
    // the shared state (IncomingUnarchiveSplit).
    public static bool NeverMember(ChatSummary chat) => !IsMerged(chat.Id) && chat.Type == "group";

    public Task EnsureLoadedAsync() => LoadAsync(reload: false);

    // A restore adds saved groups under the profile already loaded; they are read again, or the next change would write
    // the groups held here over them.
    public Task ReloadAsync() => LoadAsync(reload: true);

    public IReadOnlyList<string> GroupIds => groups.Select(g => g.Id).ToArray();

    private async Task LoadAsync(bool reload)
    {
        await gate.WaitAsync();
        try
        {
            var next = await state.ProfileAsync();
            // A client of a server whose replica could not bring it to the host's row (Abandon) reads it again.
            if (next == profile && !reload && !stale) return;
            // A client of a server holding changes the host has not taken yet holds more than its replica says while they are on their way; the replica
            // brings the host's row to it as it comes (CatchUpAsync), never by a read here.
            if (next == profile && !stale && Unconfirmed > 0) return;
            // Do not keep another profile's membership if loading the new one fails. The same profile's conversations,
            // seen on the list, are still its own.
            if (next != profile)
            {
                known.Clear();
                KnownChanged();
                lock (changes) unconfirmed.Clear();
            }
            profile = null; groups = [];
            // A client of a server reads the row and becomes its follower in one step, so a host row the replica takes after this read is handed here
            // (CatchUpAsync) and none taken before it is missed: what this holds is always what the replica's rev says, which its writes are conditional on.
            var saved = next is null ? [] : state.Shared is null
                ? await state.ReadAsync<MergedConversation[]>(next, Category, Row) ?? []
                : await state.FollowAsync<MergedConversation[]>(next, Category, Row, new Follower(this, next)) ?? [];
            groups = Usable(saved);
            hostGroups = groups;
            stale = false;
            profile = next;
        }
        finally { gate.Release(); }
    }

    private static MergedConversation[] Usable(MergedConversation[] saved) => saved.Where(g => IsMerged(g.Id) && g.Members.Length >= 2).ToArray();

    // Provider rows already carry no parent, and a list rebuild observes every loaded row: copying each one anyway made a
    // new summary per row per rebuild. A row this already holds as the very same summary changes no membership.
    public void Observe(IEnumerable<ChatSummary> chats)
    {
        var changed = false;
        foreach (var chat in chats)
        {
            if (IsMerged(chat.Id)) continue;
            var value = chat.MergedIntoChatId is null ? chat : chat with { MergedIntoChatId = null };
            if (known.TryGetValue(chat.Id, out var held) && ReferenceEquals(held, value)) continue;
            known[chat.Id] = value;
            changed = true;
        }
        if (changed) KnownChanged();
    }
    // Enumerating the dictionary itself takes no lock and copies nothing; Values locks every stripe and copies them all.
    private IEnumerable<ChatSummary> KnownChats() { foreach (var pair in known) yield return pair.Value; }
    public void Forget(string id) { if (known.TryRemove(id, out _)) KnownChanged(); }
    public MergedConversation? Find(string id) => groups.FirstOrDefault(g => g.Id == id);
    public static bool SameVoicePerson(ChatSummary member, ChatSummary candidate) =>
        member.Network == "Google Voice" && candidate.Network == "Google Voice" && member.AccountId == candidate.AccountId &&
        member.Type == "single" && candidate.Type == "single" && !candidate.IsSpam && SameOthers(member.Participants, candidate.Participants);
    // The same people besides the account itself, each as many times: what comparing the two lists of ids sorted said, without
    // sorting a copy of each. It runs for every known conversation against each Voice member whenever the merges' members are
    // worked out, and the sorted copies were most of what that allocated. One-to-one rows hold a person or two, so counting
    // each id in both lists costs less than sorting them did.
    private static bool SameOthers(IReadOnlyList<ChatParticipant> member, IReadOnlyList<ChatParticipant> candidate)
    {
        var others = Others(member);
        if (others == 0 || others != Others(candidate)) return false;
        for (var i = 0; i < member.Count; i++)
            if (member[i].IsSelf != true && Carrying(member, member[i].Id) != Carrying(candidate, member[i].Id)) return false;
        return true;
    }
    private static int Others(IReadOnlyList<ChatParticipant> people)
    {
        var count = 0;
        for (var i = 0; i < people.Count; i++) if (people[i].IsSelf != true) count++;
        return count;
    }
    private static int Carrying(IReadOnlyList<ChatParticipant> people, string id)
    {
        var count = 0;
        for (var i = 0; i < people.Count; i++) if (people[i].IsSelf != true && string.Equals(people[i].Id, id, StringComparison.Ordinal)) count++;
        return count;
    }
    public IReadOnlyList<ChatSummary> Members(string id) => id is not null && Current().Members.TryGetValue(id, out var members) ? members : [];
    // The text conversation a Voice call row routes to: the one this PC already knows by that id, else the local destination.
    public ChatSummary? VoiceTextRoute(ChatSummary call) =>
        resolveVoiceText?.Invoke(call) is { } text ? known.GetValueOrDefault(text.Id) ?? text : null;
    private IReadOnlyList<ChatSummary> Members(MergedConversation group)
    {
        var direct = group.Members.Select(m => known.GetValueOrDefault(m.Id) ?? Saved(m)).ToArray();
        var textRoutes = direct.Where(IsCall).Select(VoiceTextRoute).OfType<ChatSummary>();
        // Only a Voice member takes in other known conversations, so a merge without one reads none of them.
        var voice = direct.Where(m => m.Network == "Google Voice").ToArray();
        return direct.Concat(voice.Length == 0 ? [] : SameVoicePeople(voice)).Concat(textRoutes).DistinctBy(c => c.Id).ToArray();
    }
    // A member's copy saved with the merge says who is in it, not what is unread now: it was taken when the merge was made,
    // and nothing brings it up to date. A member not seen on the list this run is shown from it, and an old Voice call
    // record no longer on Voice's recent list held its merged row bold, and the taskbar badge at 1, for days after Voice
    // had it read (2026-10-14).
    private static ChatSummary Saved(ChatSummary member) =>
        member.UnreadCount == 0 && !member.IsMarkedUnread ? member : member with { UnreadCount = 0, IsMarkedUnread = false };
    // How many of a merge's own members the list has not shown this run, so are shown from their saved copies.
    public int SavedOnly(string id) => Find(id) is { } group ? group.Members.Count(m => !known.ContainsKey(m.Id)) : 0;
    // Every known conversation of the same Voice account and person as one of these, walked without a copy or a
    // per-conversation allocation: Members runs for every merge on every list rebuild.
    private IEnumerable<ChatSummary> SameVoicePeople(ChatSummary[] voice)
    {
        foreach (var pair in known)
            foreach (var member in voice)
                if (SameVoicePerson(member, pair.Value)) { yield return pair.Value; break; }
    }

    // Every merge's members and who shows each conversation, worked out once for the merges and the known conversations as
    // they are, and again only when either changes. DisplayId used to work out every merge's members for each id it was
    // asked, a Voice member's by testing every known conversation: the jump list's ranking (an id per recorded send or
    // open), Back and Forward (every loaded conversation) and the chat list's rebuilds asked it on the interface thread,
    // seen as 60-150 ms stalls allocating 156-178 MB each (2026-10-14). Replaced whole, never changed in place, because
    // notifications and the activity stream ask from other threads.
    private sealed class Membership
    {
        public readonly MergedConversation[] Groups;
        public readonly long Version;
        public readonly Dictionary<string, IReadOnlyList<ChatSummary>> Members = new(StringComparer.Ordinal);
        // The first merge holding each conversation, and that merge's summary of it, in the merges' order: what asking
        // each merge in turn answered.
        public readonly Dictionary<string, string> Display = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ChatSummary> Sources = new(StringComparer.Ordinal);
        private IReadOnlySet<string>? ids;
        public IReadOnlySet<string> Ids => ids ??= System.Collections.Frozen.FrozenSet.ToFrozenSet(Display.Keys, StringComparer.Ordinal);

        public Membership(MergedConversations owner, MergedConversation[] groups, long version)
        {
            Groups = groups;
            Version = version;
            foreach (var group in groups)
            {
                // Two merges with one id answer as the first, as Find does.
                if (!Members.TryGetValue(group.Id, out var members)) Members[group.Id] = members = owner.Members(group);
                foreach (var member in members)
                    if (member.Id is not null && Display.TryAdd(member.Id, group.Id)) Sources[member.Id] = member;
            }
        }
    }
    private volatile Membership? membership;
    // Bumped after every change to the known conversations, so a membership worked out before it is worked out again.
    private long knownVersion;
    private void KnownChanged() => Interlocked.Increment(ref knownVersion);
    private Membership Current()
    {
        // The version first: a change landing while this is worked out bumps it again, and the next caller works it out anew.
        var version = Interlocked.Read(ref knownVersion);
        var held = groups;
        var current = membership;
        if (current is not null && current.Version == version && ReferenceEquals(current.Groups, held)) return current;
        current = new(this, held, version);
        membership = current;
        return current;
    }
    // The merged conversation that shows this one: the first merge holding it, or the id itself (a null one too, as before).
    public string DisplayId(string id) => id is not null && Current().Display.TryGetValue(id, out var display) ? display : id!;
    // Every conversation some merged conversation holds.
    public IReadOnlySet<string> MemberIds() => Current().Ids;
    // Whether the user has added a conversation to this merge since a copy of its history listing these members was made.
    // Only the members the user chose count: Voice call records and text routes join by themselves as they load.
    public bool AddedSince(string id, IReadOnlyCollection<string> memberIds) =>
        Find(id) is { } group && group.Members.Any(m => !memberIds.Contains(m.Id));
    // The conversation to open once a merge is undone: the one being sent to when it is a row of the list again, otherwise
    // the first member the list has. A Voice text route made up for a call is not a row, and opening it showed nothing.
    public static string? UnmergeTarget(string? sendChatId, IEnumerable<ChatSummary> members, IReadOnlySet<string> loaded) =>
        sendChatId is not null && loaded.Contains(sendChatId) ? sendChatId : members.FirstOrDefault(m => loaded.Contains(m.Id))?.Id;
    public ChatSummary? Source(string id) => known.GetValueOrDefault(id) ?? Current().Sources.GetValueOrDefault(id);
    // A person filter follows only the links the user explicitly made, including differently named
    // service accounts and Voice call records. It never guesses that matching names are one person.
    public IReadOnlyList<string> SearchMemberIds(string person) => groups.Where(g => MessageSearch.WordsMatch(g.Title, person) ||
            Members(g.Id).Any(m => MessageSearch.WordsMatch(m.Title, person) || m.Participants.Any(p => p.IsSelf != true && MessageSearch.WordsMatch(p.DisplayName, person))))
        .SelectMany(g => Members(g.Id)).Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public Task RememberSendingRouteAsync(string id, string chatId)
    {
        if (!Members(id).Any(c => c.Id == chatId && !IsCall(c) && !c.IsReadOnly))
            throw new InvalidOperationException("Choose a linked text conversation.");
        var choice = new ManualSendingRoute(chatId, (clock ?? TimeProvider.System).GetUtcNow());
        // Choosing another service is the user's newer word, so a pin on a different one ends with it; choosing the pinned
        // one again keeps it. Without a pin this is the choice it always was.
        return SetAsync(id, group => group with { SendingRoute = choice, PinnedRoute = group.PinnedRoute?.ChatId == chatId ? group.PinnedRoute : null });
    }

    // A new incoming text ends the manual preference, including arrivals while the app was closed.
    // Older paging, outgoing echoes, edits, receipts and call records cannot reset it.
    public Task ObserveIncomingMessagesAsync(string id, IReadOnlyList<ChatMessage> messages)
    {
        var routes = Members(id).Where(c => !IsCall(c) && !c.IsReadOnly).Select(c => c.Id).ToHashSet();
        var newest = messages.Where(m => routes.Contains(m.ChatId) && !m.IsOutgoing && !m.IsDeleted && !m.IsHidden &&
            m.Type != "system" && m.CallKind is null && m.EditedAt is null).MaxBy(m => m.Timestamp);
        if (newest is null || Find(id)?.SendingRoute is not { } choice || newest.Timestamp <= choice.ChosenAt) return Task.CompletedTask;
        return SetAsync(id, group => group.SendingRoute is { } current && newest.Timestamp > current.ChosenAt
            ? group with { SendingRoute = null } : group);
    }

    public ChatSummary Summary(string id, IEnumerable<ChatSummary>? current = null) =>
        Summary(Find(id) ?? throw new InvalidOperationException("This merged conversation no longer exists."), current?.ToDictionary(c => c.Id), Members(id), Now());
    private ChatSummary Summary(MergedConversation group, IReadOnlyDictionary<string, ChatSummary>? overrides, IReadOnlyList<ChatSummary> known,
        DateTimeOffset now)
    {
        var id = group.Id;
        var members = known.Select(m => overrides?.GetValueOrDefault(m.Id) ?? m).ToArray();
        var newest = members.OrderByDescending(m => m.LastActivity).ThenBy(m => m.Id, StringComparer.Ordinal).First();
        var first = members.FirstOrDefault(m => !IsCall(m)) ?? members[0];
        var through = ReadThrough(members, overrides);
        var unread = 0;
        foreach (var member in members) if (Counts(member, through)) unread += member.UnreadCount;
        return first with
        {
            Id = id, AccountId = "local", Title = group.Title, Network = "Merged", Type = "single",
            LastActivity = newest.LastActivity, Preview = newest.Preview,
            UnreadCount = Math.Max(group.MarkedUnread ? 1 : 0, unread), IsMarkedUnread = group.MarkedUnread || members.Any(m => m.IsMarkedUnread),
            IsArchived = group.Archived, IsPinned = group.Pinned, IsMuted = MutedAt(group, now), IsSpam = false,
            MemberChatIds = members.Select(m => m.Id).ToArray(), DefaultMemberChatId = first.Id, MergedIntoChatId = null,
            Capabilities = ChatCapabilities.Unknown, IsEncrypted = null, ExpectedTransport = null, IsReadOnly = false,
            Participants = [], ParticipantCount = 1, SnoozedUntil = group.SnoozedUntil, IsCallHistory = false
        };
    }

    // Whose unread a merged row counts. Not a member its service files as spam (Instagram's hidden message requests): it is
    // never marked read from here. Not a call record (a missed call, a voicemail) older than what the conversation has
    // since been read or answered through: the merged history loads only its newest page, so a call from weeks back is
    // never on screen to be read, and it held the whole row bold under newer messages that were (Mom's, a missed call
    // from 2026-09-23, seen 2026-10-07). Texts are not judged this way: one on another service may be unseen content,
    // read on the phone's own app or not, and scrolling up to it reads it.
    private static bool Counts(ChatSummary member, DateTimeOffset? readThrough) =>
        member.UnreadCount > 0 && !member.IsSpam && !(member.IsCallHistory && member.LastActivity <= readThrough);
    // The newest activity of a text member its service says has nothing unread (read, or answered from any device). Only
    // one the list has shown: a saved copy's unread is never counted (Saved), which says nothing about its being read.
    private DateTimeOffset? ReadThrough(IEnumerable<ChatSummary> members, IReadOnlyDictionary<string, ChatSummary>? current = null) =>
        members.Where(m => !m.IsCallHistory && !m.IsSpam && m.UnreadCount <= 0 && (current?.ContainsKey(m.Id) == true || known.ContainsKey(m.Id)))
            .Max(m => m.LastActivity);

    /// <summary>
    /// Raised with the call records a merged row has stopped counting (<see cref="Counts"/>) that Voice still lists unread, each once
    /// per activity: shown read here, Voice was never told, and voice.google.com kept the missed call or voicemail unread under a
    /// conversation read through newer texts (2026-10-22 audit; the owner chose to tell Voice).
    /// </summary>
    public event EventHandler<IReadOnlyList<ChatSummary>>? CallRecordsSettled;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset?> settledAnnounced = new(StringComparer.Ordinal);
    private void AnnounceSettled(IEnumerable<ChatSummary> calls)
    {
        var fresh = calls.Where(call => !settledAnnounced.TryGetValue(call.Id, out var at) || at != call.LastActivity).ToArray();
        if (fresh.Length == 0) return;
        foreach (var call in fresh) settledAnnounced[call.Id] = call.LastActivity;
        CallRecordsSettled?.Invoke(this, fresh);
    }

    public IReadOnlyList<ChatSummary> Project(IEnumerable<ChatSummary> input)
    {
        var rows = input.Where(c => !IsMerged(c.Id)).ToArray();
        Observe(rows);
        // Each group's members, and who shows each conversation, as the rows just observed make them: worked out here once,
        // and what DisplayId and Members answer until the merges or the known conversations change again.
        var current = Current();
        var snapshot = current.Groups;
        if (snapshot.Length == 0) return rows;
        var members = current.Members;
        var membership = current.Display;
        var byId = rows.ToDictionary(c => c.Id);
        var now = Now();
        // A call record its merged row no longer counts is read for the per-service counts too, which count members.
        HashSet<string>? settled = null;
        foreach (var group in snapshot)
        {
            var known = members[group.Id];
            if (!known.Any(m => m.IsCallHistory)) continue;
            var present = known.Select(m => byId.GetValueOrDefault(m.Id) ?? m).ToArray();
            var through = ReadThrough(present, byId);
            foreach (var member in present)
                if (member.IsCallHistory && member.UnreadCount > 0 && !Counts(member, through)) (settled ??= new(StringComparer.Ordinal)).Add(member.Id);
        }
        if (settled is not null) AnnounceSettled(settled.Select(id => byId[id]));
        return rows.Select(c => !membership.TryGetValue(c.Id, out var parent) ? c
                : settled?.Contains(c.Id) == true ? c with { MergedIntoChatId = parent, UnreadCount = 0 } : c with { MergedIntoChatId = parent })
            .Concat(snapshot.Select(g => Summary(g, byId, members[g.Id], now))).ToArray();
    }

    public async Task<string> MergeAsync(string title, IReadOnlyList<ChatSummary> members)
    {
        await EnsureLoadedAsync();
        var distinct = members.DistinctBy(m => m.Id).ToArray();
        if (distinct.Length < 2 || distinct.Length > MaxMembers || distinct.Any(m => !CanMerge(m)))
            throw new InvalidOperationException("Choose between two and eight one-to-one conversations.");
        var id = "merged:" + Guid.NewGuid().ToString("N");
        await ChangeAsync(existing =>
        {
            // Made again over a row that already holds this merge (this device's write was taken and its answer lost): already done. A new id is never
            // there otherwise.
            if (existing.Any(g => g.Id == id)) return existing;
            Validate(existing, distinct);
            return [.. existing, new(id, string.IsNullOrWhiteSpace(title) ? distinct[0].Title : title.Trim(), distinct)];
        }, makes: id, members: [.. distinct.Select(m => m.Id)]);
        return id;
    }
    public const int MaxMembers = 8;

    /// <summary>
    /// Adds conversations to a merged conversation. Its name, local preferences and sending choice stay. The choice counts
    /// as made now: the added conversation's newest incoming message was sent before it was linked, and ending a choice
    /// the moment something is added would take the service the user picked away for a message they had already seen.
    /// </summary>
    public async Task AddMembersAsync(string id, IReadOnlyList<ChatSummary> additions)
    {
        await EnsureLoadedAsync();
        var distinct = additions.DistinctBy(m => m.Id).ToArray();
        if (distinct.Length == 0 || distinct.Any(m => !CanMerge(m))) throw new InvalidOperationException("Choose one-to-one conversations to add.");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await ChangeAsync(existing =>
        {
            var group = existing.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("This merged conversation no longer exists.");
            // Made again over a row that already holds it (this device's write was taken and its answer lost): already done.
            if (remaking && distinct.All(d => group.Members.Any(m => m.Id == d.Id))) return existing;
            if (group.Members.Length + distinct.Length > MaxMembers) throw new InvalidOperationException("A merged conversation holds up to eight conversations.");
            Validate(existing, distinct);
            return [.. existing.Select(g => g.Id != id ? g : g with
            {
                Members = [.. g.Members, .. distinct],
                SendingRoute = g.SendingRoute is { } route && route.ChosenAt < now ? route with { ChosenAt = now } : g.SendingRoute
            })];
        });
    }
    // Nothing already merged, this merge's own members included, and no Voice person whose call records a merge already
    // takes in: one person's call records cannot belong to two histories.
    private void Validate(MergedConversation[] existing, ChatSummary[] distinct)
    {
        if (Conflict(existing, distinct) is { } why) throw new InvalidOperationException(why);
    }
    // Judged against `existing`, not the groups held: a client of a server makes a change again over another device's merges (CatchUpAsync), where what
    // this one held, its own change among it, is not what the change is made over. Everywhere else the two are the same.
    private string? Conflict(MergedConversation[] existing, ChatSummary[] distinct)
    {
        // The merges held have their members worked out already (Current).
        var current = Current();
        bool Merged(string id) => ReferenceEquals(current.Groups, existing) && id is not null ? current.Display.ContainsKey(id)
            : existing.Any(g => Members(g).Any(member => member.Id == id));
        return distinct.Any(m => Merged(m.Id)) ? "A selected conversation is already merged."
            : existing.Any(g => g.Members.Any(m => distinct.Any(d => SameVoicePerson(m, d) || SameVoicePerson(d, m)))) ? "That Google Voice history is already merged."
            : null;
    }

    /// <summary>
    /// Whether <see cref="AddMembersAsync"/> would take these conversations into this merged conversation now: room for
    /// them, one-to-one conversations, none already merged. Asked before a plain conversation's Merge dialog offers a
    /// merged conversation to join, so it never offers one the add would then refuse. Writes nothing.
    /// </summary>
    public bool CanAdd(string id, IReadOnlyCollection<ChatSummary> additions)
    {
        var distinct = additions.DistinctBy(m => m.Id).ToArray();
        return Find(id) is { } group && distinct.Length > 0 && distinct.All(CanMerge) && group.Members.Length + distinct.Length <= MaxMembers &&
            Conflict(groups, distinct) is null;
    }
    public Task UnmergeAsync(string id) => ChangeAsync(existing => existing.Where(g => g.Id != id).ToArray());

    /// <summary>Whether one conversation can leave this merge: two have to stay, or the answer is Unmerge.</summary>
    public bool CanRemoveMember(string id) => Find(id) is { Members.Length: > 2 };

    /// <summary>
    /// Takes one conversation the user chose back out of a merged conversation: the undo for adding it. The name, local
    /// preferences and sending choice stay, unless the choice was a conversation that left (a Voice text route that
    /// followed a call member out among them), which is cleared in the same write. Voice call records and text routes that
    /// joined through a Voice member are not chosen on their own; they leave with it, as does another chosen member of the
    /// same Voice account and person, which would otherwise take it straight back in. Local only: no service is told.
    /// Returns every conversation that left, derived ones included. They are ordinary rows again, not forgotten.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveMemberAsync(string id, string memberId)
    {
        await EnsureLoadedAsync();
        string[] removed = [];
        await ChangeAsync(existing =>
        {
            var group = existing.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("This merged conversation no longer exists.");
            if (group.Members.FirstOrDefault(m => m.Id == memberId) is not { } chosen) throw new InvalidOperationException("Unmerge instead");
            // Another chosen member of the same Voice account and person would take this one straight back in, so they
            // leave together, and what stays has to be two.
            var person = known.GetValueOrDefault(chosen.Id) ?? chosen;
            var leaving = group.Members.Where(m => m.Id == memberId ||
                (known.GetValueOrDefault(m.Id) ?? m) is var other && (SameVoicePerson(other, person) || SameVoicePerson(person, other)))
                .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            if (group.Members.Length - leaving.Count < 2) throw new InvalidOperationException("Unmerge instead");
            var next = group with { Members = [.. group.Members.Where(m => !leaving.Contains(m.Id))] };
            var remaining = Members(next).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            if (next.SendingRoute is { } route && !remaining.Contains(route.ChatId)) next = next with { SendingRoute = null };
            next = WithoutPinOn(next, Members(group), remaining.Contains);
            removed = [.. Members(group).Select(m => m.Id).Where(member => !remaining.Contains(member))];
            return [.. existing.Select(g => g.Id == id ? next : g)];
        });
        return removed;
    }

    /// <summary>Whether the merged conversation is muted at this moment: muted with no end, or with an end still to come.</summary>
    public bool IsMuted(string id, DateTimeOffset now) => Find(id) is { } group && MutedAt(group, now);
    private static bool MutedAt(MergedConversation group, DateTimeOffset now) => group.Muted && (group.MutedUntil is null || group.MutedUntil > now);
    /// <summary>When a timed mute ends; null for no mute and for a mute until turned off.</summary>
    public DateTimeOffset? MutedUntil(string id) => Find(id) is { Muted: true } group ? group.MutedUntil : null;
    /// <summary>Mutes until a time, or until turned off when <paramref name="until"/> is null. Unmuting clears the time.</summary>
    public Task SetMutedAsync(string id, bool muted, DateTimeOffset? until) =>
        SetAsync(id, group => group with { Muted = muted, MutedUntil = muted ? until : null });
    private DateTimeOffset Now() => (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>What <see cref="ForgetMembersAsync"/> did: members taken out, the merged ids that lost any, and the merged
    /// ids removed for having fewer than two members left, each with the members that are listed on their own again.</summary>
    public sealed record ForgottenMembers(int Members, IReadOnlyList<string> Changed, IReadOnlyDictionary<string, string[]> Removed);

    /// <summary>
    /// Takes every member the test matches out of every merged conversation, with any sending choice that pointed at one;
    /// a pin on one ends as a signed-out service's does. A merged conversation left with fewer than two members is removed,
    /// as unmerging it would. One write.
    /// </summary>
    public async Task<ForgottenMembers> ForgetMembersAsync(Func<string, bool> matches)
    {
        await EnsureLoadedAsync();
        if (!groups.Any(g => g.Members.Any(m => matches(m.Id)) || g.SendingRoute is { } route && matches(route.ChatId) ||
                g.PinnedRoute is { } pin && matches(pin.ChatId)))
            return new(0, new List<string>(), new Dictionary<string, string[]>(StringComparer.Ordinal));
        var result = new ForgottenMembers(0, [], new Dictionary<string, string[]>(StringComparer.Ordinal));
        await ChangeAsync(existing =>
        {
            // Counted afresh each time, into collections of their own: a client of a server makes this change again over another device's merges
            // (CatchUpAsync), after the ones handed back here are the caller's.
            var members = 0;
            var changed = new List<string>();
            var removed = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var next = new List<MergedConversation>();
            foreach (var group in existing)
            {
                var kept = group.Members.Where(m => !matches(m.Id)).ToArray();
                var route = group.SendingRoute is { } choice && matches(choice.ChatId) ? null : group.SendingRoute;
                // What stays, derived Voice routes included: a pinned text route leaves with the call member it came from.
                var staying = Members(group with { Members = kept }).Select(m => m.Id).Where(id => !matches(id)).ToHashSet(StringComparer.Ordinal);
                // The user did not take the conversation out: its service's data went (a Google reset), as it does when a
                // service is signed out, and the pin's notice says that rather than that the conversation left.
                var unpinned = WithoutPinOn(group, Members(group), staying.Contains, EndedPin.SignedOut);
                if (kept.Length == group.Members.Length && route == group.SendingRoute && unpinned == group) { next.Add(group); continue; }
                members += group.Members.Length - kept.Length;
                changed.Add(group.Id);
                if (kept.Length < 2) removed[group.Id] = [.. kept.Select(m => m.Id)];
                else next.Add(unpinned with { Members = kept, SendingRoute = route });
            }
            result = new(members, changed, removed);
            return [.. next];
        });
        foreach (var id in known.Keys.Where(matches).ToArray()) known.TryRemove(id, out _);
        KnownChanged();
        return result;
    }
    // A timed mute that has ended is no mute by the time anything changes the group, and an unmuted group keeps no end.
    // A mute until turned off (a notification's Mute, the chat menu, the console) clears MutedUntil itself: settling keeps
    // an end still to come, so a caller setting only Muted would leave a running timed mute in place.
    public Task SetAsync(string id, Func<MergedConversation, MergedConversation> update) =>
        ChangeAsync(existing =>
        {
            var now = Now();
            return existing.Select(g => g.Id == id ? Settled(update(Settled(g, now)), now) : g).ToArray();
        });
    private static MergedConversation Settled(MergedConversation group, DateTimeOffset now) =>
        group.MutedUntil is null ? group : !group.Muted ? group with { MutedUntil = null }
            : group.MutedUntil <= now ? group with { Muted = false, MutedUntil = null } : group;
    // `makes` and `members`: a new merge's id and its members, for what a client of a server is told if the change is left out (MergesDropped).
    private async Task ChangeAsync(Func<MergedConversation[], MergedConversation[]> update, string? makes = null, string[]? members = null)
    {
        await gate.WaitAsync();
        try
        {
            // Stale: a client of a server whose replica could not bring it to the host's row refuses changes until it has read the row again.
            if (profile is null || stale || profile != await state.ProfileAsync()) throw new InvalidOperationException("Reconnect before changing merged conversations.");
            var next = update(groups);
            if (state.Shared is null) await state.WriteAsync(profile, Category, Row, next);
            else await WriteSharedAsync(profile, next, new Change(update, SharedRowTags.Next(), makes, members ?? [], next));
            groups = next;
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
