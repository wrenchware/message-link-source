namespace MessageLink.Presentation.Services;

/// <summary>
/// The label book as it is changed: the labels in the order they were made, which of them each conversation wears, and the rules every writer of it keeps
/// (a label's name, two names that read the same, a new label's colour). The window's and mlink's <see cref="ChatLabels"/> and a server's Marketplace
/// labeller both change the book through this, so the two never disagree on what a name is. Pure: it reads and writes nothing, and
/// <see cref="ChatLabelBook"/> is what it is kept as.
/// </summary>
public sealed class LabelBook
{
    public const int MaxNameLength = 32;

    private readonly List<ChatLabel> labels = [];
    private readonly Dictionary<string, List<string>> assignments = new(StringComparer.Ordinal);

    public IReadOnlyList<ChatLabel> Labels => labels;
    /// <summary>Every conversation id that wears a label.</summary>
    public IEnumerable<string> ChatIds => assignments.Keys;
    /// <summary>How many conversation-and-label pairs there are.</summary>
    public int AssignmentCount => assignments.Values.Sum(ids => ids.Count);

    /// <summary>
    /// A kept book, checked rather than trusted: a label with no name or a colour this build does not know, an assignment to a label that is gone, and
    /// duplicates are dropped or mended here, once. Null reads as an empty book.
    /// </summary>
    public static LabelBook From(ChatLabelBook? saved)
    {
        var book = new LabelBook();
        if (saved is null) return book;
        foreach (var label in saved.Labels ?? [])
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Id) || book.labels.Any(l => l.Id == label.Id)) continue;
            if (CleanName(label.Name) is not { } name) continue;
            book.labels.Add(new(label.Id, name, ChatLabelPalette.Get(label.Color).Key));
        }
        var known = book.labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (chatId, ids) in saved.Assignments ?? new Dictionary<string, string[]>())
        {
            if (string.IsNullOrWhiteSpace(chatId)) continue;
            var kept = (ids ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
            if (kept.Count > 0) book.assignments[chatId] = kept;
        }
        return book;
    }

    /// <summary>The book as it is kept.</summary>
    public ChatLabelBook ToSaved() =>
        new([.. labels], assignments.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));

    public LabelBook Copy()
    {
        var copy = new LabelBook();
        copy.labels.AddRange(labels);
        foreach (var (chatId, ids) in assignments) copy.assignments[chatId] = [.. ids];
        return copy;
    }

    /// <summary>The same labels in the same order, and the same labels on the same conversations.</summary>
    public bool SameAs(LabelBook other) =>
        labels.SequenceEqual(other.labels) && assignments.Count == other.assignments.Count
        && assignments.All(pair => other.assignments.TryGetValue(pair.Key, out var ids) && ids.SequenceEqual(pair.Value, StringComparer.Ordinal));

    // ---- Names ----

    /// <summary>
    /// A name as it is kept: control characters become spaces, the ends are trimmed, and it is cut to <see cref="MaxNameLength"/> without splitting a
    /// character in two. Null when nothing is left.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var text = new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (text.Length > MaxNameLength)
            text = text[..(char.IsHighSurrogate(text[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)].TrimEnd();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Whether two names read the same, ignoring case: two labels that do cannot be told apart. Both are cleaned first.</summary>
    public static bool SameName(string? a, string? b) =>
        CleanName(a) is { } x && CleanName(b) is { } y && string.Equals(x, y, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The label whose name reads the same as <paramref name="name"/>, or null.</summary>
    public ChatLabel? Named(string? name, string? exceptId = null) =>
        CleanName(name) is { } clean ? labels.FirstOrDefault(l => l.Id != exceptId && SameName(l.Name, clean)) : null;

    /// <summary>Whether another label already has this name.</summary>
    public bool Taken(string? name, string? exceptId = null) => Named(name, exceptId) is not null;

    /// <summary>The colour a new label starts with: the first one no label uses yet, then round again.</summary>
    public string NextColor()
    {
        var used = labels.Select(l => l.Color).ToHashSet(StringComparer.Ordinal);
        return (ChatLabelPalette.Colors.FirstOrDefault(c => !used.Contains(c.Key)) ?? ChatLabelPalette.Colors[labels.Count % ChatLabelPalette.Colors.Count]).Key;
    }

    // ---- Labels ----

    public ChatLabel? Find(string id) => labels.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// The label named <paramref name="name"/>: the one that reads the same, or a new one made with the next colour. <paramref name="made"/> says which.
    /// Throws when the name is empty once cleaned.
    /// </summary>
    public ChatLabel Ensure(string name, out bool made)
    {
        var clean = CleanName(name) ?? throw new ArgumentException("A label needs a name.", nameof(name));
        made = false;
        if (Named(clean) is { } found) return found;
        var label = new ChatLabel(Guid.NewGuid().ToString("N"), clean, NextColor());
        labels.Add(label);
        made = true;
        return label;
    }

    /// <summary>Adds a label as it is. False when the id is already there; whether its name is free is the caller's to ask (<see cref="Taken"/>).</summary>
    public bool Add(ChatLabel label)
    {
        if (Find(label.Id) is not null) return false;
        labels.Add(label);
        return true;
    }

    /// <summary>Changes a label in place. Null when it is gone, false when the change is no change.</summary>
    public bool? Replace(string id, Func<ChatLabel, ChatLabel> change)
    {
        var index = labels.FindIndex(l => l.Id == id);
        if (index < 0) return null;
        var next = change(labels[index]);
        if (next == labels[index]) return false;
        labels[index] = next;
        return true;
    }

    /// <summary>Deletes a label and takes it off every conversation. Null when it is not there; otherwise how many conversations lost it.</summary>
    public int? Delete(string id)
    {
        if (labels.RemoveAll(l => l.Id == id) == 0) return null;
        var lost = 0;
        foreach (var chatId in assignments.Keys.ToArray())
            if (assignments[chatId].Remove(id)) { lost++; if (assignments[chatId].Count == 0) assignments.Remove(chatId); }
        return lost;
    }

    // ---- Assignments ----

    /// <summary>A conversation's label ids, in the order they were put on. Empty when it has none.</summary>
    public IReadOnlyList<string> On(string chatId) => assignments.TryGetValue(chatId, out var ids) ? ids : [];

    public bool Has(string chatId, string labelId) => assignments.TryGetValue(chatId, out var ids) && ids.Contains(labelId);

    /// <summary>How many conversations wear this label.</summary>
    public int CountUsing(string labelId) => assignments.Values.Count(ids => ids.Contains(labelId));

    /// <summary>Puts a label on a conversation. False when it already wears it.</summary>
    public bool Assign(string chatId, string labelId)
    {
        if (!assignments.TryGetValue(chatId, out var ids)) assignments[chatId] = ids = [];
        if (ids.Contains(labelId)) return false;
        ids.Add(labelId);
        return true;
    }

    /// <summary>Takes a label off a conversation. False when it did not wear it.</summary>
    public bool Unassign(string chatId, string labelId)
    {
        if (!assignments.TryGetValue(chatId, out var ids) || !ids.Remove(labelId)) return false;
        if (ids.Count == 0) assignments.Remove(chatId);
        return true;
    }

    /// <summary>Takes every label off a conversation and returns their ids, or null when it wore none.</summary>
    public string[]? Forget(string chatId) => assignments.Remove(chatId, out var ids) ? [.. ids] : null;
}
