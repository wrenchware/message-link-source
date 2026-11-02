using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Independent of any window, conversation selection or UI outbox. One persisted claim per action.
public sealed class NotificationActionRouter(INotificationStore store, IAccessTokenStore tokens,
    IMessageSender sender, IReadStateWriter reads, INotificationPresenter presenter, IChatSnoozes snoozes, MergedConversations? merges = null,
    ILocalChatPreferences? localPreferences = null, INotificationAccountScope? accountScopes = null,
    // Hands a snooze made here to Windows when Messages is closed (NotificationSnoozeService.HandOffAsync). Appended.
    NotificationSnoozeService? snoozeReminders = null)
{
    /// <summary>
    /// Puts the words of a reply certainly not sent in their conversation's draft, answering whether they are there: typing them again was the only way back (audit, 2026-11-01). Set by a
    /// window. A process with none, started only for this action, says what it always has. Never for an uncertain outcome: words in a draft read as unsent, and could go twice.
    /// </summary>
    public Func<string, string, Task<bool>>? KeepNotSentWords { get; set; }

    public async Task<NotificationRecord?> OpenAsync(string id)
    {
        if (!NotificationIdentity.IsValidId(id)) return null;
        var record = await store.UpdateAsync(id, item => (item, item));
        if (record is null || record.Version != 1) return null;
        if (!await AccountCurrentAsync(record)) return null;
        // Older builds retained failed reply text for manual recovery. Notification actions still keep their persisted
        // state to prevent duplicate submission, but the text itself is no longer stored.
        if (record.ReplyText is not null)
            record = await store.UpdateAsync(id, prior =>
            {
                var updated = prior! with { ReplyText = null };
                return (updated, updated);
            });
        if (!record.IsTest && record.ReplyState == NotificationActionState.Accepted && record.PendingId is not null)
        {
            try
            {
                var message = await sender.ResolveMessageAsync(record.ChatId, record.PendingId);
                var state = message.Delivery.SendState switch
                { "SUCCESS" => NotificationActionState.Complete, "FAIL_RETRIABLE" or "FAIL_PERMANENT" => NotificationActionState.Failed, _ => NotificationActionState.Accepted };
                record = await store.UpdateAsync(id, prior =>
                {
                    var updated = prior! with { ReplyState = state, ReplyText = null };
                    return (updated, updated);
                });
            }
            catch (Exception) { /* Accepted remains pending when lookup cannot resolve it. */ }
        }
        return await AccountCurrentAsync(record) ? record : null;
    }
    // One hour, the same from every notification. A snooze button that asked how long would be slower than
    // opening the conversation; choosing the moment is the conversation menu's job.
    public static readonly TimeSpan SnoozeDuration = TimeSpan.FromHours(1);
    // Raised when a notification's Snooze button quiets a conversation, so an open window can show it without
    // waiting for the conversation list to be read again.
    public event EventHandler<ChatSnooze>? Snoozed;
    public event EventHandler<string>? Muted;
    public async Task HandleAsync(string id, string action, string? text)
    {
        if (action is not ("reply" or "read" or "snooze" or "mute") || !NotificationIdentity.IsValidId(id)) return;
        var record = await OpenAsync(id);
        if (record is null) return;
        if (DateTimeOffset.UtcNow - record.CreatedAt > TimeSpan.FromDays(7))
        { await presenter.ShowResultAsync(record, "This notification action has expired. Open the conversation to continue."); return; }
        if (action == "mute")
        {
            if (record.IsTest) { await presenter.ShowResultAsync(record, "Test mute received. No conversation was changed."); return; }
            if (merges is not null) await merges.EnsureLoadedAsync();
            if (!await AccountCurrentAsync(record)) return;
            var target = merges?.DisplayId(record.ChatId) ?? record.ChatId;
            // Until turned off, as for one conversation, replacing a timed mute still running.
            if (MergedConversations.IsMerged(target)) await merges!.SetMutedAsync(target, true, null);
            else if (localPreferences is not null) await localPreferences.SetAsync(record.Profile, target, ChatSetting.Muted, true);
            else { await presenter.ShowResultAsync(record, "Open the conversation to mute its notifications."); return; }
            Muted?.Invoke(this, target);
            await presenter.RemoveAsync(record.Id);
            return;
        }
        if (action == "snooze")
        {
            // Nothing is sent, read or changed on the account: the conversation is held quiet here, and one
            // reminder is raised when the hour is up.
            await presenter.RemoveAsync(record.Id);
            if (record.IsTest) { await presenter.ShowResultAsync(record, "Test snooze received. No conversation was changed."); return; }
            var until = DateTimeOffset.UtcNow + SnoozeDuration;
            if (merges is not null) await merges.EnsureLoadedAsync();
            if (!await AccountCurrentAsync(record)) return;
            var target = merges?.DisplayId(record.ChatId) ?? record.ChatId;
            await snoozes.SetAsync(record.Profile, target, until);
            if (MergedConversations.IsMerged(target)) await merges!.SetAsync(target, g => g with { SnoozedUntil = until });
            Snoozed?.Invoke(this, new(record.Profile, target, until));
            // With Messages closed nothing else would hand its reminder to Windows before this process ends. The snooze is
            // made whatever happens here; a reminder Windows could not take comes when Messages next runs.
            if (snoozeReminders is not null)
            {
                try { await snoozeReminders.HandOffAsync(record, new(record.Profile, target, until)); }
                catch (Exception) { /* Left to the next run, as before. */ }
            }
            return;
        }
        if (action == "reply" && (!record.CanReply || string.IsNullOrWhiteSpace(text) || text.Length > 65536))
        { await presenter.ShowResultAsync(record, "Reply not sent. Open the conversation to review the message."); return; }
        if (action == "read" && !record.CanRead) return;
        var claimed = await store.UpdateAsync(id, prior =>
        {
            if (prior is null) return (prior, false);
            var state = action == "reply" ? prior.ReplyState : prior.ReadState;
            if (state != NotificationActionState.None) return (prior, false);
            return (action == "reply" ? prior with { ReplyState = NotificationActionState.Working, ReplyText = null }
                : prior with { ReadState = NotificationActionState.Working }, true);
        });
        if (!claimed) return; // A prior process may have dispatched already. Never resubmit.
        record = (await store.UpdateAsync(id, prior => (prior, prior)))!;
        var stateAfter = NotificationActionState.Unknown;
        string result;
        string? pendingId = null;
        try
        {
            // A cold activation must not operate on a newly replaced connection.
            if (!await AccountCurrentAsync(record)) throw new SendException(false, "Connection changed.");
            if (record.IsTest)
            {
                stateAfter = NotificationActionState.Complete;
                result = action == "reply" ? "Test reply received. No message was sent." : "Test Mark read received. No chat was changed.";
            }
            else if (action == "read")
            {
                if (!await reads.CanMarkReadAsync(record.ChatId)) throw new InvalidOperationException("Mark read is unavailable.");
                if (!await AccountCurrentAsync(record)) throw new InvalidOperationException("Connection changed.");
                await reads.MarkReadAsync(record.ChatId, record.MessageId);
                stateAfter = NotificationActionState.Complete; result = "Marked read through this message.";
            }
            else
            {
                var receipt = await sender.SendTextAsync(record.ChatId, text!);
                pendingId = receipt.PendingMessageId; stateAfter = NotificationActionState.Accepted;
                result = "Reply accepted. Delivery is pending.";
            }
        }
        catch (SendException ex)
        {
            stateAfter = ex.OutcomeUnknown ? NotificationActionState.Unknown : NotificationActionState.Failed;
            result = ex.OutcomeUnknown ? "Reply outcome unknown. Open the conversation and check history before sending again."
                : await KeptAsDraftAsync(record, text) ? "Reply not sent. Your words are in its draft."
                : "Reply not sent. Open the conversation to try again.";
        }
        catch (Exception)
        { result = action == "read" ? "Could not confirm Mark read. Open the conversation to check its state."
            : "Reply outcome unknown. Open the conversation and check history before sending again."; }
        // If this write fails, the Working claim remains. A repeated activation still cannot resend it.
        record = await store.UpdateAsync(id, prior =>
        {
            var updated = action == "reply" ? prior! with { ReplyState = stateAfter, PendingId = pendingId, ReplyText = null }
                : prior! with { ReadState = stateAfter };
            return (updated, updated);
        });
        // Success needs no second notification: an accepted reply shows its delivery in the conversation, and a
        // "delivery pending" toast arrived even after the message had been delivered. Tests and problems still report.
        if (!record.IsTest && stateAfter is NotificationActionState.Accepted or NotificationActionState.Complete)
        { await presenter.RemoveAsync(record.Id); return; }
        await presenter.ShowResultAsync(record, result);
    }
    private async Task<bool> KeptAsDraftAsync(NotificationRecord record, string? words)
    {
        if (KeepNotSentWords is not { } keep || string.IsNullOrWhiteSpace(words)) return false;
        // The draft is filed under the account signed in now: another account's words aren't its to find, in a thread with one id under each.
        try { return await AccountCurrentAsync(record) && await keep(record.ChatId, words); }
        catch (Exception) { return false; }
    }
    private async Task<bool> AccountCurrentAsync(NotificationRecord record) => record.IsTest ||
        record.Profile == await NotificationIdentity.ProfileAsync(tokens) &&
        (accountScopes is null || record.SourceScope is not null && record.SourceScope == await accountScopes.ForChatAsync(record.ChatId));
}
