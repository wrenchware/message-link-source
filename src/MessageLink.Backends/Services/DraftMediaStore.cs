namespace MessageLink.App.Services;

// Short-lived, process-local photo bytes for optimistic outgoing bubbles. The upload and the preview share the same
// byte array, so showing the photo does not create another full-resolution copy. A small LRU bound prevents abandoned
// failed sends from retaining media for the rest of a long-running session.
internal static class DraftMediaStore
{
    private const string Prefix = "draft-media:";
    private const int MaxItems = 40;
    private const long MaxBytes = 128L * 1024 * 1024;
    private sealed record Entry(byte[] Bytes, string MimeType, LinkedListNode<string> Node);
    private static readonly object gate = new();
    private static readonly Dictionary<string, Entry> entries = [];
    private static readonly LinkedList<string> recency = [];
    private static long totalBytes;

    public static string Put(string id, byte[] bytes, string mimeType)
    {
        lock (gate)
        {
            if (entries.Remove(id, out var prior)) { recency.Remove(prior.Node); totalBytes -= prior.Bytes.LongLength; }
            var node = recency.AddLast(id);
            entries[id] = new(bytes, mimeType, node);
            totalBytes += bytes.LongLength;
            while (entries.Count > MaxItems || totalBytes > MaxBytes)
            {
                var oldest = recency.First;
                if (oldest is null) break;
                recency.RemoveFirst();
                if (entries.Remove(oldest.Value, out var removed)) totalBytes -= removed.Bytes.LongLength;
            }
        }
        return Prefix + id;
    }

    public static bool TryRead(string? source, out byte[] bytes)
    {
        bytes = [];
        if (source is null || !source.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var id = source[Prefix.Length..];
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var entry)) return false;
            recency.Remove(entry.Node); recency.AddLast(entry.Node);
            bytes = entry.Bytes;
            return true;
        }
    }
}
