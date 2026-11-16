using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NativeBeeper.Core.Services;

/// <summary>
/// Reports: an agent on the home network (today only Hermes) posting a plain-text message into its own thread outside a turn, with a token made on the
/// server's page. The route is neutral (<c>/v1/reports</c>) and a token picks its thread, so another kind of agent would add a source kind, not a route.
/// A report is an ordinary incoming message: unread and notified, never starting a turn and never reaching another thread or a service.
/// </summary>
public interface IAgentReports
{
    /// <summary>Each Hermes profile's reports as the server's page shows them.</summary>
    IReadOnlyList<AgentReportsState> ReportsState { get; }

    /// <summary>Turns reports on for <paramref name="profile"/>, or makes a new token for it: the old one stops at once. Answers the token, which is shown
    /// once and never kept (only its hash is). <paramref name="address"/> is the address the code names (host and port), kept so a new code starts from it.</summary>
    Task<string> IssueReportTokenAsync(string profile, string? address = null, CancellationToken cancellationToken = default);

    /// <summary>Turns reports off for <paramref name="profile"/>: its token stops.</summary>
    Task TurnOffReportsAsync(string profile, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="tokenHash"/> is a live token, without counting it: the door answers an unknown one as it answers an unknown path,
    /// before it reads a body.</summary>
    bool KnowsReportToken(byte[] tokenHash);

    /// <summary>Whether <paramref name="tokenHash"/> is a live token (<see cref="AgentReportOutcome.Ready"/>). Checks have a per-minute limit of their own and
    /// never use up the reports'.</summary>
    Task<AgentReportResult> CheckReportAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Puts a report in the token's thread. <paramref name="text"/> is already cleaned (<see cref="AgentReportRules.Clean"/>).</summary>
    Task<AgentReportResult> DeliverReportAsync(byte[] tokenHash, string text, string? key, CancellationToken cancellationToken = default);

    /// <summary>A request with this token was refused before it reached the thread (<c>address</c>: not from the home network), for the page's line.</summary>
    void ReportRefused(byte[] tokenHash, string reason);

    /// <summary>A setup code for <paramref name="profile"/> (setup in one command): works once, for <see cref="AgentReportRules.SetupLifetime"/>, and replaces any
    /// earlier one for the profile. Answers the code, shown once and never kept (only its hash is), and its id for the page's live view.</summary>
    AgentSetupIssue IssueSetupCode(string profile);

    /// <summary>Whether a setup code can be used, without using it up: a pending code, or one already used (so a setup whose answer was lost can finish).
    /// Probes have a small allowance per code.</summary>
    AgentSetupResult ProbeSetupCode(byte[] codeHash);

    /// <summary>Uses a setup code: the profile's token becomes the one whose hash is <paramref name="tokenHash"/>, made where Hermes runs, with
    /// <paramref name="address"/> (how Hermes reaches the server) and <paramref name="peer"/> (where the server saw the request come from) kept beside it. The
    /// same code with the same token's hash again is the same setup, done; with another hash it was already used. Nothing changes unless the token is saved.</summary>
    Task<AgentSetupResult> RedeemSetupCodeAsync(byte[] codeHash, byte[] tokenHash, string address, string? peer, CancellationToken cancellationToken = default);

    /// <summary>Where the setup code <paramref name="id"/> of <paramref name="profile"/> stands, for the page that shows its command; null for an id not issued.</summary>
    AgentSetupState? SetupState(string profile, string id);
}

/// <summary>A setup code as issued: the code (shown once), its one-way id and when it stops working.</summary>
public sealed record AgentSetupIssue(string Code, string Id, DateTimeOffset Expires);

public enum AgentSetupOutcome { Ready, Done, AlreadyUsed, Unknown, Limited, Unavailable }

/// <summary>What a probe or a use of a setup code came to. <paramref name="Tag"/> is the code's id when it was known.</summary>
public sealed record AgentSetupResult(AgentSetupOutcome Outcome, TimeSpan? RetryAfter = null, string? Tag = null);

public enum AgentSetupPhase { Waiting, Done, Expired, Replaced }

/// <summary>A setup code's state on the page: waiting until <paramref name="Expires"/>, or done at <paramref name="At"/> with the address Hermes uses and the
/// address the server saw it come from.</summary>
public sealed record AgentSetupState(AgentSetupPhase Phase, DateTimeOffset? Expires = null, DateTimeOffset? At = null, string? Address = null, string? From = null);

public enum AgentReportOutcome { Delivered, Duplicate, KeyReused, Unknown, Limited, Unavailable, Ready }

/// <summary>What a report or a check came to. <paramref name="Tag"/> is the token's one-way log tag when the token was known.</summary>
public sealed record AgentReportResult(AgentReportOutcome Outcome, string? MessageId = null, TimeSpan? RetryAfter = null, string? Tag = null);

/// <summary>One profile's reports: on or off, since when, the last refusal the page should say (<c>address</c>), if any, the address the current code
/// names, when the last report arrived, and, for a code set up in one command, the address the setup came from as the server saw it.</summary>
public sealed record AgentReportsState(string Profile, bool On, DateTimeOffset? Since = null, string? LastRefusal = null, DateTimeOffset? LastRefusalAt = null,
    string? Address = null, DateTimeOffset? LastReportAt = null, string? SetupFrom = null);

/// <summary>The wire rules of a report (docs/research/hermes-reports-plan.md, section 5): the token, the text and the idempotency key.</summary>
public static class AgentReportRules
{
    public const string TokenPrefix = "mlr1_", SetupPrefix = "mls1_";
    public const int MaxBodyBytes = 16 * 1024, MaxChars = 8000, MaxKeyLength = 64;
    public static readonly TimeSpan KeyWindow = TimeSpan.FromHours(24);
    /// <summary>How long a setup code works (setup in one command).</summary>
    public static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A new token: <c>mlr1_</c> and 32 random bytes as 43 base64url characters.</summary>
    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool WellFormedToken(string? token) => WellFormed(token, TokenPrefix);

    /// <summary>A new setup code: <c>mls1_</c> and 32 random bytes as 43 base64url characters.</summary>
    public static string NewSetupCode() => SetupPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool WellFormedSetupCode(string? code) => WellFormed(code, SetupPrefix);

    private static bool WellFormed(string? value, string prefix) =>
        value is { Length: 48 } && value.StartsWith(prefix, StringComparison.Ordinal) && value.AsSpan(prefix.Length).IndexOfAnyExcept(Base64UrlChars) < 0;

    /// <summary>What the server keeps of a token and finds it by: no comparison ever depends on the secret itself.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>A token's one-way tag for logs: 8 hex characters of a second, domain-separated hash. Made from the token's hash, so a token made where Hermes
    /// runs, which the server only ever sees hashed, has the same tag on both sides.</summary>
    public static string Tag(string token) => TagOfHash(Hash(token));

    public static string TagOfHash(byte[] hash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("message-link report tag\n" + Convert.ToHexStringLower(hash))))[..8];

    /// <summary>A setup code's one-way id, for logs and the page's live view.</summary>
    public static string SetupTag(byte[] codeHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("message-link setup tag\n" + Convert.ToHexStringLower(codeHash))))[..8];

    public static bool ValidKey(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// The text as kept: control characters other than newline and tab removed, and the bidirectional controls (U+202A-U+202E, U+2066-U+2069) so a report
    /// cannot turn round how an address or a name reads; carriage returns become newlines; trimmed. A report is 1 to <see cref="MaxChars"/> of these.
    /// </summary>
    public static string Clean(string text)
    {
        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { if (i + 1 >= text.Length || text[i + 1] != '\n') kept.Append('\n'); continue; }
            if (c is '\n' or '\t') { kept.Append(c); continue; }
            if (char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩') continue;
            kept.Append(c);
        }
        return kept.ToString().Trim();
    }

    /// <summary>The SHA-256 of the cleaned text, which a reused key is compared by.</summary>
    public static string Digest(string cleaned) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cleaned)));

    private static readonly System.Buffers.SearchValues<char> Base64UrlChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The connection code the server's page shows once: <c>mlrc1.</c> and the base64url (no padding) of a JSON object with <c>v</c> (1), <c>url</c> (the report
/// route), <c>sha256</c> (the server certificate's fingerprint, which the client pins before sending a byte) and <c>token</c>. It names no profile or thread:
/// the token alone picks the thread.
/// </summary>
public static class AgentReportCode
{
    public const string Prefix = "mlrc1.";
    public const string Path = "/v1/reports";

    public static string Build(Uri url, string sha256, string token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["v"] = 1, ["url"] = url.AbsoluteUri, ["sha256"] = sha256, ["token"] = token });
        return Prefix + AgentReportRules.Base64Url(json);
    }

    /// <summary>The report address for a host and port as typed on the page (<c>192.168.1.20:8787</c>, <c>messages-server:8787</c>); null when it isn't one.</summary>
    public static Uri? Url(string? hostAndPort)
    {
        if (string.IsNullOrWhiteSpace(hostAndPort)) return null;
        var typed = hostAndPort.Trim();
        if (typed.Contains("://", StringComparison.Ordinal)) typed = typed[(typed.IndexOf("://", StringComparison.Ordinal) + 3)..];
        typed = typed.TrimEnd('/');
        if (typed.Length == 0 || typed.IndexOfAny(['/', '?', '#', '@', ' ']) >= 0) return null;
        return Uri.TryCreate("https://" + typed + Path, UriKind.Absolute, out var url) && Valid(url) ? url : null;
    }

    /// <summary>A code read as a client must read it: everything checked before connecting; null for any code that breaks a rule.</summary>
    public static (Uri Url, string Sha256, string Token)? Read(string? code)
    {
        if (code is null || !code.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var data = code[Prefix.Length..];
        if (data.Length == 0 || data.Contains('=') || data.AsSpan().IndexOfAnyExcept(Base64Chars) >= 0) return null;
        byte[] json;
        try { json = Convert.FromBase64String(data.Replace('-', '+').Replace('_', '/') + new string('=', (4 - data.Length % 4) % 4)); }
        catch (FormatException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1 || v.GetRawText() != "1") return null;
            if (!root.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url) || !Valid(url)) return null;
            if (!root.TryGetProperty("sha256", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: 64 } sha
                || !sha.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) return null;
            if (!root.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String || !AgentReportRules.WellFormedToken(t.GetString())) return null;
            return (url, sha, t.GetString()!);
        }
        catch (JsonException) { return null; }
    }

    // https, a host, an optional port, the exact path, and no userinfo, query or fragment.
    private static bool Valid(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0 && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
        && url.AbsolutePath == Path && !url.OriginalString.EndsWith('?') && !url.OriginalString.EndsWith('#');

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");
}
