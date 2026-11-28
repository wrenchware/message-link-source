using MessageLink.Core.Messaging;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// What another device changed, reaching a client of a Messages Server's list (SWITCHBOARD.md stage 6, slice 4): a pin, mute,
// marked unread, archive here, snooze or name made elsewhere, and the pinned order. The rows come with this PC's settings
// already applied again (LocalPreferenceChatReader.ReapplyAsync); a burst of them is applied here at once, with one redraw.
public sealed partial class ChatListViewModel
{
    /// <summary>
    /// Takes this PC's settings from <paramref name="rows"/> onto the loaded conversations of the same id, reads the pinned
    /// order again when <paramref name="pinnedOrder"/>, and redraws once. A row not loaded is passed over (it is drawn with
    /// its settings when it is read), and nothing else of a loaded row changes: what it says and when, its unread count and
    /// its participants' own details stay what the last provider read made them.
    /// </summary>
    /// <param name="current">
    /// Whether a row is still what to apply, asked after the pinned order is read and with nothing awaited between it and the
    /// redraw: a provider read that lands while the order is read is newer than the copy the row was made from, and is kept.
    /// </param>
    /// <param name="regroup">
    /// Another device's merges or labels came (slice 5). They are already held where the list reads them from (MergedConversations,
    /// ChatLabels), and the redraw, the one this burst makes, builds the merged rows and the labels from them again.
    /// </param>
    /// <returns>How many loaded rows changed, and the rows taken (the others failed <paramref name="current"/>).</returns>
    public async Task<(int Changed, IReadOnlyList<ReappliedChat> Taken)> ApplySharedAsync(IReadOnlyList<ReappliedChat> rows, bool pinnedOrder,
        Func<ReappliedChat, bool>? current = null, bool regroup = false)
    {
        // Read again rather than merged: another device saved the order whole, and the last writer's order is the order.
        // Nothing is written back, so a remote order never echoes to the host.
        if (pinnedOrder) await EnsurePinnedOrderAsync(CancellationToken.None, reload: true);
        var taken = current is null ? rows : rows.Where(current).ToList();
        var byId = new Dictionary<string, ReappliedChat>(StringComparer.Ordinal);
        foreach (var row in taken) byId[row.Chat.Id] = row;
        var changed = 0;
        for (var i = 0; byId.Count > 0 && i < loaded.Count; i++)
        {
            if (!byId.TryGetValue(loaded[i].Id, out var row)) continue;
            var next = WithShared(loaded[i], row.Chat, row.Names);
            if (SameShared(loaded[i], next)) continue;
            loaded[i] = next;
            changed++;
        }
        if (changed == 0 && !pinnedOrder && !regroup) return (0, taken);
        Rebuild();
        if (changed > 0) SaveLoadedSoon();
        return (changed, taken);
    }

    /// <summary>
    /// <paramref name="chat"/> with the fields LocalPreferenceChatReader decides taken from <paramref name="from"/>: pin,
    /// mute and its end, marked unread, snooze, Instagram's archive here, and, when <paramref name="names"/>, the title and
    /// participants' names. The open conversation's header takes the same (ConversationViewModel.ApplyShared).
    /// </summary>
    /// <remarks>
    /// Any other conversation's archive is the service's alone, and <paramref name="from"/> was applied to the last copy the
    /// service gave, which can be older than this row: an archive just made here is on the row before the service echoes it.
    /// </remarks>
    public static ChatSummary WithShared(ChatSummary chat, ChatSummary from, bool names = true) => chat with
    {
        IsPinned = from.IsPinned, IsMuted = from.IsMuted, MutedUntil = from.MutedUntil, IsMarkedUnread = from.IsMarkedUnread,
        IsArchived = chat.Id.StartsWith("instagram:", StringComparison.Ordinal) ? from.IsArchived : chat.IsArchived,
        SnoozedUntil = from.SnoozedUntil, Title = names ? from.Title : chat.Title, Participants = names ? from.Participants : chat.Participants,
    };

    // Compared by what is shown: a name applied again is a new list of the same participants.
    private static bool SameShared(ChatSummary a, ChatSummary b) =>
        a.IsPinned == b.IsPinned && a.IsMuted == b.IsMuted && a.MutedUntil == b.MutedUntil && a.IsMarkedUnread == b.IsMarkedUnread &&
        a.IsArchived == b.IsArchived && a.SnoozedUntil == b.SnoozedUntil && a.Title == b.Title &&
        a.Participants.Select(person => person.DisplayName).SequenceEqual(b.Participants.Select(person => person.DisplayName));
}
