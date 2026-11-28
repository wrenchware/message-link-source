namespace MessageLink.Presentation.Services;

/// <summary>
/// The conversations looked at most recently, newest first, for the Ctrl+Tab switcher. Held in memory only: it is
/// never saved and never logged, because the order in which somebody reads their conversations is nobody else's business
/// and a launch starts a fresh list anyway.
/// </summary>
public sealed class RecentConversations(int capacity = 10)
{
    private readonly int capacity = Math.Max(1, capacity);
    private readonly List<string> order = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Order => [.. order];

    /// <summary>Moves the conversation to the front, once, dropping the oldest past the capacity.</summary>
    public void Visit(string displayId)
    {
        if (string.IsNullOrEmpty(displayId)) return;
        order.Remove(displayId);
        order.Insert(0, displayId);
        if (order.Count > capacity) order.RemoveRange(capacity, order.Count - capacity);
    }

    public void Forget(string displayId) => order.Remove(displayId);

    /// <summary>The order, keeping only the conversations the test says are still listed.</summary>
    public IReadOnlyList<string> Available(Func<string, bool> exists) => order.Where(exists).ToArray();

    /// <summary>
    /// The next index after moving <paramref name="delta"/> rows through <paramref name="count"/>, wrapping at both ends.
    /// Nothing to move through gives 0.
    /// </summary>
    public static int Step(int count, int index, int delta)
    {
        if (count <= 0) return 0;
        var next = (index + delta) % count;
        return next < 0 ? next + count : next;
    }
}
