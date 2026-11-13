using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// Tailscale on the server's page (SWITCHBOARD.md 4.7 and 10.8, D9): the door set up from here rather than from the container's log. Signing the server in (a button
// and the same link as a QR code, for signing in on a phone), signing in again when Tailscale signed it out, approving it, and a checklist of what is left, each line
// in plain words with a link to the exact page of Tailscale's admin console and a tick once the server sees it done: key expiry off for the server and each Message Link
// phone and PC, only the owner's devices reaching it, and Tailnet Lock when the tailnet uses it. "Fix it for me" does what the API can with an OAuth client the owner gives
// once (TailscaleAdmin); "Use another Tailscale account" signs the door out and forgets it. Only the signed-in owner sees this, from home, so the sign-in link and the
// owner's login are shown here and nowhere else.
internal sealed partial class ServerPage
{
    private const string TailscaleMachines = "https://login.tailscale.com/admin/machines";
    private const string TailscaleAccessControls = "https://login.tailscale.com/admin/acls/file";
    private const string TailscaleLock = "https://login.tailscale.com/admin/settings/tailnet-lock";

    private static bool IsTailnetPath(string? path) => path is "/tailnet/signout" or "/tailnet/oauth" or "/tailnet/oauth/remove" or "/tailnet/fix" or "/tailnet/turn";

    // Whether the page should look again soon: the door is starting, or waits to be signed in or approved.
    private bool TailnetWaiting => tailnet is { Enabled: true } board && board.Latest is not { Running: true };

    private static string? TailnetNotice(string done) => done switch
    {
        "tailnet_signed_out" => "The server signed out of Tailscale and forgot its machine there. Sign it in below with the account you want. The old machine stays in Tailscale's list until you remove it.",
        "tailnet_no_door" => "Tailscale isn't running on the server just now, so nothing was signed out. Try again in a minute.",
        "tailnet_oauth_saved" => "OAuth client saved. Choose Fix it for me to use it.",
        "tailnet_oauth_removed" => "The OAuth client was removed from the server. Delete it in Tailscale too if you no longer need it.",
        "tailnet_turn_failed" => "Tailscale couldn't be turned on or off just now, so nothing changed. Try again in a minute.",
        _ => null,
    };

    // The machine's own page in Tailscale's console, by its address.
    private static string MachinePage(string? address) => TailscaleMachines + (address is { Length: > 0 } ip ? "/" + Uri.EscapeDataString(ip) : "");

    // The card: how to turn the door on, its sign-in while it needs one, then what is done and what is left.
    // walkthrough: the setup's own screen, which shows only the account and what is left, and keeps its links inside setup.
    private void TailnetCard(StringBuilder body, TailnetBoard board, string token, bool walkthrough = false)
    {
        TailnetCardBody(body, board, token, walkthrough);
        // Where the page is the switch (the Windows server), turning it off is offered under the card once it is on.
        if (board.Enabled && board.Turn is not null && !walkthrough)
            body.Append("<p class=\"quiet\">").Append(Html("Tailscale runs inside Message Link Server on this PC.")).Append("</p>").Append(TailnetTurnForm(token, on: false));
        // What Tailscale is and what it can see, on a screen of its own: this page reloads while it waits, which would fold a <details> shut mid-read.
        body.Append("<section><div class=\"box\">").Append(Go("How remote access works", "What Tailscale is, and who can reach the server.", null,
            walkthrough ? "/manage/tailscale/about?from=welcome" : "/manage/tailscale/about", "How remote access works")).Append("</div></section>");
    }

    // For someone who has never heard of Tailscale: what it is, what it can and can't see, and who gets in. Plain facts; the setup steps are the card's.
    // Words like tailnet and WireGuard are left to Learn more (owner, 2026-11-10). Built into Message Link on the phone and on Windows alike.
    private static void TailnetAbout(StringBuilder body, bool walkthrough)
    {
        body.Append("<section>")
            .Append(Para("Tailscale joins your own devices into a private network, so your phone reaches this server from anywhere as if it were at home. Nothing on your home network is opened to the internet."))
            .Append(Para("Your devices encrypt everything they send each other, with keys that never leave them. Tailscale's servers only help them find each other and can't read what passes between them."))
            .Append(Para("Only devices signed in to your Tailscale account can reach the server, and each one still has to be added to the server before it sees your conversations."))
            .Append(Para("Tailscale is built into Message Link, so there's nothing else to install."))
            .Append("<p><a href=\"https://tailscale.com/kb/1151/what-is-tailscale\" target=\"_blank\" rel=\"noopener noreferrer\">Learn more</a></p>")
            .Append(walkthrough ? "<p><a href=\"/welcome?step=tailscale\">Back to setup</a></p></section>" : "<p><a href=\"/manage/tailscale\">Back to Remote access</a></p></section>");
    }

    private static string TailnetTurnForm(string token, bool on) =>
        "<form method=\"post\" action=\"/tailnet/turn\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\"><button type=\"submit\" name=\"do\" value=\""
        + (on ? "on\">Turn on Tailscale" : "off\" class=\"quiet\">Turn off Tailscale") + "</button></form>";

    private void TailnetCardBody(StringBuilder body, TailnetBoard board, string token, bool walkthrough)
    {
        if (!board.Enabled && board.Turn is not null)
        {
            // The Windows server (SWITCHBOARD.md 10.7): a switch, here and now; Tailscale runs inside Messages Server, with nothing else to install.
            body.Append("<section id=\"tailnet\"><h2>Away from home</h2>")
                .Append(Para("Message Link on a phone or laptop reaches this PC's Message Link Server through Tailscale, so it works anywhere without opening this PC to the internet. "
                    + "Tailscale runs inside Message Link Server: there is nothing else to install. Once it is on, this page walks you through signing it in."))
                .Append(TailnetTurnForm(token, on: true))
                .Append("</section>");
            return;
        }
        if (!board.Enabled)
        {
            body.Append("<section id=\"tailnet\"><h2>Away from home</h2>")
                .Append(Para("Message Link on a phone or laptop reaches this server through Tailscale, so it works anywhere without opening the server to the internet."))
                .Append(Para("To use it, turn it on where this server was installed, then start the server again: on Unraid, set Reach from your tailnet to 1 in the container's settings; "
                    + "with Docker Compose, MESSAGES_TAILNET=1 in the .env file; on Linux or Proxmox, MESSAGES_TAILNET=1 in /etc/messages-server/environment. This page then walks you through the rest."))
                .Append("</section>");
            return;
        }
        var view = board.Latest;
        if (view is null)
        {
            body.Append("<section id=\"tailnet\"><h2>Tailscale</h2>").Append(Para("Starting Tailscale…")).Append("</section>");
            return;
        }
        body.Append(view.NeedsAttention ? "<section class=\"attention\" id=\"tailnet\">" : "<section id=\"tailnet\">").Append("<h2>Tailscale</h2>");
        if (view.State == "NeedsLogin")
        {
            SignInPart(body, view);
            body.Append("</section>");
            return;
        }
        if (view.NeedsApproval)
        {
            body.Append("<div class=\"box\">").Append(Line(false, "Approve the server", "Your Tailscale network asks for each new machine to be approved. Open it in Tailscale and choose Approve.",
                Link(MachinePage(view.Address), "Open in Tailscale"))).Append("</div>");
            FixPart(body, token, view);
            body.Append("</section>");
            return;
        }
        if (!view.Running)
        {
            body.Append(Para(view.State == "Starting" ? "Connecting to Tailscale…" : "Tailscale isn't connected just now. The server keeps trying.")).Append("</section>");
            return;
        }
        var owned = !view.Tagged && view.Owner is { Length: > 0 } owner ? owner : null;
        // Signing in as someone else sits in the account's own line.
        var change = "<a class=\"button quiet\" href=\"/tailnet/signout\" aria-label=\"Use another Tailscale account\">Change…</a>";
        if (walkthrough)
            // Setup shows only who it's signed in as; the name and address are for troubleshooting, on Remote access.
            body.Append("<div class=\"box\">").Append(Row(owned is null ? "Signed in" : "Signed in as " + owned, null, null, change)).Append("</div>");
        else
        {
            var name = view.Name is { Length: > 0 } n ? n : "messages-server";
            body.Append("<div class=\"sh\"><h2>This server</h2></div><div class=\"box\">").Append(CopyRow("Tailscale name", board.Port == NetworkEndpoint.DefaultPort ? name : $"{name}:{board.Port}"));
            // Labelled as Tailscale's console labels it, so a 100.x address isn't taken for one on the home network.
            if (view.Address is { Length: > 0 } address) body.Append(CopyRow("Tailscale IP", address));
            body.Append(Row("Tailscale account", owned, null, change));
            body.Append("</div>");
        }
        // Setup lists only what is left: a check that needs nothing is noise there.
        if (walkthrough && view.AllDone) { body.Append("</section>"); return; }

        var lines = new StringBuilder();
        void Done(string title) { if (!walkthrough) lines.Append(Line(true, title, null, null)); }
        if (view.Tagged) lines.Append(Line(false, "Tagged", "The server has a tag in Tailscale, so it can't tell which devices are yours. Your Tailscale access rules alone decide who can reach it.", null));

        // Tailscale's own advice for a server is key expiry off, so the button names that, and the page ticks it by itself once Tailscale says so.
        if (view.ExpiryOff) Done("Server stays signed in");
        else if (view.Expiry is { } expiry)
            lines.Append(Line(false, "Keep the server signed in", $"Tailscale signs the server out{Until(expiry)} unless you turn that off. On the page that opens, choose Machine settings, then Disable key expiry. This page notices by itself.",
                Link(MachinePage(view.Address), "Open in Tailscale")));

        foreach (var phone in view.Phones ?? [])
        {
            if (phone.ExpiryOff) Done($"{phone.Name} stays signed in");
            else
                lines.Append(Line(false, $"Keep {phone.Name} signed in", $"Tailscale signs this {(phone.Pc ? "PC" : "phone")} out{Until(phone.Expiry)} unless you turn that off. On the page that opens, choose Machine settings, then Disable key expiry.",
                    Link(MachinePage(phone.Address), "Open in Tailscale")));
        }

        var rule = Html($"\"grants\": [\n  {{ \"src\": [\"{view.Owner ?? "you@example.com"}\"], \"dst\": [\"{view.Address ?? "100.x.y.z"}\"], \"ip\": [\"tcp:{board.Port}\"] }}\n]");
        var pre = "<pre class=\"copy\">" + rule + "</pre>";
        var shown = "<details><summary>Show the rule</summary>" + pre + "</details>" + Link(TailscaleAccessControls, "Open access controls");
        switch (view.Rules)
        {
            case "yours": Done("Only your devices reach it"); break;
            case "open" when view.OnlyYours && walkthrough: break;
            case "open" when view.OnlyYours:
                // Nothing to do unless the network is ever shared, so the words wait behind the rule that would be needed then.
                lines.Append(Line(true, "Only your devices reach it", null, "<details><summary>If you ever share your network</summary><p>Only your own devices are on your Tailscale network, so they're the only ones that can reach the server. "
                    + "If you ever share your network with someone, swap Tailscale's allow-everyone rule for this one.</p>" + pre + Link(TailscaleAccessControls, "Open access controls") + "</details>"));
                break;
            case "open":
                // The server's own check is the fence that matters; the rule is a second one.
                var who = view.Everyone ? "every device on your Tailscale network" : view.Others == 1 ? "1 device that isn't yours" : $"{view.Others} devices that aren't yours";
                lines.Append(Line(false, "Keep other devices out", $"The server already turns away devices that aren't yours, but your Tailscale access rules still let {who} reach it. For a second lock, swap the allow-everyone rule for the one below.", shown));
                break;
            case "none":
                lines.Append(Line(false, "Let your devices reach it", "Your access rules let no device reach the server, your phone included. Add this rule to your access controls.", shown));
                break;
            default: Done("Only your devices reach it"); break;
        }

        switch (view.Lock)
        {
            case "signed": Done("Trusted by Tailnet Lock"); break;
            case "unsigned":
                lines.Append(Line(false, "Sign it for Tailnet Lock", "Your network uses Tailnet Lock, so your devices can't reach the server until one of your signing devices signs it.",
                    view.NodeKey is { Length: > 0 } key
                        ? "<details><summary>Show the command</summary><span class=\"copy block\">tailscale lock sign " + Html(key) + "</span></details>" + Link(TailscaleLock, "Open Tailnet Lock")
                        : Link(TailscaleLock, "Open Tailnet Lock")));
                break;
        }
        if (lines.Length > 0) body.Append(walkthrough ? "<div class=\"box\">" : "<div class=\"sh\"><h2>Checks</h2></div><div class=\"box\">").Append(lines).Append("</div>");
        if (walkthrough)
        {
            // Going on without it is fine; where it waits is said, so it isn't lost.
            if (!view.ExpiryOff && view.Expiry is not null) body.Append("<p class=\"quiet\">You can turn it off later from the server's page. It's on your list there.</p>");
        }
        // "Fix it for me" is offered on Remote access and with a phone's code, where it saves a step per device; not in setup's first look at Tailscale (owner, 2026-11-10).
        else if (!view.AllDone) FixPart(body, token, view);
        body.Append("</section>");
    }

    // One check of the card: done says only that; to do says why, in amber, with what fixes it under it.
    private static string Line(bool done, string title, string? text, string? then, string word = "To do")
    {
        var row = done ? Row(title, null, "Done", null) : Row(title, text, word, null, warn: true);
        return then is null ? row : "<div class=\"line\">" + row + "<div class=\"more flat\">" + then + "</div></div>";
    }

    private static string CopyRow(string name, string value) => "<div class=\"row\"><div class=\"g\">" + Html(name) + "</div><span class=\"v copy\">" + Html(value) + "</span></div>";

    private static string Until(string? expiry) =>
        DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? " on " + at.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";

    // Signing in, or in again: the button, the same link as a QR code for a phone, and the word that it moves on by itself.
    private static void SignInPart(StringBuilder body, TailnetView view)
    {
        if (!view.NeedsLogin)
        {
            body.Append(Para(view.SignedOut ? "Tailscale signed the server out. Asking Tailscale for a new sign-in link…" : "Asking Tailscale for a sign-in link…"));
            return;
        }
        body.Append("<div class=\"qr-card\"><div class=\"qr small\">").Append(QrCode.Encode(view.Login!).ToSvg(label: "The sign-in link as a QR code")).Append("</div><div class=\"grow\">");
        body.Append(view.SignedOut
            ? Para("Tailscale signed the server out, so your phone and laptop can't reach it away from home. Sign it in again with your Tailscale account.")
            // The phone and the PC sign in with "the Tailscale account your server uses" (android JoinScreen, the Windows app's Server screen), so a second account here is the likeliest way to get it wrong.
            : Para("Sign the server in to Tailscale. Use the same Tailscale account you'll use on your phone and laptop. If you don't have one, signing in makes a free one."));
        body.Append("<p><a class=\"button\" href=\"").Append(Html(view.Login)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
            .Append(view.SignedOut ? "Sign in again" : "Sign in to Tailscale").Append("</a></p>")
            .Append("<p class=\"quiet\">Or scan the code to sign in on your phone. This page moves on by itself once you're signed in. If Tailscale says your session expired, open Sign in to Tailscale in a private window.</p></div></div>");
    }

    // "Fix it for me": the OAuth client asked for once, then a button that does every step the API can.
    private void FixPart(StringBuilder body, string token, TailnetView view)
    {
        if (tailscale is null) return;
        body.Append("<div class=\"sh\"><h2>Fix it for me</h2></div><div class=\"box\">");
        if (!tailscale.Has)
            body.Append(Row("OAuth client", "Give the server one and it does the steps above for you: it keeps itself, your phones and your PCs signed in, approves them if your network asks, and adds the access rule. Signing for Tailnet Lock stays with you.", "Not added", null))
                .Append("</div><div class=\"btns\"><form method=\"get\" action=\"/tailnet/oauth\"><button type=\"submit\" class=\"quiet\">Add an OAuth client…</button></form></div>");
        else
            body.Append(Row("OAuth client", null, "Saved", null))
                .Append("</div><div class=\"btns\">").Append(Form("/tailnet/fix", token, "Fix it for me")).Append(Form("/tailnet/oauth/remove", token, "Remove the OAuth client", quiet: true)).Append("</div>");
    }

    private async Task TailnetAsync(HttpContext context, string path, IFormCollection? form, string token)
    {
        var post = form is not null;
        switch (path)
        {
            // The Windows server's switch (TailnetBoard.Turn): kept for the next start and applied now. A form of this page, from a signed-in browser.
            case "/tailnet/turn" when post && tailnet?.Turn is { } turn:
            {
                var on = form!["do"] == "on";
                var made = turn(on);
                log($"server page tailnet turn on={(on ? "yes" : "no")} outcome={(made ? "done" : "failed")}");
                Redirect(context.Response, made ? "/manage/tailscale" : "/manage/tailscale?done=tailnet_turn_failed");
                return;
            }
            case "/tailnet/turn":
                Redirect(context.Response, "/");
                return;
            case "/tailnet/signout":
            {
                string Ask(string? problem) => AskBody("/tailnet/signout", token,
                    "Sign the server out of Tailscale and forget its machine? Your phones and laptops can't reach it away from home until it's signed in again, with this account or another. "
                    + "Nothing else on the server changes.", "Sign out of Tailscale", problem, danger: true);
                if (!post)
                {
                    await PageAsync(context, StatusCodes.Status200OK, "Use another Tailscale account", Ask(null)).ConfigureAwait(false);
                    return;
                }
                if (!await PasswordAgainAsync(context, form!, "tailnet_sign_out", "Use another Tailscale account", Ask).ConfigureAwait(false)) return;
                var asked = tailnet?.SignOut?.Invoke() == true;
                log($"server page tailnet sign_out outcome={(asked ? "asked" : "no_door")}");
                Redirect(context.Response, asked ? "/manage/tailscale?done=tailnet_signed_out" : "/manage/tailscale?done=tailnet_no_door");
                return;
            }
            case "/tailnet/oauth" when tailscale is not null:
            {
                string Ask(string? problem)
                {
                    var page = new StringBuilder("<section>");
                    if (problem is not null) page.Append(Notice(problem));
                    page.Append(Para("Make an OAuth client in Tailscale and paste its ID and secret here once. The server keeps them sealed with its own keys, never in a backup, and you can remove them here at any time."))
                        .Append("<ol class=\"steps\"><li><a href=\"").Append(TailscaleAdmin.OAuthPage).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Open OAuth clients in Tailscale</a> and choose Generate OAuth client.</li>")
                        .Append("<li>Give it two scopes, both with Write: <strong>Devices: Core</strong> (to turn off key expiry and approve machines) and <strong>Policy File</strong> (to add the access rule).</li>")
                        .Append("<li>Choose Generate client, then copy the client ID and the secret. Tailscale shows the secret only once.</li></ol>")
                        .Append("<form method=\"post\" action=\"/tailnet/oauth\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                        .Append("<label for=\"client\">Client ID</label><input id=\"client\" name=\"client\" type=\"text\" autocomplete=\"off\" spellcheck=\"false\" required>")
                        .Append("<label for=\"secret\">Client secret</label><input id=\"secret\" name=\"secret\" type=\"password\" autocomplete=\"off\" required>")
                        .Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
                        .Append("<button type=\"submit\">Save</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
                    return page.Append("</section>").ToString();
                }
                const string title = "Tailscale OAuth client";
                if (!post)
                {
                    await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
                    return;
                }
                if (!await PasswordAgainAsync(context, form!, "tailnet_oauth", title, Ask).ConfigureAwait(false)) return;
                var (client, secret) = (form!["client"].ToString(), form["secret"].ToString());
                if (!TailscaleAdmin.LooksRight(client, secret))
                {
                    await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("That doesn't look like an OAuth client. The secret begins with tskey-client-.")).ConfigureAwait(false);
                    return;
                }
                if (!await tailscale.SaveAsync(client, secret).ConfigureAwait(false))
                {
                    await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't save it. Try again.")).ConfigureAwait(false);
                    return;
                }
                Redirect(context.Response, "/manage/tailscale?done=tailnet_oauth_saved");
                return;
            }
            case "/tailnet/oauth/remove" when tailscale is not null && post:
                tailscale.Remove();
                Redirect(context.Response, "/manage/tailscale?done=tailnet_oauth_removed");
                return;
            case "/tailnet/fix" when tailscale is not null && post && tailnet?.Latest is { } view:
            {
                var report = await tailscale.FixAsync(view, tailnet.Port, context.RequestAborted).ConfigureAwait(false);
                var page = new StringBuilder("<section>");
                if (report.Problem is not null) page.Append(Notice(report.Problem));
                if (report.Changed.Count + report.Unchanged.Count > 0)
                {
                    page.Append("<ul class=\"rows\">");
                    foreach (var line in report.Changed) Step(page, true, "Changed", line);
                    foreach (var line in report.Unchanged) Step(page, true, "Already so", line, word: "Nothing to do");
                    page.Append("</ul>");
                }
                else if (report.Problem is null) page.Append(Para("There was nothing to change."));
                page.Append(Para("Tailscale's own settings take a minute to reach the server; the checklist then ticks by itself."))
                    .Append("<p><a href=\"/manage/tailscale\">Back to Remote access</a></p></section>");
                await PageAsync(context, StatusCodes.Status200OK, "Fix it for me", page.ToString()).ConfigureAwait(false);
                return;
            }
            default:
                Redirect(context.Response, "/");
                return;
        }
    }
}
