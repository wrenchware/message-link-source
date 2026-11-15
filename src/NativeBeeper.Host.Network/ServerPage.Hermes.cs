using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

// Hermes Agent on the server's page (protocol 31): where its address and each profile's key are set, as every account is set up here rather than from a client
// (HostHermesAccount refuses a server's clients). Its line sits after Instagram in Accounts and opens /manage/hermes, which says how each profile stands; the
// form is a page of its own (/hermes) so the status page can look again by itself without wiping what is being typed. A key is never written back into a page,
// a log line or an error: a saved one shows only as a placeholder, and a blank key keeps it.
internal sealed partial class ServerPage
{
    /// <summary>Hermes as this page sets it up (<see cref="NetworkHostOptions.Hermes"/>); null shows none.</summary>
    internal IHermesAccount? Hermes { get; set; }

    private const string HermesSection = "hermes";

    // The form's address, up to twenty profiles with their keys (512 characters each at most) and the password, encoded: well within this.
    private const int MaxHermesForm = 32 * 1024;

    // More rows than a form can usefully hold are not read.
    private const int MaxHermesRows = NativeBeeper.App.Services.Hermes.HermesBackend.MaxProfiles + 2;

    private static bool IsHermesPath(string? path) => path is "/hermes";

    // Hermes Agent's own app icon, the sixth picture in the page's sprite (THIRD_PARTY_NOTICES.md).
    private const string HermesMark = "<span class=\"ic hm\" aria-hidden=\"true\"></span>";

    private const string HermesStyle = "fieldset.hp{border:1px solid var(--line);border-radius:12px;padding:12px 16px 2px;margin:0 0 14px}"
        + "fieldset.hp legend{padding:0 6px;font-weight:600}label.check{display:flex;align-items:center;gap:8px;font-weight:400;margin:0 0 12px}label.check input{margin:0}";

    // A profile's state in the page's words.
    private static string HermesWord(string status) => status switch
    {
        "connected" => "Connected",
        "unreachable" => "Can't reach Hermes",
        "key_rejected" => "Key rejected",
        "no_profile" => "No such profile",
        _ => "Checking",
    };

    private static bool HermesWarns(string status) => status is "unreachable" or "key_rejected" or "no_profile";

    // What a profile is called on the page, as its thread is titled (the name given here, else Hermes Agent's own), with the profile's name under it.
    private static (string Name, string? Under) HermesNames(HermesAgentState agent) =>
        (agent.Title is { Length: > 0 } title ? title : NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(agent.Name), "Profile " + agent.Name);

    // The name given here, or blank when the thread goes by the default.
    private static string HermesGivenTitle(HermesAgentState agent) =>
        agent.Title is { Length: > 0 } title && title != NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(agent.Name) ? title : "";

    // The line in Accounts: how many profiles, and how the worst of them stands.
    private void HermesLine(StringBuilder body)
    {
        if (Hermes?.State is not { } state) return;
        if (state.Agents.Count == 0)
        {
            body.Append(Go("Hermes", "Chat with your Hermes agent.", "Not set up", "/manage/hermes", "Hermes", HermesMark, id: "row-" + HermesSection));
            return;
        }
        var worst = state.Agents.Select(agent => agent.Status).OrderBy(status => status switch { "key_rejected" => 0, "no_profile" => 1, "unreachable" => 2, "unchecked" => 3, _ => 4 }).First();
        body.Append(Go("Hermes", state.Agents.Count == 1 ? "1 profile" : $"{state.Agents.Count} profiles", HermesWord(worst), "/manage/hermes", "Hermes", HermesMark,
            warn: HermesWarns(worst), id: "row-" + HermesSection));
    }

    // What needs the owner: a key Hermes refused or a profile it doesn't have stays so until it is changed here. Hermes not answering is left to its own line.
    private void HermesAttention(StringBuilder rows)
    {
        if (Hermes?.State is not { } state) return;
        var problem = state.Agents.Any(agent => agent.Status == "key_rejected") ? "Hermes rejected an API key"
            : state.Agents.Any(agent => agent.Status == "no_profile") ? "Hermes doesn't have a profile added here" : null;
        if (problem is not null) rows.Append(Row(problem, null, null, "<a class=\"button\" href=\"/manage/hermes\">Review</a>", HermesMark));
    }

    // /manage/hermes: the address and each profile's state, and the way to change them. Whether a profile is still being checked, so the page looks again soon.
    private bool HermesCard(StringBuilder body)
    {
        var state = Hermes!.State;
        body.Append("<section id=\"hermes\"><h2>Hermes</h2>");
        if (state.Agents.Count == 0)
        {
            body.Append("<div class=\"box\">").Append(Row("Set up Hermes", "Each Hermes profile becomes a conversation. Hermes gets only what you type or send in it.", "Not set up",
                "<a class=\"button\" href=\"/hermes\">Set up…</a>")).Append("</div></section>");
            return false;
        }
        body.Append("<div class=\"box\">");
        if (state.Address is { Length: > 0 } address) body.Append(CopyRow("Address", address));
        foreach (var agent in state.Agents)
        {
            var (name, under) = HermesNames(agent);
            body.Append(Row(name, under, HermesWord(agent.Status), null, warn: HermesWarns(agent.Status)));
        }
        body.Append("</div><div class=\"box\">").Append(Row("Change Hermes", "The address, the profiles, their API keys and conversation names.", null, "<a class=\"button quiet\" href=\"/hermes\">Change…</a>"))
            .Append("</div></section>");
        return state.Agents.Any(agent => agent.Status == "unchecked");
    }

    // One profile row of the form as typed or saved: its name, whether a key is saved for it (shown only as a placeholder), and the conversation's name given here.
    private sealed record HermesRow(string Name, bool Saved, string Title = "");

    private string HermesForm(string token, string? problem, string? address, IReadOnlyList<HermesRow> rows)
    {
        var body = new StringBuilder("<section>");
        if (problem is not null) body.Append(Notice(problem));
        body.Append(Para("Message Link Server reaches Hermes Agent through Hermes's own API server. Each Hermes profile becomes a conversation, and Hermes gets only what you type or send in it."))
            .Append("<ol class=\"steps\"><li>On the machine that runs Hermes, set <span class=\"copy\">API_SERVER_HOST=0.0.0.0</span> so its API server can be reached from your home network. It listens on port 8642 by default.</li>")
            .Append("<li>Give each profile its own key with <span class=\"copy\">API_SERVER_KEY</span>, then start Hermes again.</li>")
            .Append("<li>Enter the address once, such as http://192.168.1.20:8642, then add each profile by name. The profile named default is Hermes's own default profile.</li></ol>")
            .Append("<form method=\"post\" action=\"/hermes\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append("<label for=\"address\">Address</label><input id=\"address\" name=\"address\" type=\"text\" value=\"").Append(Html(address))
            .Append("\" placeholder=\"http://192.168.1.20:8642\" autocomplete=\"off\" spellcheck=\"false\">");
        var index = 0;
        foreach (var row in rows)
        {
            Profile(index++, row.Name, row.Saved, row.Title, existing: true);
        }
        // A blank row to add one; with nothing set up yet, the first is Hermes's own default profile.
        Profile(index, rows.Count == 0 ? NativeBeeper.App.Services.Hermes.HermesClient.DefaultProfile : "", false, "", existing: false);
        body.Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\">Save</button><a class=\"cancel\" href=\"/manage/hermes\">Cancel</a></form>");
        return body.Append("</section>").ToString();

        void Profile(int i, string name, bool saved, string title, bool existing)
        {
            body.Append("<fieldset class=\"hp\"><legend>").Append(existing ? "Profile" : "Add a profile").Append("</legend>")
                .Append("<label for=\"name-").Append(i).Append("\">Profile name</label><input id=\"name-").Append(i).Append("\" name=\"name-").Append(i)
                .Append("\" type=\"text\" value=\"").Append(Html(name)).Append("\" autocomplete=\"off\" spellcheck=\"false\">")
                .Append("<label for=\"key-").Append(i).Append("\">API key</label><input id=\"key-").Append(i).Append("\" name=\"key-").Append(i)
                // new-password: a browser must not offer the page's own password here.
                .Append("\" type=\"password\" autocomplete=\"new-password\"").Append(saved ? " placeholder=\"Saved. Leave blank to keep it.\"" : "").Append('>')
                // Optional: a profile name is lowercase with no spaces, so the conversation can go by another. Blank keeps Hermes Agent's own name, shown as the placeholder.
                .Append("<label for=\"title-").Append(i).Append("\">Conversation name (optional)</label><input id=\"title-").Append(i).Append("\" name=\"title-").Append(i)
                .Append("\" type=\"text\" value=\"").Append(Html(title)).Append("\" maxlength=\"").Append(NativeBeeper.App.Services.Hermes.HermesBackend.MaxTitleLength)
                .Append("\" autocomplete=\"off\"");
            var profile = name.Trim().ToLowerInvariant();
            if (NativeBeeper.App.Services.Hermes.HermesClient.ValidProfile(profile))
                body.Append(" placeholder=\"").Append(Html(NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(profile))).Append('"');
            body.Append('>');
            if (existing) body.Append("<label class=\"check\"><input type=\"checkbox\" name=\"remove-").Append(i).Append("\" value=\"yes\">Remove this profile</label>");
            body.Append("</fieldset>");
        }
    }

    private async Task HermesAsync(HttpContext context, IFormCollection? form, string token)
    {
        const string title = "Set up Hermes";
        var hermes = Hermes!;
        var state = hermes.State;
        bool Saved(string name) => state.Agents.Any(agent => agent.Name == name && agent.HasKey);
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title,
                HermesForm(token, null, state.Address, [.. state.Agents.Select(agent => new HermesRow(agent.Name, agent.HasKey, HermesGivenTitle(agent)))])).ConfigureAwait(false);
            return;
        }
        // What was typed, keys aside, so a refused form comes back as it was sent.
        var address = form["address"].ToString().Trim();
        var profiles = new List<HermesProfileInput>();
        var nameless = false;
        for (var i = 0; i < MaxHermesRows && form.ContainsKey("name-" + i); i++)
        {
            if (form["remove-" + i] == "yes") continue;
            var name = form["name-" + i].ToString().Trim();
            var key = form["key-" + i].ToString();
            var given = form["title-" + i].ToString().Trim();
            if (name.Length == 0)
            {
                nameless |= key.Trim().Length > 0;
                continue;
            }
            // The field always sends its name, blank included, so clearing it goes back to the default rather than keeping the saved one.
            profiles.Add(new(name, string.IsNullOrWhiteSpace(key) ? null : key, given));
        }
        string Ask(string? problem) => HermesForm(token, problem, address, [.. profiles.Select(profile => new HermesRow(profile.Name, Saved(profile.Name.ToLowerInvariant()), profile.Title ?? ""))]);
        if (nameless)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("Enter a profile name for each API key.")).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "hermes", title, Ask).ConfigureAwait(false)) return;
        try { await hermes.ConfigureAsync(address.Length == 0 ? null : address, profiles, context.RequestAborted).ConfigureAwait(false); }
        catch (ArgumentException refused)
        {
            log($"server page hermes outcome=refused profiles={profiles.Count}");
            // The backend's own sentences, written for a person and never naming a key.
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask(refused.Message)).ConfigureAwait(false);
            return;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            log($"server page hermes outcome=failed type={failure.GetType().Name}");
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't save Hermes's settings. Try again.")).ConfigureAwait(false);
            return;
        }
        log($"server page hermes outcome=saved profiles={profiles.Count}");
        Redirect(context.Response, "/manage/hermes?done=hermes_saved");
    }
}
