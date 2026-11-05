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
    /// <summary>The look chosen on the page, kept with the keys.</summary>
    public const string ThemeFile = "server-page.theme";

    /// <summary>The looks the page offers, the first when none was chosen. Each is a set of colours in <see cref="Style"/>.</summary>
    internal static readonly (string Id, string Name)[] Themes =
        [("night", "Night console"), ("graphite", "Graphite"), ("aurora", "Aurora"), ("glass", "Glass"), ("phosphor", "Phosphor")];

    private const string ThemeItem = "messages-theme";

    // The services' own marks, as Settings on the PC shows them. Nothing of the server's, so like the logo they need no sign-in.
    private const string IconsPath = "/icons/";

    private static readonly Lazy<Dictionary<string, byte[]>> Icons = new(() => ServerAccountProviders.All.ToDictionary(provider => IconsPath + provider + ".png", provider => Picture("server-page-service-" + provider + ".png"), StringComparer.Ordinal));

    private static bool IsIconPath(string? path) => path is not null && path.StartsWith(IconsPath, StringComparison.Ordinal) && Icons.Value.ContainsKey(path);

    private static readonly string[] ServiceOrder = [ServerAccountProviders.GoogleMessages, ServerAccountProviders.Messenger, ServerAccountProviders.GoogleVoice, ServerAccountProviders.Instagram];

    private volatile string theme = Themes[0].Id;

    private void LoadTheme()
    {
        try
        {
            var file = Path.Combine(keysFolder, ThemeFile);
            if (File.Exists(file) && File.ReadAllText(file).Trim() is var kept && Themes.Any(known => known.Id == kept)) theme = kept;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"server page theme read=false type={failure.GetType().Name}"); }
    }

    // A look gives nobody anything, so the sign-in is enough, as a new device name is.
    private async Task ThemeAsync(HttpContext context, IFormCollection form)
    {
        var chosen = form["theme"].ToString();
        if (!Themes.Any(known => known.Id == chosen))
        {
            Redirect(context.Response, "/");
            return;
        }
        try { WriteNew(Path.Combine(keysFolder, ThemeFile), Encoding.ASCII.GetBytes(chosen), overwrite: true); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log($"server page theme kept=false type={failure.GetType().Name}");
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "Couldn't save the theme. Try again.").ConfigureAwait(false);
            return;
        }
        theme = chosen;
        Redirect(context.Response, "/?open=theme#row-theme");
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
        "push_on" => "Notifications are on. Phones with Messages open sign up now, the others when Messages on them next connects. Send a test below to be sure.",
        "push_on_unchecked" => "Notifications are on, but the server couldn't reach Google to check the key just now. Send a test below; if it doesn't arrive, add the files again.",
        "mirror" => "Checking now. Each service takes a few seconds per conversation; reload to see the results.",
        "backup_started" => "Backing up now. Reload in a minute to see it done.",
        "backup_busy" => "A backup is already running, or there's no password yet.",
        "backup_checking" => "Reading the latest backup back now. Reload in a minute to see what it found.",
        "backup_password" => "The backup password is set. Keep it safe: without it the backups can't be read.",
        "desktop_sign_in" => "The sign-in opened in Messages on this PC.",
        "desktop_sign_in_failed" => "Messages couldn't be opened on this PC to sign in. Open it from the Start menu and connect the account in Settings.",
        _ => TailnetNotice(done),
    };

    // ---- The pages under /manage ----

    private static bool IsManagePath(string? path) => path is "/manage/notifications" or "/manage/tailscale" or "/manage/backups" or "/manage/storage" or "/manage/sync";

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
                title = "Tailscale";
                TailnetCard(card, tailnet, token);
                if (TailnetWaiting) refresh = 5;
                break;
            case "/manage/backups" when backups is not null:
                title = "Backups";
                await BackupsCardAsync(card, token, context.Request, context.RequestAborted).ConfigureAwait(false);
                if (backups.Service.FolderChoiceFile is not null) refresh = null;
                break;
            case "/manage/storage" when storage is not null:
                title = "Storage";
                StorageCard(card);
                // The first look starts the measurement: its answer comes a moment later.
                if (storage.Current() is null) refresh = 2;
                break;
            case "/manage/sync" when history is not null:
                title = "Sync check";
                await CopiesCardAsync(card, token, context.RequestAborted).ConfigureAwait(false);
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

    private sealed record HomeParts(string Body, string Actions, int Refresh, string RefreshTo);

    private static string Row(string name, string? under, string? value, string? action, string? icon = null, bool warn = false, string? id = null) =>
        new StringBuilder("<div class=\"row\"").Append(id is null ? "" : " id=\"" + Html(id) + "\"").Append('>').Append(icon ?? "")
            .Append("<div class=\"g\">").Append(Html(name)).Append(under is null ? "" : "<small>" + Html(under) + "</small>").Append("</div>")
            .Append(value is null ? "" : warn ? "<span class=\"v warn\"><i class=\"dot wait\"></i>" + Html(value) + "</span>" : "<span class=\"v\">" + Html(value) + "</span>")
            .Append(action ?? "").Append("</div>").ToString();

    private static string Act(string inner) => "<span class=\"act\">" + inner + "</span>";

    private static string Icon(string provider) => ServerAccountProviders.All.Contains(provider) ? "<img class=\"ic\" src=\"" + IconsPath + provider + ".png\" alt=\"\">" : "";

    // A line that goes somewhere: the whole line is the link, with a chevron where every line's control is. Its state stays in the state's column, like every other line's.
    private static string Go(string name, string? under, string? value, string href, string label, string? icon = null, bool warn = false, string? id = null, bool? expanded = null) =>
        new StringBuilder("<a class=\"row go\" href=\"").Append(Html(href)).Append('"').Append(id is null ? "" : " id=\"" + Html(id) + "\"")
            .Append(expanded is { } open ? " aria-expanded=\"" + (open ? "true" : "false") + "\"" : "").Append(" aria-label=\"").Append(Html(label)).Append("\">").Append(icon ?? "")
            .Append("<div class=\"g\">").Append(Html(name)).Append(under is null ? "" : "<small>" + Html(under) + "</small>").Append("</div>")
            .Append(value is null ? "" : warn ? "<span class=\"v warn\"><i class=\"dot wait\"></i>" + Html(value) + "</span>" : "<span class=\"v\">" + Html(value) + "</span>")
            .Append("<span class=\"act\"><i class=\"chev\"></i></span></a>").ToString();

    // A line that opens in place, through the address, or closes again.
    private static string Opens(string id, string name, string? under, string? value, string label, string open, string? icon = null, bool warn = false) =>
        Go(name, under, value, (open == id ? "/#row-" : "/?open=" + Uri.EscapeDataString(id) + "#row-") + Uri.EscapeDataString(id), label, icon, warn, "row-" + id, open == id);

    // The line under an account's name: who it is, or until that is known (no conversation has loaded yet) what kind of account it is, as Settings on the PC says.
    private static string Who(ServerAccount account) => account.Name is { Length: > 0 } name ? name : account.Provider switch
    {
        ServerAccountProviders.GoogleMessages => "Paired phone",
        ServerAccountProviders.Messenger => "Facebook account",
        ServerAccountProviders.GoogleVoice => "Google Voice account",
        ServerAccountProviders.Instagram => "Instagram account",
        _ => "Account",
    };

    private static string SignInHref(string service) => SignInPath + "?service=" + service;

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
        if (!(open is "fingerprint" or "theme" or "requests" or "add-device" || ServiceOrder.Contains(open) || ordered.Any(account => AccountId(account) == open) || devices.Has(open))) open = "";

        var waiting = joins.Waiting();
        Attention(body, waiting, ordered, status, token);
        await ChecklistAsync(body, token, cancellationToken).ConfigureAwait(false);

        body.Append("<div class=\"cols\"><div>");
        AccountsSection(body, accounts, ordered, open, request.HttpContext, token);
        DevicesSection(body, open, token);
        body.Append("</div><div>");
        await ServerSectionAsync(body, cancellationToken).ConfigureAwait(false);
        AboutSection(body, request, status, open, token);
        body.Append("</div></div>");

        // The AGPL offer to everyone who uses the server over the network: the source of the commit this image was built from (Core SourceCode).
        body.Append("<footer><a href=\"/log\">Recent log</a><a href=\"").Append(Html(SourceCode.For(status?.App, status?.Revision))).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Source code</a>");
        if (backups is not null) body.Append("<a class=\"end\" href=\"/reset\">Reset server…</a>");
        body.Append("</footer>");

        var actions = "<form class=\"plain\" method=\"post\" action=\"/logout\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\"><button type=\"submit\" class=\"link\">Sign out</button></form>";
        // Every 5 seconds while the server waits to be signed in to Tailscale, every 10 while a device waits to join (so its going is seen), every 30 otherwise (so a new one shows up without a click).
        return new(body.ToString(), actions, waiting.Count > 0 ? 10 : TailnetWaiting ? 5 : 30,
            open.Length > 0 ? "/?open=" + Uri.EscapeDataString(open) + "#row-" + Uri.EscapeDataString(open) : "/");
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
            rows.Append(Row($"{Provider(account.Provider)} isn't connected", Who(account), null, offered ? "<a class=\"button\" href=\"" + SignInHref(service) + "\">Sign in</a>" : null, Icon(account.Provider)));
        }
        if (tailnet?.Latest is { NeedsAttention: true } view)
            rows.Append(Row(view.State == "NeedsLogin" ? "Tailscale needs signing in" : view.NeedsApproval ? "Tailscale needs the server approved" : "Tailscale has a step left", null, null, "<a class=\"button\" href=\"/manage/tailscale\">Review</a>"));
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
                    var under = desktop || offered || !AnyBrowser(services) ? null : Elsewhere(service, services);
                    if (!offered)
                    {
                        body.Append(Row(name, under, "Not connected", Act(""), Icon(provider)));
                        continue;
                    }
                    body.Append("<div class=\"line\">").Append(Opens(provider, name, null, "Not connected", "Connect " + name, open, Icon(provider)));
                    if (open == provider) body.Append("<div class=\"more\"><a class=\"button quiet\" href=\"").Append(SignInHref(service)).Append("\" aria-label=\"").Append(Html("Connect: " + name)).Append("\">Connect</a></div>");
                    body.Append("</div>");
                    continue;
                }
                foreach (var account in mine)
                {
                    var id = AccountId(account);
                    body.Append("<div class=\"line\">").Append(Opens(id, name, Who(account), Word(account.State), "Manage " + AccountLabel(account), open, Icon(provider), account.State == ServerAccountStates.Saved));
                    if (open == id)
                    {
                        body.Append("<div class=\"more\">");
                        if (offered)
                            body.Append("<a class=\"button quiet\" href=\"").Append(SignInHref(service)).Append("\" aria-label=\"").Append(Html((SignInMany(service) ? "Add a number: " : "Sign in again: ") + name)).Append("\">")
                                .Append(SignInMany(service) ? "Add a number…" : "Sign in again…").Append("</a>");
                        if (account.Removable)
                            body.Append("<a class=\"button quiet\" href=\"/accounts/remove?id=").Append(Html(Uri.EscapeDataString(id))).Append("\" aria-label=\"").Append(Html("Remove " + AccountLabel(account))).Append("\">Remove…</a>");
                        else if (provider == ServerAccountProviders.GoogleMessages) body.Append("<span class=\"hint\">").Append(Html(UnpairGoogle)).Append("</span>");
                        else body.Append("<span class=\"hint\">An account being moved between this server and a PC can't be removed until the move finishes.</span>");
                        body.Append("</div>");
                    }
                    body.Append("</div>");
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
        if (list.Count == 0) body.Append(Row("No device is paired yet.", null, null, Act("")));
        foreach (var device in list)
        {
            body.Append("<div class=\"line\">").Append(Opens(device.Id, device.Name, null, device.LastSeen is { } seen ? Ago(seen) : "Never connected", "Manage " + device.Name, open));
            if (open == device.Id)
                body.Append("<div class=\"more flat\"><a class=\"button quiet\" href=\"/devices/rename?id=").Append(Uri.EscapeDataString(device.Id)).Append("\" aria-label=\"").Append(Html("Rename " + device.Name)).Append("\">Rename…</a>")
                    .Append("<a class=\"button quiet\" href=\"/devices/remove?id=").Append(Uri.EscapeDataString(device.Id)).Append("\" aria-label=\"").Append(Html("Remove " + device.Name)).Append("\">Remove…</a></div>");
            body.Append("</div>");
        }
        // Asking to join is a way in for devices, so its switch sits with them.
        body.Append("<div class=\"line\">").Append(Opens("requests", "Requests to join", null, joins.Paused ? "Paused" : "Allowed", "Requests to join", open));
        if (open == "requests")
            body.Append("<form class=\"more flat\" method=\"post\" action=\"/joins/pause\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\"><input type=\"hidden\" name=\"paused\" value=\"").Append(joins.Paused ? "no" : "yes")
                .Append("\"><button type=\"submit\" class=\"quiet\">").Append(joins.Paused ? "Allow requests" : "Pause requests").Append("</button>").Append(joins.Paused ? "<span class=\"hint\">Paired devices keep working.</span>" : "").Append("</form>");
        body.Append("</div><div class=\"line\">").Append(Opens("add-device", "Add a device", null, null, "Add a device", open));
        // Pages that ask for the password first (a GET, so nothing is done by following them).
        if (open == "add-device") body.Append("<div class=\"more flat\"><a class=\"button quiet\" href=\"/phone\" aria-label=\"Add a phone\">A phone…</a><a class=\"button quiet\" href=\"/pair\" aria-label=\"Add a PC\">A PC…</a></div>");
        body.Append("</div></div></section>");
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
            body.Append(Go("Notifications", null, value, "/manage/notifications", "Notifications"));
        }
        if (tailnet is not null)
            body.Append(Go("Tailscale", null, !tailnet.Enabled ? "Off" : tailnet.Latest switch { null => "Starting", { Running: true } => "Connected", { State: "NeedsLogin" } => "Signed out", _ => "Not connected" },
                "/manage/tailscale", "Tailscale", warn: tailnet.Enabled && tailnet.Latest is { State: "NeedsLogin" }));
        if (history is not null)
        {
            HistoryMirrorResult? mirror = null;
            try { mirror = await history.MirrorAsync(new HistoryMirrorParams(), cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
            body.Append(Go("Sync check", null, SyncLine(mirror), "/manage/sync", "Sync check"));
        }
        if (backups is not null)
        {
            var state = backups.Service.Status();
            body.Append(Go("Daily backup", null, BackupLine(state), "/manage/backups", "Daily backup", warn: state.State is ServerBackupStates.Failing or ServerBackupStates.NoPassword || state.Stale));
        }
        if (storage is not null)
            body.Append(Go("Storage", null, storage.Current() is { } sizes ? ServerStorage.Size(sizes.Total) : "Measuring…", "/manage/storage", "Storage"));
        body.Append("</div></section>");
    }

    // What the server is, and how this page looks.
    private void AboutSection(StringBuilder body, HttpRequest request, HostStatus? status, string open, string token)
    {
        body.Append("<section class=\"sec\" id=\"about\"><div class=\"sh\"><h2>About</h2></div><div class=\"box\">");
        // The version alone: how the server is updated is its host's schedule, which it cannot know, and its protocol and build are in its log.
        if (status is null) body.Append(Row("The server couldn't say what it is just now.", null, null, Act("")));
        else
        {
            body.Append(Row("Version", null, status.App, Act("")));
            body.Append(Row("Uptime", null, Span(time.GetUtcNow() - status.Started) is { Length: > 0 } span ? span : "Just started", Act("")));
        }
        body.Append("<div class=\"row\"><div class=\"g\">Address</div><span class=\"v copy\">").Append(Html(Address(request))).Append("</span>").Append(Act("")).Append("</div>");
        var whole = fingerprint();
        body.Append("<div class=\"line\">").Append(Opens("fingerprint", "Fingerprint", null, whole.Length > 12 ? whole[..12] + "…" : whole, "The whole fingerprint", open));
        if (open == "fingerprint") body.Append("<div class=\"more flat\"><span class=\"copy\">").Append(Html(whole)).Append("</span></div>");
        body.Append("</div><div class=\"line\">").Append(Opens("theme", "Theme", null, Themes.First(known => known.Id == theme).Name, "Change the theme", open));
        if (open == "theme")
        {
            body.Append("<form class=\"more flat\" method=\"post\" action=\"/theme\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">");
            foreach (var (id, name) in Themes)
                body.Append("<button type=\"submit\" name=\"theme\" value=\"").Append(id).Append('"').Append(id == theme ? " aria-pressed=\"true\"" : " class=\"quiet\"").Append('>').Append(Html(name)).Append("</button>");
            body.Append("</form>");
        }
        body.Append("</div></div></section>");
    }
}
