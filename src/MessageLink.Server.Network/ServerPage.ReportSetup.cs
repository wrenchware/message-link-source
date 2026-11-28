using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MessageLink.Core.Services;

namespace MessageLink.Server.Network;

// Reports set up in one command (docs/research/hermes-reports-plan.md, "Setup in one command"): Set up and New code ask for the password again, then show one
// command to run where Hermes runs. It downloads the installer over a connection pinned to this server's public key, checks its SHA-256 against the one the
// command names and only then runs it (never piped into a shell); the installer makes the profile's code there and sends the server only its token's hash, with
// the one-time setup code the command carries. This page then shows the result as it comes, asking the server every two seconds, as the join requests do.
internal sealed partial class ServerPage
{
    internal const string SetupWatchPath = "/hermes/reports/setup";

    /// <summary>curl's pin for this server's public key (<see cref="ReportInstaller.PublicKeyPin"/>), set by the host.</summary>
    internal string? PublicKeyPin { get; set; }

    /// <summary>Whether this server runs in a container: Hermes in a container beside it reaches it by its container name, which the command then tries.</summary>
    internal bool InContainer { get; set; }

    /// <summary>The container name this server's image is documented to run as (docker/messages-server), tried before the page's own address.</summary>
    internal const string ContainerName = "messages-server";

    // What the command carries besides the code may only be these characters, so nothing a request named can break out of its quotes.
    private static readonly Regex CommandSafe = new("^[A-Za-z0-9.:\\[\\]_-]+$", RegexOptions.CultureInvariant);

    private async Task SetupAsync(HttpContext context, IFormCollection? form, string token, string profile, AgentReportsState state, HermesAgentState agent)
    {
        var reports = Reports!;
        var name = HermesNames(agent).Name;
        var title = state.On ? "Set up " + name + " again" : "Let " + name + " message you first";
        string Ask(string? problem) => AskBody(context, "reports_setup", "/hermes/reports", token,
            (state.On ? "A new setup command for " + name + ". Once it runs, the earlier setup stops working. "
                : name + " can start a conversation when a task finishes or needs your answer. You run one command where Hermes runs. ")
            + "Enter the server's password again, since this lets Hermes send messages to this conversation.", "Show the command", problem,
            before: "<input type=\"hidden\" name=\"profile\" value=\"" + Html(profile) + "\">");
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "reports_setup", title, Ask).ConfigureAwait(false)) return;
        AgentSetupIssue issued;
        try { issued = reports.IssueSetupCode(profile); }
        catch (ArgumentException)
        {
            Redirect(context.Response, "/manage/hermes");
            return;
        }
        var command = DesktopTrust ? WindowsCommand(issued.Code, profile) : ShellCommand(issued.Code, profile, state.Address, context.Request);
        log("server page reports outcome=setup_shown");
        context.Response.Headers.CacheControl = "no-store";
        await PageAsync(context, StatusCodes.Status200OK, title, SetupBody(command, token, profile, name, issued.Id)).ConfigureAwait(false);
    }

    // The addresses Hermes may reach this server at, tried in this order after the one the profile's current code names: the address kept from the last code,
    // this server's container name when it runs in one, and the address this page was opened at.
    private IReadOnlyList<string> SetupAddresses(string? kept, HttpRequest request)
    {
        var found = new List<string>();
        void Add(string? typed)
        {
            if (AgentReportCode.Url(typed) is not { } url) return;
            var address = url.Host + ":" + url.Port;
            if (CommandSafe.IsMatch(address) && !found.Contains(address)) found.Add(address);
        }
        Add(kept);
        if (InContainer && ListenPort?.Invoke() is { } port and > 0) Add(ContainerName + ":" + port);
        Add(Address(request));
        return found;
    }

    // POSIX sh with curl, for Linux and macOS: the code in the environment only; the file saved, checked and then run, and removed afterwards.
    private string ShellCommand(string code, string profile, string? kept, HttpRequest request, bool add = false)
    {
        var addresses = SetupAddresses(kept, request);
        var page = AgentReportCode.Url(Address(request)) is { } url && CommandSafe.IsMatch(url.Host + ":" + url.Port) ? url.Host + ":" + url.Port : addresses.LastOrDefault() ?? "";
        var port = ListenPort?.Invoke() is { } listening and > 0 ? " --port " + listening : "";
        return "MLSETUP=" + code + " sh -c 'set -e; f=$(mktemp); trap \"rm -f \\\"\\$f\\\"\" EXIT; "
            + "curl -q -fsSk --proto =https --max-time 60 --max-filesize 4000000 --pinnedpubkey " + PublicKeyPin + " -o \"$f\" https://" + page + NetworkServer.SetupShellPath + "; "
            + "[ \"$({ sha256sum 2>/dev/null || shasum -a 256; } <\"$f\" | cut -c1-64)\" = " + ReportInstaller.ShellSha256 + " ] "
            + "|| { echo \"The download does not match this server. Nothing changed.\" >&2; exit 1; }; "
            + "sh \"$f\"" + port + (add ? " --add" : "") + " " + profile + " " + fingerprint() + " " + string.Join(' ', addresses) + "'";
    }

    // PowerShell with curl.exe, for the Windows server: it takes reports only from this PC, so Hermes is installed here and runs with its own Python.
    private string WindowsCommand(string code, string profile, bool add = false)
    {
        var address = "127.0.0.1:" + (ListenPort?.Invoke() ?? MessageLink.Protocol.NetworkEndpoint.DefaultPort);
        return "$env:MLSETUP='" + code + "'; $f=Join-Path $env:TEMP ('ml-setup-'+[guid]::NewGuid()+'.py'); try { "
            + "curl.exe -q -fsSk --proto =https --max-time 60 --max-filesize 4000000 --pinnedpubkey '" + PublicKeyPin + "' -o $f 'https://" + address + NetworkServer.SetupPythonPath + "'; "
            + "if ($LASTEXITCODE -ne 0) { 'The download failed. Nothing changed.' } "
            + "elseif ((Get-FileHash $f -Algorithm SHA256).Hash -ne '" + ReportInstaller.PythonSha256 + "') { 'The download does not match this server. Nothing changed.' } "
            + "else { $h=Get-Command hermes -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1; $a=@(); $py='python'; "
            + "if ($h) { $a+='--hermes',$h.Source; $p=Join-Path (Split-Path $h.Source) 'python.exe'; if (Test-Path $p) { $py=$p } }; "
            + "& $py -I $f @a " + (add ? "--add " : "") + profile + " " + fingerprint() + " " + address + " } } "
            + "finally { Remove-Item $f -ErrorAction SilentlyContinue; Remove-Item Env:MLSETUP -ErrorAction SilentlyContinue }";
    }

    private string SetupBody(string command, string token, string profile, string name, string id)
    {
        var where = DesktopTrust
            ? "Run this in PowerShell on this PC, where Hermes runs. This PC's server takes reports only from Hermes on this PC."
            : "Run this on the computer that runs Hermes. If Hermes runs in Docker, run it there, not inside the container.";
        return new StringBuilder("<section>").Append(Para(where))
            .Append("<div id=\"setup-command\"><p class=\"copy block line\">").Append(Html(command)).Append("</p>")
            .Append("<p><button type=\"button\" class=\"quiet small\" data-copy-code=\"").Append(Html(command)).Append("\">Copy</button></p>")
            .Append(Para("It works once, within " + (int)AgentReportRules.SetupLifetime.TotalMinutes + " minutes. It adds the Message Link plugin to Hermes and makes a code that only Hermes keeps."))
            .Append("</div>")
            .Append("<div class=\"box\" id=\"setup\" data-watch=\"").Append(Html(SetupWatchPath + "?profile=" + Uri.EscapeDataString(profile) + "&id=" + Uri.EscapeDataString(id))).Append("\">")
            .Append(Row("Waiting for the command", "This page shows the result when the command is done.", null, null, id: "setup-state"))
            .Append("</div>")
            .Append(TurnOffForm(token, profile, name, "danger", "setup-off"))
            .Append("<p><a href=\"").Append(Html(ReportCodePath + "?profile=" + Uri.EscapeDataString(profile))).Append("\">Set up by hand instead</a></p>")
            .Append("<p><a href=\"/manage/hermes\">Done</a></p></section>")
            .Append("<script>").Append(SetupScript).Append("</script>").ToString();
    }

    // The setup code's state as JSON for SetupScript: never the code, only what the server saw.
    private async Task SetupWatchAsync(HttpContext context, string profile)
    {
        var id = context.Request.Query["id"].ToString();
        var response = context.Response;
        if (Reports!.SetupState(profile, id) is not { } state)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        object answer = state.Phase switch
        {
            AgentSetupPhase.Waiting => new { state = "waiting" },
            AgentSetupPhase.Done => new { state = "done", at = state.At is { } at ? When(at) : "", address = state.Address ?? "", from = state.From ?? "" },
            AgentSetupPhase.Replaced => new { state = "replaced" },
            _ => new { state = "expired" },
        };
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(JsonSerializer.Serialize(answer), Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The command page's own few lines: every two seconds while the tab is in view, and later and later while the server doesn't answer, it asks how the
    /// command stands and shows it. Once it is done (or has run out) the command leaves the page; done shows the address Hermes uses, when, and where it came
    /// from, with Turn off beside it. Words only ever go in as text.
    /// </summary>
    internal const string SetupScript = """
(function(){
var box=document.getElementById('setup');if(!box)return;var row=document.getElementById('setup-state'),cmd=document.getElementById('setup-command'),off=document.getElementById('setup-off'),fails=0;
function show(title,under){var g=row.querySelector('.g');g.textContent=title;var s=document.createElement('small');s.textContent=under;g.appendChild(s)}
function tick(){if(document.hidden){setTimeout(tick,1000);return}
fetch(box.getAttribute('data-watch'),{redirect:'error',cache:'no-store'})
.then(function(r){if(r.status===404)return {state:'expired'};if(!r.ok)throw r.status;return r.json()}).then(function(d){fails=0;
if(d.state==='waiting'){if(d.problem)show('Waiting for the command',d.problem);setTimeout(tick,2000);return}
if(cmd)cmd.remove();
if(d.state==='done'&&d.title)show(d.title,d.under);
else if(d.state==='done'){show('Turned on','Hermes reaches this server at '+d.address+'. Set up '+d.at+(d.from?' from '+d.from:'')+'.');if(off)off.hidden=false}
else if(d.state==='replaced')show('A newer command replaced this one','Use the newest command, or make another.');
else show('This command has expired','Make a new one to try again.')})
.catch(function(){fails++;setTimeout(tick,Math.min(30000,2000*Math.pow(2,fails)))})}
tick();
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="SetupScript"/> run.</summary>
    internal static readonly string SetupScriptHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(SetupScript)));
}
