using System.Collections.Concurrent;
using System.Diagnostics;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Protocol;
using static MessageLink.Server.HistoryParts;

namespace MessageLink.Server;

/// <summary>What a read of a conversation's newest page found against the copy (counts only).</summary>
internal sealed record MirrorDifference(int Missing, int Extra, int Changed)
{
    public bool Any => Missing > 0 || Extra > 0 || Changed > 0;
}

/// <summary>
/// The mirror check (SWITCHBOARD.md stage 9, A2): now and then the most recently active conversations of each service held whole are read once, as any
/// background read, and what each read found against the copy is counted into that service's report. Counts only, never what differed.
/// </summary>
internal sealed class HistoryMirror(HistoryCopy copy, HistoryTimings timings, Action<string> log)
{
    // One service's check under way: the conversations it waits on, and what those that were read found.
    private sealed class Round(string service, int asked, string reason)
    {
        public string Service { get; } = service;
        public string Why { get; } = reason;
        public int Pending = asked;
        public int Checked, Differed, Missing, Extra, Changed, Skipped;
        public long Started { get; } = Stopwatch.GetTimestamp();
    }

    // The round under way per service, which round each conversation it asked for belongs to, and the newest finished.
    private readonly object gate = new();
    private readonly Dictionary<string, Round> rounds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Round> mirroring = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistoryMirrorReport> reports = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts a check of each service that has none under way: its most recently active conversations held whole, each read once, a second apart per service
    /// and Instagram's only while its budget allows (owner's D3).
    /// </summary>
    public void Start(IReadOnlyList<ChatSummary> rows, string why)
    {
        var started = new List<string>();
        lock (gate)
        {
            Sweep();
            // Not Hermes: its threads live on this server (HermesBackend's own store), so there is no service's copy to compare the history with.
            foreach (var group in rows.Where(row => !row.IsSpam && !ServiceTags.Hermes.Owns(row.Id) && copy.Whole(row.Id) is not null && !mirroring.ContainsKey(row.Id))
                         .GroupBy(row => Service(row.Id), StringComparer.Ordinal))
            {
                if (rounds.ContainsKey(group.Key)) continue;
                var chosen = group.OrderByDescending(row => row.LastActivity ?? DateTimeOffset.MinValue).Take(timings.MirrorPerService).Select(row => row.Id).ToArray();
                if (chosen.Length == 0) continue;
                var round = new Round(group.Key, chosen.Length, why);
                rounds[group.Key] = round;
                foreach (var id in chosen) mirroring[id] = round;
                started.Add(group.Key + "=" + chosen.Length);
                foreach (var id in chosen) copy.Enqueue(id, HistoryWork.Mirror);
            }
        }
        if (started.Count > 0) log($"history mirror started reason={why} {string.Join(' ', started)}");
    }

    /// <summary>A conversation a check waits on was read (with what it found), or could not be (null). Nothing when no check waits on it.</summary>
    public void Read(string chatId, MirrorDifference? found)
    {
        if (!mirroring.TryRemove(chatId, out var round)) return;
        lock (gate)
        {
            if (found is null) round.Skipped++;
            else
            {
                round.Checked++;
                if (found.Any)
                {
                    round.Differed++;
                    round.Missing += found.Missing; round.Extra += found.Extra; round.Changed += found.Changed;
                    log($"history mirror chat={Tag(chatId)} missing={found.Missing} extra={found.Extra} changed={found.Changed}");
                }
            }
            if (--round.Pending <= 0) Finish(round);
        }
    }

    /// <summary>Whether a check is under way, and each service's newest report.</summary>
    public HistoryMirrorResult Result()
    {
        lock (gate)
        {
            Sweep();
            return new(rounds.Count > 0, reports.Values.OrderBy(report => report.Service, StringComparer.Ordinal).ToArray());
        }
    }

    // Reports a check whose reads all came back, and one that waited past its time with the rest counted as not read. Caller holds the gate.
    private void Sweep()
    {
        foreach (var round in rounds.Values.Where(round => Stopwatch.GetElapsedTime(round.Started) > timings.MirrorTimeout).ToArray())
        {
            foreach (var (id, waiting) in mirroring.ToArray())
                if (ReferenceEquals(waiting, round) && mirroring.TryRemove(id, out _)) { round.Skipped++; round.Pending--; }
            Finish(round);
        }
    }

    // Caller holds the gate.
    private void Finish(Round round)
    {
        if (!rounds.TryGetValue(round.Service, out var current) || !ReferenceEquals(current, round)) return;
        rounds.Remove(round.Service);
        reports[round.Service] = new(round.Service, DateTimeOffset.UtcNow, round.Checked, round.Differed, round.Missing, round.Extra, round.Changed, round.Skipped);
        log($"history mirror service={round.Service} reason={round.Why} checked={round.Checked} differed={round.Differed} missing={round.Missing} extra={round.Extra} changed={round.Changed} skipped={round.Skipped} ms={Ms(round.Started)}");
    }
}
