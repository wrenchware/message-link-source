using MessageLink.Core.Services;

namespace MessageLink.App.Services.Hermes;

// Reports set up in one command (docs/research/hermes-reports-plan.md, "Setup in one command"): the page shows a command carrying a setup code; where Hermes
// runs, the installer makes the profile's report token itself and sends only its hash with the setup code, which then stops working. The server keeps a pending
// code's hash in memory only (a restart loses it, and the owner makes another), and a used one's hash on the token row, so a setup whose answer was lost can
// finish with the same token's hash even after a restart, and another token's hash is told the code was already used. An add code (add an assistant in one
// command, ServerPage.HermesAdd.cs) names a profile not set up yet: its use also carries the profile's API key, and the profile is added once Hermes takes the
// key, before its token is saved as any other code's.
internal sealed partial class HermesBackend
{
    /// <summary>How long the server waits for Hermes at each address an add code's command names.</summary>
    internal TimeSpan AddProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public AgentSetupIssue IssueSetupCode(string profile) => access.IssueSetupCode(profile);

    public AgentSetupIssue IssueAddCode(string profile, string? title)
    {
        if (loaded is not { IsCompletedSuccessfully: true }) throw new ArgumentException("The server is still starting. Try again in a minute.");
        var name = profile.Trim().ToLowerInvariant();
        if (!HermesClient.ValidProfile(name)) throw new ArgumentException("A profile name uses lowercase letters, numbers, hyphens and underscores, as in Hermes.");
        var given = title?.Trim() is { Length: > 0 } named && named != DefaultTitle(name) ? named : null;
        if (given is not null && (given.Any(char.IsControl) || given.Length > MaxTitleLength))
            throw new ArgumentException($"A conversation name can have up to {MaxTitleLength} characters, on one line.");
        return access.IssueAddCode(name, given);
    }

    public AgentSetupResult ProbeSetupCode(byte[] codeHash) =>
        loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested ? new(AgentSetupOutcome.Unavailable) : access.Probe(codeHash);

    public async Task<AgentSetupResult> RedeemSetupCodeAsync(byte[] codeHash, byte[] tokenHash, string address, string? peer, CancellationToken cancellationToken = default,
        AgentAddRequest? add = null)
    {
        if (loaded is not { IsCompletedSuccessfully: true } || stop.IsCancellationRequested) return new(AgentSetupOutcome.Unavailable);
        if (codeHash.Length != 32 || tokenHash.Length != 32) return new(AgentSetupOutcome.Unknown);
        // An add code adds its profile first, outside the gate: asking Hermes takes seconds, and every thread waits on the gate.
        if (access.BeforeRedeem(codeHash, add is not null, out var adding) is { } refused) return refused;
        if (adding is not null
            && await access.AddAsync(adding, add!.Key, key => AddCoreAsync(adding, add, key, peer, cancellationToken)).ConfigureAwait(false) is { } failed)
            return failed;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await access.RedeemAsync(codeHash, tokenHash, address, peer).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Adds an add code's profile. Its key is tried at the saved address first and, only when nothing answers there and no assistant depends on it, at each address the command named and at the
    // address the request came from. Hermes answering on the saved address settles it: one Hermes serves every profile here, and another one answering
    // elsewhere would move every assistant to it. Null once the profile is saved; otherwise why not, with nothing changed and the code still waiting.
    private async Task<AgentSetupResult?> AddCoreAsync(HermesReportAccess.Code pending, AgentAddRequest add, string key, string? peer, CancellationToken cancellationToken)
    {
        var id = pending.Id;
        var savedText = settings.Address;
        var saved = HermesClient.ParseBase(savedText);
        var named = new List<Uri>();
        void Name(string hostAndPort)
        {
            if (!SafeHostAndPort(hostAndPort) || HermesClient.ParseBase("http://" + hostAndPort) is not { } url || named.Contains(url) || url == saved) return;
            named.Add(url);
        }
        foreach (var address in add.Addresses.Take(HermesReportAccess.MaxAddAddresses)) Name(address);
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
            access.AddFailed(pending, problem, tried);
            log($"hermes setup outcome=hermes_refused profile={Tag(pending.Profile)} id={id} problem={problem} tried={tried.Count}");
            return new(AgentSetupOutcome.HermesRefused, Tag: id, Problem: problem, Tried: tried);
        }
        // Saved under the settings' gate, built on the settings as they are then (another assistant may have been added meanwhile), and only while this code is
        // still the profile's (not replaced, run out or added by hand while Hermes was asked). Added is set as the settings change, in the same step, so the
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
                    return access.StillPending(pending);
                },
                () => access.Added(pending),
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

    public AgentSetupState? SetupState(string profile, string id) => access.SetupState(profile, id);
}
