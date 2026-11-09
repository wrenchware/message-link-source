using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// A device asking to join, seen wherever the owner is on the page: every signed-in page carries a line of its own that asks the server what waits, so nothing has to be reloaded and a form
// being filled in is left alone. The words only ever go in as text.
internal sealed partial class ServerPage
{
    /// <summary>The requests waiting, as JSON, for <see cref="JoinScript"/>.</summary>
    public const string JoinWatchPath = "/joins/watch";

    /// <summary>Where the PC sends its browser as it asks to join: the one request waiting, or the front page when there is none yet or several.</summary>
    public const string JoinReviewPath = "/joins/review";

    private const string JoinSection = "join";

    /// <summary>Set by the host: whether a device has a connection open now. One that has is another device of the same name, never an old entry of the one asking.</summary>
    internal Func<string, bool>? DeviceOnline { get; set; }

    private const string JoinAskBox = "<div id=\"join-ask\" class=\"box\" hidden></div>";

    private static string JoinAsk => JoinAskBox + "<script>" + JoinScript + "</script>";

    private async Task JoinWatchAsync(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(JsonSerializer.Serialize(new { asking = joins.Waiting().Select(request => new { id = request.Id, name = request.DeviceName, code = request.Code }) }), Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
    }

    private string JoinReviewTarget() => joins.Waiting() is [var only] ? "/joins/approve?id=" + Uri.EscapeDataString(only.Id) : "/";

    // The entries a device asking under this name would leave behind: the same name, and not connected now.
    private IReadOnlyList<PairedDevice> OldEntries(string name) =>
        [.. devices.List(null).Where(device => string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase) && DeviceOnline?.Invoke(device.Id) != true)];

    // Ticked at first; as it was sent when the page is drawn again after a wrong password.
    private string ReplaceBoxes(IReadOnlyList<PairedDevice> old, StringValues? sent)
    {
        var boxes = new StringBuilder();
        foreach (var device in old)
            boxes.Append("<p><label><input type=\"checkbox\" name=\"replace\" value=\"").Append(Html(device.Id)).Append('"').Append(sent is not { } kept || kept.Contains(device.Id) ? " checked" : "").Append("> Remove the old ")
                .Append(Html(device.Name)).Append(device.LastSeen is { } seen ? " (last seen " + Html(Ago(seen)) + ")" : "").Append(" once this one has joined</label></p>");
        return boxes.ToString();
    }

    /// <summary>
    /// On every signed-in page: looks every 3 seconds while the tab is in view, one question at a time and later and later while it gets no answer, and shows each request the page does not already
    /// link to, with their number in the tab's title. It draws again only when the answer changed, so focus stays where it was. Also the finish code's Copy button. Nothing is sent anywhere but this page's own server.
    /// </summary>
    internal const string JoinScript = """
(function(){
var box=document.getElementById('join-ask'),title=document.title,fails=0,last='';
function href(r){return '/joins/approve?id='+encodeURIComponent(r.id)}
function line(r){var row=document.createElement('div'),g=document.createElement('div'),v=document.createElement('span'),a=document.createElement('a');row.className='row';g.className='g';v.className='v';a.className='button';g.textContent=r.name+' wants to join';v.textContent='Code '+r.code;a.textContent='Review';a.href=href(r);row.appendChild(g);row.appendChild(v);row.appendChild(a);return row}
function draw(d){var text=JSON.stringify(d);if(text===last)return;last=text;box.textContent='';var n=0;d.asking.forEach(function(r){var h=href(r);if(location.pathname+location.search===h||document.querySelector('a[href="'+h+'"]'))return;box.appendChild(line(r));n++});box.hidden=n===0;document.title=(d.asking.length?'('+d.asking.length+') ':'')+title}
function tick(){if(document.hidden){setTimeout(tick,1000);return}
fetch('/joins/watch',{redirect:'error',cache:'no-store'}).then(function(r){if(!r.ok)throw r.status;return r.json()}).then(function(d){fails=0;draw(d);setTimeout(tick,3000)}).catch(function(){fails++;setTimeout(tick,Math.min(30000,3000*Math.pow(2,fails)))})}
tick();
document.addEventListener('click',function(e){var b=e.target.closest?e.target.closest('[data-copy-code]'):null;if(!b)return;var c=b.getAttribute('data-copy-code');function done(){b.textContent='Copied'}
function plain(){var t=document.createElement('textarea');t.value=c;document.body.appendChild(t);t.select();try{if(document.execCommand('copy'))done()}catch(x){}t.remove()}
if(navigator.clipboard&&navigator.clipboard.writeText)navigator.clipboard.writeText(c).then(done,plain);else plain()});
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="JoinScript"/> run.</summary>
    internal static readonly string JoinScriptHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(JoinScript)));
}
