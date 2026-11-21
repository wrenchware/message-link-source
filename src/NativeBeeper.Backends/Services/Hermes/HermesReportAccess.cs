using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

/// <summary>
/// Who may post to a Hermes profile's thread from outside a turn: each profile's report token, its waiting setup and add codes, the limits on checks,
/// reports and probes, and what those remember per profile. The chat backend tells it which profiles are set up (<see cref="Commit"/>) and that one is gone
/// (<see cref="ProfilesGoneAsync"/>), and asks it about a token or a code; the thread a report lands in stays the backend's.
/// </summary>
/// <remarks>
/// A token belongs to its profile: a changed address or key keeps it and the profile's waiting codes (owner, 2026-11-21). A removed profile loses all of it,
/// so the same name added again starts with no token, no code and fresh limits. Token changes are saved under the chat backend's gate, which a report's
/// delivery holds from the token's lookup to the thread's save, so a token turned off never lets one more report in. Everything in memory is under
/// <see cref="sync"/>, which is also where the set of profiles changes: a code issued while a profile is being removed is either dropped with it or refused.
/// </remarks>
internal sealed class HermesReportAccess(HermesStore store, TimeProvider clock, Action<string> log, int maxProfiles)
{
    internal const string ReportSource = "hermes";

    /// <summary>Safety limits per profile (owner, 2026-11-15), counted from the stored reports' arrival times so a restart resets nothing.</summary>
    internal static readonly (TimeSpan Window, int Most)[] ReportLimits =
        [(TimeSpan.FromMinutes(1), 6), (TimeSpan.FromHours(1), 30), (TimeSpan.FromDays(1), 200)];

    /// <summary>Checks of a token (<c>GET /v1/reports/check</c>) per profile, in memory: their own allowance, apart from the reports'.</summary>
    internal static readonly (TimeSpan Window, int Most) CheckLimit = (TimeSpan.FromMinutes(1), 6);

    /// <summary>Probes of one setup code: a minute's allowance and a cap over its life. A probe never uses the code up.</summary>
    internal static readonly (TimeSpan Window, int Most, int Life) SetupProbeLimit = (TimeSpan.FromMinutes(1), 10, 30);

    /// <summary>The most an add code's command may be used without adding the profile (each use asks Hermes at every address it names), and the most addresses one
    /// use may name.</summary>
    internal const int MaxAddTries = 10, MaxAddAddresses = 4;

    /// <summary>The setup ids remembered for the page (to tell an expired or replaced code from one never made): the newest this many.</summary>
    internal const int SetupIdsKept = 256;

    private readonly object sync = new();
    private HermesReportTokens tokens = HermesReportTokens.Empty;
    // The profiles set up, in the settings' order, and Hermes's address, as of the last Commit.
    private string[] profiles = [];
    private string? address;

    // A code waiting to be used, by its hash (hex). An add code (Add) names a profile not set up yet: Added once its use saved the profile, so a lost answer or a
    // failed token save can finish with the same code; Problem and Tried say why its last use didn't, for the page.
    private readonly Dictionary<string, Code> pending = new(StringComparer.Ordinal);
    // The ids issued, with their profile and order, so the page can tell an expired or replaced code from one it never made.
    private readonly Dictionary<string, (string Profile, long Order)> setupIds = new(StringComparer.Ordinal);
    private long issued;
    // Probes by the code's hash (hex).
    private readonly Dictionary<string, Probes> probes = new(StringComparer.Ordinal);
    // The page's last-refusal line, the once-an-hour "too many" note (a restart may repeat it once), checks of a token in the last minute, and when the last
    // report arrived (the threads are read under the backend's gate only; the page reads this without it).
    private readonly Dictionary<string, (string Reason, DateTimeOffset At)> refusals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> tooManyNoted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastReports = new(StringComparer.Ordinal);

    internal sealed class Code(string hex, string profile, string id, DateTimeOffset expires, bool add, string? title)
    {
        public string Hex { get; } = hex;
        public string Profile { get; } = profile;
        public string Id { get; } = id;
        public DateTimeOffset Expires { get; } = expires;
        public bool Add { get; } = add;
        public string? Title { get; } = title;
        public bool Added { get; set; }
        public bool Adding { get; set; }
        public int Tries { get; set; }
        public string? Problem { get; set; }
        public IReadOnlyList<string>? Tried { get; set; }
    }

    private sealed class Probes(string profile)
    {
        public string Profile { get; } = profile;
        public List<DateTimeOffset> Recent { get; } = [];
        public int Total { get; set; }
    }

    // ---- the profiles ----

    /// <summary>The settings now in effect. A profile no longer in them loses its codes and everything remembered of it; <paramref name="apply"/> runs in the same
    /// step, so the backend's settings and these never disagree.</summary>
    public void Commit(HermesSettings settings, Action? apply = null)
    {
        lock (sync)
        {
            var names = settings.Profiles.Select(p => p.Name).ToArray();
            foreach (var gone in profiles.Except(names, StringComparer.Ordinal)) ForgetLocked(gone);
            apply?.Invoke();
            (profiles, address) = (names, settings.Address);
        }
    }

    /// <summary>
    /// The profiles are being removed: their tokens are forgotten on disk first (a crash before the settings are saved must not leave a token the same name added
    /// again would bring back, code review 2026-11-15), then their codes, probes and limits. Under the backend's gate, before the settings are saved; the
    /// <see cref="Commit"/> that follows drops anything issued for them meanwhile. A failed settings save after it leaves the profile with reports off.
    /// </summary>
    public async Task ProfilesGoneAsync(IReadOnlyCollection<string> gone)
    {
        if (gone.Count == 0) return;
        HermesReportTokens? next = null;
        lock (sync)
        {
            foreach (var profile in gone) ForgetLocked(profile);
            if (tokens.Rows.Any(r => gone.Contains(r.Profile))) next = new([.. tokens.Rows.Where(r => !gone.Contains(r.Profile))]);
        }
        if (next is null) return;
        await store.SaveReportTokensAsync(next).ConfigureAwait(false);
        lock (sync) tokens = next;
    }

    // Everything held for a profile, but its token row (ProfilesGoneAsync saves that).
    private void ForgetLocked(string profile)
    {
        foreach (var hex in pending.Where(p => p.Value.Profile == profile).Select(p => p.Key).ToArray()) pending.Remove(hex);
        foreach (var id in setupIds.Where(p => p.Value.Profile == profile).Select(p => p.Key).ToArray()) setupIds.Remove(id);
        foreach (var hex in probes.Where(p => p.Value.Profile == profile).Select(p => p.Key).ToArray()) probes.Remove(hex);
        refusals.Remove(profile);
        tooManyNoted.Remove(profile);
        checks.Remove(profile);
        lastReports.Remove(profile);
    }

    private bool HasLocked(string profile) => profiles.Contains(profile, StringComparer.Ordinal);

    // ---- tokens ----

    /// <summary>Reads the saved tokens; answers whether the file was there but couldn't be read (read as none until it can be).</summary>
    public async Task<bool> ReadTokensAsync()
    {
        var read = await store.ReadReportTokensAsync().ConfigureAwait(false);
        lock (sync) tokens = read;
        return store.ReportTokensHeld;
    }

    /// <summary>Forgets the tokens of profiles not set up. Only against settings that loaded with profiles: an unreadable settings file reads as none, and pruning
    /// against that would revoke every live token for good (code review, 2026-11-15).</summary>
    public Task PruneAsync()
    {
        string[] gone;
        lock (sync) gone = profiles.Length == 0 ? [] : [.. tokens.Rows.Select(r => r.Profile).Where(p => !HasLocked(p)).Distinct()];
        return ProfilesGoneAsync(gone);
    }

    public IReadOnlyList<AgentReportsState> State
    {
        get
        {
            lock (sync)
                return [.. profiles.Select(name =>
                {
                    var row = tokens.Rows.FirstOrDefault(r => r.Profile == name);
                    var refused = refusals.TryGetValue(name, out var r) ? r : default;
                    return new AgentReportsState(name, row is not null, row?.Created, row is null ? null : refused.Reason, row is null ? null : refused.Reason is null ? null : refused.At,
                        row?.Address, lastReports.TryGetValue(name, out var last) ? last : null, row?.SetupFrom);
                })];
        }
    }

    /// <summary>A new token for <paramref name="profile"/>: the old one and any waiting code stop. Under the backend's gate.</summary>
    public async Task<string> IssueTokenAsync(string profile, string? named)
    {
        var token = AgentReportRules.NewToken();
        var tag = AgentReportRules.Tag(token);
        HermesReportTokens next;
        lock (sync)
        {
            if (!HasLocked(profile)) throw new ArgumentException("This Hermes profile isn't set up.");
            // So a setup command's redemption waiting for the gate finds its code gone rather than replacing this token.
            DropCodesLocked(profile);
            next = new([.. tokens.Rows.Where(r => r.Profile != profile),
                new(profile, ReportSource, Convert.ToHexStringLower(AgentReportRules.Hash(token)), tag, clock.GetUtcNow(), string.IsNullOrWhiteSpace(named) ? null : named.Trim())]);
        }
        await store.SaveReportTokensAsync(next).ConfigureAwait(false);
        lock (sync)
        {
            tokens = next;
            refusals.Remove(profile);
        }
        log($"hermes reports on profile={HermesBackend.Tag(profile)} tag={tag}");
        return token;
    }

    /// <summary>Reports off for <paramref name="profile"/>: its token and waiting codes stop. Under the backend's gate.</summary>
    public async Task TurnOffAsync(string profile)
    {
        HermesReportTokens? next = null;
        lock (sync)
        {
            // So a setup command's redemption waiting for the gate can't turn reports back on.
            DropCodesLocked(profile);
            if (tokens.Rows.Any(r => r.Profile == profile)) next = new([.. tokens.Rows.Where(r => r.Profile != profile)]);
        }
        if (next is not null)
        {
            await store.SaveReportTokensAsync(next).ConfigureAwait(false);
            lock (sync) tokens = next;
        }
        lock (sync) refusals.Remove(profile);
        log($"hermes reports off profile={HermesBackend.Tag(profile)}");
    }

    /// <summary>A token's row, found by its hash; none for a profile no longer set up.</summary>
    public HermesReportToken? Token(byte[] tokenHash)
    {
        if (tokenHash.Length != 32) return null;
        var hex = Convert.ToHexStringLower(tokenHash);
        lock (sync) return tokens.Rows.FirstOrDefault(r => r.Hash == hex) is { } row && HasLocked(row.Profile) ? row : null;
    }

    public void Refused(byte[] tokenHash, string reason)
    {
        if (Token(tokenHash) is { } row)
            lock (sync) refusals[row.Profile] = (reason, clock.GetUtcNow());
    }

    /// <summary>A check of a token. Checks have a minute's allowance of their own, so the plugin's check at connect or a setup's checks never leave a long
    /// message short of reports (design review, 2026-11-16: two checks could stop a five-part message). Reports never count them.</summary>
    public AgentReportResult Check(byte[] tokenHash)
    {
        if (Token(tokenHash) is not { } row) return new(AgentReportOutcome.Unknown);
        var now = clock.GetUtcNow();
        var minute = CheckLimit;
        lock (sync)
        {
            if (!checks.TryGetValue(row.Profile, out var recent)) checks[row.Profile] = recent = [];
            recent.RemoveAll(at => now - at >= minute.Window);
            if (recent.Count >= minute.Most) return new(AgentReportOutcome.Limited, RetryAfter: Wait(recent.Order().ToArray()[^minute.Most], minute.Window, now), Tag: row.Tag);
            recent.Add(now);
        }
        return new(AgentReportOutcome.Ready, Tag: row.Tag);
    }

    /// <summary>Whether a report arriving <paramref name="now"/> is over a limit, given the arrival times of the profile's stored reports, and how long until it
    /// is not.</summary>
    public static (bool Limited, TimeSpan RetryAfter) Limit(IEnumerable<DateTimeOffset> arrivals, DateTimeOffset now)
    {
        var all = arrivals.ToArray();
        var limited = false;
        TimeSpan retry = default;
        foreach (var (window, most) in ReportLimits)
        {
            var ordered = all.Where(at => now - at < window).Order().ToArray();
            if (ordered.Length < most) continue;
            var wait = Wait(ordered[^most], window, now);
            if (wait > retry) retry = wait;
            limited = true;
        }
        return (limited, retry);
    }

    /// <summary>Whether the thread's "too many" note is due: at most once an hour, claimed atomically, so two refusals at the same moment add one note.</summary>
    public bool ClaimTooManyNote(string profile)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (tooManyNoted.TryGetValue(profile, out var noted) && now - noted < TimeSpan.FromHours(1)) return false;
            tooManyNoted[profile] = now;
            return true;
        }
    }

    /// <summary>A report arrived (or, at load, the thread's last one had).</summary>
    public void Arrived(string profile, DateTimeOffset at)
    {
        lock (sync) lastReports[profile] = at;
    }

    // Until the oldest of the window's newest `most` leaves it; at least a second.
    private static TimeSpan Wait(DateTimeOffset oldest, TimeSpan window, DateTimeOffset now) =>
        oldest + window - now is var wait && wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);

    // ---- setup and add codes ----

    /// <summary>A setup code for a profile set up here; replaces any it has waiting.</summary>
    public AgentSetupIssue IssueSetupCode(string profile) => Issue(profile, add: false, null);

    /// <summary>An add code for <paramref name="name"/>, already checked for shape by the backend: refused while it is set up, while the profiles are full, or while
    /// a command is adding it.</summary>
    public AgentSetupIssue IssueAddCode(string name, string? title) => Issue(name, add: true, title);

    private AgentSetupIssue Issue(string profile, bool add, string? title)
    {
        var code = AgentReportRules.NewSetupCode();
        var hash = AgentReportRules.Hash(code);
        var id = AgentReportRules.SetupTag(hash);
        var expires = clock.GetUtcNow() + AgentReportRules.SetupLifetime;
        lock (sync)
        {
            // Asked here, under the lock where the profiles change: asked against the settings outside it, a code could land after a removal dropped the
            // profile's codes and before the new settings took effect, and the same name added again within ten minutes would take it.
            if (!add && !HasLocked(profile)) throw new ArgumentException("This Hermes profile isn't set up.");
            if (add)
            {
                if (HasLocked(profile)) throw new ArgumentException("That profile is already an assistant here.");
                if (profiles.Length >= maxProfiles) throw new ArgumentException($"Hermes can have up to {maxProfiles} profiles here.");
                // Not while a command for the name is adding it: replacing its code then would leave the profile added with reports off.
                if (pending.Values.Any(p => p.Profile == profile && p.Adding)) throw new ArgumentException("A command is adding this profile now. Try again in a minute.");
            }
            DropCodesLocked(profile);
            var hex = Convert.ToHexStringLower(hash);
            pending[hex] = new(hex, profile, id, expires, add, title);
            setupIds[id] = (profile, ++issued);
            if (setupIds.Count > SetupIdsKept) setupIds.Remove(setupIds.MinBy(p => p.Value.Order).Key);
        }
        log($"hermes setup outcome=issued{(add ? " kind=add" : "")} profile={HermesBackend.Tag(profile)} id={id}");
        return new(code, id, expires);
    }

    // A profile's waiting codes stop: a newer one, a token made by hand, or reports turned off.
    private void DropCodesLocked(string profile)
    {
        foreach (var hex in pending.Where(p => p.Value.Profile == profile).Select(p => p.Key).ToArray()) Remove(hex);
    }

    private void Remove(string hex)
    {
        pending.Remove(hex);
        probes.Remove(hex);
    }

    // A pending code still usable: not expired, and its profile still set up; for an add code, its profile not set up yet until this code adds it (one added by
    // hand meanwhile isn't this code's to finish), and set up after.
    private Code? PendingLocked(string hex)
    {
        if (!pending.TryGetValue(hex, out var code)) return null;
        // An add code that has added its profile no longer runs out: what remains is turning reports on, and a code dropped then would leave the profile
        // added with reports off and its command unable to finish (code review, 2026-11-16).
        if ((clock.GetUtcNow() < code.Expires || code.Added || code.Adding) && HasLocked(code.Profile) == (!code.Add || code.Added)) return code;
        Remove(hex);
        return null;
    }

    // The token row a code was used for, while its profile is set up.
    private HermesReportToken? UsedForLocked(string hex) => tokens.Rows.FirstOrDefault(r => r.SetupHash == hex && HasLocked(r.Profile));

    public AgentSetupResult Probe(byte[] codeHash)
    {
        if (codeHash.Length != 32) return new(AgentSetupOutcome.Unknown);
        var hex = Convert.ToHexStringLower(codeHash);
        var id = AgentReportRules.SetupTag(codeHash);
        var now = clock.GetUtcNow();
        lock (sync)
        {
            var profile = PendingLocked(hex)?.Profile ?? UsedForLocked(hex)?.Profile;
            if (profile is null) return new(AgentSetupOutcome.Unknown);
            if (!probes.TryGetValue(hex, out var seen)) probes[hex] = seen = new(profile);
            seen.Recent.RemoveAll(at => now - at >= SetupProbeLimit.Window);
            if (seen.Total >= SetupProbeLimit.Life) return new(AgentSetupOutcome.Unknown, Tag: id);
            if (seen.Recent.Count >= SetupProbeLimit.Most)
                return new(AgentSetupOutcome.Limited, Wait(seen.Recent.Order().ToArray()[^SetupProbeLimit.Most], SetupProbeLimit.Window, now), id);
            seen.Recent.Add(now);
            seen.Total++;
        }
        return new(AgentSetupOutcome.Ready, Tag: id);
    }

    /// <summary>What a code's use must do before its token is saved: refuse (an add code used without a key, or a key sent with a reports code), add its
    /// profile first (<paramref name="toAdd"/>: an add code that hasn't), or neither.</summary>
    public AgentSetupResult? BeforeRedeem(byte[] codeHash, bool withKey, out Code? toAdd)
    {
        toAdd = null;
        Code? code;
        lock (sync) code = PendingLocked(Convert.ToHexStringLower(codeHash));
        if (code is not null && code.Add != withKey) return new(AgentSetupOutcome.Unknown, Tag: code.Id);
        if (code is { Add: true, Added: false }) toAdd = code;
        return null;
    }

    /// <summary>
    /// One use of an add code that hasn't added its profile: <paramref name="addProfile"/> asks Hermes and saves the profile, and answers null once it is saved,
    /// otherwise why not. One use at a time (two at once would both ask Hermes and both save the profile), and at most <see cref="MaxAddTries"/>.
    /// </summary>
    public async Task<AgentSetupResult?> AddAsync(Code code, string key, Func<string, Task<AgentSetupResult?>> addProfile)
    {
        key = key.Trim();
        if (key.Length is 0 or > 512 || key.Any(c => c is < '!' or > '~')) return new(AgentSetupOutcome.Unknown, Tag: code.Id);
        lock (sync)
        {
            if (code.Adding) return new(AgentSetupOutcome.Unavailable, Tag: code.Id);
            if (++code.Tries > MaxAddTries)
            {
                // Used up for good: the command then says to make a new one.
                if (pending.GetValueOrDefault(code.Hex) == code) Remove(code.Hex);
                log($"hermes setup outcome=add_tries_spent profile={HermesBackend.Tag(code.Profile)} id={code.Id}");
                return new(AgentSetupOutcome.Unknown, Tag: code.Id);
            }
            code.Adding = true;
        }
        try { return await addProfile(key).ConfigureAwait(false); }
        finally { lock (sync) code.Adding = false; }
    }

    /// <summary>Why an add code's last use didn't add its profile, for the page.</summary>
    public void AddFailed(Code code, string problem, IReadOnlyList<string> tried)
    {
        lock (sync) (code.Problem, code.Tried) = (problem, tried);
    }

    /// <summary>Whether <paramref name="code"/> is still its profile's to add (not replaced, run out or added by hand meanwhile).</summary>
    public bool StillPending(Code code)
    {
        lock (sync) return PendingLocked(code.Hex) == code;
    }

    /// <summary>The add code's profile is saved: called inside the <see cref="Commit"/> that sets it up, so the code is never seen with its profile there and not
    /// yet its own (code review, 2026-11-16: a page's look in between dropped the code).</summary>
    public void Added(Code code)
    {
        lock (sync) (code.Added, code.Problem, code.Tried) = (true, null, null);
    }

    /// <summary>
    /// Uses a code: the profile's token becomes the one whose hash is <paramref name="tokenHash"/>. Used already, the same token's hash is the same setup (its
    /// answer was lost), done again with nothing changed; any other was someone else. Under the backend's gate.
    /// </summary>
    public async Task<AgentSetupResult> RedeemAsync(byte[] codeHash, byte[] tokenHash, string named, string? peer)
    {
        var hex = Convert.ToHexStringLower(codeHash);
        var tokenHex = Convert.ToHexStringLower(tokenHash);
        var id = AgentReportRules.SetupTag(codeHash);
        string profile;
        HermesReportTokens next;
        lock (sync)
        {
            if (UsedForLocked(hex) is { } used)
            {
                var same = used.Hash == tokenHex;
                log($"hermes setup outcome={(same ? "repeat" : "already_used")} profile={HermesBackend.Tag(used.Profile)} id={id}");
                return new(same ? AgentSetupOutcome.Done : AgentSetupOutcome.AlreadyUsed, Tag: id, Hermes: same ? address : null);
            }
            if (PendingLocked(hex) is not { } code) return new(AgentSetupOutcome.Unknown, Tag: id);
            profile = code.Profile;
            // A token's hash already in use (another profile's, or this one's current) is refused: each profile's token is its own.
            if (tokens.Rows.Any(r => r.Hash == tokenHex)) return new(AgentSetupOutcome.AlreadyUsed, Tag: id);
            next = new([.. tokens.Rows.Where(r => r.Profile != profile),
                new(profile, ReportSource, tokenHex, AgentReportRules.TagOfHash(tokenHash), clock.GetUtcNow(), named, hex, code.Id, peer)]);
        }
        // The old token keeps working until this is on disk; a failed save leaves the code waiting and the old token as it was.
        await store.SaveReportTokensAsync(next).ConfigureAwait(false);
        lock (sync)
        {
            tokens = next;
            pending.Remove(hex);
            probes.Remove(hex);
            refusals.Remove(profile);
        }
        log($"hermes setup outcome=done profile={HermesBackend.Tag(profile)} id={id} tag={AgentReportRules.TagOfHash(tokenHash)}");
        return new(AgentSetupOutcome.Done, Tag: id, Hermes: HermesAddress);
    }

    public string? HermesAddress
    {
        get { lock (sync) return address; }
    }

    public AgentSetupState? SetupState(string profile, string id)
    {
        lock (sync)
        {
            // Done is read from the token's row first: it outlives a restart, which forgets the ids issued.
            if (tokens.Rows.FirstOrDefault(r => r.Profile == profile) is { } row && row.SetupId == id && HasLocked(profile))
                return new(AgentSetupPhase.Done, At: row.Created, Address: row.Address, From: row.SetupFrom, Hermes: address);
            if (!setupIds.TryGetValue(id, out var issuedFor) || issuedFor.Profile != profile) return null;
            var waiting = pending.FirstOrDefault(p => p.Value.Id == id);
            if (waiting.Value is { } code)
                return PendingLocked(waiting.Key) is not null ? new(AgentSetupPhase.Waiting, code.Expires, Problem: code.Problem, Tried: code.Tried) : new(AgentSetupPhase.Expired);
            // Gone without being used here: replaced when the profile has a newer code waiting or a token made since; otherwise it ran out.
            return pending.Values.Any(p => p.Profile == profile) ? new(AgentSetupPhase.Replaced) : new(AgentSetupPhase.Expired);
        }
    }
}
