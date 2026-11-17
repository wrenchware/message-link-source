using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

// Reports set up in one command (docs/research/hermes-reports-plan.md, "Setup in one command"): the page shows a command carrying a setup code; where Hermes
// runs, the installer makes the profile's report token itself and sends only its hash with the setup code, which then stops working. The server keeps a pending
// code's hash in memory only (a restart loses it, and the owner makes another), and a used one's hash on the token row, so a setup whose answer was lost can
// finish with the same token's hash even after a restart, and another token's hash is told the code was already used. An add code (add an assistant in one
// command, ServerPage.HermesAdd.cs) names a profile not set up yet: its use also carries the profile's API key, and the profile is added once Hermes takes the
// key, before its token is saved as any other code's.
internal sealed partial class HermesBackend
{
    /// <summary>Probes of one setup code: a minute's allowance and a cap over its life. A probe never uses the code up.</summary>
    internal static readonly (TimeSpan Window, int Most, int Life) SetupProbeLimit = (TimeSpan.FromMinutes(1), 10, 30);

    /// <summary>The most an add code's command may be used without adding the profile (each use asks Hermes at every address it names), and the most addresses one
    /// use may name.</summary>
    internal const int MaxAddTries = 10, MaxAddAddresses = 4;
    /// <summary>How long the server waits for Hermes at each address an add code's command names.</summary>
    internal TimeSpan AddProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // A code waiting to be used. An add code (Add) names a profile not set up yet: Added once its use saved the profile, so a lost answer or a failed token save
    // can finish with the same code; Problem and Tried say why its last use didn't, for the page.
    private sealed class PendingSetup(string profile, string id, DateTimeOffset expires, bool add = false, string? title = null)
    {
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

    public AgentSetupIssue IssueAddCode(string profile, string? title)
    {
        if (loaded is not { IsCompletedSuccessfully: true }) throw new ArgumentException("The server is still starting. Try again in a minute.");
        var name = profile.Trim().ToLowerInvariant();
        if (!HermesClient.ValidProfile(name)) throw new ArgumentException("A profile name uses lowercase letters, numbers, hyphens and underscores, as in Hermes.");
        if (Has(name)) throw new ArgumentException("That profile is already an assistant here.");
        if (settings.Profiles.Count >= MaxProfiles) throw new ArgumentException($"Hermes can have up to {MaxProfiles} profiles here.");
        var given = title?.Trim() is { Length: > 0 } named && named != DefaultTitle(name) ? named : null;
        if (given is not null && (given.Any(char.IsControl) || given.Length > MaxTitleLength))
            throw new ArgumentException($"A conversation name can have up to {MaxTitleLength} characters, on one line.");
        var code = AgentReportRules.NewSetupCode();
        var hash = AgentReportRules.Hash(code);
        var id = AgentReportRules.SetupTag(hash);
        var expires = clock.GetUtcNow() + AgentReportRules.SetupLifetime;
        lock (setupLock)
        {
            // Not while a command for the name is adding it: replacing its code then would leave the profile added with reports off.
            if (pendingSetups.Values.Any(p => p.Profile == name && p.Adding)) throw new ArgumentException("A command is adding this profile now. Try again in a minute.");
            DropSetupCodesLocked(name);
            pendingSetups[Convert.ToHexStringLower(hash)] = new(name, id, expires, add: true, given);
            setupIds[id] = name;
        }
        log($"hermes setup outcome=issued kind=add profile={Tag(name)} id={id}");
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

    // A pending code still usable: not expired, and its profile still set up; for an add code, its profile not set up yet until this code adds it (one added by
    // hand meanwhile isn't this code's to finish), and set up after. Under setupLock.
    private PendingSetup? Pending(string hex)
    {
        if (!pendingSetups.TryGetValue(hex, out var pending)) return null;
        // An add code that has added its profile no longer runs out: what remains is turning reports on, and a code dropped then would leave the profile
        // added with reports off and its command unable to finish (code review, 2026-11-16).
        if ((clock.GetUtcNow() < pending.Expires || pending.Added || pending.Adding) && Has(pending.Profile) == (!pending.Add || pending.Added)) return pending;
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

    public async Task<AgentSetupResult> RedeemSetupCodeAsync(byte[] codeHash, byte[] tokenHash, string address, string? peer, CancellationToken cancellationToken = default,
        AgentAddRequest? add = null)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentSetupOutcome.Unavailable);
        if (codeHash.Length != 32 || tokenHash.Length != 32) return new(AgentSetupOutcome.Unknown);
        var hex = Convert.ToHexStringLower(codeHash);
        var tokenHex = Convert.ToHexStringLower(tokenHash);
        var id = AgentReportRules.SetupTag(codeHash);
        // An add code adds its profile first, outside the gate: asking Hermes takes seconds, and every thread waits on the gate. An add code used without a key,
        // or a key sent with a reports code, isn't this command's.
        PendingSetup? adding;
        lock (setupLock) adding = Pending(hex);
        if (adding is not null && adding.Add != add is not null) return new(AgentSetupOutcome.Unknown, Tag: id);
        if (adding is { Add: true, Added: false } && await AddAsync(hex, adding, add!, peer, id, cancellationToken).ConfigureAwait(false) is { } refused) return refused;
        string profile;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Used already: the same token's hash is the same setup (its answer was lost), done again with nothing changed; any other was someone else.
            if (UsedFor(hex) is { } used)
            {
                var same = used.Hash == tokenHex;
                log($"hermes setup outcome={(same ? "repeat" : "already_used")} profile={Tag(used.Profile)} id={id}");
                return new(same ? AgentSetupOutcome.Done : AgentSetupOutcome.AlreadyUsed, Tag: id, Hermes: same ? settings.Address : null);
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
        return new(AgentSetupOutcome.Done, Tag: id, Hermes: settings.Address);
    }

    // Adds an add code's profile. Its key is tried at the saved address first and, only when nothing answers there and no assistant depends on it, at each address the command named and at the
    // address the request came from. Hermes answering on the saved address settles it: one Hermes serves every profile here, and another one answering
    // elsewhere would move every assistant to it. Null once the profile is saved; otherwise why not, with nothing changed and the code still waiting.
    private async Task<AgentSetupResult?> AddAsync(string hex, PendingSetup pending, AgentAddRequest add, string? peer, string id, CancellationToken cancellationToken)
    {
        var key = add.Key.Trim();
        if (key.Length is 0 or > 512 || key.Any(c => c is < '!' or > '~')) return new(AgentSetupOutcome.Unknown, Tag: id);
        // One use at a time: two at once would both ask Hermes and both save the profile.
        lock (setupLock)
        {
            if (pending.Adding) return new(AgentSetupOutcome.Unavailable, Tag: id);
            if (++pending.Tries > MaxAddTries)
            {
                // Used up for good: the command then says to make a new one.
                pendingSetups.Remove(hex);
                log($"hermes setup outcome=add_tries_spent profile={Tag(pending.Profile)} id={id}");
                return new(AgentSetupOutcome.Unknown, Tag: id);
            }
            pending.Adding = true;
        }
        try { return await AddCoreAsync(hex, pending, add, key, peer, id, cancellationToken).ConfigureAwait(false); }
        finally { lock (setupLock) pending.Adding = false; }
    }

    private async Task<AgentSetupResult?> AddCoreAsync(string hex, PendingSetup pending, AgentAddRequest add, string key, string? peer, string id, CancellationToken cancellationToken)
    {
        var savedText = settings.Address;
        var saved = HermesClient.ParseBase(savedText);
        var named = new List<Uri>();
        void Name(string hostAndPort)
        {
            if (!SafeHostAndPort(hostAndPort) || HermesClient.ParseBase("http://" + hostAndPort) is not { } url || named.Contains(url) || url == saved) return;
            named.Add(url);
        }
        foreach (var address in add.Addresses.Take(MaxAddAddresses)) Name(address);
        if (add.PeerPort is { } port and > 0 and <= 65535 && System.Net.IPAddress.TryParse(peer, out var ip))
            Name((ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString()) + ":" + port);
        var tried = new List<string>();
        string? problem = null;
        Uri? found = null;
        // Only while no other assistant depends on the saved address may another be tried: a Hermes slow to answer there must not move every assistant, and
        // their keys, to whatever answers elsewhere (code review, 2026-11-16). Moving Hermes is Change assistants' job.
        var settled = saved is not null && settings.Profiles.Count > 0;
        foreach (var url in saved is null ? named : settled ? [saved] : [saved, .. named])
        {
            tried.Add(url.Authority);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
            bound.CancelAfter(AddProbeTimeout);
            try
            {
                await client.ProbeAsync(url, pending.Profile, key, bound.Token).ConfigureAwait(false);
                found = url;
                break;
            }
            catch (HermesException failure) when (failure.Failure is not (HermesFailure.Unreachable or HermesFailure.Unknown))
            {
                // Any answer at the saved address settles it, an error too (code review, 2026-11-16): moving on could move every assistant elsewhere.
                problem = failure.Failure switch { HermesFailure.KeyRejected => "key_rejected", HermesFailure.NoProfile => "no_profile", _ => problem ?? "error" };
                if (url == saved) break;
            }
            catch (HermesException failure) when (failure.Code.All(char.IsAsciiDigit))
            {
                // 502, 503 or 504: Hermes, or something in front of it, answered.
                problem ??= "error";
                if (url == saved) break;
            }
            catch (HermesException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stop.IsCancellationRequested) { }
        }
        if (found is null)
        {
            problem ??= settled ? "saved_unreachable" : "unreachable";
            lock (setupLock)
            {
                pending.Problem = problem;
                pending.Tried = tried;
            }
            log($"hermes setup outcome=hermes_refused profile={Tag(pending.Profile)} id={id} problem={problem} tried={tried.Count}");
            return new(AgentSetupOutcome.HermesRefused, Tag: id, Problem: problem, Tried: tried);
        }
        // Saved under the settings' gate, built on the settings as they are then (another assistant may have been added meanwhile), and only while this code is
        // still the profile's (not replaced, run out or added by hand while Hermes was asked). Added is set as the settings change, under the setup lock, so the
        // code is never seen with its profile there and not yet its own (code review, 2026-11-16: a page's look in between dropped the code).
        bool committed, moved = false;
        try
        {
            committed = await ConfigureAsync(current => (found.ToString(),
                    [.. current.Profiles.Select(p => new HermesProfileInput(p.Name, null)), new(pending.Profile, key, pending.Title ?? "")]),
                () =>
                {
                    // Hermes moved on the page while it was asked: what was found may be the old address.
                    if (settings.Address != savedText) return !(moved = true);
                    lock (setupLock) return Pending(hex) == pending;
                },
                () =>
                {
                    pending.Added = true;
                    pending.Problem = null;
                    pending.Tried = null;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            log($"hermes setup outcome=add_refused profile={Tag(pending.Profile)} id={id}");
            return new(AgentSetupOutcome.Unknown, Tag: id);
        }
        if (!committed) return new(moved ? AgentSetupOutcome.Unavailable : AgentSetupOutcome.Unknown, Tag: id);
        log($"hermes setup outcome=added profile={Tag(pending.Profile)} id={id} moved={(saved is not null && found != saved ? 1 : 0)}");
        return null;
    }

    // A host and port as the command sends them: letters, digits, dots, hyphens, underscores, an IPv6 address in brackets, then the port.
    private static bool SafeHostAndPort(string? value) =>
        value is { Length: > 2 and <= 255 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
        && value.LastIndexOf(':') is var colon and > 0 && int.TryParse(value.AsSpan(colon + 1), System.Globalization.NumberStyles.None, null, out var port) && port is > 0 and <= 65535;

    public AgentSetupState? SetupState(string profile, string id)
    {
        lock (setupLock)
        {
            // Done is read from the token's row first: it outlives a restart, which forgets the ids issued.
            if (reportTokens.Rows.FirstOrDefault(r => r.Profile == profile) is { } row && row.SetupId == id && Has(profile))
                return new(AgentSetupPhase.Done, At: row.Created, Address: row.Address, From: row.SetupFrom, Hermes: settings.Address);
            if (!setupIds.TryGetValue(id, out var issued) || issued != profile) return null;
            var waiting = pendingSetups.FirstOrDefault(p => p.Value.Id == id);
            if (waiting.Value is { } pending)
                return Pending(waiting.Key) is not null ? new(AgentSetupPhase.Waiting, pending.Expires, Problem: pending.Problem, Tried: pending.Tried) : new(AgentSetupPhase.Expired);
            // Gone without being used here: replaced when the profile has a newer code waiting or a token made since; otherwise it ran out.
            return pendingSetups.Values.Any(p => p.Profile == profile) ? new(AgentSetupPhase.Replaced) : new(AgentSetupPhase.Expired);
        }
    }
}
