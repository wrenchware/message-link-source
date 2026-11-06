using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// Send later from the composer: the text in the box, and the message it replies to, kept for a chosen time instead of
// sent now (ScheduledSends). Nothing is sent from here; the words leave the box as the scheduled row appears.
public sealed partial class ComposerViewModel
{
    private readonly ScheduledSends? scheduledSends;

    // What the flyout needs: a conversation, words within its limit, and a schedule to keep them in. The send check is not
    // needed now; it is asked when the time comes.
    public bool CanSchedule => scheduledSends is not null && IsReady && editing is null && chatId is not null && !string.IsNullOrWhiteSpace(Text) && Text.Length <= maxLength;

    /// <summary>Schedules the box's text for <paramref name="due"/>. False, with the reason in Status, when it could not.</summary>
    public async Task<bool> ScheduleAsync(DateTimeOffset due)
    {
        if (!CanSchedule || scheduledSends is null || chatId is null) return false;
        var target = chatId; var body = Text;
        var reply = replies.TryGetValue(target, out var selectedReply) ? selectedReply : default;
        try { await scheduledSends.ScheduleAsync(target, body, due, reply.Id, reply.Preview); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Status = ex.Message; return false; }
        catch (Exception) { Status = "Couldn't schedule this message: Messages couldn't save it."; return false; }
        // The words go only once they are kept; typing that happened meanwhile stays, as it does for a send.
        if (reply.Id is not null && replies.TryGetValue(target, out var current) && current == reply) { replies.Remove(target); NotifyReply(); }
        ClearSubmittedText(target, body);
        await FlushDraftAsync(target);
        return true;
    }

    /// <summary>Edit on a scheduled row: its words and reply come back here and it is no longer scheduled.</summary>
    public async Task<bool> EditScheduledAsync(string id)
    {
        if (scheduledSends is null) return false;
        ScheduledSend? taken; string? unresolved = null;
        // An edit whose outcome could not be told hands the words back as a copy (the server's original may still send); the box takes them, and says so, rather than
        // presenting it as an edit that worked.
        try { taken = await scheduledSends.TakeForEditAsync(id); }
        catch (ScheduledEditUnresolvedException copy) { taken = copy.Copy; unresolved = copy.Message; }
        if (taken is not { } item) return false;
        // Its words are what was asked to come back to the box, so an edit of a sent message there ends first.
        if (item.ChatId == chatId && !IsSavingEdit) EndEdit();
        if (item.ReplyToId is not null && !replies.ContainsKey(item.ChatId))
        { replies[item.ChatId] = (item.ReplyToId, item.ReplyPreview ?? "Reply to an earlier message"); NotifyReply(); }
        RestoreSubmittedText(item.ChatId, item.Text);
        if (item.ChatId == chatId) ScheduleDraftSave();
        if (unresolved is not null && item.ChatId == chatId) Status = unresolved;
        return true;
    }
}
