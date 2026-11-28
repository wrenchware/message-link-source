using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// Who raises a reminder whose moment has come. App: Messages raises it now (or at its moment, which is near). DeliveredByWindows:
// Windows showed it while Messages was closed, so nothing more is raised and the reminder is simply ended. LeftToWindows:
// Windows still holds it and Messages cannot take it back (notifications were not registered this run), so Windows shows it.
public enum ReminderHandOff { App, DeliveredByWindows, LeftToWindows }

/// <summary>
/// Reminders ("Remind me" on a message, and the reminder a snoozed conversation is owed) handed to Windows for their time,
/// so they come on time while Messages is closed. Exactly one notification comes for each.
/// </summary>
/// <remarks>
/// <para>The rule. While a reminder is more than <see cref="Lead"/> away, Windows holds it, scheduled under the reminder's
/// record id, and the record says so (<see cref="NotificationRecord.ScheduledFor"/>): that record is the claim-once record
/// every notification already has, so while Windows holds it the app's own delivery finds it claimed and stands down.
/// Within <see cref="Lead"/> of the moment a running Messages takes it back — removes it from Windows' schedule, marks the
/// record TakenBack — and raises it itself at the moment, with everything the running app knows (the words when they may be
/// shown, Pause, whether a snoozed conversation still has anything unread). A reminder Windows no longer holds once its
/// moment has passed was shown by Windows, and is only ended.</para>
/// <para>What that means around a restart. Closed at the moment: Windows shows it; the next start ends it. Started just
/// before it: the first pass takes it back. Started just after: Windows has shown it. Quit inside the last
/// <see cref="Lead"/>, after the app took it back: it comes at the next start, late, as every reminder did before this —
/// nothing is handed back to Windows at quit, because a quit (or a Windows sign-out) is no place for more work, and handing
/// it back would need the record rewritten on the way out. A crash is the same.</para>
/// <para>What Windows shows. Windows keeps a scheduled notification in its own store until it is shown, possibly for days,
/// so a scheduled one never carries the message's words: at most the conversation's name and "Reminder". It follows
/// "Show in notifications" and Pause as they are when it is scheduled, and every change to either (or to notifications
/// being on, or a service being off) builds them again (<see cref="ReconcileAsync"/> with rebuild).</para>
/// <para>Accounts. Signing out of a service removes that service's reminders from Windows; signing back in schedules them
/// again. An account that is merely not known yet (a service still connecting at a start) keeps its reminders with Windows,
/// so a quick restart loses none; if one comes while that account is still unknown, Windows shows it and the app ends it
/// once the account is back. Clicking one whose account has changed (signed out while Messages was closed) goes through the
/// same record checks as every notification, which refuse another connection's record and open nothing of it.</para>
/// </remarks>
public sealed class ReminderSchedule(IScheduledNotifications windows, INotificationStore store, NotificationPreferences preferences,
    TimeProvider? clock = null, Action<string>? log = null)
{
    /// <summary>How long before its moment a running Messages takes a reminder back from Windows.</summary>
    /// <remarks>Longer than the snooze service's 30-second pass, so a pass always lands inside it.</remarks>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(45);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    // Message reminders and snooze reminders reconcile and take back from their own timers; one at a time here.
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool IsAvailable => Available();

    public void Log(string line) => log?.Invoke(line);

    // One-way, short and different from the scheduled-send tags, for app.log.
    public static string Tag(string id) => NotificationIdentity.Hash("reminder\n" + id)[..10].ToLowerInvariant();

    /// <summary>
    /// Makes Windows' schedule hold exactly the reminders of one kind that are wanted: every one more than <see cref="Lead"/>
    /// away is scheduled, and a scheduled one of that kind that is no longer wanted (cancelled, changed, delivered, its
    /// account signed out) is removed. <paramref name="rebuild"/> replaces those already scheduled, for a change in what
    /// they should show.
    /// </summary>
    /// <param name="messageReminders">True for "Remind me" reminders, false for snooze reminders. Each kind reconciles only its own.</param>
    /// <param name="wanted">Every reminder of the kind that is still to come, near or far, with what Windows should show.</param>
    /// <param name="owed">
    /// For one not in <paramref name="wanted"/>: whether it is still owed all the same, because the account it belongs to
    /// is not known yet (a service still connecting at a start) rather than signed out. Such a one stays with Windows.
    /// </param>
    public async Task ReconcileAsync(bool messageReminders, IReadOnlyList<(NotificationContent Content, DateTimeOffset At)> wanted, bool rebuild,
        Func<NotificationRecord, bool>? owed = null, CancellationToken token = default)
    {
        if (!Available()) return;
        await gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            var pending = new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
            foreach (var item in windows.Pending()) pending.TryAdd(item.Id, item);
            var ids = wanted.Select(item => item.Content.Record.Id).ToHashSet(StringComparer.Ordinal);
            var enabled = preferences.Enabled;
            foreach (var (id, _) in pending)
            {
                if (enabled && ids.Contains(id)) continue;
                // A reminder of the other kind is the other reconcile's; one with no record can only say it has expired.
                var record = await ReadAsync(id, token);
                if (record is not null && record.IsReminder != messageReminders) continue;
                if (enabled && record is not null && owed?.Invoke(record) == true) continue;
                if (!windows.Remove(id)) continue;
                await ReleaseAsync(id, token);
                log?.Invoke($"reminder unscheduled kind={Kind(messageReminders)} id={Tag(id)} reason={(enabled ? "gone" : "notifications_off")}");
            }
            if (!enabled) return;
            foreach (var (content, at) in wanted)
            {
                // Near enough that the running app takes it back at its next pass; nothing to hand over now.
                if (at - now <= Lead) continue;
                var held = pending.ContainsKey(content.Record.Id);
                if (held && !rebuild) continue;
                await HandOverAsync(content, at, held, messageReminders, now, token);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Hands one reminder to Windows now and leaves every other one as it is. For a snooze made where nothing runs the
    /// passes: "Remind me in 1 hour" clicked on a notification while Messages is closed, in the process Windows starts only
    /// to handle that click and which ends straight after. A running Messages' next pass reconciles it with the rest. False
    /// when Windows cannot hold reminders here, notifications are off, the moment is too near, or it was shown already.
    /// </summary>
    public async Task<bool> AddAsync(NotificationContent content, DateTimeOffset at, bool messageReminder, CancellationToken token = default)
    {
        if (!Available() || !preferences.Enabled) return false;
        await gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (at - now <= Lead) return false;
            var held = windows.Pending().Any(item => item.Id == content.Record.Id);
            return await HandOverAsync(content, at, held, messageReminder, now, token);
        }
        finally { gate.Release(); }
    }

    // Claims the record for Windows and gives Windows the notification, inside the gate. True when Windows holds it.
    private async Task<bool> HandOverAsync(NotificationContent content, DateTimeOffset at, bool held, bool messageReminder, DateTimeOffset now,
        CancellationToken token)
    {
        var id = content.Record.Id;
        if (!await ClaimAsync(content.Record, at, token))
        {
            // Raised by the app already: Windows must not show it a second time.
            if (held && windows.Remove(id)) log?.Invoke($"reminder unscheduled kind={Kind(messageReminder)} id={Tag(id)} reason=already_shown");
            return false;
        }
        try
        {
            if (held) windows.Remove(id);
            windows.Add(content with { Record = content.Record with { ScheduledFor = at, TakenBack = false, CreatedAt = at } }, at);
            if (!held) log?.Invoke($"reminder scheduled kind={Kind(messageReminder)} id={Tag(id)} in_min={(long)(at - now).TotalMinutes}");
            return true;
        }
        catch (Exception failure)
        {
            // Not held by Windows after all, so the app raises it at its moment.
            await ReleaseAsync(id, token);
            log?.Invoke($"reminder schedule failed kind={Kind(messageReminder)} id={Tag(id)} error={failure.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Called by the running app within <see cref="Lead"/> of a reminder's moment, or once it has passed: who raises it.
    /// App means the app does, once, at the moment (its claim-once record now lets it); otherwise it is ended or left.
    /// </summary>
    public async Task<ReminderHandOff> TakeBackAsync(string id, DateTimeOffset at, bool messageReminder, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!NotificationIdentity.IsValidId(id)) return ReminderHandOff.App;
            var record = await ReadAsync(id, token);
            if (record is not { ScheduledFor: not null, TakenBack: false }) return ReminderHandOff.App;
            var now = clock.GetUtcNow();
            if (Available())
            {
                // Removed while still waiting: nobody has shown it, and now only the app will.
                if (windows.Remove(id))
                {
                    await ReleaseAsync(id, token);
                    log?.Invoke($"reminder unscheduled kind={Kind(messageReminder)} id={Tag(id)} reason=app_running");
                    return ReminderHandOff.App;
                }
                // Still to come, yet Windows no longer holds it (its schedule was cleared): the app raises it.
                if (at > now)
                {
                    await ReleaseAsync(id, token);
                    log?.Invoke($"reminder unscheduled kind={Kind(messageReminder)} id={Tag(id)} reason=missing");
                    return ReminderHandOff.App;
                }
            }
            else if (at > now) return ReminderHandOff.LeftToWindows;
            // Windows held it and its moment has passed: Windows showed it.
            log?.Invoke($"reminder delivered_by=windows kind={Kind(messageReminder)} id={Tag(id)} late_min={(long)Math.Max(0, (now - at).TotalMinutes)}");
            return ReminderHandOff.DeliveredByWindows;
        }
        finally { gate.Release(); }
    }

    public void Delivered(string id, bool messageReminder) =>
        log?.Invoke($"reminder delivered_by=app kind={Kind(messageReminder)} id={Tag(id)}");

    private static string Kind(bool messageReminder) => messageReminder ? "message" : "snooze";

    private bool Available()
    {
        try { return windows.IsAvailable; }
        catch (Exception) { return false; }
    }

    private Task<NotificationRecord?> ReadAsync(string id, CancellationToken token) =>
        store.UpdateAsync(id, prior => (prior, prior), token);

    // Windows' claim on the record, made before Windows is given the notification. False when the app has shown it already.
    private Task<bool> ClaimAsync(NotificationRecord record, DateTimeOffset at, CancellationToken token) =>
        store.UpdateAsync(record.Id, prior => prior switch
        {
            null => (record with { ScheduledFor = at, TakenBack = false, CreatedAt = at }, true),
            { ScheduledFor: null } => (prior, false),
            { TakenBack: false } when prior.ScheduledFor == at => (prior, true),
            _ => (prior with { ScheduledFor = at, TakenBack = false, CreatedAt = at }, true),
        }, token);

    // The claim goes back to the app: it raises this reminder itself, once, if its moment comes while it is wanted.
    private Task ReleaseAsync(string id, CancellationToken token) =>
        store.UpdateAsync(id, prior => prior is { ScheduledFor: not null, TakenBack: false } ? (prior with { TakenBack = true }, true) : (prior, false), token);
}

// The services signed out here, as both kinds of reminder keep them: by the prefix their conversations' ids share. Only a
// sign-out adds one, and only that service connecting again takes it out; a connection that dropped, a helper restarting
// or an account not known yet at a start is none of these, and its reminders stay with Windows throughout.
internal static class ReminderAccounts
{
    /// <summary>Takes out every signed-out prefix the connection scope names. True when one went.</summary>
    public static bool Forget(HashSet<string> signedOut, string scope)
    {
        if (string.IsNullOrEmpty(scope)) return false;
        lock (signedOut) return signedOut.RemoveWhere(prefix => prefix.StartsWith(scope, StringComparison.Ordinal)) > 0;
    }
}
