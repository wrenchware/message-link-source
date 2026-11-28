using System.Globalization;
using System.Text;

namespace MessageLink.Presentation.Services;

// In-memory word index. Every query word must match the start of a word in a document,
// ignoring case and accents: "rest caf" finds "Café restaurant". Postings are compact
// handle lists; replaced documents leave dead handles that queries skip.
public sealed class TextSearchIndex
{
    private const int MaxWordLength = 64;
    private readonly object sync = new();
    private readonly Dictionary<string, int> handles = new(StringComparer.Ordinal);
    private readonly List<string?> ids = [];
    private readonly List<string[]> documentWords = [];
    private readonly Dictionary<string, List<int>> postings = new(StringComparer.Ordinal);
    private readonly SortedSet<string> words = new(StringComparer.Ordinal);

    public int Count { get { lock (sync) return handles.Count; } }

    public void Set(string id, string? text)
    {
        var tokens = Words(text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        lock (sync)
        {
            if (ids.Count > 4096 && ids.Count > handles.Count * 2) CompactCore();
            if (handles.TryGetValue(id, out var existing))
            {
                if (documentWords[existing].AsSpan().SequenceEqual(tokens)) return; // Status/reaction updates keep the same text.
                ids[existing] = null;
                handles.Remove(id);
            }
            if (tokens.Length == 0) return;
            // Each document keeps the index's own copy of a word it shares with others. Kept Messenger history put tens of
            // thousands of documents here, and a private copy of every word in each came to about half the index's memory.
            for (var index = 0; index < tokens.Length; index++) if (words.TryGetValue(tokens[index], out var known)) tokens[index] = known;
            var handle = ids.Count;
            ids.Add(id); documentWords.Add(tokens); handles[id] = handle;
            foreach (var token in tokens)
            {
                if (!postings.TryGetValue(token, out var list)) { postings[token] = list = []; words.Add(token); }
                list.Add(handle);
            }
        }
    }

    public void Clear()
    {
        lock (sync) { handles.Clear(); ids.Clear(); documentWords.Clear(); postings.Clear(); words.Clear(); }
    }

    public void Remove(string id)
    {
        lock (sync) if (handles.Remove(id, out var handle)) ids[handle] = null;
    }

    public IReadOnlyCollection<string> Query(string query, CancellationToken cancellationToken = default)
    {
        // Longest words first narrow the candidate set fastest.
        var terms = Words(query).Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Length).ToArray();
        if (terms.Length == 0) return [];
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<int>? result = null;
            foreach (var term in terms)
            {
                var matches = new HashSet<int>();
                foreach (var word in words.GetViewBetween(term, term + char.MaxValue))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var handle in postings[word])
                        if (result is null || result.Contains(handle)) matches.Add(handle);
                }
                result = matches;
                if (result.Count == 0) return [];
            }
            return result!.Select(handle => ids[handle]).OfType<string>().ToArray();
        }
    }

    private void CompactCore()
    {
        var live = handles.Select(pair => (Id: pair.Key, Words: documentWords[pair.Value])).ToArray();
        handles.Clear(); ids.Clear(); documentWords.Clear(); postings.Clear(); words.Clear();
        foreach (var document in live)
        {
            var handle = ids.Count;
            ids.Add(document.Id); documentWords.Add(document.Words); handles[document.Id] = handle;
            foreach (var word in document.Words)
            {
                if (!postings.TryGetValue(word, out var list)) { postings[word] = list = []; words.Add(word); }
                list.Add(handle);
            }
        }
    }

    public static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var word = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { word.Append(char.ToLowerInvariant(ch)); continue; }
            if (word.Length is > 0 and <= MaxWordLength) yield return word.ToString();
            word.Clear();
        }
        if (word.Length is > 0 and <= MaxWordLength) yield return word.ToString();
    }
}
