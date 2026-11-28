namespace MessageLink.Presentation.Services;

/// <summary>
/// Alt+Shift+Down and Alt+Shift+Up: the next conversation, in list order, that is waiting to be read. Something marked
/// unread on this PC counts, because that mark is exactly a note to come back to it. A muted conversation counts too
/// (the user's decision, 2026-10-01): muting quiets its notifications, it does not take its messages off the reading
/// list. It never wraps round to the top, so reaching the end is something the user is told rather than a jump back
/// to where they started.
/// </summary>
public static class UnreadNavigation
{
    /// <param name="rows">The list's rows in order.</param>
    /// <param name="current">The selected row, or null when nothing is selected.</param>
    /// <param name="direction">+1 down the list, -1 up it.</param>
    /// <returns>The row to go to, or null when there is no further one that way.</returns>
    public static string? Next(IReadOnlyList<(string Id, int Unread, bool MarkedUnread)> rows, string? current, int direction)
    {
        if (rows.Count == 0 || direction == 0) return null;
        var step = direction > 0 ? 1 : -1;
        var at = -1;
        if (current is not null)
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Id == current) { at = i; break; }
        // Nothing selected (or the selection is not in this view): down starts above the first row, up below the last.
        var index = at >= 0 ? at + step : step > 0 ? 0 : rows.Count - 1;
        for (; index >= 0 && index < rows.Count; index += step)
        {
            var row = rows[index];
            if (row.Id == current) continue;
            if (row.Unread > 0 || row.MarkedUnread) return row.Id;
        }
        return null;
    }
}
