using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

// Reports (IAgentReports): a message Hermes, or anything on the home network holding the profile's report token, puts into the profile's thread outside a turn.
// Stored as an ordinary incoming message (Kind "message", Origin "report"), so every rule that counts unread, maps to the wire and notifies treats it as one; only
// the key, the limits and the next turn read Origin. It never starts a turn and never reaches another thread or a service. Who may post, and how often, is
// HermesReportAccess's; the thread is this backend's.
internal sealed partial class HermesBackend : IAgentReports
{
    internal const string ReportOrigin = "report";
    internal const string TooManyReports = "Hermes sent too many reports. Some were not shown.";

    public IReadOnlyList<AgentReportsState> ReportsState => access.State;

    // Under the gate, at load.
    private void SeedLastReport(HermesThread thread)
    {
        if (thread.Messages.LastOrDefault(m => m.Origin == ReportOrigin) is { } last) access.Arrived(thread.Profile, last.At);
    }

    public async Task<string> IssueReportTokenAsync(string profile, string? address = null, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await access.IssueTokenAsync(profile, address).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task TurnOffReportsAsync(string profile, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await access.TurnOffAsync(profile).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public bool KnowsReportToken(byte[] tokenHash)
    {
        // The door asks without loading. Tokens that couldn't be read are asked for again here (LoadAsync waits out its own
        // backoff), so the report after they can be read finds its token without a restart.
        if (loaded is { IsCompletedSuccessfully: true } && (reportsPending || settingsPending)) _ = LoadAsync();
        return loaded is { IsCompletedSuccessfully: true } && access.Token(tokenHash) is not null;
    }

    public void ReportRefused(byte[] tokenHash, string reason) => access.Refused(tokenHash, reason);

    public async Task<AgentReportResult> CheckReportAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentReportOutcome.Unavailable);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return access.Check(tokenHash); }
        finally { gate.Release(); }
    }

    public async Task<AgentReportResult> DeliverReportAsync(byte[] tokenHash, string text, string? key, CancellationToken cancellationToken = default)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentReportOutcome.Unavailable);
        string profile, tag;
        HermesStoredMessage report;
        bool limited;
        TimeSpan retry;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (access.Token(tokenHash) is not { } row) return new(AgentReportOutcome.Unknown);
            (profile, tag) = (row.Profile, row.Tag);
            var now = clock.GetUtcNow();
            var digest = AgentReportRules.Digest(text);
            var reports = Reports(profile).ToArray();
            // A retry after a lost answer: the same key within a day is the same report; with other words it is a mistake, refused.
            if (key is not null && reports.LastOrDefault(m => m.ReportKey == key && now - m.At < AgentReportRules.KeyWindow) is { } earlier)
                return earlier.ReportDigest == digest ? new(AgentReportOutcome.Duplicate, earlier.Id, Tag: tag) : new(AgentReportOutcome.KeyReused, Tag: tag);
            (limited, retry) = HermesReportAccess.Limit(reports.Select(m => m.At), now);
            if (!limited)
            {
                // Kept in memory only once it is on disk: a failed save is a failed report (503), never one a retry would find as a duplicate.
                var thread = Thread(profile);
                var seq = thread.NextReportSeq + 1;
                report = new(NewMessageId(profile), now, false, text, State: "done", Origin: ReportOrigin, ReportKey: key, ReportDigest: digest, ReportSeq: seq);
                var next = thread with { Messages = [.. thread.Messages, report], NextReportSeq = seq };
                await store.SaveThreadAsync(next).ConfigureAwait(false);
                threads[profile] = next;
                access.Arrived(profile, now);
            }
            else report = null!;
        }
        finally { gate.Release(); }
        var chatId = HermesIds.Chat(profile);
        if (limited)
        {
            // One line in the thread at most once an hour, so a runaway loop shows without flooding it. A note is a line of ours: no unread, no notification.
            if (access.ClaimTooManyNote(profile)) await NoteAsync(profile, chatId, TooManyReports).ConfigureAwait(false);
            return new(AgentReportOutcome.Limited, RetryAfter: retry, Tag: tag);
        }
        // A new arrival, as a finished reply is: unread and notified on every client.
        realtime.TryWrite(new(RealtimeKind.MessageChanged, chatId, [report.Id], true, clock.GetUtcNow()));
        realtime.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, clock.GetUtcNow()));
        return new(AgentReportOutcome.Delivered, report.Id, Tag: tag);
    }

    // Under the gate.
    private IEnumerable<HermesStoredMessage> Reports(string profile) => Thread(profile).Messages.Where(m => m.Origin == ReportOrigin);
}
