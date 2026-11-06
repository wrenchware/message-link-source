using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// Signing in to a service on the server's page (SWITCHBOARD.md 10.6): the server's own browser, shown on the page as the Windows app shows it, and driven from the page.
//
// The view is a page of its own (SignInPath) whose few lines of script, allowed by their hash alone, open a socket to the page's own address (SignInSocketPath). The socket is the page's,
// never the client door's: it takes the page's sign-in cookie, the page's form token and the page's own origin, all three, from the home network only (the door's FromTrustedNetwork),
// and the client socket still refuses anything that names an origin. A signed-in page counts as the server's owner, but opening the browser asks for the password again, as
// every other dangerous step on the page does: it can open on a copy of the kept Google profile, Voice's web app included, and a cookie alone reaches any HTTPS service on this
// address (audit, 2026-11-01). The answer is a pass the socket spends: one use, this browser's session and this service, for two minutes. The sign-in itself runs through the host's one
// RemoteSignInSessions, as the app's does, so one sign-in is open at a time across the app and the page and input reaches it only from the socket that started it.
//
// Logged: `page sign-in service= outcome=` with fixed words only (a refused socket the first time and then one in a hundred). Never a picture, a key, typed text, a cookie or a token.
internal sealed partial class ServerPage
{
    /// <summary>The page that shows the server's browser for one service.</summary>
    public const string SignInPath = "/sign-in";

    /// <summary>The page's own socket for that view: frames out, the person's input in.</summary>
    public const string SignInSocketPath = "/page/sign-in";

    /// <summary>The most one message from the page may carry: a batch of input, a paste among it.</summary>
    public const int MaxSignInMessage = 256 * 1024;

    /// <summary>How often a running sign-in checks the page is still signed in (a sign-out anywhere ends it).</summary>
    internal TimeSpan SignInRecheck { get; set; } = TimeSpan.FromSeconds(15);

    private volatile ProtocolServices? signInServices;
    private int socketRefusals;

    /// <summary>The host's services, whose browser sign-ins the page offers. Without them (a host given none) every service says where to connect it instead.</summary>
    internal void OfferSignIn(ProtocolServices services) => signInServices = services;

    private static bool IsSignInPath(string? path) => path is SignInPath;

    // A new service is one line here (and a card in Settings). Many: it takes more than one account, so its button adds one rather than signing the one in again.
    // Only Google Voice does today: its helper runs one process per account, and the other services' ports hold one account each (ROADMAP.md, Several accounts on one service).
    private static readonly (string Service, string Name, string Provider, bool Many)[] SignInServiceList =
    [
        (RemoteSignInServices.Google, "Google Messages", ServerAccountProviders.GoogleMessages, false),
        (RemoteSignInServices.Messenger, "Messenger", ServerAccountProviders.Messenger, false),
        (RemoteSignInServices.Voice, "Google Voice", ServerAccountProviders.GoogleVoice, true),
        (RemoteSignInServices.Instagram, "Instagram", ServerAccountProviders.Instagram, false),
    ];

    private static string SignInName(string service) => SignInServiceList.First(item => item.Service == service).Name;

    private static bool SignInMany(string service) => SignInServiceList.First(item => item.Service == service).Many;

    // ---- The Connect buttons, on the Accounts card and the walkthrough's accounts step ----

    private void ConnectList(StringBuilder body, IReadOnlyList<ServerAccount>? accounts, bool walkthrough, HttpContext context, string token)
    {
        // The Windows server (10.7) signs in with Messages' native windows on its own desktop (owner D2): Connect there, from a browser on that PC, and nowhere else.
        if (DesktopSignIn is not null)
        {
            DesktopSignInForms(body, context, token, accounts);
            if (!OnThisPc(context)) body.Append(Para("Accounts are connected on the PC this server runs on: in Message Link there (Settings → Connections), or with Connect on this page in a browser on that PC."));
            return;
        }
        var services = signInServices;
        // A server with no browser at all says so once, rather than the same sentence under each of the four services.
        if (!AnyBrowser(services))
        {
            body.Append(Para(NoSignIn));
            return;
        }
        var any = false;
        body.Append("<ul class=\"rows connect\">");
        foreach (var (service, name, provider, many) in SignInServiceList)
        {
            var has = accounts?.Any(account => account.Provider == provider) == true;
            var offered = services is not null && RemoteSignInSessions.Offers(services, service);
            any |= offered;
            var button = many ? has ? "Add another account" : "Add an account" : has ? "Sign in again" : "Connect";
            body.Append("<li><span class=\"grow\"><strong>").Append(Html(name)).Append("</strong><span class=\"quiet\">")
                .Append(Html(offered ? ConnectHint(service) : Elsewhere(service, services))).Append("</span></span>");
            if (offered)
                body.Append("<a class=\"button").Append(has ? " quiet" : "").Append("\" href=\"").Append(SignInPath).Append("?service=").Append(service)
                    .Append(walkthrough ? "&amp;from=welcome" : "").Append("\" aria-label=\"").Append(Html($"{button}: {name}")).Append("\">").Append(Html(button)).Append("…</a>");
            body.Append("</li>");
        }
        body.Append("</ul>");
        if (!any) return;
        if (services!.SignIns.OpenOn is { } open) body.Append(Notice(BusyWords(open)));
        body.Append("<p class=\"quiet\">Each opens the service's own sign-in page in the server's browser, shown here. Only someone signed in to this page can connect an account, one at a time.</p>");
    }

    private static string ConnectHint(string service) => service switch
    {
        RemoteSignInServices.Google => "Sign in with your Google account, then tap an emoji on your phone.",
        RemoteSignInServices.Voice => "Each Google Voice number signs in on its own.",
        RemoteSignInServices.Messenger => "Sign in to Facebook, then choose Done.",
        _ => "Sign in to Instagram, then choose Done.",
    };

    // The page is the only place a server's accounts connect (owner, 2026-11-03), so a server that can't show a sign-in here says what it needs. Its one image has Chrome: this is a server
    // run on the host's network (SignInExposure.cs) or installed without it.
    private const string NoSignIn = "This server can't show sign-in pages, so accounts can't be connected here yet. It needs Google Chrome and a network of its own.";

    private static string Elsewhere(string service, ProtocolServices? services) =>
        AnyBrowser(services) ? $"This server can't sign in to {SignInName(service)} in its own browser. Update the server, then try again." : NoSignIn;

    private static bool AnyBrowser(ProtocolServices? services) =>
        services is not null && (services.RemoteSignIn is not null || services.VoiceRemoteSignIn is not null || services.MetaRemoteSignIn is not null);

    private static string BusyWords(string? face) => face switch
    {
        RemoteSignInFaces.Page => "Another sign-in is open on this page, in another tab or browser. Finish or cancel it there first.",
        _ => "The server is already signing in, or renewing a sign-in. Try again in a moment.",
    };

    // ---- The view ----

    private async Task SignInViewAsync(HttpContext context, IFormCollection? form, string token)
    {
        var request = context.Request;
        var service = request.Query["service"].ToString();
        // The Windows server signs in on its own desktop (ServerPage.Desktop.cs), never in a view here.
        if (!RemoteSignInServices.Known(service) || DesktopSignIn is not null)
        {
            Redirect(context.Response, "/#accounts");
            return;
        }
        var walkthrough = request.Query["from"] == "welcome";
        var back = walkthrough ? "/welcome?step=accounts" : "/#accounts";
        var backWords = walkthrough ? "Back to setting up" : "Back to the server";
        var name = SignInName(service);
        var title = SignInMany(service) ? $"Add a {name} account" : "Connect " + name;
        var services = signInServices;
        if (services is null || !RemoteSignInSessions.Offers(services, service))
        {
            await PageAsync(context, StatusCodes.Status200OK, title, "<section>" + Notice(Elsewhere(service, services)) + "<p><a href=\"" + back + "\">" + Html(backWords) + "</a></p></section>")
                .ConfigureAwait(false);
            return;
        }
        var action = SignInPath + "?service=" + service + (walkthrough ? "&amp;from=welcome" : "");
        string Ask(string? problem) => AskBody(action, token, "This opens the server's own browser, which can hold your signed-in accounts, so it asks for the server password again.", "Continue", problem);
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "open_browser", title, Ask).ConfigureAwait(false)) return;
        var grant = SignInGrant(Session(request)!, service);
        var meta = RemoteSignInServices.EndsWithDone(service);
        var body = new StringBuilder("<style>").Append(SignInStyle).Append("</style>");
        body.Append("<section class=\"signin\" id=\"view\" data-service=\"").Append(service).Append("\" data-form=\"").Append(Html(token)).Append("\" data-grant=\"").Append(grant).Append("\" data-ready=\"")
            .Append(Html(ReadyWords(service))).Append("\">")
            .Append("<p id=\"status\" role=\"status\" aria-live=\"polite\">Starting the server's browser…</p>")
            .Append("<div id=\"emoji\" class=\"emoji\" hidden><p id=\"emoji-text\" class=\"emoji-big\" aria-live=\"assertive\"></p><p>In Google Messages on your phone, tap this emoji.</p></div>")
            .Append("<div class=\"frame\" id=\"frame\"><textarea id=\"keys\" aria-label=\"Type into the sign-in page\" autocomplete=\"off\" autocapitalize=\"off\" autocorrect=\"off\" spellcheck=\"false\" rows=\"1\"></textarea>")
            .Append("<div id=\"stage\" class=\"stage\"><canvas id=\"screen\" width=\"").Append(RemoteSignIn.DefaultWidth).Append("\" height=\"").Append(RemoteSignIn.DefaultHeight)
            .Append("\" role=\"img\" aria-label=\"The sign-in page, in the server's browser\"></canvas></div></div>")
            .Append("<div id=\"bar\" class=\"bar\"><span class=\"group\">")
            .Append("<button id=\"keyboard\" type=\"button\" class=\"quiet\" aria-label=\"Show the keyboard to type into the sign-in page\">Keyboard</button>")
            .Append("<button id=\"zoom\" type=\"button\" class=\"quiet\" aria-pressed=\"false\">Zoom in</button>")
            .Append("</span><span class=\"group end\"><button id=\"cancel\" type=\"button\" class=\"quiet\">Cancel</button>")
            .Append(meta ? "<button id=\"done\" type=\"button\">Done</button>" : "")
            .Append("</span></div>")
            .Append("<p id=\"hint\" class=\"quiet\">Click or tap the page to use it, and type as you would in a browser. What you type goes straight to the sign-in page: the server never keeps or logs it.</p>")
            .Append("</section>");
        body.Append("<section id=\"outcome\" class=\"outcome\" tabindex=\"-1\" hidden><h2 id=\"outcome-title\"></h2><p id=\"outcome-text\"></p><p class=\"actions\"><a class=\"button\" href=\"")
            .Append(back).Append("\">").Append(Html(backWords)).Append("</a> <a id=\"again\" class=\"button quiet\" href=\"").Append(SignInPath).Append("?service=").Append(service)
            .Append(walkthrough ? "&amp;from=welcome" : "").Append("\">Start again</a></p></section>");
        body.Append("<script>").Append(SignInScript).Append("</script>");
        context.Response.Headers.ContentSecurityPolicy = SignInPolicy(request);
        await PageAsync(context, StatusCodes.Status200OK, title, body.ToString()).ConfigureAwait(false);
    }

    // ---- The pass the password buys ----

    /// <summary>How long a pass waits for its socket: the view opens it as it loads.</summary>
    internal TimeSpan SignInGrantLife { get; set; } = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, (string Session, string Service, DateTimeOffset Until)> signInGrants = new(StringComparer.Ordinal);

    private static string GrantKey(string grant) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(grant)));

    private string SignInGrant(string session, string service)
    {
        var grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        lock (signInGrants)
        {
            foreach (var key in signInGrants.Where(pair => pair.Value.Until <= now).Select(pair => pair.Key).ToArray()) signInGrants.Remove(key);
            signInGrants[GrantKey(grant)] = (session, service, now + SignInGrantLife);
        }
        return grant;
    }

    // Spent by the asking: right or wrong, it is not asked of again.
    private bool TakeSignInGrant(string grant, string session, string service)
    {
        lock (signInGrants)
            return signInGrants.Remove(GrantKey(grant), out var held) && held.Session == session && held.Service == service && time.GetUtcNow() < held.Until;
    }

    private static string ReadyWords(string service) => service switch
    {
        RemoteSignInServices.Google => "Sign in with the Google account you use in Google Messages. Your phone then shows an emoji to tap.",
        RemoteSignInServices.Voice => "Sign in with the Google account that has your Google Voice number.",
        RemoteSignInServices.Messenger => "Sign in to Facebook. Once it shows you're signed in, choose Done.",
        _ => "Sign in to Instagram. Once it shows you're signed in, choose Done.",
    };

    // The view's policy: every other page's, with its one script allowed by hash and its socket to this address alone.
    internal static string SignInPolicy(HttpRequest request)
    {
        var host = request.Host.Value;
        var socket = host is { Length: > 0 and <= 255 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']') ? " wss://" + host : "";
        return $"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src '{SignInScriptHash}'; connect-src 'self'{socket}; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    }

    // ---- The socket ----

    /// <summary>
    /// The view's socket, as the door hands it over (from the home network only). Refused before the upgrade, with a status and nothing else, unless it is a WebSocket GET from a page signed in
    /// with the page's cookie, naming the page's own origin and carrying the page's form token, for a service this server signs in. Then one sign-in runs for as long as the socket is open:
    /// closing it is Cancel.
    /// </summary>
    internal async Task SignInSocketAsync(HttpContext context, bool draining, Action admitted, CancellationToken stopping)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        var service = request.Query["service"].ToString();
        var known = RemoteSignInServices.Known(service);
        var session = Session(request);
        var services = signInServices;
        var (status, refusal) =
            draining ? (StatusCodes.Status503ServiceUnavailable, "stopping")
            : !HttpMethods.IsGet(request.Method) || !context.WebSockets.IsWebSocketRequest ? (StatusCodes.Status400BadRequest, "handshake")
            : settingUp ? (StatusCodes.Status403Forbidden, "setting_up")
            : session is null ? (StatusCodes.Status403Forbidden, "cookie")
            : !SameOrigin(request) ? (StatusCodes.Status403Forbidden, "origin")
            : !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Query["form"].ToString()), Encoding.ASCII.GetBytes(FormToken(session))) ? (StatusCodes.Status403Forbidden, "token")
            : !known ? (StatusCodes.Status400BadRequest, "service")
            : DesktopSignIn is not null || services is null || !RemoteSignInSessions.Offers(services, service) ? (StatusCodes.Status404NotFound, "not_supported")
            : !TakeSignInGrant(request.Query["grant"].ToString(), session, service) ? (StatusCodes.Status403Forbidden, "grant")
            : (0, null);
        if (refusal is not null)
        {
            var count = Interlocked.Increment(ref socketRefusals);
            if (count == 1 || count % 100 == 0) log($"page sign-in service={(known ? service : "unknown")} outcome=refused_{refusal}{(count == 1 ? "" : $" count={count}")}");
            response.StatusCode = status;
            response.Headers.Connection = "close";
            response.ContentLength = 0;
            await response.CompleteAsync().ConfigureAwait(false);
            return;
        }

        WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = NetworkEndpoint.KeepAlive, KeepAliveTimeout = NetworkEndpoint.KeepAlive, DangerousEnableCompression = false,
            }).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log($"page sign-in service={service} outcome=failed_upgrade");
            return;
        }
        // Through the door: the socket no longer holds one of its places.
        admitted();
        await RunSignInAsync(socket, services!, service, session!, stopping).ConfigureAwait(false);
    }

    private async Task RunSignInAsync(WebSocket socket, ProtocolServices services, string service, string session, CancellationToken stopping)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var signedOut = 0;
        await using var link = new PageSignInLink(socket, service);
        var driver = new RemoteSignInDriver(link, RemoteSignInFaces.Page, link, link, log);
        log($"page sign-in service={service} outcome=started");
        // A sign-out anywhere (or the password changed) ends it: checked now and then, and before each batch of input is passed on. The cookie's value is checked, never the request,
        // which the server may reuse once this returns.
        bool StillSignedIn()
        {
            if (SessionOf(session) is not null) return true;
            Interlocked.Exchange(ref signedOut, 1);
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            return false;
        }
        using var recheck = new Timer(_ => StillSignedIn(), null, SignInRecheck, SignInRecheck);
        var run = Task.Run(() => services.SignIns.RunAsync(services, service, driver, "page-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), false, 0, 0, stop.Token));
        var reading = ReadInputAsync(socket, link, services, StillSignedIn, stop.Token);
        // The page going away (Cancel, a closed tab) or a sign-out cancels the sign-in; the server stopping does through `stopping`.
        if (await Task.WhenAny(run, reading).ConfigureAwait(false) == reading)
            try { stop.Cancel(); } catch (ObjectDisposedException) { }

        string outcome;
        bool ok = false;
        string words;
        try
        {
            var result = await run.ConfigureAwait(false);
            ok = true;
            outcome = "ok";
            words = DoneWords(service, result);
        }
        catch (OperationCanceledException)
        {
            outcome = Volatile.Read(ref signedOut) != 0 ? "signed_out" : stopping.IsCancellationRequested ? "stopping" : "cancelled";
            words = outcome switch
            {
                "signed_out" => "This page was signed out, so the sign-in was stopped. Sign in to the page again to connect an account.",
                "stopping" => "The server is stopping, so the sign-in was stopped. Nothing was changed.",
                _ => "Cancelled. Nothing was changed.",
            };
        }
        catch (RemoteSignInBusyException busy) { outcome = "busy"; words = BusyWords(busy.Face); }
        catch (NotSupportedException) { outcome = "not_supported"; words = Elsewhere(service, services); }
        catch (InvalidOperationException failure) when (ProtocolDispatcher.IsCodeWord(failure.Message)) { outcome = failure.Message; words = FailedWords(service, failure.Message); }
        catch (Exception failure) { outcome = "failed_" + failure.GetType().Name; words = FailedWords(service, null); }
        log($"page sign-in service={service} outcome={outcome}");

        try { await link.EndAsync(ok, words, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) { }
        // The page answers the close (a browser does by itself), which ends the reading; only then is anything cut, so the last word is never lost to a reset.
        try { await reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        socket.Abort();
        socket.Dispose();
    }

    // What the page sends: batches of input, {"events": [...]}, each passed on in order to the sign-in this socket holds. A message too large ends the socket; one that is not a batch is dropped.
    // Nothing read here is logged, kept or put into an error.
    private static async Task ReadInputAsync(WebSocket socket, PageSignInLink link, ProtocolServices services, Func<bool> stillSignedIn, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxSignInMessage];
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    if (length == buffer.Length)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    received = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    length += received.Count;
                }
                while (!received.EndOfMessage);
                if (received.MessageType != WebSocketMessageType.Text) continue;
                RemoteSignInInputParams? batch;
                try { batch = JsonSerializer.Deserialize<RemoteSignInInputParams>(buffer.AsSpan(0, length), ProtocolJson.Options); }
                catch (JsonException) { continue; }
                if (batch?.Events is not { Count: > 0 and <= RemoteSignIn.MaxEvents } events || events.Any(item => item is null)) continue;
                if (!stillSignedIn()) return;
                try { await services.SignIns.InputAsync(link, events, cancellationToken).ConfigureAwait(false); }
                // The sign-in ended (or has not reached its browser yet): what was sent goes nowhere.
                catch (InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        Array.Clear(buffer);
    }

    private static string DoneWords(string service, RemoteSignInResult result)
    {
        var joined = result.Conversations == 1 ? "1 conversation joined." : $"{result.Conversations} conversations joined.";
        return service switch
        {
            RemoteSignInServices.Google => "Google Messages is connected. Your conversations arrive on your devices in a moment.",
            RemoteSignInServices.Voice => "The Google Voice account is added.",
            RemoteSignInServices.Instagram => "Instagram is connected. " + joined,
            _ => "Messenger is connected. " + joined + result.Encrypted switch
            {
                null or RemoteSignInEncryption.AlreadyOn => "",
                RemoteSignInEncryption.On => " Encrypted conversations are on too.",
                _ => " Encrypted conversations didn't turn on, so most conversations can't be read yet. In Message Link on your PC, choose Turn on encrypted conversations on the Messenger card.",
            },
        };
    }

    private static string FailedWords(string service, string? word) => word switch
    {
        "sign_in_busy" => BusyWords(null),
        "sign_in_timeout" => "The sign-in took too long and was stopped. Start it again whenever you're ready.",
        "sign_in_browser" => "The server's browser couldn't run the sign-in. Try again.",
        "sign_in_unsaved" or "sign_in_profile" => "The server's browser couldn't save this sign-in safely. Nothing was changed. Try again.",
        "missing_cookies" => $"{(service == RemoteSignInServices.Instagram ? "Instagram" : "Facebook")} hadn't finished signing you in when you chose Done. Start again, and choose Done once you're signed in.",
        "no_voice_number" => "That Google account has no Google Voice number. Sign in with the account that has one.",
        { } other => $"The sign-in didn't finish ({other}). Start it again.",
        null => "The sign-in didn't finish. Start it again.",
    };

    private const string SignInStyle =
        "[hidden]{display:none!important}main:has(.signin){max-width:1000px}.signin #status{font-weight:600;min-height:1.5em}.frame{position:relative;margin:0 0 10px}"
        + "#keys{position:absolute;left:0;top:0;width:1px;height:1px;opacity:0;border:0;padding:0;margin:0;resize:none;font-size:16px;caret-color:transparent;overflow:hidden}"
        + ".stage{border:1px solid var(--line);border-radius:6px;overflow:hidden;background:#fff;line-height:0}.frame:focus-within .stage{outline:2px solid var(--accent);outline-offset:2px}"
        + ".stage canvas{display:block;width:100%;height:auto;touch-action:none;cursor:default}"
        + ".stage.zoomed{overflow:auto;max-height:75vh;-webkit-overflow-scrolling:touch}.stage.zoomed canvas{max-width:none;touch-action:pan-x pan-y}"
        + ".bar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:0 0 10px}.bar .group{display:flex;gap:8px}.bar .end{margin-left:auto}"
        + ".emoji{text-align:center;margin:0 0 12px}.emoji-big{font-size:72px;line-height:1.1;margin:8px 0}"
        + ".outcome h2{font-size:18px}.outcome:focus{outline:none}"
        + "@media(hover:hover) and (pointer:fine){#keyboard{display:none}}";

    /// <summary>
    /// The view's one script: the server's browser drawn on the canvas, and what the person does sent back in the page's own pixels. Pointer and wheel on the canvas; a tap is a click and a
    /// drag scrolls on a touch screen; keys and text through a hidden field that keeps an input method's composition whole and sends what it makes as text; paste as text; Done and Cancel.
    /// Nothing is sent anywhere but this page's own socket. Allowed by its hash (<see cref="SignInScriptHash"/>), so no other script can run.
    /// </summary>
    internal const string SignInScript = """
(function(){
var v=document.getElementById('view');if(!v||!window.WebSocket)return;
function $(i){return document.getElementById(i)}
var svc=v.getAttribute('data-service'),tok=v.getAttribute('data-form'),ready=v.getAttribute('data-ready');
var cv=$('screen'),cx=cv.getContext('2d'),keys=$('keys'),st=$('status'),stage=$('stage'),frame=$('frame'),em=$('emoji'),emt=$('emoji-text'),out=$('outcome'),
outh=$('outcome-title'),outt=$('outcome-text'),bar=$('bar'),done=$('done'),cancel=$('cancel'),kb=$('keyboard'),zoom=$('zoom'),hint=$('hint');
var W=cv.width,H=cv.height,ended=false,shown=false,q=[],pend=null,timer=0,held=0,clicks=0,lastDown=0,lastAt=null,touch=null,downs={},composing=false,justComposed=false;
var ws=new WebSocket('wss://'+location.host+'/page/sign-in?service='+encodeURIComponent(svc)+'&form='+encodeURIComponent(tok)+'&grant='+encodeURIComponent(v.getAttribute('data-grant')));
ws.binaryType='blob';
function say(t){st.textContent=t}
function finish(title,text,ok){if(ended)return;ended=true;v.hidden=true;$('again').hidden=!!ok;
outh.textContent=title;outt.textContent=text;out.hidden=false;out.focus();try{ws.close(1000)}catch(e){}}
ws.onmessage=function(m){
if(typeof m.data!=='string'){paint(m.data);return}
var d;try{d=JSON.parse(m.data)}catch(e){return}
if(d.t==='size'){if(d.w>0&&d.h>0){W=d.w;H=d.h;if(cv.width!==W)cv.width=W;if(cv.height!==H)cv.height=H;fit()}}
else if(d.t==='signedIn'){frame.hidden=true;hint.hidden=true;bar.hidden=true;say(svc==='google'?'Signed in. Now your phone…':'Signed in. Connecting the account…')}
else if(d.t==='emoji'){emt.textContent=d.text;em.hidden=false;say('Waiting for your phone…')}
else if(d.t==='stage'){em.hidden=true;say(d.word==='encrypting'?'Connected. Turning on encrypted conversations…':'Connecting the account…')}
else if(d.t==='end'){finish(d.ok?'Connected':'Not connected',d.say||'',d.ok)}};
ws.onclose=function(){finish('Not connected','The connection to the server ended before the sign-in finished. Start it again.')};
var painting=false,next=null;
function paint(b){if(painting){next=b;return}painting=true;
createImageBitmap(b).then(function(i){cx.drawImage(i,0,0,cv.width,cv.height);if(i.close)i.close();if(!shown){shown=true;say(ready)}},function(){})
.then(function(){painting=false;if(next){var n=next;next=null;paint(n)}})}
function flush(){if(timer){clearTimeout(timer);timer=0}if(pend){q.push(pend);pend=null}
while(q.length){var c=q.splice(0,64);if(ws.readyState===1)ws.send(JSON.stringify({events:c}))}}
function send(e){if(pend){q.push(pend);pend=null}q.push(e);if(q.length>=64)flush();else if(!timer)timer=setTimeout(flush,16)}
function later(e){if(pend&&pend.type===e.type){if(e.type==='mouseWheel'){pend.deltaX+=e.deltaX;pend.deltaY+=e.deltaY;pend.x=e.x;pend.y=e.y}else pend=e}
else{if(pend)q.push(pend);pend=e}if(!timer)timer=setTimeout(flush,40)}
function at(e){var r=cv.getBoundingClientRect();return{x:Math.max(0,Math.min(W-1,(e.clientX-r.left)*W/r.width)),y:Math.max(0,Math.min(H-1,(e.clientY-r.top)*H/r.height))}}
function mods(e){return(e.altKey?1:0)|(e.ctrlKey?2:0)|(e.metaKey?4:0)|(e.shiftKey?8:0)}
function bit(b){return b===2?2:b===1?4:1}
function name(b){return b===2?'right':b===1?'middle':'left'}
function reset(){keys.value=' ';try{keys.setSelectionRange(1,1)}catch(e){}}
function focusKeys(){try{keys.focus({preventScroll:true})}catch(e){keys.focus()}reset()}
function mouse(type,p,b,count,m){return{kind:'mouse',type:type,x:p.x,y:p.y,button:b,buttons:held,clickCount:count,modifiers:m}}
cv.addEventListener('pointerdown',function(e){
if(e.pointerType==='touch'){touch={id:e.pointerId,x:e.clientX,y:e.clientY,lx:e.clientX,ly:e.clientY,t:Date.now(),moved:false,typing:document.activeElement===keys};return}
e.preventDefault();focusKeys();
var p=at(e),now=Date.now();
clicks=(now-lastDown<400&&lastAt&&Math.abs(p.x-lastAt.x)<5&&Math.abs(p.y-lastAt.y)<5)?Math.min(3,clicks+1):1;lastDown=now;lastAt=p;
try{cv.setPointerCapture(e.pointerId)}catch(x){}
send(mouse('mouseMoved',p,'none',0,mods(e)));held|=bit(e.button);send(mouse('mousePressed',p,name(e.button),clicks,mods(e)))});
cv.addEventListener('pointermove',function(e){
if(e.pointerType==='touch'){if(!touch||touch.id!==e.pointerId)return;
if(Math.abs(e.clientX-touch.x)+Math.abs(e.clientY-touch.y)>10)touch.moved=true;
if(touch.moved&&!stage.classList.contains('zoomed')){var r=cv.getBoundingClientRect(),s=H/r.height,p=at(e);
later({kind:'mouse',type:'mouseWheel',x:p.x,y:p.y,deltaX:(touch.lx-e.clientX)*s,deltaY:(touch.ly-e.clientY)*s,modifiers:0})}
touch.lx=e.clientX;touch.ly=e.clientY;return}
later(mouse('mouseMoved',at(e),'none',0,mods(e)))});
cv.addEventListener('pointerup',function(e){
if(e.pointerType==='touch'){var t=touch;touch=null;if(!t||t.id!==e.pointerId||t.moved||Date.now()-t.t>800)return;
var p=at(e);held=1;send(mouse('mouseMoved',p,'none',0,0));send(mouse('mousePressed',p,'left',1,0));held=0;send(mouse('mouseReleased',p,'left',1,0));flush();
if(t.typing)focusKeys();return}
held&=~bit(e.button);send(mouse('mouseReleased',at(e),name(e.button),clicks,mods(e)));flush()});
cv.addEventListener('pointercancel',function(e){if(e.pointerType==='touch'){touch=null;return}if(held){var p=at(e);held=0;send(mouse('mouseReleased',p,'left',1,0));flush()}});
cv.addEventListener('wheel',function(e){e.preventDefault();var s=e.deltaMode===1?40:e.deltaMode===2?800:1;var p=at(e);
later({kind:'mouse',type:'mouseWheel',x:p.x,y:p.y,deltaX:e.deltaX*s,deltaY:e.deltaY*s,modifiers:mods(e)})},{passive:false});
cv.addEventListener('contextmenu',function(e){e.preventDefault()});
var CK={Enter:1,Tab:1,Backspace:1,Delete:1,Escape:1,ArrowLeft:1,ArrowUp:1,ArrowRight:1,ArrowDown:1,Home:1,End:1,PageUp:1,PageDown:1};
function key(k,type,m){send({kind:'key',type:type,key:k,modifiers:m})}
function tap(k){key(k,'keyDown',0);key(k,'keyUp',0);flush()}
function text(t){t=String(t).slice(0,32768);var i=0;while(i<t.length){var j=Math.min(t.length,i+4096);var c=t.charCodeAt(j-1);if(j<t.length&&c>=0xD800&&c<=0xDBFF)j--;
send({kind:'text',text:t.slice(i,j)});i=j}flush()}
keys.addEventListener('keydown',function(e){
if(e.isComposing||composing||e.keyCode===229)return;
if((e.ctrlKey||e.metaKey)&&!e.altKey&&(e.key==='a'||e.key==='A')){e.preventDefault();key('a','keyDown',2);key('a','keyUp',2);flush();return}
if(CK[e.key]&&!(e.ctrlKey||e.metaKey||e.altKey)){e.preventDefault();downs[e.key]=1;key(e.key,'keyDown',e.shiftKey?8:0);flush()}});
keys.addEventListener('keyup',function(e){if(downs[e.key]){delete downs[e.key];e.preventDefault();key(e.key,'keyUp',e.shiftKey?8:0);flush()}});
keys.addEventListener('beforeinput',function(e){var t=e.inputType||'';
if(t==='insertCompositionText'||t==='deleteCompositionText'||t==='insertFromComposition'||t==='deleteByComposition'||t==='insertFromPaste'||t==='insertFromDrop')return;
e.preventDefault();
if(t==='insertText'||t==='insertReplacementText'){if(e.data)text(e.data)}
else if(t==='insertLineBreak'||t==='insertParagraph')tap('Enter');
else if(t==='deleteContentBackward')tap('Backspace');
else if(t==='deleteContentForward')tap('Delete')});
keys.addEventListener('compositionstart',function(){composing=true});
keys.addEventListener('compositionend',function(e){composing=false;if(e.data)text(e.data);justComposed=true;setTimeout(function(){justComposed=false;reset()},0)});
keys.addEventListener('input',function(){if(composing)return;if(justComposed){return}var s=keys.value;if(s===' ')return;
if(s.length>1&&s.charAt(0)===' ')text(s.slice(1));else if(s.length===0)tap('Backspace');reset()});
keys.addEventListener('paste',function(e){e.preventDefault();var d=e.clipboardData||window.clipboardData;var t=d?d.getData('text'):'';if(t)text(t)});
kb.addEventListener('click',focusKeys);
function fit(){if(stage.classList.contains('zoomed'))cv.style.width=W+'px';else cv.style.width=''}
zoom.addEventListener('click',function(){var z=stage.classList.toggle('zoomed');zoom.setAttribute('aria-pressed',z?'true':'false');zoom.textContent=z?'Fit':'Zoom in';fit();
if(z)stage.scrollLeft=Math.max(0,(stage.scrollWidth-stage.clientWidth)/2)});
if(stage.clientWidth&&stage.clientWidth<600)zoom.click();
if(done)done.addEventListener('click',function(){send({kind:'done'});flush();done.disabled=true;say('Closing the browser and reading the sign-in…')});
cancel.addEventListener('click',function(){finish('Cancelled','Nothing was changed. The server closed its browser.')});
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="SignInScript"/> run, and nothing else.</summary>
    internal static readonly string SignInScriptHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(SignInScript)));
}
