using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using MessageLink.Core.Services;

namespace MessageLink.Server.Network;

// Reports set up in one command (HermesReportSetup.cs): what the command downloads, and the two calls its installer makes from where Hermes runs. The probe
// and the redemption follow the reports' own rules (home network only, never through the tailnet relay, no Origin, no query, every failure of where a request
// came from or what it presented a 404 with no body), so an address that works for them works for reports. The installer files are public source whose SHA-256
// the command names, so they are served to anyone the door answers: from the Docker host itself the server can see a request arrive from Docker's own address,
// which the home rule refuses, and the very first step must not depend on that.
internal sealed partial class NetworkHost
{
    internal const string SetupPrefix = AgentReportCode.Path + "/setup/";
    internal const string SetupShellPath = SetupPrefix + ReportInstaller.ShellName, SetupPythonPath = SetupPrefix + ReportInstaller.PythonName;
    internal const string SetupProbePath = SetupPrefix + "probe", SetupRedeemPath = SetupPrefix + "redeem";
    // A report token's hash and an address; an add code's command also sends the API key (512 characters at most) and up to four addresses.
    private const int MaxRedeemBody = 4096;

    // The setup command's own paths on the door (the page's first-run and walkthrough screens are its routes, ServerPage.Setup.cs).
    internal static bool IsReportSetupPath(string? path) => path is SetupShellPath or SetupPythonPath or SetupProbePath or SetupRedeemPath;

    private async Task ReportSetupAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value;
        var reports = options.Reports;
        var installer = path is SetupShellPath or SetupPythonPath;
        if (reports is null || (path == SetupRedeemPath ? !HttpMethods.IsPost(request.Method) : !HttpMethods.IsGet(request.Method)))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_route").ConfigureAwait(false);
            return;
        }
        if (request.Headers.ContainsKey("Origin") || request.QueryString.HasValue)
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_" + (request.QueryString.HasValue ? "query" : "origin")).ConfigureAwait(false);
            return;
        }
        if (installer)
        {
            var bytes = path == SetupShellPath ? ReportInstaller.Shell : ReportInstaller.Python;
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.Headers.Connection = "close";
            response.Headers.CacheControl = "no-store";
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            await response.CompleteAsync().ConfigureAwait(false);
            log($"hermes setup outcome=installer file={(path == SetupShellPath ? "sh" : "py")}");
            return;
        }
        if (Relayed(context))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_relay").ConfigureAwait(false);
            return;
        }
        var authorization = request.Headers.Authorization;
        var code = authorization.Count == 1 && authorization[0] is { } presented && presented.StartsWith("Bearer ", StringComparison.Ordinal) ? presented[7..] : null;
        if (!AgentReportRules.WellFormedSetupCode(code))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_code").ConfigureAwait(false);
            return;
        }
        var hash = AgentReportRules.Hash(code!);
        var id = AgentReportRules.SetupTag(hash);
        var peer = context.Connection.RemoteIpAddress;
        if (!FromHome(context))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_address", id).ConfigureAwait(false);
            return;
        }
        if (draining)
        {
            await ReportAnswerAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "draining", id).ConfigureAwait(false);
            return;
        }
        if (path == SetupProbePath)
        {
            await SetupOutcomeAsync(context, reports.ProbeSetupCode(hash), "probe").ConfigureAwait(false);
            return;
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Charset.HasValue && !type.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            await ReportAnswerAsync(context, StatusCodes.Status415UnsupportedMediaType, "refused", "setup_type", id).ConfigureAwait(false);
            return;
        }
        if (request.ContentLength is not { } length || length > MaxRedeemBody)
        {
            await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid", id).ConfigureAwait(false);
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxRedeemBody;
        string? tokenHex = null, address = null, key = null;
        List<string>? hermes = null;
        int? peerPort = null;
        var shaped = true;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            deadline.CancelAfter(ReportReadLimit);
            try
            {
                using var copy = new MemoryStream();
                await request.Body.CopyToAsync(copy, deadline.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(copy.ToArray());
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    tokenHex = root.TryGetProperty("token_sha256", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    address = root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    // An add code's: the profile's API key, where the server might reach Hermes, and the port Hermes answers on at the address this request came from.
                    if (root.TryGetProperty("api_key", out var k)) { key = k.ValueKind == JsonValueKind.String ? k.GetString() : null; shaped &= key is not null; }
                    if (root.TryGetProperty("hermes", out var h))
                    {
                        shaped &= h.ValueKind == JsonValueKind.Array && h.GetArrayLength() <= 8;
                        if (shaped) hermes = [.. h.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "")];
                    }
                    if (root.TryGetProperty("peer_port", out var pp) && pp.ValueKind != JsonValueKind.Null) { shaped &= pp.ValueKind == JsonValueKind.Number && pp.TryGetInt32(out var n) && n is > 0 and <= 65535; if (shaped) peerPort = pp.GetInt32(); }
                }
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or BadHttpRequestException or JsonException) { }
        }
        // The address is kept as the code the installer writes names it (host and port), so a new code starts from it.
        if (!shaped || key is null && (hermes is not null || peerPort is not null)
            || tokenHex is not { Length: 64 } || !tokenHex.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')
            || address is not { Length: > 0 and <= 255 } || !address.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_' or '[' or ']')
            || AgentReportCode.Url(address) is null)
        {
            await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid", id).ConfigureAwait(false);
            return;
        }
        // Where the server saw it come from, never what the installer says.
        var from = peer is null ? null : (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString();
        AgentSetupResult outcome;
        try { outcome = await reports.RedeemSetupCodeAsync(hash, Convert.FromHexString(tokenHex), address, from, context.RequestAborted,
            key is null ? null : new AgentAddRequest(key, hermes ?? [], peerPort)).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log($"hermes setup outcome=failed error={failure.GetType().Name} id={id}");
            outcome = new(AgentSetupOutcome.Unavailable, Tag: id);
        }
        if (options.LoseRedeemAnswer?.Invoke() == true)
        {
            context.Abort();
            return;
        }
        await SetupOutcomeAsync(context, outcome, "redeem").ConfigureAwait(false);
    }

    private async Task SetupOutcomeAsync(HttpContext context, AgentSetupResult outcome, string step)
    {
        switch (outcome.Outcome)
        {
            case AgentSetupOutcome.Ready:
                await ReportAnswerAsync(context, StatusCodes.Status204NoContent, "ready", null, outcome.Tag).ConfigureAwait(false);
                break;
            case AgentSetupOutcome.Done:
                // The address the server reaches Hermes at, for an add code's command to say.
                await ReportJsonAsync(context, StatusCodes.Status200OK, new { status = "on", hermes = outcome.Hermes }).ConfigureAwait(false);
                break;
            case AgentSetupOutcome.HermesRefused:
                // Nothing changed and the code still waits: the command says what to change, with the addresses tried (names and ports the command itself sent, or saved here).
                ReportLog("hermes_" + outcome.Problem, outcome.Tag);
                await ReportJsonAsync(context, StatusCodes.Status422UnprocessableEntity, new { error = "hermes_" + outcome.Problem, tried = outcome.Tried ?? [] }).ConfigureAwait(false);
                break;
            case AgentSetupOutcome.AlreadyUsed:
                await ReportErrorAsync(context, StatusCodes.Status409Conflict, "already_used", outcome.Tag).ConfigureAwait(false);
                break;
            case AgentSetupOutcome.Limited:
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling((outcome.RetryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ReportErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", outcome.Tag).ConfigureAwait(false);
                break;
            case AgentSetupOutcome.Unavailable:
                await ReportAnswerAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "setup_" + step, outcome.Tag).ConfigureAwait(false);
                break;
            default:
                await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "setup_" + step, outcome.Tag).ConfigureAwait(false);
                break;
        }
    }
}
