using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MessageLink.Server.Backup;

namespace MessageLink.Server.Network;

// Choosing where backups go (owner's design, 2026-11-22): the Backups card's Location line opens the Backup location screen, which shows what is in use and
// offers a folder on this server or a network share, each picked from lists in one browser look (the path as links, the folders, a bar naming the exact folder
// with Use this folder). Each place is its own address, so it works with no script and Back works as a person expects.
// A file server's sign-in is typed once on Connect, or taken from the share in use, and then held here, in memory, for the browser that typed it, under a random
// id for ten minutes from its last use: the lists' links carry the id, never the sign-in, and a refused one keeps only its server and username, to type the
// password again. Nothing is logged but outcomes: the folders' names and the sign-in are the person's own.
internal sealed partial class ServerPage
{
    private static readonly TimeSpan ShareSignInLifetime = TimeSpan.FromMinutes(10);
    // A few at once, each its own browser's or tab's, so connecting on the phone doesn't end a list open on the PC; past that the oldest goes.
    private const int MostShareSignIns = 8;
    // How often the sweep looks: a sign-in is gone within this of its ten minutes.
    private static readonly TimeSpan ShareSweepEvery = TimeSpan.FromSeconds(10);
    private readonly object shareGate = new();
    // Refused: the file server refused it (or couldn't be reached to list), so its password is gone and it only fills in Connect again.
    private sealed record HeldSignIn(string Id, string? Session, BackupDestination SignIn, DateTimeOffset At, bool Refused);
    private readonly List<HeldSignIn> shareSignIns = [];
    // Drops a sign-in ten minutes unused even if its browser never comes back, so a password isn't kept for as long as the server runs. Runs only while one is held.
    // A request already past its lookup keeps the copy it took until it finishes; the service's one-at-a-time hold still paces what it can do with it.
    private ITimer? shareSweep;

    // Only for the backup location's screens.
    internal const string BackupBrowseStyle =
        "html{--good:#5fb37a}.gl{width:24px;height:24px;flex:none;display:flex;align-items:center;justify-content:center;color:var(--muted)}"
        + ".gl.lg{width:32px;height:32px;border-radius:8px;background:var(--field);border:1px solid var(--fieldline);color:var(--soft)}"
        + ".row.tight{min-height:44px;padding:8px 16px}.row.off .g{color:var(--muted);font-weight:400}.g small.mono{font-family:ui-monospace,'Cascadia Mono',Consolas,Menlo,monospace;font-size:12.5px;color:var(--soft);margin-top:3px}"
        // The path as links, and who a file server is signed in as.
        + ".crumbs{display:flex;align-items:center;flex-wrap:wrap;gap:2px;min-width:0;font-size:14px}.crumbs a{color:var(--soft);text-decoration:none;padding:2px 6px;border-radius:6px;overflow-wrap:anywhere}"
        + ".crumbs a:hover{background:var(--fieldhover);color:var(--fg)}.crumbs .sepc{width:6px;height:6px;border:solid var(--faint);border-width:1.3px 1.3px 0 0;transform:rotate(45deg);margin:0 4px 0 2px;flex:none}"
        + ".crumbs .here{color:var(--fg);font-weight:600;padding:2px 6px;overflow-wrap:anywhere}.sh .who{font-size:12.5px;color:var(--muted);display:flex;align-items:center;gap:6px;white-space:nowrap}.who .dot{background:var(--good);margin:0}"
        // New folder: a <details> whose button sits at the path's right and whose line opens at the top of the list.
        + ".browse{position:relative}.browse.can-make>.sh{padding-right:128px}.browse .sh{flex-wrap:wrap}details.mkd>summary{position:absolute;top:-4px;right:2px;list-style:none;display:inline-flex;align-items:center;gap:6px;font-size:13px;font-weight:500;line-height:20px;padding:5px 12px;border-radius:7px;border:1px solid var(--fieldline);background:var(--field);color:var(--fg);cursor:pointer;white-space:nowrap}"
        + "details.mkd>summary::-webkit-details-marker{display:none}details.mkd>summary:hover{background:var(--fieldhover)}.box>details.mkd:not([open])+*{border-top:0}"
        + "form.mk{display:flex;align-items:center;gap:10px;margin:0;padding:8px 12px 8px 16px;background:var(--rowhover);border-radius:11px 11px 0 0}form.mk input{margin:0;max-width:none;flex:1;min-width:0}"
        + ".box>p.found{display:flex;align-items:center;gap:10px;padding:10px 16px;font-size:13px;color:var(--soft)}.found svg{color:var(--good);flex:none}"
        // The bar under a list that names what Use this folder would choose.
        + ".usebar{display:flex;align-items:center;gap:14px;margin:18px 0 0;padding:12px 14px 12px 16px;border:1px solid var(--line);border-radius:12px;background:var(--box)}"
        + ".usebar .to{flex:1;min-width:0}.usebar .to small{display:block;color:var(--muted);font-size:12.5px}.usebar .to span{font-family:ui-monospace,'Cascadia Mono',Consolas,Menlo,monospace;font-size:12.5px;overflow-wrap:anywhere}"
        + ".usebar form,.usebar .use{margin:0;flex:none;display:flex;align-items:center}button[disabled]{opacity:.4;cursor:default}button[disabled]:hover{background:var(--acc);border-color:var(--acc)}"
        // The location screen's two ways, as cards, and what is folded away.
        + ".choice{display:grid;grid-template-columns:1fr 1fr;gap:12px}.choice a{display:flex;flex-direction:column;gap:10px;padding:16px;border:1px solid var(--line);border-radius:12px;background:var(--box);color:var(--fg);text-decoration:none}"
        + ".choice a:hover{background:var(--rowhover);border-color:var(--fieldline)}.choice b{font-weight:600}.choice small{color:var(--muted);font-size:13px;line-height:1.4}.box+.sh{margin-top:22px}"
        + "details.fold{margin:18px 0 14px}details.fold>summary{color:var(--soft);font-size:13px;cursor:pointer;list-style:none;display:flex;align-items:center;gap:8px}details.fold>summary::-webkit-details-marker{display:none}"
        + "details.fold>summary::before{content:\"\";width:6px;height:6px;border:solid var(--faint);border-width:1.5px 1.5px 0 0;transform:rotate(45deg)}details.fold[open]>summary::before{transform:rotate(135deg)}details.fold[open]>summary{margin-bottom:12px}"
        + "details.fold form{margin:0}.pick{display:flex;gap:8px;align-items:stretch;max-width:460px;margin:0 0 14px}.pick input{margin:0;max-width:none;flex:1;min-width:0}.pick button{flex:none}"
        // The share in use on Connect: a line that is a form's button.
        + ".box>form.rowform{margin:0}button.row{width:100%;border:0;border-radius:inherit;background:none;color:var(--fg);font:inherit;font-weight:400;line-height:inherit;text-align:left;white-space:normal}"
        + "button.row:hover{background:var(--rowhover);border:0}form.rowform+.sh,.box+form{margin-top:22px}"
        + "section>.box+p:not(.hint){margin-top:12px}.notice.good svg{color:var(--good)}.notice.bad{background:var(--warnbg);border-color:var(--warnline)}.notice.bad svg{color:var(--warn)}"
        + "@media(max-width:640px){.choice{grid-template-columns:1fr}.usebar{flex-direction:column;align-items:stretch;gap:10px}.usebar form,.usebar .use{justify-content:flex-end}.browse .sh .r{margin-left:0;width:100%}.row.inuse{flex-wrap:wrap;row-gap:8px}.row.inuse .g{flex-basis:calc(100% - 38px)}.row.inuse .v{margin-left:38px;flex:1}}"
        // A line that stops backups until it is dealt with; a running backup's bar; a field a form refused, and one the browser finds missing on Submit.
        + ".row.need{background:rgba(245,165,36,.11)}.row.run .g{flex:1;min-width:0}"
        + "progress.bar{display:block;-webkit-appearance:none;appearance:none;width:100%;height:4px;margin:10px 0 2px;border:0;border-radius:2px;background:var(--switchoff);overflow:hidden}"
        + "progress.bar::-webkit-progress-bar{background:var(--switchoff)}progress.bar::-webkit-progress-value{background:var(--acc);transition:width .4s}progress.bar::-moz-progress-bar{background:var(--acc)}"
        + ".notice.error{background:rgba(201,52,58,.08);border-color:rgba(255,123,123,.35);color:var(--fg)}.notice.error svg{color:var(--danger-ink)}"
        + "input[aria-invalid=true],input:user-invalid,select:user-invalid{border-color:var(--danger-ink);box-shadow:0 0 0 1px var(--danger-ink)}"
        + "input[type=checkbox][aria-invalid=true],input[type=checkbox]:user-invalid{outline:2px solid var(--danger-ink);outline-offset:2px;box-shadow:none}";

    private const string FolderGlyph = "<path d=\"M3.5 7.5a1 1 0 0 1 1-1h4.6l1.8 2h8.6a1 1 0 0 1 1 1V18a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1z\"/>";
    private const string FolderPlusGlyph = FolderGlyph + "<path d=\"M12 11.5v5M9.5 14h5\"/>";
    private const string ShareGlyph = FolderGlyph + "<path d=\"M12 11.5v4.5M9.8 13.7 12 11.5l2.2 2.2\"/>";
    private const string DriveGlyph = "<rect x=\"3.5\" y=\"7\" width=\"17\" height=\"10\" rx=\"1.5\"/><path d=\"M7 13.5h.01M10 13.5h4\"/>";
    private const string NetDriveGlyph = "<rect x=\"3.5\" y=\"4.5\" width=\"17\" height=\"9\" rx=\"1.5\"/><path d=\"M7 10h.01M12 13.5v4M5 19.5h14M12 17.5v2\"/>";
    private const string ServerGlyph = "<rect x=\"4\" y=\"3.5\" width=\"16\" height=\"7\" rx=\"1.5\"/><rect x=\"4\" y=\"13.5\" width=\"16\" height=\"7\" rx=\"1.5\"/><path d=\"M7.5 7h.01M7.5 17h.01\"/>";
    private const string CheckGlyph = "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"m8.5 12.2 2.4 2.4 4.6-4.8\"/>";

    // A line's picture, in the column its words line up after.
    private static string Gl(string glyph, bool large = false) => "<span class=\"gl" + (large ? " lg" : "") + "\">" + Glyph(glyph, 18) + "</span>";

    private static string BackTo(bool fromSteps) => fromSteps ? "/welcome?step=backups#backups" : "/manage/backups#backups";

    // The card again, saying how a choice went (FolderWords): the walkthrough's screen when it came from there.
    private static string CardWith(bool fromSteps, string? word) =>
        (fromSteps ? "/welcome?step=backups&done=" : "/manage/backups?done=") + "backup_folder" + (word is null ? "" : "_" + word) + "#backups";

    // An address with its query, leaving out what is empty.
    private static string Address(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(part => !string.IsNullOrEmpty(part.Value)).Select(part => part.Name + "=" + Uri.EscapeDataString(part.Value!)).ToArray();
        return parts.Length == 0 ? path : path + "?" + string.Join("&", parts);
    }

    private static string? StepsOf(bool fromSteps) => fromSteps ? "steps" : null;

    private static string LocationAt(bool fromSteps, string? done = null, string? tried = null) =>
        Address("/backups/location", ("done", done), ("tried", tried), ("from", StepsOf(fromSteps)));

    private static string BrowseAt(string? path, bool fromSteps, string? done = null) => Address("/backups/browse", ("path", path), ("done", done), ("from", StepsOf(fromSteps)));

    private static string ConnectAt(bool fromSteps, string? done = null, string? id = null) => Address("/backups/connect", ("id", id), ("done", done), ("from", StepsOf(fromSteps)));

    private static string ShareLink(string id, string? share, string? path, bool fromSteps, string? done = null, string? made = null) =>
        Address("/backups/share", ("id", id), ("share", share), ("path", path), ("done", done), ("made", made), ("from", StepsOf(fromSteps)));

    private static string FormStart(string action, string token, bool fromSteps, string? css = null) =>
        "<form method=\"post\" action=\"" + action + "\"" + (css is null ? "" : " class=\"" + css + "\"") + "><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\">"
        + (fromSteps ? "<input type=\"hidden\" name=\"from\" value=\"steps\">" : "");

    private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + name + "\" value=\"" + Html(value) + "\">";

    // What a choice that didn't take said, on the screen it came from; only the card's own words are put on a page.
    private static string? ChoiceWords(string? done) => string.IsNullOrEmpty(done) ? null : FolderWords("backup_folder_" + done);

    // Who a file server is signed in as, as Windows writes an account with its domain.
    private static string Account(BackupDestination signIn) => string.IsNullOrEmpty(signIn.Domain) ? signIn.User ?? "" : signIn.Domain + "\\" + signIn.User;

    // A drive's size in the units File Explorer shows.
    private static string Space(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024, Tb = Gb * 1024;
        var invariant = CultureInfo.InvariantCulture;
        return bytes >= Tb ? (bytes / Tb).ToString("0.0", invariant) + " TB"
            : bytes >= 10 * Gb ? (bytes / Gb).ToString("0", invariant) + " GB"
            : bytes >= Gb ? (bytes / Gb).ToString("0.0", invariant) + " GB"
            : (bytes / (1024d * 1024)).ToString("0", invariant) + " MB";
    }

    // "3 Message Link backups are in this folder, the newest from Nov 21": what Restore would find here, in the server's time.
    private string FoundLine(BackupsFound? found)
    {
        if (found is not { Count: > 0 }) return "";
        var day = found.Newest is { } newest ? newest.ToLocalTime() : (DateTimeOffset?)null;
        var date = day is { } at ? at.ToString(at.Year == time.GetUtcNow().ToLocalTime().Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture) : null;
        var words = found.Count == 1 ? "1 Message Link backup is in this folder" + (date is null ? "." : ", from " + date + ".")
            : found.Count.ToString(CultureInfo.InvariantCulture) + " Message Link backups are in this folder" + (date is null ? "." : ", the newest from " + date + ".");
        return "<p class=\"found\">" + Glyph(CheckGlyph) + "<span>" + Html(words) + "</span></p>";
    }

    // New folder, at the top of a list: its button beside the path, its line opening in place. Cancel is this list's own address, which closes it.
    private static string MakeLine(string action, string token, bool fromSteps, string fields, string cancel) =>
        "<details class=\"mkd\"><summary>" + Glyph(FolderPlusGlyph, 15) + "New folder</summary>" + FormStart(action, token, fromSteps, "mk") + fields + Gl(FolderGlyph)
        + "<input name=\"name\" required maxlength=\"255\" spellcheck=\"false\" autocomplete=\"off\" aria-label=\"New folder name\"><button type=\"submit\">Make</button>"
        + "<a class=\"button quiet\" href=\"" + Html(cancel) + "\">Cancel</a></form></details>";

    // The bar under a list: the exact folder Use this folder would choose, or, where nothing can be chosen yet, what to do.
    private static string UseBar(string token, bool fromSteps, string? path, string fields, string hint) =>
        path is null
            ? "<div class=\"usebar\"><div class=\"to\"><small>" + Html(hint) + "</small></div><div class=\"use\"><button type=\"button\" disabled>Use this folder</button><a class=\"cancel\" href=\""
              + Html(BackTo(fromSteps)) + "\">Cancel</a></div></div>"
            : "<div class=\"usebar\"><div class=\"to\"><small>Backups go to</small><span>" + Html(path) + "</span></div>" + FormStart("/backups", token, fromSteps) + fields
              + "<a class=\"cancel\" href=\"" + Html(BackTo(fromSteps)) + "\">Cancel</a></form></div>";

    private static void Crumb(StringBuilder body, string name, string? href, bool first)
    {
        if (!first) body.Append("<i class=\"sepc\" aria-hidden=\"true\"></i>");
        if (href is null) body.Append("<span class=\"here\" aria-current=\"location\">").Append(Html(name)).Append("</span>");
        else body.Append("<a href=\"").Append(Html(href)).Append("\">").Append(Html(name)).Append("</a>");
    }

    // A place in a list: the whole line opens it. One that can't be opened now (a mapped drive whose server is off) says so and isn't a link.
    private static string PlaceLine(string name, string? under, string? value, string? href, string glyph) =>
        href is null
            ? "<div class=\"row tight off\">" + Gl(glyph) + "<div class=\"g\">" + Html(name) + "</div><span class=\"v\">Not available</span><span class=\"act\"></span></div>"
            : Go(name, under, value, href, "Open " + name, Gl(glyph), kind: "tight");

    // ---- The Backup location screen ----

    private async Task LocationAsync(Call call)
    {
        var service = backups!.Service;
        var fromSteps = call.Field("from") == "steps";
        var tried = call.Field("tried") is { Length: > 0 and <= 1024 } typed ? typed : null;
        var body = new StringBuilder();
        if (TestWords(call.Field("tested"), service.UsesSmb) is { } test) body.Append(TestNotice(test.Ok, test.Words));
        else if (ChoiceWords(call.Field("done")) is { } said) body.Append(Notice(said));
        else if (service.DestinationUnreadable) body.Append(Notice("The saved backup location couldn't be read. Choose it again."));
        if (service.Folder is { } folder)
        {
            var share = service.SavedShare;
            body.Append("<div class=\"sh\"><h2>In use</h2></div><div class=\"box\"><div class=\"row inuse\">").Append(Gl(share is null ? FolderGlyph : ShareGlyph))
                .Append("<div class=\"g\">").Append(Html(folder)).Append(share is null ? "" : "<small>Signed in as " + Html(Account(share)) + "</small>").Append("</div>")
                .Append("<span class=\"v\">").Append(service.Settings.LatestWrite is { } written ? "Last written " + Html(Ago(written)) : "No backups yet").Append("</span>")
                .Append(FormStart("/backups", call.Token, fromSteps)).Append("<button type=\"submit\" name=\"do\" value=\"test\" class=\"quiet\" aria-label=\"")
                .Append(share is null ? "Test the backup folder" : "Test the network share").Append("\">Test</button></form></div></div>");
        }
        body.Append("<div class=\"sh\"><h2>").Append(service.Folder is null ? "Choose a location" : "Choose a new location").Append("</h2></div><div class=\"choice\">")
            .Append("<a href=\"").Append(Html(BrowseAt(service.UsesSmb ? null : service.Folder, fromSteps))).Append("\">").Append(Gl(DriveGlyph, large: true))
            .Append("<b>Folder on this server</b><small>A drive or folder this server can write to, including a mapped network drive.</small></a>")
            .Append("<a href=\"").Append(Html(ConnectAt(fromSteps))).Append("\">").Append(Gl(ServerGlyph, large: true))
            // Docker only where the server runs in a container: elsewhere it's a word the screen hasn't introduced.
            .Append("<b>Network share</b><small>A shared folder on a file server or NAS, with its own sign-in.").Append(InContainer ? " No Docker folder mapping needed." : "").Append("</small></a></div>")
            // A folder past the browser's limit, or a path copied from elsewhere.
            .Append("<details class=\"fold\"").Append(tried is null ? "" : " open").Append("><summary>Enter a path instead</summary>")
            .Append(FolderForm(call.Token, tried ?? (service.UsesSmb ? null : service.Folder), fromSteps)).Append("</details>");
        if (fromSteps) body.Append("<p class=\"hint\"><a href=\"").Append(Html(BackTo(true))).Append("\">Cancel</a></p>");
        await PageAsync(call.Context, StatusCodes.Status200OK, "Backup location", body.ToString()).ConfigureAwait(false);
    }

    // What Test found, said on the location screen: whether the location works, or what to do about it.
    private static (bool Ok, string Words)? TestWords(string? tested, bool share) => tested switch
    {
        "ok" => (true, share ? "The share works. The server wrote a test file there, read it back and removed it."
            : "The folder works. The server wrote a test file there, read it back and removed it."),
        "busy" => (false, "A backup or check is running. Wait for it to finish, then test again."),
        "wait" => (false, "Wait 30 seconds, then test again."),
        "auth" => (false, "The file server refused the saved sign-in. Choose Network share and connect with its current password."),
        "unavailable" => (false, "The file server didn't answer. Check that it's on and connected to the network."),
        "share" => (false, "That share isn't on the file server anymore."),
        "missing" => (false, share ? "That folder isn't on the share anymore." : "That folder isn't there anymore, or its drive isn't connected."),
        "denied" => (false, share ? "The saved sign-in can't write to that folder on the share." : "The server can't write to that folder."),
        "space" => (false, "The backup location is full."),
        "write" or "path" => (false, "The test file didn't come back the same, so backups there can't be trusted. Check the drive or file server."),
        "io" => (false, "The server couldn't write and remove a test file there. Check the drive and its connection."),
        _ => null,
    };

    private static string TestNotice(bool ok, string words) =>
        "<p class=\"notice " + (ok ? "good" : "bad") + "\" role=\"status\">" + Glyph(ok ? CheckGlyph : InfoGlyph) + "<span>" + Html(words) + "</span></p>";

    // The server's folder as a typed path: the server runs as the person (or the container's user), so whatever it can open it can write to.
    private static string FolderForm(string token, string? current, bool fromSteps) =>
        new StringBuilder(FormStart("/backups", token, fromSteps))
            .Append("<label for=\"folder\">Folder</label><div class=\"pick\"><input id=\"folder\" name=\"folder\" type=\"text\" required maxlength=\"1024\" spellcheck=\"false\" placeholder=\"")
            .Append(OperatingSystem.IsWindows() ? "D:\\Backups\\Message Link" : "/backups").Append("\" value=\"").Append(Html(current ?? "")).Append("\">")
            .Append("<button type=\"submit\" name=\"do\" value=\"folder\">Use this folder</button></div></form>").ToString();

    // ---- A folder on this server ----

    private async Task BrowseAsync(Call call)
    {
        var service = backups!.Service;
        var fromSteps = call.Field("from") == "steps";
        if (!call.Post)
        {
            await PageAsync(call.Context, StatusCodes.Status200OK, "Choose a folder", BrowseBody(call.Token, service.BrowseFolders(call.Field("path")), fromSteps,
                ChoiceWords(call.Field("done")))).ConfigureAwait(false);
            return;
        }
        // New folder: made inside the folder shown, then opened.
        var place = call.Field("path").Trim().Trim('"');
        var made = service.MakeFolder(place, call.Field("name"));
        log($"server page backup browse new_folder outcome={made}");
        if (made is "made" or "exists")
        {
            Redirect(call.Response, BrowseAt(Path.Combine(place, call.Field("name").Trim()), fromSteps));
            return;
        }
        await PageAsync(call.Context, StatusCodes.Status400BadRequest, "Choose a folder", BrowseBody(call.Token, service.BrowseFolders(place), fromSteps, MadeWords(made))).ConfigureAwait(false);
    }

    // Why a new folder wasn't made, local or on a share.
    private static string? MadeWords(string? made) => made switch
    {
        "name" => "Enter a folder name without \\ / : * ? \" < > |.",
        "internal" => FolderWords("backup_folder_internal"),
        "missing" => "That folder isn't available anymore.",
        "wait" => "Wait 30 seconds, then try again.",
        "unavailable" => "The file server didn't answer. Check its connection.",
        "path" => "That folder can't be opened on a share. Go up and choose another.",
        // Read-only to the server (a Linux service's protected system folders, a share it can only read): trying again won't help.
        "file" => "A file already has that name here. Choose another name.",
        "denied" or "unsaved" or "share" or "space" => "The server can't make a folder here. Choose another place.",
        _ => null,
    };

    private string BrowseBody(string token, FolderListing listing, bool fromSteps, string? problem)
    {
        var body = new StringBuilder();
        if (problem is not null) body.Append(Notice(problem));
        var said = listing.Outcome switch
        {
            "nearest" => "That folder isn't there, so the nearest folder above it is shown.",
            "not_full" => "Enter the full folder path as this server sees it.",
            "missing" => "That drive or folder isn't available.",
            "internal" => FolderWords("backup_folder_internal"),
            "denied" => "Message Link can't open that folder.",
            "unavailable" => "That folder couldn't be read. Check its connection.",
            _ => null,
        };
        // A choice refused for the reason the list itself gives (the server's own folder) is said once.
        if (said is not null && said != problem) body.Append(Notice(said));
        if (listing.Place is not null && !listing.Mapped && InContainer)
            body.Append(Notice("This folder is inside the container, so backups here are lost when it is replaced. Choose a mapped folder."));
        var usable = listing.Place is not null && listing.Outcome is "ok" or "nearest";
        var start = OperatingSystem.IsWindows() ? "This PC" : InContainer ? "Mapped folders" : "This server";
        body.Append("<div class=\"browse").Append(usable ? " can-make" : "").Append("\"><div class=\"sh\"><nav class=\"crumbs\" aria-label=\"Folder\">");
        if (listing.Place is not { } place) Crumb(body, start, null, first: true);
        else
        {
            // The start, the drive or mount, then each folder on the way, each a link but the last.
            Crumb(body, start, BrowseAt(null, fromSteps), first: true);
            var root = listing.Root ?? Path.GetPathRoot(place) ?? place;
            var parts = place.Length > root.Length && place.StartsWith(root, StringComparison.Ordinal)
                ? place[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) : [];
            Crumb(body, listing.RootName ?? root, parts.Length == 0 ? null : BrowseAt(root, fromSteps), first: false);
            var walked = root;
            for (var i = 0; i < parts.Length; i++)
            {
                walked = Path.Combine(walked, parts[i]);
                Crumb(body, parts[i], i == parts.Length - 1 ? null : BrowseAt(walked, fromSteps), first: false);
            }
        }
        body.Append("</nav></div><div class=\"box\">");
        if (usable) body.Append(MakeLine("/backups/browse", token, fromSteps, Hidden("path", listing.Place!), BrowseAt(listing.Place, fromSteps)));
        body.Append(FoundLine(listing.Backups));
        // In a container the whole tree is its own disk, lost when it is replaced: only what is mapped in is offered at the start.
        var folders = listing.Folders.Where(folder => !(listing.Place is null && InContainer && folder.Path == "/")).ToArray();
        foreach (var folder in folders)
        {
            var glyph = folder.Kind switch { "drive" => DriveGlyph, "network" => NetDriveGlyph, _ => FolderGlyph };
            var free = folder.Free is { } bytes ? Space(bytes) + " free" + (folder.Total is { } total ? " of " + Space(total) : "") : null;
            var under = folder.Mount ? folder.Path == "/" ? "Every folder on this server" : null : folder.Note;
            body.Append(PlaceLine(folder.Name, under, free, folder.Available ? BrowseAt(folder.Path, fromSteps) : null, glyph));
        }
        if (folders.Length == 0) body.Append("<p>").Append(listing.Place is null ? "No drives or folders were found." : "No folders here.").Append("</p>");
        body.Append("</div>");
        if (listing.More) body.Append("<p class=\"hint\">Only the first ").Append(BackupFolderBrowser.Most).Append(" folders by name are shown. Enter the path on the Backup location screen to choose another.</p>");
        if (listing.Place is null && InContainer)
            body.Append("<p class=\"hint\">Only folders mapped into the container are listed: anything else is lost when the container is replaced.</p>");
        body.Append(UseBar(token, fromSteps, usable ? listing.Place : null,
            Hidden("folder", listing.Place ?? "") + Hidden("via", "browse") + "<button type=\"submit\" name=\"do\" value=\"folder\">Use this folder</button>",
            listing.Place is not null ? "Choose another folder."
            : OperatingSystem.IsWindows() ? "Open a drive, then the folder for your backups."
            : InContainer ? "Open a mapped folder, then the folder for your backups." : "Open the folder for your backups."));
        return body.Append("</div>").ToString();
    }

    // ---- A folder on a file server ----

    private Task<ShareListing> ListShareAsync(BackupDestination place, CancellationToken token) =>
        (backups!.BrowseShare ?? backups.Service.BrowseSmbAsync)(place, token);

    // The share and the folder in it, as the browser's links name them, checked before anything signs in.
    private static BackupDestination? ShareAt(BackupDestination signIn, string share, string path)
    {
        var place = signIn with { Share = share.Trim(), Subfolder = path.Trim().Replace('/', '\\').Trim('\\') };
        return place.Share!.Length == 0 ? place with { Subfolder = "" } : BackupDestination.ValidSmb(place) ? place : null;
    }

    // The entry held under an id for this browser, while its lifetime lasts (under shareGate). Every entry is compared, in constant time.
    private int HeldAt(string id, string? session)
    {
        SweepShares();
        var at = -1;
        var asked = Encoding.ASCII.GetBytes(id);
        for (var i = 0; i < shareSignIns.Count; i++)
        {
            var entry = shareSignIns[i];
            if (session is not null && entry.Session == session && asked.Length == entry.Id.Length && CryptographicOperations.FixedTimeEquals(asked, Encoding.ASCII.GetBytes(entry.Id)))
                at = i;
        }
        return at;
    }

    // Forgets every sign-in past its lifetime (under shareGate), and stops the sweep when none is left.
    private void SweepShares()
    {
        var now = time.GetUtcNow();
        shareSignIns.RemoveAll(entry => now - entry.At > ShareSignInLifetime);
        if (shareSignIns.Count == 0 && shareSweep is not null)
        {
            shareSweep.Dispose();
            shareSweep = null;
        }
    }

    // The sign-in held for the browser that typed it, and only while it is used: another browser with the id has nothing, nor has a refused one.
    private BackupDestination? HeldShare(string id, string? session)
    {
        lock (shareGate)
        {
            if (HeldAt(id, session) is not (>= 0 and var at) || shareSignIns[at].Refused) return null;
            shareSignIns[at] = shareSignIns[at] with { At = time.GetUtcNow() };
            return shareSignIns[at].SignIn;
        }
    }

    // What Connect fills in again after a refusal: the server, username and domain, never the password.
    private BackupDestination? HeldPrefill(string id, string? session)
    {
        lock (shareGate) return HeldAt(id, session) is >= 0 and var at ? shareSignIns[at].SignIn with { Password = null } : null;
    }

    private string Hold(BackupDestination signIn, string? session)
    {
        var made = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (shareGate)
        {
            SweepShares();
            if (shareSignIns.Count >= MostShareSignIns) shareSignIns.RemoveAt(0);
            shareSignIns.Add(new(made, session, signIn with { Share = "", Subfolder = "", Id = null }, time.GetUtcNow(), false));
            shareSweep ??= time.CreateTimer(_ => { lock (shareGate) SweepShares(); }, null, ShareSweepEvery, ShareSweepEvery);
        }
        return made;
    }

    // The file server refused it: its password goes, its server and username stay to fill in Connect.
    private void Refuse(string id)
    {
        lock (shareGate)
        {
            var at = shareSignIns.FindIndex(entry => entry.Id == id);
            if (at >= 0) shareSignIns[at] = shareSignIns[at] with { SignIn = shareSignIns[at].SignIn with { Password = null }, Refused = true };
        }
    }

    private void DropShare(string id)
    {
        lock (shareGate) shareSignIns.RemoveAll(entry => entry.Id == id);
    }

    // What a list of a share said, on the list itself.
    private static string? ShareWords(string outcome) => outcome switch
    {
        "ok" => null,
        "path" => "That folder can't be opened on a share. Go up and choose another.",
        "denied" => "The file server refused access to that folder.",
        "missing" => "That folder isn't on the share anymore.",
        "share" => "That share wasn't found on the server.",
        "wait" => "Wait 30 seconds, then try again.",
        _ => "The file server didn't answer. Check its connection.",
    };

    // Connect: a file server's sign-in, or the share in use's. Both are held and the list opens by its own address, so Back and Reload never send the password again.
    private async Task ConnectAsync(Call call)
    {
        var service = backups!.Service;
        var fromSteps = call.Field("from") == "steps";
        if (!call.Post)
        {
            var prefill = call.Field("id") is { Length: 32 } id ? HeldPrefill(id, call.Session) : null;
            await PageAsync(call.Context, StatusCodes.Status200OK, "Connect to a file server", ConnectBody(call.Token, fromSteps, prefill, ConnectWords(call.Field("done")))).ConfigureAwait(false);
            return;
        }
        BackupDestination signIn;
        if (call.Field("do") == "saved")
        {
            // The share in use: its saved sign-in, held as a typed one is, without anyone typing its password.
            if (service.SavedShare is not { } saved)
            {
                Redirect(call.Response, ConnectAt(fromSteps));
                return;
            }
            signIn = saved;
        }
        else
        {
            signIn = new BackupDestination("smb", Server: call.Field("server").Trim(), Share: "", Subfolder: "", User: call.Field("user"), Domain: call.Field("domain"),
                Password: call.Field("share_password"));
            if (!BackupDestination.ValidSignIn(signIn))
            {
                log("server page backup browse connect outcome=signin");
                await PageAsync(call.Context, StatusCodes.Status400BadRequest, "Connect to a file server",
                    ConnectBody(call.Token, fromSteps, signIn with { Password = null }, ConnectWords("signin"))).ConfigureAwait(false);
                return;
            }
        }
        var held = Hold(signIn, call.Session);
        log($"server page backup browse connect outcome=held via={(call.Field("do") == "saved" ? "saved" : "typed")}");
        Redirect(call.Response, ShareLink(held, null, null, fromSteps));
    }

    private static string? ConnectWords(string? done) => done switch
    {
        "signin" => "Enter the file server's name or address, a username and the password.",
        "auth" => "The file server refused that sign-in. Check the username, password and domain.",
        "unavailable" => "The file server didn't answer. Check its name or address and its connection.",
        "wait" => "Wait 30 seconds, then try again.",
        "expired" => "Sign in to the file server again.",
        _ => null,
    };

    private string ConnectBody(string token, bool fromSteps, BackupDestination? prefill, string? problem)
    {
        var body = new StringBuilder();
        if (problem is not null) body.Append(Notice(problem));
        var saved = backups!.Service.SavedShare;
        if (saved is not null)
        {
            // The share in use: browse its file server again with the sign-in the server keeps.
            body.Append("<div class=\"box\">").Append(FormStart("/backups/connect", token, fromSteps, "rowform")).Append(Hidden("do", "saved"))
                .Append("<button type=\"submit\" class=\"row go\" aria-label=\"").Append(Html(@"Browse \\" + saved.Server + ", signed in as " + Account(saved))).Append("\">")
                .Append(Gl(ServerGlyph)).Append("<div class=\"g\">").Append(Html(@"\\" + saved.Server)).Append("<small>Signed in as ").Append(Html(Account(saved))).Append("</small></div>")
                .Append("<span class=\"act\"><i class=\"chev\"></i></span></button></form></div><div class=\"sh\"><h2>Another file server</h2></div>");
        }
        else body.Append(Para("Sign in with an account that can write to the share. Your backup password is separate."));
        var domain = prefill?.Domain ?? "";
        body.Append(FormStart("/backups/connect", token, fromSteps))
            .Append("<label for=\"smb-server\">File server</label><input id=\"smb-server\" name=\"server\" required maxlength=\"253\" placeholder=\"nas\" spellcheck=\"false\" autocomplete=\"off\" value=\"")
            .Append(Html(prefill?.Server)).Append("\">")
            .Append("<label for=\"smb-user\">Username</label><input id=\"smb-user\" name=\"user\" required maxlength=\"256\" spellcheck=\"false\" autocomplete=\"off\" value=\"").Append(Html(prefill?.User)).Append("\">")
            .Append("<label for=\"smb-password\">Password</label><input id=\"smb-password\" name=\"share_password\" type=\"password\" required maxlength=\"1024\" autocomplete=\"off\">")
            .Append("<details class=\"fold\"").Append(domain.Length > 0 ? " open" : "").Append("><summary>Domain</summary>")
            .Append("<input name=\"domain\" maxlength=\"256\" spellcheck=\"false\" autocomplete=\"off\" aria-label=\"Domain\" value=\"").Append(Html(domain)).Append("\"></details>")
            .Append("<div class=\"btns\"><button type=\"submit\"").Append(saved is null ? "" : " class=\"quiet\"").Append(">Connect</button><a class=\"cancel\" href=\"")
            .Append(Html(BackTo(fromSteps))).Append("\">Cancel</a></div></form>");
        return body.ToString();
    }

    // The file server's shares, or a share's folders; POST makes a new folder in the folder shown.
    private async Task BrowseShareAsync(Call call)
    {
        var fromSteps = call.Field("from") == "steps";
        var id = call.Field("id");
        if (HeldShare(id, call.Session) is not { } signIn)
        {
            // A refused sign-in fills in Connect again; one gone (ten minutes unused, another browser's) is typed afresh.
            Redirect(call.Response, ConnectAt(fromSteps, "expired", HeldPrefill(id, call.Session) is null ? null : id));
            return;
        }
        if (call.Post)
        {
            await MakeShareFolderAsync(call, id, signIn, fromSteps).ConfigureAwait(false);
            return;
        }
        // Enter a path instead: the share and the folders in it typed as one path ("backups\Message Link", or with \\server in front), opened by its own address.
        if (call.Field("place") is { Length: > 0 } typed)
        {
            // Only the whole path is trimmed: a folder's name may start with a space, and trimming each part would open another folder.
            var parts = typed.Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (typed.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) || typed.TrimStart().StartsWith("//", StringComparison.Ordinal))
                parts = parts.Length > 0 && string.Equals(parts[0], signIn.Server, StringComparison.OrdinalIgnoreCase) ? parts[1..] : ["\\"];
            Redirect(call.Response, ShareLink(id, parts.FirstOrDefault() ?? "", string.Join('\\', parts.Skip(1)), fromSteps));
            return;
        }
        if (ShareAt(signIn, call.Field("share"), call.Field("path")) is not { } place)
        {
            await PageAsync(call.Context, StatusCodes.Status400BadRequest, "Choose a network folder",
                ShareBody(call.Token, id, signIn with { Share = "", Subfolder = "" }, new("path", []), fromSteps, ShareWords("path"))).ConfigureAwait(false);
            return;
        }
        var listing = await ListShareAsync(place, call.Context.RequestAborted).ConfigureAwait(false);
        log($"server page backup browse share outcome={listing.Outcome}");
        // A refused sign-in, or a server that couldn't be reached, is fixed on Connect: it goes back there with the server and username kept. One held back by the
        // 30-second wait was never tried, so it stays held and this list says to try again.
        if (listing.Outcome == "auth" || place.Share!.Length == 0 && listing.Outcome == "unavailable")
        {
            Refuse(id);
            Redirect(call.Response, ConnectAt(fromSteps, listing.Outcome, id));
            return;
        }
        var said = MadeWords(call.Field("made")) ?? ChoiceWords(call.Field("done"));
        await PageAsync(call.Context, listing.Outcome == "ok" ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, "Choose a network folder",
            ShareBody(call.Token, id, place, listing, fromSteps, said ?? ShareWords(listing.Outcome))).ConfigureAwait(false);
    }

    private async Task MakeShareFolderAsync(Call call, string id, BackupDestination signIn, bool fromSteps)
    {
        if (ShareAt(signIn, call.Field("share"), call.Field("path")) is not { Share.Length: > 0 } place)
        {
            Redirect(call.Response, ShareLink(id, null, null, fromSteps, made: "path"));
            return;
        }
        var name = call.Field("name").Trim();
        var made = await (backups!.MakeShareFolder ?? backups.Service.MakeSmbFolderAsync)(place, name, call.Context.RequestAborted).ConfigureAwait(false);
        log($"server page backup browse share_folder outcome={made}");
        if (made is "made" or "exists")
        {
            Redirect(call.Response, ShareLink(id, place.Share, string.IsNullOrEmpty(place.Subfolder) ? name : place.Subfolder + "\\" + name, fromSteps));
            return;
        }
        if (made == "auth")
        {
            Refuse(id);
            Redirect(call.Response, ConnectAt(fromSteps, "auth", id));
            return;
        }
        Redirect(call.Response, ShareLink(id, place.Share, place.Subfolder, fromSteps, made: made));
    }

    private string ShareBody(string token, string id, BackupDestination place, ShareListing listing, bool fromSteps, string? problem)
    {
        var share = place.Share ?? "";
        var sub = place.Subfolder ?? "";
        var usable = listing.Outcome == "ok";
        var body = new StringBuilder();
        if (problem is not null) body.Append(Notice(problem));
        body.Append("<div class=\"browse").Append(share.Length > 0 && usable ? " can-make" : "").Append("\"><div class=\"sh\"><nav class=\"crumbs\" aria-label=\"Folder\">");
        Crumb(body, @"\\" + place.Server, share.Length == 0 ? null : ShareLink(id, null, null, fromSteps), first: true);
        if (share.Length > 0)
        {
            var parts = sub.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            Crumb(body, share, parts.Length == 0 ? null : ShareLink(id, share, null, fromSteps), first: false);
            for (var i = 0; i < parts.Length; i++)
                Crumb(body, parts[i], i == parts.Length - 1 ? null : ShareLink(id, share, string.Join('\\', parts[..(i + 1)]), fromSteps), first: false);
        }
        body.Append("</nav><div class=\"r\"><span class=\"who\"><i class=\"dot\"></i>Signed in as ").Append(Html(Account(place))).Append("</span></div></div><div class=\"box\">");
        if (share.Length > 0 && usable)
            body.Append(MakeLine("/backups/share", token, fromSteps, Hidden("id", id) + Hidden("share", share) + Hidden("path", sub), ShareLink(id, share, sub, fromSteps)));
        body.Append(FoundLine(listing.Backups));
        foreach (var name in listing.Names)
            body.Append(PlaceLine(name, null, null, share.Length == 0 ? ShareLink(id, name, null, fromSteps) : ShareLink(id, share, sub.Length == 0 ? name : sub + "\\" + name, fromSteps),
                share.Length == 0 ? ShareGlyph : FolderGlyph));
        // Nothing listed because the list wasn't read (the 30-second wait, a refusal): the way to read it again, rather than saying it's empty.
        if (listing.Names.Count == 0)
            body.Append("<p>").Append(usable ? share.Length == 0 ? "No shares were found." : "No folders here." : "<a href=\"" + Html(ShareLink(id, share, sub, fromSteps)) + "\">Try again</a>").Append("</p>");
        body.Append("</div>");
        if (listing.More) body.Append("<p class=\"hint\">Only the first ").Append(BackupFolderBrowser.Most).Append(listing.Names.Count > 0 && share.Length == 0 ? " shares" : " folders")
            .Append(" by name are shown. Enter the path instead to open another.</p>");
        // A share the list leaves out (a hidden one ending in $), a file server that won't list its shares, or a folder past the list's first ones: opened by its path.
        var current = share.Length == 0 ? "" : sub.Length == 0 ? share : share + "\\" + sub;
        body.Append("<details class=\"fold\"").Append(listing.More || share.Length == 0 && listing.Names.Count == 0 && listing.Outcome != "wait" ? " open" : "")
            .Append("><summary>Enter a path instead</summary><form method=\"get\" action=\"/backups/share\">").Append(Hidden("id", id)).Append(fromSteps ? Hidden("from", "steps") : "")
            .Append("<label for=\"share-path\">Share and folder</label><div class=\"pick\"><input id=\"share-path\" name=\"place\" required maxlength=\"1024\" spellcheck=\"false\" autocomplete=\"off\" value=\"")
            .Append(Html(current.Length == 0 ? "" : current + "\\")).Append("\"><button type=\"submit\" class=\"quiet\">Open</button></div></form></details>");
        body.Append(UseBar(token, fromSteps, share.Length > 0 && usable ? place.Display : null,
            Hidden("choice", id) + Hidden("share", share) + Hidden("subfolder", sub) + "<button type=\"submit\" name=\"do\" value=\"share_held\">Use this folder</button>",
            share.Length == 0 ? "Open a share, then the folder for your backups." : "Choose another folder."));
        return body.Append("</div>").ToString();
    }

    // Use this folder, from the share's list: the sign-in held under the id, checked (write, read back, remove) and kept. Back to Backups when it took; else the
    // list again, saying why.
    private async Task ChooseHeldShareAsync(HttpContext context, IFormCollection form, string? session)
    {
        var fromSteps = form["from"].ToString() == "steps";
        var id = form["choice"].ToString();
        if (HeldShare(id, session) is not { } signIn)
        {
            Redirect(context.Response, ConnectAt(fromSteps, "expired", HeldPrefill(id, session) is null ? null : id));
            return;
        }
        var share = form["share"].ToString();
        var subfolder = form["subfolder"].ToString();
        var result = await backups!.Service.ChooseSmbAsync(signIn.Server!, share, subfolder, signIn.User!, signIn.Domain ?? "", signIn.Password!, context.RequestAborted).ConfigureAwait(false);
        log($"server page backup destination outcome={result} via=browse");
        if (result is "chosen" or "chosen_settings")
        {
            DropShare(id);
            Redirect(context.Response, CardWith(fromSteps, result == "chosen" ? null : result));
        }
        else if (result == "auth")
        {
            Refuse(id);
            Redirect(context.Response, ConnectAt(fromSteps, "auth", id));
        }
        else Redirect(context.Response, ShareLink(id, share, subfolder, fromSteps, done: result));
    }
}
