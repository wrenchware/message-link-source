using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// What one subscriber to a provider's typing has been told, so it is told only what changed. Each conversation window
/// watches separately, while the conversations being watched are one shared set that the first watcher to see somebody
/// stop removes. A second watcher never saw that conversation again, so it never said the typing ended, and its window
/// went on showing "is typing…" until something unrelated redrew it. This also says so for every conversation that
/// has left the shared set while this watcher still had somebody typing in it.
/// </summary>
public sealed class TypingWatch
{
    private readonly Dictionary<string, string> told = new(StringComparer.Ordinal);

    /// <param name="current">Every conversation still in the shared set, with who is typing there now — nobody once
    /// they have stopped, which is when the caller drops it from the set.</param>
    public List<TypingUpdate> Next(IEnumerable<TypingUpdate> current)
    {
        List<TypingUpdate> updates = [];
        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (var update in current)
        {
            present.Add(update.ChatId);
            var signature = string.Join("\n", update.Participants.Select(p => $"{p.UserId}:{p.ExpiresAt.UtcTicks}"));
            if (!told.TryGetValue(update.ChatId, out var old) || old != signature) { updates.Add(update); told[update.ChatId] = signature; }
        }
        // A conversation that left the set. With one watcher it had already been told empty, on the tick before its owner
        // dropped it; with more, this is where the others hear of it.
        foreach (var gone in told.Keys.Where(chat => !present.Contains(chat)).ToArray())
        {
            if (told[gone].Length > 0) updates.Add(new(gone, []));
            told.Remove(gone);
        }
        return updates;
    }
}
