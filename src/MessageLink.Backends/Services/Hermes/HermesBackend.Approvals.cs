using System.Collections.Concurrent;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Hermes;

// Hermes's approvals (owner, 2026-11-16). A terminal command Hermes's approvals hold arrives in the turn's own stream as approval.request, and Hermes waits
// (approvals.timeout, 300 s by default) for POST runs/{run}/approval before it refuses the command itself. Until protocol 32 the stream's reader dropped the
// event, so every held command waited out its time unseen. Now it is a message in the thread, answered from any of the owner's devices: Approve runs it once,
// Deny refuses it. "For this session" and "always" are never offered: they reach past the one command shown (owner, 2026-11-16). The command never enters a log.
//
// A request ends in one place, SettleAsync, from an answer, Hermes's deadline, the turn's end or a restart. Only an answer Hermes confirmed says approved or
// denied; an answer of unknown outcome that Hermes then stops waiting on is closed, never "didn't run" (code review, 2026-11-16).
internal sealed partial class HermesBackend
{
    internal const string ApprovalText = "Hermes needs your approval to run a command.";
    internal const string WaitingStatus = "Waiting for your approval";
    internal const string CutRefusal = "This command can't be shown in full here, so it can only be denied. To run it, approve it in Hermes.";
    /// <summary>The longest an answer waits for Hermes before its outcome is unknown.</summary>
    internal TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>How often the typing line is said again while a request waits: a client drops it after two minutes.</summary>
    internal TimeSpan WaitingRefresh { get; init; } = TimeSpan.FromSeconds(60);

    // Only while Hermes waits: what it takes to answer, by the request's message id. Never saved: Hermes stops waiting when the turn's stream ends.
    private readonly ConcurrentDictionary<string, Held> held = new(StringComparer.Ordinal);

    // Expires: when Hermes said it stops waiting; null when it didn't say (session authority leaves timeout_s out of an API turn's request, and its
    // approvals.timeout may be anything), and then only the turn's end closes it.
    private sealed class Held(string profile, string chatId, string messageId, HermesApprovalRequest request, DateTimeOffset? expires)
    {
        public string Profile { get; } = profile;
        public string ChatId { get; } = chatId;
        public string MessageId { get; } = messageId;
        public HermesApprovalRequest Request { get; } = request;
        public DateTimeOffset? Expires { get; } = expires;
        public readonly CancellationTokenSource Done = new();
        public int Answering;
        public volatile bool TurnOpen = true;
        public bool Past(DateTimeOffset now) => Expires is { } until && now >= until;
    }

    // A request in the stream: a message in the thread, told as new so it notifies, and a clock that closes it when Hermes stops waiting.
    private async Task<Held> HoldAsync(string profile, string chatId, HermesApprovalRequest request)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? expires = request.Wait is { } wait ? now + wait : null;
        var message = new HermesStoredMessage(NewMessageId(profile), now, false, ApprovalText, Kind: "approval", State: "done",
            Approval: new(request.Command, request.Description, AgentApprovalStates.Pending, expires, request.Advised, request.Cut));
        var entry = new Held(profile, chatId, message.Id, request, expires);
        held[message.Id] = entry;
        await UpdateAsync(profile, message).ConfigureAwait(false);
        log($"hermes approval held profile={Tag(profile)} wait_s={(expires is { } e ? ((long)(e - now).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unsaid")}"
            + $" advised={(request.Advised ? 1 : 0)} cut={(request.Cut ? 1 : 0)} generation={(request.Generation is null ? 0 : 1)}");
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [message.Id], true, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        Typing(chatId, WaitingStatus, stopped: false);
        _ = Task.Run(() => WaitOutAsync(entry));
        return entry;
    }

    private async Task WaitOutAsync(Held entry)
    {
        try
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(entry.Done.Token, stop.Token);
            while (true)
            {
                var left = entry.Expires is { } until ? until - clock.GetUtcNow() : WaitingRefresh;
                if (left <= TimeSpan.Zero) break;
                await Task.Delay(left < WaitingRefresh ? left : WaitingRefresh, clock, both.Token).ConfigureAwait(false);
                if (entry.TurnOpen && !entry.Past(clock.GetUtcNow()) && Volatile.Read(ref entry.Answering) == 0) Typing(entry.ChatId, WaitingStatus, stopped: false);
            }
            // Hermes refused it at its own deadline. An answer on its way now settles it, or leaves it to ReconcileAsync once it is back.
            await ReconcileAsync(entry).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { log($"hermes approval wait failed error={failure.GetType().Name}"); }
    }

    // The turn's stream ended: Hermes waits for nothing it held in it.
    private async Task TurnEndedAsync(IReadOnlyList<Held> turn)
    {
        foreach (var entry in turn)
        {
            entry.TurnOpen = false;
            await ReconcileAsync(entry).ConfigureAwait(false);
        }
    }

    // Closes a request Hermes no longer waits on (its deadline passed, or its turn ended), unless an answer is on its way: that answer's own end comes
    // back here, so whichever of the two finishes last settles it.
    private async Task ReconcileAsync(Held entry)
    {
        if (!held.ContainsKey(entry.MessageId) || Volatile.Read(ref entry.Answering) != 0) return;
        var now = clock.GetUtcNow();
        if (entry.Past(now)) await SettleAsync(entry, AgentApprovalStates.Expired, answered: false).ConfigureAwait(false);
        else if (!entry.TurnOpen) await SettleAsync(entry, AgentApprovalStates.Closed, answered: false).ConfigureAwait(false);
    }

    public async Task<string> AnswerApprovalAsync(string chatId, string messageId, bool approve, CancellationToken cancellationToken = default)
    {
        var profile = Profile(chatId);
        await LoadAsync().ConfigureAwait(false);
        if (HermesIds.ProfileOf(messageId) != profile) throw new ArgumentException("That request isn't in this conversation.");
        AgentApproval? stored;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { stored = Thread(profile).Messages.FirstOrDefault(m => m.Id == messageId)?.Approval; }
        finally { gate.Release(); }
        if (stored is null) throw new ArgumentException("That request isn't in this conversation.");
        if (!stored.Pending) return stored.State;
        if (!held.TryGetValue(messageId, out var entry))
        {
            // Pending in the thread with nothing to answer it by: Hermes's turn ended without the thread being told. Said as it stands.
            await SettleAsync(new(profile, chatId, messageId, new("", "", null, "", null, null, false), null), AgentApprovalStates.Closed, answered: false).ConfigureAwait(false);
            return AgentApprovalStates.Closed;
        }
        // Approving a command cut short would run what nobody saw: only Deny is taken for it.
        if (approve && entry.Request.Cut) throw new ArgumentException(CutRefusal);
        if (Interlocked.Exchange(ref entry.Answering, 1) == 1) throw new ArgumentException("An answer to this request is already on its way.");
        var started = clock.GetTimestamp();
        var choice = approve ? "once" : "deny";
        try
        {
            // Hermes may have stopped waiting while this answer was being asked for: nothing is sent then.
            if (entry.Past(clock.GetUtcNow()) || !entry.TurnOpen)
            {
                await SettleAsync(entry, entry.Past(clock.GetUtcNow()) ? AgentApprovalStates.Expired : AgentApprovalStates.Closed, answered: false).ConfigureAwait(false);
                return await HeldStateAsync(entry).ConfigureAwait(false);
            }
            var setting = settings.Profiles.FirstOrDefault(p => p.Name == profile);
            var address = HermesClient.ParseBase(settings.Address);
            if (setting is null || address is null) throw new ArgumentException("This Hermes profile isn't set up.");
            bool settled;
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token))
            {
                bounded.CancelAfter(AnswerTimeout);
                try { settled = await client.AnswerApprovalAsync(address, profile, setting.Key, entry.Request, approve, bounded.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new HermesException(HermesFailure.Unknown, cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"); }
            }
            var state = settled ? approve ? AgentApprovalStates.Approved : AgentApprovalStates.Denied
                : entry.Past(clock.GetUtcNow()) ? AgentApprovalStates.Expired : AgentApprovalStates.Closed;
            log($"hermes approval answered profile={Tag(profile)} choice={choice} outcome={state} ms={(long)clock.GetElapsedTime(started).TotalMilliseconds}");
            // Hermes said no to an answer after an earlier one of unknown outcome: whether the earlier one ran isn't known, so SettleAsync closes it.
            await SettleAsync(entry, state, answered: settled).ConfigureAwait(false);
            return await HeldStateAsync(entry).ConfigureAwait(false);
        }
        catch (HermesException failure) when (failure.Failure == HermesFailure.Unknown)
        {
            // The answer may have reached Hermes. Never sent again by itself; the person may answer again while Hermes still waits, which Hermes refuses if
            // the first one settled it.
            log($"hermes approval answered profile={Tag(profile)} choice={choice} outcome=unknown code={failure.Code}");
            await SettleAsync(entry, AgentApprovalStates.Unknown, answered: true).ConfigureAwait(false);
            return AgentApprovalStates.Unknown;
        }
        catch (HermesException failure)
        {
            log($"hermes approval answered profile={Tag(profile)} choice={choice} outcome=refused failure={failure.Failure} code={failure.Code}");
            throw new ArgumentException(failure.Failure switch
            {
                HermesFailure.Unreachable => "Hermes isn't reachable. Try again.",
                HermesFailure.KeyRejected => "Hermes rejected this profile's API key. Change it in Settings.",
                _ => "Hermes couldn't take that answer. Try again.",
            });
        }
        finally
        {
            Volatile.Write(ref entry.Answering, 0);
            // The deadline or the turn's end may have come while this answer was out; they left the request to this.
            await ReconcileAsync(entry).ConfigureAwait(false);
        }
    }

    // How the request stands in the thread now: what an answer reports back.
    private async Task<string> HeldStateAsync(Held entry)
    {
        await ReconcileAsync(entry).ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { return Thread(entry.Profile).Messages.FirstOrDefault(m => m.Id == entry.MessageId)?.Approval?.State ?? AgentApprovalStates.Closed; }
        finally { gate.Release(); }
    }

    // Writes how the request stands. A state that came from no answer never replaces one that did. One that ends the wait on an answer of unknown outcome
    // is "closed", never "expired": the answer may have run the command, so nothing may say it didn't. A final state ends the wait.
    private async Task SettleAsync(Held entry, string state, bool answered)
    {
        bool changed = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var thread = Thread(entry.Profile);
            var at = thread.Messages.ToList().FindIndex(m => m.Id == entry.MessageId);
            if (at >= 0 && thread.Messages[at].Approval is { } approval && (answered || approval.Pending))
            {
                if (!answered || state is AgentApprovalStates.Closed or AgentApprovalStates.Expired)
                    if (approval.State == AgentApprovalStates.Unknown) state = AgentApprovalStates.Closed;
                if (approval.State != state)
                {
                    var list = thread.Messages.ToList();
                    list[at] = list[at] with { Approval = approval with { State = state } };
                    threads[entry.Profile] = thread with { Messages = list };
                    await store.SaveThreadAsync(threads[entry.Profile]).ConfigureAwait(false);
                    changed = true;
                }
            }
            if (state != AgentApprovalStates.Unknown && held.TryRemove(entry.MessageId, out var removed)) removed.Done.Cancel();
        }
        finally { gate.Release(); }
        if (!changed) return;
        if (!answered) log($"hermes approval {state} profile={Tag(entry.Profile)}");
        realtime.TryWrite(new(RealtimeKind.MessageChanged, entry.ChatId, [entry.MessageId], false, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [entry.ChatId], null, clock.GetUtcNow()));
        // Hermes works on (or says why it didn't run it) while its turn lasts.
        if (state != AgentApprovalStates.Unknown && entry.TurnOpen && !held.Values.Any(h => h.ChatId == entry.ChatId)) Typing(entry.ChatId, null, stopped: false);
    }

    // A request still pending in a saved thread outlived its turn (the server stopped while Hermes waited): closed, and the server's history told.
    private HermesThread CloseStale(HermesThread thread, List<string> closed)
    {
        if (!thread.Messages.Any(m => m.Approval?.Pending == true)) return thread;
        return thread with
        {
            Messages = [.. thread.Messages.Select(m =>
            {
                if (m.Approval is not { Pending: true } approval) return m;
                closed.Add(m.Id);
                return m with { Approval = approval with { State = AgentApprovalStates.Closed } };
            })],
        };
    }
}
