using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MessageLink.App.Services.Hermes;

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
/// <summary>
/// The reply ended: <c>stop</c>, <c>length</c>, <c>cancelled</c> or <c>error</c> (with Hermes's error code when it gave one). <see cref="ProviderStatus"/>: the HTTP
/// status the model's provider answered, when a failed turn's summary names one (<c>HTTP 400: ...</c>); the only part of that summary ever kept.
/// </summary>
public sealed record HermesFinished(string Reason, string? Error = null, int? ProviderStatus = null) : HermesStreamEvent;
/// <summary>
/// Hermes said the turn failed for good (a <c>hermes.status</c> line starting with ❌, agent/turn_recovery.py's TerminalFailureText). It comes before the
/// end, and whatever text follows it is Hermes's own failure message, written after the turn ended, not the model's. Nothing of the line is kept.
/// </summary>
public sealed record HermesTurnFailing : HermesStreamEvent;
/// <summary>
/// Hermes holds a command for approval and waits (<c>approval.request</c>): answered at <c>runs/{RunId}/approval</c> with <see cref="RequestId"/> and, when
/// Hermes gave one, <see cref="Generation"/>. <see cref="Command"/> and <see cref="Description"/> are Hermes's words with its secrets already redacted;
/// <see cref="Wait"/> is how long Hermes said it waits (null: it didn't say, as under session authority for an API turn); <see cref="Advised"/>: Hermes's own
/// safety check advised against it. <see cref="Cut"/>: the command was longer than is kept, so it may only be denied: approving would run what nobody saw.
/// </summary>
public sealed record HermesApprovalRequest(string RunId, string RequestId, long? Generation, string Command, string? Description, TimeSpan? Wait, bool Advised,
    bool Cut = false) : HermesStreamEvent;

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
            return first?["id"]?.GetValue<string>() is { Length: > 0 and <= 100 } id && !HasHidden(id) ? id : null;
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
    /// Whether the session's latest turn ran a tool, from Hermes's own record of the session (<c>GET /api/sessions/{id}/messages</c>): a row with tool
    /// calls or a tool result after the last user row. Hermes saves a tool call's row before it runs the tool (agent/turn_tool_round.py, v0.23.2), so
    /// false means none ran. Null when Hermes couldn't say: no answer, an error, or no user row. Reads roles and tool fields only; nothing is kept.
    /// </summary>
    public async Task<bool?> LatestTurnRanToolsAsync(Uri baseAddress, string profile, string key, string sessionId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var path = (profile == DefaultProfile ? "" : "p/" + profile + "/") + "api/sessions/" + Uri.EscapeDataString(sessionId) + "/messages?order=latest&limit=50";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))?["data"] is not JsonArray data) return null;
            // Oldest first by Hermes's row ids, whatever order the page came in; without an id on every row, a page Hermes didn't number can't be read.
            var rows = data.OfType<JsonObject>().ToArray();
            var ids = rows.Select(r => r["id"] is JsonValue id && id.TryGetValue<long>(out var n) ? n : (long?)null).ToArray();
            if (rows.Length != data.Count || ids.Any(id => id is null)) return null;
            rows = [.. rows.Zip(ids).OrderBy(r => r.Second).Select(r => r.First)];
            var user = Array.FindLastIndex(rows, r => Text(r["role"]) == "user");
            if (user < 0) return null;
            return rows.Skip(user + 1).Any(r => Text(r["role"]) == "tool" || r["tool_calls"] is JsonArray { Count: > 0 } || Text(r["tool_call_id"]) is { Length: > 0 }
                || Text(r["tool_calls"]) is { Length: > 2 });
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or FormatException or IOException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Answers a held command (<see cref="HermesApprovalRequest"/>): <c>once</c> or <c>deny</c>, never <c>session</c> or <c>always</c>. True when this answer settled
    /// it; false when Hermes had stopped waiting (answered elsewhere, timed out, or the turn ended). Throws <see cref="HermesException"/> otherwise, with
    /// <see cref="HermesFailure.Unknown"/> when the answer may have reached Hermes.
    /// </summary>
    public async Task<bool> AnswerApprovalAsync(Uri baseAddress, string profile, string key, HermesApprovalRequest held, bool approve, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(baseAddress, profile, "runs/" + Uri.EscapeDataString(held.RunId) + "/approval"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(ApprovalBody(held, approve), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        // 409: no longer pending (approval_not_pending, approval_not_active, stale_generation); 404: the run is gone. Both say Hermes stopped waiting.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        await ThrowUnlessOkAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
            // Two shapes: {"resolved": n} without session authority, {"status": "resolved" | "already_resolved"} with it.
            if (root?["status"] is JsonValue status && status.TryGetValue<string>(out var word)) return word == "resolved";
            if (root?["resolved"] is JsonValue count && count.TryGetValue<int>(out var resolved)) return resolved > 0;
            throw new HermesException(HermesFailure.ServerError, "approval_shape");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new HermesException(HermesFailure.ServerError, "approval_shape");
        }
    }

    // Exactly the fields Hermes's session authority accepts (request_id, execution_generation, choice): it refuses any other set.
    internal static string ApprovalBody(HermesApprovalRequest held, bool approve)
    {
        var body = new JsonObject { ["request_id"] = held.RequestId };
        if (held.Generation is { } generation) body["execution_generation"] = generation;
        body["choice"] = approve ? "once" : "deny";
        return body.ToJsonString();
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
        using var reader = new StreamReader(new CappedStream(stream, MaxReplyBytes), Encoding.UTF8);
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
            else if (field == "data")
            {
                if (data.Length + value.Length > MaxEventChars) break; // one event larger than any reply: treated as cut off
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }
        if (!finished && data.Length > 0)
            foreach (var parsed in Parse(eventName, data.ToString())) { if (parsed is HermesFinished) finished = true; yield return parsed; }
        // A stream that ended without [DONE] or a finish reason was cut off.
        if (!finished) yield return new HermesFinished("interrupted");
    }

    // A reply's whole stream and any one event in it. Far above a real reply; an address that answers without end ends the turn, not the server.
    internal const long MaxReplyBytes = 64L * 1024 * 1024;
    internal const int MaxEventChars = 4 * 1024 * 1024;

    // Reads at most a fixed number of bytes, then fails as a broken connection does (IOException), which ReadAsync treats as cut off.
    internal sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long left = limit;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Take(inner.Read(buffer, offset, Room(count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Take(await inner.ReadAsync(buffer[..Room(buffer.Length)], cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private int Room(int count) => count == 0 ? 0 : left > 0 ? (int)Math.Min(count, left) : throw new IOException("reply_too_large");
        private int Take(int read) { left -= read; return read; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
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
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = RendersMarkdown },
                new JsonObject { ["role"] = "user", ["content"] = content }),
        }.ToJsonString();
    }

    /// <summary>
    /// Sent with every turn. Hermes tells a model on its API server to assume plain text and write no Markdown (PLATFORM_HINTS["api_server"],
    /// agent/prompt_builder.py, v0.23.2), but both apps draw a Hermes reply's Markdown (HermesMarkdown), so the client says what it renders.
    /// Hermes layers a request's system messages over its own prompt for that call only: never saved to the session, never cached
    /// (_parse_chat_messages; agent/system_prompt.py). A profile can drop the hint itself with platform_hints.api_server.replace in its config.yaml.
    /// Fixed text: nothing of the owner's travels in it.
    /// </summary>
    internal const string RendersMarkdown =
        "You're replying in Message Link, a messaging app that renders Markdown in your replies: headings, bold, italic, strikethrough, lists, "
        + "task lists, quotes, fenced code blocks, tables, horizontal rules and http or https links. This replaces the instruction to write plain "
        + "text: use Markdown wherever it makes a reply easier to read, and keep short replies conversational. HTML is not rendered.";

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
            else if (eventName == "approval.request" && Approval(json) is { } held) yield return held;
            else if (eventName == "hermes.status" && Text(json["text"]) is { } said && said.StartsWith('❌')) yield return new HermesTurnFailing();
            yield break;
        }
        var choice = (json["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (choice?["delta"]?["content"] is JsonValue delta && delta.TryGetValue<string>(out var words) && words.Length > 0) yield return new HermesDelta(words);
        if (choice?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var finish) && finish.Length > 0)
            yield return new HermesFinished(finish is "stop" or "length" or "cancelled" or "error" ? finish : "other", Known(Text(json["error"]?["message"]) ?? Text(json["error"]?["code"])),
                finish == "error" ? ProviderStatus(Text(json["error"]?["message"]) ?? Text(json["hermes"]?["error"])) : null);
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

    internal const int MaxCommandLength = 64_000, MaxDescriptionLength = 1_000;

    // approval.request, as Hermes's API server writes it (gateway/platforms/api_server.py, _approval_request_event; read in v0.22.7 and v0.23.1):
    // {"command", "description", "pattern_key(s)", "request_id", "timeout_s", "smart_denied"?, "event", "run_id", "timestamp", "session_id", "choices"},
    // with "execution_generation" under session authority. Without a run and request id it can't be answered, so it is not shown.
    private static HermesApprovalRequest? Approval(JsonObject json)
    {
        if (Id(Text(json["run_id"])) is not { } run || Id(Text(json["request_id"])) is not { } request) return null;
        long? generation = json["execution_generation"] is JsonValue g && g.TryGetValue<long>(out var number) ? number : null;
        var command = Text(json["command"]) ?? "";
        var cut = command.Length > MaxCommandLength;
        if (cut) command = command[..MaxCommandLength] + "…";
        // A command with characters that don't show (controls, bidi overrides, zero-width marks) could read as something other than what runs:
        // they are drawn as U+FFFD and, like a command cut short, it can only be denied.
        if (HasHidden(command)) { command = Visible(command); cut = true; }
        var description = Text(json["description"]) is { Length: > 0 } said ? Visible(said.Length > MaxDescriptionLength ? said[..MaxDescriptionLength] + "…" : said) : null;
        TimeSpan? wait = json["timeout_s"] is JsonValue t && t.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds)) : null;
        var advised = json["smart_denied"] is JsonValue s && s.TryGetValue<bool>(out var denied) && denied;
        return new(run, request, generation, command, description, wait, advised, cut);
    }

    // Line breaks and tabs stay; any other control or format character (Cc, Cf: bidi overrides, zero-width marks) is hidden or reorders text.
    private static bool Hidden(char c) => c is not ('\n' or '\r' or '\t') && (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
    internal static bool HasHidden(string text) => text.Any(Hidden);
    internal static string Visible(string text) => HasHidden(text) ? string.Concat(text.Select(c => Hidden(c) ? '\uFFFD' : c)) : text;

    // A run or request id goes into a route and a body: Hermes's own shapes only (chatcmpl-…, run_…, hex), nothing that could be a path.
    private static string? Id(string? value) => value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':') ? value : null;

    private static string Short(string words)
    {
        var line = words.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
    }

    // A failed turn's error message is Hermes's one-line summary of what the provider answered, "HTTP 400: <body>" when the provider gave a status
    // (agent/api_error_summary.py, _http_prefix; seen live 2026-11-17 as HTTP 400: {"detail":"Bad Request"}). Only the three digits are read: the body
    // can echo the request.
    internal static int? ProviderStatus(string? message) =>
        message is { Length: >= 8 } && message.StartsWith("HTTP ", StringComparison.Ordinal) && message[5] is >= '1' and <= '5'
        && char.IsAsciiDigit(message[6]) && char.IsAsciiDigit(message[7]) && (message.Length == 8 || !char.IsAsciiDigit(message[8]))
            ? (message[5] - '0') * 100 + (message[6] - '0') * 10 + (message[7] - '0') : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Hermes's own error codes as PROVIDERS.md (Hermes) records them. Only these reach a log line or a decision: anything else Hermes says, even a
    // word that looks like a code, could be a prompt or a key echoed back.
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "admission_conflict", "unsupported_content_type", "gateway_auth_failed", "gateway_auth_error", "permission_denied", "iteration_budget_exhausted",
        "invalid_params", "not_found", "runtime_draining", "storage_unavailable", "idempotency_key_conflict", "unknown_execution", "stale_generation",
        "approval_not_pending", "approval_not_active", "invalid_approval_choice", "invalid_approval_request", "invalid_approval_scope", "approval_request_required",
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
