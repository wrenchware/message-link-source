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

    private static IEnumerable<Route> HermesRoutes =>
    [
        new("/hermes", Verbs.Both, (page, call) => page.HermesAsync(call.Context, call.Form, call.Token)) { Body = MaxHermesForm, Present = page => page.Hermes is not null },
    ];

    // Reports and adding an assistant need both of Hermes's parts.
    private static bool HasHermesReports(ServerPage page) => page.Hermes is not null && page.Reports is not null;

    // Hermes Agent's own app icon, the sixth picture in the page's sprite (THIRD_PARTY_NOTICES.md).
    private const string HermesMark = "<span class=\"ic hm\" aria-hidden=\"true\"></span>";

    private const string HermesStyle =
        // The form: titled cards as the status pages have. Where Hermes runs, with how to set it up by hand folded under it.
        "form.hermes>.sec{margin:0 0 24px}form.hermes>.lead{color:var(--soft);margin:0 2px 20px}.hf{padding:14px 16px}.hf input{max-width:420px;margin:0}.hf .hint{margin:6px 0 0}"
        + "details.how{margin:0;padding:0 16px}details.how>summary{display:flex;align-items:center;gap:10px;min-height:44px;color:var(--soft);font-size:13px;list-style:none}details.how>summary::-webkit-details-marker{display:none}"
        + "details.how>summary:hover{color:var(--fg)}details.how>summary .chev{transform:rotate(45deg);margin-left:auto}details.how[open]>summary .chev{transform:rotate(135deg)}details.how ol.steps{margin:0 0 14px}"
        // The profiles: one line each under the columns' names, the name of a saved one as text (its key is kept under it), a blank line to add one.
        + ".hgrid{display:grid;grid-template-columns:150px minmax(0,1fr) minmax(0,1fr) 84px;gap:10px;align-items:center;padding:10px 16px}.hgrid.hhead{padding:9px 16px;font-size:12.5px;font-weight:500;color:var(--muted)}"
        + ".hgrid input{max-width:none;margin:0}.hgrid .pn{font-weight:600;overflow-wrap:anywhere}.vh{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;margin:0}"
        // Remove: a checkbox drawn as a quiet button; ticked, it reads as pressed and the line fades until Save.
        + "label.rm{justify-self:end;margin:0;padding:3px 10px;font-size:12.5px;font-weight:500;line-height:20px;border:1px solid var(--fieldline);border-radius:7px;background:var(--field);color:var(--soft);cursor:pointer;white-space:nowrap}"
        + "label.rm:hover{background:var(--fieldhover);color:var(--fg)}label.rm:has(:focus-visible){outline:2px solid var(--acc);outline-offset:2px}label.rm:has(:checked){color:var(--danger-ink);border-color:var(--danger-ink)}"
        + ".hgrid:has(.rm :checked)>:not(.rm){opacity:.4}.hgrid:has(.rm :checked) .pn{text-decoration:line-through}form.hermes{margin-top:0}form.hermes>input[type=password]{margin-bottom:18px}"
        + "@media(max-width:640px){.hgrid{grid-template-columns:minmax(0,1fr) auto;gap:8px 10px;padding:12px 16px}.hgrid.hhead{display:none}.hgrid>.f{grid-column:1/-1}.hgrid>.pn{grid-row:1;grid-column:1}.hgrid>.rm{grid-row:1;grid-column:2}"
        + ".hgrid label.vh{position:static;width:auto;height:auto;overflow:visible;clip:auto;margin:0 0 4px;font-size:13px}}"
        // A profile's Reports line, set in under the profile's name past its picture; the settings apart from the profiles; a menu's item that posts a form.
        + ".row.sub{padding-left:54px;min-height:44px}.row.sub .g{font-weight:500}.box.settings{margin-top:20px}"
        + ".pop form{margin:0}button.item{display:block;width:100%;padding:7px 10px;border:0;border-radius:6px;background:none;color:var(--fg);font:inherit;font-size:13px;font-weight:400;line-height:inherit;text-align:left;white-space:nowrap}"
        + "button.item:hover{background:var(--fieldhover);border:0}button.item.danger{color:var(--danger-ink)}";

    // A profile's state in the page's words.
    private static string HermesWord(string status) => status switch
    {
        "connected" => "Ready",
        "unreachable" => "Can't reach Hermes",
        "key_rejected" => "Wrong API key",
        "no_profile" => "Not found in Hermes",
        _ => "Checking",
    };

    private static bool HermesWarns(string status) => status is "unreachable" or "key_rejected" or "no_profile";

    // What an assistant is called on the page, as its thread is titled (the name given here, else the profile's own). The profile's name goes under it only
    // when it was renamed, since the default name is the profile's.
    private static (string Name, string? Under) HermesNames(HermesAgentState agent) =>
        HermesGivenTitle(agent) is { Length: > 0 } given ? (given, "Hermes profile " + agent.Name) : (NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(agent.Name), null);

    // The name given here, or blank when the thread goes by the default.
    private static string HermesGivenTitle(HermesAgentState agent) =>
        agent.Title is { Length: > 0 } title && title != NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(agent.Name) ? title : "";

    // The line in Accounts: how many profiles, and how the worst of them stands.
    private void HermesLine(StringBuilder body)
    {
        if (Hermes?.State is not { } state) return;
        if (state.Agents.Count == 0)
        {
            body.Append(Go("Hermes", "Chat with your Hermes assistants.", "Not set up", "/manage/hermes", "Hermes", HermesMark, id: "row-" + HermesSection));
            return;
        }
        var worst = state.Agents.Select(agent => agent.Status).OrderBy(status => status switch { "key_rejected" => 0, "no_profile" => 1, "unreachable" => 2, "unchecked" => 3, _ => 4 }).First();
        body.Append(Go("Hermes", state.Agents.Count == 1 ? "1 assistant" : $"{state.Agents.Count} assistants", HermesWord(worst), "/manage/hermes", "Hermes", HermesMark,
            warn: HermesWarns(worst), id: "row-" + HermesSection));
    }

    // What needs the owner: a key Hermes refused or a profile it doesn't have stays so until it is changed here. Hermes not answering is left to its own line.
    private void HermesAttention(StringBuilder rows)
    {
        if (Hermes?.State is not { } state) return;
        var problem = state.Agents.Any(agent => agent.Status == "key_rejected") ? "Hermes rejected an API key"
            : state.Agents.Any(agent => agent.Status == "no_profile") ? "An assistant isn't found in Hermes" : null;
        if (problem is not null) rows.Append(Row(problem, null, null, "<a class=\"button\" href=\"/manage/hermes\">Review</a>", HermesMark));
    }

    // /manage/hermes: each assistant's state, and the way to change them. Whether a profile is still being checked, so the page looks again soon.
    private bool HermesCard(StringBuilder body, string token)
    {
        var state = Hermes!.State;
        body.Append("<section id=\"hermes\"><h2>Assistants</h2>");
        if (state.Agents.Count == 0)
        {
            body.Append("<div class=\"box\">").Append(Row("Set up Hermes", "Chat with your Hermes assistants in Message Link. Each assistant gets its own conversation.", "Not set up",
                Reports is null ? "<a class=\"button\" href=\"/hermes\">Set up…</a>" : "<a class=\"button\" href=\"" + AddPath + "\">Add an assistant…</a>")).Append("</div>")
                .Append(Reports is null ? "" : "<p><a href=\"/hermes\">Set up by hand instead</a></p>").Append("</section>");
            return false;
        }
        // A box for each assistant, its "can message you first" line set in under it, so with several each one's lines read as its own; then one row to
        // change them. The address is only in the form (owner, 2026-11-16: the plumbing stays out of the way).
        foreach (var agent in state.Agents)
        {
            var (name, under) = HermesNames(agent);
            body.Append("<div class=\"box\">").Append(Row(name, under, HermesWord(agent.Status), null, HermesMark, warn: HermesWarns(agent.Status)));
            ReportsRow(body, agent, token);
            body.Append("</div>");
        }
        body.Append("<div class=\"box settings\">")
            .Append(Reports is null ? "" : Row("Add an assistant", "Chat with another Hermes profile. You run one command where Hermes runs.", null, "<a class=\"button quiet\" href=\"" + AddPath + "\">Add…</a>"))
            .Append(Row("Change assistants", "Rename or remove an assistant, change an API key or where Hermes runs, or add one by hand.", null,
                "<a class=\"button quiet\" href=\"/hermes\">Change…</a>"))
            .Append("</div></section>");
        return state.Agents.Any(agent => agent.Status == "unchecked");
    }

    // One profile row of the form as typed or saved: its name, whether a key is saved for it (shown only as a placeholder), and the conversation's name given here.
    private sealed record HermesRow(string Name, bool Saved, string Title = "");

    private string HermesForm(string token, string? problem, string? address, IReadOnlyList<HermesRow> rows)
    {
        var body = new StringBuilder();
        if (problem is not null) body.Append(Notice(problem));
        body.Append("<form class=\"hermes\" method=\"post\" action=\"/hermes\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
            .Append("<p class=\"lead\">Chat with your Hermes assistants in Message Link. Each assistant gets its own conversation.</p>")
            .Append("<section class=\"sec\"><div class=\"sh\"><h2>Where Hermes runs</h2></div><div class=\"box\"><div class=\"hf\">")
            .Append("<label for=\"address\">Address</label><input id=\"address\" name=\"address\" type=\"text\" value=\"").Append(Html(address))
            .Append("\" placeholder=\"http://192.168.1.20:8642\" autocomplete=\"off\" spellcheck=\"false\"><p class=\"hint\">Hermes listens on port 8642 by default.</p></div>")
            // How to make Hermes reachable by hand: folded, as Add an assistant's command does it (ServerPage.HermesAdd.cs).
            .Append("<details class=\"how\"><summary>Set up Hermes yourself<span class=\"chev\" aria-hidden=\"true\"></span></summary>")
            .Append("<ol class=\"steps\"><li>On the machine that runs Hermes, set <span class=\"copy\">API_SERVER_HOST=0.0.0.0</span> so its API server can be reached from your home network.</li>")
            .Append("<li>Give each profile its own key with <span class=\"copy\">API_SERVER_KEY</span>, then start Hermes again.</li></ol></details></div></section>")
            .Append("<section class=\"sec\"><div class=\"sh\"><h2>Assistants</h2></div><div class=\"box\">")
            .Append("<div class=\"hgrid hhead\" aria-hidden=\"true\"><span>Hermes profile</span><span>Name</span><span>API key</span><span></span></div>");
        var index = 0;
        foreach (var row in rows)
        {
            Profile(index++, row.Name, row.Saved, row.Title, existing: true);
        }
        // A blank row to add one; with nothing set up yet, the first is Hermes's own default profile.
        Profile(index, rows.Count == 0 ? NativeBeeper.App.Services.Hermes.HermesClient.DefaultProfile : "", false, "", existing: false);
        body.Append("</div><p class=\"hint\">The Hermes profile named default is Hermes's own.")
            .Append(rows.Any(row => row.Saved) ? " Leave a saved API key blank to keep it." : "").Append("</p></section>")
            .Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\">Save</button><a class=\"cancel\" href=\"/manage/hermes\">Cancel</a></form>");
        return body.ToString();

        // One line of the table. A saved profile's name is its identity (its key is kept under it), so it shows as text; a typed one that isn't saved stays a field.
        // On a narrow screen the line stacks and each field's label shows.
        void Profile(int i, string name, bool saved, string title, bool existing)
        {
            body.Append("<div class=\"hgrid\">");
            if (saved)
                body.Append("<div class=\"pn\">").Append(Html(name)).Append("</div><input type=\"hidden\" name=\"name-").Append(i).Append("\" value=\"").Append(Html(name)).Append("\">");
            else
                body.Append("<div class=\"f\"><label class=\"vh\" for=\"name-").Append(i).Append("\">Hermes profile</label><input id=\"name-").Append(i).Append("\" name=\"name-").Append(i)
                    .Append("\" type=\"text\" value=\"").Append(Html(name)).Append('"').Append(existing ? "" : " placeholder=\"Add an assistant\"").Append(" autocomplete=\"off\" spellcheck=\"false\"></div>");
            // The assistant's name, optional: a profile name is lowercase with no spaces, so the conversation can go by another. Blank keeps the default, shown as the placeholder.
            body.Append("<div class=\"f\"><label class=\"vh\" for=\"title-").Append(i).Append("\">Name</label><input id=\"title-").Append(i).Append("\" name=\"title-").Append(i)
                .Append("\" type=\"text\" value=\"").Append(Html(title)).Append("\" maxlength=\"").Append(NativeBeeper.App.Services.Hermes.HermesBackend.MaxTitleLength)
                .Append("\" autocomplete=\"off\"");
            var profile = name.Trim().ToLowerInvariant();
            if (NativeBeeper.App.Services.Hermes.HermesClient.ValidProfile(profile))
                body.Append(" placeholder=\"").Append(Html(NativeBeeper.App.Services.Hermes.HermesBackend.DefaultTitle(profile))).Append('"');
            body.Append("></div>")
                .Append("<div class=\"f\"><label class=\"vh\" for=\"key-").Append(i).Append("\">API key</label><input id=\"key-").Append(i).Append("\" name=\"key-").Append(i)
                // new-password: a browser must not offer the page's own password here.
                .Append("\" type=\"password\" autocomplete=\"new-password\"").Append(saved ? " placeholder=\"Saved\"" : "").Append("></div>");
            if (existing) body.Append("<label class=\"rm\"><input class=\"vh\" type=\"checkbox\" name=\"remove-").Append(i).Append("\" value=\"yes\">Remove</label>");
            body.Append("</div>");
        }
    }

    private async Task HermesAsync(HttpContext context, IFormCollection? form, string token)
    {
        var hermes = Hermes!;
        var state = hermes.State;
        // As the card's own line.
        const string title = "Change assistants";
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
        // Every name the form had a line for, removed ones too: an assistant added in one command while the form was open had none, and is kept.
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < MaxHermesRows && form.ContainsKey("name-" + i); i++)
        {
            listed.Add(form["name-" + i].ToString().Trim().ToLowerInvariant());
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
        foreach (var agent in hermes.State.Agents)
            if (!listed.Contains(agent.Name)) profiles.Add(new(agent.Name, null));
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
