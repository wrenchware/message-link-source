using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

// The report route (IAgentReports; wire format in PROVIDERS.md, Hermes): POST /v1/reports puts a plain-text message into the thread the bearer token picks, and
// GET /v1/reports/check says whether a token is live. From the home network only, as the page is, and never through the tailnet relay: a relayed or NAT'd peer
// address proves nothing about where a report came from, and the token alone is the authentication. Every failure of where it came from or what it presented is
// a 404 with no body, as an unknown path is. Each answer closes its connection.
internal sealed partial class NetworkHost
{
    internal const string ReportPath = AgentReportCode.Path, ReportCheckPath = AgentReportCode.Path + "/check";
    /// <summary>How long a report's body may take to arrive.</summary>
    internal TimeSpan ReportReadLimit { get; set; } = TimeSpan.FromSeconds(5);

    private int reportsRefused;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private async Task ReportAsync(HttpContext context)
    {
        var request = context.Request;
        var check = request.Path.Value == ReportCheckPath;
        var reports = options.Reports;
        if (reports is null || (check ? !HttpMethods.IsGet(request.Method) : !HttpMethods.IsPost(request.Method)))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "route").ConfigureAwait(false);
            return;
        }
        if (Relayed(context))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "relay").ConfigureAwait(false);
            return;
        }
        // A browser always names its page's origin; an agent never does. A cross-site form post needs no preflight, so it is refused here, token or not.
        if (request.Headers.ContainsKey("Origin") || request.QueryString.HasValue)
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", request.QueryString.HasValue ? "query" : "origin").ConfigureAwait(false);
            return;
        }
        var authorization = request.Headers.Authorization;
        var token = authorization.Count == 1 && authorization[0] is { } presented && presented.StartsWith("Bearer ", StringComparison.Ordinal) ? presented[7..] : null;
        if (!AgentReportRules.WellFormedToken(token))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "token").ConfigureAwait(false);
            return;
        }
        var hash = AgentReportRules.Hash(token!);
        var tag = AgentReportRules.Tag(token!);
        // The plain home check, without the relay's exception. A peer behind Docker's published port shows as the bridge's gateway, which FromHome refuses:
        // the page's Reports line says so, so a setup that can never work is seen without the logs.
        if (!(options.HomeCheck ?? ServerPage.FromHome)(context.Connection.RemoteIpAddress))
        {
            reports.ReportRefused(hash, "address");
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "address", tag).ConfigureAwait(false);
            return;
        }
        if (!reports.KnowsReportToken(hash))
        {
            await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "token").ConfigureAwait(false);
            return;
        }
        // Only a known token from the home network learns the server is stopping; anyone else gets the same 404 as ever.
        if (draining)
        {
            await ReportAnswerAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "draining", tag).ConfigureAwait(false);
            return;
        }
        if (check)
        {
            var known = await reports.CheckReportAsync(hash, context.RequestAborted).ConfigureAwait(false);
            await ReportOutcomeAsync(context, known, 0).ConfigureAwait(false);
            return;
        }
        if (request.ContentLength > AgentReportRules.MaxBodyBytes)
        {
            await ReportAnswerAsync(context, StatusCodes.Status413PayloadTooLarge, "refused", "size", tag).ConfigureAwait(false);
            return;
        }
        var json = false;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || type.Charset.HasValue && !type.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || !(type.MediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || (json = type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))))
        {
            await ReportAnswerAsync(context, StatusCodes.Status415UnsupportedMediaType, "refused", "type", tag).ConfigureAwait(false);
            return;
        }
        var keys = request.Headers["Idempotency-Key"];
        string? key = keys.Count == 0 ? null : keys.Count == 1 ? keys[0] : "";
        if (key is not null && !AgentReportRules.ValidKey(key))
        {
            await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_key", tag).ConfigureAwait(false);
            return;
        }
        // The default MaxRequestBodySize of 0 stays for everything else; this request may carry a report.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = AgentReportRules.MaxBodyBytes;
        byte[] body;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            deadline.CancelAfter(ReportReadLimit);
            try
            {
                using var copy = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = await request.Body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > AgentReportRules.MaxBodyBytes) break;
                }
                body = copy.ToArray();
            }
            catch (BadHttpRequestException tooLarge) when (tooLarge.StatusCode == StatusCodes.Status413PayloadTooLarge) { body = new byte[AgentReportRules.MaxBodyBytes + 1]; }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or BadHttpRequestException)
            {
                await ReportAnswerAsync(context, StatusCodes.Status400BadRequest, "refused", "body", tag).ConfigureAwait(false);
                return;
            }
        }
        if (body.Length > AgentReportRules.MaxBodyBytes)
        {
            await ReportAnswerAsync(context, StatusCodes.Status413PayloadTooLarge, "refused", "size", tag).ConfigureAwait(false);
            return;
        }
        string raw;
        try { raw = StrictUtf8.GetString(body); }
        catch (ArgumentException)
        {
            await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_text", tag).ConfigureAwait(false);
            return;
        }
        if (json)
        {
            string? id = null;
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_text", tag).ConfigureAwait(false);
                    return;
                }
                raw = text.GetString()!;
                if (root.TryGetProperty("id", out var given))
                    id = given.ValueKind == JsonValueKind.String ? given.GetString() : "";
            }
            catch (JsonException)
            {
                await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_text", tag).ConfigureAwait(false);
                return;
            }
            // A header and an id that disagree are a client's mistake, not a choice between them.
            if (id is not null && (!AgentReportRules.ValidKey(id) || key is not null && key != id))
            {
                await ReportErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_key", tag).ConfigureAwait(false);
                return;
            }
            key ??= id;
        }
        var cleaned = AgentReportRules.Clean(raw);
        if (cleaned.Length == 0 || cleaned.Length > AgentReportRules.MaxChars)
        {
            await ReportErrorAsync(context, StatusCodes.Status400BadRequest, cleaned.Length == 0 ? "empty" : "invalid_text", tag).ConfigureAwait(false);
            return;
        }
        AgentReportResult outcome;
        // A thread that couldn't be saved is a report not delivered: the client may send it again with the same key.
        try { outcome = await reports.DeliverReportAsync(hash, cleaned, key, context.RequestAborted).ConfigureAwait(false); }
        // Any failure but the client leaving: the protector can throw more than IO and crypto errors (InvalidDataException for a key gone bad; code review,
        // 2026-11-15), and every one of them is a report not kept.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log($"hermes report outcome=failed error={failure.GetType().Name} tag={tag}");
            outcome = new(AgentReportOutcome.Unavailable, Tag: tag);
        }
        await ReportOutcomeAsync(context, outcome, cleaned.Length).ConfigureAwait(false);
    }

    private async Task ReportOutcomeAsync(HttpContext context, AgentReportResult outcome, int chars)
    {
        // The id the client gets back is the message's own part only: the thread's id names the profile, which the code deliberately doesn't carry.
        var id = outcome.MessageId is { } full && full.LastIndexOf(':') is var colon and >= 0 ? full[(colon + 1)..] : outcome.MessageId;
        switch (outcome.Outcome)
        {
            case AgentReportOutcome.Ready:
                await ReportAnswerAsync(context, StatusCodes.Status204NoContent, "ready", null, outcome.Tag).ConfigureAwait(false);
                break;
            case AgentReportOutcome.Delivered:
                log($"hermes report outcome=delivered tag={outcome.Tag} chars={chars}");
                await ReportJsonAsync(context, StatusCodes.Status202Accepted, new { id, status = "delivered" }).ConfigureAwait(false);
                break;
            case AgentReportOutcome.Duplicate:
                log($"hermes report outcome=duplicate tag={outcome.Tag} chars={chars}");
                await ReportJsonAsync(context, StatusCodes.Status200OK, new { id, status = "duplicate" }).ConfigureAwait(false);
                break;
            case AgentReportOutcome.KeyReused:
                await ReportErrorAsync(context, StatusCodes.Status409Conflict, "key_reused", outcome.Tag).ConfigureAwait(false);
                break;
            case AgentReportOutcome.Limited:
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling((outcome.RetryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ReportErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", outcome.Tag).ConfigureAwait(false);
                break;
            case AgentReportOutcome.Unavailable:
                await ReportAnswerAsync(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "hermes", outcome.Tag).ConfigureAwait(false);
                break;
            default:
                await ReportAnswerAsync(context, StatusCodes.Status404NotFound, "refused", "token", outcome.Tag).ConfigureAwait(false);
                break;
        }
    }

    // A refusal's word in a small JSON body: what the client did that it can fix.
    private Task ReportErrorAsync(HttpContext context, int status, string error, string? tag)
    {
        ReportLog(error, tag);
        return ReportJsonAsync(context, status, new { error });
    }

    private static async Task ReportJsonAsync(HttpContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        var response = context.Response;
        response.StatusCode = status;
        response.Headers.Connection = "close";
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/json";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes).ConfigureAwait(false);
        await response.CompleteAsync().ConfigureAwait(false);
    }

    // An answer with no body. Refusals are logged the first time and then one in a hundred, with the token's tag when it was well formed; never the text or token.
    private async Task ReportAnswerAsync(HttpContext context, int status, string outcome, string? reason, string? tag = null)
    {
        if (outcome == "refused" || outcome == "unavailable") ReportLog(reason ?? outcome, tag, outcome);
        var response = context.Response;
        response.StatusCode = status;
        response.Headers.Connection = "close";
        response.ContentLength = 0;
        await response.CompleteAsync().ConfigureAwait(false);
    }

    private void ReportLog(string reason, string? tag, string outcome = "refused")
    {
        var count = Interlocked.Increment(ref reportsRefused);
        if (count == 1 || count % 100 == 0)
            log($"hermes report outcome={outcome} reason={reason}{(tag is null ? "" : " tag=" + tag)}{(count == 1 ? "" : $" count={count}")}");
    }
}
