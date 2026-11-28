using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
namespace MessageLink.Presentation.Services;
/// <summary>
/// A conversation with this PC's settings applied again (<see cref="LocalPreferenceChatReader.ReapplyAsync"/>). Version: the
/// copy of the source's it was applied to, 0 for the row it was given. Names: whether its title and participants' names are
/// this PC's as they are now (only from a copy; the row given keeps its own).
/// </summary>
public sealed record ReappliedChat(ChatSummary Chat, long Version, bool Names);
// isInstagram: which conversations belong to a service that cannot archive from here, so an archive kept on this PC
// applies to them. The default reads the id's service prefix, the same test as the Instagram backend's own.
// remembersSources: keep the source's own copy of each conversation read, so ReapplyAsync can apply this PC's settings
// again when another device changed them (a client of a Messages Server only).
public sealed class LocalPreferenceChatReader(IChatReader source, ILocalChatPreferences preferences, IAccessTokenStore tokens,
    ILocalContactNames? names = null, IChatSnoozes? snoozes = null, bool mergedOnly = false, Func<string, bool>? isInstagram = null,
    TimeProvider? clock = null, bool remembersSources = false) : IChatReader, ILinkPreviewLoader
{
    private readonly Func<string, bool> instagram = isInstagram ?? (static id => id.StartsWith("instagram:", StringComparison.Ordinal));
    // The last copy the source gave of each conversation, as it gave it, numbered in the order read; null unless
    // remembersSources. Under `remembering`, with the profile they were read under: another profile's copies are let go.
    private readonly Dictionary<string, (ChatSummary Chat, long Version)>? sources = remembersSources ? new(StringComparer.Ordinal) : null;
    private readonly object remembering = new();
    private string? rememberedProfile;
    private long versions;
    // Twice the list's 2,000 rows: past it the oldest copies go (KeepOnly trims to the list at each change from elsewhere).
    private const int MaxRemembered = 4000;
    // Fixed words only (see AppLog): which conversation is never said. Raised on whatever thread read the conversation.
    public static event Action<string>? Diagnostic;
    // Each archive an arrival has lifted, by conversation and the moment it was archived, so a burst of reads (or one that
    // started before the file went) removes it once. Archiving again makes a new moment, which an arrival lifts again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Chat, DateTimeOffset At), byte> lifting = new();
    private Task lifted = Task.CompletedTask;
    /// <summary>The last removal an arrival started; the offline checks wait on it.</summary>
    public Task PendingLift => Volatile.Read(ref lifted);
    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (mergedOnly && !MergedConversations.IsMerged(chatId)) return await source.GetChatAsync(chatId, cancellationToken);
        var profile = await NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        var chat = await source.GetChatAsync(chatId, cancellationToken);
        Remember(chat, profile);
        return await ApplyAsync(chat, profile, cancellationToken);
    }
    public async Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        if (mergedOnly) return await source.GetChatsAsync(olderThan, cancellationToken);
        var profile = await NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        var page = await source.GetChatsAsync(olderThan, cancellationToken);
        var chats = new List<ChatSummary>();
        foreach (var chat in page.Items)
        {
            Remember(chat, profile);
            chats.Add(await ApplyAsync(chat, profile, cancellationToken));
        }
        return page with { Items = chats };
    }

    private void Remember(ChatSummary chat, string? profile)
    {
        if (sources is null) return;
        lock (remembering)
        {
            if (!string.Equals(profile, rememberedProfile, StringComparison.Ordinal)) { sources.Clear(); rememberedProfile = profile; }
            sources[chat.Id] = (chat, ++versions);
            if (sources.Count <= MaxRemembered) return;
            foreach (var (id, _) in sources.OrderBy(pair => pair.Value.Version).Take(sources.Count - MaxRemembered * 3 / 4).ToArray()) sources.Remove(id);
        }
    }

    /// <summary>
    /// <paramref name="shown"/> (a row the list holds) with this PC's settings (pin, mute, marked unread, archive here,
    /// snooze, names) applied again as they are now, for a change made on another device; null when this reader remembers
    /// nothing. Applied to the copy the source last gave when there is one, so a name taken off shows the provider's title
    /// again, and no service is asked: a provider read per changed row would be a request to Instagram each time, and one
    /// snapshot can change hundreds of rows at once. A row this reader never read (the saved list at a start, a page kept
    /// from a service that did not answer) has its settings applied to itself, and its names wait for its read: the
    /// provider's title is not on it any more.
    /// </summary>
    public async Task<ReappliedChat?> ReapplyAsync(ChatSummary shown, CancellationToken cancellationToken = default)
    {
        if (sources is null) return null;
        var profile = await NotificationIdentity.ProfileAsync(tokens, cancellationToken);
        (ChatSummary Chat, long Version) copy;
        lock (remembering)
            if (!string.Equals(profile, rememberedProfile, StringComparison.Ordinal) || !sources.TryGetValue(shown.Id, out copy)) copy = default;
        return copy.Chat is { } read
            ? new(await ApplyAsync(read, profile, cancellationToken), copy.Version, Names: true)
            : new(await ApplyAsync(shown, profile, cancellationToken, applied: true), 0, Names: false);
    }

    /// <summary>The copy <see cref="ReapplyAsync"/> would apply to now: its version, or 0 for none. A later read changes it.</summary>
    public long CopyVersion(string chatId)
    {
        if (sources is null) return 0;
        lock (remembering) return sources.TryGetValue(chatId, out var copy) ? copy.Version : 0;
    }

    /// <summary>The newest copy's version so far, for <see cref="KeepOnly"/>.</summary>
    public long LatestVersion { get { lock (remembering) return versions; } }

    /// <summary>Lets go of the copies of conversations not in <paramref name="ids"/> that were read at or before <paramref name="upTo"/>.</summary>
    public void KeepOnly(IReadOnlySet<string> ids, long upTo)
    {
        if (sources is null) return;
        lock (remembering)
            foreach (var (id, copy) in sources.ToArray())
                if (copy.Version <= upTo && !ids.Contains(id)) sources.Remove(id);
    }

    // applied: a row this reader's settings were already applied to (ReapplyAsync's shown, with no copy of the source's), whose
    // title is no longer the provider's and whose archive may be this PC's own. Only ReapplyAsync passes it, and only when
    // remembering; every read applies to the source's own copy as before.
    private async Task<ChatSummary> ApplyAsync(ChatSummary chat, string? profile, CancellationToken token, bool applied = false)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (profile is null)
        {
            var service = WithServiceMute((false, null), chat.ServiceMute, null, now);
            return chat with { IsPinned = false, IsMuted = service.Muted, IsMarkedUnread = false, SnoozedUntil = null, MutedUntil = service.Until };
        }
        var local = await preferences.GetAsync(profile, chat.Id, token);
        var snoozed = await SnoozedUntilAsync(profile, chat.Id, token);
        // A timed mute holds until its moment, decided now rather than when it was saved.
        var mute = MuteAt(local, now);
        // A merged row represents all its member conversations.
        foreach (var id in MergedConversations.IsMerged(chat.Id) ? Array.Empty<string>() : chat.MemberChatIds)
        {
            var member = await preferences.GetAsync(profile, id, token);
            local = local with { Pinned = local.Pinned || member.Pinned, MarkedUnread = local.MarkedUnread || member.MarkedUnread };
            // Muted while any conversation behind it is, for as long as the longest of them: until turned off beats a moment.
            if (MuteAt(member, now) is { Muted: true } other)
                mute = !mute.Muted ? other : (true, mute.Until is null || other.Until is null ? null : Max(mute.Until.Value, other.Until.Value));
            // The row is quiet for as long as any conversation behind it is.
            if (await SnoozedUntilAsync(profile, id, token) is { } memberSnooze && memberSnooze > (snoozed ?? DateTimeOffset.MinValue)) snoozed = memberSnooze;
        }
        var named = applied ? chat : await NameAsync(chat, profile, token);
        var merged = MergedConversations.IsMerged(chat.Id);
        // The service's own archive, before this PC's archive here. On an applied row of a service that cannot archive from
        // here that archive is this PC's alone: Instagram reports none of its own (InstagramModels.Map), and an applied row
        // (the saved list) keeps no separate record of one, so its base is not archived and lifting archive here on another
        // device shows it at once rather than at its next read.
        var serviceArchived = applied && !merged && instagram(chat.Id) ? false : chat.IsArchived;
        // The service's own mute, set on the phone, shows here too, unless it was unmuted on this PC: then it stays
        // unmuted here for as long as the service reports that same mute, and a newer one shows again.
        // Not for a member of a merge: the merged row is where mute is shown and offered.
        if (!merged && chat.MergedIntoChatId is null) mute = WithServiceMute(mute, chat.ServiceMute, local.ServiceMuteSetAside, now);
        return named with { IsPinned = merged ? chat.IsPinned : local.Pinned,
            IsMuted = merged ? chat.IsMuted : mute.Muted,
            MutedUntil = merged ? chat.MutedUntil : mute.Until,
            IsMarkedUnread = local.MarkedUnread || merged && chat.IsMarkedUnread,
            IsArchived = serviceArchived || !merged && instagram(chat.Id) && ArchivedHere(chat, local.ArchivedAt, profile),
            SnoozedUntil = snoozed ?? (merged ? chat.SnoozedUntil : null) };
    }
    private static (bool Muted, DateTimeOffset? Until) MuteAt(LocalChatPreferences local, DateTimeOffset now)
        => local.Muted && (local.MutedUntil is null || local.MutedUntil > now) ? (true, local.MutedUntil) : (false, null);
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    // Muted while either is, for as long as the longer: until turned off beats a moment. An ended service mute is none.
    private static (bool Muted, DateTimeOffset? Until) WithServiceMute((bool Muted, DateTimeOffset? Until) local, ServiceMute? service,
        long? setAside, DateTimeOffset now)
    {
        if (service is null || !service.HoldsAt(now) || setAside == service.Key) return local;
        if (!local.Muted) return (true, service.Until);
        return (true, local.Until is null || service.Until is null ? null : Max(local.Until.Value, service.Until.Value));
    }

    /// <summary>
    /// Archive on this PC, for a service that cannot archive from here: held until something new arrives, judged only
    /// from the preview the list already carries. Nothing is asked of the service to decide it.
    /// </summary>
    /// <remarks>
    /// An arrival lifts it for good: the record is removed once, so a reply sent afterwards (an outgoing preview newer
    /// than the arrival) cannot bring the archive back. Outgoing, hidden, deleted and system lines are not arrivals.
    /// </remarks>
    private bool ArchivedHere(ChatSummary chat, DateTimeOffset? archivedAt, string profile)
    {
        if (archivedAt is not { } at) return false;
        if (chat.Preview is not { IsOutgoing: false, IsHidden: false, IsDeleted: false } preview || preview.Type == "system" || preview.Timestamp <= at) return true;
        var key = (profile + "\n" + chat.Id, at);
        if (lifting.TryAdd(key, 0)) Volatile.Write(ref lifted, LiftAsync(profile, chat.Id, key));
        return false;
    }
    private async Task LiftAsync(string profile, string chatId, (string, DateTimeOffset) key)
    {
        try
        {
            // Off the reading thread's path: the row is already shown unarchived whether or not the file goes now.
            await Task.Yield();
            await preferences.SetAsync(profile, chatId, ChatSetting.Archived, false);
            Diagnostic?.Invoke("instagram local archive lifted reason=arrival");
        }
        catch (Exception ex)
        {
            // Left to the next read to try again; the row is shown unarchived meanwhile.
            lifting.TryRemove(key, out _);
            Diagnostic?.Invoke($"instagram local archive lift failed error={ex.GetType().Name}");
        }
    }

    // A moment already past is not a snooze any more: the reminder it owes is the snooze service's to deliver,
    // and until it does, the conversation notifies normally.
    private async Task<DateTimeOffset?> SnoozedUntilAsync(string profile, string chatId, CancellationToken token)
    {
        if (snoozes is null) return null;
        try { return await snoozes.GetAsync(profile, chatId, token) is { } until && until > DateTimeOffset.UtcNow ? until : null; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // An unreadable snooze must not keep the conversation list from loading.
    }

    /// <summary>
    /// A name this PC keeps wins over the provider's own, because the provider's is what the user was trying to
    /// replace: a conversation showing a bare number is exactly the one worth naming.
    /// </summary>
    /// <remarks>
    /// The name is applied to the participant too, not only the title, so a group message from someone named here
    /// carries that name above it rather than the number the title no longer shows.
    /// </remarks>
    private async Task<ChatSummary> NameAsync(ChatSummary chat, string profile, CancellationToken token)
    {
        if (names is null) return chat;
        ChatParticipant[]? people = null;
        for (var index = 0; index < chat.Participants.Count; index++)
        {
            var person = chat.Participants[index];
            if (person.IsSelf == true || string.IsNullOrWhiteSpace(person.Id)) continue;
            if (await names.GetAsync(profile, AvatarOverrideIdentity.ForParticipant(chat, person.Id), token) is not { } local) continue;
            // Copied only once something is actually renamed, so a conversation with no local names is untouched.
            people ??= [.. chat.Participants];
            people[index] = person with { DisplayName = local };
        }
        var title = await names.GetAsync(profile, AvatarOverrideIdentity.ForChat(chat), token);
        if (title is null && people is null) return chat;
        return chat with { Title = title ?? chat.Title, Participants = people ?? chat.Participants };
    }
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
        => source.GetMessagesAsync(chatId, olderThan, cancellationToken);

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
        => source is ILinkPreviewLoader previews ? previews.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : Task.CompletedTask;
}
