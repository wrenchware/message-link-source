using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

// The page of a Windows server on its own PC. Messages opens it signed in with a one-time code (LocalPageLink), and a browser on this PC that is
// signed in is the PC's own user, so a step that asks for the page's password again takes it as given. Only a browser on this PC (loopback, never a relayed tailnet
// device, whose address is the device's own) has either; every other visitor gets the page exactly as on Linux.
internal sealed partial class ServerPage
{
    /// <summary>Takes a one-time code from Messages (<see cref="NetworkServerOptions.OnceSignIn"/>); null on a host without one.</summary>
    internal Func<string?, bool>? OnceSignIn { get; set; }

    /// <summary>A signed-in browser on this PC needs no password again (<see cref="NetworkServerOptions.DesktopTrust"/>, decided in <see cref="PasswordAgain"/>).</summary>
    internal bool DesktopTrust { get => passwordAgain.DesktopTrust; set => passwordAgain.DesktopTrust = value; }

    /// <summary>Reset server signs the tailnet door's node out first (<see cref="NetworkServerOptions.ResetLeavesTailnet"/>).</summary>
    internal bool ResetLeavesTailnet { get; set; }

    private static bool OnThisPc(HttpContext context) => context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    // `/login?once=<code>`: a browser on this PC with the code Messages made signs in as the password would sign it in. Anything else goes on to the sign-in form.
    private bool TryOnceSignIn(HttpContext context)
    {
        if (OnceSignIn is not { } take || !OnThisPc(context) || context.Request.Query["once"] is not { Count: 1 } code) return false;
        if (!take(code.ToString()))
        {
            log("server page sign_in outcome=once_refused");
            return false;
        }
        var expires = time.GetUtcNow() + SessionLifetime;
        context.Response.Cookies.Append(CookieName, NewSession(expires), Cookie(expires));
        log("server page sign_in outcome=signed_in via=once");
        Redirect(context.Response, SectionPath(Section(context.Request.Query["section"].ToString())));
        return true;
    }

    // A code brought by a browser already signed in: taken (and so spent) without signing in again or saying anything.
    private void SpendOnce(HttpContext context)
    {
        if (OnceSignIn is { } take && OnThisPc(context) && context.Request.Query["once"] is { Count: 1 } code) take(code.ToString());
    }

    /// <summary>
    /// Opens a service's sign-in on this PC's desktop (<see cref="NetworkServerOptions.DesktopSignIn"/>): the Windows server asks Messages, which owns the native sign-in
    /// windows. Null on every other server, whose page has no such buttons.
    /// </summary>
    internal Func<string, bool>? DesktopSignIn { get; set; }
    internal Func<string, string?, Func<bool>, Task<MessageLink.App.Services.NativeAuthenticationStart>>? DesktopAccountSignIn { get; set; }


    // Its GET is the front page.
    private static IEnumerable<Route> DesktopRoutes =>
    [
        new("/signin/desktop", Verbs.Both, (page, call) => call.Post ? page.DesktopSignInPostAsync(call.Context, call.Form!) : page.HomeAsync(call)),
    ];

    private const string DesktopElsewhere = "Open this server page in a browser on the PC that runs the server to connect accounts.";


    // One desktop sign-in as a form that posts it: a line's Connect button, or an item in an account's menu. `account` is the slot DesktopAccountSignIn opens.
    private static string DesktopForm(string token, string service, string? account, string text, string label, bool item) =>
        "<form method=\"post\" action=\"/signin/desktop\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\">"
        + (account is null ? "" : "<input type=\"hidden\" name=\"account\" value=\"" + Html(account) + "\">")
        + "<button type=\"submit\" name=\"service\" value=\"" + service + "\" class=\"" + (item ? "item" : "quiet") + "\" aria-label=\"" + Html(label) + "\">" + Html(text) + "</button></form>";

    // The walkthrough's Connect buttons (the Accounts card has them in its lines), for a signed-in browser on this PC only: the window opens on this PC's desktop, so a
    // browser elsewhere is not offered it. Once an account is connected the rest wait behind one button, and a service connected and well isn't offered again; Google
    // Voice takes more than one number.
    private void DesktopSignInForms(StringBuilder body, HttpContext context, string token, IReadOnlyList<ServerAccount>? accounts)
    {
        if (DesktopSignIn is null || !OnThisPc(context)) return;
        var any = accounts is { Count: > 0 };
        if (any) body.Append("<details><summary class=\"button\">Connect another service</summary>");
        body.Append("<p class=\"quiet\">Connect opens the service's own sign-in in Message Link on this PC.</p><div class=\"actions\">");
        foreach (var offers in Offers(accounts, context).Services.Take(SignInService.All.Count))
        {
            var name = offers.Service.Name;
            // This row names each service in its buttons, so they say what they're for (owner, 2026-11-26: "Add account: Messenger").
            void Action(SignInOffer offer, string? account = null) =>
                body.Append(DesktopForm(token, offers.Service.Desktop!, offer.Slot, offer.Named(name, account), offer.Named(name, account), item: false));
            if (DesktopAccountSignIn is not null)
            {
                foreach (var line in offers.Accounts)
                    if (line.SignInAgain is { } again) Action(again, line.Account.Name);
                if ((offers.Connect ?? offers.Add) is { } more) Action(more);
                continue;
            }
            // Without the chooser, a service connected and well isn't offered again; Google Voice still adds a number.
            if (offers.Add is null && offers.Accounts.Count > 0 && offers.Accounts.All(line => Tone(line.Account.State) == "ok")) continue;
            if ((offers.Connect ?? offers.Add ?? offers.Accounts.FirstOrDefault()?.SignInAgain) is { } offer) Action(offer);
        }
        body.Append("</div>");
        if (any) body.Append("</details>");
    }

    private async Task DesktopSignInPostAsync(HttpContext context, IFormCollection form)
    {
        var service = form["service"].ToString();
        if (DesktopSignIn is not { } open || !OnThisPc(context) || !SignInService.All.Any(known => known.Desktop == service))
        {
            Redirect(context.Response, "/");
            return;
        }
        var cookie = Session(context.Request);
        var account = form["account"].ToString();
        // One sign-in at a time: a click while one is open says so, rather than that Message Link couldn't be opened.
        var outcome = DesktopAccountSignIn is { } selected
            ? cookie is null ? MessageLink.App.Services.NativeAuthenticationStart.Refused
                : await selected(service, account.Length == 0 ? null : account, () => SessionOf(cookie) is not null).ConfigureAwait(false)
            : open(service) ? MessageLink.App.Services.NativeAuthenticationStart.Started : MessageLink.App.Services.NativeAuthenticationStart.Refused;
        var (word, done) = outcome switch
        {
            MessageLink.App.Services.NativeAuthenticationStart.Started => ("opened", "desktop_sign_in"),
            MessageLink.App.Services.NativeAuthenticationStart.Busy => ("busy", "desktop_sign_in_busy"),
            _ => ("failed", "desktop_sign_in_failed"),
        };
        log($"server page desktop sign_in service={service} outcome={word}");
        Redirect(context.Response, "/?done=" + done + "#accounts");
    }
}
