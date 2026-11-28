namespace MessageLink.Presentation.Services;

/// <summary>"Remind me about this message": raises one notification for each saved message whose reminder time has come.</summary>
/// <remarks>
/// <para>A reminder is kept in Saved Messages with its time (<see cref="SavedMessage.RemindAt"/>), so it survives a restart
/// and is seen, and cancelled, in the Saved Messages list. Each one still to come is also handed to Windows for its time
/// (<see cref="ReminderSchedule"/>), so it comes on time while Messages is closed; a running Messages takes it back just
/// before its moment and raises it itself. When Windows cannot hold it (a development copy, notifications not registered), a
/// time that passed while Messages was closed is delivered once at the next start, after the start-up quiet period; nothing
/// is dropped for being late.</para>
/// <para>Nothing here contacts a service or marks anything read: the notification is built from the saved copy. A reminder
/// whose account is not signed in now is left as it is, to be delivered when that account is back, as a snooze is; Windows
/// stops holding it when that service is signed out here, and keeps it while the account is only not known yet.</para>
/// <para><b>On every device</b> (stage 6 slice 8, a client of a server only: <see cref="SavedMessages.IsShared"/>). Each running device raises a reminder for itself, and
/// the first to raise it marks the shared row reminded, which is the row's end state however many do (the marks are the same words, so a second changes nothing
/// the first did not). The mark reaches the other devices as the time going from pending to raised, which would take their pending entry away before their own alarm
/// is due; so an entry that goes that way, raised by another device at the very time it waited for, stays here as an <i>owed</i> one until this device has raised
/// it (its own notification identity makes that at most once, as for any reminder) or the row says otherwise: a time set again, a cancel, an unsave. A device that
/// was not running when it was raised raises nothing for it afterwards. Nothing here sends or marks read anywhere: a reminder is built from the saved copy.</para>
/// </remarks>
public sealed class MessageReminders : IDisposable
{
    private readonly SavedMessages saved;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> accounts;
    private readonly NotificationObserver observer;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly ReminderSchedule? windows;
    private readonly LocalAlarm alarm;
    // What is due when, read from Saved Messages again whenever it changes. Each entry is read once more from the store
    // just before its notification, which is what decides: this list only says when to look.
    private IReadOnlyList<SavedMessage> due = [];
    private bool dirty = true, started, reschedule = true, rebuild;
    private DateTimeOffset readAt;
    private static readonly TimeSpan Reread = TimeSpan.FromMinutes(10);
    // Slice 8, shared rows only. Owed: entries of `due` whose time another device raised (the row says so) and this device has not yet; Raised: the times this
    // device has raised and marked itself, so the row's mark of them is not mistaken for another device's.
    private readonly List<SavedMessage> owed = [];
    private readonly HashSet<string> raisedHere = new(StringComparer.Ordinal);
    private static readonly TimeSpan OwedKept = TimeSpan.FromMinutes(10);
    // When this device began running: a time raised elsewhere from then on is one it was running for (D7).
    private readonly DateTimeOffset runningSince;
    private const int RaisedLimit = 1000;
    private static string RaisedKey(SavedMessage entry) => entry.Scope + "\n" + entry.Chat.Id + "\n" + entry.Message.Id + "\n" + entry.RemindAt!.Value.UtcTicks;

    public MessageReminders(SavedMessages saved, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> accounts,
        NotificationObserver observer, TimeProvider? clock = null, IUiDispatcher? dispatcher = null, Action<string>? log = null,
        ReminderSchedule? windows = null)
    {
        this.saved = saved; this.accounts = accounts; this.observer = observer;
        this.clock = clock ?? TimeProvider.System; this.log = log; this.windows = windows;
        alarm = new LocalAlarm(this.clock, RunDueAsync, dispatcher);
        runningSince = this.clock.GetUtcNow();
        saved.Changed += SavedChanged;
    }

    public Task Idle => alarm.Idle;

    /// <summary>Starts delivering, the first pass after <paramref name="quiet"/> (the start-up quiet period).</summary>
    public void Start(TimeSpan quiet)
    {
        started = true;
        alarm.WakeAt(clock.GetUtcNow() + (quiet > TimeSpan.Zero ? quiet : TimeSpan.Zero));
    }

    public void Stop() => alarm.Stop();

    /// <summary>
    /// Something that decides what Windows holds has changed: an account signed in or out (<paramref name="rebuildAll"/>
    /// false), or what a reminder should show — Notification options, Pause, notifications on or off (true).
    /// </summary>
    public void Reschedule(bool rebuildAll)
    {
        dirty = true; reschedule = true; rebuild |= rebuildAll;
        if (started) alarm.WakeAt(clock.GetUtcNow());
    }

    /// <summary>
    /// A service was signed out (its conversations' ids begin <paramref name="chatPrefix"/>): Windows stops holding its
    /// reminders now, rather than keeping them as it does for an account that is only not known yet.
    /// </summary>
    public void SignedOut(string chatPrefix)
    {
        lock (signedOut) signedOut.Add(chatPrefix);
        Reschedule(false);
    }

    /// <summary>
    /// A service connected again (its connection scope): one signed out here has its reminders handed back to Windows.
    /// Nothing else puts a service in that set, so a dropped connection or a helper restarting changes nothing here.
    /// </summary>
    public void Connected(string scope)
    {
        if (ReminderAccounts.Forget(signedOut, scope)) Reschedule(false);
    }

    private readonly HashSet<string> signedOut = new(StringComparer.Ordinal);
    private IReadOnlySet<string> currentScopes = new HashSet<string>(StringComparer.Ordinal);

    // Not wanted, yet still owed: its account is not signed in as far as this pass knows, and was not signed out here.
    private bool Owed(MessageLink.Core.Services.NotificationRecord record)
    {
        if (record.SourceScope is not { } scope || currentScopes.Contains(scope)) return false;
        lock (signedOut) return !signedOut.Any(prefix => record.ChatId.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void SavedChanged(object? sender, EventArgs args)
    {
        dirty = true; reschedule = true;
        if (started) alarm.WakeAt(clock.GetUtcNow());
    }

    // The moment a pass has something to do for an entry: taking it back from Windows a little before its time, then its time.
    private TimeSpan Lead => windows is null ? TimeSpan.Zero : ReminderSchedule.Lead;

    /// <summary>One pass: every reminder whose time has come is raised once and then ended.</summary>
    public async Task RunDueAsync()
    {
        if (!started) return;
        var now = clock.GetUtcNow();
        // An account signed in since the last read brings its own reminders, so the list is read again now and then even
        // when Saved Messages has not changed.
        if (now - readAt > Reread) { dirty = true; reschedule = true; }
        // Nothing due and nothing changed: the minute's wake costs nothing more.
        if (!dirty && !due.Any(entry => entry.RemindAt!.Value - Lead <= now)) { alarm.WakeAt(Next(now)); return; }
        var scopes = await accounts(CancellationToken.None);
        currentScopes = scopes.Values.ToHashSet(StringComparer.Ordinal);
        async Task ReadAsync()
        {
            if (!dirty) return;
            dirty = false; readAt = clock.GetUtcNow();
            var all = await saved.ListAsync(scopes.Values);
            bool Signed(SavedMessage item) => scopes.TryGetValue(item.Chat.AccountId, out var scope) && scope == item.Scope;
            var pending = all.Where(item => item.RemindAt is not null && Signed(item)).OrderBy(item => item.RemindAt).ToArray();
            due = saved.IsShared ? await WithOwedAsync(pending, all.Where(Signed)) : pending;
        }
        await ReadAsync();
        if (windows is not null && reschedule) await ReconcileAsync();
        foreach (var entry in due.Where(entry => entry.RemindAt!.Value - Lead <= now).ToArray())
        {
            var at = entry.RemindAt!.Value;
            var owedNow = IsOwed(entry);
            // Read again: unsaved, moved or cancelled since the list was read means nothing is due. An owed time (another device raised it) is still owed only while
            // the row says it was raised at that very time and nothing has been set since.
            if (await saved.GetAsync(entry.Scope, entry.Chat.Id, entry.Message.Id) is not { } item || (owedNow ? item.RemindAt is not null || item.RemindedAt != at : item.RemindAt != at))
            {
                dirty = true;
                if (owedNow) owed.RemoveAll(other => Same(other, entry));
                continue;
            }
            // The account that saved it must be the one signed in now; otherwise it waits for that account.
            if (!scopes.TryGetValue(item.Chat.AccountId, out var scope) || scope != item.Scope) continue;
            // Shared rows only: this device raised this very time already, and its mark lost (another device rewrote the row meanwhile, with the same time) so the
            // entry is pending again. Only the mark is made again; raising it a second time would show it twice.
            if (!owedNow && saved.IsShared && raisedHere.Contains(RaisedKey(entry)))
            {
                await saved.MarkRemindedAsync(entry.Scope, entry.Chat.Id, entry.Message.Id, at);
                continue;
            }
            var tag = ScheduledSends.Tag(entry.Chat.Id + "\n" + entry.Message.Id);
            string? id = null;
            if (windows is not null)
            {
                id = await observer.MessageReminderIdAsync(item.Scope, item.Message.ChatId, item.Message.Id, at);
                if (id is null) continue;
                var handOff = await windows.TakeBackAsync(id, at, messageReminder: true);
                if (handOff == ReminderHandOff.LeftToWindows) continue;
                if (handOff == ReminderHandOff.DeliveredByWindows)
                {
                    await RaisedAsync(entry, at, owedNow);
                    log?.Invoke($"message-reminder delivered id={tag} by=windows late_min={(long)Math.Max(0, (now - at).TotalMinutes)}{(owedNow ? " via=other_device" : "")}");
                    continue;
                }
                // Taken back: raised here at its moment.
                if (at > now) continue;
            }
            var outcome = await observer.RemindAboutMessageAsync(item.Chat, item.Message, item.Scope, at, now: clock.GetUtcNow());
            if (outcome == ReminderOutcome.NotYet) continue;
            await RaisedAsync(entry, at, owedNow);
            log?.Invoke($"message-reminder {(outcome == ReminderOutcome.Off ? "not_shown" : "delivered")} id={tag} " +
                $"late_min={(long)Math.Max(0, (now - at).TotalMinutes)}{(outcome == ReminderOutcome.AlreadyShown ? " already=1" : "")}{(owedNow ? " via=other_device" : "")}");
            if (outcome == ReminderOutcome.Shown && id is not null) windows!.Delivered(id, messageReminder: true);
        }
        await ReadAsync();
        alarm.WakeAt(Next(now));
    }

    private static bool Same(SavedMessage a, SavedMessage b) =>
        a.RemindAt == b.RemindAt && a.Message.Id == b.Message.Id && a.Chat.Id == b.Chat.Id && a.Scope == b.Scope;

    private bool IsOwed(SavedMessage entry) => owed.Count > 0 && owed.Any(other => Same(other, entry));

    // Shared rows only. The pending reminders with the owed ones: an entry the last read had that is not pending now, whose row says it was raised at the time it
    // waited for and by a device other than this one (this device marks its own: `raisedHere`), is owed to this device. An owed time that has outlived
    // OwedKept without this device raising it (its account is not signed in here) is let go: the other device raised it, and a reminder is not kept for later.
    private async Task<IReadOnlyList<SavedMessage>> WithOwedAsync(SavedMessage[] pending, IEnumerable<SavedMessage> all)
    {
        foreach (var entry in due)
        {
            if (IsOwed(entry) || pending.Any(item => Same(item, entry)) || raisedHere.Contains(RaisedKey(entry))) continue;
            var at = entry.RemindAt!.Value;
            if (await saved.GetAsync(entry.Scope, entry.Chat.Id, entry.Message.Id) is { RemindAt: null } item && item.RemindedAt == at) owed.Add(entry);
        }
        var now = clock.GetUtcNow();
        // Also a time another device raised while this one was running, which this device may never have read as pending (it is still in its start-up quiet period,
        // or has not made a pass since): the row alone says it was raised at a time from this run, and this device has not raised it.
        foreach (var item in all)
        {
            if (item is not { RemindAt: null, RemindedAt: { } raised }) continue;
            // When it was raised, not when it was due: raised late by another device after this one started, it is a reminder this device was running for. A row marked by a device
            // that did not say when (an older one) is judged by the time it was due, as before.
            // The raising device's clock may be wrong by any amount: it cannot have raised it before it was due, nor after now.
            var rang = item.RaisedAt is { } stamped ? (stamped < raised ? raised : stamped > now ? now : stamped) : raised;
            if (rang < runningSince || rang + OwedKept < now) continue;
            var entry = item with { RemindAt = raised, RaisedAt = rang };
            if (!IsOwed(entry) && !raisedHere.Contains(RaisedKey(entry))) owed.Add(entry);
        }
        owed.RemoveAll(entry => (entry.RaisedAt ?? entry.RemindAt!.Value) + OwedKept < now);
        return owed.Count == 0 ? pending : [.. pending.Concat(owed).OrderBy(item => item.RemindAt)];
    }

    // This device raised the reminder: the shared row is marked, unless it already says so because another device raised it first, in which case this device's
    // part is done.
    private async Task RaisedAsync(SavedMessage entry, DateTimeOffset at, bool owedNow)
    {
        if (saved.IsShared)
        {
            if (raisedHere.Count >= RaisedLimit) raisedHere.Clear();
            raisedHere.Add(RaisedKey(entry));
        }
        if (owedNow)
        {
            owed.RemoveAll(other => Same(other, entry));
            dirty = true;
            return;
        }
        await saved.MarkRemindedAsync(entry.Scope, entry.Chat.Id, entry.Message.Id, at);
    }

    // Windows' schedule made to match the reminders to come, each shaped as Windows would show it.
    private async Task ReconcileAsync()
    {
        var full = rebuild;
        reschedule = false; rebuild = false;
        var wanted = new List<(MessageLink.Core.Services.NotificationContent, DateTimeOffset)>();
        foreach (var item in due)
            if (await observer.ScheduledMessageReminderAsync(item.Chat, item.Message, item.Scope, item.RemindAt!.Value) is { } content)
                wanted.Add((content, item.RemindAt.Value));
        try { await windows!.ReconcileAsync(messageReminders: true, wanted, full, Owed); }
        catch (Exception failure) { reschedule = true; log?.Invoke($"reminder reconcile failed kind=message error={failure.GetType().Name}"); }
    }

    private DateTimeOffset? Next(DateTimeOffset now) => due.Select(entry => entry.RemindAt!.Value)
        .Select(at => at - Lead > now ? at - Lead : at > now ? at : (DateTimeOffset?)null)
        .Where(at => at is not null).Min();

    public void Dispose() { alarm.Stop(); saved.Changed -= SavedChanged; }
}
