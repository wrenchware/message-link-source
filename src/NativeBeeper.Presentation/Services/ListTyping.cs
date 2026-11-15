using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Who is typing in each row of the conversation list, as the words the row's second line shows. Fed from the typing
/// stream the app already receives; it asks nothing and sends nothing. Pure: time is passed in, and the list row a
/// conversation belongs to and whether it is a group are asked of the caller.
///
/// When typing ends — a stop, or its <see cref="TypingParticipant.ExpiresAt"/> passing — the words stay up to
/// <see cref="Linger"/>, and go at once when the row's own time or preview changes (<see cref="Activity"/>). A reply
/// therefore goes straight from "typing…" to the new message, never back through the old preview for a moment first.
/// </summary>
public sealed class ListTyping(Func<string, string>? displayId = null, Func<string, bool>? isGroup = null)
{
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);

    private sealed class Row
    {
        // Who is typing, by the provider conversation that said so: a merged row hears from each member separately.
        public readonly Dictionary<string, TypingParticipant[]> Sources = new(StringComparer.Ordinal);
        public string? Text;
        public DateTimeOffset? LingerUntil;
        // The row's preview or time moved while someone was typing: the message is already there, so when the typing
        // ends there is nothing to wait for.
        public bool Arrived;
    }

    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);

    /// <summary>Whether any row is typing or lingering, so a caller's timer can run only while there is something to end.</summary>
    public bool IsEmpty => rows.Count == 0;

    /// <summary>The words a row shows now, or null.</summary>
    public string? Text(string chatId) => rows.TryGetValue(chatId, out var row) ? row.Text : null;

    /// <summary>The list row a provider conversation belongs to.</summary>
    public string RowOf(string chatId) => displayId?.Invoke(chatId) ?? chatId;

    /// <summary>One conversation's typing, as the stream reported it. Returns the row whose words changed, or null.</summary>
    public string? Apply(TypingUpdate update, DateTimeOffset now)
    {
        var id = RowOf(update.ChatId);
        var live = update.Participants.Where(person => person.ExpiresAt > now).ToArray();
        if (!rows.TryGetValue(id, out var row))
        {
            if (live.Length == 0) return null;
            rows[id] = row = new();
        }
        if (live.Length > 0) row.Sources[update.ChatId] = live; else row.Sources.Remove(update.ChatId);
        return Settle(id, row, now, ended: now) ? id : null;
    }

    /// <summary>
    /// The row's time or preview changed. Words still showing only because typing just ended go now; a row where someone
    /// is still typing keeps them, and loses them the moment that typing ends.
    /// </summary>
    public bool Activity(string chatId)
    {
        if (!rows.TryGetValue(chatId, out var row)) return false;
        if (row.Sources.Count > 0) { row.Arrived = true; return false; }
        rows.Remove(chatId);
        return row.Text is not null;
    }

    /// <summary>Ends typing whose time has passed and lingering that has run out. Returns the rows whose words changed.</summary>
    public IReadOnlyList<string> Tick(DateTimeOffset now)
    {
        List<string>? changed = null;
        foreach (var (id, row) in rows.ToArray())
        {
            // Typing that ran out without a stop ended when it ran out, not when this tick noticed.
            var ended = row.Sources.Values.SelectMany(people => people).Select(person => person.ExpiresAt).DefaultIfEmpty(now).Max();
            foreach (var (source, people) in row.Sources.ToArray())
            {
                var still = people.Where(person => person.ExpiresAt > now).ToArray();
                if (still.Length == people.Length) continue;
                if (still.Length == 0) row.Sources.Remove(source); else row.Sources[source] = still;
            }
            if (Settle(id, row, now, ended < now ? ended : now)) (changed ??= []).Add(id);
        }
        return changed ?? (IReadOnlyList<string>)[];
    }

    private bool Settle(string id, Row row, DateTimeOffset now, DateTimeOffset ended)
    {
        var before = row.Text;
        var people = row.Sources.Values.SelectMany(list => list).DistinctBy(person => person.UserId).ToArray();
        if (people.Length > 0)
        {
            // A participant's status (Hermes: the tool it is using) says more than "typing…".
            row.Text = people.Select(person => person.Status?.Trim()).FirstOrDefault(status => !string.IsNullOrEmpty(status))
                ?? Words(people.Select(person => person.DisplayName).ToArray(), isGroup?.Invoke(id) == true);
            row.LingerUntil = null;
        }
        else if (row.Text is null || row.Arrived) { rows.Remove(id); row.Text = null; }
        else if (row.LingerUntil is null) row.LingerUntil = ended + Linger;
        if (row.Text is not null && row.LingerUntil is { } until && now >= until) { rows.Remove(id); row.Text = null; }
        return before != row.Text;
    }

    /// <summary>
    /// "typing…" in a conversation with one other person; in a group, "Sam is typing…", "Sam and Alex are typing…", or
    /// "3 people are typing…". First names only: the row is narrow.
    /// </summary>
    public static string Words(IReadOnlyList<string> names, bool group)
    {
        if (!group || names.Count == 0) return "typing…";
        return names.Count switch
        {
            1 => $"{FirstName(names[0])} is typing…",
            2 => $"{FirstName(names[0])} and {FirstName(names[1])} are typing…",
            _ => $"{names.Count} people are typing…",
        };
    }

    public static string FirstName(string? name)
    {
        var first = name?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "Someone" : first;
    }
}
