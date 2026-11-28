using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

public enum ReadOutcome { Complete, NothingUnread, Unavailable, Failed }

/// <summary>
/// Marks a conversation read without opening it, only because the user asked (the console's /read). Each member is
/// marked through its newest incoming message the way a notification's Mark read and an open merged conversation do:
/// through the read router's own <see cref="IReadStateWriter.CanMarkReadAsync"/>, so every backend rule (Instagram
/// spam, an account that is not connected) still holds, with the conversation list shown read while the marker is
/// written and put back if it fails. Nothing is ever retried: a failed or uncertain marker is reported, and the user
/// can ask again.
/// </summary>
/// <remarks>Runs on the interface thread: the list's read bookkeeping is interface state, and every await here
/// resumes on the context it started on.</remarks>
/// <param name="mergedSnoozeCleared">Told a merged conversation's id when reading it ends its snooze, so the saved
/// reminder can be cleared as reading it in its window does. Optional; the merge's own snooze is cleared either way.</param>
public sealed class ExplicitRead(IReadStateWriter reads, ChatListViewModel chats, MergedConversations merges,
    Func<string, Task>? mergedSnoozeCleared = null)
{
    public async Task<ReadOutcome> MarkAsync(ChatTarget target, Func<string, ChatMessage?> newestIncoming, CancellationToken ct = default)
    {
        var current = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var chat in chats.AllLoadedChats) current.TryAdd(chat.Id, chat);
        // The list's own copy where it has one: a member snapshot can be older than the row the user is looking at.
        var members = target.Members.Select(m => current.GetValueOrDefault(m.Id) ?? m).DistinctBy(m => m.Id)
            .Where(m => !MergedConversations.IsMerged(m.Id)).ToArray();
        var group = MergedConversations.IsMerged(target.DisplayId) ? merges.Find(target.DisplayId) : null;
        var unread = members.Where(m => m.UnreadCount > 0 || m.IsMarkedUnread).ToArray();
        if (unread.Length == 0 && group?.MarkedUnread != true) return ReadOutcome.NothingUnread;

        int done = 0, unavailable = 0;
        var failed = false;
        foreach (var member in unread)
        {
            ct.ThrowIfCancellationRequested();
            if (Through(member, newestIncoming(member.Id)) is not { } message)
            {
                // Nothing incoming to mark through. A Mark unread set on this PC is still undone here, as reading does.
                if (member.IsMarkedUnread) { chats.ApplyPendingRead(member.Id, null); done++; }
                else unavailable++;
                continue;
            }
            // The row is shown read only up to the activity this marker covers; anything newer stays unread.
            var boundary = member.LastActivity <= message.Timestamp ? member.LastActivity : null;
            long attempt = 0;
            var confirmed = false;
            try
            {
                if (!await reads.CanMarkReadAsync(member.Id, ct)) { unavailable++; continue; }
                attempt = chats.ApplyPendingRead(member.Id, boundary);
                await reads.MarkReadAsync(member.Id, message.Id, ct);
                chats.ApplyAcknowledgedRead(member.Id, boundary, attempt);
                confirmed = true; done++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // One unavailable account does not stop the others being read; the outcome still says it failed.
            catch (Exception) { failed = true; }
            finally { if (!confirmed && attempt != 0) chats.CancelPendingRead(member.Id, boundary, attempt); }
        }

        // A merged conversation's own Mark unread and snooze live in the merge, and end when it is read, as they do when
        // it is read in its window.
        if (group is not null && (done > 0 || unread.Length == 0) && (group.MarkedUnread || group.SnoozedUntil is not null))
        {
            try
            {
                await merges.SetAsync(group.Id, g => g with { MarkedUnread = false, SnoozedUntil = null });
                if (mergedSnoozeCleared is not null) await mergedSnoozeCleared(group.Id);
                if (unread.Length == 0) done++;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { failed = true; }
        }
        return failed ? ReadOutcome.Failed : done > 0 ? ReadOutcome.Complete : ReadOutcome.Unavailable;
    }

    // The newest incoming message this member can be marked through: what the console has seen, or the list's own last
    // line when that is incoming. A message still being sent from here has no provider id to mark through.
    public static ChatMessage? Through(ChatSummary member, ChatMessage? seen)
    {
        static bool Usable(ChatMessage? m) => m is { IsOutgoing: false, IsHidden: false } &&
            !string.IsNullOrEmpty(m.Id) && !m.Id.StartsWith("local:", StringComparison.Ordinal);
        // A message seen elsewhere must be this member's own; the row's preview is by definition.
        var a = Usable(seen) && seen!.ChatId == member.Id ? seen : null;
        var b = Usable(member.Preview) ? member.Preview : null;
        if (a is null) return b;
        if (b is null) return a;
        return b.Timestamp > a.Timestamp ? b : a;
    }
}
