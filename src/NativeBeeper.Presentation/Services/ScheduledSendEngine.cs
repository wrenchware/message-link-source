using NativeBeeper.Core.Backend;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Send later's policy: when a scheduled message goes, waits or is held, and that it goes at most once. Everything it
/// acts on is a port (ScheduledSendPorts.cs), so the in-process app (ScheduledSends) and a host firing for its clients
/// run the same rules.
/// </summary>
/// <remarks>
/// <para>It sends only while it is running. A message whose time passed more than <see cref="SendWindow"/> ago —
/// Messages was closed, the PC asleep, the service unreachable the whole time — is not sent by itself: it stays in its
/// conversation as not sent, with Send now, Edit and Cancel. Within the window it goes as soon as its service can take it.</para>
/// <para>It is marked as sending, and that is saved, before the outlet is called. A run that ends after that point cannot
/// know whether the service has it, so the next load records it as an interrupted send and never sends it again.
/// Quitting while a message is only waiting sends nothing; it waits for the next run.</para>
/// <para>Signing out of a service keeps its scheduled messages, unsent: while the service is away its messages cannot go,
/// and once their window has passed they are held as not sent like any other, until the user decides.</para>
/// <para>While the custody port says the schedule is not this engine's (S9), a pass sends, holds and saves nothing (and
/// stops at once if custody goes during it), a load only reads, and Send now is refused. The answer to a send already
/// handed over is still recorded.</para>
/// <para>The log gets one line per step with a one-way tag of the message and its conversation: never the text, a name
/// or a number.</para>
/// </remarks>
public sealed class ScheduledSendEngine : IDisposable
{
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(10);
    // While a due message waits for its service to connect, it is looked at again this often.
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    // A cancel or edit the store did not keep says "couldn't cancel, try again", so the message is not sent in the seconds
    // after that: it waits this long past its time (or past the failure, if that is later) for the user to try again.
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(60);
    public const int Limit = 200;
    private readonly IScheduledSendStore? store;
    private readonly IScheduledSendOutlet outlet;
    private readonly IScheduledSendConditions conditions;
    private readonly IScheduledSendCustody custody;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly LocalAlarm alarm;
    private readonly SemaphoreSlim writing = new(1), loading = new(1);
    private List<ScheduledSend> items = [];
    // Due messages that have already waited for their service at least once this run, so one that runs out of its window
    // that way is said to have been unreachable rather than missed.
    private readonly HashSet<string> waited = new(StringComparer.Ordinal);
    // Messages whose cancel or edit failed, and when each may go after all. Cleared by the next thing done with it.
    private readonly Dictionary<string, DateTimeOffset> heldAfterFailure = new(StringComparer.Ordinal);
    private string? profile;
    // judged: what was loaded has been looked at with custody (interrupted and overdue messages settled). A load without
    // custody only reads, so it is judged at the first pass that has custody again.
    private bool loaded, judged, stopped;
    public event EventHandler<string>? Changed;
    /// <summary>A message has just stopped waiting to go, held as not sent, so the person may want to know: once for each, as a hold happens once.</summary>
    public event Action<ScheduledSend>? Held;

    /// <summary>
    /// Counts hand-overs (<see cref="ScheduledSend.Attempt"/>): each one is written, with the row marked sending, as one more than the last, so a host can send
    /// under the fixed id <c>sched:&lt;id&gt;:&lt;n&gt;</c> (S4). The in-process app leaves it off and its rows are as they always were.
    /// </summary>
    public bool NumbersAttempts { get; init; }

    /// <summary>
    /// A send the outlet reports certainly refused is kept as a not-sent message (reason refused) with its words, for the user to decide, instead of ending the
    /// schedule. A host needs it: the in-process outbox hands such a send's words back to the composer, and nobody is at a host's composer to take them.
    /// </summary>
    public bool KeepsRejected { get; init; }

    /// <param name="store">Null keeps nothing: every message lives only in this run (the layout probe).</param>
    /// <param name="dispatcher">Where passes run, when the outlet belongs to one thread (the in-process Outbox).</param>
    public ScheduledSendEngine(IScheduledSendStore? store, IScheduledSendOutlet outlet, IScheduledSendConditions conditions,
        IScheduledSendCustody custody, TimeProvider? clock = null, IUiDispatcher? dispatcher = null, Action<string>? log = null)
    {
        this.store = store; this.outlet = outlet; this.conditions = conditions; this.custody = custody;
        this.clock = clock ?? TimeProvider.System; this.log = log;
        alarm = new LocalAlarm(this.clock, RunDueAsync, dispatcher);
        outlet.Changed += OutletChanged;
    }

    public IReadOnlyList<ScheduledSend> ForChat(string chatId) => [.. items.Where(item => item.ChatId == chatId).OrderBy(item => item.Due)];
    public IReadOnlyList<ScheduledSend> All => [.. items.OrderBy(item => item.Due)];
    public ScheduledSend? Find(string id) => items.FirstOrDefault(item => item.Id == id);
    // The latest pass; the offline checks await it after moving the clock.
    public Task Idle => alarm.Idle;

    /// <summary>
    /// Reads this profile's scheduled messages, once per run (again if the profile changes). A message found sending was
    /// left that way by a run that ended mid-send: it becomes an interrupted send. One whose time passed more than the
    /// window ago was due while Messages was closed: it is held as not sent. Then the schedule starts.
    /// </summary>
    public async Task LoadAsync()
    {
        // One at a time: a message scheduled while the launch's load was still reading must not be replaced by it.
        await loading.WaitAsync();
        try { await LoadCoreAsync(); }
        finally { loading.Release(); }
    }

    private async Task LoadCoreAsync()
    {
        var current = store is null ? "" : await store.ProfileAsync();
        if (loaded && judged && current == profile) return;
        var saved = store is null || current is null ? null : await store.ReadAsync(current);
        profile = current; loaded = true; waited.Clear(); heldAfterFailure.Clear();
        items = [.. saved ?? []];
        // Not this engine's schedule (S9): read to be shown, nothing more. A message found sending is not called
        // interrupted, an overdue one is not held and nothing is saved: whoever holds the schedule settles those, and this
        // copy is the one a rollback returns to. The in-process app's custody is always local, so this never stops it.
        judged = await custody.IsLocalAsync();
        if (!judged)
        {
            Changed?.Invoke(this, "");
            alarm.WakeAt(null);
            return;
        }
        var now = clock.GetUtcNow();
        var heldAtStart = new List<ScheduledSend>();
        foreach (var item in items.ToArray())
        {
            if (item.State == ScheduledSendState.Sending)
            {
                items.Remove(item);
                // A record that could not be kept fails the load: the store still holds the message as sending, so the next load reads it and carries it again, and no save made
                // meanwhile can write a list without it (nothing is saved until a load has finished).
                try { await outlet.CarryInterruptedAsync(item, new InterruptedSend(item.Id, item.ChatId, item.SendingAt ?? item.Due, null, null)); }
                catch (Exception) { loaded = false; throw; }
                Log("interrupted", item);
            }
            else if (item.State == ScheduledSendState.Waiting && now - item.Due > SendWindow)
            {
                Replace(item with { State = ScheduledSendState.NotSent, Reason = ScheduledSendReason.Closed });
                Log("overdue_held", item, "reason=closed");
                heldAtStart.Add(item);
            }
        }
        await SaveAsync();
        foreach (var item in heldAtStart) Held?.Invoke(item);
        Changed?.Invoke(this, "");
        alarm.WakeAt(clock.GetUtcNow());
    }

    /// <summary>
    /// Keeps this text for <paramref name="due"/>. Throws when it cannot be kept; nothing is sent here. <paramref name="id"/> names the message when the
    /// caller has to know it before it is kept (a host makes it from the client's operation id, so a lost answer can be looked for); a message with that
    /// id already here is returned as it is, kept once.
    /// </summary>
    public async Task<ScheduledSend> ScheduleAsync(string chatId, string text, DateTimeOffset due, string? replyToId = null, string? replyPreview = null, string? id = null)
    {
        await LoadAsync();
        if (id is not null && Find(id) is { } already) return already;
        if (store is not null && profile is null) throw new InvalidOperationException("Sign in to a service before scheduling a message.");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(chatId)) throw new ArgumentException("There is nothing to schedule.");
        var now = clock.GetUtcNow();
        // Said in the composer as it is, so no parameter name is attached to it.
        if (due <= now) throw new ArgumentException("Choose a time that hasn't passed yet.");
        if (items.Count >= Limit) throw new InvalidOperationException("Too many messages are scheduled. Send or cancel some first.");
        var scope = conditions.TracksAccounts ? await conditions.AccountOfAsync(chatId) : null;
        if (scope is null && conditions.TracksAccounts && conditions.ExpectsAccount(chatId))
            throw new InvalidOperationException("Message Link can't tell which account this conversation belongs to yet. Try again in a moment.");
        var item = new ScheduledSend(id ?? "local:scheduled:" + Guid.NewGuid().ToString("N"), chatId, text, due, now, replyToId,
            replyToId is null ? null : replyPreview, Scope: scope);
        items.Add(item);
        // Scheduled only once it is kept: the composer says it was not, so it must not stay queued for the alarm to send,
        // and a second try must not make two.
        bool saved;
        try { saved = await SaveAsync(); }
        // The store looked at the id and made nothing (a host never makes a message twice): there is nothing to take back out of it.
        catch (ScheduledIdRetiredException gone) when (gone.Ids.Contains(item.Id)) { items.Remove(item); throw; }
        catch (Exception) { saved = false; }
        if (!saved)
        {
            items.Remove(item);
            try { await SaveAsync(); } catch (Exception) { /* The store failed already; it holds what it held before. */ }
            throw new IOException("Couldn't schedule this message: Message Link couldn't save it on this PC.");
        }
        Log("scheduled", item, $"in_min={(long)Math.Ceiling((due - now).TotalMinutes)}{(replyToId is null ? "" : " reply=1")}");
        Changed?.Invoke(this, chatId);
        alarm.WakeAt(Next(now));
        return item;
    }

    /// <summary>
    /// Removes a message that has not started sending. False once it has, or when it is not here. Throws when the removal
    /// could not be kept: the message is then back, scheduled and cancellable, exactly as it was.
    /// </summary>
    public async Task<bool> CancelAsync(string id) => await RemoveAsync(id, "cancelled") is not null;

    /// <summary>
    /// Takes a message that has not started sending back for the composer to edit: it is no longer scheduled. Throws,
    /// leaving it scheduled, when that could not be kept.
    /// </summary>
    public Task<ScheduledSend?> TakeForEditAsync(string id) => RemoveAsync(id, "cancelled", "by=edit");

    // Gone once the store says so. It leaves the list first, so the alarm cannot send it while the save is pending; if the
    // save fails it comes back as it was, since the stored copy still has it and a restart would send it.
    private async Task<ScheduledSend?> RemoveAsync(string id, string step, string detail = "by=user")
    {
        if (Find(id) is not { State: not ScheduledSendState.Sending } item) return null;
        var from = profile;
        items.Remove(item);
        heldAfterFailure.Remove(id);
        bool saved;
        try { saved = await SaveAsync(); }
        catch (Exception) { saved = false; }
        if (!saved)
        {
            if (profile == from && !items.Any(existing => existing.Id == item.Id))
            {
                items.Add(item);
                // The person is told it is still scheduled, so it does not go in the seconds after that; the alarm looks at
                // it again once the grace has passed.
                var now = clock.GetUtcNow();
                var until = (item.Due > now ? item.Due : now) + CancelGrace;
                heldAfterFailure[id] = until;
                alarm.WakeAt(until);
                // Another save may have succeeded while this one failed, without the message in it: the stored copy must
                // hold it again, or a restart would lose a message the person was told is still scheduled. If this fails
                // as well it stays in memory only, and the failure the person was told about stands.
                try { await SaveAsync(); } catch (Exception) { /* The store is still failing. */ }
            }
            Log("cancel_failed", item, detail);
            throw new IOException("Couldn't cancel this scheduled message: Message Link couldn't save the change on this PC.");
        }
        Log(step, item, detail);
        Changed?.Invoke(this, item.ChatId);
        return item;
    }

    /// <summary>
    /// Asks for a waiting or not-sent message to go now. It goes through the same check as one whose time came: if its
    /// service is still connecting it goes when it can, within the window from now, and is held again after that.
    /// </summary>
    public async Task<bool> SendNowAsync(string id)
    {
        if (Find(id) is not { State: not ScheduledSendState.Sending } item) return false;
        // Not this engine's schedule (S9): refused, with nothing changed. Accepted, it would wait here and go by itself
        // once custody came back, long after the person asked and without asking them again.
        if (!await custody.IsLocalAsync())
        {
            Log("send_now_refused", item, "reason=custody");
            return false;
        }
        var now = clock.GetUtcNow();
        waited.Remove(id);
        heldAfterFailure.Remove(id);
        Replace(item with { Due = now, State = ScheduledSendState.Waiting, Reason = ScheduledSendReason.None });
        await SaveAsync();
        Log("send_now", item);
        Changed?.Invoke(this, item.ChatId);
        await alarm.RunAsync();
        return true;
    }

    /// <summary>Removes every scheduled message this predicate names that is not already sending (a Google reset).</summary>
    public async Task<int> ForgetAsync(Func<string, bool> chat)
    {
        await LoadAsync();
        var gone = items.Where(item => item.State != ScheduledSendState.Sending && chat(item.ChatId)).ToArray();
        if (gone.Length == 0) return 0;
        foreach (var item in gone) { items.Remove(item); Log("cancelled", item, "by=reset"); }
        await SaveAsync();
        Changed?.Invoke(this, "");
        return gone.Length;
    }

    public bool IsSendingIn(Func<string, bool> chat) => items.Any(item => item.State == ScheduledSendState.Sending && chat(item.ChatId));

    /// <summary>Starts the schedule: loads, then wakes at each message's time.</summary>
    public Task StartAsync() => LoadAsync();

    /// <summary>No scheduled message starts sending after this: quitting leaves waiting ones for the next run.</summary>
    public void Stop() { stopped = true; alarm.Stop(); }

    /// <summary>One pass: every waiting message whose time has come is sent, waited for, or held.</summary>
    public async Task RunDueAsync()
    {
        if (stopped) return;
        if (await CustodyLostAsync()) return;
        if (!loaded || !judged || store is not null && await store.ProfileAsync() != profile) await LoadAsync();
        var now = clock.GetUtcNow();
        var retry = false;
        foreach (var item in items.Where(item => item.State == ScheduledSendState.Waiting && Ready(item) <= now).OrderBy(item => item.Due).ToArray())
        {
            if (stopped) return;
            if (await CustodyLostAsync()) return;
            if (IsLate(item))
            {
                // Its time went by without Messages being able to send it: never sent by itself this late.
                await HoldAsync(item, LateReason(item));
                continue;
            }
            var ready = await ReadyAsync(item);
            if (stopped) return;
            // Asked again after the service was: the check can take half a minute, and the schedule may have been handed
            // over meanwhile. Nothing waits between here and the write that follows (Sending, or a hold).
            if (await CustodyLostAsync()) return;
            // The user may have cancelled, edited or sent it while the service was asked.
            if (Find(item.Id) is not { State: ScheduledSendState.Waiting } still || Ready(still) > now) continue;
            // The check can take up to half a minute, and earlier messages in this pass took theirs: the window is judged
            // again at the moment it would go.
            if (IsLate(still)) { await HoldAsync(still, LateReason(still)); continue; }
            if (ready == Readiness.Wait)
            {
                if (waited.Add(item.Id)) Log("waiting", item);
                retry = true;
                continue;
            }
            if (ready == Readiness.Refused) { await HoldAsync(still, ScheduledSendReason.Refused); continue; }
            if (ready == Readiness.OtherAccount) { await HoldAsync(still, ScheduledSendReason.Account); continue; }
            await BeginSendAsync(still);
        }
        var next = Next(now);
        if (retry) next = next is { } at && at < now + RetryInterval ? at : now + RetryInterval;
        alarm.WakeAt(next);
    }

    // Not this engine's schedule (S9): the pass stops where it is, before anything more is sent, held or saved, and
    // custody is looked at again at the alarm's longest wait. Always false in the in-process app, whose custody is local.
    private async ValueTask<bool> CustodyLostAsync()
    {
        if (await custody.IsLocalAsync()) return false;
        alarm.WakeAt(null);
        return true;
    }

    // The next time still to come. One already due and waiting for its service is looked at again after RetryInterval,
    // never at once: that would ask the service over and over while it connects.
    private DateTimeOffset? Next(DateTimeOffset now) =>
        items.Where(item => item.State == ScheduledSendState.Waiting && Ready(item) > now).Select(item => (DateTimeOffset?)Ready(item)).Min();

    // When the alarm may act on a message: its time, or later while a cancel or edit that failed holds it.
    private DateTimeOffset Ready(ScheduledSend item) =>
        heldAfterFailure.TryGetValue(item.Id, out var until) && until > item.Due ? until : item.Due;

    // Read from the clock each time, never from the pass's start: a pass can spend seconds per message.
    private bool IsLate(ScheduledSend item) => clock.GetUtcNow() - item.Due > SendWindow;
    private ScheduledSendReason LateReason(ScheduledSend item) => waited.Contains(item.Id) ? ScheduledSendReason.Offline : ScheduledSendReason.Late;

    private enum Readiness { Ready, Wait, Refused, OtherAccount }

    // What the composer asks before sending, asked the same way: a service still connecting is waited for.
    private async Task<Readiness> ReadyAsync(ScheduledSend item)
    {
        // Scheduled from one account, it never goes from another: held for the user instead. Not listed yet (the service
        // still loading) is waited for like a connection.
        // A message of a service whose account must be known, kept with none (before this was refused), is held for the user rather than sent from whichever account is there.
        if (item.Scope is null && conditions.TracksAccounts && conditions.ExpectsAccount(item.ChatId)) return Readiness.OtherAccount;
        if (item.Scope is { } expected && conditions.TracksAccounts)
        {
            var current = await conditions.AccountOfAsync(item.ChatId);
            if (current is null) return Readiness.Wait;
            if (current != expected) return Readiness.OtherAccount;
        }
        if (!conditions.IsConnected(item.ChatId)) return Readiness.Wait;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var permission = await conditions.GetSendPermissionAsync(item.ChatId, limit.Token);
            if (permission.Allowed) return item.Text.Length <= Math.Min(permission.MaxTextLength ?? 65536, 65536) ? Readiness.Ready : Readiness.Refused;
            return permission.Pending ? Readiness.Wait : Readiness.Refused;
        }
        catch (BackendException ex) when (ex.Waits) { return Readiness.Wait; }
        // A failed check is not a refusal: the composer calls it temporary, and it is asked again shortly.
        catch (Exception) { return Readiness.Wait; }
    }

    private async Task HoldAsync(ScheduledSend item, ScheduledSendReason reason)
    {
        waited.Remove(item.Id);
        heldAfterFailure.Remove(item.Id);
        Replace(item with { State = ScheduledSendState.NotSent, Reason = reason });
        await SaveAsync();
        Log("overdue_held", item, "reason=" + reason.ToString().ToLowerInvariant());
        Held?.Invoke(item);
        Changed?.Invoke(this, item.ChatId);
    }

    // Saved as sending before the outlet is called: from here on a run that ends is an interrupted send, never a resend.
    private async Task BeginSendAsync(ScheduledSend item)
    {
        var lateReason = LateReason(item);
        waited.Remove(item.Id);
        heldAfterFailure.Remove(item.Id);
        var sending = item with { State = ScheduledSendState.Sending, SendingAt = clock.GetUtcNow(), Attempt = NumbersAttempts ? item.Attempt + 1 : item.Attempt };
        Replace(sending);
        bool saved;
        try { saved = await SaveAsync(); }
        catch (Exception) { saved = false; }
        if (Find(item.Id) != sending) return;
        // Custody lost while Sending was being written (S9): the outlet is not called and nothing more is written. Saved as
        // sending, it stays sending, as a crash at this point would leave it. A load with custody (this engine's, or the
        // export to the new custodian) then records it as an interrupted send, never sent (S4). Putting it back to waiting
        // instead would need a write this engine may no longer make, and a copy taken as waiting could then be fired by the
        // new custodian as well as by this engine once custody came back. Not judged, so a pass with custody again loads
        // and settles it. Not saved, the store still holds it as it was, and only this run's copy goes back.
        if (await CustodyLostAsync())
        {
            if (saved) judged = false;
            else Replace(item);
            Log("not_handed_over", item, "reason=custody");
            Changed?.Invoke(this, item.ChatId);
            return;
        }
        // Not handed over after all: back to waiting, which it certainly still is. Unsaved as sending, a send now could be
        // sent again by a run that found it waiting after a crash; stopped, the next launch would call a message that was
        // never handed over an interrupted send.
        if (!saved || stopped)
        {
            Replace(item);
            try { await SaveAsync(); } catch (Exception) { /* It was saved as waiting before, or is unsaved either way. */ }
            if (!saved) Log("waiting", item, "reason=unsaved");
            return;
        }
        // Its window closed while it was being saved: not handed over, so held like any other late message.
        if (IsLate(item))
        {
            Replace(item);
            await HoldAsync(item, lateReason);
            return;
        }
        Log("due", item, $"late_s={(long)Math.Max(0, (clock.GetUtcNow() - item.Due).TotalSeconds)}");
        Changed?.Invoke(this, item.ChatId);
        Task send;
        // The message as it was saved as sending, so an outlet that names a hand-over (the host's: sched:<id>:<n>) names the one that was written down.
        try { send = outlet.SendAsync(sending); }
        catch (Exception ex) { send = Task.FromException(ex); }
        // The outlet refuses before submitting anything when too many sends are unsettled: then it certainly was not
        // sent, and it is held for the user rather than tried again by itself.
        if (send.IsFaulted && outlet.StateOf(item.Id) is null)
        {
            _ = send.Exception;
            if (Find(item.Id) is { } refused) await HoldAsync(refused, ScheduledSendReason.Refused);
            return;
        }
        // The outlet settles every outcome itself; what it decides reaches here through Changed.
        _ = send.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // Once the outlet has an answer for a scheduled message it is the outlet's, like any other send: accepted, refused
    // (Restore puts it back in the composer) or uncertain (an interrupted send if Messages closes). Its schedule ends.
    private async void OutletChanged(object? source, string localId)
    {
        if (Find(localId) is not { State: ScheduledSendState.Sending } item) return;
        var sent = outlet.StateOf(localId);
        if (sent is OutgoingState.Sending) return;
        // Certainly refused, on a host: the words stay, as a message not sent for the user to send again (a new attempt) or cancel.
        if (sent is OutgoingState.Rejected && KeepsRejected)
        {
            Replace(item with { State = ScheduledSendState.NotSent, Reason = ScheduledSendReason.Refused });
            Log("rejected_kept", item);
            Changed?.Invoke(this, item.ChatId);
            try { await SaveAsync(); }
            catch (Exception) { /* The next change saves again; until then a restart finds it sending and carries it as interrupted, never sent. */ }
            return;
        }
        var step = sent switch
        {
            OutgoingState.Pending => "sent",
            OutgoingState.Rejected => "rejected",
            OutgoingState.Unknown => "unknown",
            // Gone from the outbox with no answer seen here: the outbox was cleared (signing out), which ends its sends.
            _ => "cleared",
        };
        items.Remove(item);
        Log(step, item);
        Changed?.Invoke(this, item.ChatId);
        try { await SaveAsync(); }
        catch (Exception) { /* The next change saves again; the outbox's own record covers the send meanwhile. */ }
    }

    private void Replace(ScheduledSend item)
    {
        var index = items.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0) items[index] = item;
    }

    // True once the store holds it; false when there is nothing to hold it or the profile changed meanwhile.
    private async Task<bool> SaveAsync()
    {
        if (store is null) return true;
        if (!loaded || profile is null) return false;
        var records = items.ToArray();
        await writing.WaitAsync();
        try
        {
            // A different account on this PC keeps its own schedule; nothing carries across profiles.
            if (await store.ProfileAsync() != profile) return false;
            await store.WriteAsync(profile, records);
            return true;
        }
        finally { writing.Release(); }
    }

    // A one-way tag of the message and of its conversation, so a live run can be followed without naming anything.
    public static string Tag(string value) => NotificationIdentity.Hash("scheduled\n" + value)[..10].ToLowerInvariant();
    private void Log(string step, ScheduledSend item, string? detail = null) =>
        log?.Invoke($"scheduled-send {step} id={Tag(item.Id)} chat={Tag(item.ChatId)}{(detail is null ? "" : " " + detail)}");

    public void Dispose() { Stop(); outlet.Changed -= OutletChanged; }
}
