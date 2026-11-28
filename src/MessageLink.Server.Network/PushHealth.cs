using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>What became of the owner's test push, as the server's page says it.</summary>
internal enum PushTestState { Waiting, Shown, TimedOut, Refused, Unreachable, Gone, NotSignedUp }

/// <summary>
/// The owner's test push to one phone: its id (the push's <c>MessageId</c>, which the phone answers with <c>push.testShown</c>), the phone, when it went, how it
/// stands, Google's status when Google refused it, and when the phone said it showed it.
/// </summary>
internal sealed record PushTest(string Id, string DeviceId, string Name, DateTimeOffset At, PushTestState State, int Status = 0, DateTimeOffset? ShownAt = null);

/// <summary>
/// How the server's pushes are doing, kept in memory since the start: when one last went, the failures per phone since the last that went to
/// it with the newest reason, which phones asked for the offer while pushes were on (a PC never does), and the owner's test push. The page's card and
/// <c>push.status</c> read it. Logged by outcome and counts only: <c>push test sent outcome= status=</c>, <c>push test shown ms=</c>; never a token or a name.
/// </summary>
internal sealed class PushHealth(TimeProvider time, Action<string> log)
{
    /// <summary>How long a test push is waited for before the page says it was not shown.</summary>
    public static readonly TimeSpan TestWait = TimeSpan.FromSeconds(30);

    private sealed record Failure(int Count, string Reason, int Status);

    private readonly object gate = new();
    private readonly Dictionary<string, Failure> failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> asked = new(StringComparer.Ordinal);
    private DateTimeOffset? lastSent;
    private PushTest? test;

    /// <summary>When a push last went to any phone, since the start.</summary>
    public DateTimeOffset? LastSent { get { lock (gate) return lastSent; } }

    /// <summary>A phone asked what the server offers while pushes were on.</summary>
    public void Asked(string deviceId) { lock (gate) asked.Add(deviceId); }

    /// <summary>A push went to <paramref name="deviceId"/>, or failed: its failures start again from a push that went.</summary>
    public void Report(string deviceId, PushResult result)
    {
        lock (gate)
        {
            if (result.Outcome == PushOutcome.Sent)
            {
                lastSent = time.GetUtcNow();
                failures.Remove(deviceId);
                return;
            }
            var reason = result.Outcome == PushOutcome.Gone ? PushFailureReasons.Gone : result.Status > 0 ? PushFailureReasons.Refused : PushFailureReasons.Unreachable;
            failures[deviceId] = new((failures.GetValueOrDefault(deviceId)?.Count ?? 0) + 1, reason, result.Status);
        }
    }

    /// <summary>The device was removed: nothing of it is kept.</summary>
    public void Forget(string deviceId)
    {
        lock (gate)
        {
            failures.Remove(deviceId);
            asked.Remove(deviceId);
            if (test?.DeviceId == deviceId) test = null;
        }
    }

    /// <summary>The newest test push as it stands now: one waited on past <see cref="TestWait"/> is <see cref="PushTestState.TimedOut"/>.</summary>
    public PushTest? Test
    {
        get
        {
            lock (gate)
                return test is { State: PushTestState.Waiting } waiting && time.GetUtcNow() - waiting.At > TestWait ? waiting with { State = PushTestState.TimedOut } : test;
        }
    }

    /// <summary>
    /// Sends a test push to <paramref name="deviceId"/> through <paramref name="transport"/>, urgent since the phone shows it: kind <c>test</c>, its id as the
    /// message's. A phone without a registration gets nothing; one Google says is gone is forgotten (it signs up again when it connects). One test at a time:
    /// a new one replaces the last.
    /// </summary>
    public async Task<PushTest> SendTestAsync(string deviceId, string name, PushRegistry registry, IPushTransport transport, CancellationToken cancellationToken = default)
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        var at = time.GetUtcNow();
        if (registry.Get(deviceId) is not { } device || !registry.Paired(deviceId))
        {
            log("push test sent outcome=not_signed_up status=0");
            return Keep(new(id, deviceId, name, at, PushTestState.NotSignedUp));
        }
        // Waited on before it goes, so an answer quicker than Google's own is not missed.
        Keep(new(id, deviceId, name, at, PushTestState.Waiting));
        var result = await PushNotifier.DeliverAsync(new PushMessage(1, "test", "", id), device, transport, registry, this, urgent: true, sync: false, cancellationToken)
            .ConfigureAwait(false) ?? new PushResult(PushOutcome.Failed);
        var state = result.Outcome switch
        {
            PushOutcome.Sent => PushTestState.Waiting,
            PushOutcome.Gone => PushTestState.Gone,
            _ => result.Status > 0 ? PushTestState.Refused : PushTestState.Unreachable,
        };
        log($"push test sent outcome={(state == PushTestState.Waiting ? "sent" : state.ToString().ToLowerInvariant())} status={result.Status}");
        lock (gate)
        {
            // Shown already (the phone answered before Google did), or replaced by a newer test: left as it is.
            if (test?.Id != id || test.State != PushTestState.Waiting) return test is { } kept && kept.Id == id ? kept : new(id, deviceId, name, at, state, result.Status);
            return test = test with { State = state, Status = result.Status };
        }
    }

    private PushTest Keep(PushTest made)
    {
        lock (gate) return test = made;
    }

    /// <summary>The phone <paramref name="deviceId"/> showed test <paramref name="id"/>: true when that was the test being waited on (even past its wait).</summary>
    public bool Shown(string deviceId, string id)
    {
        lock (gate)
        {
            if (test is not { State: PushTestState.Waiting } waiting || waiting.Id != id || waiting.DeviceId != deviceId)
            {
                log("push test shown outcome=unknown");
                return false;
            }
            var now = time.GetUtcNow();
            test = waiting with { State = PushTestState.Shown, ShownAt = now };
            log($"push test shown ms={(long)(now - waiting.At).TotalMilliseconds}");
            return true;
        }
    }

    /// <summary>
    /// How pushes stand, for the page and <c>push.status</c>: each paired device that signed up (failing or not), reported a missing app, or asked and never signed up,
    /// by name. A device that never asked (a PC) is left out.
    /// </summary>
    public PushStatus Status(bool available, PushRegistry registry, IEnumerable<PairedDevice> devices)
    {
        if (!available) return new PushStatus(false);
        var missing = registry.Missing().ToDictionary(report => report.DeviceId, StringComparer.Ordinal);
        var phones = new List<PushPhone>();
        lock (gate)
            foreach (var device in devices)
            {
                if (registry.Get(device.Id) is not null)
                    phones.Add(failures.TryGetValue(device.Id, out var failed)
                        ? new PushPhone(device.Name, PushPhoneStates.Failing, failed.Count, failed.Reason, failed.Status)
                        : new PushPhone(device.Name, PushPhoneStates.On));
                else if (missing.TryGetValue(device.Id, out var report)) phones.Add(new PushPhone(device.Name, PushPhoneStates.MissingApp, Package: report.Package));
                else if (failures.TryGetValue(device.Id, out var gone) && gone.Reason == PushFailureReasons.Gone)
                    phones.Add(new PushPhone(device.Name, PushPhoneStates.Failing, gone.Count, gone.Reason, gone.Status));
                else if (asked.Contains(device.Id)) phones.Add(new PushPhone(device.Name, PushPhoneStates.NotSignedUp));
            }
        return new PushStatus(true, LastSent, [.. phones.OrderBy(phone => phone.Name, StringComparer.CurrentCulture)]);
    }
}
