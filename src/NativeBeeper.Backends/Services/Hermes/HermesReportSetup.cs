using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

// Reports set up in one command (docs/research/hermes-reports-plan.md, "Setup in one command"): the page shows a command carrying a setup code; where Hermes
// runs, the installer makes the profile's report token itself and sends only its hash with the setup code, which then stops working. The server keeps a pending
// code's hash in memory only (a restart loses it, and the owner makes another), and a used one's hash on the token row, so a setup whose answer was lost can
// finish with the same token's hash even after a restart, and another token's hash is told the code was already used.
internal sealed partial class HermesBackend
{
    /// <summary>Probes of one setup code: a minute's allowance and a cap over its life. A probe never uses the code up.</summary>
    internal static readonly (TimeSpan Window, int Most, int Life) SetupProbeLimit = (TimeSpan.FromMinutes(1), 10, 30);

    private sealed class PendingSetup(string profile, string id, DateTimeOffset expires)
    {
        public string Profile { get; } = profile;
        public string Id { get; } = id;
        public DateTimeOffset Expires { get; } = expires;
    }

    // By the code's hash (hex). Under setupLock.
    private readonly Dictionary<string, PendingSetup> pendingSetups = new(StringComparer.Ordinal);
    // Every id issued since start, with its profile, so the page can tell an expired or replaced code from one it never made. Under setupLock.
    private readonly Dictionary<string, string> setupIds = new(StringComparer.Ordinal);
    // Probes by the code's hash (hex). Under setupLock.
    private readonly Dictionary<string, (List<DateTimeOffset> Recent, int Total)> setupProbes = new(StringComparer.Ordinal);
    private readonly object setupLock = new();

    public AgentSetupIssue IssueSetupCode(string profile)
    {
        if (!Has(profile)) throw new ArgumentException("This Hermes profile isn't set up.");
        var code = AgentReportRules.NewSetupCode();
        var hash = AgentReportRules.Hash(code);
        var id = AgentReportRules.SetupTag(hash);
        var expires = clock.GetUtcNow() + AgentReportRules.SetupLifetime;
        lock (setupLock)
        {
            DropSetupCodesLocked(profile);
            pendingSetups[Convert.ToHexStringLower(hash)] = new(profile, id, expires);
            setupIds[id] = profile;
        }
        log($"hermes setup outcome=issued profile={Tag(profile)} id={id}");
        return new(code, id, expires);
    }

    // A profile's waiting codes stop: a newer one, a code made by hand, or reports turned off.
    private void DropSetupCodes(string profile)
    {
        lock (setupLock) DropSetupCodesLocked(profile);
    }

    private void DropSetupCodesLocked(string profile)
    {
        foreach (var hex in pendingSetups.Where(p => p.Value.Profile == profile).Select(p => p.Key).ToArray()) pendingSetups.Remove(hex);
    }

    // A pending code still usable: not expired, and its profile still set up. Under setupLock.
    private PendingSetup? Pending(string hex)
    {
        if (!pendingSetups.TryGetValue(hex, out var pending)) return null;
        if (clock.GetUtcNow() < pending.Expires && Has(pending.Profile)) return pending;
        pendingSetups.Remove(hex);
        return null;
    }

    // The token row a code was used for, while its profile is set up.
    private HermesReportToken? UsedFor(string hex) => reportTokens.Rows.FirstOrDefault(r => r.SetupHash == hex && Has(r.Profile));

    public AgentSetupResult ProbeSetupCode(byte[] codeHash)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentSetupOutcome.Unavailable);
        if (codeHash.Length != 32) return new(AgentSetupOutcome.Unknown);
        var hex = Convert.ToHexStringLower(codeHash);
        var id = AgentReportRules.SetupTag(codeHash);
        var now = clock.GetUtcNow();
        lock (setupLock)
        {
            if (Pending(hex) is null && UsedFor(hex) is null) return new(AgentSetupOutcome.Unknown);
            var (recent, total) = setupProbes.TryGetValue(hex, out var seen) ? seen : ([], 0);
            recent.RemoveAll(at => now - at >= SetupProbeLimit.Window);
            if (total >= SetupProbeLimit.Life) return new(AgentSetupOutcome.Unknown, Tag: id);
            if (recent.Count >= SetupProbeLimit.Most)
                return new(AgentSetupOutcome.Limited, Wait(recent.Order().ToArray()[^SetupProbeLimit.Most], SetupProbeLimit.Window, now), id);
            recent.Add(now);
            setupProbes[hex] = (recent, total + 1);
        }
        return new(AgentSetupOutcome.Ready, Tag: id);
    }

    public async Task<AgentSetupResult> RedeemSetupCodeAsync(byte[] codeHash, byte[] tokenHash, string address, string? peer, CancellationToken cancellationToken = default)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentSetupOutcome.Unavailable);
        if (codeHash.Length != 32 || tokenHash.Length != 32) return new(AgentSetupOutcome.Unknown);
        var hex = Convert.ToHexStringLower(codeHash);
        var tokenHex = Convert.ToHexStringLower(tokenHash);
        var id = AgentReportRules.SetupTag(codeHash);
        string profile;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Used already: the same token's hash is the same setup (its answer was lost), done again with nothing changed; any other was someone else.
            if (UsedFor(hex) is { } used)
            {
                var same = used.Hash == tokenHex;
                log($"hermes setup outcome={(same ? "repeat" : "already_used")} profile={Tag(used.Profile)} id={id}");
                return new(same ? AgentSetupOutcome.Done : AgentSetupOutcome.AlreadyUsed, Tag: id);
            }
            PendingSetup? pending;
            lock (setupLock) pending = Pending(hex);
            if (pending is null) return new(AgentSetupOutcome.Unknown, Tag: id);
            profile = pending.Profile;
            // A token's hash already in use (another profile's, or this one's current) is refused: each profile's token is its own.
            if (reportTokens.Rows.Any(r => r.Hash == tokenHex)) return new(AgentSetupOutcome.AlreadyUsed, Tag: id);
            var next = new HermesReportTokens([.. reportTokens.Rows.Where(r => r.Profile != profile),
                new(profile, ReportSource, tokenHex, AgentReportRules.TagOfHash(tokenHash), clock.GetUtcNow(), address, hex, pending.Id, peer)]);
            // The old token keeps working until this is on disk; a failed save leaves the code waiting and the old token as it was.
            await store.SaveReportTokensAsync(next).ConfigureAwait(false);
            reportTokens = next;
            lock (setupLock)
            {
                pendingSetups.Remove(hex);
                setupProbes.Remove(hex);
            }
            reportRefusals.TryRemove(profile, out _);
        }
        finally { gate.Release(); }
        log($"hermes setup outcome=done profile={Tag(profile)} id={id} tag={AgentReportRules.TagOfHash(tokenHash)}");
        return new(AgentSetupOutcome.Done, Tag: id);
    }

    public AgentSetupState? SetupState(string profile, string id)
    {
        lock (setupLock)
        {
            // Done is read from the token's row first: it outlives a restart, which forgets the ids issued.
            if (reportTokens.Rows.FirstOrDefault(r => r.Profile == profile) is { } row && row.SetupId == id && Has(profile))
                return new(AgentSetupPhase.Done, At: row.Created, Address: row.Address, From: row.SetupFrom);
            if (!setupIds.TryGetValue(id, out var issued) || issued != profile) return null;
            var waiting = pendingSetups.FirstOrDefault(p => p.Value.Id == id);
            if (waiting.Value is { } pending)
                return Pending(waiting.Key) is not null ? new(AgentSetupPhase.Waiting, pending.Expires) : new(AgentSetupPhase.Expired);
            // Gone without being used here: replaced when the profile has a newer code waiting or a token made since; otherwise it ran out.
            return pendingSetups.Values.Any(p => p.Profile == profile) ? new(AgentSetupPhase.Replaced) : new(AgentSetupPhase.Expired);
        }
    }
}
