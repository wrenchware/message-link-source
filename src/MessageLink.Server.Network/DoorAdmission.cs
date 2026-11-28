using System.Collections.Concurrent;
using System.Net;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>Where a request at the door goes: one of the page's or the reports' own surfaces, a connection the door admits, or nowhere.</summary>
internal enum DoorWay { SignInSocket, Page, Report, ReportSetup, Device, Pairing, Join, Refused }

/// <summary>
/// What the door knows of a request before it decides, read from it once (<see cref="NetworkServer"/>), so <see cref="DoorAdmission"/> can be checked without a
/// TLS host or a handshake. Each header is as many values as it came with. What can change while a request is judged (<see cref="Trusted"/>,
/// <see cref="Approver"/>, <see cref="Draining"/>) is asked at the check that needs it, as the door always read it, and <see cref="Trusted"/> only where its
/// answer matters (the page and a join), since asking it logs a proxied request.
/// </summary>
internal sealed record DoorRequest
{
    public string? Path { get; init; }
    public bool HasQuery { get; init; }
    public bool IsGet { get; init; }
    /// <summary>The request names a page's origin, as a browser always does and this app's clients never do.</summary>
    public bool HasOrigin { get; init; }
    public bool IsWebSocket { get; init; }
    public IReadOnlyList<string?> Authorization { get; init; } = [];
    public IReadOnlyList<string?> PairingCode { get; init; } = [];
    public IReadOnlyList<string?> Join { get; init; } = [];
    public IPAddress? Source { get; init; }
    /// <summary>From the home network or through the server's own tailnet door.</summary>
    public Func<bool> Trusted { get; init; } = () => false;
    /// <summary>The server has its page.</summary>
    public bool HasPage { get; init; }
    /// <summary>The page is ready to answer a join: it exists and has its password.</summary>
    public Func<bool> Approver { get; init; } = () => false;
    /// <summary>The server is stopping.</summary>
    public Func<bool> Draining { get; init; } = () => false;
}

/// <summary>What the door asks of the server's parts on the way in. Each may change them: a code is taken, a join takes its place.</summary>
internal interface IDoorAnswers
{
    /// <summary>The device a token belongs to; null for none (<see cref="DeviceRegistry.Authenticate"/>).</summary>
    string? Authenticate(byte[]? token);

    /// <summary>A token this server, restored from a backup, says it doesn't know (<see cref="DeviceRegistry.RefusesAsRestored"/>).</summary>
    bool RefusesAsRestored(byte[]? token);

    /// <summary>The open pairing code, taken, or null; <paramref name="paced"/> when it was tried too soon (<see cref="PairingCodes.Take(string?, IPAddress?, out bool)"/>).</summary>
    byte[]? TakePairingCode(string? presented, IPAddress? source, out bool paced);

    /// <summary>A place to ask to join from this address (<see cref="JoinRequests.Admit"/>).</summary>
    JoinRequests.Admission AdmitJoin(IPAddress? source, out JoinRequests.Request? request);
}

/// <summary>The door's answer: the way in, with what it was let in by, or the refusal's status, reason (the log's word) and header word.</summary>
internal sealed record DoorDecision(DoorWay Way, int Status = 0, string? Reason = null, string? Word = null, string? Device = null, byte[]? Code = null, JoinRequests.Request? Joining = null)
{
    public static DoorDecision Refuse(int status, string reason, string? word = null) => new(DoorWay.Refused, status, reason, word);
}

/// <summary>
/// The network door's decision, apart from accepting the connection, which the host does. In order: the page's sign-in socket and its
/// paths, to a trusted network only; the reports' paths; then the one socket path, exactly, a GET with no query; never a browser (an Origin header) and only an
/// upgrade; nothing while the server stops; then a token, and only a token when one is sent, even beside a code; else the pairing code, a try too soon told
/// apart from a wrong one; else asking to join, from a trusted network only and only while the page can answer; else refused as a missing token. Nothing is
/// taken from a part (a code, a join's place) before every check ahead of it has passed.
/// </summary>
internal static class DoorAdmission
{
    public const int NotFound = 404, BadRequest = 400, Unauthorized = 401, Forbidden = 403, Conflict = 409, TooMany = 429, Unavailable = 503;

    public static DoorDecision Decide(DoorRequest request, IDoorAnswers answers)
    {
        // The page's own socket for signing in on it, to the home network only as the page is; the page itself asks for its cookie, its token and its origin.
        if (request.HasPage && string.Equals(request.Path, ServerPage.SignInSocketPath, StringComparison.Ordinal) && request.Trusted()) return new(DoorWay.SignInSocket);
        // The server's own page, when its owner set it a password (ServerPage): its few paths only, to the home network only.
        if (request.HasPage && ServerPage.Serves(request.Path) && request.Trusted()) return new(DoorWay.Page);
        // Reports (IAgentReports): their own small HTTP surface, never the socket's.
        if (request.Path is NetworkServer.ReportPath or NetworkServer.ReportCheckPath) return new(DoorWay.Report);
        if (NetworkServer.IsReportSetupPath(request.Path)) return new(DoorWay.ReportSetup);
        // Exactly the one path, compared as written (PathString's own comparison ignores case).
        if (!string.Equals(request.Path, NetworkEndpoint.Target, StringComparison.Ordinal) || request.HasQuery || !request.IsGet) return DoorDecision.Refuse(NotFound, "handshake");
        // A browser always names its page's origin; this app's clients never do. A page on the home network gets nothing here, not even a try at a code.
        if (request.HasOrigin || !request.IsWebSocket) return DoorDecision.Refuse(BadRequest, "handshake");
        if (request.Draining()) return DoorDecision.Refuse(Unavailable, "handshake");
        if (request.Authorization.Count > 0)
        {
            // A token, and only a token: a request that also carries a code is judged by its token alone.
            var token = request.Authorization.Count == 1 ? ClientToken.Presented(request.Authorization[0]) : null;
            if (token is not null && answers.Authenticate(token) is { } device) return new(DoorWay.Device, Device: device);
            // A server restored from a backup says so to a token it does not know, so the device can say to ask to join again (NetworkEndpoint).
            return answers.RefusesAsRestored(token) ? DoorDecision.Refuse(Unauthorized, "restored", NetworkPairing.RefusalRestored) : DoorDecision.Refuse(Unauthorized, "token");
        }
        if (request.PairingCode.Count == 1)
        {
            // Too soon is not a wrong code: the client tries again later, and the code is still good.
            var code = answers.TakePairingCode(request.PairingCode[0], request.Source, out var paced);
            return code is not null ? new(DoorWay.Pairing, Code: code) : paced ? DoorDecision.Refuse(TooMany, "pairing_paced") : DoorDecision.Refuse(Unauthorized, "pairing");
        }
        if (request.Join is ["1"])
        {
            // Asking to join (protocol 21), from the home network only, as the page is: nobody elsewhere may put a request in front of the owner. Only the page
            // answers one, so a server without a page ready says so at once, and a device pairs with the printed code.
            if (!request.Trusted()) return DoorDecision.Refuse(Forbidden, "join_not_home");
            if (!request.Approver()) return DoorDecision.Refuse(Conflict, "join_no_approver");
            return answers.AdmitJoin(request.Source, out var joining) == JoinRequests.Admission.Admitted && joining is not null
                ? new(DoorWay.Join, Joining: joining) : DoorDecision.Refuse(TooMany, "join_paced");
        }
        return DoorDecision.Refuse(Unauthorized, "token");
    }
}

/// <summary>
/// The door's refusals, counted per reason: each reason's first is logged, then every hundredth of it with its own count, so a peer that keeps knocking
/// doesn't fill the log and a noisy reason can't hide a new one (owner, 2026-11-26).
/// </summary>
internal sealed class DoorRefusals
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);
    private int total;

    /// <summary>Every refusal, all reasons.</summary>
    public int Total => Volatile.Read(ref total);

    /// <summary>Counts one, and gives the line to log for it, or null when this one isn't logged.</summary>
    public string? Count(string reason)
    {
        Interlocked.Increment(ref total);
        var count = counts.AddOrUpdate(reason, 1, (_, held) => held + 1);
        return count == 1 ? $"protocol connection refused reason={reason}" : count % 100 == 0 ? $"protocol connection refused reason={reason} count={count}" : null;
    }
}
