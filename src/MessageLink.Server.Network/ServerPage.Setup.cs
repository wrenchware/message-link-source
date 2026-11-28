using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>
/// What the server's first run on its page needs (SWITCHBOARD.md 10.4): where the setup code is printed (the container's own output, never the log file on the data
/// volume; null prints it to the log), and Tailscale's API for "Fix it for me" (10.8), when the server offers it.
/// </summary>
internal sealed record ServerPageSetup(Action<string>? Show, TailscaleAdmin? Tailscale = null);

/// <summary>
/// The page's password as the owner chose it on the first run: a salted PBKDF2-SHA256 hash kept with the server's keys (<see cref="FileName"/>), never the password.
/// The file is one line, <c>pbkdf2-sha256 &lt;rounds&gt; &lt;salt&gt; &lt;hash&gt;</c>, base64. A password in <c>MESSAGES_ADMIN_PASSWORD</c> always wins over it.
/// </summary>
internal sealed class StoredPassword
{
    public const string FileName = "server-page.password";

    /// <summary>The rounds a new hash is made with (OWASP's figure for PBKDF2-SHA256).</summary>
    public const int Rounds = 600_000;

    private const string Kind = "pbkdf2-sha256";
    private readonly int rounds;
    private readonly byte[] salt, hash;

    /// <summary>The file's line, which the page's sign-ins are keyed by: a new password ends every sign-in made with the old.</summary>
    public string Line { get; }

    private StoredPassword(int rounds, byte[] salt, byte[] hash)
    {
        this.rounds = rounds; this.salt = salt; this.hash = hash;
        Line = $"{Kind} {rounds.ToString(CultureInfo.InvariantCulture)} {Convert.ToBase64String(salt)} {Convert.ToBase64String(hash)}";
    }

    public static StoredPassword Make(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(Rounds, salt, Rfc2898DeriveBytes.Pbkdf2(password, salt, Rounds, HashAlgorithmName.SHA256, 32));
    }

    /// <summary>Whether <paramref name="presented"/> is the password, compared in fixed time.</summary>
    public bool Verify(string presented) =>
        CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(presented, salt, rounds, HashAlgorithmName.SHA256, hash.Length), hash);

    /// <summary>The kept password, or null when there is none. Throws <see cref="InvalidDataException"/> for a file that is not one.</summary>
    public static StoredPassword? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var parts = File.ReadAllText(path).Trim().Split(' ');
        try
        {
            if (parts.Length == 4 && parts[0] == Kind && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rounds) && rounds >= 10_000
                && Convert.FromBase64String(parts[2]) is { Length: >= 16 } salt && Convert.FromBase64String(parts[3]) is { Length: 32 } hash)
                return new(rounds, salt, hash);
        }
        catch (FormatException) { }
        throw new InvalidDataException("The page's password file is not one.");
    }
}

// The server's first run on its page (SWITCHBOARD.md 10.4, D4) and the walkthrough after it. With no password set where the server was installed, the server prints a
// one-time setup code to its container's output (PairingCodes, as a pairing code is) and the page shows only the setup screen, to the home network and the tailnet
// alone (NetworkHost): the code, then the password, which is kept as a hash with the keys. The code is taken by the first right try, as a pairing code is, and its tries
// are paced the same way; the password must then be chosen in the same browser within half an hour (a cookie only that browser holds, tied to this screen's form).
// Then one screen each, every one skippable: Tailscale, your accounts, notifications, a phone, a PC, backups. A phone comes after notifications because it needs them (owner, 2026-11-02). A checklist on the front page keeps what is left, each
// line reopening its screen, until everything is done or the owner hides it.
internal sealed partial class ServerPage
{
    private volatile StoredPassword? stored;
    private volatile bool settingUp;
    private readonly ServerPageSetup? setup;
    private readonly TailscaleAdmin? tailscale;
    private PairingCodes? setupCodes;
    private readonly object setupGate = new();
    private (byte[] Hash, DateTimeOffset Until)? setupSession;
    // The phone page's New code, given after the password: its hash, until when, and the session that typed the password, the only one it is good for.
    private (byte[] Hash, DateTimeOffset Until, string Session)? phoneGrant;

    /// <summary>The cookie that holds the half-finished setup between the code and the password: only the browser that gave the code may choose the password.</summary>
    public const string SetupCookie = "messages-server-setup";

    /// <summary>How long a right setup code holds for choosing the password, and a phone's code page for making new codes without the password again.</summary>
    public static readonly TimeSpan SetupWindow = TimeSpan.FromMinutes(30);

    /// <summary>The front page's checklist, hidden for good once its owner chose so, kept with the keys.</summary>
    public const string ChecklistFile = "server-page.checklist";

    /// <summary>"I don't use a phone", said on the notifications step, kept with the keys: the phone's steps leave the walkthrough and the checklist until notifications are on.</summary>
    public const string NoPhoneFile = "server-page.no-phone";

    // How far setting up has come, read from the server's parts as they are now: accounts only where the caller read them (they take a call to the host).
    private SetupProgress Progress(int? accounts = null)
    {
        var view = tailnet?.Latest;
        var registry = pushes?.Registry;
        return new(new SetupFacts
        {
            TailnetEnabled = tailnet is { Enabled: true },
            TailnetSignedIn = view is { Running: true },
            TailnetReachable = tailnet is { Enabled: true, Latest: { Running: true, Name.Length: > 0 } },
            TailnetAllDone = view is { AllDone: true },
            TailnetPhones = view?.PhoneCount ?? 0,
            DesktopTrust = DesktopTrust,
            Accounts = accounts,
            PushesOffered = pushes is not null,
            PushesOn = pushes is { Switch.Current: not null },
            NoPhoneSaid = File.Exists(Path.Combine(keysFolder, NoPhoneFile)),
            Devices = devices.Count,
            PhonesSignedUp = registry?.All().Count(registration => registry.Paired(registration.DeviceId)) ?? 0,
            PhonesNotified = pushes is null ? 0
                : (pushes.Health.Status(true, pushes.Registry, devices.List(null)).Phones ?? []).Count(phone => phone.State is PushPhoneStates.On or PushPhoneStates.Failing),
            BackupsOffered = backups is not null,
            // From what the service holds, not its Status(), which lists the backup folder on disk: this runs for every walkthrough screen and pairing page.
            BackupsFolder = backups?.Service.Folder is not null,
            BackupGood = backups?.Service is { Folder: not null, Settings.LastSuccess: not null },
        });
    }

    /// <summary>Whether the page still waits for its first password (the harness's view).</summary>
    internal bool SettingUp => settingUp;

    /// <summary>The setup code's issuer while the page waits for its password (the harness reads the printed line instead; this is for its pacing checks).</summary>
    internal PairingCodes? SetupCodes => setupCodes;

    private static byte[] StoredCredential(byte[] secret, StoredPassword stored) => Mac(secret, "stored|" + stored.Line);

    private static string StoredCheck(byte[] secret, StoredPassword stored) => Convert.ToHexStringLower(Mac(secret, "check|stored|" + stored.Line))[..16];

    // The first run's screens (answered by FirstRunAsync while the page waits for its password, turned away once it has one), and the walkthrough's.
    private static IEnumerable<Route> SetupRoutes =>
    [
        new("/setup", Verbs.Both, (page, call) => Gone(call)) { Stage = Stage.Open },
        new("/setup/password", Verbs.Both, (page, call) => Gone(call)) { Stage = Stage.Open },
        .. new[] { "/welcome", "/welcome/no-phone", "/phone", "/checklist/hide", "/checklist/show" }.Select(path =>
            new Route(path, Verbs.Both, (page, call) => page.WalkthroughAsync(call.Context, call.Path, call.Form, call.Token))),
    ];

    private static Task Gone(Call call)
    {
        Redirect(call.Response, "/");
        return Task.CompletedTask;
    }

    // The walkthrough's screen last opened, so the front page's Continue setup goes back to it (step 1 included). In memory: after a restart it goes to the first left.
    private volatile string? lastStep;

    // Opens the setup code at the start, when the page waits for its password.
    private void OpenSetup()
    {
        if (!settingUp) return;
        setupCodes = new PairingCodes(time, log, setup?.Show, setup: true);
        setupCodes.Issue();
    }

    // ---- The first run: the setup code, then the password ----

    // Handles every request while the page waits for its password.
    private async Task FirstRunAsync(HttpContext context, string? path, IFormCollection? form)
    {
        var post = form is not null;
        switch (path)
        {
            case "/setup" when !post:
                // A code that ran out is replaced at once: the new one is in the server's output.
                var renewed = false;
                if (!setupCodes!.Open) { setupCodes.Issue(); renewed = true; }
                await PageAsync(context, StatusCodes.Status200OK, "Set up", SetupCodeBody(renewed ? "The last setup code ran out, so the server printed a new one just now." : null)).ConfigureAwait(false);
                return;
            case "/setup":
                await SetupCodeAsync(context, form!).ConfigureAwait(false);
                return;
            case "/setup/password" when post:
                await SetupPasswordAsync(context, form!).ConfigureAwait(false);
                return;
            default:
                if (post) await PlainAsync(context, StatusCodes.Status403Forbidden, "Set the server up first.").ConfigureAwait(false);
                else Redirect(context.Response, "/setup");
                return;
        }
    }

    private static string SetupCodeBody(string? problem) =>
        "<section>" + (problem is null ? "" : Notice(problem))
        + Para("Welcome. This is the first visit to your Message Link Server's page. To prove it's you, enter the setup code the server printed when it started, then choose a password for this page.")
        + "<p class=\"quiet\">It's on the line with “setup code” in the server's output: on Unraid, the container's log; with Docker, <span class=\"copy\">docker logs messages-server</span>; "
        + "on Linux or Proxmox, <span class=\"copy\">journalctl -u messages-server</span>.</p>"
        + "<form method=\"post\" action=\"/setup\"><label for=\"code\">Setup code</label><input id=\"code\" name=\"code\" type=\"text\" autocomplete=\"off\" autocapitalize=\"characters\" "
        + "spellcheck=\"false\" placeholder=\"ABCDE-FGHJK\" required autofocus><button type=\"submit\">Continue</button></form></section>";

    private async Task SetupCodeAsync(HttpContext context, IFormCollection form)
    {
        const string title = "Set up";
        var codes = setupCodes!;
        if (codes.Take(form["code"].ToString(), context.Connection.RemoteIpAddress, out var paced) is null)
        {
            if (paced)
            {
                context.Response.Headers.RetryAfter = "60";
                await PageAsync(context, StatusCodes.Status429TooManyRequests, title, SetupCodeBody("Too many tries. Wait a minute, then try again.")).ConfigureAwait(false);
                return;
            }
            // Taken by another browser a moment ago, or run out: a new code, so the owner is never left without one.
            var renewed = !codes.Open;
            if (renewed) codes.Issue();
            await PageAsync(context, StatusCodes.Status401Unauthorized, title, SetupCodeBody(renewed
                ? "That code isn't good any more, so the server printed a new one just now. Enter the new one."
                : "That isn't the setup code. It's 10 letters and digits, like ABCDE-FGHJK, on the newest line with “setup code” in the server's output.")).ConfigureAwait(false);
            return;
        }
        var held = RandomNumberGenerator.GetBytes(32);
        var until = time.GetUtcNow() + SetupWindow;
        lock (setupGate) setupSession = (SHA256.HashData(held), until);
        var cookie = Base64Url(held);
        context.Response.Cookies.Append(SetupCookie, cookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/setup", Expires = until, IsEssential = true });
        await PageAsync(context, StatusCodes.Status200OK, "Choose a password", SetupPasswordBody(SetupFormToken(cookie), null)).ConfigureAwait(false);
    }

    private string SetupFormToken(string cookie) => Base64Url(Mac(secret, "setup-form|" + cookie)[..16]);

    private static string SetupPasswordBody(string token, string? problem) =>
        "<section>" + (problem is null ? "" : Notice(problem))
        + Para($"Choose the password for this page: at least {MinimumPassword} characters, used nowhere else. Keep it somewhere safe: the server keeps only a scrambled form of it, so it can't tell it to you again.")
        + "<form method=\"post\" action=\"/setup/password\"><input type=\"hidden\" name=\"form\" value=\"" + Html(token) + "\">"
        + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"new-password\" minlength=\"" + MinimumPassword + "\" required autofocus>"
        + "<label for=\"again\">The same again</label><input id=\"again\" name=\"again\" type=\"password\" autocomplete=\"new-password\" required>"
        + "<button type=\"submit\">Set the password</button></form>"
        + "<p class=\"quiet\">Forgot it later? Set MESSAGES_ADMIN_PASSWORD where the server was installed and start it again: that password then works instead.</p></section>";

    private async Task SetupPasswordAsync(HttpContext context, IFormCollection form)
    {
        const string title = "Choose a password";
        var cookie = context.Request.Cookies[SetupCookie];
        var now = time.GetUtcNow();
        bool held;
        lock (setupGate)
            held = cookie is { Length: > 0 and <= 64 } && setupSession is { } session && now < session.Until
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(FromBase64UrlOrEmpty(cookie)), session.Hash);
        if (!held || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(form["form"].ToString()), Encoding.ASCII.GetBytes(SetupFormToken(cookie!))))
        {
            if (!setupCodes!.Open) setupCodes.Issue();
            await PageAsync(context, StatusCodes.Status403Forbidden, "Set up",
                SetupCodeBody("That took too long, or came from another browser. The server printed a new setup code: enter it to start again.")).ConfigureAwait(false);
            return;
        }
        var (password, again) = (form["password"].ToString(), form["again"].ToString());
        var problem = password.Length < MinimumPassword ? $"Use at least {MinimumPassword} characters."
            : Encoding.UTF8.GetByteCount(password) > MaximumPasswordBytes ? "That's longer than the page takes. Use a shorter one."
            : password != again ? "The two didn't match. Type the same password twice."
            : null;
        if (problem is not null)
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, title, SetupPasswordBody(SetupFormToken(cookie!), problem)).ConfigureAwait(false);
            return;
        }
        var made = StoredPassword.Make(password);
        lock (signingOut)
        {
            if (!settingUp)
            {
                Redirect(context.Response, "/login");
                return;
            }
            try { WriteNew(Path.Combine(keysFolder, StoredPassword.FileName), Encoding.ASCII.GetBytes(made.Line + "\n"), overwrite: false); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log($"server page password kept=false type={failure.GetType().Name}");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Items["setup-failed"] = true;
            }
            if (!context.Items.ContainsKey("setup-failed"))
            {
                stored = made;
                credential = StoredCredential(secret, made);
                passwordCheck = StoredCheck(secret, made);
                generation++;
                sessionKey = SessionKey(generation);
                // Not kept: the next start counts on from the page having been off, so sign-ins made now end there, and nothing earlier comes back.
                if (!TryWriteState(Path.Combine(keysFolder, SignedOutFile), generation, passwordCheck)) log("server page state kept=false");
                settingUp = false;
            }
        }
        if (context.Items.ContainsKey("setup-failed"))
        {
            await PageAsync(context, StatusCodes.Status500InternalServerError, title, SetupPasswordBody(SetupFormToken(cookie!), "The server couldn't keep the password. Try again.")).ConfigureAwait(false);
            return;
        }
        lock (setupGate) setupSession = null;
        log("server page password set via=setup");
        context.Response.Cookies.Delete(SetupCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/setup" });
        var expires = time.GetUtcNow() + SessionLifetime;
        var signedIn = NewSession(expires);
        passwordAgain.Typed(signedIn, SetupWindow);
        context.Response.Cookies.Append(CookieName, signedIn, Cookie(expires));
        log("server page sign_in outcome=signed_in");
        Redirect(context.Response, "/welcome");
    }

    private static byte[] FromBase64UrlOrEmpty(string text)
    {
        try { return FromBase64Url(text); }
        catch (FormatException) { return []; }
    }

    // ---- The walkthrough, the phone's code and the checklist ----

    private async Task WalkthroughAsync(HttpContext context, string path, IFormCollection? form, string token)
    {
        switch (path)
        {
            case "/phone":
                await PhoneAsync(context, form, token).ConfigureAwait(false);
                return;
            case "/checklist/hide" when form is not null:
                try { WriteNew(Path.Combine(keysFolder, ChecklistFile), "hidden"u8.ToArray(), overwrite: true); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"server page checklist kept=false type={failure.GetType().Name}"); }
                Redirect(context.Response, "/");
                return;
            case "/checklist/show" when form is not null:
                try { File.Delete(Path.Combine(keysFolder, ChecklistFile)); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"server page checklist shown=false type={failure.GetType().Name}"); }
                Redirect(context.Response, "/#checklist");
                return;
            case "/welcome/no-phone" when form is not null:
                try { WriteNew(Path.Combine(keysFolder, NoPhoneFile), "no-phone"u8.ToArray(), overwrite: true); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"server page no_phone kept=false type={failure.GetType().Name}"); }
                Redirect(context.Response, Progress().After("notifications") is { } on ? "/welcome?step=" + on : "/");
                return;
            case "/welcome":
                // A screen opened by its address while it isn't part of the walkthrough (an old link) still shows, unnumbered, and leads on to the next one that is.
                var step = context.Request.Query["step"].ToString();
                if (Array.IndexOf(SetupProgress.Steps, step) < 0) step = SetupProgress.Steps[0];
                lastStep = step;
                var (body, waiting) = await StepBodyAsync(step, token, context.Request, context.RequestAborted).ConfigureAwait(false);
                await PageAsync(context, StatusCodes.Status200OK, StepTitle(step), body, refresh: waiting ? 5 : null, refreshTo: "/welcome?step=" + step).ConfigureAwait(false);
                return;
            default:
                Redirect(context.Response, "/");
                return;
        }
    }

    private static string StepTitle(string step) => step switch
    {
        "start" => "Set up your server",
        "tailscale" => "Away from home",
        "accounts" => "Your accounts",
        "phone" => "Add a phone",
        "notifications" => "Notifications",
        "pc" => "Add a PC",
        _ => "Backups",
    };

    // One screen of the walkthrough, and whether it waits on something that happens elsewhere (Tailscale's sign-in), so it looks again by itself.
    private async Task<(string Body, bool Waiting)> StepBodyAsync(string step, string token, HttpRequest request, CancellationToken cancellationToken)
    {
        // The accounts' screen reads them first, so its Next goes by the accounts it lists.
        IReadOnlyList<ServerAccount>? accounts = null;
        if (step == "accounts" && admin is not null)
            try { accounts = await admin.AccountsAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
        var progress = Progress(accounts?.Count);
        var body = new StringBuilder();
        if (progress.Number(step) is var number and > 0) body.Append("<p class=\"quiet\">Step ").Append(number).Append(" of ").Append(progress.Shown.Length).Append("</p>");
        var done = progress.DoneInWalkthrough(step);
        var waiting = false;
        switch (step)
        {
            case "start":
                // The owner's review of setup (docs/onboarding-review.md, 2026-11-10): what the server is, why Tailscale and Firebase are the person's own, what it takes, who can do what.
                body.Append("<section><h2>Before you start</h2>")
                    .Append(Para("This server connects to your messaging accounts and keeps your history in one place. Message Link on your phone and PCs connects to it, so every device shows the same conversations."))
                    .Append("<p><strong>Why you set up Tailscale and Firebase yourself</strong></p>")
                    .Append(Para("Message Link has no servers of its own, so your conversations and sign-ins never pass through us. Two free services fill that gap. You create your own copy of each, so they answer only to you:"))
                    .Append("<ul><li><strong>Tailscale:</strong> lets your phone and laptop reach this server when you're away from home, without opening it to the internet.</li>")
                    .Append("<li><strong>Firebase:</strong> wakes your phone when a message arrives. Each notification is locked so only your phone can read it.</li></ul>")
                    .Append(Para("Only using it at home on a PC? You can skip both."))
                    .Append(Para("Setup takes some time, most of it on those two services. Each step says what to click, and any step can be skipped and finished later from this page."))
                    .Append(Para("The password for this page manages the server. Phones and PCs you add can read and send messages but can't change the server's settings."));
                if (backups is not null)
                    body.Append("<p><strong>Moving from another server or PC?</strong> Restore your backup before you connect accounts, and stop the old server first.</p>")
                        .Append("<p><a class=\"button quiet\" href=\"/restore\">Restore from a backup</a></p>");
                body.Append("<p><a href=\"").Append(JoinLink.SetupGuide).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Learn more</a></p></section>");
                break;
            case "tailscale":
                if (tailnet is not null)
                {
                    TailnetCard(body, tailnet, token, walkthrough: true);
                    // Looks again while a step is left, so turning key expiry off in Tailscale ticks here by itself.
                    waiting = TailnetWaiting || tailnet.Latest is { AllDone: false };
                }
                else body.Append(Para("This server has no Tailscale door, so phones and laptops reach it at home only."));
                break;
            case "accounts":
                // Each connected account sits under its own service, by name (ConnectList), rather than in a list of its own above.
                body.Append("<section id=\"connect\">").Append(Para("Connect the accounts you use. The server keeps every conversation from them."));
                ConnectList(body, accounts, walkthrough: true, request.HttpContext, token);
                body.Append("</section>");
                break;
            case "phone":
                body.Append("<section>");
                if (progress.DesktopJoinUnavailable) body.Append(DesktopJoinHelp);
                else if (progress.PhoneWaitsForNotifications) body.Append(Para(PhoneNeedsNotifications)).Append("<p><a class=\"button\" href=\"/welcome?step=notifications\">Set up notifications…</a></p>");
                else
                {
                    body.Append(Para("Message Link on your phone joins by scanning a code from this page: no codes to type or match. Install Message Link on the phone first."))
                        .Append(Para("The code holds a one-time pairing code, so the server's password is asked for before it's shown."))
                        .Append(PasswordForm(request.HttpContext, "phone", "/phone", token, "Show the code", before: FromSetupField));
                }
                body.Append("</section>");
                break;
            case "notifications":
                // A phone can't join until notifications are on (PhoneWaitsForNotifications; owner, 2026-11-10), so this says so rather than calling them optional.
                body.Append("<section><h2>Phone notifications</h2>");
                if (pushes is null) body.Append(Para("This server doesn't send notifications."));
                else if (!progress.NotificationsOn)
                    body.Append(Para("Get new messages on your phone even when Message Link is closed. A phone needs this before it can join."))
                        .Append(Para("It uses a free Firebase project of your own, made with your Google account. Each step is shown."))
                        .Append("<p><a class=\"button\" href=\"/push?from=welcome\">Set up notifications…</a></p>");
                else body.Append(Para(progress.NotificationsReachPhone ? "Notifications are on, and a phone gets them." : "Notifications are on. A phone signs up for them by itself when it joins."));
                body.Append("</section>");
                break;
            case "pc":
                if (progress.DesktopJoinUnavailable) { body.Append("<section>").Append(DesktopJoinHelp).Append("</section>"); break; }
                body.Append("<section>").Append(Para("Message Link on a Windows PC joins with one line copied from here and pasted into Settings → Connections → Message Link Server. The line holds a one-time pairing code, so the server's password is asked for first."))
                    .Append(PasswordForm(request.HttpContext, "pair", "/pair", token, "Show the line", before: FromSetupField)).Append("</section>");
                break;
            default:
                if (backups is not null)
                {
                    await BackupsCardAsync(body, token, request, cancellationToken).ConfigureAwait(false);
                    // Read again after the card, which waits on the host: a backup that finished meanwhile shows on the card, so its way on says so too.
                    progress = Progress(accounts?.Count);
                    done = progress.DoneInWalkthrough(step);
                }
                else body.Append("<section>").Append(Para(progress.Left(step)!)).Append("</section>");
                break;
        }
        // Forms elsewhere on the screen end at "Cancel"; the walkthrough's own way on is below them.
        body.Append("<nav class=\"steps\">");
        if (progress.Before(step) is { } back) body.Append("<a href=\"/welcome?step=").Append(back).Append("\">Back</a>");
        var after = progress.After(step);
        // Skipping notifications is saying there is no phone: kept, so the phone's step is passed too, and Back and the checklist pass them both.
        if (step == "notifications" && !done && pushes is not null)
            body.Append("<form class=\"plain\" method=\"post\" action=\"/welcome/no-phone\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token))
                .Append("\"><button type=\"submit\" class=\"quiet\">I don't use a phone</button></form>");
        else
            body.Append("<a class=\"button").Append(done ? "" : " quiet").Append("\" href=\"").Append(after is null ? "/" : "/welcome?step=" + after).Append("\">")
                .Append(after is null ? "Finish" : done ? "Next" : "Skip for now").Append("</a>");
        body.Append("</nav>");
        return (body.ToString(), waiting && (!done || step == "tailscale"));
    }

    // A server that can send notifications but has none set up yet shows no phone's code (SetupProgress.PhoneWaitsForNotifications), and says why.
    private const string PhoneNeedsNotifications = "Set up notifications first. Without them, the phone only gets new messages while the app is open.";

    // The Windows server listens on loopback only. A code containing that address sends another device to itself.
    private bool DesktopJoinUnavailable => Progress().DesktopJoinUnavailable;
    private static string DesktopJoinHelp => Para("Set up Tailscale before adding another device. This PC's server accepts other devices through Tailscale, including at home. Message Link on the other device signs in to Tailscale itself, with the same Tailscale account as this server.")
        + "<p><a class=\"button\" href=\"/welcome?step=tailscale\">Set up Tailscale</a></p>";
    private string PairAddress(HttpRequest request) => DesktopTrust && tailnet?.Latest is { Running: true } view
        ? $"{(view.DnsName ?? view.Name)!.TrimEnd('.')}:{tailnet.Port}" : Address(request);

    // What is left of setting up, on the front page, each line reopening its screen; nothing once everything is done or the owner hid it.
    private void Checklist(StringBuilder body, SetupProgress progress, string token)
    {
        if (File.Exists(Path.Combine(keysFolder, ChecklistFile))) return;
        // Back into the walkthrough where it was left, so a click that wandered off isn't the end of it (owner, 2026-11-10).
        if (progress.Resume(lastStep) is not { } resume) return;
        body.Append("<section class=\"sec checklist\" id=\"checklist\"><div class=\"sh\"><h2>Finish setting up</h2><span class=\"r\"><form class=\"plain\" method=\"post\" action=\"/checklist/hide\"><input type=\"hidden\" name=\"form\" value=\"")
            .Append(Html(token)).Append("\"><button type=\"submit\" class=\"link\">Hide this list</button></form></span></div><div class=\"box\">");
        body.Append(Go("Continue setup", $"Step {progress.Number(resume)} of {progress.Shown.Length}", null, "/welcome?step=" + resume, "Continue setup"));
        foreach (var item in progress.ChecklistLeft)
            body.Append(Go(item.Title, item.Detail, null, "/welcome?step=" + item.Step, item.Title));
        body.Append("</div></section>");
    }
    // A page the walkthrough opens (a phone's code, a PC's line, the Firebase files) says so, in its address or its form, so it leads back into setup rather than to the
    // Overview when it's done (owner, 2026-11-10: turning notifications on left setup).
    private const string FromSetupField = "<input type=\"hidden\" name=\"from\" value=\"welcome\">";

    private static bool FromSetup(HttpRequest request, IFormCollection? form) => (form is not null ? form["from"].ToString() : request.Query["from"].ToString()) == "welcome";

    // A phone's code: the password first (a GET shows the form), then the code as a QR, with a button for a new one that needs no password again for half an hour.
    private async Task PhoneAsync(HttpContext context, IFormCollection? form, string token)
    {
        const string title = "Add a phone";
        var fromSetup = FromSetup(context.Request, form);
        string Ask(string? problem) => AskBody(context, "phone", "/phone", token, "The code lets a phone read every conversation on the server" + AskedAgain(context, "phone"), "Show the code", problem, before: fromSetup ? FromSetupField : "");
        var progress = Progress();
        if (progress.DesktopJoinUnavailable)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, "<section>" + DesktopJoinHelp + "</section>").ConfigureAwait(false);
            return;
        }
        if (progress.PhoneWaitsForNotifications)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, "<section>" + Para(PhoneNeedsNotifications) + "<p><a class=\"button\" href=\"/welcome?step=notifications\">Set up notifications…</a></p></section>").ConfigureAwait(false);
            return;
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
            return;
        }
        var now = time.GetUtcNow();
        var grant = form["grant"].ToString();
        var session = Session(context.Request);
        bool granted;
        // Only for the browser that typed the password: another session holding the grant (a copied page, a shared screen) is asked for it.
        lock (setupGate)
            granted = grant.Length is > 0 and <= 64 && session is not null && phoneGrant is { } held && now < held.Until
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(session), Encoding.ASCII.GetBytes(held.Session))
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(FromBase64UrlOrEmpty(grant)), held.Hash);
        if (!granted)
        {
            if (!await PasswordAgainAsync(context, form, "phone", title, Ask).ConfigureAwait(false)) return;
            var made = RandomNumberGenerator.GetBytes(32);
            lock (setupGate) phoneGrant = session is null ? null : (SHA256.HashData(made), now + SetupWindow, session);
            grant = Base64Url(made);
        }
        var code = pairing.Issue();
        var view = tailnet?.Latest is { Running: true, Tagged: false } running ? running : null;
        // The home network's address only when the browser came that way: through the tailnet the page's address is the server's tailnet name, already in the code.
        var remote = context.Connection.RemoteIpAddress;
        var address = DesktopTrust || remote is not null && TailnetRelay.IsTailnet(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote) ? null : Address(context.Request);
        var link = JoinLink.Build(view?.Name ?? tailnet?.Latest?.Name, address, fingerprint(), code, view?.Owner, tailnet?.Port ?? NetworkEndpoint.DefaultPort);
        var body = new StringBuilder("<section><div class=\"qr-card\"><div class=\"qr\">").Append(QrCode.Encode(link).ToSvg(label: "The join code for Message Link on a phone")).Append("</div><div class=\"grow\">")
            .Append(Para("Open Message Link on your phone, choose I have a server, then Scan your server's code. Check the server shown and choose Join."))
            .Append("<p class=\"quiet\">It works once, for the next ").Append((int)PairingCodes.Lifetime.TotalMinutes).Append(" minutes. The phone pins this server's fingerprint and joins with it: there's nothing to type or match.</p>")
            .Append("<p><a href=\"").Append(Html(link)).Append("\">On this phone? Open the link instead.</a></p>")
            .Append("<form method=\"post\" action=\"/phone\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\"><input type=\"hidden\" name=\"grant\" value=\"")
            .Append(Html(grant)).Append("\">").Append(fromSetup ? FromSetupField : "").Append("<button type=\"submit\" class=\"quiet\">New code</button></form></div></div>");
        if (tailnet is { Enabled: true } && view is null)
            body.Append(Notice("Away from home the phone reaches the server through Tailscale, which isn't finished yet. At home it can join now."))
                .Append("<p><a href=\"/welcome?step=tailscale\">Set up Tailscale</a></p>");
        else if (tailnet is not { Enabled: true })
            body.Append(Notice("This server has no Tailscale, so the phone can only reach it on your home network."));
        else if (view is not null && view.Owner is { Length: > 0 } owner)
            body.Append("<p class=\"quiet\">The phone signs in to Tailscale as ").Append(Html(owner)).Append(", the account this server uses.</p>");
        // Each phone on Tailscale gets the server's key-expiry step too, so "Fix it for me" is offered where it saves one per device (owner, 2026-11-10).
        if (view is not null && tailscale is not null && !tailscale.Has)
            body.Append("<p><a href=\"/tailnet/oauth\">Let the server keep your devices signed in</a></p>");
        body.Append(fromSetup
            ? "</section><nav class=\"steps\"><a href=\"/welcome?step=phone\">Back</a><a class=\"button\" href=\"/welcome?step=pc\">Next</a></nav>"
            : "</section><p><a href=\"/\">Back to Overview</a></p>");
        log("server page phone_code shown");
        await PageAsync(context, StatusCodes.Status200OK, title, body.ToString()).ConfigureAwait(false);
    }

    // The walkthrough's and the codes' look, beside the page's own.
    private const string SetupStyle =
        ".qr-card{display:flex;gap:20px;align-items:flex-start;margin:0 0 12px}.qr{flex:0 0 auto;width:220px;max-width:100%;background:#fff;border-radius:8px;padding:6px;box-sizing:border-box}"
        + ".qr.small{width:150px}.qr svg{display:block;width:100%;height:auto}"
        + "nav.steps{display:flex;justify-content:space-between;align-items:center;gap:16px;margin:4px 0 16px}nav.steps a.button:only-child{margin-left:auto}"
        + ""
        + "details{margin:6px 0 0}summary{cursor:pointer;color:var(--accent)}summary.button{display:inline-block;padding:5px 13px;border-radius:var(--rb,7px);border:1px solid var(--line);list-style:none;margin:0 0 8px;color:var(--fg);font-size:13px}.actions .button{margin:0 8px 6px 0}"
        + ".copy.line{font-size:14px;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:rgba(0,0,0,.2);margin:0 0 12px}"
        + "@media(max-width:520px){.qr-card{flex-direction:column;align-items:stretch}.qr{width:100%;max-width:300px;align-self:center}.qr.small{max-width:200px}}";
}
