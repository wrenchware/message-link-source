namespace MessageLink.Presentation.Services;

// A browser-style trail of deliberate conversation opens, separate from Ctrl+Tab's most-recent-first list.
public sealed class ConversationNavigation(int limit = 100)
{
    private readonly List<string> visits = [];
    private int position = -1;
    private long revision;
    public string? Current => position >= 0 ? visits[position] : null;
    public IReadOnlyList<string> Visits => visits.AsReadOnly();
    public void Visit(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Current == id) return;
        if (position + 1 < visits.Count) visits.RemoveRange(position + 1, visits.Count - position - 1);
        visits.Add(id);
        if (visits.Count > Math.Max(2, limit)) visits.RemoveAt(0);
        position = visits.Count - 1;
        revision++;
    }
    public string? Peek(int direction, Func<string, bool> available) => Find(direction, available) is var next && next >= 0 ? visits[next] : null;
    public NavigationStep? Move(int direction, Func<string, bool> available)
    {
        var next = Find(direction, available);
        if (next < 0) return null;
        var before = position;
        position = next;
        return new(visits[next], before, ++revision);
    }
    public void RollBack(NavigationStep step)
    {
        // A newer list click owns navigation now; a failed older open must not move its cursor.
        if (step.Revision != revision) return;
        position = step.Before;
        revision++;
    }
    public void Clear() { visits.Clear(); position = -1; revision++; }
    private int Find(int direction, Func<string, bool> available)
    {
        if (direction is not (-1 or 1)) return -1;
        for (var next = position + direction; next >= 0 && next < visits.Count; next += direction)
            if (available(visits[next])) return next;
        return -1;
    }
}

public sealed record NavigationStep(string ChatId, int Before, long Revision);
