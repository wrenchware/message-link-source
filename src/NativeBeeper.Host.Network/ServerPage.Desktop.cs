using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NativeBeeper.Host.Network;

// The page of a Windows server on its own PC (SWITCHBOARD.md 10.7). Messages opens it signed in with a one-time code (LocalPageLink), and a browser on this PC that is
// signed in is the PC's own user, so a step that asks for the page's password again takes it as given. Only a browser on this PC (loopback, never a relayed tailnet
// device, whose address is the device's own) has either; every other visitor gets the page exactly as on Linux.
internal sealed partial class ServerPage
{
    /// <summary>Takes a one-time code from Messages (<see cref="NetworkHostOptions.OnceSignIn"/>); null on a host without one.</summary>
    internal Func<string?, bool>? OnceSignIn { get; set; }

    /// <summary>A signed-in browser on this PC needs no password again (<see cref="NetworkHostOptions.DesktopTrust"/>).</summary>
    internal bool DesktopTrust { get; set; }

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
        Redirect(context.Response, "/");
        return true;
    }

    // A step that asks for the password again (a code, a removal, the backup's password), from a signed-in browser on this PC: the PC's own user, whose session only
    // the code from Messages or the password could have made.
    private bool TrustedHere(HttpContext context, string purpose) => DesktopTrust && purpose != "sign_in" && OnThisPc(context);

    /// <summary>
    /// Opens a service's sign-in on this PC's desktop (<see cref="NetworkHostOptions.DesktopSignIn"/>): the Windows server asks Messages, which owns the native sign-in
    /// windows (owner D2). Null on every other host, whose page has no such buttons.
    /// </summary>
    internal Func<string, bool>? DesktopSignIn { get; set; }

    private static readonly (string Service, string Name)[] DesktopServices =
        [("google-messages", "Google Messages"), ("messenger", "Messenger"), ("google-voice", "Google Voice"), ("instagram", "Instagram")];

    private static bool IsDesktopPath(string? path) => path is "/signin/desktop";

    // The Accounts card's Connect buttons, for a signed-in browser on this PC only: the window opens on this PC's desktop, so a browser elsewhere is not offered it.
    private void DesktopSignInForms(StringBuilder body, HttpContext context, string token)
    {
        if (DesktopSignIn is null || !OnThisPc(context)) return;
        body.Append("<p class=\"quiet\">Connect opens the service's own sign-in in Messages on this PC.</p><div class=\"actions\">");
        foreach (var (service, name) in DesktopServices)
            body.Append("<form method=\"post\" action=\"/signin/desktop\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                .Append("<button type=\"submit\" name=\"service\" value=\"").Append(service).Append("\" class=\"quiet\">Connect ").Append(Html(name)).Append("</button></form>");
        body.Append("</div>");
    }

    private void DesktopSignInPost(HttpContext context, IFormCollection form)
    {
        var service = form["service"].ToString();
        if (DesktopSignIn is not { } open || !OnThisPc(context) || !DesktopServices.Any(known => known.Service == service))
        {
            Redirect(context.Response, "/");
            return;
        }
        var opened = open(service);
        log($"server page desktop sign_in service={service} outcome={(opened ? "opened" : "failed")}");
        Redirect(context.Response, opened ? "/?done=desktop_sign_in#accounts" : "/?done=desktop_sign_in_failed#accounts");
    }

    private const string DesktopItem = "messages-desktop";
    private static readonly System.Text.RegularExpressions.Regex PasswordAgain = new(
        "<label for=\"password\">(?:Server password|Password)</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required(?: autofocus)?>",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The forms a signed-in browser on this PC is shown carry no password field: the step takes it as given (TrustedHere), and the page says nothing it would need.
    private static string WithoutPasswordAgain(HttpContext context, string body) =>
        context.Items.ContainsKey(DesktopItem) ? PasswordAgain.Replace(body, "<input type=\"hidden\" name=\"password\" value=\"\">") : body;
}
