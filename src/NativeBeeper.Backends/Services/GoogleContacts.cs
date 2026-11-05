using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

/// <summary>How a contact save ended. <see cref="Unknown"/>: the request left and no answer came, so it may have been saved; it is never sent again by itself.</summary>
public enum GoogleContactSave { Saved, NotConnected, Refused, Failed, Unknown, Cancelled, NotFound, Several, AlreadyThere }

/// <summary>A contact to save. <see cref="PhoneType"/> is one of People API's own words (<see cref="GoogleContacts.PhoneTypes"/>); <see cref="Company"/> may be empty.</summary>
public sealed record GoogleContactDraft(string GivenName, string FamilyName, string Phone, string PhoneType = "mobile", string Company = "");

/// <summary>
/// Adds a contact to the owner's Google account through the People API, from this PC (owner, 2026-11-05). Google Messages and Google Voice take their
/// names from Google Contacts, so a contact saved here names the conversation on every device once they read their contacts again.
/// </summary>
/// <remarks>
/// <para>The OAuth client is the owner's own (Google Cloud, a Desktop app client), read from <c>google-contacts-client.json</c> in the data folder exactly as
/// Google downloads it. Without that file nothing here is offered. Its secret is not one (Google says so for installed apps); PKCE is what protects the
/// code.</para>
/// <para>Connecting opens the person's own browser at Google's consent page and takes the answer on a loopback port (127.0.0.1, any free port), which
/// Google allows for Desktop clients without registering it. Only the refresh token is kept, protected for this Windows user. Google cancels a refresh
/// token after 7 days while the client's consent screen is in Testing, so the owner's client is set to In production; a refused refresh forgets the token
/// and the next save asks again.</para>
/// <para>The consent also asks for the account's email address (<c>openid email</c>), read once from the answer's ID token and kept with the
/// permission, so the dialog can say which account a contact goes to.</para>
/// <para>Logs say steps and status codes only: never a name, a number, an email address, a token or the client.</para>
/// </remarks>
public sealed class GoogleContacts : IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/contacts";
    private const string Scopes = "openid email " + Scope;
    /// <summary>The phone types offered, in People API's words, the first the default.</summary>
    public static readonly IReadOnlyList<string> PhoneTypes = ["mobile", "home", "work", "main", "other"];
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string CreateEndpoint = "https://people.googleapis.com/v1/people:createContact?personFields=names";
    public const int MaxNameLength = 128;

    private readonly string folder;
    private readonly IStateProtector protector;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private string? account;
    private DateTimeOffset accessUntil;

    /// <summary>How long the browser is given to answer before Connect gives up.</summary>
    public TimeSpan ConsentWait { get; set; } = TimeSpan.FromMinutes(5);

    public GoogleContacts(string folder, IStateProtector protector, HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        this.folder = folder;
        this.protector = protector;
        http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(20) };
        this.log = log ?? AppLog.Write;
    }

    public GoogleContacts(IStateProtector protector) : this(AppPaths.DataRoot, protector) { }

    private string ClientPath => Path.Combine(folder, "google-contacts-client.json");
    private string TokenPath => Path.Combine(folder, "google-contacts.protected");

    /// <summary>The owner has put an OAuth client in place, so Add to contacts can be offered.</summary>
    public bool Offered => ReadClient() is not null;

    /// <summary>A permission is kept from an earlier Connect (it may still be refused when used).</summary>
    public bool Connected => File.Exists(TokenPath);

    private sealed record Client(string Id, string Secret);

    private Client? ReadClient()
    {
        try
        {
            if (!File.Exists(ClientPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(ClientPath));
            var installed = root?["installed"];
            var id = installed?["client_id"]?.GetValue<string>();
            var secret = installed?["client_secret"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : new Client(id, secret);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The Google account a contact is saved to, once a permission is kept; null before the first Connect (or a kept one that is unreadable).</summary>
    public async Task<string?> AccountAsync() => account ??= (await ReadKeptAsync())?.Email;

    /// <summary>
    /// Saves <paramref name="contact"/>, connecting first if no permission is kept: <paramref name="openBrowser"/> shows Google's consent page and
    /// <paramref name="waiting"/> is told while the browser has it.
    /// </summary>
    public async Task<GoogleContactSave> SaveAsync(GoogleContactDraft contact, Func<Uri, Task<bool>> openBrowser, Action? waiting = null,
        CancellationToken cancellationToken = default)
    {
        contact = contact with
        {
            GivenName = contact.GivenName.Trim(), FamilyName = contact.FamilyName.Trim(), Phone = contact.Phone.Trim(), Company = contact.Company.Trim(),
        };
        if (contact.GivenName.Length + contact.FamilyName.Length == 0 || contact.Phone.Length == 0 || !PhoneTypes.Contains(contact.PhoneType)
            || new[] { contact.GivenName, contact.FamilyName, contact.Phone, contact.Company }.Any(field => field.Length > MaxNameLength)) return GoogleContactSave.Failed;
        return await WithTokenAsync("save", openBrowser, waiting, token => CreateAsync(token, contact, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Puts <paramref name="png"/> on the Google contact that has <paramref name="phone"/>, or with null takes that contact's photo off, connecting first as
    /// <see cref="SaveAsync"/> does. <see cref="GoogleContactSave.NotFound"/>: no contact has the number; <see cref="GoogleContactSave.Several"/>: more than
    /// one does, and which is meant is not guessed. Either way nothing was changed.
    /// </summary>
    public async Task<GoogleContactSave> SetPhotoAsync(string phone, byte[]? png, Func<Uri, Task<bool>> openBrowser, Action? waiting = null,
        CancellationToken cancellationToken = default)
    {
        phone = phone.Trim();
        if (Canonical(phone) is null || png is { Length: 0 }) return GoogleContactSave.Failed;
        return await WithTokenAsync("photo", openBrowser, waiting, async token =>
        {
            var found = await FindAsync(token, phone, cancellationToken);
            if (found.Outcome is { } failed) return failed;
            if (found.People.Count == 0) { log("google contacts photo outcome=not_found"); return GoogleContactSave.NotFound; }
            if (found.People.Count > 1) { log($"google contacts photo outcome=several count={found.People.Count}"); return GoogleContactSave.Several; }
            return await ChangePhotoAsync(token, found.People[0], png, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>A contact found by name, to add a number to: its People API name (<c>people/c…</c>), its display name and how many numbers it has.</summary>
    public sealed record ContactMatch(string ResourceName, string Name, int Numbers);

    /// <summary>
    /// The owner's contacts whose name starts with <paramref name="query"/> (People API's own prefix search), for Add to contacts' Existing contact. Reads
    /// only; connects first as <see cref="SaveAsync"/> does. <see cref="GoogleContactSave.Saved"/> with the matches, or why there are none.
    /// </summary>
    public async Task<(IReadOnlyList<ContactMatch> Matches, GoogleContactSave Outcome)> SearchByNameAsync(string query, Func<Uri, Task<bool>> openBrowser,
        Action? waiting = null, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return ([], GoogleContactSave.Saved);
        var matches = new List<ContactMatch>();
        var outcome = await WithTokenAsync("search", openBrowser, waiting, async token =>
        {
            // Google's cache wants one empty search before the first real one; a name typed a moment later finds what is there.
            if (!nameSearchWarmed)
            {
                using var warm = await SearchAsync(token, "", cancellationToken, "names");
                if (await RefusalAsync(warm, "search") is { } refusedWarm) return refusedWarm;
                nameSearchWarmed = true;
            }
            using var response = await SearchAsync(token, query, cancellationToken, "names,phoneNumbers");
            if (await RefusalAsync(response, "search") is { } refused) return refused;
            foreach (var result in JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["results"]?.AsArray() ?? [])
            {
                var person = result?["person"];
                if (person?["resourceName"]?.GetValue<string>() is not { Length: > 0 } name || !name.StartsWith("people/", StringComparison.Ordinal)) continue;
                var shown = person["names"]?.AsArray().Select(entry => entry?["displayName"]?.GetValue<string>()).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
                if (shown is null) continue;
                matches.Add(new ContactMatch(name, shown, person["phoneNumbers"]?.AsArray().Count ?? 0));
            }
            return GoogleContactSave.Saved;
        }, cancellationToken);
        return (matches, outcome);
    }

    private bool nameSearchWarmed;

    /// <summary>
    /// Adds <paramref name="phone"/> to the existing contact <paramref name="resourceName"/> (from <see cref="SearchByNameAsync"/>), keeping its other
    /// numbers. <see cref="GoogleContactSave.AlreadyThere"/>: it has the number already, and nothing was changed. The contact is read and written back
    /// under Google's etag, so a contact changed elsewhere in between is refused rather than overwritten, and never tried again here.
    /// </summary>
    public async Task<GoogleContactSave> AddNumberAsync(string resourceName, string phone, string phoneType, Func<Uri, Task<bool>> openBrowser,
        Action? waiting = null, CancellationToken cancellationToken = default)
    {
        phone = phone.Trim();
        if (!resourceName.StartsWith("people/", StringComparison.Ordinal) || resourceName.Length > 64 || resourceName[7..].Any(c => !char.IsAsciiLetterOrDigit(c))
            || phone.Length == 0 || phone.Length > MaxNameLength || !PhoneTypes.Contains(phoneType)) return GoogleContactSave.Failed;
        return await WithTokenAsync("add_number", openBrowser, waiting, async token =>
        {
            // The contact's own data only (a linked Google profile's numbers are not the contact's to write), with the source Google wants named back.
            using var read = new HttpRequestMessage(HttpMethod.Get,
                "https://people.googleapis.com/v1/" + resourceName + "?personFields=metadata,phoneNumbers&sources=READ_SOURCE_TYPE_CONTACT");
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await http.SendAsync(read, cancellationToken);
            if (await RefusalAsync(current, "add_number") is { } refused) return refused;
            var person = JsonNode.Parse(await current.Content.ReadAsStringAsync(cancellationToken));
            var source = person?["metadata"]?["sources"]?.AsArray().FirstOrDefault(entry => entry?["type"]?.GetValue<string>() == "CONTACT");
            if (person?["etag"]?.GetValue<string>() is not { Length: > 0 } etag || source?["id"]?.GetValue<string>() is not { Length: > 0 } sourceId
                || source["etag"]?.GetValue<string>() is not { Length: > 0 } sourceEtag)
            {
                log("google contacts add_number outcome=failed step=etag");
                return GoogleContactSave.Failed;
            }
            var wanted = Canonical(phone);
            var numbers = new JsonArray();
            foreach (var number in person["phoneNumbers"]?.AsArray() ?? [])
            {
                // Another source's number (a profile's) is shown with the contact but is not written into it.
                if (number?["metadata"]?["source"]?["type"]?.GetValue<string>() is { } from && from != "CONTACT") continue;
                var value = number?["value"]?.GetValue<string>() ?? "";
                var known = Canonical(number?["canonicalForm"]?.GetValue<string>() is { Length: > 0 } canonical ? canonical : value);
                if ((wanted is not null && known == wanted)
                    || (value.Length > 0 && !value.Any(char.IsLetter) && !phone.Any(char.IsLetter) && Digits(value) == Digits(phone)))
                {
                    log("google contacts add_number outcome=already_there");
                    return GoogleContactSave.AlreadyThere;
                }
                // Written back as it was (its label, and which number is preferred), without what Google works out itself (canonicalForm,
                // formattedType, metadata.primary).
                var kept = new JsonObject { ["value"] = value };
                if (number?["type"]?.GetValue<string>() is { Length: > 0 } type) kept["type"] = type;
                if (number?["metadata"] is JsonObject metadata)
                {
                    var keptMetadata = new JsonObject();
                    if (metadata["sourcePrimary"]?.GetValue<bool>() is { } preferred) keptMetadata["sourcePrimary"] = preferred;
                    if (metadata["source"] is JsonNode from2) keptMetadata["source"] = from2.DeepClone();
                    if (keptMetadata.Count > 0) kept["metadata"] = keptMetadata;
                }
                numbers.Add(kept);
            }
            numbers.Add(new JsonObject { ["value"] = phone, ["type"] = phoneType });
            var body = new JsonObject
            {
                ["etag"] = etag,
                ["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = sourceId, ["etag"] = sourceEtag }) },
                ["phoneNumbers"] = numbers,
            };
            using var write = new HttpRequestMessage(HttpMethod.Patch, "https://people.googleapis.com/v1/" + resourceName + ":updateContact?updatePersonFields=phoneNumbers")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await MutateAsync(write, "add_number", cancellationToken);
        }, cancellationToken);
    }

    // The one request that changes the account: an answer is the outcome, no answer (or a server error after it left) is unknown, never sent again.
    private async Task<GoogleContactSave> MutateAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            log($"google contacts {what} outcome=unknown");
            return GoogleContactSave.Unknown;
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) { log($"google contacts {what} outcome=saved"); return GoogleContactSave.Saved; }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { accessToken = null; log($"google contacts {what} outcome=unauthorized status=401"); return GoogleContactSave.NotConnected; }
            if (await InsufficientAsync(response)) { log($"google contacts {what} outcome=insufficient_scope status=403"); Forget(); return GoogleContactSave.NotConnected; }
            log($"google contacts {what} outcome={(status >= 500 ? "unknown" : "refused")} status={status}");
            return status >= 500 ? GoogleContactSave.Unknown : GoogleContactSave.Refused;
        }
    }

    // A token, from the kept permission or from Connect, then the one request that changes the account. Anything that goes wrong before that request
    // changed nothing; the request answers for itself from the moment it leaves.
    private async Task<GoogleContactSave> WithTokenAsync(string step, Func<Uri, Task<bool>> openBrowser, Action? waiting, Func<string, Task<GoogleContactSave>> act,
        CancellationToken cancellationToken)
    {
        if (ReadClient() is not { } client) { log($"google contacts {step} outcome=no_client"); return GoogleContactSave.NotConnected; }
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return GoogleContactSave.Cancelled; }
        try
        {
            var token = await AccessTokenAsync(client, cancellationToken);
            if (token is null)
            {
                waiting?.Invoke();
                var connected = await ConnectAsync(client, openBrowser, cancellationToken);
                if (connected != GoogleContactSave.Saved) return connected;
                token = await AccessTokenAsync(client, cancellationToken);
                if (token is null) return GoogleContactSave.NotConnected;
            }
            return await act(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return GoogleContactSave.Cancelled; }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Before the change was asked for (a token, the consent's answer, keeping the permission, finding the contact): nothing was changed.
            log($"google contacts {step} outcome=failed step=token error={error.GetType().Name}");
            return GoogleContactSave.Failed;
        }
        finally { gate.Release(); }
    }

    private static string Digits(string phone) => new(phone.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// A number in international form ("+" and its digits), or null when it cannot be put in one. A number written with its country code is taken as it
    /// is; one without is read as North American (ten digits, or eleven starting with 1), the owner's numbering plan and Google Voice's only one. Anything
    /// else has no country code to compare by, so it is never taken to be another number.
    /// </summary>
    internal static string? Canonical(string phone)
    {
        var trimmed = phone.Trim();
        var digits = Digits(trimmed);
        if (digits.Length < 4) return null;
        if (trimmed.StartsWith('+')) return "+" + digits;
        if (trimmed.StartsWith("00", StringComparison.Ordinal)) return "+" + digits[2..];
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits[0] == '1') return "+" + digits;
        return null;
    }

    // Two numbers are the same when their international forms are: +1 917… and +91 917… are not, however their last digits agree.
    internal static bool SameNumber(string a, string b) => Canonical(a) is { } x && x == Canonical(b);

    /// <summary>How long Google's search is given after the empty warm-up search before the number is looked for; Google's own guidance waits a few seconds.</summary>
    public TimeSpan SearchSettle { get; set; } = TimeSpan.FromSeconds(3);

    // Every contact that has the number, through People API's own search (which matches phone numbers). Each lookup first sends the empty search Google
    // asks for, so its cache is current (a contact added a moment ago is found), and waits for it; the number is then tried as written and in its
    // international form, and only results whose number really is this one are kept, each contact once. A contact's number is taken in the form Google
    // worked out for it (canonicalForm) where it has one.
    private async Task<(IReadOnlyList<string> People, GoogleContactSave? Outcome)> FindAsync(string token, string phone, CancellationToken cancellationToken)
    {
        var wanted = Canonical(phone)!;
        using (var warm = await SearchAsync(token, "", cancellationToken))
            if (await RefusalAsync(warm, "find") is { } refused) return ([], refused);
        if (SearchSettle > TimeSpan.Zero) await Task.Delay(SearchSettle, cancellationToken);
        var people = new List<string>();
        foreach (var query in new[] { phone, wanted }.Distinct())
        {
            using var response = await SearchAsync(token, query, cancellationToken);
            if (await RefusalAsync(response, "find") is { } refused) return ([], refused);
            var results = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["results"]?.AsArray() ?? [];
            foreach (var result in results)
            {
                var person = result?["person"];
                var numbers = person?["phoneNumbers"]?.AsArray() ?? [];
                var matches = numbers.Any(number =>
                    Canonical(number?["canonicalForm"]?.GetValue<string>() is { Length: > 0 } canonical ? canonical : number?["value"]?.GetValue<string>() ?? "") == wanted);
                if (matches && person?["resourceName"]?.GetValue<string>() is { Length: > 0 } name && !people.Contains(name)) people.Add(name);
            }
        }
        return (people, null);
    }

    // An answer that is not a good one: the permission it lacks, or a failure. Null for a good answer.
    private async Task<GoogleContactSave?> RefusalAsync(HttpResponseMessage response, string step)
    {
        if (response.IsSuccessStatusCode) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized) { accessToken = null; log($"google contacts {step} outcome=unauthorized status=401"); return GoogleContactSave.NotConnected; }
        if (await InsufficientAsync(response)) { log($"google contacts {step} outcome=insufficient_scope status=403"); Forget(); return GoogleContactSave.NotConnected; }
        log($"google contacts {step} outcome=failed status={(int)response.StatusCode}");
        return GoogleContactSave.Failed;
    }

    // A 403 that says the permission does not cover contacts (part of Google's consent was declined, or it was narrowed since): kept, it could never work,
    // so it is forgotten and the next try asks again.
    private static async Task<bool> InsufficientAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        if (response.Headers.WwwAuthenticate.ToString().Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase)) return true;
        var body = await response.Content.ReadAsStringAsync();
        return body.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.Ordinal) || body.Contains("insufficientPermissions", StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> SearchAsync(string token, string query, CancellationToken cancellationToken, string readMask = "phoneNumbers")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://people.googleapis.com/v1/people:searchContacts?readMask=" + readMask + "&pageSize=30&query=" + Uri.EscapeDataString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, cancellationToken);
    }

    private async Task<GoogleContactSave> ChangePhotoAsync(string token, string person, byte[]? png, CancellationToken cancellationToken)
    {
        var address = "https://people.googleapis.com/v1/" + person + (png is null ? ":deleteContactPhoto" : ":updateContactPhoto");
        using var request = new HttpRequestMessage(png is null ? HttpMethod.Delete : HttpMethod.Patch, address);
        if (png is not null)
            request.Content = new StringContent(new JsonObject { ["photoBytes"] = Convert.ToBase64String(png) }.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var what = png is null ? "photo_remove" : "photo_set";
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            log($"google contacts {what} outcome=unknown");
            return GoogleContactSave.Unknown;
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) { log($"google contacts {what} outcome=saved"); return GoogleContactSave.Saved; }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { accessToken = null; log($"google contacts {what} outcome=unauthorized status=401"); return GoogleContactSave.NotConnected; }
            if (await InsufficientAsync(response)) { log($"google contacts {what} outcome=insufficient_scope status=403"); Forget(); return GoogleContactSave.NotConnected; }
            log($"google contacts {what} outcome={(status >= 500 ? "unknown" : "refused")} status={status}");
            return status >= 500 ? GoogleContactSave.Unknown : GoogleContactSave.Refused;
        }
    }

    /// <summary>Forgets the kept permission on this PC. Google still lists the app under the account's third-party access until it is removed there.</summary>
    public void Forget()
    {
        accessToken = null;
        account = null;
        try { File.Delete(TokenPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        log("google contacts forgotten");
    }

    private async Task<GoogleContactSave> CreateAsync(string token, GoogleContactDraft contact, CancellationToken cancellationToken)
    {
        var name = new JsonObject();
        if (contact.GivenName.Length > 0) name["givenName"] = contact.GivenName;
        if (contact.FamilyName.Length > 0) name["familyName"] = contact.FamilyName;
        var body = new JsonObject
        {
            ["names"] = new JsonArray(name),
            ["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = contact.Phone, ["type"] = contact.PhoneType }),
        };
        if (contact.Company.Length > 0) body["organizations"] = new JsonArray(new JsonObject { ["name"] = contact.Company });
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateEndpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Cancelled before it leaves, nothing was asked; once it has left, a cancel is no answer and the contact may be saved.
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // Gone and not answered: it may be saved. Said as unknown, never sent again here.
            log("google contacts save outcome=unknown");
            return GoogleContactSave.Unknown;
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) { log("google contacts save outcome=saved"); return GoogleContactSave.Saved; }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                accessToken = null;
                log("google contacts save outcome=unauthorized status=401");
                return GoogleContactSave.NotConnected;
            }
            if (await InsufficientAsync(response)) { log("google contacts save outcome=insufficient_scope status=403"); Forget(); return GoogleContactSave.NotConnected; }
            log($"google contacts save outcome={(status >= 500 ? "unknown" : "refused")} status={status}");
            // A server error may have come after the contact was made.
            return status >= 500 ? GoogleContactSave.Unknown : GoogleContactSave.Refused;
        }
    }

    // A fresh access token from the kept refresh token, or null when none is kept or Google no longer accepts it (then it is forgotten).
    private async Task<string?> AccessTokenAsync(Client client, CancellationToken cancellationToken)
    {
        if (accessToken is not null && DateTimeOffset.UtcNow < accessUntil) return accessToken;
        if (await ReadKeptAsync() is not { } kept) return null;
        account = kept.Email;
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.Id, ["client_secret"] = client.Secret, ["refresh_token"] = kept.Refresh, ["grant_type"] = "refresh_token",
        }), cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            log($"google contacts refresh outcome=refused status={(int)response.StatusCode}");
            Forget();
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            log($"google contacts refresh outcome=failed status={(int)response.StatusCode}");
            throw new HttpRequestException("The token could not be refreshed.");
        }
        var taken = Take(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!taken.Covers)
        {
            log("google contacts refresh outcome=insufficient_scope");
            Forget();
            return null;
        }
        return taken.Access;
    }

    // Covers: the permission includes contacts. Google's consent lets a person allow the email address and decline contacts, and such a grant is never kept.
    private (string? Access, string? Refresh, string? Email, bool Covers) Take(string json)
    {
        var root = JsonNode.Parse(json);
        var access = root?["access_token"]?.GetValue<string>();
        var seconds = root?["expires_in"]?.GetValue<int>() ?? 3600;
        var covers = root?["scope"]?.GetValue<string>() is not { } granted || granted.Split(' ').Contains(Scope);
        accessToken = covers ? access : null;
        // A minute early, so a token never lapses between being read and being used.
        accessUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds - 60));
        return (access, root?["refresh_token"]?.GetValue<string>(), EmailOf(root?["id_token"]?.GetValue<string>()), covers);
    }

    // The ID token came straight from Google's token endpoint over TLS, so its claims are read, not verified; only the address is taken, to show.
    internal static string? EmailOf(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var email = JsonNode.Parse(Convert.FromBase64String(payload))?["email"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(email) ? null : email;
        }
        catch (Exception) { return null; }
    }

    private async Task<GoogleContactSave> ConnectAsync(Client client, Func<Uri, Task<bool>> openBrowser, CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = client.Id, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scopes,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
            // A refresh token every time: Google gives one only with consent, and one may have been forgotten here.
            ["access_type"] = "offline", ["prompt"] = "consent",
        }.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        if (!await openBrowser(new Uri(AuthorizeEndpoint + "?" + query)))
        {
            log("google contacts connect outcome=no_browser");
            return GoogleContactSave.Failed;
        }
        log("google contacts connect step=browser");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ConsentWait);
        Answer answer;
        var answered = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = AcceptAsync(listener, state, answered, wait.Token);
        try { answer = await answered.Task.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log("google contacts connect outcome=timed_out");
            return GoogleContactSave.Cancelled;
        }
        finally
        {
            await wait.CancelAsync();
            listener.Stop();
            await accepting;
        }
        if (answer is { Error: { } error })
        {
            log($"google contacts connect outcome=declined reason={(error == "access_denied" ? "access_denied" : "other")}");
            return GoogleContactSave.Cancelled;
        }
        var code = answer.Code!;
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.Id, ["client_secret"] = client.Secret, ["code"] = code, ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code",
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            log($"google contacts connect outcome=refused status={(int)response.StatusCode}");
            return GoogleContactSave.Failed;
        }
        var (_, refresh, email, covers) = Take(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!covers)
        {
            log("google contacts connect outcome=partial");
            return GoogleContactSave.NotConnected;
        }
        if (string.IsNullOrEmpty(refresh))
        {
            log("google contacts connect outcome=no_refresh");
            return GoogleContactSave.Failed;
        }
        await WriteKeptAsync(new Kept(refresh, email));
        account = email;
        log($"google contacts connect outcome=connected account={(email is null ? "unknown" : "known")}");
        return GoogleContactSave.Saved;
    }

    private sealed record Answer(string? Code, string? Error);

    /// <summary>How long one connection to the loopback port may take to say what it wants, and how many may be open at once.</summary>
    internal static TimeSpan RequestWait { get; set; } = TimeSpan.FromSeconds(10);
    private const int MostOpen = 8;

    // Every connection is served on its own, under its own short deadline, so one that never finishes its request (or drops) cannot hold up Google's
    // answer behind it; any program on this PC can reach the port, and only a request carrying this attempt's state is taken as the answer.
    private static async Task AcceptAsync(TcpListener listener, string state, TaskCompletionSource<Answer> answered, CancellationToken cancellationToken)
    {
        var open = 0;
        var serving = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient socket;
            try { socket = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (cancellationToken.IsCancellationRequested) break; continue; }
            if (Interlocked.Increment(ref open) > MostOpen)
            {
                Interlocked.Decrement(ref open);
                socket.Dispose();
                continue;
            }
            serving.RemoveAll(task => task.IsCompleted);
            serving.Add(ServeAsync(socket));
        }
        await Task.WhenAll(serving);

        async Task ServeAsync(TcpClient socket)
        {
            try
            {
                using (socket)
                {
                    using var each = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    each.CancelAfter(RequestWait);
                    if (await ReadAnswerAsync(socket, state, each.Token) is { } answer) answered.TrySetResult(answer);
                }
            }
            catch (Exception) { /* That connection alone: reset, too slow, or not a request. */ }
            finally { Interlocked.Decrement(ref open); }
        }
    }

    // One request on the loopback port: its first line's query, answered with a page that says to go back to Messages. Null for a request that is not Google's answer.
    private static async Task<Answer?> ReadAnswerAsync(TcpClient socket, string state, CancellationToken cancellationToken)
    {
        var stream = socket.GetStream();
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var line = head.Split("\r\n", 2)[0].Split(' ');
        var target = line.Length >= 2 ? line[1] : "";
        var values = Query(target);
        Answer? answer = values.GetValueOrDefault("state") == state
            ? new Answer(values.GetValueOrDefault("code") is { Length: > 0 } code ? code : null, values.GetValueOrDefault("error") is { Length: > 0 } error ? error : null)
            : null;
        if (answer is { Code: null, Error: null }) answer = null;
        var page = answer is null ? "" : answer.Error is null
            ? "<!doctype html><meta charset=utf-8><title>Messages</title><p style=\"font:16px Segoe UI,sans-serif;margin:40px\">Messages can now add contacts to your Google account. You can close this tab.</p>"
            : "<!doctype html><meta charset=utf-8><title>Messages</title><p style=\"font:16px Segoe UI,sans-serif;margin:40px\">Messages wasn't allowed to add contacts. You can close this tab.</p>";
        var bytes = Encoding.UTF8.GetBytes(page);
        var reply = $"HTTP/1.1 {(answer is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        catch (IOException) { }
        return answer;
    }

    internal static Dictionary<string, string> Query(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = target.IndexOf('?');
        if (at < 0) return result;
        foreach (var pair in target[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((split < 0 ? pair : pair[..split]).Replace('+', ' '));
            var value = split < 0 ? "" : Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    // What is kept: the refresh token and, when Google said it, the account's address. Protected together, as one file.
    private sealed record Kept(string Refresh, string? Email);

    private async Task<Kept?> ReadKeptAsync()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var root = JsonNode.Parse(await protector.UnprotectAsync(await File.ReadAllBytesAsync(TokenPath)));
            var refresh = root?["refresh"]?.GetValue<string>();
            return string.IsNullOrEmpty(refresh) ? null : new Kept(refresh, root?["email"]?.GetValue<string>());
        }
        catch (Exception)
        {
            // Not readable by this Windows user (a copied data folder): as if never connected.
            log("google contacts token outcome=unreadable");
            return null;
        }
    }

    private async Task WriteKeptAsync(Kept kept)
    {
        Directory.CreateDirectory(folder);
        var json = new JsonObject { ["refresh"] = kept.Refresh, ["email"] = kept.Email }.ToJsonString();
        var sealedBytes = await protector.ProtectAsync(Encoding.UTF8.GetBytes(json));
        var temporary = TokenPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, sealedBytes);
        File.Move(temporary, TokenPath, overwrite: true);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }
}
