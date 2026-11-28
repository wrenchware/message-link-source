using System.Globalization;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The console's typing strip: who is typing right now, across the conversations the console shows. Live state, not a
/// line: recomputed from what the typing service last said on each update and on a one-second tick, so it reads
/// "Sam is typing… · Instagram", "Sam and Alex are typing…" or "3 people are typing…", and is null when nobody is.
/// It receives only; the console never tells anyone it is typing.
/// </summary>
/// <param name="visible">Whether a conversation's typing is shown: the console's current filter. Everything when null.</param>
public sealed class ConsoleTyping(TimeProvider? clock = null, Func<string, bool>? visible = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, IReadOnlyList<TypingParticipant>> chats = new(StringComparer.Ordinal);

    /// <summary>Whether a conversation's typing is shown; set by the session when its filter changes.</summary>
    public Func<string, bool>? Visible { get; set; } = visible;

    public string? Text { get; private set; }

    /// <summary>Raised when <see cref="Text"/> changes, on the thread that caused it.</summary>
    public event EventHandler? Changed;

    /// <summary>What the typing service said about one conversation. True when the text changed.</summary>
    public bool Update(TypingUpdate update)
    {
        lock (gate)
        {
            if (update.Participants.Count == 0) chats.Remove(update.ChatId);
            else chats[update.ChatId] = [.. update.Participants];
        }
        return Refresh();
    }

    /// <summary>The one-second tick: someone whose typing has expired drops out. True when the text changed.</summary>
    public bool Tick() => Refresh();

    /// <summary>Forgets everyone (the account changed).</summary>
    public bool Clear()
    {
        lock (gate) chats.Clear();
        return Refresh();
    }

    /// <summary>Recomputes the text, as after a filter change. True when it changed.</summary>
    public bool Refresh()
    {
        KeyValuePair<string, IReadOnlyList<TypingParticipant>>[] copy;
        lock (gate) copy = [.. chats.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
        // Worked out outside the lock: the filter belongs to the session, which has a lock of its own.
        var next = Compute(copy);
        lock (gate)
        {
            if (next == Text) return false;
            Text = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private string? Compute(IEnumerable<KeyValuePair<string, IReadOnlyList<TypingParticipant>>> current)
    {
        var now = time.GetUtcNow();
        var shown = Visible;
        var typists = new List<(string Chat, string Name, string? Status)>();
        foreach (var (chatId, participants) in current)
        {
            if (shown is not null && !shown(chatId)) continue;
            foreach (var person in participants)
                if (person.ExpiresAt > now && !typists.Any(t => t.Chat == chatId && t.Name == Display(person)))
                    typists.Add((chatId, Display(person), string.IsNullOrWhiteSpace(person.Status) ? null : person.Status.Trim()));
        }
        return typists.Count switch
        {
            0 => null,
            // Hermes says what it is doing ("Using terminal: date -u") in place of typing; without that, it is working, not typing.
            1 when typists[0].Status is { } status => typists[0].Name + ": " + status + " · " + ServiceScopes.Name(typists[0].Chat),
            1 when HermesIds.Owns(typists[0].Chat) => typists[0].Name + " is working… · " + ServiceScopes.Name(typists[0].Chat),
            1 => typists[0].Name + " is typing… · " + ServiceScopes.Name(typists[0].Chat),
            2 => typists[0].Name + " and " + typists[1].Name + " are typing…",
            _ => typists.Count.ToString(CultureInfo.CurrentCulture) + " people are typing…"
        };
    }

    private static string Display(TypingParticipant person) => string.IsNullOrWhiteSpace(person.DisplayName) ? "Someone" : person.DisplayName.Trim();
}
