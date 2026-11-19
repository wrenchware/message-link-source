using System.Collections.Concurrent;
using System.Security.Cryptography;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

// Reports (IAgentReports): a message Hermes, or anything on the home network holding the profile's report token, puts into the profile's thread outside a turn.
// Stored as an ordinary incoming message (Kind "message", Origin "report"), so every rule that counts unread, maps to the wire and notifies treats it as one; only
// the key, the limits and the next turn read Origin. It never starts a turn and never reaches another thread or a service.
internal sealed partial class HermesBackend : IAgentReports
{
    internal const string ReportOrigin = "report", ReportSource = "hermes";
    internal const string TooManyReports = "Hermes sent too many reports. Some were not shown.";

    /// <summary>Safety limits per profile (owner, 2026-11-15), counted from the stored reports' arrival times so a restart resets nothing.</summary>
    internal static readonly (TimeSpan Window, int Most)[] ReportLimits =
        [(TimeSpan.FromMinutes(1), 6), (TimeSpan.FromHours(1), 30), (TimeSpan.FromDays(1), 200)];

    /// <summary>Checks of a token (<c>GET /v1/reports/check</c>) per profile, in memory: their own allowance, apart from the reports'.</summary>
    internal static readonly (TimeSpan Window, int Most) CheckLimit = (TimeSpan.FromMinutes(1), 6);

    private HermesReportTokens reportTokens = HermesReportTokens.Empty;
    // In memory only: the page's last-refusal line, the once-an-hour "too many" note (a restart may repeat it once), and checks of a token in the last minute.
    private readonly ConcurrentDictionary<string, (string Reason, DateTimeOffset At)> reportRefusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> tooManyNoted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> reportChecks = new(StringComparer.Ordinal);
    // When each profile's last report arrived, for the page: the threads are read under the gate only, and the page reads this without it. Seeded at load.
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastReports = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentReportsState> ReportsState
    {
        get
        {
            var rows = reportTokens.Rows;
            return [.. settings.Profiles.Select(p =>
            {
                var row = rows.FirstOrDefault(r => r.Profile == p.Name);
                var refused = reportRefusals.TryGetValue(p.Name, out var r) ? r : default;
                return new AgentReportsState(p.Name, row is not null, row?.Created, row is null ? null : refused.Reason, row is null ? null : refused.Reason is null ? null : refused.At,
                    row?.Address, lastReports.TryGetValue(p.Name, out var last) ? last : null, row?.SetupFrom);
            })];
        }
    }

    // Under the gate, at load.
    private void SeedLastReport(HermesThread thread)
    {
        if (thread.Messages.LastOrDefault(m => m.Origin == ReportOrigin) is { } last) lastReports[thread.Profile] = last.At;
    }

    public async Task<string> IssueReportTokenAsync(string profile, string? address = null, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        var token = AgentReportRules.NewToken();
        var tag = AgentReportRules.Tag(token);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Has(profile)) throw new ArgumentException("This Hermes profile isn't set up.");
            // Under the gate, so a setup command's redemption waiting for it finds its code gone rather than replacing this token.
            DropSetupCodes(profile);
            var next = new HermesReportTokens([.. reportTokens.Rows.Where(r => r.Profile != profile),
                new(profile, ReportSource, Convert.ToHexStringLower(AgentReportRules.Hash(token)), tag, clock.GetUtcNow(), string.IsNullOrWhiteSpace(address) ? null : address.Trim())]);
            await store.SaveReportTokensAsync(next).ConfigureAwait(false);
            reportTokens = next;
            reportRefusals.TryRemove(profile, out _);
        }
        finally { gate.Release(); }
        log($"hermes reports on profile={Tag(profile)} tag={tag}");
        return token;
    }

    public async Task TurnOffReportsAsync(string profile, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Under the gate, so a setup command's redemption waiting for it can't turn reports back on.
            DropSetupCodes(profile);
            await ForgetReportTokensAsync([profile]).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        reportRefusals.TryRemove(profile, out _);
        log($"hermes reports off profile={Tag(profile)}");
    }

    // Under the gate.
    private async Task ForgetReportTokensAsync(IReadOnlyCollection<string> profiles)
    {
        if (!reportTokens.Rows.Any(r => profiles.Contains(r.Profile))) return;
        var next = new HermesReportTokens([.. reportTokens.Rows.Where(r => !profiles.Contains(r.Profile))]);
        await store.SaveReportTokensAsync(next).ConfigureAwait(false);
        reportTokens = next;
    }

    // A token's row, found by its hash; none for a profile no longer set up.
    private HermesReportToken? TokenRow(byte[] tokenHash)
    {
        if (tokenHash.Length != 32) return null;
        var hex = Convert.ToHexStringLower(tokenHash);
        return reportTokens.Rows.FirstOrDefault(r => r.Hash == hex) is { } row && Has(row.Profile) ? row : null;
    }

    public bool KnowsReportToken(byte[] tokenHash)
    {
        // The door asks without loading. Tokens that couldn't be read are asked for again here (LoadAsync waits out its own
        // backoff), so the report after they can be read finds its token without a restart.
        if (loaded is { IsCompletedSuccessfully: true } && (reportsPending || settingsPending)) _ = LoadAsync();
        return loaded is { IsCompletedSuccessfully: true } && TokenRow(tokenHash) is not null;
    }

    public void ReportRefused(byte[] tokenHash, string reason)
    {
        if (TokenRow(tokenHash) is { } row) reportRefusals[row.Profile] = (reason, clock.GetUtcNow());
    }

    public async Task<AgentReportResult> CheckReportAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentReportOutcome.Unavailable);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TokenRow(tokenHash) is not { } row) return new(AgentReportOutcome.Unknown);
            // Checks have a minute's allowance of their own, so the plugin's check at connect or a setup's checks never leave a long message short of reports
            // (design review, 2026-11-16: two checks could stop a five-part message). Reports never count them.
            var now = clock.GetUtcNow();
            var minute = CheckLimit;
            var checks = reportChecks.GetOrAdd(row.Profile, _ => []);
            lock (checks)
            {
                checks.RemoveAll(at => now - at >= minute.Window);
                if (checks.Count >= minute.Most) return new(AgentReportOutcome.Limited, RetryAfter: Wait(checks.Order().ToArray()[^minute.Most], minute.Window, now), Tag: row.Tag);
                checks.Add(now);
            }
            return new(AgentReportOutcome.Ready, Tag: row.Tag);
        }
        finally { gate.Release(); }
    }

    public async Task<AgentReportResult> DeliverReportAsync(byte[] tokenHash, string text, string? key, CancellationToken cancellationToken = default)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentReportOutcome.Unavailable);
        string profile, tag;
        HermesStoredMessage report;
        var limited = false;
        TimeSpan retry = default;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TokenRow(tokenHash) is not { } row) return new(AgentReportOutcome.Unknown);
            (profile, tag) = (row.Profile, row.Tag);
            var now = clock.GetUtcNow();
            var digest = AgentReportRules.Digest(text);
            var reports = Reports(profile).ToArray();
            // A retry after a lost answer: the same key within a day is the same report; with other words it is a mistake, refused.
            if (key is not null && reports.LastOrDefault(m => m.ReportKey == key && now - m.At < AgentReportRules.KeyWindow) is { } earlier)
                return earlier.ReportDigest == digest ? new(AgentReportOutcome.Duplicate, earlier.Id, Tag: tag) : new(AgentReportOutcome.KeyReused, Tag: tag);
            foreach (var (window, most) in ReportLimits)
            {
                var ordered = reports.Where(m => now - m.At < window).Select(m => m.At).Order().ToArray();
                if (ordered.Length < most) continue;
                var wait = Wait(ordered[^most], window, now);
                if (wait > retry) retry = wait;
                limited = true;
            }
            if (!limited)
            {
                // Kept in memory only once it is on disk: a failed save is a failed report (503), never one a retry would find as a duplicate.
                var thread = Thread(profile);
                var seq = thread.NextReportSeq + 1;
                report = new(NewMessageId(profile), now, false, text, State: "done", Origin: ReportOrigin, ReportKey: key, ReportDigest: digest, ReportSeq: seq);
                var next = thread with { Messages = [.. thread.Messages, report], NextReportSeq = seq };
                await store.SaveThreadAsync(next).ConfigureAwait(false);
                threads[profile] = next;
                lastReports[profile] = now;
            }
            else report = null!;
        }
        finally { gate.Release(); }
        var chatId = HermesIds.Chat(profile);
        if (limited)
        {
            // One line in the thread at most once an hour, so a runaway loop shows without flooding it. A note is a line of ours: no unread, no notification.
            // Claimed atomically: two refusals at the same moment add one note between them.
            var now = clock.GetUtcNow();
            bool due;
            lock (tooManyNoted)
            {
                due = !tooManyNoted.TryGetValue(profile, out var noted) || now - noted >= TimeSpan.FromHours(1);
                if (due) tooManyNoted[profile] = now;
            }
            if (due) await NoteAsync(profile, chatId, TooManyReports).ConfigureAwait(false);
            return new(AgentReportOutcome.Limited, RetryAfter: retry, Tag: tag);
        }
        // A new arrival, as a finished reply is: unread and notified on every client.
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [report.Id], true, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        return new(AgentReportOutcome.Delivered, report.Id, Tag: tag);
    }

    // Under the gate.
    private IEnumerable<HermesStoredMessage> Reports(string profile) => Thread(profile).Messages.Where(m => m.Origin == ReportOrigin);

    // Until the oldest of the window's newest `most` leaves it; at least a second.
    private static TimeSpan Wait(DateTimeOffset oldest, TimeSpan window, DateTimeOffset now) =>
        oldest + window - now is var wait && wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
}
