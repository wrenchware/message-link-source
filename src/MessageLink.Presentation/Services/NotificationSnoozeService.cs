using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>Delivers the one reminder each snoozed conversation is owed, once its moment arrives.</summary>
/// <remarks>
/// <para>Each snooze still to come is also handed to Windows for its moment (<see cref="ReminderSchedule"/>), so its reminder
/// comes on time while Messages is closed; a running Messages takes it back just before the moment and raises its own, which
/// knows whether anything is still unread. Where Windows cannot hold it, a moment that passed while the app was closed is
/// still delivered at the next start rather than dropped: the point of snoozing is to be told later, and later has already
/// arrived. Nothing here sends, reads or otherwise touches an account; a reminder is a local notification about messages
/// already received.</para>
/// <para>What Windows is handed is built from this PC's own copy of each conversation (<paramref name="localChat"/>: the
/// chat list as last shown), never by asking a service. Scheduling runs again on every snooze, Notification options change,
/// Pause, notifications on or off and sign-out; asking a service each time would be a read per snooze per change, and on
/// Instagram a live request whose limits are undocumented. A conversation this PC has not shown yet keeps what Windows
/// already holds for it, as an account not known yet does. Only the reminder itself, raised by the running app at its
/// moment, reads the conversation, once, to know what is unread.</para>
/// </remarks>
public sealed class NotificationSnoozeService(IChatSnoozes snoozes, IChatReader chats, NotificationObserver observer, IAccessTokenStore tokens, MergedConversations? merges = null,
    ReminderSchedule? windows = null, TimeProvider? clock = null, Func<string, ChatSummary?>? localChat = null)
{
    // Close enough that a reminder lands within half a minute of its moment, cheap enough to run all day: a pass
    // reads one small directory and stops there when nothing is due.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    // How long a reminder keeps trying when the conversation cannot be read yet — a start that has not finished
    // connecting, mostly. After that the snooze is cleared, because a conversation silenced forever is worse than
    // a reminder that never came.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    // One pass at a time: the timer's, and the one a changed snooze asks for at once.
    private readonly SemaphoreSlim passes = new(1, 1);
    private volatile bool reschedule = true, rebuild, started;
    private int hooked;

    // Raised with a conversation's id when its snooze ends, so the list can stop showing it as snoozed.
    public event EventHandler<string>? Cleared;

    private volatile string summary = "No conversations are snoozed.";
    // What is waiting and when it comes back; times only, no conversation named.
    public string Summary => summary;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        started = true;
        Hook();
        using var timer = new PeriodicTimer(Interval, time);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await DeliverDueAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { /* A snooze failure must not stop messaging, and the next pass tries again. */ }
            try { if (!await timer.WaitForNextTickAsync(cancellationToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Something that decides what Windows holds has changed: an account signed in or out (<paramref name="rebuildAll"/>
    /// false), or what a reminder should show (true). The next pass, at once while running, schedules again.
    /// </summary>
    public void Reschedule(bool rebuildAll)
    {
        reschedule = true; rebuild |= rebuildAll; lookedAgain = 0;
        if (started) _ = PassNowAsync();
    }

    /// <summary>
    /// A snooze just made by a notification's "Remind me in 1 hour" (<paramref name="clicked"/>), handed to Windows at once
    /// when no pass will do it: in the process Windows starts only to handle that click while Messages is closed, where this
    /// service does not run and which ends straight after. Without Windows' schedule (a development copy) it stays as it
    /// was: the reminder comes when Messages next runs. False when nothing was handed over; while running, always (the
    /// change to the snooze already asks for a pass).
    /// </summary>
    public async Task<bool> HandOffAsync(NotificationRecord clicked, ChatSnooze snooze, CancellationToken cancellationToken = default)
    {
        if (started || windows is null || !windows.IsAvailable) return false;
        var content = await observer.ScheduledSnoozeReminderAsync(clicked, snooze.ChatId, snooze.Until, localChat?.Invoke(snooze.ChatId), cancellationToken);
        return content is not null && await windows.AddAsync(content, snooze.Until, messageReminder: false, cancellationToken);
    }

    // A snooze whose conversation this PC has not shown yet (the list still loading at a start, or a service just signed in)
    // is looked for again at the next passes, half a minute apart, for as long as this; then only a change asks again.
    private const int LookAgainPasses = 20;
    private int lookedAgain;

    // A snooze set or ended anywhere (ObservedChatSnoozes) is followed at once, so Windows never keeps one that was ended.
    private void Hook()
    {
        if (snoozes is not ObservedChatSnoozes observed || Interlocked.Exchange(ref hooked, 1) == 1) return;
        observed.Changed += (_, _) => Reschedule(false);
    }

    private async Task PassNowAsync()
    {
        try { await DeliverDueAsync(); }
        catch (Exception) { /* The timer's pass tries again. */ }
    }

    /// <summary>Reminds every conversation whose moment has come. Returns how many notifications were raised.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        Hook();
        await passes.WaitAsync(cancellationToken);
        try { return await PassAsync(cancellationToken); }
        finally { passes.Release(); }
    }

    private async Task<int> PassAsync(CancellationToken cancellationToken)
    {
        var all = await snoozes.ListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var delivered = 0;
        string? profile = null;
        var lead = windows is null ? TimeSpan.Zero : ReminderSchedule.Lead;
        if (windows is not null && reschedule)
        {
            profile = await NotificationIdentity.ProfileAsync(tokens, cancellationToken);
            await ReconcileAsync(all, profile, cancellationToken);
        }
        foreach (var snooze in all)
        {
            if (snooze.Until - lead > now) continue;
            // A snooze belongs to the connection that made it. Another account's is left untouched rather than
            // delivered or deleted: signing back in should find it where it was.
            profile ??= await NotificationIdentity.ProfileAsync(tokens, cancellationToken);
            if (profile is null || snooze.Profile != profile) continue;
            string? scheduledId = null;
            if (windows is not null)
            {
                scheduledId = NotificationObserver.SnoozeReminderId(profile, snooze.ChatId, snooze.Until);
                var handOff = await windows.TakeBackAsync(scheduledId, snooze.Until, messageReminder: false, cancellationToken);
                if (handOff == ReminderHandOff.LeftToWindows) continue;
                // Shown by Windows while Messages was closed: the snooze is over, and nothing more is raised.
                if (handOff == ReminderHandOff.DeliveredByWindows) { await EndAsync(snooze, cancellationToken); continue; }
                // Taken back: raised here at its moment.
                if (snooze.Until > now) continue;
            }
            var spent = true;
            try
            {
                var chat = await chats.GetChatAsync(snooze.ChatId, cancellationToken);
                if (await observer.RemindAsync(chat, snooze.Until, cancellationToken))
                {
                    delivered++;
                    if (scheduledId is not null) windows!.Delivered(scheduledId, messageReminder: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { spent = now - snooze.Until > Grace; }
            if (!spent) continue;
            await EndAsync(snooze, cancellationToken);
        }
        Describe(all, now);
        return delivered;
    }

    private async Task EndAsync(ChatSnooze snooze, CancellationToken cancellationToken)
    {
        await snoozes.SetAsync(snooze.Profile, snooze.ChatId, null, cancellationToken);
        if (merges?.Find(snooze.ChatId) is not null) await merges.SetAsync(snooze.ChatId, g => g with { SnoozedUntil = null });
        Cleared?.Invoke(this, snooze.ChatId);
    }

    /// <summary>
    /// A service was signed out (its conversations' ids begin <paramref name="chatPrefix"/>): Windows stops holding its
    /// snoozes' reminders now. The snoozes themselves stay, for when it is back.
    /// </summary>
    public void SignedOut(string chatPrefix)
    {
        lock (signedOut) signedOut.Add(chatPrefix);
        Reschedule(false);
    }

    /// <summary>
    /// A service connected (its connection scope, "messenger:" or "gvoice:&lt;account&gt;:"): one signed out here is handed
    /// back to Windows, and the schedule is looked at again for the snoozes a service just signed in brings, which costs
    /// only this PC's own copies. Only a sign-out puts a service in that set; a connection that dropped, a helper restarting
    /// or a service still connecting at a start never does, and keeps its reminders with Windows throughout.
    /// </summary>
    public void Connected(string scope)
    {
        ReminderAccounts.Forget(signedOut, scope);
        Reschedule(false);
    }

    private readonly HashSet<string> signedOut = new(StringComparer.Ordinal);

    // Windows' schedule made to match the snoozes of the connection signed in now, each shaped as Windows would show it.
    // One whose conversation or account cannot be told yet (a start still connecting) keeps what Windows holds for it, unless
    // its service was signed out here; one that could not be scheduled is still reminded by the app when its moment comes.
    private async Task ReconcileAsync(IReadOnlyList<ChatSnooze> all, string? profile, CancellationToken cancellationToken)
    {
        var full = rebuild;
        reschedule = false; rebuild = false;
        var wanted = new List<(NotificationContent, DateTimeOffset)>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        if (profile is not null)
            foreach (var snooze in all.Where(snooze => snooze.Profile == profile))
            {
                try
                {
                    // This PC's copy only (see the remarks): one not shown here yet is not known, and keeps what Windows holds.
                    if (localChat?.Invoke(snooze.ChatId) is not { } chat)
                    { unknown.Add(NotificationObserver.SnoozeReminderId(profile, snooze.ChatId, snooze.Until)); continue; }
                    var (content, undecided) = await observer.ScheduledSnoozeReminderAsync(chat, snooze.Until, cancellationToken);
                    if (content is not null) wanted.Add((content, snooze.Until));
                    else if (undecided) unknown.Add(NotificationObserver.SnoozeReminderId(profile, snooze.ChatId, snooze.Until));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { unknown.Add(NotificationObserver.SnoozeReminderId(profile, snooze.ChatId, snooze.Until)); }
            }
        bool Owed(NotificationRecord record)
        {
            if (!unknown.Contains(record.Id)) return false;
            lock (signedOut) return !signedOut.Any(prefix => record.ChatId.StartsWith(prefix, StringComparison.Ordinal));
        }
        try { await windows!.ReconcileAsync(messageReminders: false, wanted, full, Owed, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) { reschedule = true; windows!.Log($"reminder reconcile failed kind=snooze error={failure.GetType().Name}"); }
        if (unknown.Count > 0 && lookedAgain < LookAgainPasses) { lookedAgain++; reschedule = true; }
    }

    private void Describe(IReadOnlyList<ChatSnooze> all, DateTimeOffset now)
    {
        var waiting = all.Where(s => s.Until > now).Select(s => s.Until).OrderBy(until => until).ToList();
        summary = waiting.Count == 0 ? "No conversations are snoozed."
            : waiting.Count == 1 ? $"1 conversation snoozed until {waiting[0].ToLocalTime():t}."
            : $"{waiting.Count} conversations snoozed; the next reminder is at {waiting[0].ToLocalTime():t}.";
    }
}
