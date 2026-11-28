namespace MessageLink.Presentation.Services;

/// <summary>
/// The Ctrl+Tab switcher's strip, as Alt+Tab lays out windows: one tile per recent conversation, left to right newest
/// first, as many as the window has room for (at most <see cref="MaxVisible"/>), and the rest reached by scrolling the
/// strip so the highlighted tile is always in view. Only the arithmetic lives here, so it can be checked without a window.
/// </summary>
public static class RecentSwitcherStrip
{
    public const double TileWidth = 112, TileHeight = 132, Spacing = 4;
    // The card's padding and its 1 px stroke, each side.
    public const double Inset = 12, Stroke = 1;
    public const int MaxVisible = 7;

    /// <summary>How many tiles show at once: every one up to the cap, fewer when the window is too narrow, never none.</summary>
    public static int Visible(int count, double availableWidth)
    {
        if (count <= 0) return 0;
        var room = double.IsFinite(availableWidth) ? (int)Math.Floor((availableWidth - 2 * (Inset + Stroke) + Spacing) / (TileWidth + Spacing)) : MaxVisible;
        return Math.Clamp(Math.Min(room, Math.Min(count, MaxVisible)), 1, MaxVisible);
    }

    /// <summary>The card's width for that many tiles at once: fixed while the highlight moves.</summary>
    public static double Width(int visible) =>
        visible <= 0 ? 2 * (Inset + Stroke) : visible * TileWidth + (visible - 1) * Spacing + 2 * (Inset + Stroke);

    public static double Height => TileHeight + 2 * (Inset + Stroke);

    /// <summary>
    /// The first tile in view once <paramref name="index"/> is highlighted: unchanged while it is already in view, otherwise
    /// moved just far enough to show it, as a list scrolls its selection into view. Wrapping from the last tile to the first
    /// therefore goes back to the start.
    /// </summary>
    public static int FirstVisible(int index, int first, int visible, int count)
    {
        if (count <= 0 || visible <= 0) return 0;
        first = Math.Clamp(first, 0, Math.Max(0, count - visible));
        if (index < 0 || index >= count) return first;
        if (index < first) return index;
        if (index >= first + visible) return index - visible + 1;
        return first;
    }

    /// <summary>The horizontal offset that puts <paramref name="first"/> at the strip's left edge.</summary>
    public static double Offset(int first) => Math.Max(0, first) * (TileWidth + Spacing);
}
