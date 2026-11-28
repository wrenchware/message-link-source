namespace MessageLink.Protocol;

/// <summary>Identifiers and time for an ordinary send that failed or may have gone through; never its words or attachment.</summary>
public sealed record UnsettledSendOperation(string OperationId, string ChatId, DateTimeOffset Started, bool OutcomeUnknown);

public sealed partial class OperationJournal
{
    // One background publisher at a time, started on demand; all three fields are guarded by `gate`. Code review, round 3 (2026-10-13): a completion
    // used to await publication itself, so a stuck shared write held up a successful send's answer and its Ended (any completion retried an
    // earlier failed write), and a failure arriving while another publication was under way returned without being published by anyone.
    private bool publisherRunning;
    private bool publicationSignalled;
    private Task publisher = Task.CompletedTask;

    /// <summary>A server publishes these to its shared outbox. In-process and loopback hosts leave this null.</summary>
    public Func<UnsettledSendOperation, Task>? UnsettledSendRecorder { get; set; }

    /// <summary>
    /// Publishes failed sends, including sends loaded as unknown after a server crash. A failed write leaves the metadata for the next signal or restart.
    /// Nothing calls a provider; running sends stay per device (the echo reaches the others in 0.2–1.5 s). The returned task ends when the publisher that took this signal stops;
    /// an operation's completion only signals and never waits for it.
    /// </summary>
    public Task PublishUnsettledSendsAsync()
    {
        if (UnsettledSendRecorder is null) return Task.CompletedTask;
        lock (gate)
        {
            // With nothing owed no publisher is started, so an ordinary completion costs one scan and nothing more.
            if (!publisherRunning && !entries.Values.Any(Owed)) return Task.CompletedTask;
            publicationSignalled = true;
            // A running publisher sees the signal when its pass ends (it checks under this same lock before stopping), so nothing is left unpublished.
            if (publisherRunning) return publisher;
            publisherRunning = true;
            // Task.Run: a recorder that blocks before its first await must not block the completion that signalled it.
            return publisher = Task.Run(DrainUnsettledSendsAsync);
        }
    }

    private static bool Owed(Entry entry) => entry.SendChatId is not null && entry.State == OpState.Failed;

    // Drains until nothing is owed. After a pass with a failed write it stops unless another signal came in meanwhile, so a store that is down is
    // tried again per completion (or at the next start), never in a loop of its own.
    private async Task DrainUnsettledSendsAsync()
    {
        if (UnsettledSendRecorder is not { } record) { lock (gate) publisherRunning = false; return; }
        var failed = false;
        while (true)
        {
            Entry[] pending;
            lock (gate)
            {
                var signalled = publicationSignalled;
                publicationSignalled = false;
                pending = entries.Values.Where(Owed).ToArray();
                if (pending.Length == 0 || failed && !signalled) { publisherRunning = false; break; }
            }
            failed = false;
            foreach (var entry in pending)
            {
                try
                {
                    await record(new(entry.Id, entry.SendChatId!, entry.Started,
                        entry.Error is { OutcomeUnknown: true } or { Code: ProtocolErrorCodes.OutcomeUnknown })).ConfigureAwait(false);
                    lock (gate) { entry.SendChatId = null; version++; }
                }
                catch (Exception) { failed = true; log?.Invoke("outbox shared write failed reason=io"); }
            }
            ScheduleFlush();
        }
    }
}
