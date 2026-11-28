using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using MessageLink.App.Services.Backup;
using MessageLink.Core.Services;
using MessageLink.Server.Backup;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

// The front page: what needs the owner, the accounts and devices, what runs by itself, and the server. A line opens in place through the address (`/?open=messenger`), which
// the page's reload keeps; a `<details>` would close on every reload. The larger subjects each have a page of their own under /manage, which reload only while waiting on something.
internal sealed partial class ServerPage
{
    // The logo and the services' marks, as Settings on the PC shows them, in one picture the page draws parts of. It has to be one: the door takes two connections from an address at
    // a time and drops the rest unread (MaxPendingPerAddress), and every answer here closes its connection, so a browser asking for five pictures at once got two per reload.
    // Nothing of the server's, so like the logo it needs no sign-in. Its pictures are stacked square, and the page's `.ic` rule names how many (600% for six) once, for
    // every size: the logo's own sizes kept the five-picture height after Hermes's mark made six, which squashed it with the next picture's top showing under it.
    internal const string PicturesPath = "/page-pictures.png";

    private static readonly Lazy<byte[]> Pictures = new(() => Picture("server-page-pictures.png"));


    // ---- The side list every signed-in page has ----

    private const string NavItem = "messages-nav";

    // Drawn in the page's text colour, 24 units square, as outlines.
    private static string Glyph(string paths, int size = 16) =>
        "<svg viewBox=\"0 0 24 24\" width=\"" + size + "\" height=\"" + size + "\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + paths + "</svg>";

    private const string HomeGlyph = "<path d=\"M3.5 10.5 12 3.5l8.5 7V20a.5.5 0 0 1-.5.5h-5v-6h-6v6H4a.5.5 0 0 1-.5-.5z\"/>";
    private const string BellGlyph = "<path d=\"M6 9a6 6 0 0 1 12 0c0 6 2.5 8 2.5 8h-17S6 15 6 9\"/><path d=\"M10 20.5a2.2 2.2 0 0 0 4 0\"/>";
    private const string NetGlyph = "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M3.5 12h17M12 3.5c2.3 2.4 3.5 5.3 3.5 8.5s-1.2 6.1-3.5 8.5c-2.3-2.4-3.5-5.3-3.5-8.5S9.7 5.9 12 3.5z\"/>";
    private const string SyncGlyph = "<path d=\"M19.5 11A7.5 7.5 0 0 0 6 6.6L4.5 8\"/><path d=\"M4.5 3.5V8H9\"/><path d=\"M4.5 13A7.5 7.5 0 0 0 18 17.4l1.5-1.4\"/><path d=\"M19.5 20.5V16H15\"/>";
    private const string BoxGlyph = "<rect x=\"3.5\" y=\"4\" width=\"17\" height=\"5\" rx=\"1\"/><path d=\"M5 9v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9M10 13h4\"/>";
    private const string DiskGlyph = "<ellipse cx=\"12\" cy=\"6\" rx=\"7.5\" ry=\"2.5\"/><path d=\"M4.5 6v12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5V6M4.5 12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5\"/>";
    private const string LogGlyph = "<path d=\"M14 3.5H6.5a1 1 0 0 0-1 1v15a1 1 0 0 0 1 1h11a1 1 0 0 0 1-1V8z\"/><path d=\"M14 3.5V8h4.5M9 13h6M9 16.5h6\"/>";
    private const string DotsGlyph = "<circle cx=\"5.5\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"12\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"18.5\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/>";
    private const string InfoGlyph = "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 11v5.5M12 7.6v.1\"/>";
    private const string BackGlyph = "<path d=\"M20 12H4.5M10.5 6l-6 6 6 6\"/>";
    private const string OutGlyph ="<path d=\"M9.5 20.5h-4a1 1 0 0 1-1-1v-15a1 1 0 0 1 1-1h4M15.5 16.5 20 12l-4.5-4.5M20 12H9.5\"/>";

    // The page's list of places, as the PC's Settings has down its side: the front page, each subject with a page of its own, the log, and signing out.
    private string Nav(string path, string token)
    {
        var here = Place(path);
        var nav = new StringBuilder("<aside class=\"side\"><a class=\"brand\" href=\"/\"><span class=\"ic logo\" aria-hidden=\"true\"></span>").Append(SiteName).Append("</a><nav aria-label=\"The server's pages\">");
        void Entry(string href, string glyph, string name) =>
            nav.Append("<a href=\"").Append(href).Append('"').Append(href == here ? " class=\"here\" aria-current=\"page\"" : "").Append('>').Append(Glyph(glyph)).Append("<span>").Append(name).Append("</span></a>");
        Entry("/", HomeGlyph, "Overview");
        if (pushes is not null || tailnet is not null || history is not null || backups is not null || storage is not null) nav.Append("<p class=\"grp\">Server</p>");
        if (pushes is not null) Entry("/manage/notifications", BellGlyph, "Notifications");
        if (tailnet is not null) Entry("/manage/tailscale", NetGlyph, "Remote access");
        if (history is not null) Entry("/manage/sync", SyncGlyph, "Sync check");
        if (backups is not null) Entry("/manage/backups", BoxGlyph, "Backups");
        if (storage is not null) Entry("/manage/storage", DiskGlyph, "Storage");
        nav.Append("<p class=\"grp\">Help</p>");
        Entry("/log", LogGlyph, "Log");
        return nav.Append("</nav><form class=\"out\" method=\"post\" action=\"/logout\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token))
            .Append("\"><button type=\"submit\">").Append(Glyph(OutGlyph)).Append("<span>Sign out</span></button></form></aside>").ToString();
    }

    // What a form that came back to the page says, once.
    private static string? DoneWords(string done) => done switch
    {
        "removed" => "The device was removed. It stops connecting at once, and nothing on it was deleted.",
        "gone" => "That device isn't paired with the server any more.",
        "renamed" => "The device was renamed.",
        "account_removed" => "The account was removed from the server.",
        "google_removed" => "Google Messages was removed from the server. If your phone still lists the server under Device pairing, remove it there too.",
        "account_gone" => "The server doesn't hold that account any more.",
        "account_unpair" => UnpairGoogle,
        "joined" => "The device joined. It can use the server now.",
        "declined" => "The request was turned away.",
        "join_gone" => "That request isn't waiting any more: it was answered, it ran out, or the device stopped asking.",
        "push_on" => "Notifications are on. Phones with Message Link open sign up now, the others when Message Link on them next connects. Send a test below to be sure.",
        "push_on_unchecked" => "Notifications are on, but the server couldn't reach Google to check the key just now. Send a test below; if it doesn't arrive, add the files again.",
        // The Backups screen shows a backup or a check running, and its end, by itself.
        "backup_started" or "backup_checking" or "backup_watch" or "check_watch" => null,
        "backup_busy" => "A backup is already running, or there's no password yet.",
        "backup_password" => "The backup password is set. Keep it safe: without it the backups can't be read.",
        "desktop_sign_in" => "The sign-in opened in Message Link on this PC.",
        "desktop_sign_in_busy" => "A sign-in is already in progress on this PC. Finish it, or try again in a minute.",
        "desktop_sign_in_failed" => "Message Link couldn't be opened on this PC to sign in. Open it from the Start menu and connect the account in Settings.",
        "hermes_saved" => "Saved. Each assistant is checked now.",
        "reports_off" => "Turned off. That assistant can no longer message you first.",
        _ => TailnetNotice(done),
    };

    // ---- The pages under /manage ----

    private static IEnumerable<Route> ManageRoutes =>
    [.. new (string Path, string Place)[]
        {
            ("/manage/notifications", "/manage/notifications"), ("/manage/tailscale", "/manage/tailscale"), ("/manage/tailscale/about", "/manage/tailscale"),
            ("/manage/backups", "/manage/backups"), ("/manage/storage", "/manage/storage"), ("/manage/sync", "/manage/sync"), ("/manage/hermes", "/"),
        }.Select(route => new Route(route.Path, Verbs.Get, (page, call) => page.ManageAsync(call.Context, call.Path, call.Token)) { Place = route.Place })];

    private async Task ManageAsync(HttpContext context, string path, string token)
    {
        var card = new StringBuilder();
        string title;
        // Each looks again by itself, as the front page does; only the Windows server's typed backup folder must not be reloaded away.
        int? refresh = 30;
        string? watchTo = null;
        switch (path)
        {
            case "/manage/notifications" when pushes is not null:
                title = "Notifications";
                PushCard(card, pushes, token);
                if (pushes.Health.Test is { State: PushTestState.Waiting }) refresh = 2;
                break;
            case "/manage/tailscale" when tailnet is not null:
                title = "Remote access";
                TailnetCard(card, tailnet, token);
                if (TailnetWaiting) refresh = 5;
                break;
            case "/manage/tailscale/about" when tailnet is not null:
                title = "How remote access works";
                TailnetAbout(card, context.Request.Query["from"] == "welcome");
                refresh = null;
                break;
            case "/manage/backups" when backups is not null:
                title = "Backups";
                // Only while a backup or a check runs, every 2 seconds, so it shows the end by itself.
                watchTo = await BackupsCardAsync(card, token, context.Request, context.RequestAborted).ConfigureAwait(false);
                refresh = watchTo is null ? null : 2;
                break;
            case "/manage/storage" when storage is not null:
                title = "Storage";
                StorageCard(card);
                // The first look starts the measurement: its answer comes a moment later.
                if (storage.Current() is null) refresh = 2;
                break;
            case "/manage/sync" when history is not null:
                title = "Sync check";
                if (await CopiesCardAsync(card, token, context.RequestAborted).ConfigureAwait(false)) refresh = 5;
                break;
            case "/manage/hermes" when Hermes is not null:
                title = "Hermes";
                if (HermesCard(card, token)) refresh = 5;
                break;
            default:
                Redirect(context.Response, "/");
                return;
        }
        var said = DoneWords(context.Request.Query["done"].ToString());
        var body = (said is null ? "" : Notice(said)) + "<div class=\"manage\">" + card + "</div>";
        await PageAsync(context, StatusCodes.Status200OK, title, body, refresh: refresh, refreshTo: watchTo ?? path).ConfigureAwait(false);
    }

    // ---- The front page ----

    private sealed record HomeParts(string Body, int Refresh, string RefreshTo);

    // `kind` adds a class to the line (`sub`: a line that belongs to the one above it, set in under it).
    private static string Row(string name, string? under, string? value, string? action, string? icon = null, bool warn = false, string? id = null, string? kind = null) =>
        new StringBuilder("<div class=\"row").Append(kind is null ? "" : " " + kind).Append('"').Append(id is null ? "" : " id=\"" + Html(id) + "\"").Append('>').Append(icon ?? "")
            .Append("<div class=\"g\">").Append(Html(name)).Append(under is null ? "" : "<small>" + Html(under) + "</small>").Append("</div>")
            .Append(value is null ? "" : warn ? "<span class=\"v warn\"><i class=\"dot wait\"></i>" + Html(value) + "</span>" : "<span class=\"v\">" + Html(value) + "</span>")
            .Append(action ?? "").Append("</div>").ToString();

    private static string Act(string inner) => "<span class=\"act\">" + inner + "</span>";

    private static string Icon(string provider) => provider switch
    {
        ServerAccountProviders.GoogleMessages => "<span class=\"ic gm\" aria-hidden=\"true\"></span>",
        ServerAccountProviders.Messenger => "<span class=\"ic me\" aria-hidden=\"true\"></span>",
        ServerAccountProviders.GoogleVoice => "<span class=\"ic gv\" aria-hidden=\"true\"></span>",
        ServerAccountProviders.Instagram => "<span class=\"ic ig\" aria-hidden=\"true\"></span>",
        _ => "",
    };

    // A line that goes somewhere: the whole line is the link, with a chevron where every line's control is. Its state stays in the state's column, like every other line's.
    // `kind` adds a class to the line (`tight`: a browser's list); `underClass` to the line under its name (`mono`: a path).
    private static string Go(string name, string? under, string? value, string href, string label, string? icon = null, bool warn = false, string? id = null, bool? expanded = null,
        string? kind = null, string? underClass = null) =>
        new StringBuilder("<a class=\"row go").Append(kind is null ? "" : " " + kind).Append("\" href=\"").Append(Html(href)).Append('"').Append(id is null ? "" : " id=\"" + Html(id) + "\"")
            .Append(expanded is { } open ? " aria-expanded=\"" + (open ? "true" : "false") + "\"" : "").Append(" aria-label=\"").Append(Html(label)).Append("\">").Append(icon ?? "")
            .Append("<div class=\"g\">").Append(Html(name)).Append(under is null ? "" : "<small" + (underClass is null ? "" : " class=\"" + underClass + "\"") + ">" + Html(under) + "</small>").Append("</div>")
            .Append(value is null ? "" : warn ? "<span class=\"v warn\"><i class=\"dot wait\"></i>" + Html(value) + "</span>" : "<span class=\"v\">" + Html(value) + "</span>")
            .Append("<span class=\"act\"><i class=\"chev\"></i></span></a>").ToString();

    // A line's own short list of what can be done with it, behind "⋯" at its end, as a settings window has. A <details>, so it works without the script, which only closes it
    // on a click elsewhere. A link naming the line (`/?open=messenger`, SectionPath) opens it.
    private static string Menu(string label, bool open, IEnumerable<string> items) =>
        "<details class=\"menu\"" + (open ? " open" : "") + "><summary aria-label=\"" + Html(label) + "\">" + Glyph(DotsGlyph) + "</summary><div class=\"pop\">" + string.Concat(items) + "</div></details>";

    private static string Item(string href, string text, string label, bool danger = false) =>
        "<a class=\"item" + (danger ? " danger" : "") + "\" href=\"" + href + "\" aria-label=\"" + Html(label) + "\">" + Html(text) + "</a>";

    // Element text, so an apostrophe needs no escaping and stays as written.
    private static string MenuNote(string text) => "<p>" + Html(text).Replace("&#39;", "'", StringComparison.Ordinal) + "</p>";

    // A switch: a form of one button that says which way it is and turns it the other way, by posting `name=value`.
    private static string Switch(string action, string token, bool on, string name, string value, string label) =>
        "<form method=\"post\" action=\"" + action + "\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\"><input type=\"hidden\" name=\"" + name + "\" value=\"" + Html(value) + "\">"
        + "<button type=\"submit\" class=\"switch\" role=\"switch\" aria-checked=\"" + (on ? "true" : "false") + "\" aria-label=\"" + Html(label) + "\"><span></span></button></form>";

    /// <summary>
    /// The front page's one script: where the page was scrolled, kept across a reload of the same page (a switch turned, a form answered, the page looking again by
    /// itself), so the page stays put; and a line's menu closed by a click elsewhere or Escape. Another page, or an address naming a place on it, starts as the browser would. Allowed by its hash (<see cref="PageScriptHash"/>).
    /// </summary>
    internal const string PageScript = """
(function(){
var s=window.sessionStorage,k='server-page-scroll',p=location.pathname,v=null;
try{v=JSON.parse(s.getItem(k)||'null')}catch(e){}
s.removeItem(k);
if('scrollRestoration' in history)history.scrollRestoration='manual';
var m=document.querySelector('html.in-window body.framed>.pane>main');
if(v&&v.p===p&&!location.hash){if(m)m.scrollTop=v.y;else window.scrollTo(0,v.y)}
window.addEventListener('pagehide',function(){s.setItem(k,JSON.stringify({p:p,y:m?m.scrollTop:window.scrollY}))});
function shut(but){document.querySelectorAll('details.menu[open]').forEach(function(d){if(d!==but)d.removeAttribute('open')})}
document.addEventListener('click',function(e){var d=e.target.closest?e.target.closest('details.menu'):null;shut(d)});
document.addEventListener('keydown',function(e){if(e.key==='Escape')shut(null)});
})();
""";

    /// <summary>The Content-Security-Policy source that lets <see cref="PageScript"/> run.</summary>
    internal static readonly string PageScriptHash = "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(PageScript)));

    // The line under an account's name: who it is, or until that is known (no conversation has loaded yet) what kind of account it is, as Settings on the PC says.
    private static string Who(ServerAccount account) => account.Name is { Length: > 0 } name ? name : account.Provider switch
    {
        ServerAccountProviders.GoogleMessages => "Paired phone",
        ServerAccountProviders.Messenger => "Facebook account",
        ServerAccountProviders.GoogleVoice => "Google Voice account",
        ServerAccountProviders.Instagram => "Instagram account",
        _ => "Account",
    };

    // What the page offers for each service and its accounts, as this server signs in and from where this browser asks.
    private SignInOffers Offers(IReadOnlyList<ServerAccount>? accounts, HttpContext context) => new(new SignInFacts
    {
        Accounts = accounts,
        Host = DesktopSignIn is not null ? SignInHost.Desktop : signInServices is not null ? SignInHost.Browser : SignInHost.None,
        BrowserOffers = service => signInServices is { } services && RemoteSignInSessions.Offers(services, service),
        ChoosesAccount = DesktopSignIn is not null ? DesktopAccountSignIn is not null : signInServices?.AccountSignIn is not null,
        OnThisPc = DesktopSignIn is not null && OnThisPc(context),
    });

    // The address itself, for what is escaped when it is drawn (ServerAttention's items).
    private static string SignInUrl(string service, string? account = null) => SignInPath + "?service=" + service + (account is null ? "" : "&account=" + account);

    private static string SignInHref(string service, string? account = null) => SignInUrl(service, account).Replace("&", "&amp;", StringComparison.Ordinal);


    private async Task<HomeParts> HomeBodyAsync(HttpRequest request, string token, string done, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        if (DoneWords(done) is { } said) body.Append(Notice(said));
        if (backups is not null && ServerRestore.LastOutcome(backups.Service.Paths) is { } outcome && time.GetUtcNow() - outcome.At < TimeSpan.FromDays(2))
            body.Append(Notice(OutcomeWords(outcome)));

        ServerStatus? status = null;
        IReadOnlyList<ServerAccount>? accounts = null;
        if (admin is not null)
        {
            try { status = await admin.StatusAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
            try { accounts = await admin.AccountsAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
        }
        var ordered = (accounts ?? []).OrderBy(account => SignInService.Order(account.Provider)).ToArray();
        var open = request.Query["open"].ToString();
        if (!(SignInService.All.Any(service => service.Provider == open) || ordered.Any(account => AccountId(account) == open) || devices.Has(open))) open = "";
        // A link to a service (`/?section=google-voice`) opens its account when it has just the one.
        if (SignInService.All.Any(service => service.Provider == open) && ordered.Where(account => account.Provider == open).ToArray() is [var only]) open = AccountId(only);

        var waiting = joins.Waiting();
        var progress = Progress(accounts?.Count);
        var signIns = Offers(ordered, request.HttpContext);
        var attention = new ServerAttention(AttentionFactsOf(waiting, ordered, signIns, status, progress), time.GetUtcNow());
        Attention(body, attention);
        Checklist(body, progress, token);

        AccountsSection(body, accounts, signIns, open, request.HttpContext, token);
        DevicesSection(body, open, token);
        await ServerSectionAsync(body, attention, cancellationToken).ConfigureAwait(false);
        AboutSection(body, request, status, open, token);
        body.Append("<script>").Append(PageScript).Append("</script>");

        // Every 5 seconds while the server waits to be signed in to Tailscale, every 10 while a device waits to join (so its going is seen), every 30 otherwise (so a new one shows up without a click).
        return new(body.ToString(), waiting.Count > 0 ? 10 : TailnetWaiting ? 5 : 30,
            open.Length > 0 ? "/?open=" + Uri.EscapeDataString(open) : "/");
    }

    // What ServerAttention works from, read from the server's parts as they are now.
    private AttentionFacts AttentionFactsOf(IReadOnlyList<JoinRequest> waiting, ServerAccount[] ordered, SignInOffers signIns, ServerStatus? status, SetupProgress progress)
    {
        var agents = Hermes?.State?.Agents ?? [];
        return new()
        {
            Joins = waiting,
            Unconnected = [.. ordered.Where(account => account.State == ServerAccountStates.Saved).Select(account =>
                new UnconnectedAccount(account.Provider, Provider(account.Provider), Who(account),
                    signIns.AttentionSignIn(account) is { } offer ? SignInUrl(SignInService.Of(account.Provider).Service, offer.Slot) : null))],
            TailnetEnabled = tailnet is { Enabled: true },
            Tailnet = tailnet?.Latest,
            PushesOffered = pushes is not null,
            PushesOn = progress.NotificationsOn,
            PhonesNotified = progress.PhonesNotified,
            Phones = pushes is { Switch.Current: not null } card ? card.Health.Status(true, card.Registry, devices.List(null)).Phones ?? [] : [],
            Backup = backups?.Service.Status(),
            ResetPlanned = backups is not null && ServerRestore.ResetPlanned(backups.Service.Paths),
            RestorePlanned = backups is not null && ServerRestore.Planned(backups.Service.Paths) && !ServerRestore.IsUnfinished(backups.Service.Paths),
            // The setup list says a missing folder itself while it shows (Checklist), so Needs attention doesn't say it twice (owner, 2026-11-26).
            ChecklistHasBackups = !File.Exists(Path.Combine(keysFolder, ChecklistFile)) && progress.Resume(lastStep) is not null && progress.ChecklistLeft.Any(item => item.Step == "backups"),
            ChromeSandboxInactive = status?.ChromeSandbox is ChromeSandboxWords.Inactive,
            HermesKeyRejected = agents.Any(agent => agent.Status == "key_rejected"),
            HermesNoProfile = agents.Any(agent => agent.Status == "no_profile"),
        };
    }

    // Only what needs someone now; nothing at all when nothing does. Internal for ServerAttentionChecks, which draws it.
    internal static void Attention(StringBuilder body, ServerAttention attention)
    {
        var rows = new StringBuilder();
        foreach (var item in attention.Items)
            rows.Append(Row(item.Title, item.Detail, item.Value, item.Href is null ? null : "<a class=\"button\" href=\"" + Html(item.Href) + "\">" + Html(item.Button) + "</a>",
                item.Provider switch { null => null, ServerAttention.HermesProvider => HermesMark, var provider => Icon(provider) }));
        if (rows.Length == 0) return;
        body.Append("<section class=\"sec\" id=\"attention\"><div class=\"sh\"><h2 class=\"warn\">Needs attention</h2></div><div class=\"box alert\">").Append(rows).Append("</div></section>");
    }

    private void AccountsSection(StringBuilder body, IReadOnlyList<ServerAccount>? accounts, SignInOffers signIns, string open, HttpContext context, string token)
    {
        body.Append("<section class=\"sec\" id=\"accounts\"><div class=\"sh\"><h2>Accounts</h2></div><div class=\"box\">");
        if (DesktopAccountSignIn is not null || signInServices?.AccountSignIn is not null)
            body.Append(Para("Before adding another account, update Message Link on every device. Older apps cannot connect after an additional account is added."));
        if (accounts is null) body.Append(Row("The server couldn't list its accounts just now.", null, null, null));
        else
        {
            var services = signInServices;
            var desktop = DesktopSignIn is not null;
            // Any other service a newer server lists goes after the four, rather than unseen.
            foreach (var offers in signIns.Services)
            {
                var (provider, service, name) = (offers.Service.Provider, offers.Service.Service, offers.Service.Name);
                // One offer as this host takes it: a link to the server's browser sign-in, or a form that opens Message Link's on this PC's desktop.
                string Draw(SignInOffer offer, string target, bool item) =>
                    desktop ? DesktopForm(token, offers.Service.Desktop!, offer.Slot, offer.Words, offer.Label(target), item)
                    : item ? Item(SignInHref(service, offer.Slot), offer.Words, offer.Label(target))
                    : "<a class=\"button quiet\" href=\"" + SignInHref(service, offer.Slot) + "\" aria-label=\"" + Html(offer.Label(target)) + "\">" + Html(offer.Words) + "</a>";
                if (offers.Accounts.Count == 0)
                {
                    // A service with no account: Connect in its line when this server can sign in to it here, otherwise only that it isn't connected.
                    var under = desktop || offers.Offered || !AnyBrowser(services) ? null : Elsewhere(service, services);
                    body.Append(offers.Connect is { } connect
                        ? Row(name, "Not set up", null, Draw(connect, name, item: false), Icon(provider), id: "row-" + provider)
                        : Row(name, under, "Not connected", Act(""), Icon(provider), id: "row-" + provider));
                    if (provider == ServerAccountProviders.Instagram) HermesLine(body);
                    continue;
                }
                // Where a link to the service lands (SectionPath), when its accounts' own lines are named by their keys.
                if (AccountId(offers.Accounts[0].Account) != provider) body.Append("<div id=\"row-").Append(Html(provider)).Append("\"></div>");
                foreach (var line in offers.Accounts)
                {
                    var account = line.Account;
                    var id = AccountId(account);
                    var items = new List<string>();
                    if (line.SignInAgain is { } again) items.Add(Draw(again, AccountLabel(account), item: true));
                    if (offers.Add is { } add) items.Add(Draw(add, name, item: true));
                    if (line.Removable) items.Add(Item("/accounts/remove?id=" + Html(Uri.EscapeDataString(id)), "Remove…", "Remove " + AccountLabel(account), danger: true));
                    else if (line.UnpairNote) items.Add(MenuNote(UnpairGoogle));
                    body.Append(Row(name, Who(account), Word(account.State), Act(Menu("Manage " + AccountLabel(account), open == id, items)), Icon(provider), account.State == ServerAccountStates.Saved, "row-" + id));
                }
                // Hermes after Instagram, before any service a newer server lists.
                if (provider == ServerAccountProviders.Instagram) HermesLine(body);
            }
        }
        if (accounts is null) HermesLine(body);
        body.Append("</div>");
        if (accounts is not null && signInServices?.SignIns.OpenOn is { } busy) body.Append(Notice(BusyWords(busy)));
        // The Windows server's sign-ins are in its lines on this PC only; a server with no browser at all says so once.
        if (DesktopSignIn is not null)
        {
            if (!OnThisPc(context)) body.Append(Para(DesktopElsewhere));
            // With no list there are no lines to put them in, and connecting doesn't wait on the list.
            else if (accounts is null) DesktopSignInForms(body, context, token, null);
        }
        else if (!AnyBrowser(signInServices)) body.Append("<p class=\"hint\">").Append(Html(NoSignIn)).Append("</p>");
        body.Append("</section>");
    }

    private void DevicesSection(StringBuilder body, string open, string token)
    {
        body.Append("<section class=\"sec\" id=\"devices\"><div class=\"sh\"><h2>Devices</h2></div><div class=\"box\">");
        var list = devices.List(null);
        if (list.Count == 0) body.Append(Row("No devices yet", "Add a phone or a PC below to start using the server.", null, Act("")));
        foreach (var device in list)
        {
            // What it runs under its name, and for a phone that saves contacts from PCs (protocol 29), that too.
            var parts = new[] { SystemName(devices.SystemOf(device.Id)), SavesContacts(device.Id) ? "Saves contacts from your PC" : null }.OfType<string>().ToArray();
            var id = Uri.EscapeDataString(device.Id);
            body.Append(Row(device.Name, parts.Length == 0 ? null : string.Join(" · ", parts), device.LastSeen is { } seen ? "Last seen " + Ago(seen) : "Never connected",
                Act(Menu("Manage " + device.Name, open == device.Id, [Item("/devices/details?id=" + id, "Details", "Details of " + device.Name),
                    Item("/devices/rename?id=" + id, "Rename…", "Rename " + device.Name), Item("/devices/remove?id=" + id, "Remove…", "Remove " + device.Name, danger: true)])),
                id: "row-" + device.Id));
        }
        // Asking to join is a way in for devices, so its switch sits with them. Turning it off leaves paired devices working.
        body.Append(Row("Requests to join", joins.Paused ? "New devices can't ask to join. Paired devices keep working." : "Let new devices ask to join.", joins.Paused ? "Off" : null,
            Switch("/joins/pause", token, !joins.Paused, "paused", joins.Paused ? "no" : "yes", "Requests to join"), id: "row-requests"));
        // Pages that ask for the password first (a GET, so nothing is done by following them).
        body.Append(Row("Add a device", "Connect a phone or a PC to this server.", null,
            "<span class=\"acts\"><a class=\"button quiet\" href=\"/phone\" aria-label=\"Add a phone\">Phone…</a><a class=\"button quiet\" href=\"/pair\" aria-label=\"Add a PC\">PC…</a></span>", id: "row-add-device"));
        body.Append("</div></section>");
    }

    private bool SavesContacts(string id) => pushes?.Contacts?.SavesContacts(id) == true;

    // What a device runs, as its hello said; a client from before it said so is named by its kind alone. Null until it has connected once since.
    private static string? SystemName(DeviceSystem? system) => system?.Os ?? system?.Client switch
    {
        "windows" => "Windows",
        "android" => "Android",
        _ => null,
    };

    // One device's own screen: what it runs, where it last connected from, and what can be done with it.
    private async Task DeviceDetailsAsync(HttpContext context, string id)
    {
        var device = devices.List(null).FirstOrDefault(found => found.Id == id);
        if (device is null)
        {
            Redirect(context.Response, "/?done=gone");
            return;
        }
        var system = devices.SystemOf(device.Id);
        var escaped = Uri.EscapeDataString(device.Id);
        var body = new StringBuilder("<div class=\"manage\"><section><div class=\"box\">");
        body.Append(Row("System", system?.OsVersion, SystemName(system) ?? "Not known yet", null));
        if (system?.Model is { } model) body.Append(Row("Model", null, model, null));
        if (system?.App is { } app) body.Append(Row("Message Link", null, "Version " + app, null));
        if (system is not null) body.Append(Row("Last connected from", null, system.ViaTailnet ? "Tailscale" : system.Address ?? "Not known", null));
        body.Append(Row("Last seen", null, device.LastSeen is { } seen ? When(seen) : "Never connected", null));
        body.Append(Row("Added", null, When(device.Created), null));
        if (SavesContacts(device.Id)) body.Append(Row("Contacts", null, "Saves contacts from your PC", null));
        body.Append("</div></section><section><div class=\"box\">");
        body.Append(Row("Rename", null, null, "<a class=\"button quiet\" href=\"/devices/rename?id=" + escaped + "\" aria-label=\"" + Html("Rename " + device.Name) + "\">Rename…</a>"));
        body.Append(Row("Remove", "It stops connecting at once. Nothing on it is deleted.", null,
            "<a class=\"button quiet danger\" href=\"/devices/remove?id=" + escaped + "\" aria-label=\"" + Html("Remove " + device.Name) + "\">Remove…</a>"));
        body.Append("</div></section></div>");
        await PageAsync(context, StatusCodes.Status200OK, device.Name, body.ToString(), refresh: 30, refreshTo: "/devices/details?id=" + escaped).ConfigureAwait(false);
    }

    // One line for the check across every service: what the newest round found, and when.
    private string SyncLine(HistoryMirrorResult? mirror)
    {
        if (mirror is null) return "Couldn't be read";
        if (mirror.Running) return "Checking now";
        if (mirror.Reports.Count == 0) return "Not checked yet";
        var fixedCount = mirror.Reports.Sum(report => report.Differed);
        return $"{(fixedCount == 0 ? "All match" : fixedCount == 1 ? "1 fixed" : $"{fixedCount} fixed")} · {Ago(mirror.Reports.Max(report => report.At))}";
    }


    // What the server does and holds: each has a page of its own.
    // Each line's word and warning are ServerAttention's, so a line warns exactly when Needs attention has an item for it.
    private async Task ServerSectionAsync(StringBuilder body, ServerAttention attention, CancellationToken cancellationToken)
    {
        if (pushes is null && tailnet is null && storage is null && history is null && backups is null) return;
        body.Append("<section class=\"sec\" id=\"server\"><div class=\"sh\"><h2>Server</h2></div><div class=\"box\">");
        if (pushes is not null)
        {
            // The phones notifications reach (or are failing to), not every phone that joined: SetupProgress.PhonesNotified.
            var line = attention.Notifications;
            body.Append(Go("Notifications", "Push notifications to your phones.", line.Value, "/manage/notifications", "Notifications", warn: line.Warn));
        }
        if (tailnet is not null)
        {
            var line = attention.RemoteAccess;
            body.Append(Go("Remote access", "Reach the server from anywhere.", line.Value, "/manage/tailscale", "Remote access", warn: line.Warn));
        }
        if (history is not null)
        {
            HistoryMirrorResult? mirror = null;
            try { mirror = await history.MirrorAsync(new HistoryMirrorParams(), cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
            body.Append(Go("Sync check", "Keeps the server's copy matching each service.", SyncLine(mirror), "/manage/sync", "Sync check"));
        }
        if (attention.Backup(Ago) is { } backupLine)
            body.Append(Go("Daily backup", "Conversations, settings and devices.", backupLine.Value, "/manage/backups", "Daily backup", warn: backupLine.Warn));
        if (storage is not null)
            body.Append(Go("Storage", "Disk space used.", storage.Current() is { } sizes ? ServerStorage.Size(sizes.Total) : "Measuring…", "/manage/storage", "Storage"));
        body.Append("</div></section>");
    }

    // What the server is, where its source is, and starting it again empty.
    private void AboutSection(StringBuilder body, HttpRequest request, ServerStatus? status, string open, string token)
    {
        body.Append("<section class=\"sec\" id=\"about\"><div class=\"sh\"><h2>About</h2></div><div class=\"box\">");
        // The version alone: how the server is updated is its server's schedule, which it cannot know, and its protocol and build are in its log. Nor does it say nightly
        // or stable: a promotion gives the nightly's very image the stable tags, so the image cannot tell which tag its server pulled (docs/release.md, "Server image channels").
        if (status is null) body.Append(Row("The server couldn't say what it is just now.", null, null, Act("")));
        else
        {
            body.Append(Row("Version", null, status.App, Act("")));
            body.Append(Row("Uptime", "Since the last restart.", Span(time.GetUtcNow() - status.Started) is { Length: > 0 } span ? span : "Just started", Act("")));
        }
        body.Append("<div class=\"row\"><div class=\"g\">Address<small>This server's IP address or host name.</small></div><span class=\"v copy\">").Append(Html(Address(request))).Append("</span>").Append(Act("")).Append("</div>");
        // The whole fingerprint, to read against what a PC shows.
        body.Append("<div class=\"row\" id=\"row-fingerprint\"><div class=\"g\">Fingerprint<small>Matches what a PC shows when it joins.</small><small class=\"copy\">").Append(Html(fingerprint())).Append("</small></div>").Append(Act("")).Append("</div>");
        // The AGPL offer to everyone who uses the server over the network: the source of the commit this image was built from (Core SourceCode).
        body.Append("<a class=\"row go\" href=\"").Append(Html(SourceCode.For(status?.App, status?.Revision))).Append("\" target=\"_blank\" rel=\"noopener noreferrer\"><div class=\"g\">Source code<small>On GitHub.</small></div>")
            .Append("<span class=\"act\"><i class=\"chev\"></i></span></a>");
        // The walkthrough from its first screen, always; and the front page's list back, once it was hidden (owner, 2026-11-10: hiding it had no way back).
        var hidden = File.Exists(Path.Combine(keysFolder, ChecklistFile));
        body.Append(Row("Setup", hidden ? "The list of setup steps is hidden." : "Go through setting up from the first step.", null,
            "<span class=\"acts\">" + (hidden ? Form("/checklist/show", token, "Show setup steps", quiet: true) : "") + "<a class=\"button quiet\" href=\"/welcome\">Open setup</a></span>", id: "row-setup"));
        if (backups is not null)
            body.Append(Row("Reset server", "Erase everything and start again. Backups are kept.", null, "<a class=\"button quiet danger\" href=\"/reset\">Reset…</a>"));
        body.Append("</div></section>");
    }
}
