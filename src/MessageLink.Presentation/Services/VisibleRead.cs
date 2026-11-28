using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

/// <summary>What only the conversation's view can see, asked each time a read is considered.</summary>
public interface IVisibleReadScreen
{
    /// <summary>The view is closed.</summary>
    bool Closed { get; }
    /// <summary>A search result is being opened and placed.</summary>
    bool OpeningSearchResult { get; }
    /// <summary>The hosting window has focus. Only the focused window marks its messages read.</summary>
    bool Active { get; }
    /// <summary>A dialog anywhere, or something the host draws over the conversation (Settings, New chat, the media viewer).</summary>
    bool Obscured { get; }
    /// <summary>
    /// The newest message is on screen. Reaching the bottom of the history is what counts as having seen it. A call or
    /// voicemail conversation has no history to reach the bottom of: the card is the whole conversation and it is on screen,
    /// so requiring the message list's scroller there would leave a voicemail unread however long it was open.
    /// </summary>
    bool AtNewest { get; }
}

/// <summary>What a visible read reaches beyond the conversation and its list. Each is asked when it is needed, as the app's
/// own services are only there once it has started.</summary>
/// <param name="ServiceConnected">Whether a plain conversation's own service (a Voice conversation's own account) is connected.</param>
/// <param name="ScopeOf">The connection a conversation id belongs to (ProviderScopes.OfChat).</param>
/// <param name="GoogleScope">Google Messages' own scope, whose reads are shown before its service is asked.</param>
public sealed record VisibleReadPorts(
    Func<IReadStateWriter> Reads,
    Func<string?, bool> ServiceConnected,
    Func<string, string?> ScopeOf,
    string GoogleScope,
    Func<MergedConversations> Merges,
    Func<IChatSnoozes> Snoozes,
    Func<PrivateReading?> PrivateReading,
    Action<string> Log);

/// <summary>
/// When an open conversation tells its services it was read, and the list's pending and acknowledged bookkeeping around it,
/// for a plain conversation and a merged one. The view says what it alone sees (<see cref="IVisibleReadScreen"/>) and calls
/// <see cref="ScheduleAsync"/> on any viewing activity: a scroll ending, the window gaining focus, history changing or
/// settling. Steps as <see cref="ExplicitRead"/> takes them: <see cref="ChatListViewModel.ApplyPendingRead"/>, the marker,
/// then <see cref="ChatListViewModel.ApplyAcknowledgedRead"/> or <see cref="ChatListViewModel.CancelPendingRead"/>.
/// </summary>
/// <remarks>Runs on the interface thread: the list's read bookkeeping is interface state, and every await here resumes on
/// the context it started on.</remarks>
public sealed class VisibleRead(ConversationViewModel history, ChatListViewModel chats, IAccessTokenStore tokens, IVisibleReadScreen screen,
    VisibleReadPorts ports, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? current;
    private bool writing;
    // Set when viewing activity arrives while a read marker is being written; that write ends by checking again.
    private bool readAgain;
    private string? acknowledged;
    private long acknowledgedAt;
    private readonly HashSet<string> mergedAcknowledgements = [];

    /// <summary>How long a read waits after the conversation is in view before it is told. Settable for the offline checks.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Stops a read waiting or under way (the conversation is being left, or a search result opened).</summary>
    public void Cancel() => current?.Cancel();

    // The conversation left open when Messages was brought forward for something else (a share from Explorer, whose
    // picker opens over it): on screen, but nobody chose to read it. Its reads wait until the user clicks, types or
    // scrolls in this view; opening another conversation ends the hold, since it names only this one.
    private string? heldFor;

    /// <summary>Holds reads of the conversation open now until <see cref="Engaged"/>. False when none is open.</summary>
    public bool HoldUntilEngaged()
    {
        heldFor = history.ActiveChatId;
        if (heldFor is null) return false;
        current?.Cancel();
        return true;
    }

    /// <summary>The user clicked, typed or scrolled in the view. True when that ended a hold, and a read should be considered.</summary>
    public bool Engaged()
    {
        if (heldFor is null) return false;
        heldFor = null;
        return true;
    }

    private bool Held => heldFor is not null && heldFor == history.ActiveChatId;

    /// <summary>Whether the conversation on screen counts as read now.</summary>
    // A window of older history (a search result far back) is not the newest message, however far down it is read.
    public bool CanRead() => !screen.Closed && !screen.OpeningSearchResult && !Held && !history.IsInWindow && screen.Active &&
        Connected() && !history.IsBusy && !screen.Obscured && screen.AtNewest;

    // A merged conversation's members are each read against their own service below; a plain one against its own
    // service's live connection (ServiceConnected), never Google's alone.
    private bool Connected() => history.IsMerged || ports.ServiceConnected(history.ActiveChatId);

    // A conversation whose service connected after it was last loaded shows what it held before the connection: a read then
    // would name the newest message on screen, not the newest there is, and what a service does with an older message is not
    // known (Google takes a conversation and a message). So it waits for the reload the connection starts, whose end reads
    // again (HistorySettled), and at most ReloadWait, after which it reads as before (review, 2026-10-14).
    public static readonly TimeSpan ReloadWait = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, long> serviceConnectedAt = new(StringComparer.Ordinal);
    private long historySettledAt;

    /// <summary>A service connected. True when it is the open conversation's own, whose read now waits for the reload
    /// (or <see cref="ReloadWait"/>, after which the view considers a read again).</summary>
    public bool ServiceConnected(string scope)
    {
        serviceConnectedAt[scope] = clock.GetTimestamp();
        return history.ActiveChatId is { } id && ports.ScopeOf(id) == scope;
    }

    /// <summary>The history finished loading or updating.</summary>
    public void HistorySettled() => historySettledAt = clock.GetTimestamp();

    private bool ReloadAwaited(string chatId) =>
        ports.ScopeOf(chatId) is { } scope && serviceConnectedAt.TryGetValue(scope, out var at) && at > historySettledAt
        && clock.GetElapsedTime(at) < ReloadWait;

    /// <summary>Considers reading the open conversation. Completes once that read has ended or was not made.</summary>
    public async Task ScheduleAsync()
    {
        if (writing) { readAgain = true; return; }
        current?.Cancel();
        if (writing || !CanRead() || history.ActiveChatId is not { } chatId) return;
        if (history.IsMerged) { await ReadMergedAsync(chatId); return; }
        if (ReloadAwaited(chatId)) return;
        var message = history.Messages.LastOrDefault(m => !m.Message.Id.StartsWith("local:", StringComparison.Ordinal))?.Message;
        if (message is null) return;
        // Every loaded conversation, not only the rows the current filter or scope shows: one opened from search, a
        // notification, Back or the rail may have no visible row, and a private read then had nothing to record it here.
        var chat = chats.AllLoadedChats.FirstOrDefault(c => c.Id == chatId);
        var activityAtRead = chat?.LastActivity;
        // Only Google's read is shown before its service is asked, since Google keeps every read it is told. Another service's waits for its own yes below:
        // shown first, an Instagram request, a spam thread or a missing Hermes profile flipped the row read and back again (2026-11-18, code review).
        var shownFirst = ports.ScopeOf(chatId) == ports.GoogleScope;
        var localAttempt = chat is null || !shownFirst ? 0 : chats.ApplyPendingRead(chatId, activityAtRead);
        var confirmed = false;
        using var request = new CancellationTokenSource(); current = request;
        try
        {
            await Task.Delay(Settle, request.Token);
            var profile = await NotificationIdentity.ProfileAsync(tokens, request.Token);
            var key = profile + "\n" + chatId + "\n" + message.Id;
            if (profile is null || !CanRead() || history.ActiveChatId != chatId || ReloadAwaited(chatId)) return;
            // The newest message was already marked read here; newer list activity (such as a delayed chat update) is not
            // a reason to show the open conversation as unread again, nor to send the same marker twice.
            // Known only while the service agrees, once its grace is over: a read it took and then lost (unread again for the same
            // newest message) is told again, as the merged path does since 2026-10-22. Within the grace the list's copy can
            // still be the one from before the read.
            if (acknowledged == key && chat is { UnreadCount: > 0 } && chat.LastActivity == activityAtRead
                && clock.GetElapsedTime(acknowledgedAt) >= ChatListViewModel.ReadTrustGrace)
                acknowledged = null;
            if (acknowledged == key)
            {
                if (chat is not null && !shownFirst) localAttempt = chats.ApplyPendingRead(chatId, activityAtRead);
                if (chat is not null && localAttempt != 0) { chats.ApplyAcknowledgedRead(chatId, activityAtRead, localAttempt); confirmed = true; }
                return;
            }
            // The service's own rule decides, as the merged path asks it: Instagram answers a read it cannot say (helper gone, not
            // connected, a message request) as done without telling anyone, and the conversation was then shown read here and never
            // told again. Google's check reads its chat from the phone, so Google keeps the read it always made.
            if (!shownFirst)
            {
                bool allowed;
                try { allowed = await ports.Reads().CanMarkReadAsync(chatId, request.Token); }
                catch (Exception failure) when (failure is not OperationCanceledException) { allowed = false; }
                if (history.ActiveChatId != chatId) return;
                // Seeing it still undoes Mark unread and answers a snooze, as on the merged path, whatever the service says. The row itself shows read only
                // once the service took the read (below): drawn before, a read the service then failed showed read and unread again.
                if (chat is not null) chats.ApplySeen(chatId);
                if (!allowed) return;
            }
            writing = true;
            await ports.Reads().MarkReadAsync(chatId, message.Id, request.Token);
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) return;
            acknowledged = key; acknowledgedAt = clock.GetTimestamp();
            // Google's was shown pending (localAttempt); another service's is shown here for the first time, already taken.
            if (chat is not null) { chats.ApplyAcknowledgedRead(chatId, activityAtRead, localAttempt); confirmed = true; }
        }
        catch (OperationCanceledException) { }
        catch { /* Keep authoritative unread state; retry on the next viewing activity. */ }
        finally
        {
            if (!confirmed && localAttempt != 0) chats.CancelPendingRead(chatId, activityAtRead, localAttempt);
            var wasWriting = writing;
            writing = false;
            if (ReferenceEquals(current, request)) current = null;
            if (wasWriting && readAgain) { readAgain = false; _ = ScheduleAsync(); }
        }
    }

    private async Task ReadMergedAsync(string displayId)
    {
        var targets = history.MergedReadTargets(chats.AllLoadedChats);
        var unseen = targets.Count - history.DisplayedReadTargets().Count;
        var note = history.MergedReadNote;
        int marked = 0, known = 0, refused = 0, failed = 0;
        var stopped = "none";
        var told = new List<string>();
        var began = clock.GetTimestamp();
        long shownMs = -1;
        using var request = new CancellationTokenSource(); current = request;
        try
        {
            await Task.Delay(Settle, request.Token);
            var profile = await NotificationIdentity.ProfileAsync(tokens, request.Token);
            if (profile is null || history.ActiveChatId != displayId || !CanRead()) return;
            writing = true;
            var anyRead = false;
            // Every member that will be read shows read at once, before the first service is told. The services are told one
            // after another, and a row used to stay bold until its own turn: Voice listed last in a four-service thread was
            // shown read four seconds after opening (2026-10-08). One a service refuses is never shown read, so nothing
            // flickers; one that fails is shown unread again, as before.
            var pending = new List<(ChatMessage Message, DateTimeOffset? Boundary, long Attempt, bool Known)>(targets.Count);
            try
            {
                var unreadNow = chats.AllLoadedChats.Where(c => c.UnreadCount > 0).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var message in targets)
                {
                    var source = chats.AllLoadedChats.FirstOrDefault(c => c.Id == message.ChatId);
                    var boundary = source?.LastActivity <= message.Timestamp ? source?.LastActivity : null;
                    // Read privately, nothing else will ever record this read: the service is not told, so it never says read. Its
                    // newest activity can be newer than its newest shown message (a reaction, an edit, a hidden placeholder), and
                    // then the read is taken through that activity rather than lost.
                    if (boundary is null && source?.LastActivity is { } newer && ports.PrivateReading()?.IsOn(message.ChatId) == true) boundary = newer;
                    // A read the service took and then lost (Messenger after the server's nightly restart) shows unread again. Known
                    // only while the list agrees: one conversation stayed bold for two days because this run remembered telling Messenger once
                    // and never told it again (2026-10-22).
                    var isKnown = !unreadNow.Contains(message.ChatId) && mergedAcknowledgements.Contains(profile + "\n" + MessageIdentity.Key(message));
                    bool allowed;
                    // One unavailable account (Google's check reads its chat) cannot stop the others being read: it counts as failed and
                    // is never shown read, as when its own read failed (review, 2026-10-08).
                    try { allowed = isKnown || await ports.Reads().CanMarkReadAsync(message.ChatId, request.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        chats.CancelPendingRead(message.ChatId, boundary, chats.ApplyPendingRead(message.ChatId, boundary));
                        failed++; continue;
                    }
                    if (!allowed)
                    {
                        // Seeing it still undoes Mark unread and answers a snooze (ApplyPendingRead), as it always did; only the
                        // read itself is not shown.
                        chats.CancelPendingRead(message.ChatId, boundary, chats.ApplyPendingRead(message.ChatId, boundary));
                        refused++; continue;
                    }
                    pending.Add((message, boundary, 0L, isKnown));
                }
                // The members still unread are told first. Each is a round trip to its service (about 0.8 s through a server), told one after another,
                // and opening another conversation stops the rest: a Voice missed call listed after four other members stayed bold for someone who
                // moved on after two seconds, twice (2026-10-14, "stopped=left" after 2 of 5). The members already read follow; the order among each is kept.
                pending = [.. pending.OrderByDescending(entry => unreadNow.Contains(entry.Message.ChatId))];
                if (history.ActiveChatId != displayId || screen.Closed) { stopped = "left"; return; }
                for (var i = 0; i < pending.Count; i++)
                    pending[i] = pending[i] with { Attempt = chats.ApplyPendingRead(pending[i].Message.ChatId, pending[i].Boundary) };
                shownMs = (long)clock.GetElapsedTime(began).TotalMilliseconds;
                // Read once it was seen: the whole conversation was viewable just above, and leaving the window (the console's
                // shortcut, a second after opening Mom) only stopped the services still waiting their turn, so the one listed
                // first was read and the rest never were (2026-10-07). Only opening another conversation stops it now.
                for (var i = 0; i < pending.Count; i++)
                {
                    var (message, boundary, attempt, isKnown) = pending[i];
                    if (history.ActiveChatId != displayId || screen.Closed || request.IsCancellationRequested) { stopped = "left"; break; }
                    try
                    {
                        if (!isKnown)
                        {
                            await ports.Reads().MarkReadAsync(message.ChatId, message.Id, request.Token);
                            if (profile != await NotificationIdentity.ProfileAsync(tokens, request.Token)) { stopped = "profile"; return; }
                            mergedAcknowledgements.Add(profile + "\n" + MessageIdentity.Key(message));
                            if (mergedAcknowledgements.Count > 2000) mergedAcknowledgements.Remove(mergedAcknowledgements.First());
                            marked++; told.Add(message.ChatId);
                        }
                        else known++;
                        chats.ApplyAcknowledgedRead(message.ChatId, boundary, attempt); anyRead = true;
                        pending[i] = pending[i] with { Attempt = 0 };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failed++; /* One unavailable account cannot prevent other displayed messages being read. */ }
                }
            }
            finally
            {
                // Whatever was shown read and not confirmed (a failure, leaving, a sign-out) is shown as it was.
                foreach (var (message, boundary, attempt, _) in pending)
                    if (attempt != 0) chats.CancelPendingRead(message.ChatId, boundary, attempt);
            }
            if (anyRead && ports.Merges().Find(displayId) is { } group && (group.MarkedUnread || group.SnoozedUntil is not null))
            {
                await ports.Merges().SetAsync(displayId, g => g with { MarkedUnread = false, SnoozedUntil = null });
                await ports.Snoozes().SetAsync(profile, displayId, null, request.Token);
            }
        }
        catch (OperationCanceledException) { if (stopped == "none") stopped = "cancelled"; }
        catch (Exception) { stopped = "error"; }
        finally
        {
            var wasWriting = writing; writing = false;
            // What one merged read did, by count: a member left unread is then one of these numbers, not a guess.
            if (wasWriting)
                ports.Log($"merged read targets={targets.Count} unseen={unseen} {note} marked={marked} known={known} refused={refused} failed={failed} stopped={stopped} " +
                    $"shown_ms={shownMs} ms={(long)clock.GetElapsedTime(began).TotalMilliseconds}");
            if (ReferenceEquals(current, request)) current = null;
            if (wasWriting && readAgain) { readAgain = false; _ = ScheduleAsync(); }
            // Not into another profile's list. After the read has let go, so the next one never waits on it.
            if (told.Count > 0 && stopped != "profile") _ = ReadBackUnlistedAsync(told);
        }
    }

    // A member the list does not hold is shown from the merge's copy of it, which a read never reaches, so its service's own
    // copy is read back into the list once its service has been told. Not Instagram's: reading one of its conversations is a
    // request to Instagram, which RemoteContactSearch's rules keep scarce.
    private async Task ReadBackUnlistedAsync(IReadOnlyList<string> told)
    {
        var listed = chats.AllLoadedChats.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var unlisted = told.Where(id => !listed.Contains(id) && !id.StartsWith("instagram:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (unlisted.Length == 0) return;
        try
        {
            var (read, failed) = await chats.ReadBackAsync(unlisted);
            ports.Log($"merged read back asked={unlisted.Length} read={read} failed={failed}");
        }
        catch (Exception) { ports.Log($"merged read back asked={unlisted.Length} outcome=error"); }
    }
}
