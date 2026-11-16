using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

// Reports on the server's page (IAgentReports): each Hermes profile's line says whether Hermes may post to its conversation, sets it up, makes a new code or
// turns it off. Set up and New code ask for the password again and show one command to run where Hermes runs (setup in one command, ServerPage.ReportSetup.cs),
// whose installer makes the profile's code there; the page then shows the result as it comes. Setting up by hand shows a connection code once, on the answer to
// its form and no later page. The server keeps only hashes. The scripts Hermes uses to post, and the Hermes plugin, are offered from here too, each as one download.
internal sealed partial class ServerPage
{
    /// <summary>The report tokens this page turns on and off (<see cref="NetworkHostOptions.Reports"/>); null shows no Reports line.</summary>
    internal IAgentReports? Reports { get; set; }

    internal const string ReportScriptPath = "/hermes/ml-report.zip";
    internal const string PluginPath = "/hermes/plugin.zip";
    internal const string ReportCodePath = "/hermes/reports/code";

    /// <summary>The port the server's door listens on, once it does (the Windows server's report code names it).</summary>
    internal Func<int>? ListenPort { get; set; }

    private static bool IsReportsPath(string? path) => path is "/hermes/reports" or "/hermes/reports/off" or ReportCodePath or SetupWatchPath or ReportScriptPath or PluginPath;

    // The files of integrations/hermes-messagelink, embedded under "hermes-messagelink/" (NativeBeeper.Host.Network.csproj), in the ml-report download.
    internal static readonly string[] ReportScriptFiles = ["ml-report", "mlreport_client.py", "SKILL.md", "README.md", "LICENSE"];

    // The Hermes plugin's files from the same folder. Hermes loads a plugin from a folder of its own under the profile's plugins folder, named as the plugin
    // (plugins/messagelink/plugin.yaml, imported as a package through __init__.py), so the zip holds them in messagelink/ and unzips in place there.
    internal static readonly string[] PluginFiles = ["plugin.yaml", "__init__.py", "adapter.py", "mlreport_client.py", "README.md", "LICENSE"];
    internal const string PluginFolder = "messagelink/";

    private const string ReportsIntro = "Hermes uses this code to send messages to this conversation. Anyone with it can do the same, so keep it in Hermes only.";

    // The profile's Reports line, set in under its own line in the profile's box (HermesCard): New code and Turn off behind its menu, as a device's are.
    private void ReportsRow(StringBuilder body, HermesAgentState agent, string token)
    {
        if (Reports?.ReportsState.FirstOrDefault(r => r.Profile == agent.Name) is not { } state) return;
        var ask = "/hermes/reports?profile=" + Uri.EscapeDataString(agent.Name);
        if (!state.On)
        {
            body.Append(Row("Reports", "Let Hermes post to this conversation when a job is done or something needs you.", "Off",
                "<a class=\"button quiet small\" href=\"" + Html(ask) + "\">Set up…</a>", kind: "sub"));
            return;
        }
        var under = state.LastRefusal == "address"
            ? (state.LastRefusalAt is { } at ? "Last report refused " + Ago(at) + "." : "Last report refused.") + " It didn't come from the home network."
            : state.LastReportAt is { } last && last >= (state.Since ?? DateTimeOffset.MinValue) ? "Last report " + Ago(last) + "."
            : state.SetupFrom is { Length: > 0 } from && state.Since is { } since ? "Set up " + Ago(since) + " from " + from + ". No reports yet."
            : "No reports since this code was made.";
        var name = HermesNames(agent).Name;
        body.Append(Row("Reports", under, "On", Act(Menu("Manage reports for " + name, false, [Item(Html(ask), "New code…", "New report code for " + name), TurnOffForm(token, agent.Name, name, "item danger")])),
            warn: state.LastRefusal is not null, kind: "sub"));
    }

    private static string TurnOffForm(string token, string profile, string name, string style, string id = "") =>
        "<form method=\"post\" action=\"/hermes/reports/off\"" + (id.Length > 0 ? " id=\"" + id + "\" hidden" : "") + "><input type=\"hidden\" name=\"form\" value=\"" + Html(token)
        + "\"><input type=\"hidden\" name=\"profile\" value=\"" + Html(profile) + "\"><button type=\"submit\" class=\"" + style + "\" aria-label=\"" + Html("Turn off reports for " + name)
        + "\">Turn off</button></form>";

    private async Task ReportsAsync(HttpContext context, string path, IFormCollection? form, string token)
    {
        var reports = Reports!;
        if (path == ReportScriptPath)
        {
            await DownloadAsync(context, ReportScriptZip(), "ml-report.zip").ConfigureAwait(false);
            return;
        }
        if (path == PluginPath)
        {
            await DownloadAsync(context, PluginZip(), "messagelink-plugin.zip").ConfigureAwait(false);
            return;
        }
        var profile = (form is null ? context.Request.Query["profile"] : form["profile"]).ToString();
        if (reports.ReportsState.FirstOrDefault(r => r.Profile == profile) is not { } state || Hermes!.State.Agents.FirstOrDefault(a => a.Name == profile) is not { } agent)
        {
            if (path == SetupWatchPath) context.Response.StatusCode = StatusCodes.Status404NotFound;
            else Redirect(context.Response, "/manage/hermes");
            return;
        }
        if (path == SetupWatchPath)
        {
            await SetupWatchAsync(context, profile).ConfigureAwait(false);
            return;
        }
        if (path == "/hermes/reports/off")
        {
            await reports.TurnOffReportsAsync(profile, context.RequestAborted).ConfigureAwait(false);
            log("server page reports outcome=off");
            Redirect(context.Response, "/manage/hermes?done=reports_off");
            return;
        }
        if (path == "/hermes/reports")
        {
            await SetupAsync(context, form, token, profile, state, agent).ConfigureAwait(false);
            return;
        }
        var title = "Set up reports by hand";
        // The Windows server listens on this PC only (ServerNetwork) and reports never come through Tailscale, so its code always names 127.0.0.1 and the
        // listener's own port, whatever address the page was opened by (a tailnet name would make a code every report to is refused). Elsewhere a new code
        // starts from the address the current one names: the page's own address is how a browser reaches the server, which for Hermes in a container beside it
        // is the published port, refused as Docker's gateway (live test, 2026-11-16: a new code made that way was refused until the address was typed again).
        var typed = form is not null ? form["address"].ToString().Trim()
            : DesktopTrust && ListenPort?.Invoke() is { } port and > 0 ? "127.0.0.1:" + port
            : state.Address is { Length: > 0 } kept ? kept : Address(context.Request);
        string Ask(string? problem)
        {
            var intro = "A connection code for " + HermesNames(agent).Name + ", to put in Hermes yourself. " + ReportsIntro + (state.On ? " The current code stops working." : "");
            var field = "<label for=\"address\">Address Hermes reaches this server at</label><input id=\"address\" name=\"address\" type=\"text\" value=\"" + Html(typed)
                + "\" autocomplete=\"off\" spellcheck=\"false\"><p class=\"hint\">" + (DesktopTrust
                ? "This PC's server takes reports only from Hermes running on this PC."
                : "Such as 192.168.1.20:8787. With Hermes in a Docker container on the same machine, use this server's container name on a shared Docker network, "
                    + "such as messages-server:8787, not a published port.") + "</p>"
                + "<input type=\"hidden\" name=\"profile\" value=\"" + Html(profile) + "\">";
            return AskBody(ReportCodePath, token, intro, "Show the code", problem, before: field);
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (AgentReportCode.Url(typed) is not { } url)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("Enter the address as a name or IP address and a port, such as 192.168.1.20:8787.")).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "reports", title, Ask).ConfigureAwait(false)) return;
        string issued;
        try { issued = await reports.IssueReportTokenAsync(profile, url.Authority, context.RequestAborted).ConfigureAwait(false); }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            log($"server page reports outcome=failed type={failure.GetType().Name}");
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't turn on reports. Try again.")).ConfigureAwait(false);
            return;
        }
        log("server page reports outcome=issued");
        var code = AgentReportCode.Build(url, fingerprint(), issued);
        context.Response.Headers.CacheControl = "no-store";
        await PageAsync(context, StatusCodes.Status200OK, "Reports are on", ReportsCodeBody(code)).ConfigureAwait(false);
    }

    // The code, once, with what to do with it in Hermes. Copy is data-copy-code, which the script on every signed-in page handles (JoinScript); data-copy is
    // handled only by the push page's own script.
    private static string ReportsCodeBody(string code) => new StringBuilder("<section>")
        .Append(Para(ReportsIntro + " This page shows it once."))
        .Append("<p class=\"copy block line\">").Append(Html(code)).Append("</p>")
        .Append("<p><button type=\"button\" class=\"quiet small\" data-copy-code=\"").Append(Html(code)).Append("\">Copy</button></p>")
        .Append("<h3>Set up Hermes</h3><ol class=\"steps\">")
        .Append("<li><a href=\"").Append(ReportScriptPath).Append("\">Download ml-report</a> and put its files in the profile's scripts folder, <span class=\"copy\">$HERMES_HOME/scripts/</span>. Make the folder if there isn't one.</li>")
        .Append("<li>Add <span class=\"copy\">MESSAGELINK_CODE=</span> followed by the code to the profile's <span class=\"copy\">.env</span> file.</li>")
        .Append("<li>Let Hermes's commands see it: add <span class=\"copy\">MESSAGELINK_CODE</span> to <span class=\"copy\">terminal.env_passthrough</span> in the profile's <span class=\"copy\">config.yaml</span>, or add the included SKILL.md to the profile's skills.</li>")
        .Append("<li>Run <span class=\"copy\">ml-report check</span> in the profile's terminal. Then <span class=\"copy\">ml-report \"Hello\"</span> posts a message here.</li></ol>")
        .Append("<p>Or use the <a href=\"").Append(PluginPath).Append("\">Message Link plugin for Hermes</a>, which keeps the code inside Hermes and lets scheduled jobs and <span class=\"copy\">hermes send</span> deliver here. ")
        .Append("A profile whose terminal runs over SSH should use the plugin. Its README has the steps.</p>")
        .Append("<p><a href=\"/manage/hermes\">Done</a></p></section>").ToString();

    // A zip made from the copies built into the server.
    private static async Task DownloadAsync(HttpContext context, byte[] bytes, string fileName)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/zip";
        response.Headers.ContentDisposition = "attachment; filename=\"" + fileName + "\"";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    internal static byte[] ReportScriptZip() => Zip(ReportScriptFiles);

    internal static byte[] PluginZip() => Zip(PluginFiles, PluginFolder);

    internal static byte[] Zip(IEnumerable<string> names, string folder = "")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names)
            {
                using var source = typeof(ServerPage).Assembly.GetManifestResourceStream("hermes-messagelink/" + name);
                if (source is null) continue;
                var entry = zip.CreateEntry(folder + name, CompressionLevel.Optimal);
                // A fixed time, so the same files make the same zip.
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                // The script is run directly: executable for its owner, as unzip on Linux keeps it.
                if (name == "ml-report") entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
                using var target = entry.Open();
                source.CopyTo(target);
            }
        return output.ToArray();
    }
}
