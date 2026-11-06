using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

/// <summary>
/// "Fix it for me" on the server's page (SWITCHBOARD.md 10.8, D9): with a Tailscale OAuth client the owner made once (scopes <c>devices:core</c> and <c>policy_file</c>,
/// both write), the server turns off key expiry for itself and each of the owner's Messages phones, approves them where device approval is on, and adds an access rule
/// letting the owner's devices reach its port, through Tailscale's API. It never signs for Tailnet Lock (signing stays on the owner's own trusted device) and never
/// removes anything from the policy file: the rule is added only when missing, to the file as read (comments kept), and written back only if nobody changed it since
/// (its ETag, sent as If-Match).
/// <para>
/// The client's id and secret are kept sealed with the server's keys (<see cref="FileName"/> in the keys folder, never on the data volume, never in a backup), and can
/// be removed on the page. Logged: <c>server tailnet oauth saved|removed</c> and one <c>server tailnet fix outcome=</c> line with counts, the failing step and a status;
/// never the secret, a token, a login, a name or an address.
/// </para>
/// </summary>
internal sealed partial class TailscaleAdmin(string keysFolder, IStateProtector protector, Action<string> log, HttpMessageHandler? handler = null)
{
    /// <summary>The sealed OAuth client, in the keys folder.</summary>
    public const string FileName = "tailscale-oauth.protected";

    /// <summary>Tailscale's API.</summary>
    public static readonly Uri Api = new("https://api.tailscale.com/");

    /// <summary>Where the owner makes an OAuth client.</summary>
    public const string OAuthPage = "https://login.tailscale.com/admin/settings/oauth";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private string Path => System.IO.Path.Combine(keysFolder, FileName);

    [GeneratedRegex("^[A-Za-z0-9_-]{4,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientId();

    [GeneratedRegex("^tskey-client-[A-Za-z0-9_-]{8,300}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientSecret();

    /// <summary>Whether an OAuth client is kept.</summary>
    public bool Has => File.Exists(Path);

    /// <summary>Whether <paramref name="id"/> and <paramref name="secret"/> have the shapes of a Tailscale OAuth client's (the secret begins <c>tskey-client-</c>).</summary>
    public static bool LooksRight(string? id, string? secret) => id is not null && secret is not null && ClientId().IsMatch(id.Trim()) && ClientSecret().IsMatch(secret.Trim());

    /// <summary>Keeps the client sealed, in place of any before. False when it could not be written.</summary>
    public async Task<bool> SaveAsync(string id, string secret)
    {
        try
        {
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(new Client(id.Trim(), secret.Trim()))).ConfigureAwait(false);
            Directory.CreateDirectory(keysFolder);
            var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(sealedBytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
            log("server tailnet oauth saved");
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            log($"server tailnet oauth save_failed type={failure.GetType().Name}");
            return false;
        }
    }

    /// <summary>Forgets the client. The client itself stays in Tailscale until the owner deletes it there.</summary>
    public bool Remove()
    {
        try
        {
            File.Delete(Path);
            log("server tailnet oauth removed");
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed record Client(string Id, string Secret);

    private async Task<Client?> ReadAsync()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var opened = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Path).ConfigureAwait(false)).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Client>(opened) is { Id.Length: > 0, Secret.Length: > 0 } client ? client : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>What a fix did: the sentences for the page, and why it stopped when it did (<paramref name="Problem"/>), in plain words.</summary>
    public sealed record Report(IReadOnlyList<string> Changed, IReadOnlyList<string> Unchanged, string? Problem);

    // A step that failed: the page's sentence, and the step and status for the log.
    private sealed class Stop(string step, int status, string sentence) : Exception(sentence)
    {
        public string Step { get; } = step;
        public int Status { get; } = status;
    }

    /// <summary>
    /// Does every step the door's last report (<paramref name="view"/>) says is left and the API can do: key expiry off for the server and the owner's Messages phones it
    /// lists, approval where a device waits for it, and the access rule from the owner to the server's <paramref name="port"/> when the rules do not already let only the
    /// owner's devices in. Each step is looked at first and done only when needed.
    /// </summary>
    public async Task<Report> FixAsync(TailnetView view, int port, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        var unchanged = new List<string>();
        int expiry = 0, approved = 0;
        var rule = "skipped";
        try
        {
            var client = await ReadAsync().ConfigureAwait(false) ?? throw new Stop("client", 0, "There's no OAuth client to use. Add one first.");
            if (view.Address is not { Length: > 0 } serverAddress) throw new Stop("view", 0, "The server isn't on your Tailscale network yet. Sign it in first.");
            using var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { BaseAddress = Api, Timeout = Patience };
            var token = await TokenAsync(http, client, cancellationToken).ConfigureAwait(false);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var listed = await SendAsync(http, HttpMethod.Get, "api/v2/tailnet/-/devices", null, "devices", cancellationToken).ConfigureAwait(false);
            var devices = (await ReadJsonAsync(listed, cancellationToken).ConfigureAwait(false))?["devices"] as JsonArray ?? [];
            JsonNode? Find(string? address) => address is null ? null
                : devices.FirstOrDefault(device => device?["addresses"] is JsonArray addresses && addresses.Any(each => each?.GetValue<string>() == address));
            var server = Find(serverAddress) ?? throw new Stop("devices", 0, "Tailscale's list of machines doesn't have this server. Wait a minute and try again.");
            var targets = new List<(JsonNode Device, string Name)> { (server, view.Name ?? "the server") };
            foreach (var phone in view.Phones ?? [])
                if (Find(phone.Address) is { } found && found["user"]?.GetValue<string>() == server["user"]?.GetValue<string>()) targets.Add((found, phone.Name));
            foreach (var (device, name) in targets)
            {
                var id = device["id"]?.GetValue<string>() ?? device["nodeId"]?.GetValue<string>();
                if (id is null) continue;
                if (device["authorized"]?.GetValue<bool>() == false)
                {
                    using var _ = await SendAsync(http, HttpMethod.Post, $"api/v2/device/{Uri.EscapeDataString(id)}/authorized", new JsonObject { ["authorized"] = true }, "approve",
                        cancellationToken).ConfigureAwait(false);
                    approved++;
                    changed.Add($"Approved {name}.");
                }
                if (device["keyExpiryDisabled"]?.GetValue<bool>() == true) unchanged.Add($"Key expiry was already off for {name}.");
                else
                {
                    using var _ = await SendAsync(http, HttpMethod.Post, $"api/v2/device/{Uri.EscapeDataString(id)}/key", new JsonObject { ["keyExpiryDisabled"] = true }, "key",
                        cancellationToken).ConfigureAwait(false);
                    expiry++;
                    changed.Add($"Turned off key expiry for {name}.");
                }
            }

            if (view.OnlyYours && view.Rules == "yours") unchanged.Add("Your access rules already let only your devices reach the server.");
            else if (view.Tagged || view.Owner is not { Length: > 0 } owner)
                unchanged.Add("The server is tagged, so it has no owner to write a rule for. Your access rules decide who reaches it.");
            else
            {
                rule = await AddRuleAsync(http, owner, serverAddress, port, cancellationToken).ConfigureAwait(false);
                if (rule == "added") changed.Add($"Added an access rule letting {owner}'s devices reach the server on port {port}. Nothing else in your access rules was changed.");
                else unchanged.Add("Your access rules already have a rule letting your devices reach the server.");
            }
            log($"server tailnet fix outcome=done expiry={expiry} approved={approved} rule={rule}");
            return new(changed, unchanged, null);
        }
        catch (Stop stop)
        {
            log($"server tailnet fix outcome=failed step={stop.Step} status={stop.Status} expiry={expiry} approved={approved}");
            return new(changed, unchanged, stop.Message);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            if (failure is TaskCanceledException && cancellationToken.IsCancellationRequested) throw;
            log($"server tailnet fix outcome=failed step=network type={failure.GetType().Name} expiry={expiry} approved={approved}");
            return new(changed, unchanged, "The server couldn't reach Tailscale just now. Try again in a minute.");
        }
    }

    private static async Task<string> TokenAsync(HttpClient http, Client client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.Id, ["client_secret"] = client.Secret, ["grant_type"] = "client_credentials",
            }),
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new Stop("token", (int)response.StatusCode, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                ? "Tailscale didn't accept the OAuth client. Check its ID and secret, or make a new one and add it again."
                : "Tailscale didn't answer as expected. Try again in a minute.");
        return (await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false))?["access_token"]?.GetValue<string>() is { Length: > 0 } token
            ? token : throw new Stop("token", 200, "Tailscale didn't answer as expected. Try again in a minute.");
    }

    // One API call; a refusal stops the fix with what to do about it.
    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, JsonNode? body, string step, CancellationToken cancellationToken,
        Action<HttpRequestMessage>? adjust = null, string? text = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (text is not null) request.Content = new StringContent(text, Encoding.UTF8, "application/hujson");
        adjust?.Invoke(request);
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PreconditionFailed && step == "policy_write") return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new Stop(step, status, response.StatusCode == HttpStatusCode.Forbidden
            ? step.StartsWith("policy", StringComparison.Ordinal)
                ? "The OAuth client may not change your access rules. Make one with the Policy File scope (write), and add it again."
                : "The OAuth client may not change your machines. Make one with the Devices Core scope (write), and add it again."
            : step.StartsWith("policy", StringComparison.Ordinal) && status == 400
                ? "Tailscale didn't accept the access rule: your access rules may have a test it fails. Add the rule by hand instead."
                : "Tailscale didn't answer as expected. Try again in a minute.");
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

    // The access rule: read the policy file as written (HuJSON, with its ETag), add the grant only when no rule already lets the owner reach the port, and write it back
    // only if it is still the file read. Changed under it: read again once.
    private static async Task<string> AddRuleAsync(HttpClient http, string owner, string address, int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var read = await SendAsync(http, HttpMethod.Get, "api/v2/tailnet/-/acl", null, "policy_read", cancellationToken,
                request => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hujson"))).ConfigureAwait(false);
            var etag = read.Headers.ETag?.ToString() ?? (read.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() : null);
            var text = await read.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (etag is null) throw new Stop("policy_read", 200, "Tailscale didn't answer as expected. Try again in a minute.");
            JsonNode? policy;
            try { policy = JsonNode.Parse(HuJson.Standard(text)); }
            catch (Exception failure) when (failure is JsonException or FormatException) { throw new Stop("policy_read", 200, "The server couldn't read your access rules. Add the rule by hand instead."); }
            if (HasRule(policy, owner, address, port)) return "present";
            var grant = new JsonObject { ["src"] = new JsonArray(owner), ["dst"] = new JsonArray(address), ["ip"] = new JsonArray($"tcp:{port}") }.ToJsonString();
            var written = HuJson.AddGrant(text, grant) ?? throw new Stop("policy_read", 200, "The server couldn't read your access rules. Add the rule by hand instead.");
            using var write = await SendAsync(http, HttpMethod.Post, "api/v2/tailnet/-/acl", null, "policy_write", cancellationToken,
                request => request.Headers.TryAddWithoutValidation("If-Match", etag), written).ConfigureAwait(false);
            if (write.StatusCode != HttpStatusCode.PreconditionFailed) return "added";
            if (attempt >= 1) throw new Stop("policy_write", 412, "Your access rules kept changing while the server wrote them. Try again in a minute.");
        }
    }

    /// <summary>
    /// Whether a grant or an ACL already names the owner as reaching the server's address on the port. A rule for everyone (Tailscale's default) does not count: it is
    /// the one the owner may later take out, and the owner's own rule is what keeps their phone reaching the server then.
    /// </summary>
    internal static bool HasRule(JsonNode? policy, string owner, string address, int port)
    {
        static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(each => each is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>() : [];
        bool Source(string source) => string.Equals(source, owner, StringComparison.OrdinalIgnoreCase);
        foreach (var grant in (policy?["grants"] as JsonArray ?? []).OfType<JsonObject>())
            if (Strings(grant["src"]).Any(Source) && Strings(grant["dst"]).Any(dst => dst == address || dst == "*")
                && Strings(grant["ip"]).Any(ip => ip is "*" or "tcp:*" || ip == $"tcp:{port}" || ip == port.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return true;
        foreach (var acl in (policy?["acls"] as JsonArray ?? []).OfType<JsonObject>())
            if ((acl["action"]?.GetValue<string>() ?? "accept") == "accept" && Strings(acl["src"]).Any(Source)
                && Strings(acl["dst"]).Any(dst => dst is "*:*" || dst == $"{address}:*" || dst == $"{address}:{port}" || dst == $"*:{port}"))
                return true;
        return false;
    }
}

/// <summary>
/// Tailscale's policy file is HuJSON: JSON with comments and trailing commas. Read here only to find where to add, so what the owner wrote (comments, order, spacing)
/// is written back as it was, with the one addition.
/// </summary>
internal static class HuJson
{
    private readonly record struct Token(char Kind, int Start, int End);

    // '{', '}', '[', ']', ':', ',', 's' a string, 'v' any other value, '/' a comment. Throws FormatException on an unfinished string or comment.
    private static List<Token> Tokens(string text)
    {
        var list = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var start = i;
                while (i < text.Length && text[i] != '\n') i++;
                list.Add(new('/', start, i));
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("An unfinished comment.");
                list.Add(new('/', i, end + 2));
                i = end + 2;
                continue;
            }
            if (c == '"')
            {
                var start = i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                if (i >= text.Length) throw new FormatException("An unfinished string.");
                list.Add(new('s', start, ++i));
                continue;
            }
            if ("{}[]:,".Contains(c)) { list.Add(new(c, i, i + 1)); i++; continue; }
            var from = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && "{}[]:,\"/".IndexOf(text[i]) < 0) i++;
            if (i == from) throw new FormatException("A stray character.");
            list.Add(new('v', from, i));
        }
        return list;
    }

    /// <summary>The text as plain JSON: comments and trailing commas out.</summary>
    public static string Standard(string text)
    {
        var tokens = Tokens(text);
        var drop = new List<(int Start, int End)>();
        var meaningful = tokens.Where(token => token.Kind != '/').ToList();
        for (var k = 0; k < meaningful.Count; k++)
            if (meaningful[k].Kind == ',' && (k + 1 == meaningful.Count || meaningful[k + 1].Kind is '}' or ']')) drop.Add((meaningful[k].Start, meaningful[k].End));
        drop.AddRange(tokens.Where(token => token.Kind == '/').Select(token => (token.Start, token.End)));
        var plain = new StringBuilder(text.Length);
        var at = 0;
        foreach (var (start, end) in drop.OrderBy(span => span.Start))
        {
            plain.Append(text, at, start - at);
            // A space where a comment was, so two values it separated stay apart.
            plain.Append(' ');
            at = end;
        }
        return plain.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// The text with <paramref name="grant"/> (one JSON object) added first in the top-level <c>grants</c> array, or with a new <c>grants</c> member holding it at the top of
    /// the policy when there is none. Null when the text is not a policy object, or its <c>grants</c> is not an array.
    /// </summary>
    public static string? AddGrant(string text, string grant)
    {
        List<Token> tokens;
        try { tokens = [.. Tokens(text).Where(token => token.Kind != '/')]; }
        catch (FormatException) { return null; }
        if (tokens.Count == 0 || tokens[0].Kind != '{') return null;
        var depth = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            var token = tokens[k];
            if (token.Kind is '{' or '[') depth++;
            else if (token.Kind is '}' or ']') depth--;
            else if (token.Kind == 's' && depth == 1 && k + 1 < tokens.Count && tokens[k + 1].Kind == ':' && Key(text, token) == "grants")
            {
                if (k + 2 >= tokens.Count || tokens[k + 2].Kind != '[') return null;
                var at = tokens[k + 2].End;
                return text[..at] + "\n\t\t// Added by Messages Server: your devices reach it.\n\t\t" + grant + "," + text[at..];
            }
        }
        var top = tokens[0].End;
        return text[..top] + "\n\t// Added by Messages Server: your devices reach it.\n\t\"grants\": [" + grant + "]," + text[top..];
    }

    private static string? Key(string text, Token token)
    {
        try { return JsonSerializer.Deserialize<string>(text.AsSpan(token.Start, token.End - token.Start)); }
        catch (JsonException) { return null; }
    }
}
