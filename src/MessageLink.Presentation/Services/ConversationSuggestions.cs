using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public enum ConversationActivityKind { Sent, Opened }

// One thing you did in a conversation on this PC: sent to it, or opened it. Only the conversation and the moment; never a
// word of what was written.
public sealed record ActivityEvent(string ChatId, DateTimeOffset At, ConversationActivityKind Kind);

/// <summary>
/// What the taskbar jump list's "Suggested" conversations are drawn from: this PC's own record of the conversations you
/// sent to and opened here, for <see cref="ConversationSuggestions.Window"/>. Kept encrypted by the app (PrivateState).
/// Switching back and forth between two conversations is one open each, not dozens: an open within
/// <see cref="OpenRepeat"/> of the last one in the same conversation is not counted again.
/// </summary>
public sealed class ConversationActivityLog
{
    public static readonly TimeSpan OpenRepeat = TimeSpan.FromMinutes(10);
    private readonly List<ActivityEvent> events = [];

    public ConversationActivityLog(IEnumerable<ActivityEvent>? saved = null, DateTimeOffset? now = null)
    {
        if (saved is not null) events.AddRange(ConversationSuggestions.Trim(saved, now ?? DateTimeOffset.Now));
    }

    public IReadOnlyList<ActivityEvent> Events => events;

    /// <summary>Records the event; false when it was not counted (an open repeated too soon, or no conversation).</summary>
    public bool Add(ActivityEvent item)
    {
        if (string.IsNullOrEmpty(item.ChatId)) return false;
        if (item.Kind == ConversationActivityKind.Opened &&
            events.LastOrDefault(e => e.Kind == ConversationActivityKind.Opened && e.ChatId == item.ChatId) is { } last &&
            item.At - last.At < OpenRepeat) return false;
        events.Add(item);
        if (events.Count > ConversationSuggestions.MaxEvents * 5 / 4)
        {
            var kept = ConversationSuggestions.Trim(events, item.At);
            events.Clear();
            events.AddRange(kept);
        }
        return true;
    }

    public IReadOnlyList<ActivityEvent> Snapshot(DateTimeOffset now) => ConversationSuggestions.Trim(events, now);

    /// <summary>The saved record, read after this run may already have added to it: both are kept, in time order.</summary>
    public void Load(IEnumerable<ActivityEvent> saved, DateTimeOffset now)
    {
        var merged = ConversationSuggestions.Trim(saved.Concat(events), now);
        events.Clear();
        events.AddRange(merged);
    }

    public void Clear() => events.Clear();
}

/// <summary>
/// The jump list's suggestions: the conversations you message most, counting the last few days most and the time of day
/// it is now. Each send counts 1 and each open <see cref="OpenWeight"/>, halving every <see cref="HalfLifeDays"/> days; a
/// send at about this hour counts up to twice, and one on the same kind of day (weekday or weekend) a quarter more. Until
/// there is enough of a record (a new install, a quiet month), the most recently active conversations fill the rest.
/// Pinned conversations are already in the list and are never suggested.
/// </summary>
public static class ConversationSuggestions
{
    public const int Count = 3;
    public const int MaxEvents = 2000;
    public static readonly TimeSpan Window = TimeSpan.FromDays(42);
    public const double HalfLifeDays = 7;
    public const double OpenWeight = 0.25;
    private const double HourSpread = 1.5;
    // A clock that moved back a little (a time sync) must not drop what was just recorded.
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<ActivityEvent> Trim(IEnumerable<ActivityEvent> events, DateTimeOffset now) =>
        events.Where(e => !string.IsNullOrEmpty(e.ChatId) && e.At <= now + Skew && now - e.At <= Window)
            .OrderBy(e => e.At).TakeLast(MaxEvents).ToArray();

    /// <param name="displayId">The conversation a member is shown as (a merged conversation's own id), or the id itself.</param>
    /// <param name="exclude">Conversations already in the list: the pinned ones.</param>
    public static IReadOnlyList<ChatSummary> Rank(IReadOnlyList<ActivityEvent> events, IReadOnlyList<ChatSummary> candidates,
        Func<string, string> displayId, IReadOnlySet<string> exclude, DateTimeOffset now, int count = Count)
    {
        var eligible = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var chat in candidates)
            if (Eligible(chat) && !exclude.Contains(chat.Id)) eligible.TryAdd(chat.Id, chat);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item.At > now + Skew || now - item.At > Window) continue;
            var id = displayId(item.ChatId);
            if (!eligible.ContainsKey(id)) continue;
            scores[id] = scores.GetValueOrDefault(id) + Weight(item, now);
        }
        var ranked = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => eligible[pair.Key]);
        var recent = eligible.Values.Where(chat => !scores.ContainsKey(chat.Id) && !chat.IsReadOnly && chat.LastActivity is { } at && now - at <= Window)
            .OrderByDescending(chat => chat.LastActivity).ThenBy(chat => chat.Id, StringComparer.Ordinal);
        return ranked.Concat(recent).Take(Math.Max(0, count)).ToArray();
    }

    public static double Weight(ActivityEvent item, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - item.At).TotalDays);
        var recency = Math.Pow(0.5, age / HalfLifeDays);
        var local = item.At.ToOffset(now.Offset);
        var hours = Math.Abs(local.TimeOfDay.TotalHours - now.TimeOfDay.TotalHours);
        hours = Math.Min(hours, 24 - hours);
        var hour = 1 + Math.Exp(-hours * hours / (2 * HourSpread * HourSpread));
        var day = IsWeekend(local.DayOfWeek) == IsWeekend(now.DayOfWeek) ? 1.25 : 1;
        return (item.Kind == ConversationActivityKind.Sent ? 1 : OpenWeight) * recency * hour * day;
    }

    // The candidates are the rows the inbox shows (ChatListViewModel.InboxChats); this keeps out anything archived or spam
    // a caller passes anyway, and an id a command line cannot carry.
    private static bool Eligible(ChatSummary chat) => !chat.IsArchived && !chat.IsSpam && LaunchCommand.IsChatId(chat.Id);

    private static bool IsWeekend(DayOfWeek day) => day is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
