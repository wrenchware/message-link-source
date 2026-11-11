namespace NativeBeeper.Core.Services;

public enum NotificationActionState { None, Working, Accepted, Complete, Failed, Unknown }
public sealed record NotificationRecord(string Id, string Profile, string ChatId, string MessageId, DateTimeOffset CreatedAt,
    bool CanReply, bool CanRead, NotificationActionState ReplyState = NotificationActionState.None,
    NotificationActionState ReadState = NotificationActionState.None, string? ReplyText = null, string? PendingId = null,
    int Version = 1, bool IsTest = false, string? GroupId = null, string? SourceScope = null,
    // "Remind me about this message": opening it goes to MessageId itself, and it offers no reply, read, snooze or mute.
    bool IsReminder = false,
    // A reminder handed to Windows to show at this moment, so it comes while Messages is closed (ReminderSchedule). Until
    // TakenBack the claim is Windows': the app raises nothing for it. TakenBack: the app took it back (it is running near
    // the moment, or the reminder was cancelled) and raises it itself, once.
    DateTimeOffset? ScheduledFor = null, bool TakenBack = false);
// The sound a notification plays, chosen per service in Notification options. None plays nothing.
public enum NotificationSound { TextMessage, Mail, Reminder, WindowsDefault, None, Balloon, Chimes, Chord, Ding, Notify, Nudge, Proximity, Ring, Tada, Custom, Wake, Acknowledge, Signal, Dock, Transmission, Swell, Descent, Resonance, Pulse, Blip, Beacon, Glow, Hum }
// CustomSound: with NotificationSound.Custom, the name of the person's own sound file kept in the data folder.
// Silent: add to Notification Center without showing a banner.
// Attribution: a small line under the message naming what this notification is, when it is not simply a new message.
// Photo: the sender's picture as a file on this PC (NotificationPhotos), shown as the notification's round logo. Only when
// "Show the sender's photo" is on, and the file goes when the notification does.
public sealed record NotificationContent(NotificationRecord Record, string Title, string Preview, string Network, bool Silent = false, string? Attribution = null, bool IsRepeat = false,
    NotificationSound Sound = NotificationSound.TextMessage, string? Photo = null, string? CustomSound = null);
// Presentation hints from already-loaded state, never permission to bypass mutation checks.
public interface INotificationActionHints
{
    (bool Reply, bool Read) GetNotificationActions(string chatId);
}
// A provider's chat/account alias may be reused after sign-out. Actions require the actual current owner.
public interface INotificationAccountScope
{
    Task<string?> ForSourceAsync(NativeBeeper.Core.Messaging.ChatSummary source, CancellationToken cancellationToken = default);
    Task<string?> ForChatAsync(string chatId, CancellationToken cancellationToken = default);
}
public interface INotificationStore
{
    // Mutation and replacement are atomic across processes. Throw on corruption; never treat it as a new operation.
    Task<T> UpdateAsync<T>(string id, Func<NotificationRecord?, (NotificationRecord? Record, T Result)> mutation, CancellationToken cancellationToken = default);
}
public interface INotificationPresenter
{
    Task ShowMessageAsync(NotificationContent content);
    Task ShowResultAsync(NotificationRecord record, string status);
    Task RemoveAsync(string id);
}
// A notification Windows holds and shows at its own time, whether or not Messages is running. Id is the record's id, which
// is also the notification's tag once it is shown.
public sealed record ScheduledNotification(string Id, DateTimeOffset At);
// The Windows side of reminders that come on time while Messages is closed. Synchronous: each is one call into Windows.
public interface IScheduledNotifications
{
    // False where Windows cannot hold one for this app (a development copy, a registration that failed, an identity that
    // could not be found): the app then raises every reminder itself, as it always did.
    bool IsAvailable { get; }
    IReadOnlyList<ScheduledNotification> Pending();
    void Add(NotificationContent content, DateTimeOffset at);
    // True when it was still waiting and is now gone; false when Windows no longer held it (shown already, or lost).
    bool Remove(string id);
}
