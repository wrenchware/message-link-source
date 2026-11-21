using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// The page's paths. Each section declares its own once (Route): the methods it takes, how large a signed-in browser's form may be, what answers it, whether the
// part of the server it needs is there, and which entry of the page's list it lights. What the door serves, the 405s, the form's size and the dispatch all come
// from that one table; ServerPageRouteChecks holds every path's facts as they stood before the table.
internal sealed partial class ServerPage
{
    [Flags]
    internal enum Verbs { Get = 1, Post = 2, Both = Get | Post }

    // When a path is answered: a picture or the font before anything else, needing no sign-in (Asset); before the sign-in is asked for (Open); or signed in.
    internal enum Stage { Asset, Open, SignedIn }

    /// <summary>One request on its way to its route's answer: the form (a POST's, else null), the sign-in's form token (empty before a sign-in) and the section
    /// a link asked to go to.</summary>
    private readonly record struct Call(HttpContext Context, string Path, IFormCollection? Form, string Token, string? Session, string? Section)
    {
        public HttpRequest Request => Context.Request;
        public HttpResponse Response => Context.Response;
        public bool Post => Form is not null;
        // A value from the form on a POST, from the query on a GET.
        public string Field(string name) => (Post ? Form![name] : Request.Query[name]).ToString();
    }

    /// <param name="Verbs">The methods it takes; any other is answered 405 before a form is read.</param>
    /// <param name="Answer">What answers it, once the method, the form and the sign-in are through.</param>
    private sealed record Route(string Path, Verbs Verbs, Func<ServerPage, Call, Task> Answer)
    {
        public Stage Stage { get; init; } = Stage.SignedIn;
        /// <summary>The most a signed-in browser's form may send here; anyone else's is held to <see cref="MaxBody"/>.</summary>
        public int Body { get; init; } = MaxBody;
        /// <summary>Whether the part of the server it needs is there; when not, it goes to the front page.</summary>
        public Func<ServerPage, bool>? Present { get; init; }
        /// <summary>The entry of the page's list it keeps lit.</summary>
        public string Place { get; init; } = "/";

        public bool Takes(bool get) => (Verbs & (get ? Verbs.Get : Verbs.Post)) != 0;
    }

    private static readonly Lazy<FrozenDictionary<string, Route>> Routes = new(() =>
        new[] { CoreRoutes, LogRoutes, JoinRoutes, PushRoutes, ManageRoutes, BackupRoutes, SetupRoutes, TailnetRoutes, DesktopRoutes, SignInRoutes, HermesRoutes,
            ReportRoutes, AddRoutes }
        .SelectMany(section => section).ToFrozenDictionary(route => route.Path, StringComparer.Ordinal));

    /// <summary>Whether a request is one of the page's (any other path stays the door's 404).</summary>
    public static bool Serves(string? path) => path is not null && Routes.Value.ContainsKey(path);

    /// <summary>Every path's facts, for the checks: its methods, stage, form size, whether it can be absent, and its entry.</summary>
    internal static IEnumerable<(string Path, Verbs Verbs, Stage Stage, int Body, bool Optional, string Place)> RouteFacts =>
        Routes.Value.Values.Select(route => (route.Path, route.Verbs, route.Stage, route.Body, route.Present is not null, route.Place));

    /// <summary>For the checks: told the path of each route a request reached, before its answer runs.</summary>
    internal Action<string>? Answering { get; set; }

    // Which entry a path belongs to: a page under one of them (the tailnet's steps, a backup's restore) keeps its entry lit.
    private static string Place(string path) => Routes.Value.TryGetValue(path, out var route) ? route.Place : "/";

    private static IEnumerable<Route> CoreRoutes =>
    [
        new(LogoPath, Verbs.Get, (page, call) => AssetAsync(call.Context, Logo.Value, "image/png", "max-age=86400")) { Stage = Stage.Asset },
        new(PicturesPath, Verbs.Get, (page, call) => AssetAsync(call.Context, Pictures.Value, "image/png", "max-age=86400")) { Stage = Stage.Asset },
        new(FontPath, Verbs.Get, (page, call) => AssetAsync(call.Context, Font.Value, "font/woff2", "max-age=31536000, immutable")) { Stage = Stage.Asset },
        new("/login", Verbs.Both, (page, call) => page.LoginAsync(call)) { Stage = Stage.Open },
        new("/", Verbs.Get, (page, call) => page.HomeAsync(call)),
        new("/logout", Verbs.Post, (page, call) =>
        {
            var kept = page.SignOutEverywhere();
            call.Response.Cookies.Delete(CookieName, Cookie(DateTimeOffset.UnixEpoch));
            Redirect(call.Response, kept ? "/login" : "/login?signed_out=unsaved");
            return Task.CompletedTask;
        }),
        new("/pair", Verbs.Both, (page, call) => page.PairAsync(call)),
        new("/devices/details", Verbs.Get, (page, call) => page.DeviceDetailsAsync(call.Context, call.Request.Query["id"].ToString())),
        new("/devices/remove", Verbs.Both, (page, call) => page.RemoveDeviceAsync(call.Context, call.Field("id"), call.Form, call.Token)),
        new("/devices/rename", Verbs.Both, (page, call) => page.RenameDeviceAsync(call.Context, call.Field("id"), call.Form, call.Token)),
        new("/accounts/remove", Verbs.Both, (page, call) => page.RemoveAccountAsync(call.Context, call.Field("id"), call.Form, call.Token)),
        new("/mirror", Verbs.Post, (page, call) => page.MirrorAsync(call)) { Place = "/manage/sync" },
    ];

    private static async Task AssetAsync(HttpContext context, byte[] bytes, string type, string cache)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = type;
        response.Headers.CacheControl = cache;
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Answers one request of the page, and closes its connection.</summary>
    public async Task HandleAsync(HttpContext context, bool draining)
    {
        var request = context.Request;
        var response = context.Response;
        Secure(response);
        if (draining)
        {
            await PlainAsync(context, StatusCodes.Status503ServiceUnavailable, "The server is stopping.").ConfigureAwait(false);
            return;
        }
        var path = request.Path.Value;
        var route = path is null ? null : Routes.Value.GetValueOrDefault(path);
        var get = HttpMethods.IsGet(request.Method);
        var post = HttpMethods.IsPost(request.Method);
        if (get && route is { Stage: Stage.Asset })
        {
            Answering?.Invoke(route.Path);
            await route.Answer(this, new(context, route.Path, null, "", null, null)).ConfigureAwait(false);
            return;
        }
        if (!get && !post || route is not null && !route.Takes(get))
        {
            await PlainAsync(context, StatusCodes.Status405MethodNotAllowed, "There's nothing at this address.").ConfigureAwait(false);
            return;
        }
        // A form comes from this page or not at all: its own origin (which a browser always names on a POST), and no larger than a form.
        // Read from the cookie alone, before any body: the upload's larger limit is for a signed-in browser only, so nobody else can hold one of the door's
        // places for minutes dripping 64 KiB (review, 2026-10-17).
        var session = Session(request);
        // A signed-in browser on the Windows server's own PC, or one that typed the password a moment ago, is not asked for it again, so its forms leave the field out
        // (ServerPage.Desktop.cs).
        if (session is not null && (DesktopTrust && OnThisPc(context) || RecentlyTyped(session))) context.Items[PasswordGivenItem] = true;
        IFormCollection? form = null;
        if (post)
        {
            if (!SameOrigin(request))
            {
                await PlainAsync(context, StatusCodes.Status403Forbidden, "That request came from another website, so the server ignored it.").ConfigureAwait(false);
                return;
            }
            // The notifications form carries files; the restore's review of the devices, with a box for each, and the Hermes forms are larger than a form of one button.
            var most = session is not null && route is not null ? route.Body : MaxBody;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = most;
            if (!request.HasFormContentType || request.ContentLength is not { } length || length < 0 || length > most)
            {
                await PlainAsync(context, StatusCodes.Status400BadRequest, "The server couldn't read that form. Go back and try again.").ConfigureAwait(false);
                return;
            }
            try { form = await request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false); }
            catch (Exception failure) when (failure is InvalidDataException or IOException or BadHttpRequestException)
            {
                await PlainAsync(context, StatusCodes.Status400BadRequest, "The server couldn't read that form. Go back and try again.").ConfigureAwait(false);
                return;
            }
        }

        // The server's first run (ServerPage.Setup.cs): until a password is chosen, the setup screen is all the page shows.
        if (settingUp)
        {
            await FirstRunAsync(context, path, form).ConfigureAwait(false);
            return;
        }

        // A link to a place on the page (`/?section=storage`) goes there once signed in; the sign-in form carries the section along, so it survives it.
        var section = Section((post ? form!["section"] : request.Query["section"]).ToString());
        if (route is { Stage: Stage.Open })
        {
            Answering?.Invoke(route.Path);
            await route.Answer(this, new(context, route.Path, form, "", session, section)).ConfigureAwait(false);
            return;
        }
        if (session is null)
        {
            // A form without a sign-in does nothing; a page asks for one.
            if (post) await PlainAsync(context, StatusCodes.Status403Forbidden, "Sign in first.").ConfigureAwait(false);
            else Redirect(response, path == "/" && section is not null ? "/login?section=" + section : "/login");
            return;
        }
        if (get && path == "/" && section is not null)
        {
            Redirect(response, SectionPath(section));
            return;
        }
        if (post && !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(form!["form"].ToString()), Encoding.ASCII.GetBytes(FormToken(session))))
        {
            await PlainAsync(context, StatusCodes.Status403Forbidden, "That form is out of date. Go back, reload the page and try again.").ConfigureAwait(false);
            return;
        }
        var token = FormToken(session);
        context.Items[NavItem] = Nav(path ?? "/", token);
        var call = new Call(context, path ?? "/", form, token, session, section);
        if (route is null)
        {
            await HomeAsync(call).ConfigureAwait(false);
            return;
        }
        Answering?.Invoke(route.Path);
        if (route.Present?.Invoke(this) == false)
        {
            Redirect(response, "/");
            return;
        }
        await route.Answer(this, call).ConfigureAwait(false);
    }

    private async Task HomeAsync(Call call)
    {
        var home = await HomeBodyAsync(call.Request, call.Token, call.Request.Query["done"].ToString(), call.Context.RequestAborted).ConfigureAwait(false);
        await PageAsync(call.Context, StatusCodes.Status200OK, SiteName, home.Body, home.Refresh, home.RefreshTo).ConfigureAwait(false);
    }

    private async Task LoginAsync(Call call)
    {
        var (context, section) = (call.Context, call.Section);
        if (call.Session is not null) { Redirect(call.Response, SectionPath(section)); return; }
        if (!call.Post)
        {
            // Messages opening its own server's page on this PC (ServerPage.Desktop.cs).
            if (TryOnceSignIn(context)) return;
            // A sign-out the server could not keep is said, so the owner knows a restart would undo it.
            var unsaved = call.Request.Query["signed_out"] == "unsaved" ? "Signed out, but the server couldn't save it, so starting the server again would undo it. Change the password to sign every browser out for good." : null;
            await PageAsync(context, StatusCodes.Status200OK, "Sign in", SignInBody(unsaved, section)).ConfigureAwait(false);
            return;
        }
        await SignInAsync(context, call.Form!, section).ConfigureAwait(false);
    }

    private async Task PairAsync(Call call)
    {
        var (context, token) = (call.Context, call.Token);
        if (DesktopJoinUnavailable)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Add a PC", "<section>" + DesktopJoinHelp + "</section>").ConfigureAwait(false);
            return;
        }
        // A code lets a device read everything: the password itself is asked for, not only the sign-in a copied cookie would be.
        if (!call.Post)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Add a PC", AskBody("/pair", token, PairAsk, "Show the line", null)).ConfigureAwait(false);
            return;
        }
        var pairFromSetup = FromSetup(call.Request, call.Form);
        if (!await PasswordAgainAsync(context, call.Form!, "pair", "Add a PC", problem => AskBody("/pair", token, PairAsk, "Show the line", problem, before: pairFromSetup ? FromSetupField : "")).ConfigureAwait(false)) return;
        var code = pairing.Issue();
        await PageAsync(context, StatusCodes.Status200OK, "Add a PC", PairBody(call.Request, code, pairFromSetup)).ConfigureAwait(false);
    }

    private async Task MirrorAsync(Call call)
    {
        // Reads each service's most recently active conversations again, as the walk does every half hour: nothing is sent or marked read.
        if (history is not null)
            try { await history.MirrorAsync(new HistoryMirrorParams(Start: true), call.Context.RequestAborted).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { log($"server page mirror failed type={failure.GetType().Name}"); }
        Redirect(call.Response, "/manage/sync?done=mirror");
    }
}
