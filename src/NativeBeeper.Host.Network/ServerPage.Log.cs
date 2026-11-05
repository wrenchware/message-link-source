using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// The server's log on its page, live: the page draws its end, and its few lines of script, allowed by their hash alone on this page alone, ask its own server for what was written since.
// The words only ever go in as text. A person scrolling up to read stops it following; the lines keep arriving below them.
internal sealed partial class ServerPage
{
    /// <summary>What was written to the log since a place in it, as JSON, for the log page's script.</summary>
    public const string LogTailPath = "/log/tail";

    // The end of the log, whole lines only.
    private async Task<HostLogChunk?> LogEndAsync(CancellationToken cancellationToken)
    {
        if (logs is null) return null;
        var head = await logs.ReadAsync(HostLogNames.Server, long.MaxValue, cancellationToken).ConfigureAwait(false);
        if (!head.Exists) return null;
        var chunk = await logs.ReadAsync(HostLogNames.Server, Math.Max(0, head.Length - LogTail), cancellationToken).ConfigureAwait(false);
        if (!chunk.Exists) return null;
        // From the end of the log, whole lines only.
        return chunk.Length > LogTail && chunk.Text.IndexOf('\n') is var first and >= 0 ? chunk with { Text = chunk.Text[(first + 1)..] } : chunk;
    }

    private async Task<string> LogBodyAsync(CancellationToken cancellationToken)
    {
        if (await LogEndAsync(cancellationToken).ConfigureAwait(false) is not { } chunk) return Para("The server keeps no log here.");
        return new StringBuilder("<div class=\"sh\"><h2>Server log</h2><span class=\"r\"><button id=\"log-jump\" type=\"button\" class=\"quiet\" hidden>Jump to newest</button><span id=\"log-state\" class=\"v\" role=\"status\"></span></span></div>")
            .Append("<pre id=\"log\" class=\"log\" tabindex=\"0\" role=\"log\" aria-label=\"The server's log\" data-from=\"").Append(chunk.Next).Append("\" data-head=\"").Append(Html(chunk.Head)).Append("\">").Append(Html(chunk.Text)).Append("</pre>")
            .Append("<script>").Append(LogScript).Append("</script>").ToString();
    }

    // What was written since `from`, or the end of the log again when that place is no more: the file is a new run (its first bytes differ), or shorter than the place.
    private async Task LogTailAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var query = context.Request.Query;
        HostLogChunk? chunk = null;
        var reset = true;
        if (logs is not null && long.TryParse(query["from"].ToString(), out var from) && from >= 0)
        {
            var read = await logs.ReadAsync(HostLogNames.Server, from, cancellationToken).ConfigureAwait(false);
            if (read.Exists && read.Length >= from && read.Head == query["head"].ToString())
            {
                chunk = read;
                reset = false;
            }
        }
        chunk ??= await LogEndAsync(cancellationToken).ConfigureAwait(false);
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(JsonSerializer.Serialize(new { exists = chunk is not null, reset, next = chunk?.Next ?? 0, head = chunk?.Head ?? "", text = chunk?.Text ?? "" }), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The log page's policy: every other page's, with its script instead and a way to its own server.</summary>
    internal static string LogPolicy => $"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src '{LogScriptHash}'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>
    /// The log page's script: asks about once a second what was written since, while the tab is in view, one question at a time, and later and later while it gets no answer. New lines go in as
    /// their own text, which leaves a selection alone, and old ones go only while following. Nothing is sent anywhere but this page's own server.
    /// </summary>
    internal const string LogScript = """
(function(){
var box=document.getElementById('log'),state=document.getElementById('log-state'),jump=document.getElementById('log-jump');
if(!box)return;
var from=box.getAttribute('data-from'),head=box.getAttribute('data-head'),follow=true,fails=0;
function toEnd(){box.scrollTop=box.scrollHeight}
function show(){jump.hidden=follow;state.textContent=fails>2?'Can’t reach the server':follow?'Live':'Paused'}
function look(){var at=box.scrollHeight-box.scrollTop-box.clientHeight<24;if(at!==follow){follow=at;show()}}
box.addEventListener('scroll',look);
jump.addEventListener('click',function(){follow=true;toEnd();show()});
function add(text,reset){look();if(reset)box.textContent='';if(text)box.appendChild(document.createTextNode(text));while(follow&&box.childNodes.length>4000)box.removeChild(box.firstChild);if(follow)toEnd()}
function tick(){if(document.hidden){setTimeout(tick,1000);return}
fetch('/log/tail?from='+encodeURIComponent(from)+'&head='+encodeURIComponent(head),{redirect:'error',cache:'no-store'}).then(function(r){if(!r.ok)throw r.status;return r.json()}).then(function(d){fails=0;if(d.exists){add(d.text,d.reset);from=d.next;head=d.head}show();setTimeout(tick,1000)}).catch(function(){fails++;show();setTimeout(tick,Math.min(30000,1000*Math.pow(2,fails)))})}
toEnd();show();setTimeout(tick,1000);
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="LogScript"/> run.</summary>
    internal static readonly string LogScriptHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(LogScript)));
}
