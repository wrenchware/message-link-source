using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NativeBeeper.App.Services.Hermes;

/// <summary>Why a request to Hermes did not go through, in the words the thread's note and the logs use.</summary>
public enum HermesFailure
{
    /// <summary>Nothing answered at the address, or the connection broke before Hermes said anything.</summary>
    Unreachable,
    /// <summary>401 or 403: the profile's key was refused. Not retried until the key changes.</summary>
    KeyRejected,
    /// <summary>404 at <c>/v1/models</c> or the chat route: no such profile at this address.</summary>
    NoProfile,
    /// <summary>429: Hermes is at its limit of concurrent runs.</summary>
    Busy,
    /// <summary>409: the session is held by another of Hermes's own clients, or a turn is already being admitted.</summary>
    Conflict,
    /// <summary>400: Hermes refused what was sent (an unsupported attachment, a malformed body).</summary>
    Refused,
    /// <summary>5xx, or an answer that is not what the API documents.</summary>
    ServerError,
    /// <summary>The request may have reached Hermes: the connection broke after it was sent, or no answer came in time. Never resent.</summary>
    Unknown,
}

public sealed class HermesException(HermesFailure failure, string code) : Exception("hermes " + code)
{
    public HermesFailure Failure { get; } = failure;
    /// <summary>A fixed word for the logs: the HTTP status class or Hermes's own error code when it is a plain identifier.</summary>
    public string Code { get; } = code;
}

/// <summary>One piece of a streamed reply.</summary>
public abstract record HermesStreamEvent;
/// <summary>More of the reply's words.</summary>
public sealed record HermesDelta(string Text) : HermesStreamEvent;
/// <summary>Hermes is using a tool; the short words to show while it works (<c>hermes.tool.progress</c>).</summary>
public sealed record HermesProgress(string Status) : HermesStreamEvent;
/// <summary>The reply ended: <c>stop</c>, <c>length</c>, <c>cancelled</c> or <c>error</c> (with Hermes's error code when it gave one).</summary>
public sealed record HermesFinished(string Reason, string? Error = null) : HermesStreamEvent;

/// <summary>
/// Hermes Agent's chat-completions API server, as PROVIDERS.md (Hermes) records it: each profile at its own route on one
/// port (<c>/v1/...</c> for the default profile, <c>/p/&lt;profile&gt;/v1/...</c> for the others when Hermes multiplexes
/// them), each with its own <c>API_SERVER_KEY</c> as a bearer token. A thread keeps one Hermes session and sends only the
/// new turn with <c>X-Hermes-Session-Id</c>; Hermes holds the history and compacts it in place under the same id.
/// Never logs or throws with a key, a body or a reply in it.
/// </summary>
public sealed class HermesClient(HttpClient http)
{
    public const string DefaultProfile = "default";
    public const string SessionHeader = "X-Hermes-Session-Id";

    /// <summary>A profile name Hermes accepts as a directory and a route segment, and the only shapes kept in a chat id.</summary>
    public static bool ValidProfile(string? name) => name is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>The address as typed, made a base: http or https only, no query, no fragment, no trailing slash.</summary>
    public static Uri? ParseBase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        // An address copied with the API's own suffix still names the same server.
        if (path.EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3];
        return new UriBuilder(uri) { Path = path.Length == 0 ? "/" : path + "/" }.Uri;
    }

    public static Uri Route(Uri baseAddress, string profile, string path) =>
        new(baseAddress, (profile == DefaultProfile ? "" : "p/" + profile + "/") + "v1/" + path);

    /// <summary>
    /// Asks the profile's <c>/v1/models</c>, which needs its key and names the profile's model: the thread's title when Hermes gives
    /// one. Returns that name (or null when the list was empty) or throws <see cref="HermesException"/>.
    /// </summary>
    public async Task<string?> ProbeAsync(Uri baseAddress, string profile, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(baseAddress, profile, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await ThrowUnlessOkAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var first = (root?["data"] as JsonArray)?.FirstOrDefault();
            return first?["id"]?.GetValue<string>() is { Length: > 0 and <= 100 } id ? id : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new HermesException(HermesFailure.ServerError, "models_shape");
        }
    }

    /// <summary>
    /// Sends one turn to the profile's session and answers once Hermes has accepted it (the reply's headers). <paramref name="images"/>
    /// are data URLs of pictures only, the one kind of file Hermes takes. Throws <see cref="HermesException"/> when Hermes refuses the turn.
    /// </summary>
    public async Task<HttpResponseMessage> OpenAsync(Uri baseAddress, string profile, string key, string sessionId, string? text,
        IReadOnlyList<string> images, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(baseAddress, profile, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(SessionHeader, sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(Body(profile, text, images), Encoding.UTF8, "application/json");
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try { await ThrowUnlessOkAsync(response, cancellationToken).ConfigureAwait(false); }
        catch { response.Dispose(); throw; }
        return response;
    }

    /// <summary>
    /// The session id a reply came back under, when it is one we could send again. Hermes echoes the id it used, so one unlike the id sent means Hermes
    /// moved the conversation (a compaction with <c>compression.in_place: false</c> continues in a new session, as the docs' sessions page describes).
    /// </summary>
    public static string? SessionOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(SessionHeader, out var values) && values.FirstOrDefault() is { Length: > 0 and <= 128 } id
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? id : null;

    /// <summary>The events of a reply <see cref="OpenAsync"/> opened. Ends with one <see cref="HermesFinished"/>, <c>interrupted</c> when the stream was cut off.</summary>
    public static async IAsyncEnumerable<HermesStreamEvent> ReadAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream stream;
        HermesFinished? openFailed = null;
        try { stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { stream = Stream.Null; openFailed = new("interrupted"); }
        if (openFailed is not null) { yield return openFailed; yield break; }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        var finished = false;
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or IOException) { line = null; }
            if (line is null) break;
            if (line.Length == 0)
            {
                // A blank line ends one event.
                if (data.Length > 0 || eventName is not null)
                {
                    foreach (var parsed in Parse(eventName, data.ToString()))
                    {
                        if (parsed is HermesFinished) finished = true;
                        yield return parsed;
                    }
                    if (finished) yield break;
                }
                eventName = null; data.Clear();
                continue;
            }
            if (line[0] == ':') continue; // ": keepalive" every ten quiet seconds
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            if (field == "event") eventName = value;
            else if (field == "data") { if (data.Length > 0) data.Append('\n'); data.Append(value); }
        }
        if (!finished && data.Length > 0)
            foreach (var parsed in Parse(eventName, data.ToString())) { if (parsed is HermesFinished) finished = true; yield return parsed; }
        // A stream that ended without [DONE] or a finish reason was cut off.
        if (!finished) yield return new HermesFinished("interrupted");
    }

    internal static string Body(string profile, string? text, IReadOnlyList<string> images)
    {
        JsonNode content;
        if (images.Count == 0) content = JsonValue.Create(text ?? "");
        else
        {
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(text)) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            foreach (var image in images) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } });
            content = parts;
        }
        return new JsonObject
        {
            ["model"] = profile == DefaultProfile ? "hermes-agent" : profile,
            ["stream"] = true,
            // Only the new turn: the session holds everything before it (X-Hermes-Session-Id).
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        }.ToJsonString();
    }

    internal static IEnumerable<HermesStreamEvent> Parse(string? eventName, string data)
    {
        if (data == "[DONE]") { yield return new HermesFinished("stop"); yield break; }
        JsonNode? node;
        try { node = JsonNode.Parse(data); }
        catch (JsonException) { yield break; }
        if (node is not JsonObject json) yield break;
        if (eventName is { Length: > 0 } && eventName != "message")
        {
            if (eventName == "hermes.tool.progress" && ProgressWords(json) is { } status) yield return new HermesProgress(status);
            yield break;
        }
        var choice = (json["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (choice?["delta"]?["content"] is JsonValue delta && delta.TryGetValue<string>(out var words) && words.Length > 0) yield return new HermesDelta(words);
        if (choice?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var finish) && finish.Length > 0)
            yield return new HermesFinished(finish is "stop" or "length" or "cancelled" or "error" ? finish : "other", Known(Text(json["error"]?["message"]) ?? Text(json["error"]?["code"])));
        else if (json["error"] is JsonObject error)
            yield return new HermesFinished("error", Known(Text(error["code"]) ?? Text(error["message"])));
    }

    // hermes.tool.progress, as Hermes sent it live (PROVIDERS.md, Hermes): {"tool":"terminal","emoji":"…","label":"date -u","status":"running"}, then the
    // same toolCallId with "status":"completed" and no label. A finished tool clears the line (an empty status) until the next one starts.
    private static string? ProgressWords(JsonObject json)
    {
        if (Text(json["status"]) is "completed" or "failed" or "error") return "";
        var tool = Text(json["tool"]) ?? Text(json["name"]) ?? Text(json["tool_name"]);
        var label = Text(json["label"]) ?? Text(json["preview"]);
        if (tool is not { Length: > 0 }) return label is { Length: > 0 } ? Short(label) : null;
        return Short(label is { Length: > 0 } ? $"Using {tool}: {label}" : $"Using {tool}");
    }

    private static string Short(string words)
    {
        var line = words.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Hermes's own error codes as PROVIDERS.md (Hermes) records them. Only these reach a log line or a decision: anything else Hermes says, even a
    // word that looks like a code, could be a prompt or a key echoed back.
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "admission_conflict", "unsupported_content_type", "gateway_auth_failed", "gateway_auth_error", "permission_denied", "iteration_budget_exhausted",
        "invalid_params", "not_found", "runtime_draining", "storage_unavailable", "idempotency_key_conflict", "unknown_execution", "stale_generation",
    };
    internal static string? Known(string? value) => value is not null && KnownCodes.Contains(value) ? value : null;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        try { return await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false); }
        // Only a failure before anything was sent says Hermes never saw the request: no route, no name, no connection, no TLS. A connection that broke
        // after it, or no answer in time, may have reached it.
        catch (HttpRequestException failure) when (failure.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        { throw new HermesException(HermesFailure.Unreachable, "connect"); }
        catch (HttpRequestException) { throw new HermesException(HermesFailure.Unknown, "broken"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new HermesException(HermesFailure.Unknown, "timeout"); }
    }

    private static async Task ThrowUnlessOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length <= 64 * 1024 && JsonNode.Parse(text) is JsonObject json)
                code = Known(Text(json["error"]?["code"]) ?? Text(json["code"]) ?? Text(json["error"]?["type"]));
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException or InvalidOperationException) { }
        var status = (int)response.StatusCode;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HermesException(HermesFailure.KeyRejected, code ?? status.ToString()),
            HttpStatusCode.NotFound => new HermesException(HermesFailure.NoProfile, code ?? "404"),
            HttpStatusCode.TooManyRequests => new HermesException(HermesFailure.Busy, code ?? "429"),
            HttpStatusCode.Conflict => new HermesException(HermesFailure.Conflict, code ?? "409"),
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType
                => new HermesException(HermesFailure.Refused, code ?? status.ToString()),
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                => new HermesException(HermesFailure.Unreachable, code ?? status.ToString()),
            _ => new HermesException(HermesFailure.ServerError, code ?? status.ToString()),
        };
    }
}
