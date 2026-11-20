namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// A ceiling on the conversation reads clients ask for (<c>ui.messages</c>, <c>ui.chat</c>): each is a real request on the owner's account, and the
/// background budget (<see cref="InstagramReadBudget"/>) only covers reads nobody asked for. Far above what a person scrolling and opening conversations
/// makes; it is there so a client caught in a loop, or one that misbehaves, can't send Instagram an unbounded stream of reads. In memory: a restart
/// starts it over, which the background budget's file is for.
/// </summary>
internal sealed class InstagramAskedReads(int perMinute = InstagramAskedReads.PerMinute, int perHour = InstagramAskedReads.PerHour)
{
    public const int PerMinute = 60, PerHour = 600;
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> taken = new();
    private DateTimeOffset loggedAt = DateTimeOffset.MinValue;

    /// <summary>Takes one read, or false when the last minute or hour already holds its share. Logs the first refusal in each minute.</summary>
    public bool TryTake(DateTimeOffset now, string kind)
    {
        bool log;
        lock (gate)
        {
            while (taken.Count > 0 && now - taken.Peek() >= TimeSpan.FromHours(1)) taken.Dequeue();
            var lastMinute = taken.Count(time => now - time < TimeSpan.FromMinutes(1));
            if (taken.Count < perHour && lastMinute < perMinute) { taken.Enqueue(now); return true; }
            log = now - loggedAt >= TimeSpan.FromMinutes(1);
            if (log) loggedAt = now;
        }
        if (log) InstagramLog.Write($"asked reads ceiling kind={kind} per_minute={perMinute} per_hour={perHour}");
        return false;
    }
}
