namespace MessageLink.Presentation.Services;

public readonly record struct PinnedDropRow(string ChatId, double Top, double Bottom);

// Dragging a pin: the dragged card takes the slot under the pointer and the pins between its old slot and that one slide
// one card toward the old slot, so the cards move around rather than a line marking the gap (owner, 2026-11-23).
// Rows are the realized pinned rows in list order, at their laid-out positions (without the slide); unpinned rows never
// take part. From and To index those rows; TargetId and After say the same move the way MovePinnedChat takes it, and
// TargetId is null while the pin is over its own slot.
public readonly record struct PinnedSlide(int From, int To, string? TargetId, bool After)
{
    public static PinnedSlide? Find(IReadOnlyList<PinnedDropRow> rows, string draggedId, double pointerY, double viewportHeight)
    {
        if (!double.IsFinite(pointerY) || pointerY < 0 || pointerY > viewportHeight) return null;
        var from = -1;
        for (var i = 0; i < rows.Count; i++) if (rows[i].ChatId == draggedId) { from = i; break; }
        if (from < 0) return null;
        // Above the first pin it goes first; below the last, over the unpinned rows, it stays last.
        var to = rows.Count - 1;
        for (var i = 0; i < rows.Count; i++) if (pointerY < rows[i].Bottom) { to = i; break; }
        return to == from ? new(from, to, null, false) : new(from, to, rows[to].ChatId, to > from);
    }

    // How far the row at index slides while the dragged pin is over To: one dragged card toward the dragged pin's old slot.
    public double Offset(int index, double draggedHeight) =>
        From < To && index > From && index <= To ? -draggedHeight
        : To < From && index >= To && index < From ? draggedHeight : 0;
}
