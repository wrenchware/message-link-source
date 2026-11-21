using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NativeBeeper.App.Services.Hermes;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

// Add an assistant in one command (docs/research/hermes-reports-plan.md, "Add an assistant in one command"; PROVIDERS.md, Hermes): the page asks for a
// Hermes profile's name and the password, then shows one command to run where Hermes runs, as reports' setup does (ServerPage.ReportSetup.cs). Its installer
// gives the profile an API key when it has none (a key it has is kept, owner 2026-11-16, so other clients of the profile keep working), checks it with Hermes,
// and sends it with the one-time code over the connection pinned to this server; the server adds the profile once Hermes takes the key at an address the
// server reaches, then reports are turned on in the same run. The key never shows on a page or in a command line. Typing the address and key stays, as
// "Set up by hand instead" (/hermes).
internal sealed partial class ServerPage
{
    internal const string AddPath = "/hermes/add", AddWatchPath = "/hermes/add/setup";

    private static IEnumerable<Route> AddRoutes => [.. new[] { AddPath, AddWatchPath }.Select(path =>
        new Route(path, Verbs.Both, (page, call) => page.AddAssistantAsync(call.Context, call.Path, call.Form, call.Token)) { Body = MaxHermesForm, Present = HasHermesReports })];

    private async Task AddAssistantAsync(HttpContext context, string path, IFormCollection? form, string token)
    {
        var reports = Reports!;
        if (path == AddWatchPath)
        {
            await AddWatchAsync(context).ConfigureAwait(false);
            return;
        }
        const string title = "Add an assistant";
        var state = Hermes!.State;
        // With none set up yet, Hermes's own default profile is the likely first.
        var profile = form is null ? state.Agents.Count == 0 ? HermesClient.DefaultProfile : "" : form["profile"].ToString().Trim().ToLowerInvariant();
        var given = form is null ? "" : form["title"].ToString().Trim();
        string Ask(string? problem) => AddForm(token, problem, profile, given);
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!HermesClient.ValidProfile(profile))
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title,
                Ask(profile.Length == 0 ? "Enter the name of a Hermes profile." : "A profile name uses lowercase letters, numbers, hyphens and underscores, as in Hermes.")).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "hermes_add", title, Ask).ConfigureAwait(false)) return;
        AgentSetupIssue issued;
        try { issued = reports.IssueAddCode(profile, given); }
        catch (ArgumentException refused)
        {
            // The backend's own sentences, written for a person.
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask(refused.Message)).ConfigureAwait(false);
            return;
        }
        // Hermes reaches this server from where any other assistant's reports do, when one has them on: they all run in the one Hermes.
        var kept = reports.ReportsState.Select(r => r.Address).FirstOrDefault(a => a is { Length: > 0 });
        var command = DesktopTrust ? WindowsCommand(issued.Code, profile, add: true) : ShellCommand(issued.Code, profile, kept, context.Request, add: true);
        log("server page hermes outcome=add_shown");
        context.Response.Headers.CacheControl = "no-store";
        var name = given.Length > 0 ? given : HermesBackend.DefaultTitle(profile);
        await PageAsync(context, StatusCodes.Status200OK, title, AddBody(command, profile, name, issued.Id)).ConfigureAwait(false);
    }

    private static string AddForm(string token, string? problem, string profile, string given)
    {
        var fields = new StringBuilder()
            .Append("<label for=\"profile\">Hermes profile</label><input id=\"profile\" name=\"profile\" type=\"text\" value=\"").Append(Html(profile))
            .Append("\" autocomplete=\"off\" spellcheck=\"false\" required><p class=\"hint\">The Hermes profile named default is Hermes's own.</p>")
            .Append("<label for=\"title\">Name (optional)</label><input id=\"title\" name=\"title\" type=\"text\" value=\"").Append(Html(given)).Append("\" maxlength=\"")
            .Append(HermesBackend.MaxTitleLength).Append("\" autocomplete=\"off\"><p class=\"hint\">What its conversation is called. Leave it blank to use the profile's name.</p>");
        return AskBody(AddPath, token, "Chat with a Hermes profile in Message Link. You run one command where Hermes runs, and the assistant gets its own conversation.",
            "Show the command", problem, before: fields.ToString())
            .Replace("</section>", "<p><a href=\"/hermes\">Set up by hand instead</a></p></section>", StringComparison.Ordinal);
    }

    private string AddBody(string command, string profile, string name, string id)
    {
        var where = DesktopTrust
            ? "Run this in PowerShell on this PC, where Hermes runs."
            : "Run this on the computer that runs Hermes. If Hermes runs in Docker, run it there, not inside the container.";
        return new StringBuilder("<section>").Append(Para(where))
            .Append("<div id=\"setup-command\"><p class=\"copy block line\">").Append(Html(command)).Append("</p>")
            .Append("<p><button type=\"button\" class=\"quiet small\" data-copy-code=\"").Append(Html(command)).Append("\">Copy</button></p>")
            .Append(Para("It works once, within " + (int)AgentReportRules.SetupLifetime.TotalMinutes + " minutes. It gives the profile an API key if it doesn't have one, connects "
                + name + " to this server, and lets " + name + " message you first. Hermes restarts at the end."))
            .Append("</div>")
            .Append("<div class=\"box\" id=\"setup\" data-watch=\"").Append(Html(AddWatchPath + "?profile=" + Uri.EscapeDataString(profile) + "&id=" + Uri.EscapeDataString(id))).Append("\">")
            .Append(Row("Waiting for the command", "This page shows the result when the command is done.", null, null, id: "setup-state"))
            .Append("</div>")
            .Append("<p><a href=\"/hermes\">Set up by hand instead</a></p>")
            .Append("<p><a href=\"/manage/hermes\">Done</a></p></section>")
            .Append("<script>").Append(SetupScript).Append("</script>").ToString();
    }

    // An add code's state as JSON for SetupScript: never the code or the key. Done carries its own words, since the assistant is new.
    private async Task AddWatchAsync(HttpContext context)
    {
        var profile = context.Request.Query["profile"].ToString();
        var id = context.Request.Query["id"].ToString();
        var response = context.Response;
        if (!HermesClient.ValidProfile(profile) || Reports!.SetupState(profile, id) is not { } state)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var name = Hermes!.State.Agents.FirstOrDefault(a => a.Name == profile) is { } agent ? HermesNames(agent).Name : HermesBackend.DefaultTitle(profile);
        object answer = state.Phase switch
        {
            AgentSetupPhase.Waiting => new { state = "waiting", problem = AddProblem(state) },
            AgentSetupPhase.Done => new
            {
                state = "done", title = "Added",
                under = name + " is ready in Message Link and can message you first."
                    + (HermesClient.ParseBase(state.Hermes) is { } hermes ? " This server reaches Hermes at " + hermes.Authority + "." : ""),
            },
            AgentSetupPhase.Replaced => new { state = "replaced" },
            _ => new { state = "expired" },
        };
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(JsonSerializer.Serialize(answer), Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
    }

    // Why the command's last run didn't add the profile, when it ran; the command itself says what to change.
    private static string? AddProblem(AgentSetupState state)
    {
        var tried = state.Tried is { Count: > 0 } list ? string.Join(", ", list) : "its address";
        return state.Problem switch
        {
            "unreachable" => "The command ran, but this server couldn't reach Hermes at " + tried + ". Do what the command says, then run it again.",
            "key_rejected" => "The command ran, but Hermes didn't accept the profile's API key yet. Do what the command says, then run it again.",
            "no_profile" => "The command ran, but Hermes at " + tried + " doesn't serve this profile yet. Do what the command says, then run it again.",
            "saved_unreachable" => "The command ran, but this server couldn't reach Hermes at " + tried + ", where it reaches your other assistants. If Hermes moved, change where it runs with Change assistants, then run the command again.",
            "error" => "The command ran, but Hermes at " + tried + " answered with an error. Run the command again in a minute.",
            _ => null,
        };
    }
}
