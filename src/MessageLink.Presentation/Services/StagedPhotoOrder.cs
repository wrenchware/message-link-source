namespace MessageLink.Presentation.Services;

public enum StagedDragStep { None, Begin, Drag, Drop, Cancel }

// One press on a staged photo, from the press to the drop, free of Windows types so the harness can play the pointer's
// events in the order Windows sends them. A press becomes a drag once it has moved the threshold along the strip.
//
// Only the tile losing the pointer cancels a drag. The picture inside the tile is a button, which takes the pointer on
// the press and lets go of it on the release before the release reaches the tile; that let-go used to be read as the
// drag being taken away, so every drop was cancelled a moment before it landed and the photo went back.
public sealed class StagedPhotoDrag(double threshold)
{
    private uint? pointer;
    private double startX;
    public bool IsPressed => pointer is not null;
    public bool IsDragging { get; private set; }

    public void Press(uint id, double x) { pointer = id; startX = x; IsDragging = false; }
    public void Reset() { pointer = null; IsDragging = false; }
    public double Offset(double x) => x - startX;

    // Begin once the press has moved far enough; the caller says Began when the drag has really started, or Resets.
    public StagedDragStep Move(uint id, double x, bool leftPressed)
    {
        if (pointer != id) return StagedDragStep.None;
        if (IsDragging) return StagedDragStep.Drag;
        if (!leftPressed) { Reset(); return StagedDragStep.None; }
        return Math.Abs(x - startX) < threshold ? StagedDragStep.None : StagedDragStep.Begin;
    }
    public void Began() { if (pointer is not null) IsDragging = true; }

    // A release of a press that never moved is the picture's click, which is not the drag's to handle.
    public StagedDragStep Release(uint id)
    {
        if (pointer != id) return StagedDragStep.None;
        var dragging = IsDragging;
        Reset();
        return dragging ? StagedDragStep.Drop : StagedDragStep.None;
    }

    // byTile: the tile itself lost the pointer (another window, the system, Escape). A part of the tile letting go is not
    // the drag ending, and neither is a cancel of the press before it became a drag, which leaves the click to the button.
    public StagedDragStep Lost(uint id, bool byTile)
    {
        if (pointer != id || !byTile) return StagedDragStep.None;
        var dragging = IsDragging;
        Reset();
        return dragging ? StagedDragStep.Cancel : StagedDragStep.None;
    }
}

// Photos waiting in the composer, in the order they are shown and therefore sent: the batch send and the one-by-one
// send both read the draft list front to back, so moving a photo here is the only thing that decides where it lands in
// the conversation. The arithmetic for dragging a tile along the strip lives here too, free of Windows types, so the
// offline harness can check it.
public static class StagedPhotoOrder
{
    // One photo from one place to another, the rest keeping their order around it. Nothing changes, and false comes
    // back, for a place outside the list or a move to where it already is.
    public static bool Move<T>(IList<T> items, int from, int to)
    {
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to) return false;
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        return true;
    }

    // How far a tile may be dragged from its own slot: no further than the first and last slots, so it never leaves
    // the strip that clips it.
    public static double Clamp(int from, double dx, double pitch, int count)
    {
        if (count <= 0 || from < 0 || from >= count || !(pitch > 0) || !double.IsFinite(pitch) || !double.IsFinite(dx)) return 0;
        return Math.Clamp(dx, -from * pitch, (count - 1 - from) * pitch);
    }

    // The slot a tile dragged dx from slot `from` would drop into: the one it is more than half way over. Rounded away
    // from zero at exactly half, so dragging left and right behave alike.
    public static int DropIndex(int from, double dx, double pitch, int count)
    {
        if (count <= 0) return 0;
        from = Math.Clamp(from, 0, count - 1);
        if (!(pitch > 0) || !double.IsFinite(pitch) || !double.IsFinite(dx)) return from;
        var moved = (int)Math.Round(Clamp(from, dx, pitch, count) / pitch, MidpointRounding.AwayFromZero);
        return Math.Clamp(from + moved, 0, count - 1);
    }

    // Where the tile in slot `index` stands aside to while another is dragged from `from` towards `to`: one slot
    // back towards the gap the dragged tile left, or where it is. The dragged tile itself follows the pointer instead.
    public static double Shift(int index, int from, int to, double pitch)
    {
        if (index == from || from == to) return 0;
        if (from < to && index > from && index <= to) return -pitch;
        if (to < from && index >= to && index < from) return pitch;
        return 0;
    }

    // What a screen reader says for a tile: its name, and where it stands among several.
    public static string TileName(string name, int index, int count) =>
        count > 1 && index >= 0 && index < count ? $"{name}, {index + 1} of {count}" : name;

    // How a tile can be used from the keyboard, said once after its name.
    public static string TileHelp(bool viewable, bool movable) =>
        string.Join(" ", new[]
        {
            viewable ? "Enter to view." : null,
            movable ? "Alt+Left or Alt+Right to move." : null,
            "Delete to remove.",
        }.Where(part => part is not null));

    // What a screen reader hears once a photo has moved.
    public static string MovedAnnouncement(int index, int count) => $"Moved to {index + 1} of {count}";

    // A thumbnail can grow into the full photo only when it shows the photo's own shape. A thumbnail of another shape
    // (a squeezed decode, or the icon Windows hands back when it cannot read a format) would land on a picture that
    // is not the one it grew as, so the photo opens without the movement instead.
    public static bool MatchesAspect(double pixelWidth, double pixelHeight, double? width, double? height, double tolerance = 0.03)
    {
        if (!(pixelWidth > 0) || !(pixelHeight > 0) || width is not > 0 || height is not > 0) return false;
        if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight) || !double.IsFinite(width.Value) || !double.IsFinite(height.Value)) return false;
        var shown = pixelWidth / pixelHeight;
        var real = width.Value / height.Value;
        return Math.Abs(shown / real - 1) <= tolerance;
    }

    // The size a pasted photo's thumbnail is decoded at: its shorter side 160 pixels, enough to fill a 64-pixel tile at
    // 250% scaling, and the longer side left to follow the photo's own shape. Zero leaves that side to follow. The
    // clipboard thumbnail used to be decoded at 240 by 160 whatever the photo's shape, which squeezed a tall
    // screenshot into a wide tile.
    public static (int Width, int Height) PreviewDecode(double? width, double? height)
    {
        if (width is not > 0 || height is not > 0 || !double.IsFinite(width.Value) || !double.IsFinite(height.Value)) return (240, 0);
        return width.Value >= height.Value ? (0, 160) : (160, 0);
    }
}
