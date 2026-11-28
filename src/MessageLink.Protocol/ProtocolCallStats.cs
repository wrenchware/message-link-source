using System.Globalization;

namespace MessageLink.Protocol;

/// <summary>
/// How long each method's calls took, written as one fixed-format line per method at most once a minute:
/// <c>protocol calls method=chats.get n=12 avg_ms=0.31 worst_ms=1.40</c>. Only method names (fixed by the catalogue),
/// counts and times; nothing a call carried. <paramref name="label"/> is the line's first words, so the client's and the
/// host's measurements of the same method can be told apart (the difference is what the transport costs).
/// </summary>
public sealed class ProtocolCallStats(string label, Action<string>? log, TimeProvider? clock = null, TimeSpan? interval = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (int Count, double TotalMs, double WorstMs)> window = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> totals = new(StringComparer.Ordinal);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan every = interval ?? TimeSpan.FromMinutes(1);
    private long? lastFlush;

    public void Record(string method, TimeSpan elapsed)
    {
        List<string>? lines = null;
        lock (gate)
        {
            var ms = elapsed.TotalMilliseconds;
            window.TryGetValue(method, out var entry);
            window[method] = (entry.Count + 1, entry.TotalMs + ms, Math.Max(entry.WorstMs, ms));
            totals[method] = totals.GetValueOrDefault(method) + 1;
            var now = time.GetTimestamp();
            lastFlush ??= now;
            if (time.GetElapsedTime(lastFlush.Value, now) >= every)
            {
                lastFlush = now;
                lines = Take();
            }
        }
        Write(lines);
    }

    /// <summary>Writes what has been counted since the last line, whatever the interval says. For shutdown.</summary>
    public void Flush()
    {
        List<string> lines;
        lock (gate)
        {
            lastFlush = time.GetTimestamp();
            lines = Take();
        }
        Write(lines);
    }

    /// <summary>How many calls of <paramref name="method"/> were ever recorded.</summary>
    public int Count(string method)
    {
        lock (gate) return totals.GetValueOrDefault(method);
    }

    private List<string> Take()
    {
        var lines = window.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            $"{label} method={pair.Key} n={pair.Value.Count} avg_ms={(pair.Value.TotalMs / pair.Value.Count).ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"worst_ms={pair.Value.WorstMs.ToString("0.00", CultureInfo.InvariantCulture)}").ToList();
        window.Clear();
        return lines;
    }

    private void Write(List<string>? lines)
    {
        if (lines is null || log is null) return;
        foreach (var line in lines)
            try { log(line); } catch (Exception) { /* a logger that fails must not fail a call */ }
    }
}
