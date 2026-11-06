using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Shown: raised now. AlreadyShown: raised before (its identity was already claimed). Off: notifications are off, so it
// cannot be. NotYet: nothing is signed in to say whose it is; asked again later.
public enum ReminderOutcome { Shown, AlreadyShown, Off, NotYet }

public sealed class NotificationPreferences
{
    public bool Enabled { get; set; } = true;
    public bool RequireUnreadSnapshot { get; set; } = true;
    public bool WindowFocused { get; set; }
    public string? ActiveChatId { get; set; }
    // Off until explicitly enabled. Repeats run only for notifications raised in this app session.
    public TimeSpan? RepeatInterval { get; set; }
    // Null while someone is at the PC; otherwise why notifications go silently to Notification Center.
    public Func<string?> AwayReason { get; set; } = static () => null;
    // What Windows reports that may hold banners back (full-screen apps, presentation mode); diagnostics only.
    public Func<string?> WindowsBannerState { get; set; } = static () => null;
    // Services turned off, each service's sound and how much a notification shows (Settings → Notification options).
    public NotificationOptions Options { get; set; } = NotificationOptions.Default;
    // When Pause ends, or null when not paused (NotificationPauseFile.Attach). A reminder Windows holds for later is built
    // silent when the pause will still be on at its moment; being away cannot be known ahead, so it is not.
    public Func<DateTimeOffset?> PausedUntil { get; set; } = static () => null;
}

public sealed class NotificationObserver(IChatReader chats, IMessageSender messages, IReadStateWriter reads,
    IAccessTokenStore tokens, INotificationStore store, INotificationPresenter presenter, NotificationPreferences preferences, MergedConversations? merges = null,
    INotificationAccountScope? accountScopes = null, NotificationPhotos? photos = null, ReadHereMarks? readHere = null)
{
    public event EventHandler<NotificationContent>? Published;
    private DateTimeOffset connectedAt = DateTimeOffset.MaxValue;
    // When the client's link to its host was lost and not yet seen again (ClientLink); null while it is up.
    private DateTimeOffset? lostLinkAt;

    /// <summary>
    /// The longest a client can have been cut off from its host and still have what the host replays on reconnecting count as news. Longer than that the replay is
    /// a backlog (a laptop that slept), and moving the cutoff to the reconnect keeps it from toasting everything that arrived meanwhile in one burst.
    /// </summary>
    public TimeSpan ReplayWindow { get; init; } = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> connectedScopes = new(StringComparer.Ordinal);
    private readonly object delayLock = new();
    private readonly Queue<(double Arrival, double App)> delays = new();
    public string DelaySummary { get { lock (delayLock) return NotificationDelayText.Summary([.. delays]); } }
    // How recent notifications were delivered (banner, silent and why), newest first; times and reasons only.
    private readonly Queue<(DateTimeOffset At, string Outcome)> deliveries = new();
    public string DeliverySummary
    {
        get
        {
            lock (delayLock)
                return deliveries.Count == 0 ? "No notifications delivered yet this session."
                    : "Recent notifications: " + string.Join(" · ", deliveries.Reverse().Select(d => $"{d.At.ToLocalTime():t} {d.Outcome}"));
        }
    }
    private void RecordDelivery(string outcome)
    {
        lock (delayLock) { deliveries.Enqueue((DateTimeOffset.UtcNow, outcome)); while (deliveries.Count > 8) deliveries.Dequeue(); }
    }
    /// <summary>The record ids of the notifications Windows still shows for this app; null when unknown.</summary>
    public Func<Task<IReadOnlyCollection<string>?>>? LiveIds { get; set; }

    // A conversation that left the list takes its notifications with it: a Reply on one is refused, and it opens nothing (audit, 2026-11-01). A reminder stays, as the person
    // asked for it. From the records, so a notification from before a restart goes as well.
    private async Task WithdrawAsync(RealtimeEvent change, CancellationToken token)
    {
        var gone = (change.Ids ?? []).Append(change.ChatId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (gone.Count == 0 || LiveIds is null || await LiveIds() is not { } live) return;
        foreach (var id in live)
        {
            var record = await store.UpdateAsync(id, prior => (prior, prior), token);
            if (record is { IsTest: false, IsReminder: false } && gone.Contains(record.ChatId)) await presenter.RemoveAsync(id);
        }
    }

    /// <param name="unarchiving">The host is returning this conversation to the inbox for this arrival (stage 6 slice 9, <see cref="UnarchiveOutcome.LeftToHost"/>), and
    /// its answer comes after this decision: the arrival is judged as not archived. Should the host's unarchive fail, it was still shown, the safer mistake.</param>
    public async Task ObserveAsync(RealtimeEvent change, CancellationToken token, bool unarchiving = false)
    {
        // The client's own link to its host dropped (RealtimeFeedProxy), not a provider's connection to its service. The host kept running, and a feed that resumes
        // replays what arrived meanwhile, so the cutoff stays where the providers' last connect put it: what the replay brings is news, not backfill. A feed that
        // could not resume says so with a resync, which moves the cutoff as it always did, and so does an outage longer than ReplayWindow (below).
        if (change.Kind == RealtimeKind.Disconnected && change.Scope is null && change.ClientLink == true)
        {
            lostLinkAt ??= DateTimeOffset.UtcNow;
            return;
        }
        if (lostLinkAt is { } lost)
        {
            lostLinkAt = null;
            if (DateTimeOffset.UtcNow - lost > ReplayWindow) { connectedScopes.Clear(); connectedAt = DateTimeOffset.UtcNow; }
        }
        if (change.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.ResyncRequired or RealtimeKind.AuthenticationRequired)
        {
            if (change.Scope is { } scope) connectedScopes[scope] = DateTimeOffset.UtcNow;
            else { connectedScopes.Clear(); connectedAt = DateTimeOffset.UtcNow; }
            return;
        }
        if (change.Kind == RealtimeKind.ChatDeleted) { await WithdrawAsync(change, token); return; }
        if (!preferences.Enabled || change.Kind != RealtimeKind.MessageChanged || change.ChatId is null || change.IsNewMessage == false) return;
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return;
        var chat = await chats.GetChatAsync(change.ChatId, token);
        if (readHere is not null) await readHere.EnsureLoadedAsync();
        if (unarchiving) chat = chat with { IsArchived = false };
        // Judged by the service that received the message, before a merged conversation's settings are laid over it.
        var serviceKey = NotificationOptions.ServiceKey(chat);
        // Recorded only for a change the provider marks as a new message: receipts and status changes from a service
        // that is off would otherwise crowd every outcome worth reading out of the short delivery summary.
        if (!preferences.Options.IsOn(serviceKey))
        {
            if (change.IsNewMessage == true && !change.ReceiptOnly) RecordDelivery("not shown (service off)");
            return;
        }
        var sourceScope = accountScopes is null ? null : await accountScopes.ForSourceAsync(chat, token);
        if (accountScopes is not null && sourceScope is null) return;
        if (merges is not null)
        {
            await merges.EnsureLoadedAsync();
            if (merges.Find(merges.DisplayId(chat.Id)) is { } group)
                // A timed mute that has run out notifies again, as a single conversation's does (see LocalPreferenceChatReader).
                chat = chat with { IsMuted = merges.IsMuted(group.Id, DateTimeOffset.UtcNow), MutedUntil = merges.MutedUntil(group.Id), IsArchived = group.Archived, Title = group.Title, SnoozedUntil = group.SnoozedUntil };
        }
        if (chat.IsMuted || chat.IsArchived || chat.IsSpam || (preferences.RequireUnreadSnapshot && chat.UnreadCount == 0) || chat.MemberChatIds.Count != 0) return;
        // Snoozed: the conversation stays quiet until its moment, when one reminder covers whatever is unread then.
        if (chat.SnoozedUntil is { } snoozed && snoozed > DateTimeOffset.UtcNow)
        { RecordDelivery($"not shown (snoozed until {snoozed.ToLocalTime():t})"); return; }
        // Nothing is posted while the app window is focused: the conversation list already shows new messages, and
        // Windows holds banners for the focused app and releases them all at once when the window loses focus.
        if (preferences.WindowFocused) return;
        // A reconnect suppresses backfill only on that connection. Voice account scopes override the provider scope.
        var since = connectedScopes.Where(pair => chat.Id.StartsWith(pair.Key, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault(connectedAt);
        // A notification Windows refuses does not cost the rest of its batch theirs: each is shown, and the first refusal is
        // thrown once the batch is done, for the feed to log.
        var refusals = new List<Exception>();
        foreach (var messageId in change.Ids ?? [])
        {
            var message = await messages.ResolveMessageAsync(chat.Id, messageId, token);
            if (message.ChatId != chat.Id || message.Id != messageId || message.IsOutgoing || message.IsHidden || message.IsDeleted || message.Type == "system" || message.Timestamp < since ||
                message.Timestamp > DateTimeOffset.UtcNow.AddMinutes(1) || message.EditedAt is not null || AlreadyRead(chat, message)) continue;
            var id = NotificationIdentity.Hash(profile + "\n" + sourceScope + "\n" + chat.Id + "\n" + message.Id);
            if (!await PublishAsync(chat, message, profile, id, null, sourceScope, serviceKey, token, refusals)) continue;
            if (change.ReceivedAt is { } received)
                lock (delayLock)
                {
                    delays.Enqueue((Math.Max(0, (received - message.Timestamp).TotalSeconds), Math.Max(0, (DateTimeOffset.UtcNow - received).TotalSeconds)));
                    while (delays.Count > 20) delays.Dequeue();
                }
        }
        if (refusals.Count > 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refusals[0]);
    }

    /// <summary>The moment a snooze ends: one notification for whatever is unread then.</summary>
    /// <remarks>
    /// Not for the message that was newest when the conversation was snoozed. A conversation read in the meantime,
    /// here or on the phone, has nothing left to say and stays quiet. A muted conversation is reminded even so: the
    /// snooze was asked for by name, while mute is only a standing preference.
    /// </remarks>
    public async Task<bool> RemindAsync(ChatSummary chat, DateTimeOffset until, CancellationToken token = default)
    {
        if (!preferences.Enabled || chat.UnreadCount == 0 || chat.IsArchived || chat.IsSpam) return false;
        if (await LatestIncomingAsync(chat, token) is not { } message) return false;
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return false;
        var source = chat.Id == message.ChatId ? chat : merges?.Members(chat.Id).FirstOrDefault(member => member.Id == message.ChatId);
        // The service the reminder's message came from; a merged conversation's own row names no single service.
        var serviceKey = NotificationOptions.ServiceKey(source ?? chat);
        if (!preferences.Options.IsOn(serviceKey)) { RecordDelivery("reminder not shown (service off)"); return false; }
        var sourceScope = accountScopes is null || source is null ? null : await accountScopes.ForSourceAsync(source, token);
        if (accountScopes is not null && sourceScope is null) return false;
        // Its own identity, so a reminder is never mistaken for the notification already shown for that message,
        // and a conversation snoozed a second time is reminded a second time.
        var id = NotificationIdentity.Hash(profile + "\n" + sourceScope + "\n" + chat.Id + "\n" + message.Id + "\nreminder\n" + until.ToUnixTimeSeconds());
        var attribution = chat.UnreadCount > 1 ? $"Snoozed reminder · {chat.UnreadCount} unread" : "Snoozed reminder";
        return await PublishAsync(chat, message, profile, id, attribution, sourceScope, serviceKey, token);
    }

    /// <summary>"Remind me about this message": one notification for a saved message whose time has come.</summary>
    /// <remarks>
    /// Built from the saved copy alone, so raising it asks no service anything and marks nothing read. It was asked for by
    /// name, so it is raised while the window has focus and when that service's own notifications are off; what it shows
    /// follows "Show in notifications", and Pause (or being away) sends it to Notification Center without a banner. Its
    /// identity is claimed before it is shown, so a reminder is never raised twice, even across a crash.
    /// </remarks>
    public async Task<ReminderOutcome> RemindAboutMessageAsync(ChatSummary chat, ChatMessage message, string sourceScope, DateTimeOffset remindAt,
        CancellationToken token = default, DateTimeOffset? now = null)
    {
        if (!preferences.Enabled) { RecordDelivery("message reminder not shown (notifications off)"); return ReminderOutcome.Off; }
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return ReminderOutcome.NotYet;
        var id = MessageReminderId(profile, sourceScope, message.ChatId, message.Id, remindAt);
        var groupId = NotificationIdentity.Hash(profile + "\n" + (merges?.DisplayId(message.ChatId) ?? message.ChatId));
        var record = new NotificationRecord(id, profile, message.ChatId, message.Id, DateTimeOffset.UtcNow, false, false,
            GroupId: groupId, SourceScope: sourceScope, IsReminder: true);
        // Unclaimed, or taken back from Windows (ReminderSchedule): a record Windows still holds is Windows' to show.
        if (!await store.UpdateAsync(id, prior => prior is null || prior is { ScheduledFor: not null, TakenBack: true } ? (record, true) : (prior, false), token))
            return ReminderOutcome.AlreadyShown;
        var options = preferences.Options;
        var serviceKey = NotificationOptions.ServiceKey(chat);
        var text = string.IsNullOrWhiteSpace(message.Text) ? message.Attachments.Count > 0 ? "Attachment" : "Message" : MessageText.PlainText(message.Text);
        var (title, preview) = options.Detail switch
        {
            NotificationDetail.SenderOnly => ("Reminder · " + chat.Title, "A message you saved"),
            NotificationDetail.None => ("Message Link", "Reminder"),
            _ => ("Reminder · " + chat.Title, text),
        };
        var away = preferences.AwayReason();
        var late = (now ?? DateTimeOffset.UtcNow) - remindAt > TimeSpan.FromMinutes(2);
        var photo = await PhotoAsync(id, chat, null, token);
        var content = new NotificationContent(record, Clip(title, 100), Clip(preview, 240), chat.Network, Silent: away is not null,
            Attribution: late ? $"Reminder for {remindAt.ToLocalTime():t}" : "Reminder", Sound: options.SoundFor(serviceKey), Photo: photo);
        // Windows refusing it (notifications unavailable in this process) is the same as notifications being off: the
        // identity is claimed, and raising it again later would be a second reminder rather than a late first one.
        try { await presenter.ShowMessageAsync(content); }
        catch (Exception)
        {
            photos?.Release(id);
            RecordDelivery("message reminder not shown (Windows notifications unavailable)");
            return ReminderOutcome.Off;
        }
        RecordDelivery(away is not null ? $"message reminder silent ({away})" : "message reminder banner");
        return ReminderOutcome.Shown;
    }

    /// <summary>
    /// A scheduled message held as not sent (ScheduledHeldNotices): asked for by being scheduled, so it is raised while the window has focus and when that service's own notifications are off.
    /// Once for each, across a restart. True when nothing is left to do for it, shown or not; false to be asked again.
    /// </summary>
    public async Task<bool> ScheduledHeldAsync(ChatSummary chat, string scheduledId, CancellationToken token = default)
    {
        if (!preferences.Enabled) { RecordDelivery("held scheduled message not shown (notifications off)"); return true; }
        if (await NotificationIdentity.ProfileAsync(tokens, token) is not { } profile) return false;
        var sourceScope = accountScopes is null ? null : await accountScopes.ForSourceAsync(chat, token);
        // Not known yet, or another account's now: asked again at the next change. Shown with none, a click would be refused as out of date.
        if (accountScopes is not null && sourceScope is null) return false;
        var id = NotificationIdentity.Hash(profile + "\n" + scheduledId + "\nscheduled-held");
        var record = new NotificationRecord(id, profile, chat.Id, scheduledId, DateTimeOffset.UtcNow, false, false,
            GroupId: NotificationIdentity.Hash(profile + "\n" + (merges?.DisplayId(chat.Id) ?? chat.Id)), SourceScope: sourceScope, IsReminder: true);
        if (!await store.UpdateAsync(id, prior => prior is null ? (record, true) : (prior, false), token)) return true;
        var options = preferences.Options;
        var title = options.Detail == NotificationDetail.None ? "Message Link" : chat.Title;
        const string Words = "Your scheduled message wasn't sent. Open it to send or drop it.";
        var content = new NotificationContent(record, Clip(title, 100), Words, chat.Network, Silent: preferences.AwayReason() is not null,
            Attribution: "Send later", Sound: options.SoundFor(NotificationOptions.ServiceKey(chat)));
        try { await presenter.ShowMessageAsync(content); }
        catch (Exception) { RecordDelivery("held scheduled message not shown (Windows notifications unavailable)"); return true; }
        RecordDelivery("held scheduled message shown");
        return true;
    }

    // The identity of a message reminder, the same whoever shows it: the app, or Windows while Messages is closed.
    public static string MessageReminderId(string profile, string sourceScope, string chatId, string messageId, DateTimeOffset remindAt) =>
        NotificationIdentity.Hash(profile + "\n" + sourceScope + "\n" + chatId + "\n" + messageId + "\nmessage-reminder\n" + remindAt.ToUnixTimeSeconds());

    public async Task<string?> MessageReminderIdAsync(string sourceScope, string chatId, string messageId, DateTimeOffset remindAt, CancellationToken token = default) =>
        await NotificationIdentity.ProfileAsync(tokens, token) is { } profile ? MessageReminderId(profile, sourceScope, chatId, messageId, remindAt) : null;

    // The identity of the reminder Windows holds for a snoozed conversation (the conversation as the list shows it, merged
    // or not). The app's own snooze reminder keeps its identity by message (RemindAsync); ReminderSchedule decides between them.
    public static string SnoozeReminderId(string profile, string chatId, DateTimeOffset until) =>
        NotificationIdentity.Hash(profile + "\n" + chatId + "\nsnooze-reminder\n" + until.ToUnixTimeSeconds());

    /// <summary>A message reminder as Windows holds it for its moment, to show if Messages is closed then.</summary>
    /// <remarks>
    /// Windows keeps it in its own store until then, so it never carries the message's words: the conversation's name and
    /// "A message you saved" at most, only "Reminder" with No details. Silent when Pause will still be on at its moment. No
    /// sender's photo: a file kept for days for a notification not yet shown is not what that option promises. Null when
    /// notifications are off or nothing is signed in. Nothing is claimed or shown here.
    /// </remarks>
    public async Task<NotificationContent?> ScheduledMessageReminderAsync(ChatSummary chat, ChatMessage message, string sourceScope, DateTimeOffset remindAt,
        CancellationToken token = default)
    {
        if (!preferences.Enabled) return null;
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return null;
        var id = MessageReminderId(profile, sourceScope, message.ChatId, message.Id, remindAt);
        var groupId = NotificationIdentity.Hash(profile + "\n" + (merges?.DisplayId(message.ChatId) ?? message.ChatId));
        var record = new NotificationRecord(id, profile, message.ChatId, message.Id, remindAt, false, false,
            GroupId: groupId, SourceScope: sourceScope, IsReminder: true, ScheduledFor: remindAt);
        var options = preferences.Options;
        var (title, preview) = options.Detail == NotificationDetail.None ? ("Message Link", "Reminder") : ("Reminder · " + chat.Title, "A message you saved");
        return new NotificationContent(record, Clip(title, 100), preview, chat.Network, Silent: PausedAt(remindAt), Attribution: "Reminder",
            Sound: options.SoundFor(NotificationOptions.ServiceKey(chat)));
    }

    /// <summary>A snoozed conversation's reminder as Windows holds it for its moment, to show if Messages is closed then.</summary>
    /// <remarks>
    /// Shown by Windows only while Messages is closed, when nothing can say whether the conversation was read meanwhile, so
    /// it comes whatever is unread: the conversation's name (or only "Messages") and "Snoozed reminder", never a message's
    /// words. It opens the conversation and offers "Remind me in 1 hour" and Mute, as the app's own does. Null when it could
    /// not be raised by the app either: notifications or that service off, archived or spam. Unknown when the account the
    /// conversation belongs to is not known right now (a service still connecting): neither shown nor refused.
    /// </remarks>
    public async Task<(NotificationContent? Content, bool Unknown)> ScheduledSnoozeReminderAsync(ChatSummary chat, DateTimeOffset until, CancellationToken token = default)
    {
        if (!preferences.Enabled || chat.IsArchived || chat.IsSpam) return (null, false);
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null) return (null, false);
        // What opening it goes to: the newest message the list has, or else the conversation (for a merged one, a member).
        var message = chat.Preview is { IsOutgoing: false, IsHidden: false, IsDeleted: false } preview && preview.Type != "system" ? preview : null;
        var source = message is not null && chat.Id == message.ChatId ? chat
            : message is not null ? merges?.Members(chat.Id).FirstOrDefault(member => member.Id == message.ChatId)
            : chat.MemberChatIds.Count == 0 ? chat : merges?.Members(chat.Id).FirstOrDefault();
        if (source is null) return (null, true);
        var serviceKey = NotificationOptions.ServiceKey(source);
        if (!preferences.Options.IsOn(serviceKey)) return (null, false);
        var sourceScope = accountScopes is null ? null : await accountScopes.ForSourceAsync(source, token);
        if (accountScopes is not null && sourceScope is null) return (null, true);
        var id = SnoozeReminderId(profile, chat.Id, until);
        var groupId = NotificationIdentity.Hash(profile + "\n" + (merges?.DisplayId(source.Id) ?? source.Id));
        var record = new NotificationRecord(id, profile, source.Id, message?.Id ?? "", until, false, false,
            GroupId: groupId, SourceScope: sourceScope, ScheduledFor: until);
        var options = preferences.Options;
        var title = options.Detail == NotificationDetail.None ? "Message Link" : chat.Title;
        return (new NotificationContent(record, Clip(title, 100), "Snoozed reminder", chat.Network, Silent: PausedAt(until), Attribution: "Snoozed reminder",
            Sound: options.SoundFor(serviceKey)), false);
    }

    /// <summary>
    /// The same reminder, for a snooze made by a notification's "Remind me in 1 hour" where that notification is all there
    /// is to go on: the process Windows starts to handle the click while Messages is closed, which has no conversation list.
    /// It names the conversation when <paramref name="known"/> (this PC's copy) is given, and otherwise says only
    /// "Messages"; opening it goes to the conversation the notification was about. Null when it could not be raised: another
    /// connection's record, notifications or that service off, or the conversation archived or spam.
    /// </summary>
    public async Task<NotificationContent?> ScheduledSnoozeReminderAsync(NotificationRecord clicked, string target, DateTimeOffset until, ChatSummary? known,
        CancellationToken token = default)
    {
        if (!preferences.Enabled || known is { IsArchived: true } or { IsSpam: true }) return null;
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null || profile != clicked.Profile) return null;
        var network = ServiceScopes.Name(clicked.ChatId);
        var serviceKey = network == "Google Voice" && ServiceScopes.Scope(clicked.ChatId) is { } scope
            ? NotificationOptions.VoiceKey(scope.TrimEnd(':')) : network;
        if (!preferences.Options.IsOn(serviceKey)) return null;
        var record = new NotificationRecord(SnoozeReminderId(profile, target, until), profile, clicked.ChatId, "", until, false, false,
            GroupId: NotificationIdentity.Hash(profile + "\n" + target), SourceScope: clicked.SourceScope, ScheduledFor: until);
        var options = preferences.Options;
        var title = options.Detail == NotificationDetail.None || known is null ? "Message Link" : known.Title;
        return new NotificationContent(record, Clip(title, 100), "Snoozed reminder", known?.Network ?? network, Silent: PausedAt(until),
            Attribution: "Snoozed reminder", Sound: options.SoundFor(serviceKey));
    }

    private bool PausedAt(DateTimeOffset moment) => preferences.PausedUntil() is { } until && until > moment;

    // The sender's picture for a notification about to be shown, as a file, when "Show the sender's photo" is on. Never for
    // No details or spam. A merged conversation shows its own picture, as its row does; in a group, the person who wrote.
    private async Task<string?> PhotoAsync(string id, ChatSummary chat, string? senderId, CancellationToken token)
    {
        var options = preferences.Options;
        if (photos is null || !options.SenderPhoto || options.Detail == NotificationDetail.None || chat.IsSpam) return null;
        var shown = chat;
        if (merges is not null && merges.DisplayId(chat.Id) is var display && display != chat.Id && merges.Find(display) is not null)
        {
            try { shown = merges.Summary(display); senderId = null; }
            catch (InvalidOperationException) { /* Unmerged meanwhile: the conversation's own picture. */ }
        }
        try { return await photos.PrepareAsync(id, shown, senderId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    // What a reminder is about. Most conversation summaries already carry their latest message, but not every
    // provider fills one in, and a reminder that quietly never came would look exactly like a snooze that worked.
    private async Task<ChatMessage?> LatestIncomingAsync(ChatSummary chat, CancellationToken token)
    {
        if (chat.Preview is { } preview && Eligible(preview)) return preview;
        try
        {
            var page = await chats.GetMessagesAsync(chat.Id, cancellationToken: token);
            return page.Items.Where(Eligible).OrderByDescending(message => message.Timestamp).FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        static bool Eligible(ChatMessage message) => !message.IsOutgoing && !message.IsHidden && !message.IsDeleted && message.Type != "system";
    }

    // Shared by a message as it arrives and by a snooze reminder: claim the identity once, learn what the
    // conversation allows, and decide between a banner and a silent Notification Center entry.
    /// <param name="refusals">Given, Windows refusing to show it is added here and it counts as not shown, instead of being thrown.</param>
    private async Task<bool> PublishAsync(ChatSummary chat, ChatMessage message, string profile, string id, string? attribution, string? sourceScope, string serviceKey, CancellationToken token,
        List<Exception>? refusals = null)
    {
        if (await store.UpdateAsync(id, prior => (prior, prior is not null), token)) return false;
        var reply = false; var read = false;
        try
        {
            if (messages is INotificationActionHints hints) (reply, read) = hints.GetNotificationActions(message.ChatId);
            else { reply = (await messages.GetSendPermissionAsync(message.ChatId, token)).Allowed; read = await reads.CanMarkReadAsync(message.ChatId, token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* Preview remains useful if action capability discovery fails. */ }
        if (accountScopes is not null && (sourceScope is null || sourceScope != await accountScopes.ForChatAsync(message.ChatId, token))) return false;
        if (profile != await NotificationIdentity.ProfileAsync(tokens, token)) return false;
        // A shared private read may arrive while message/action lookups await. Re-check without writing a read marker or making another provider read.
        if (attribution is null && AlreadyRead(chat, message)) return false;
        var groupId = NotificationIdentity.Hash(profile + "\n" + (merges?.DisplayId(message.ChatId) ?? message.ChatId));
        var record = new NotificationRecord(id, profile, message.ChatId, message.Id, DateTimeOffset.UtcNow, reply, read, GroupId: groupId, SourceScope: sourceScope);
        if (!await store.UpdateAsync(id, prior => prior is null ? (record, true) : (prior, false), token)) return false;
        var title = chat.Type == "group" ? $"{message.SenderName ?? "New message"} · {chat.Title}" : chat.Title;
        var preview = string.IsNullOrWhiteSpace(message.Text) ? message.Attachments.Count > 0 ? "Attachment" : "New message" : MessageText.PlainText(message.Text);
        // A reminder says so on the notification itself, so it is never read as a message that just arrived.
        var kind = attribution is null ? "" : "reminder ";
        // Focus can return while the message and actions resolve.
        if (preferences.WindowFocused) { RecordDelivery($"{kind}not shown (app window focused)"); return false; }
        // Away from the PC, the notification lands in Notification Center without a banner, so nothing floods in on return.
        var away = preferences.AwayReason();
        // What the notification shows, by Notification options. A code is offered only where the message itself is: a
        // notification that hides the text must not show the one part of it that matters most.
        var options = preferences.Options;
        (title, preview) = NotificationOptions.Shape(title, preview, options.Detail);
        var code = options.Detail == NotificationDetail.SenderAndMessage ? OneTimeCode.Find(message.Text) : null;
        var photo = await PhotoAsync(id, chat, chat.Type == "group" ? message.SenderId : null, token);
        if (attribution is null && (AlreadyRead(chat, message) || await ReadSinceAsync(message, token))) { photos?.Release(id); return false; }
        var content = new NotificationContent(record, Clip(title, 100), Clip(preview, 240), chat.Network, Silent: away is not null, Attribution: attribution,
            Sound: options.SoundFor(serviceKey), Code: code, Photo: photo);
        Remember(record.Id, serviceKey);
        try { await presenter.ShowMessageAsync(content); }
        catch (Exception failure) when (refusals is not null) { photos?.Release(id); refusals.Add(failure); return false; }
        catch (Exception) { photos?.Release(id); throw; }
        Published?.Invoke(this, content);
        RecordDelivery(away is not null ? $"{kind}silent ({away})"
            : preferences.WindowsBannerState() is { } windows ? $"{kind}banner requested, but {windows}" : $"{kind}banner");
        return true;
    }

    private bool AlreadyRead(ChatSummary chat, ChatMessage message) =>
        chat.ReadUpTo is { } serviceRead && message.Timestamp <= serviceRead
        || readHere?.Through(message.ChatId) is { } sharedRead && message.Timestamp <= sharedRead;

    // The summary `chat` was read before the message and its actions resolved; the message may have been read on another device since, which moves the
    // service's watermark. Code review, round 3 (2026-10-13): the stale summary still said unread and the notification was shown. Read the summary again,
    // the same local/host lookup ObserveAsync starts with, never a provider read or a read marker. A lookup that fails shows it, the safer mistake.
    private async Task<bool> ReadSinceAsync(ChatMessage message, CancellationToken token)
    {
        try { return (await chats.GetChatAsync(message.ChatId, token)).ReadUpTo is { } serviceRead && message.Timestamp <= serviceRead; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    // The service each notification of this session came from, so its unread repeats follow that service's choices. As
    // bounded as the repeats themselves, which also live only for this session.
    private readonly Dictionary<string, string> serviceOf = new(StringComparer.Ordinal);
    private readonly Queue<string> serviceOrder = new();
    private void Remember(string id, string serviceKey)
    {
        lock (delayLock)
        {
            if (serviceOf.TryAdd(id, serviceKey)) serviceOrder.Enqueue(id);
            while (serviceOrder.Count > 1000) serviceOf.Remove(serviceOrder.Dequeue());
        }
    }

    /// <summary>
    /// Whether the service a notification raised here came from has been turned off since, so its unread repeats end.
    /// Says so in the delivery summary; the repeat service then forgets the notification, so that is said once.
    /// </summary>
    public bool RepeatsStopped(string recordId)
    {
        string? serviceKey;
        lock (delayLock) serviceOf.TryGetValue(recordId, out serviceKey);
        if (serviceKey is null || preferences.Options.IsOn(serviceKey)) return false;
        RecordDelivery("repeat not shown (service off)");
        return true;
    }

    /// <summary>An unread repeat of a notification raised here, as Notification options now say it should look.</summary>
    /// <remarks>
    /// Repeats are raised by <see cref="NotificationRepeatService"/> straight to the presenter, so the presenter hands
    /// them back through here (<see cref="NotificationOptionsPresenter"/>). Null when that service has been turned off
    /// since: a repeat is not shown. It never offers a code; that was offered once, with the message. It keeps the sender's
    /// photo only while that option is still on and details are shown.
    /// </remarks>
    public NotificationContent? ForRepeat(NotificationContent content)
    {
        string? serviceKey;
        lock (delayLock) serviceOf.TryGetValue(content.Record.Id, out serviceKey);
        var options = preferences.Options;
        if (serviceKey is not null && !options.IsOn(serviceKey)) { RecordDelivery("repeat not shown (service off)"); return null; }
        var (title, preview) = NotificationOptions.Shape(content.Title, content.Preview, options.Detail);
        return content with
        {
            Title = title, Preview = preview, Code = null,
            Photo = options.SenderPhoto && options.Detail != NotificationDetail.None ? content.Photo : null,
            Sound = serviceKey is null ? NotificationSound.TextMessage : options.SoundFor(serviceKey),
        };
    }

    internal static string Clip(string value, int length)
    {
        // What Windows cannot load goes first (ToastText), so it neither fails the notification nor counts toward the length.
        value = ToastText.XmlSafe(value);
        // Never cut a surrogate pair, combining character or joined emoji in a toast preview.
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        if (elements.Length <= length) return value;
        return value[..elements[length]] + "…";
    }
}
