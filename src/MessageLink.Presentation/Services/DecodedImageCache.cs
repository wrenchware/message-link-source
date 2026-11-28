namespace MessageLink.Presentation.Services;

// UI-context LRU for decoded thumbnails. Bound both entries and decoded BGRA bytes, not compressed file sizes.
// The caller scopes this to its media reader, so replacing the reader also retires its cached images.
public sealed class DecodedImageCache<TKey, TImage>(long maximumBytes, int maximumEntries) where TKey : notnull
{
    private sealed record Entry(TImage Image, long Bytes, LinkedListNode<TKey> Node);
    private readonly Dictionary<TKey, Entry> entries = [];
    private readonly LinkedList<TKey> recency = [];
    public long Bytes { get; private set; }
    public int Count => entries.Count;

    public bool TryGet(TKey key, out TImage image)
    {
        if (!entries.TryGetValue(key, out var entry)) { image = default!; return false; }
        recency.Remove(entry.Node); recency.AddLast(entry.Node);
        image = entry.Image;
        return true;
    }

    public void Put(TKey key, TImage image, int pixelWidth, int pixelHeight)
    {
        if (entries.Remove(key, out var prior)) { recency.Remove(prior.Node); Bytes -= prior.Bytes; }
        var pixels = (long)pixelWidth * pixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0 || maximumBytes <= 0 || pixels > maximumBytes / 4 || maximumEntries <= 0) return;
        var bytes = pixels * 4;
        while (recency.First is { } oldest && (entries.Count >= maximumEntries || bytes > maximumBytes - Bytes))
        {
            Bytes -= entries[oldest.Value].Bytes;
            entries.Remove(oldest.Value); recency.RemoveFirst();
        }
        entries[key] = new(image, bytes, recency.AddLast(key));
        Bytes += bytes;
    }
}
