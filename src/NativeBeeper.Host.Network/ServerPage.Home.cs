using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.Core.Services;
using NativeBeeper.Host.Backup;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// The front page: what needs the owner, the accounts and devices, what runs by itself, and the server. A line opens in place through the address (`/?open=messenger`), which
// the page's reload keeps; a `<details>` would close on every reload. The larger subjects each have a page of their own under /manage, which reload only while waiting on something.
internal sealed partial class ServerPage
{
    // The logo and the services' marks, as Settings on the PC shows them, in one picture the page draws parts of. It has to be one: the door takes two connections from an address at
    // a time and drops the rest unread (MaxPendingPerAddress), and every answer here closes its connection, so a browser asking for five pictures at once got two per reload.
    // Nothing of the server's, so like the logo it needs no sign-in.
    internal const string PicturesPath = "/page-pictures.png";

    private static readonly Lazy<byte[]> Pictures = new(() => Picture("server-page-pictures.png"));

    private static readonly string[] ServiceOrder = [ServerAccountProviders.GoogleMessages, ServerAccountProviders.Messenger, ServerAccountProviders.GoogleVoice, ServerAccountProviders.Instagram];

    // ---- The side list every signed-in page has ----

    private const string NavItem = "messages-nav";

    // Drawn in the page's text colour, 24 units square, as outlines.
    private static string Glyph(string paths) =>
        "<svg viewBox=\"0 0 24 24\" width=\"16\" height=\"16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + paths + "</svg>";

    private const string HomeGlyph = "<path d=\"M3.5 10.5 12 3.5l8.5 7V20a.5.5 0 0 1-.5.5h-5v-6h-6v6H4a.5.5 0 0 1-.5-.5z\"/>";
    private const string BellGlyph = "<path d=\"M6 9a6 6 0 0 1 12 0c0 6 2.5 8 2.5 8h-17S6 15 6 9\"/><path d=\"M10 20.5a2.2 2.2 0 0 0 4 0\"/>";
    private const string NetGlyph = "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M3.5 12h17M12 3.5c2.3 2.4 3.5 5.3 3.5 8.5s-1.2 6.1-3.5 8.5c-2.3-2.4-3.5-5.3-3.5-8.5S9.7 5.9 12 3.5z\"/>";
    private const string SyncGlyph = "<path d=\"M19.5 11A7.5 7.5 0 0 0 6 6.6L4.5 8\"/><path d=\"M4.5 3.5V8H9\"/><path d=\"M4.5 13A7.5 7.5 0 0 0 18 17.4l1.5-1.4\"/><path d=\"M19.5 20.5V16H15\"/>";
    private const string BoxGlyph = "<rect x=\"3.5\" y=\"4\" width=\"17\" height=\"5\" rx=\"1\"/><path d=\"M5 9v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9M10 13h4\"/>";
    private const string DiskGlyph = "<ellipse cx=\"12\" cy=\"6\" rx=\"7.5\" ry=\"2.5\"/><path d=\"M4.5 6v12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5V6M4.5 12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5\"/>";
    private const string LogGlyph = "<path d=\"M14 3.5H6.5a1 1 0 0 0-1 1v15a1 1 0 0 0 1 1h11a1 1 0 0 0 1-1V8z\"/><path d=\"M14 3.5V8h4.5M9 13h6M9 16.5h6\"/>";
    private const string DotsGlyph = "<circle cx=\"5.5\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"12\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/><circle cx=\"18.5\" cy=\"12\" r=\"1.4\" fill=\"currentColor\" stroke=\"none\"/>";
    private const string InfoGlyph = "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 11v5.5M12 7.6v.1\"/>";
    private const string OutGlyph = "<path d=\"M9.5 20.5h-4a1 1 0 0 1-1-1v-15a1 1 0 0 1 1-1h4M15.5 16.5 20 12l-4.5-4.5M20 12H9.5\"/>";

    // Which entry a path belongs to: a page under one of them (the tailnet's steps, a backup's restore) keeps its entry lit.
    private static string Place(string path) => path switch
    {
        "/manage/notifications" or "/push" or "/push/test" => "/manage/notifications",
        "/manage/tailscale" or "/manage/tailscale/about" => "/manage/tailscale",
        _ when IsTailnetPath(path) => "/manage/tailscale",
        "/manage/sync" or "/mirror" => "/manage/sync",
        "/manage/backups" => "/manage/backups",
        _ when IsBackupPath(path) && path != "/reset" => "/manage/backups",
        "/manage/storage" => "/manage/storage",
        "/log" => "/log",
        _ => "/",
    };

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
        "account_gone" => "The server doesn't hold that account any more.",
        "account_moving" => "That account is part of a move between this server and a PC, so it can't be removed yet.",
        "account_unpair" => UnpairGoogle,
        "joined" => "The device joined. It can use the server now.",
        "declined" => "The request was turned away.",
        "join_gone" => "That request isn't waiting any more: it was answered, it ran out, or the device stopped asking.",
        "push_on" => "Notifications are on. Phones with Message Link open sign up now, the others when Message Link on them next connects. Send a test below to be sure.",
        "push_on_unchecked" => "Notifications are on, but the server couldn't reach Google to check the key just now. Send a test below; if it doesn't arrive, add the files again.",
        "backup_started" => "Backing up now. Reload in a minute to see it done.",
        "backup_busy" => "A backup is already running, or there's no password yet.",
        "backup_checking" => "Reading the latest backup back now. Reload in a minute to see what it found.",
        "backup_password" => "The backup password is set. Keep it safe: without it the backups can't be read.",
        "desktop_sign_in" => "The sign-in opened in Message Link on this PC.",
        "desktop_sign_in_failed" => "Message Link couldn't be opened on this PC to sign in. Open it from the Start menu and connect the account in Settings.",
        _ => TailnetNotice(done),
    };

    // ---- The pages under /manage ----

    private static bool IsManagePath(string? path) => path is "/manage/notifications" or "/manage/tailscale" or "/manage/tailscale/about" or "/manage/backups" or "/manage/storage" or "/manage/sync";

    private async Task ManageAsync(HttpContext context, string path, string token)
    {
        var card = new StringBuilder();
        string title;
        // Each looks again by itself, as the front page does; only the Windows server's typed backup folder must not be reloaded away.
        int? refresh = 30;
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
                await BackupsCardAsync(card, token, context.Request, context.RequestAborted).ConfigureAwait(false);
                refresh = null; // A refresh must not erase a share sign-in being entered.
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
            default:
                Redirect(context.Response, "/");
                return;
        }
        var said = DoneWords(context.Request.Query["done"].ToString());
        var body = (said is null ? "" : Notice(said)) + "<div class=\"manage\">" + card + "</div>";
        await PageAsync(context, StatusCodes.Status200OK, title, body, refresh: refresh, refreshTo: path).ConfigureAwait(false);
    }

    // ---- The front page ----

    private sealed record HomeParts(string Body, int Refresh, string RefreshTo);

    private static string Row(string name, string? under, string? value, string? action, string? icon = null, bool warn = false, string? id = null) =>
        new StringBuilder("<div class=\"row\"").Append(id is null ? "" : " id=\"" + Html(id) + "\"").Append('>').Append(icon ?? "")
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
    private static string Go(string name, string? under, string? value, string href, string label, string? icon = null, bool warn = false, string? id = null, bool? expanded = null) =>
        new StringBuilder("<a class=\"row go\" href=\"").Append(Html(href)).Append('"').Append(id is null ? "" : " id=\"" + Html(id) + "\"")
            .Append(expanded is { } open ? " aria-expanded=\"" + (open ? "true" : "false") + "\"" : "").Append(" aria-label=\"").Append(Html(label)).Append("\">").Append(icon ?? "")
            .Append("<div class=\"g\">").Append(Html(name)).Append(under is null ? "" : "<small>" + Html(under) + "</small>").Append("</div>")
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
if(v&&v.p===p&&!location.hash)window.scrollTo(0,v.y);
window.addEventListener('pagehide',function(){s.setItem(k,JSON.stringify({p:p,y:window.scrollY}))});
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

    private static string? AccountSignInKey(string service, ServerAccount account) => service != RemoteSignInServices.Voice && NativeBeeper.Core.Messaging.AccountNamespace.ValidKey(account.Key) ? account.Key : null;
    private static string SignInHref(string service, string? account = null) => SignInPath + "?service=" + service + (account is null ? "" : "&amp;account=" + account);

    private static string ServiceOf(string provider) => SignInServiceList.FirstOrDefault(item => item.Provider == provider).Service ?? "";

    private async Task<HomeParts> HomeBodyAsync(HttpRequest request, string token, string done, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        if (DoneWords(done) is { } said) body.Append(Notice(said));
        if (backups is not null && ServerRestore.LastOutcome(backups.Service.Paths) is { } outcome && time.GetUtcNow() - outcome.At < TimeSpan.FromDays(2))
            body.Append(Notice(OutcomeWords(outcome)));

        HostStatus? status = null;
        IReadOnlyList<ServerAccount>? accounts = null;
        if (admin is not null)
        {
            try { status = await admin.StatusAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
            try { accounts = await admin.AccountsAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
        }
        var ordered = (accounts ?? []).OrderBy(account => Array.IndexOf(ServiceOrder, account.Provider) is var at and >= 0 ? at : ServiceOrder.Length).ToArray();
        var open = request.Query["open"].ToString();
        if (!(ServiceOrder.Contains(open) || ordered.Any(account => AccountId(account) == open) || devices.Has(open))) open = "";
        // A link to a service (`/?section=google-voice`) opens its account when it has just the one.
        if (ServiceOrder.Contains(open) && ordered.Where(account => account.Provider == open).ToArray() is [var only]) open = AccountId(only);

        var waiting = joins.Waiting();
        Attention(body, waiting, ordered, status, token);
        await ChecklistAsync(body, token, cancellationToken).ConfigureAwait(false);

        AccountsSection(body, accounts, ordered, open, request.HttpContext, token);
        DevicesSection(body, open, token);
        await ServerSectionAsync(body, cancellationToken).ConfigureAwait(false);
        AboutSection(body, request, status, open, token);
        body.Append("<script>").Append(PageScript).Append("</script>");

        // Every 5 seconds while the server waits to be signed in to Tailscale, every 10 while a device waits to join (so its going is seen), every 30 otherwise (so a new one shows up without a click).
        return new(body.ToString(), waiting.Count > 0 ? 10 : TailnetWaiting ? 5 : 30,
            open.Length > 0 ? "/?open=" + Uri.EscapeDataString(open) : "/");
    }

    // Only what needs someone now; nothing at all when nothing does.
    private void Attention(StringBuilder body, IReadOnlyList<JoinRequest> waiting, ServerAccount[] ordered, HostStatus? status, string token)
    {
        var rows = new StringBuilder();
        // Deny is on the page Review opens, with the rest of the answer.
        foreach (var asking in waiting)
            rows.Append(Row($"{asking.DeviceName} wants to join", null, "Code " + asking.Code, "<a class=\"button\" href=\"/joins/approve?id=" + Uri.EscapeDataString(asking.Id) + "\">Review</a>"));
        foreach (var account in ordered.Where(account => account.State == ServerAccountStates.Saved))
        {
            var service = ServiceOf(account.Provider);
            var offered = DesktopSignIn is null && signInServices is { } services && RemoteSignInSessions.Offers(services, service);
            rows.Append(Row($"{Provider(account.Provider)} isn't connected", Who(account), null, offered ? "<a class=\"button\" href=\"" + SignInHref(service, AccountSignInKey(service, account)) + "\">Sign in</a>" : null, Icon(account.Provider)));
        }
        if (tailnet?.Latest is { NeedsAttention: true } view)
        {
            // Key expiry left on goes silent until the phone can't reach the server, so its last month names the day (owner, 2026-11-10).
            var soon = view is { Running: true, ExpiryOff: false } && DateTimeOffset.TryParse(view.Expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && at - time.GetUtcNow() < TimeSpan.FromDays(30) ? at : (DateTimeOffset?)null;
            rows.Append(Row(view.State == "NeedsLogin" ? "Tailscale needs signing in" : view.NeedsApproval ? "Tailscale needs the server approved"
                : soon is { } day ? $"Tailscale signs the server out on {day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : "Tailscale has a step left",
                soon is null ? null : "Disable key expiry in Tailscale to keep it signed in.", null, "<a class=\"button\" href=\"/manage/tailscale\">Review</a>"));
        }
        if (pushes is { Switch.Current: not null } card)
            foreach (var phone in card.Health.Status(true, card.Registry, devices.List(null)).Phones ?? [])
                if (phone.State is PushPhoneStates.Failing or PushPhoneStates.MissingApp or PushPhoneStates.NotSignedUp)
                    rows.Append(Row($"{phone.Name} isn't getting notifications", null, null, "<a class=\"button\" href=\"/manage/notifications\">Review</a>"));
        if (backups is not null)
        {
            var state = backups.Service.Status();
            var problem = state.State switch
            {
                ServerBackupStates.Failing => "The last backup failed",
                ServerBackupStates.NoPassword => "Backups need a password",
                _ when state.Stale => "No backup has worked for two days",
                _ => null,
            };
            if (ServerRestore.ResetPlanned(backups.Service.Paths)) problem = "A reset waits for the server to start again";
            else if (ServerRestore.Planned(backups.Service.Paths) && !ServerRestore.IsUnfinished(backups.Service.Paths)) problem = "A restore waits for the server to start again";
            if (problem is not null) rows.Append(Row(problem, state.State == ServerBackupStates.Failing ? ServerFailure(state.Failure) : null, null, "<a class=\"button\" href=\"/manage/backups\">Review</a>"));
        }
        // Only said when something is wrong: a running sandbox, or one turned off by whoever installed the server, is nothing a person needs to read.
        if (status?.ChromeSandbox is ChromeSandboxWords.Inactive)
            rows.Append(Row("Google Chrome", "Its sandbox isn't working, so the pages it opens aren't kept apart from the rest of the server.", null, null));
        if (rows.Length == 0) return;
        body.Append("<section class=\"sec\" id=\"attention\"><div class=\"sh\"><h2 class=\"warn\">Needs attention</h2></div><div class=\"box alert\">").Append(rows).Append("</div></section>");
    }

    private void AccountsSection(StringBuilder body, IReadOnlyList<ServerAccount>? accounts, ServerAccount[] ordered, string open, HttpContext context, string token)
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
            foreach (var provider in ServiceOrder.Concat(ordered.Select(account => account.Provider).Except(ServiceOrder)))
            {
                var service = ServiceOf(provider);
                var offered = !desktop && services is not null && RemoteSignInSessions.Offers(services, service);
                var name = Provider(provider);
                var mine = ordered.Where(account => account.Provider == provider).ToArray();
                if (mine.Length == 0)
                {
                    // A service with no account: Connect in its line when this server can sign in to it here, otherwise only that it isn't connected.
                    var under = desktop || offered || !AnyBrowser(services) ? null : Elsewhere(service, services);
                    body.Append(offered
                        ? Row(name, "Not set up", null, "<a class=\"button quiet\" href=\"" + SignInHref(service) + "\" aria-label=\"" + Html("Connect: " + name) + "\">Connect</a>", Icon(provider), id: "row-" + provider)
                        : Row(name, under, "Not connected", Act(""), Icon(provider), id: "row-" + provider));
                    continue;
                }
                // Where a link to the service lands (SectionPath), when its accounts' own lines are named by their keys.
                if (AccountId(mine[0]) != provider) body.Append("<div id=\"row-").Append(Html(provider)).Append("\"></div>");
                foreach (var account in mine)
                {
                    var id = AccountId(account);
                    var items = new List<string>();
                    if (offered)
                    {
                        items.Add(Item(SignInHref(service, AccountSignInKey(service, account)), service == RemoteSignInServices.Voice ? "Add a number…" : "Sign in again…", "Sign in: " + name));
                        if (service != RemoteSignInServices.Voice && SignInMany(service)) items.Add(Item(SignInHref(service, "new"), "Add account…", "Add account: " + name));
                    }
                    if (account.Removable) items.Add(Item("/accounts/remove?id=" + Html(Uri.EscapeDataString(id)), "Remove…", "Remove " + AccountLabel(account), danger: true));
                    else items.Add(MenuNote(provider == ServerAccountProviders.GoogleMessages ? UnpairGoogle : "An account being moved between this server and a PC can't be removed until the move finishes."));
                    body.Append(Row(name, Who(account), Word(account.State), Act(Menu("Manage " + AccountLabel(account), open == id, items)), Icon(provider), account.State == ServerAccountStates.Saved, "row-" + id));
                }
            }
        }
        body.Append("</div>");
        if (accounts is not null && signInServices?.SignIns.OpenOn is { } busy) body.Append(Notice(BusyWords(busy)));
        // The Windows server (10.7) signs in with Messages' native windows on its own desktop; a server with no browser at all says so once.
        if (DesktopSignIn is not null) ConnectList(body, accounts, walkthrough: false, context, token);
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

    private string BackupLine(ServerBackupStatus status)
    {
        if (!status.HasFolder) return "No folder";
        if (!status.Enabled) return "Off";
        if (status.State == ServerBackupStates.Running) return "Backing up now";
        if (!status.HasPassword) return "Needs a password";
        if (status.State == ServerBackupStates.Failing) return "Failed";
        return status.LastGood is { } good ? Ago(good) + " · " + BackupSettings.Size(status.Bytes) : "None yet";
    }

    // What the server does and holds: each has a page of its own.
    private async Task ServerSectionAsync(StringBuilder body, CancellationToken cancellationToken)
    {
        if (pushes is null && tailnet is null && storage is null && history is null && backups is null) return;
        body.Append("<section class=\"sec\" id=\"server\"><div class=\"sh\"><h2>Server</h2></div><div class=\"box\">");
        if (pushes is not null)
        {
            string value;
            if (pushes.Switch.Current is null) value = "Off";
            else
            {
                var signedUp = (pushes.Health.Status(true, pushes.Registry, devices.List(null)).Phones ?? []).Count(phone => phone.State is PushPhoneStates.On or PushPhoneStates.Failing);
                value = signedUp == 0 ? "On · no phone yet" : signedUp == 1 ? "On · 1 phone" : $"On · {signedUp} phones";
            }
            body.Append(Go("Notifications", "Push notifications to your phones.", value, "/manage/notifications", "Notifications"));
        }
        if (tailnet is not null)
            body.Append(Go("Remote access", "Reach the server from anywhere.", !tailnet.Enabled ? "Off" : tailnet.Latest switch { null => "Starting", { Running: true } => "Connected", { State: "NeedsLogin" } => "Signed out", _ => "Not connected" },
                "/manage/tailscale", "Remote access", warn: tailnet.Enabled && tailnet.Latest is { State: "NeedsLogin" }));
        if (history is not null)
        {
            HistoryMirrorResult? mirror = null;
            try { mirror = await history.MirrorAsync(new HistoryMirrorParams(), cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
            body.Append(Go("Sync check", "Keeps the server's copy matching each service.", SyncLine(mirror), "/manage/sync", "Sync check"));
        }
        if (backups is not null)
        {
            var state = backups.Service.Status();
            body.Append(Go("Daily backup", "Conversations, settings and devices.", BackupLine(state), "/manage/backups", "Daily backup", warn: state.State is ServerBackupStates.Failing or ServerBackupStates.NoPassword || state.Stale));
        }
        if (storage is not null)
            body.Append(Go("Storage", "Disk space used.", storage.Current() is { } sizes ? ServerStorage.Size(sizes.Total) : "Measuring…", "/manage/storage", "Storage"));
        body.Append("</div></section>");
    }

    // What the server is, where its source is, and starting it again empty.
    private void AboutSection(StringBuilder body, HttpRequest request, HostStatus? status, string open, string token)
    {
        body.Append("<section class=\"sec\" id=\"about\"><div class=\"sh\"><h2>About</h2></div><div class=\"box\">");
        // The version alone: how the server is updated is its host's schedule, which it cannot know, and its protocol and build are in its log. Nor does it say nightly
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
