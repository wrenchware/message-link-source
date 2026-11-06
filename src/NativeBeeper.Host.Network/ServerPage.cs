using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// The server's own page (SWITCHBOARD.md 4.6), for its owner in a browser on the home network: what the server is and holds, its paired devices, its accounts (connected and removed here), a new pairing code with the address and fingerprint a PC needs, and the server's recent log.
/// <para>
/// It is served on the host's one HTTPS port beside <c>/v1/socket</c>, and only when the owner set a password (<c>MESSAGES_ADMIN_PASSWORD</c>, at least
/// <see cref="MinimumPassword"/> characters); without one every path but the socket stays 404. Signing in sets a cookie good for <see cref="SessionLifetime"/>, made with a key
/// kept with the server's keys (<see cref="KeyFile"/>) and the password, so it survives the nightly update and is void once the password changes or anyone signs out (which
/// ends every sign-in: a cookie is not kept to one port, so another HTTPS service on the same address is sent it too, and only the password itself makes a pairing code or
/// removes a device). Every form carries a token tied to that cookie and must come from this page's own origin; password tries are paced as wrong pairing codes are. It answers
/// only addresses on the home network or a private network (<see cref="FromHome"/>). Each answer closes its connection, so the page never holds one of the door's few places,
/// and nothing from a browser can reach the socket (it refuses any request that names an origin).
/// </para>
/// Logged: <c>server page on|off reason=</c> once, <c>server page sign_in outcome=signed_in</c>, <c>server page sign_out</c>, and <c>server page password outcome=wrong|paced</c>
/// the first time and then one in a hundred; never the password, a cookie, an address or a name.
/// </summary>
internal sealed partial class ServerPage
{
    /// <summary>The shortest password the page accepts; a shorter one leaves the page off, said once in the log.</summary>
    public const int MinimumPassword = 12;

    /// <summary>The longest password the page accepts, in UTF-8 bytes: a form with it and the page's token stays within <see cref="MaxBody"/> however it is encoded.</summary>
    public const int MaximumPasswordBytes = 128;

    /// <summary>The cookie a sign-in sets.</summary>
    public const string CookieName = "messages-server";

    /// <summary>How long a sign-in lasts; signing out, or changing the password, ends every one at once.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    /// <summary>The page's own key, with the server's keys (off the data volume), made on the first start with a password.</summary>
    public const string KeyFile = "server-page.key";

    /// <summary>
    /// How many times every sign-in has been ended (a sign-out, a new password, the page turned off), with which password it was last on, kept with the keys: the count is part
    /// of every sign-in's key, so each of those voids every sign-in made before it, across restarts and whatever the clock says, even when an earlier password comes back.
    /// </summary>
    public const string SignedOutFile = "server-page.signed-out";

    /// <summary>The most a form may send.</summary>
    public const int MaxBody = 1024;

    /// <summary>The most the notifications form may send: its two files and the password.</summary>
    public const int MaxUpload = 2 * PushSwitch.MaxFile + 4096;

    /// <summary>The server's pushes as the page shows and sets them up: the switch they go through, which devices have signed up for them, and how they are doing.</summary>
    internal sealed record Pushes(PushSwitch Switch, PushRegistry Registry, PushHealth Health, PhoneContactsQueue? Contacts = null);

    /// <summary>The most of the server's log the page shows: its end.</summary>
    public const int LogTail = 64 * 1024;

    // Sign-ins that keep failing wait as pairing codes do: free for a few, then doubling to PairingCodes.LongestWait; every source waits a moment after any wrong one.
    private const int MaxSources = 1024;

    // What sign-ins are made with: a MAC of the password given in the environment, or of the stored hash (ServerPage.Setup.cs). Changed once, when first run sets it.
    private volatile byte[] credential;
    private volatile byte[] sessionKey;
    private readonly byte[] secret;
    private readonly DeviceRegistry devices;
    private readonly PairingCodes pairing;
    private readonly JoinRequests joins;
    private readonly Func<string> fingerprint;
    private readonly IServerAdmin? admin;
    private readonly IHostLogs? logs;
    private readonly TailnetBoard? tailnet;
    private readonly Pushes? pushes;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly Dictionary<string, (int Wrong, DateTimeOffset Next)> sources = new(StringComparer.Ordinal);
    private DateTimeOffset nextAnywhere;
    private readonly string keysFolder;
    private long generation;
    private readonly object signingOut = new();
    private volatile string passwordCheck;
    private int wrongTries, pacedTries;

    private readonly IWholeHistory? history;
    private readonly ServerPageBackups? backups;

    // credential is the environment's password (MAC), or null with the stored one (stored) or with none yet (first run, setting up).
    private ServerPage(byte[]? credential, StoredPassword? stored, byte[] secret, string keysFolder, long generation, string passwordCheck, DeviceRegistry devices, PairingCodes pairing,
        JoinRequests joins, Func<string> fingerprint, IServerAdmin? admin, IHostLogs? logs, TimeProvider time, Action<string> log, TailnetBoard? tailnet, Pushes? pushes,
        IWholeHistory? history, ServerPageBackups? backups, ServerPageSetup? setup)
    {
        this.history = history;
        this.backups = backups;
        this.keysFolder = keysFolder; this.generation = generation; this.passwordCheck = passwordCheck; this.joins = joins;
        this.secret = secret; this.devices = devices; this.pairing = pairing; this.fingerprint = fingerprint; this.admin = admin; this.logs = logs; this.time = time; this.log = log;
        this.tailnet = tailnet; this.pushes = pushes?.Switch.Folder is null ? null : pushes;
        this.stored = stored;
        this.setup = setup;
        tailscale = setup?.Tailscale;
        this.credential = credential ?? (stored is not null ? StoredCredential(secret, stored) : RandomNumberGenerator.GetBytes(32));
        settingUp = credential is null && stored is null;
        sessionKey = SessionKey(generation);
        LoadTheme();
    }

    /// <summary>
    /// The page, when <paramref name="password"/> allows one; null otherwise, said once as <c>server page off reason=</c>: <c>no_password</c>, <c>short_password</c> (under
    /// <see cref="MinimumPassword"/> characters), <c>long_password</c> (over <see cref="MaximumPasswordBytes"/>), <c>changed_by_unraid</c> (see <see cref="UnraidWouldChange"/>),
    /// or <c>key</c> (its key cannot be kept). Turned off, or on with another password than last time, it ends every sign-in made before.
    /// A transition that cannot keep that invalidation refuses startup rather than let an earlier password revive old sign-ins on a later restart.
    /// </summary>
    /// <para>
    /// With <paramref name="setup"/> (the server's first run, SWITCHBOARD.md 10.4) and no password in the environment, the page is on all the same: with the password the
    /// owner chose on it, kept as a slow hash with the keys (<see cref="StoredPassword"/>, <c>server page on password=stored</c>), or, before one is chosen, as a setup screen
    /// that takes a one-time setup code printed to the container's output (<c>server page setup</c>). A password in the environment always wins.
    /// </para>
    public static ServerPage? Create(string? password, string keysFolder, DeviceRegistry devices, PairingCodes pairing, JoinRequests joins, Func<string> fingerprint, IServerAdmin? admin,
        IHostLogs? logs, TimeProvider time, Action<string> log, TailnetBoard? tailnet = null, Pushes? pushes = null, IWholeHistory? history = null, ServerPageBackups? backups = null,
        ServerPageSetup? setup = null)
    {
        var stateFile = Path.Combine(keysFolder, SignedOutFile);
        StoredPassword? stored = null;
        if (string.IsNullOrEmpty(password) && setup is not null)
        {
            try { stored = StoredPassword.Read(Path.Combine(keysFolder, StoredPassword.FileName)); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Never taken for "none": anyone who could damage the file would otherwise get the setup screen.
                log($"server page off reason=stored_password type={failure.GetType().Name}");
                return null;
            }
        }
        var refusal = string.IsNullOrEmpty(password) ? setup is null ? "no_password" : null
            : password.Length < MinimumPassword ? $"short_password minimum={MinimumPassword}"
            : Encoding.UTF8.GetByteCount(password) > MaximumPasswordBytes ? $"long_password maximum={MaximumPasswordBytes}"
            : UnraidWouldChange(password) ? "changed_by_unraid" : null;
        if (refusal is not null)
        {
            log($"server page off reason={refusal}");
            // A page that was on, turned off: whatever sign-ins it made end, so turning it on again later (even with the same password) brings none of them back.
            if (File.Exists(Path.Combine(keysFolder, KeyFile)) && ReadState(stateFile) is var (count, last) && last != OffWord)
                KeepStartupState(stateFile, count < 0 ? RandomNumberGenerator.GetInt32(1, int.MaxValue) : count + 1, OffWord, log);
            return null;
        }
        byte[] secret;
        try { secret = LoadOrCreateKey(Path.Combine(keysFolder, KeyFile)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"server page off reason=key type={failure.GetType().Name}");
            return null;
        }
        // The count kept from before the start (none yet is 0). One that cannot be read is a new random count; another password than the one it was last on, or the page
        // having been off, the next count: either way every earlier sign-in is void.
        // Setting up, there is no sign-in to keep: the page counts as off, so whatever sign-ins an earlier password made stay void.
        var fromEnvironment = !string.IsNullOrEmpty(password);
        var check = fromEnvironment ? Convert.ToHexStringLower(Mac(secret, "check|" + password))[..16] : stored is not null ? StoredCheck(secret, stored) : OffWord;
        var (signOuts, lastCheck) = ReadState(stateFile);
        if (signOuts < 0) signOuts = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        else if (lastCheck is not null && lastCheck != check) signOuts++;
        if (lastCheck != check) KeepStartupState(stateFile, signOuts, check, log);
        log(fromEnvironment ? "server page on" : stored is not null ? "server page on password=stored" : "server page setup");
        var page = new ServerPage(fromEnvironment ? Mac(secret, "password|" + password) : null, stored, secret, keysFolder, signOuts, check, devices, pairing, joins, fingerprint,
            admin, logs, time, log, tailnet, pushes, history, backups, setup);
        page.OpenSetup();
        return page;
    }

    /// <summary>
    /// Whether Unraid would change the password between the template and the container: its editor collapses runs of white space, decodes <c>&amp;name;</c> and <c>&amp;#n;</c>
    /// codes and may trim the ends, so such a password stops working after the next update with nothing said. Refused instead, so the log says why the page is off.
    /// </summary>
    public static bool UnraidWouldChange(string password) =>
        password != password.Trim() || password.Any(char.IsControl) || EntityShape().IsMatch(password)
        || password.Zip(password.Skip(1)).Any(pair => char.IsWhiteSpace(pair.First) && char.IsWhiteSpace(pair.Second));

    [System.Text.RegularExpressions.GeneratedRegex("&(#[0-9]+|#[xX][0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);")]
    private static partial System.Text.RegularExpressions.Regex EntityShape();

    // The state file: the count, a space, and a short check of the password it was last on (or "off"). Its first form (the count alone) reads as no check. (-1, null) when
    // it cannot be read, (0, null) when there is none.
    private const string OffWord = "off";

    private static (long Count, string? Check) ReadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, null);
            var parts = File.ReadAllText(path).Trim().Split(' ', 2);
            var count = long.Parse(parts[0], CultureInfo.InvariantCulture);
            return count < 0 ? (-1, null) : (count, parts.Length == 2 ? parts[1] : "");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return (-1, null); }
    }

    private static bool TryWriteState(string path, long count, string check)
    {
        try
        {
            WriteNew(path, Encoding.ASCII.GetBytes(count.ToString(CultureInfo.InvariantCulture) + " " + check), overwrite: true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void KeepStartupState(string path, long count, string check, Action<string> log)
    {
        if (TryWriteState(path, count, check)) return;
        // The prior state is still readable: merely using a fresh in-memory generation would revive its cookies when the old password returns.
        log("server page state kept=false action=start_refused");
        throw new InvalidOperationException("server_page_state_not_saved");
    }

    // 32 random bytes, made once and kept owner-only; a file of another size is not taken for it.
    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var kept = File.ReadAllBytes(path);
            return kept.Length == 32 ? kept : throw new InvalidDataException("The page's key is not 32 bytes.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var made = RandomNumberGenerator.GetBytes(32);
        WriteNew(path, made, overwrite: false);
        return made;
    }

    // A file written whole under a name of its own first (made new, owner-only from its creation, so nothing planted there is followed) and then moved into place.
    private static void WriteNew(string path, byte[] bytes, bool overwrite)
    {
        var temporary = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(bytes);
            // On the disk before the rename, so a power cut leaves the old file or the new one, never an empty one.
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite);
    }

    /// <summary>
    /// Whether a browser at <paramref name="address"/> may have the page: one on the home network or a private network the owner reaches it by (loopback, the private and
    /// link-local IPv4 ranges, IPv6 unique-local and link-local). The host's gateway is excluded because Docker's IPv6 proxy can hide an outside peer behind it.
    /// </summary>
    public static bool FromHome(IPAddress? address)
        => FromHome(address, ProxyGateways.Value);

    // Docker's userland IPv6 proxy presents outside peers as its bridge gateway. That address proves no LAN origin.
    // Tailnet admission is verified by the separate relay; shared carrier space by itself proves no ownership either.
    private static readonly Lazy<IPAddress[]> ProxyGateways = new(() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address).ToArray());

    internal static bool FromHome(IPAddress? address, IReadOnlyList<IPAddress> gateways)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (gateways.Any(g => (g.IsIPv4MappedToIPv6 ? g.MapToIPv4() : g).Equals(address))) return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    /// <summary>Whether a request is one of the page's (any other path stays the door's 404).</summary>
    public static bool Serves(string? path) => path is "/" or "/login" or "/logout" or "/pair" or "/devices/remove" or "/devices/rename" or "/accounts/remove" or "/theme" or "/log" or LogTailPath or "/joins/approve" or "/joins/deny" or "/joins/pause" or "/push"
        or "/push/test" or "/mirror" or LogoPath or PicturesPath || IsManagePath(path) || IsBackupPath(path) || IsSetupPath(path) || IsTailnetPath(path) || IsDesktopPath(path) || IsSignInPath(path);

    /// <summary>The page's logo, shown before signing in too (it is the app's own icon, nothing of the server's).</summary>
    public const string LogoPath = "/logo.png";

    private static readonly Lazy<byte[]> Logo = new(() => Picture("server-page-logo.png"));

    private static byte[] Picture(string name)
    {
        using var stream = typeof(ServerPage).Assembly.GetManifestResourceStream(name);
        if (stream is null) return [];
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Answers one request of the page, and closes its connection.</summary>
    public async Task HandleAsync(HttpContext context, bool draining)
    {
        var request = context.Request;
        var response = context.Response;
        Secure(response);
        context.Items[ThemeItem] = theme;
        if (draining)
        {
            await PlainAsync(context, StatusCodes.Status503ServiceUnavailable, "The server is stopping.").ConfigureAwait(false);
            return;
        }
        var path = request.Path.Value;
        var get = HttpMethods.IsGet(request.Method);
        var post = HttpMethods.IsPost(request.Method);
        if (get && path is LogoPath or PicturesPath)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "image/png";
            response.Headers.CacheControl = "max-age=86400";
            var bytes = path == LogoPath ? Logo.Value : Pictures.Value;
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (!get && !post || get && path is "/logout" or "/joins/deny" or "/joins/pause" or "/mirror" or "/push/test" or "/theme" || post && (path is "/" or "/log" or LogTailPath or LogoPath or PicturesPath || IsManagePath(path)))
        {
            await PlainAsync(context, StatusCodes.Status405MethodNotAllowed, "There's nothing at this address.").ConfigureAwait(false);
            return;
        }
        // A form comes from this page or not at all: its own origin (which a browser always names on a POST), and no larger than a form.
        // Read from the cookie alone, before any body: the upload's larger limit is for a signed-in browser only, so nobody else can hold one of the door's
        // places for minutes dripping 64 KiB (review, 2026-10-17).
        var session = Session(request);
        // A signed-in browser on the Windows server's own PC is not asked for the password again, so its forms leave the field out (ServerPage.Desktop.cs).
        if (session is not null && DesktopTrust && OnThisPc(context)) context.Items[DesktopItem] = true;
        IFormCollection? form = null;
        if (post)
        {
            if (!SameOrigin(request))
            {
                await PlainAsync(context, StatusCodes.Status403Forbidden, "That request came from another website, so the server ignored it.").ConfigureAwait(false);
                return;
            }
            // The notifications form alone carries files.
            // The restore's review of the devices, with a box for each, is larger than a form of one button.
            var most = path == "/push" && session is not null ? MaxUpload : IsBackupPath(path) && session is not null ? MaxBackupForm : MaxBody;
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
        if (await FirstRunAsync(context, path, form).ConfigureAwait(false)) return;

        // A link to a place on the page (`/?section=storage`) goes there once signed in; the sign-in form carries the section along, so it survives it.
        var section = Section((post ? form!["section"] : request.Query["section"]).ToString());
        if (path == "/login")
        {
            if (session is not null) { Redirect(response, SectionPath(section)); return; }
            if (get)
            {
                // Messages opening its own server's page on this PC (ServerPage.Desktop.cs).
                if (TryOnceSignIn(context)) return;
                // A sign-out the server could not keep is said, so the owner knows a restart would undo it.
                var unsaved = request.Query["signed_out"] == "unsaved" ? "Signed out, but the server couldn't save it, so starting the server again would undo it. Change the password to sign every browser out for good." : null;
                await PageAsync(context, StatusCodes.Status200OK, "Sign in", SignInBody(unsaved, section)).ConfigureAwait(false);
                return;
            }
            await SignInAsync(context, form!, section).ConfigureAwait(false);
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
        switch (path)
        {
            case "/logout":
                var kept = SignOutEverywhere();
                response.Cookies.Delete(CookieName, Cookie(DateTimeOffset.UnixEpoch));
                Redirect(response, kept ? "/login" : "/login?signed_out=unsaved");
                return;
            case "/pair":
                // A code lets a device read everything: the password itself is asked for, not only the sign-in a copied cookie would be.
                if (get)
                {
                    await PageAsync(context, StatusCodes.Status200OK, "Add a PC", AskBody("/pair", token, PairAsk, "Show the line", null)).ConfigureAwait(false);
                    return;
                }
                if (!await PasswordAgainAsync(context, form!, "pair", "Add a PC", problem => AskBody("/pair", token, PairAsk, "Show the line", problem)).ConfigureAwait(false)) return;
                var code = pairing.Issue();
                await PageAsync(context, StatusCodes.Status200OK, "Add a PC", PairBody(request, code)).ConfigureAwait(false);
                return;
            case "/devices/remove":
                await RemoveDeviceAsync(context, get ? request.Query["id"].ToString() : form!["id"].ToString(), form, token).ConfigureAwait(false);
                return;
            case "/devices/rename":
                await RenameDeviceAsync(context, get ? request.Query["id"].ToString() : form!["id"].ToString(), form, token).ConfigureAwait(false);
                return;
            case "/accounts/remove":
                await RemoveAccountAsync(context, get ? request.Query["id"].ToString() : form!["id"].ToString(), form, token).ConfigureAwait(false);
                return;
            case "/joins/approve":
                await ApproveJoinAsync(context, get ? request.Query["id"].ToString() : form!["id"].ToString(), form, token).ConfigureAwait(false);
                return;
            case "/joins/deny":
                // Turning a device away gives nothing to anyone, so the sign-in is enough; letting one in asks for the password again.
                Redirect(response, joins.Deny(form!["id"].ToString(), "page").Denied ? "/?done=declined" : "/?done=join_gone");
                return;
            case "/joins/pause":
                try { joins.SetPaused(form!["paused"] == "yes"); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    await PlainAsync(context, StatusCodes.Status500InternalServerError, "Couldn't save whether requests are paused. Try again.").ConfigureAwait(false);
                    return;
                }
                Redirect(response, "/");
                return;
            case "/theme":
                await ThemeAsync(context, form!).ConfigureAwait(false);
                return;
            case { } manage when IsManagePath(manage):
                await ManageAsync(context, manage, token).ConfigureAwait(false);
                return;
            case "/log":
                response.Headers.ContentSecurityPolicy = LogPolicy;
                await PageAsync(context, StatusCodes.Status200OK, "Log", await LogBodyAsync(context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            case LogTailPath:
                await LogTailAsync(context).ConfigureAwait(false);
                return;
            case "/mirror":
                // Reads each service's most recently active conversations again, as the walk does every half hour: nothing is sent or marked read.
                if (history is not null)
                    try { await history.MirrorAsync(new HistoryMirrorParams(Start: true), context.RequestAborted).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is not OperationCanceledException) { log($"server page mirror failed type={failure.GetType().Name}"); }
                Redirect(response, "/manage/sync?done=mirror");
                return;
            case "/push" when pushes is not null:
                await PushAsync(context, form, token, pushes).ConfigureAwait(false);
                return;
            case "/push/test" when pushes is not null:
                await PushTestAsync(context, form!, pushes).ConfigureAwait(false);
                return;
            case "/push" or "/push/test":
                Redirect(response, "/");
                return;
            case { } walkthrough when IsSetupPath(walkthrough):
                await WalkthroughAsync(context, walkthrough, form, token).ConfigureAwait(false);
                return;
            case "/signin/desktop" when post:
                DesktopSignInPost(context, form!);
                return;
            case SignInPath:
                await SignInViewAsync(context, form, token).ConfigureAwait(false);
                return;
            case { } door when IsTailnetPath(door):
                await TailnetAsync(context, door, form, token).ConfigureAwait(false);
                return;
            case "/backups" when backups is not null && post:
                await BackupsAsync(context, form!).ConfigureAwait(false);
                return;
            case "/backups/password" when backups is not null:
                await BackupPasswordAsync(context, form, token).ConfigureAwait(false);
                return;
            case "/restore" when backups is not null:
                await RestoreAsync(context, form, token).ConfigureAwait(false);
                return;
            case "/restore/review" when backups is not null && post:
                await RestoreReviewAsync(context, form!, token).ConfigureAwait(false);
                return;
            case "/reset" when backups is not null:
                await ResetAsync(context, form, token).ConfigureAwait(false);
                return;
            case "/backups" or "/backups/password" or "/restore" or "/restore/review" or "/reset":
                Redirect(response, "/");
                return;
            default:
                var home = await HomeBodyAsync(request, token, request.Query["done"].ToString(), context.RequestAborted).ConfigureAwait(false);
                await PageAsync(context, StatusCodes.Status200OK, SiteName, home.Body, home.Refresh, home.RefreshTo, home.Actions).ConfigureAwait(false);
                return;
        }
    }

    // ---- Signing in, and the password asked again ----

    private enum Try { Right, Wrong, Paced }

    // One password try, to sign in or asked again before a code or a removal (`purpose`, for the log). Paced as wrong pairing codes are: a source that keeps getting it
    // wrong waits longer each time, and every source waits a moment after any try. The moment is claimed under the same lock it is looked at in, so tries sent together are
    // one try and the rest are told to wait. Wrong and paced tries are logged the first time and then one in a hundred, so guessing cannot fill the server's log.
    private Try CheckPassword(HttpContext context, string presented, string purpose) =>
        TrustedHere(context, purpose) ? Try.Right : CheckPassword(PairingCodes.Key(context.Connection.RemoteIpAddress), presented, purpose);

    // The password a device gives to move accounts (ProtocolDispatcher.AccountMove.cs): paced with the page's own tries, per device. Never logged.
    internal string CheckMovePassword(string device, string presented)
    {
        if (settingUp) return AccountMoveAuthorizeWords.NoPassword;
        if (Encoding.UTF8.GetByteCount(presented) > MaximumPasswordBytes) return AccountMoveAuthorizeWords.Wrong;
        return CheckPassword("device:" + device, presented, "move") switch
        {
            Try.Right => AccountMoveAuthorizeWords.Right,
            Try.Paced => AccountMoveAuthorizeWords.Paced,
            _ => AccountMoveAuthorizeWords.Wrong,
        };
    }

    private Try CheckPassword(string source, string presented, string purpose)
    {
        var now = time.GetUtcNow();
        DateTimeOffset claimed;
        lock (gate)
        {
            if (now < nextAnywhere || sources.TryGetValue(source, out var held) && now < held.Next)
            {
                Sampled(ref pacedTries, "paced", purpose);
                return Try.Paced;
            }
            nextAnywhere = claimed = now + PairingCodes.GlobalSpacing;
        }
        // Too soon was not looked at, so a guess made then told nothing; this one is.
        var right = !settingUp && (stored is { } kept ? kept.Verify(presented) : CryptographicOperations.FixedTimeEquals(Mac(secret, "password|" + presented), credential));
        lock (gate)
        {
            if (right)
            {
                sources.Remove(source);
                // The owner's next step (a code just after signing in) does not wait on the moment this try claimed.
                if (nextAnywhere == claimed) nextAnywhere = now;
                return Try.Right;
            }
            // A source quiet for an hour past its wait starts again: an owner who mistyped last week does not wait five minutes now.
            var wrong = (sources.TryGetValue(source, out var held) && now - held.Next < TimeSpan.FromHours(1) ? held.Wrong : 0) + 1;
            if (sources.Count >= MaxSources && !sources.ContainsKey(source)) sources.Remove(sources.MinBy(pair => pair.Value.Next).Key);
            sources[source] = (wrong, now + PairingCodes.Wait(wrong));
            Sampled(ref wrongTries, "wrong", purpose);
            return Try.Wrong;
        }
    }

    // Under the gate.
    private void Sampled(ref int count, string outcome, string purpose)
    {
        var total = ++count;
        if (total == 1 || total % 100 == 0) log($"server page password outcome={outcome} purpose={purpose}{(total == 1 ? "" : $" count={total}")}");
    }

    private async Task SignInAsync(HttpContext context, IFormCollection form, string? section)
    {
        switch (CheckPassword(context, form["password"].ToString(), "sign_in"))
        {
            case Try.Paced:
                context.Response.Headers.RetryAfter = "60";
                await PageAsync(context, StatusCodes.Status429TooManyRequests, "Sign in", SignInBody("Too many tries. Wait a minute, then try again.", section)).ConfigureAwait(false);
                return;
            case Try.Wrong:
                await PageAsync(context, StatusCodes.Status401Unauthorized, "Sign in", SignInBody("That isn't the server password.", section)).ConfigureAwait(false);
                return;
        }
        var expires = time.GetUtcNow() + SessionLifetime;
        context.Response.Cookies.Append(CookieName, NewSession(expires), Cookie(expires));
        log("server page sign_in outcome=signed_in");
        Redirect(context.Response, SectionPath(section));
    }

    // The password asked again (a code, a removal): true to go on; otherwise the page is answered with `body` saying why.
    private async Task<bool> PasswordAgainAsync(HttpContext context, IFormCollection form, string purpose, string title, Func<string, string> body)
    {
        switch (CheckPassword(context, form["password"].ToString(), purpose))
        {
            case Try.Paced:
                context.Response.Headers.RetryAfter = "60";
                await PageAsync(context, StatusCodes.Status429TooManyRequests, title, body("Too many tries. Wait a minute, then try again.")).ConfigureAwait(false);
                return false;
            case Try.Wrong:
                await PageAsync(context, StatusCodes.Status401Unauthorized, title, body("That isn't the server password.")).ConfigureAwait(false);
                return false;
            default:
                return true;
        }
    }

    // Signing out ends every sign-in, in this browser or any other (a copied cookie with them): the next number is part of the key from now on, kept with the keys across
    // restarts. One at a time, so the count, the key and the file always agree. False when it could not be kept: it holds until the server starts again.
    private bool SignOutEverywhere()
    {
        lock (signingOut)
        {
            var next = generation + 1;
            generation = next;
            sessionKey = SessionKey(next);
            try
            {
                WriteNew(Path.Combine(keysFolder, SignedOutFile), Encoding.ASCII.GetBytes(next.ToString(CultureInfo.InvariantCulture) + " " + passwordCheck), overwrite: true);
                log("server page sign_out");
                return true;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log($"server page sign_out kept=false type={failure.GetType().Name}");
                return false;
            }
        }
    }

    private static CookieOptions Cookie(DateTimeOffset expires) =>
        new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = expires, IsEssential = true };

    // A session: when it ends and 16 random bytes, then their MAC under the session key (which the password is part of). Nothing is kept on the server.
    private string NewSession(DateTimeOffset expires)
    {
        var payload = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(payload, expires.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(payload.AsSpan(8));
        return Base64Url([.. payload, .. Mac(sessionKey, payload)]);
    }

    // The session the request's cookie is, when it is one this server made with this password and it has not ended.
    private string? Session(HttpRequest request) => request.Cookies.TryGetValue(CookieName, out var cookie) ? SessionOf(cookie) : null;

    // The same for a cookie's value already read (the sign-in view's socket checks it again while it runs).
    private string? SessionOf(string? cookie)
    {
        if (cookie is not { Length: > 0 and <= 128 }) return null;
        byte[] bytes;
        try { bytes = FromBase64Url(cookie); }
        catch (FormatException) { return null; }
        if (bytes.Length != 24 + 32 || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(24), Mac(sessionKey, bytes[..24]))) return null;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes), 0, 253402300799));
        var now = time.GetUtcNow();
        // Past its end, or ending further off than a sign-in lasts (made under a longer lifetime, or with the clock ahead): void.
        return now < expiresAt && expiresAt <= now + SessionLifetime + TimeSpan.FromMinutes(1) ? cookie : null;
    }

    // The key sign-ins are made and checked with: the page's own key, the password and how many times the owner has signed out.
    private byte[] SessionKey(long signOuts) => Mac(secret, $"session|{Convert.ToHexStringLower(credential)}|{signOuts}");

    private string FormToken(string session) => Base64Url(Mac(sessionKey, "form|" + session)[..16]);

    private static bool SameOrigin(HttpRequest request) =>
        request.Headers.Origin is { Count: 1 } origin && string.Equals(origin[0], "https://" + request.Host.Value, StringComparison.OrdinalIgnoreCase)
        && (!request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site.ToString() == "same-origin");

    // ---- What the page does ----

    // Asked about first (a GET), then done only with the password itself: a removed device stops connecting.
    private async Task RemoveDeviceAsync(HttpContext context, string id, IFormCollection? form, string token)
    {
        var device = devices.List(null).FirstOrDefault(found => found.Id == id);
        if (device is null)
        {
            Redirect(context.Response, "/?done=gone");
            return;
        }
        string Ask(string? problem) => AskBody("/devices/remove", token,
            $"Remove {device.Name}? It stops connecting at once, and nothing on it is deleted. To use the server again it has to be paired again with a new code.", "Remove", problem,
            id: device.Id, danger: true);
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Remove a device", Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "remove", "Remove a device", Ask).ConfigureAwait(false)) return;
        var removed = false;
        try { removed = await devices.RevokeAsync(id).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "Couldn't tell whether the device was removed: the server couldn't save its list. Check the list before trying again.").ConfigureAwait(false);
            return;
        }
        Redirect(context.Response, removed ? "/?done=removed" : "/?done=gone");
    }

    // A new name gives nobody anything, so the sign-in is enough, as turning a device away is.
    private async Task RenameDeviceAsync(HttpContext context, string id, IFormCollection? form, string token)
    {
        var device = devices.List(null).FirstOrDefault(found => found.Id == id);
        if (device is null)
        {
            Redirect(context.Response, "/?done=gone");
            return;
        }
        string Ask(string? problem) => new StringBuilder("<section>").Append(problem is null ? "" : Notice(problem))
            .Append("<form method=\"post\" action=\"/devices/rename\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token))
            .Append("\"><input type=\"hidden\" name=\"id\" value=\"").Append(Html(device.Id))
            .Append("\"><label for=\"name\">Name</label><input id=\"name\" name=\"name\" value=\"").Append(Html(device.Name)).Append("\" maxlength=\"").Append(NetworkPairing.MaxDeviceName)
            .Append("\" autocomplete=\"off\" required autofocus><button type=\"submit\">Rename</button><a class=\"cancel\" href=\"/\">Cancel</a></form></section>").ToString();
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Rename a device", Ask(null)).ConfigureAwait(false);
            return;
        }
        var name = form["name"].ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            await PageAsync(context, StatusCodes.Status400BadRequest, "Rename a device", Ask("A device needs a name.")).ConfigureAwait(false);
            return;
        }
        string? kept;
        try { kept = await devices.RenameAsync(id, name).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "Couldn't save the new name. Try again.").ConfigureAwait(false);
            return;
        }
        Redirect(context.Response, kept is null ? "/?done=gone" : "/?done=renamed");
    }

    // What names an account in a link and a form: its service, and for one of several Voice accounts its one-way key. Only ever compared with the list the server gives.
    private static string AccountId(ServerAccount account) => account.Key is { Length: > 0 } key ? account.Provider + ":" + key : account.Provider;

    // The account as the person knows it, so that one of two Voice accounts is never removed by guess.
    private static string AccountLabel(ServerAccount account) => account.Name is { Length: > 0 } name ? $"{Provider(account.Provider)} ({name})" : Provider(account.Provider);

    // Asked about first (a GET), then done only with the password itself: the server signs out and forgets what it kept, for every device that uses it. Not tied to the browser
    // staying open once it starts, and never tried twice: a second press finds nothing to remove.
    private async Task RemoveAccountAsync(HttpContext context, string id, IFormCollection? form, string token)
    {
        IReadOnlyList<ServerAccount>? accounts = null;
        if (admin is not null)
            try { accounts = await admin.AccountsAsync(context.RequestAborted).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException) { log($"server page accounts failed type={failure.GetType().Name}"); }
        var account = accounts?.FirstOrDefault(found => AccountId(found) == id);
        if (account is null)
        {
            Redirect(context.Response, "/?done=account_gone");
            return;
        }
        if (!account.Removable)
        {
            Redirect(context.Response, account.Provider == ServerAccountProviders.GoogleMessages ? "/?done=account_unpair" : "/?done=account_moving");
            return;
        }
        var service = Provider(account.Provider);
        var said = account.State == ServerAccountStates.MovedAway
            ? $"The server forgets the paused copy of {service} it kept. The account on your PC stays as it is, and your saved messages stay."
            : $"The server signs out of {service} and forgets what it kept of it, so every device that uses the server stops showing it until you connect it here again. "
              + $"Nothing changes at {service} itself or in its own app, and your saved messages stay."
              // The Windows server keeps Messenger's device where it is, so a new sign-in there reuses it (WindowsServerAdmin).
              + (account.Provider == ServerAccountProviders.Messenger && DesktopSignIn is null ? " Encrypted chats would start over here: the server would join as a new device." : "");
        string Ask(string? problem) => AskBody("/accounts/remove", token, $"Remove {AccountLabel(account)} from the server? {said}", "Remove", problem, id: id, danger: true);
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Remove an account", Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "remove_account", "Remove an account", Ask).ConfigureAwait(false)) return;
        try
        {
            var removed = await admin!.RemoveAccountAsync(account.Provider, account.Key, CancellationToken.None).ConfigureAwait(false);
            Redirect(context.Response, removed.Removed ? "/?done=account_removed" : "/?done=account_gone");
        }
        catch (InvalidOperationException refused) when (refused.Message == AccountRemoveWords.Moving) { Redirect(context.Response, "/?done=account_moving"); }
        catch (InvalidOperationException refused) when (refused.Message == AccountRemoveWords.NotOffered) { Redirect(context.Response, "/?done=account_unpair"); }
        catch (InvalidOperationException refused) when (refused.Message == AccountRemoveWords.Unknown) { Redirect(context.Response, "/?done=account_gone"); }
        catch (Exception failure)
        {
            log($"server page account remove failed provider={account.Provider} type={failure.GetType().Name}");
            await PlainAsync(context, StatusCodes.Status500InternalServerError, "Couldn't tell whether the account was removed. Check the list on the page before trying again.").ConfigureAwait(false);
        }
    }

    // Asked about first (a GET, which shows the code to match), then done only with the password itself: an approved device can read every conversation on the server.
    private async Task ApproveJoinAsync(HttpContext context, string id, IFormCollection? form, string token)
    {
        var waiting = joins.Waiting().FirstOrDefault(found => found.Id == id);
        if (waiting is null)
        {
            Redirect(context.Response, "/?done=join_gone");
            return;
        }
        string Ask(string? problem)
        {
            var body = new StringBuilder("<section>");
            if (problem is not null) body.Append(Notice(problem));
            body.Append(Para($"Let {waiting.DeviceName} join? It will be able to read every conversation on the server. Approve it only if {waiting.DeviceName} shows this code:"));
            body.Append("<p class=\"code\">").Append(Html(waiting.Code)).Append("</p>");
            body.Append("<p class=\"quiet\">Asked from ").Append(Html(waiting.Source)).Append(", ").Append(Html(Ago(waiting.Asked))).Append(".</p>");
            body.Append(PasswordForm("/joins/approve", token, "Approve", id: waiting.Id));
            // Turning a device away gives nothing to anyone, so it needs no password.
            body.Append("<form method=\"post\" action=\"/joins/deny\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\"><input type=\"hidden\" name=\"id\" value=\"").Append(Html(waiting.Id)).Append("\"><button type=\"submit\" class=\"quiet\">Deny</button></form>");
            return body.Append("</section>").ToString();
        }
        if (form is null)
        {
            await PageAsync(context, StatusCodes.Status200OK, "Approve a device", Ask(null)).ConfigureAwait(false);
            return;
        }
        if (!await PasswordAgainAsync(context, form, "join", "Approve a device", Ask).ConfigureAwait(false)) return;
        var approved = await joins.ApproveAsync(id, "page").ConfigureAwait(false);
        // The finish code is shown on this answer itself, never put in an address (where history and logs keep it), and only here: the device's person types it.
        if (approved.FinishCode is { } finish)
        {
            var told = new StringBuilder("<section>");
            told.Append(Para($"Approved. On {waiting.DeviceName}, type this finish code within 5 minutes. It joins once the code is typed."));
            told.Append("<p class=\"code\">").Append(Html(finish)).Append("</p>");
            told.Append("<p><a href=\"/\">Back to the server</a></p>");
            await PageAsync(context, StatusCodes.Status200OK, "Approved", told.Append("</section>").ToString()).ConfigureAwait(false);
            return;
        }
        Redirect(context.Response, approved.Approved ? "/?done=joined" : "/?done=join_gone");
    }

    // One line of the card: done or to do, its words, and what to do it with.
    // [word] names a step that is neither done nor to do (Tailnet Lock not used).
    private static void Step(StringBuilder body, bool done, string title, string text, string? then = null, bool optional = false, string? word = null) =>
        body.Append("<li><span class=\"grow\"><strong>").Append(Html(title)).Append("</strong><span class=\"quiet\">").Append(Html(text)).Append("</span>")
            .Append(then ?? "").Append("</span><span class=\"state\"><span class=\"dot ").Append(done ? "ok" : "wait").Append("\"></span>")
            .Append(Html(word ?? (optional ? "Optional step" : done ? "Done" : "To do"))).Append("</span></li>");

    private static string Link(string href, string text) =>
        "<a class=\"button small\" href=\"" + Html(href) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + Html(text) + "</a>";


    private string PairBody(HttpRequest request, string code)
    {
        var body = new StringBuilder("<section>");
        // Add a PC (SWITCHBOARD.md 10.4): the three in one line, which the PC's server dialog takes pasted whole.
        body.Append(Para($"In Messages on the PC, open Settings → Connections → Messages Server, and paste this line where it asks for the server's address. The code in it works once, for the next {(int)PairingCodes.Lifetime.TotalMinutes} minutes; making another replaces it."));
        body.Append("<p class=\"copy block line\">").Append(Html(JoinLink.Line(Address(request), fingerprint(), code))).Append("</p>");
        body.Append(Para("Or choose to use a pairing code there, and enter these three:"));
        body.Append("<dl>");
        Row(body, "Pairing code", code, copy: true, large: true);
        Row(body, "Address", Address(request), copy: true);
        Row(body, "Fingerprint", fingerprint(), copy: true);
        body.Append("</dl>");
        body.Append(Para("Check that the fingerprint Messages shows is this one before you accept it."));
        body.Append("</section><p><a href=\"/\">Back</a></p>");
        return body.ToString();
    }

    // How long ago, in the page's plain words.
    private string Ago(DateTimeOffset at)
    {
        var span = time.GetUtcNow() - at;
        return span < TimeSpan.FromMinutes(1) ? "just now"
            : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} min ago"
            : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours} h ago"
            : When(at);
    }

    // The dot beside an account's state.
    private static string Tone(string state) => state switch
    {
        ServerAccountStates.Connected or ServerAccountStates.Paired => "ok",
        ServerAccountStates.Saved or ServerAccountStates.Moving => "wait",
        _ => "off",
    };

    private const string PairAsk = "A pairing code lets a device read every conversation on the server, so the server's password is asked for again.";

    private const string UnpairGoogle = "To remove Google Messages, unpair the server on your phone: Google Messages → Device pairing.";

    // A form that asks for the password before it does `button`: what it does, why it fails (when it did), and the device it is about.
    private static string AskBody(string action, string token, string intro, string button, string? problem, string? id = null, bool danger = false)
    {
        var body = new StringBuilder("<section>");
        if (problem is not null) body.Append(Notice(problem));
        body.Append(Para(intro));
        body.Append(PasswordForm(action, token, button, id, danger));
        return body.Append("</section>").ToString();
    }

    // The password asked again, and the button that then does it.
    private static string PasswordForm(string action, string token, string button, string? id = null, bool danger = false)
    {
        var form = new StringBuilder("<form method=\"post\" action=\"").Append(action).Append("\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">");
        if (id is not null) form.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(Html(id)).Append("\">");
        return form.Append("<label for=\"password\">Server password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required autofocus>")
            .Append("<button type=\"submit\"").Append(danger ? " class=\"danger\"" : "").Append('>').Append(Html(button)).Append("</button><a class=\"cancel\" href=\"/\">Cancel</a></form>").ToString();
    }

    // The server's check of its copy against each service (SWITCHBOARD.md stage 9): what the newest one found, and a button to check now.
    private async Task CopiesCardAsync(StringBuilder body, string token, CancellationToken cancellationToken)
    {
        HistoryMirrorResult? mirror = null;
        try { mirror = await history!.MirrorAsync(new HistoryMirrorParams(), cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException) { }
        body.Append("<section id=\"sync\"><h2>Sync check</h2>");
        if (mirror is null) body.Append(Para("The server couldn't say how its last check went just now."));
        else if (mirror.Reports.Count == 0) body.Append(Para(mirror.Running ? "Checking now." : "Not checked yet. The server checks every half hour."));
        else
        {
            if (mirror.Running) body.Append(Notice("Checking again now."));
            body.Append("<div class=\"box\">");
            foreach (var report in mirror.Reports.OrderBy(report => Array.IndexOf(MirrorOrder, report.Service)))
            {
                var found = report.Differed == 0 ? "all match" : $"{report.Differed} fixed";
                body.Append(Row(Service(report.Service), null, $"{report.Checked} checked · {found}" + (report.Skipped > 0 ? $" · {report.Skipped} skipped" : "") + " · " + Ago(report.At), null));
            }
            body.Append("</div>");
        }
        body.Append("<p class=\"hint\">Every half hour the server compares its most active conversations with each service and fixes any that differ. Nothing is sent or marked read.</p>");
        body.Append(Form("/mirror", token, "Check now", quiet: true)).Append("</section>");
    }

    private static readonly string[] MirrorOrder = ["google-direct", "messenger", "gvoice", "instagram"];

    private static string Service(string service) => service switch
    {
        "google-direct" => "Google Messages",
        "messenger" => "Messenger",
        "instagram" => "Instagram",
        _ => "Google Voice",
    };

    // ---- Words and HTML ----

    private static string SignInBody(string? problem, string? section = null) =>
        "<section>" + (problem is null ? "" : Notice(problem))
        + "<form method=\"post\" action=\"/login\">" + (section is null ? "" : "<input type=\"hidden\" name=\"section\" value=\"" + section + "\">") + "<label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required autofocus>"
        + "<button type=\"submit\">Sign in</button></form>"
        + "<p class=\"quiet\">The password chosen when this server was installed. Changing it signs every browser out.</p></section>";

    private static string Provider(string provider) => provider switch
    {
        ServerAccountProviders.GoogleMessages => "Google Messages",
        ServerAccountProviders.Messenger => "Messenger",
        ServerAccountProviders.Instagram => "Instagram",
        ServerAccountProviders.GoogleVoice => "Google Voice",
        _ => "Another service",
    };

    private static string State(string state) => state switch
    {
        ServerAccountStates.Connected => "Connected",
        ServerAccountStates.Saved => "Signed in, not connected now",
        ServerAccountStates.MovedAway => "Moved back to a PC",
        ServerAccountStates.Moving => "Being moved",
        ServerAccountStates.Paired => "Paired",
        _ => "",
    };

    // The short word beside an account in the walkthrough, where State() is the line under it.
    private static string Word(string state) => state switch
    {
        ServerAccountStates.Connected => "Connected",
        ServerAccountStates.Paired => "Paired",
        ServerAccountStates.Saved => "Not connected",
        ServerAccountStates.MovedAway => "Moved",
        ServerAccountStates.Moving => "Moving",
        _ => "",
    };

    // As Messages asks for it: the name or address and the port.
    private static string Address(HttpRequest request) => request.Host.Value ?? "";

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    // How long the server has run, for the badge beside the title.
    private static string Span(TimeSpan span) =>
        span.TotalDays >= 2 ? $"{(int)span.TotalDays} days" : span.TotalHours >= 2 ? $"{(int)span.TotalHours} hours" : span.TotalMinutes >= 2 ? $"{(int)span.TotalMinutes} minutes"
        : span.TotalHours >= 1 ? "an hour" : "";

    private static string Html(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Para(string text) => "<p>" + Html(text) + "</p>";

    private static string Notice(string text) => "<p class=\"notice\" role=\"status\">" + Html(text) + "</p>";

    private static void Row(StringBuilder body, string name, string value, bool copy = false, bool large = false) =>
        body.Append("<dt>").Append(Html(name)).Append("</dt><dd").Append(copy ? large ? " class=\"copy large\"" : " class=\"copy\"" : "").Append('>').Append(Html(value)).Append("</dd>");

    private static string Form(string action, string token, string button, bool quiet = false) =>
        $"<form method=\"post\" action=\"{action}\"><input type=\"hidden\" name=\"form\" value=\"{Html(token)}\"><button type=\"submit\"{(quiet ? " class=\"quiet\"" : "")}>{Html(button)}</button></form>";

    private const string Style =
        // Every look: the same names, read by everything below. All five are dark; colour is for what needs someone.
        "html{color-scheme:dark;--danger:#ff8a80;--on-danger:#2a0d0a;--card:var(--box);--quiet:var(--muted);--accent:var(--acc);--on-accent:var(--btnfg);--attention:var(--warnbg);--ok:var(--muted);--off:var(--muted);--wait:var(--warn)}"
        + ".t-night{--bg:#0a0d13;--fg:#e8edf5;--muted:#93a0b4;--box:rgba(255,255,255,.035);--line:rgba(255,255,255,.09);--hair:rgba(255,255,255,.06);--warn:#ffb23e;--warnbg:rgba(255,178,62,.08);--warnline:rgba(255,178,62,.32);--btn:#3884ff;--btnfg:#fff;--acc:#6aa8ff;--glow:radial-gradient(900px 260px at 18% -60px,rgba(56,132,255,.22),transparent 70%)}"
        + ".t-graphite{--bg:#141516;--fg:#ededed;--muted:#8d8f94;--box:#1b1c1e;--line:#2b2c2f;--hair:#26272a;--warn:#f5a524;--warnbg:#1f1b12;--warnline:#4a3a17;--btn:#ededed;--btnfg:#141516;--acc:#ededed;--capt:none;--caps:0;--capsize:13px;--capc:#ededed;--icons:grayscale(1)}"
        + ".t-aurora{--bg:#0a0f1f;--fg:#eaf0fb;--muted:#94a2c0;--box:rgba(255,255,255,.04);--line:rgba(255,255,255,.09);--hair:rgba(255,255,255,.06);--warn:#ffb84d;--warnbg:rgba(255,184,77,.09);--warnline:rgba(255,184,77,.32);--btn:linear-gradient(100deg,#19c8c0,#7c6cff);--btnfg:#fff;--acc:#4fd1c5;--glow:radial-gradient(620px 240px at 18% -40px,rgba(25,211,197,.26),transparent 72%),radial-gradient(760px 280px at 62% -70px,rgba(139,92,246,.3),transparent 72%)}"
        + ".t-glass{--bg:#0d0b1e;--fg:#fff;--muted:rgba(255,255,255,.68);--box:rgba(255,255,255,.075);--line:rgba(255,255,255,.16);--hair:rgba(255,255,255,.09);--warn:#ffd27a;--warnbg:rgba(255,190,90,.14);--warnline:rgba(255,205,120,.42);--btn:rgba(255,255,255,.92);--btnfg:#171330;--acc:#fff;--blur:blur(16px);--r:14px;--rb:999px;--glow:radial-gradient(40vw 40vw at 12% 18%,rgba(43,123,255,.6),transparent 70%),radial-gradient(44vw 44vw at 60% 92%,rgba(236,72,153,.42),transparent 70%),radial-gradient(38vw 38vw at 94% 22%,rgba(20,200,190,.4),transparent 70%)}"
        + ".t-phosphor{--font:'Cascadia Mono',ui-monospace,Consolas,Menlo,monospace;--bg:#060a08;--fg:#d3ecdc;--muted:#6f9580;--box:#0b120e;--line:#1c3327;--hair:#14241b;--warn:#ffb84d;--warnbg:#171208;--warnline:#57401a;--btn:transparent;--btnfg:#5dffa0;--btnline:#5dffa0;--acc:#5dffa0;--r:3px;--rb:3px;--ri:3px;--capc:#5dffa0;--icons:grayscale(1)}"
        // The frame.
        + "*{box-sizing:border-box}html{background:var(--bg)}body{margin:0;min-height:100vh;font:14px/1.45 var(--font,'Segoe UI',system-ui,-apple-system,Roboto,sans-serif);color:var(--fg);background-image:var(--glow,none);background-repeat:no-repeat;background-attachment:fixed}"
        + "main{max-width:720px;margin:0 auto;padding:14px 16px 32px}main:has(.cols),main:has(pre.log){max-width:1040px}pre.log{height:calc(100vh - 124px);min-height:240px;overflow:auto;margin:0}"
        + "header.top{display:flex;align-items:center;flex-wrap:wrap;gap:4px 10px;padding-bottom:12px;margin-bottom:14px;border-bottom:1px solid var(--line)}header.top .ic{display:block}[hidden]{display:none!important}h1{font-size:15px;font-weight:600;margin:0}header.top h1 a{color:var(--muted);font-size:inherit;font-weight:400;text-decoration:none;white-space:normal}header.top h1 a:hover{color:var(--fg)}.sep{color:var(--muted);font-weight:400;margin:0 7px}"
        + "header.top .r{margin-left:auto;display:flex;gap:12px;align-items:center}"
        // The front page's sections and lines.
        + "section.sec{background:none;border:0;padding:0;margin:0 0 16px;backdrop-filter:none;min-width:0}.sh{display:flex;align-items:center;gap:12px;padding:0 2px 6px;min-height:24px}.sh .r{margin-left:auto;display:flex;gap:12px}"
        + "section.sec h2,.sh h2{font-size:var(--capsize,11px);letter-spacing:var(--caps,.08em);text-transform:var(--capt,uppercase);color:var(--capc,var(--muted));font-weight:600;margin:0}section.sec h2.warn{color:var(--warn)}"
        + ".box{background:var(--box);border:1px solid var(--line);border-radius:var(--r,10px);backdrop-filter:var(--blur,none);overflow:hidden}.box.alert{background:var(--warnbg);border-color:var(--warnline)}.box>*+*{border-top:1px solid var(--hair)}"
        + ".row{display:flex;align-items:center;gap:10px;min-height:48px;padding:6px 12px;scroll-margin-top:12px}.btns{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:10px 0 0}.btns form{margin:0}.g{flex:1;min-width:0;overflow-wrap:anywhere}.g small{display:block;color:var(--muted);font-size:12px;line-height:1.25}"
        + ".v{color:var(--muted);font-size:13px;font-variant-numeric:tabular-nums;white-space:nowrap}.v.warn{color:var(--warn)}.act{width:12px;display:flex;justify-content:flex-end;flex:none}.chev{width:7px;height:7px;border:solid var(--muted);border-width:1.5px 1.5px 0 0;transform:rotate(45deg);display:block}"
        + ".sec a:not(.button):not(.go),.row a:not(.button),header.top a,footer a,button.link{color:var(--muted);font:inherit;font-size:13px;text-decoration:underline;text-decoration-color:var(--line);text-underline-offset:3px;white-space:nowrap;background:none;border:0;padding:0;cursor:pointer}.sec a:not(.button):not(.go):hover,.row a:not(.button):hover,header.top a:hover,footer a:hover,button.link:hover{color:var(--fg)}"
        + "a.go{color:var(--fg);text-decoration:none}a.go:hover{background:var(--hair)}a.go[aria-expanded=true] .chev{transform:rotate(135deg)}form.plain{display:inline;margin:0}.more{display:flex;gap:8px;flex-wrap:wrap;align-items:center;padding:0 12px 10px 50px;margin:0}.more.flat{padding-left:12px}.hint{color:var(--muted);font-size:12.5px;margin:6px 2px 0}.more .hint{margin:0}.more .copy{overflow-wrap:anywhere;color:var(--muted)}.more details{width:100%;margin:0}.more details p{margin:6px 0}.more pre{margin:6px 0 8px}"
        + ".ic{width:28px;height:28px;flex:none;display:block;background:url(/page-pictures.png) no-repeat;background-size:28px 140px;filter:var(--icons,none)}.ic.logo{width:24px;height:24px;background-size:24px 120px;filter:none}.ic.gm{background-position:0 -28px}.ic.me{background-position:0 -56px}.ic.gv{background-position:0 -84px}.ic.ig{background-position:0 -112px}a.go:focus{outline:none}a.go:focus-visible{outline:2px solid var(--acc);outline-offset:-2px}"
        + ".cols{display:grid;grid-template-columns:minmax(0,1fr);gap:0 16px;align-items:start}@media(min-width:800px){.cols{grid-template-columns:minmax(0,1fr) minmax(0,1fr)}}"
        + "footer{display:flex;flex-wrap:wrap;align-items:center;gap:6px 16px;padding:2px 2px 0}footer .end{margin-left:auto}"
        // Everything else: the cards of the pages under /manage, the forms and the steps of setting up.
        + "section{background:var(--box);border:1px solid var(--line);border-radius:var(--r,10px);padding:14px;margin:0 0 14px;backdrop-filter:var(--blur,none)}section.attention{background:var(--warnbg);border-color:var(--warnline)}section>:last-child{margin-bottom:0}"
        + "h2{font-size:14px;font-weight:600;margin:0 0 10px}.manage>section>h2:first-child{display:none}.manage>section:has(>.box){background:none;border:0;padding:0;backdrop-filter:none}section>.sh{margin-top:14px}section.sec>.sh,section>.notice+.sh,section>h2+.sh{margin-top:0}.row>.v.copy{font:inherit;font-size:13px;white-space:normal;overflow-wrap:anywhere;text-align:right;min-width:0;user-select:all}.row form{margin:0}.row details summary{font-size:13px}.row a.button.small{margin:0}section .sh h2,.sh h2{font-size:var(--capsize,11px);letter-spacing:var(--caps,.08em);text-transform:var(--capt,uppercase);color:var(--capc,var(--muted));font-weight:600;margin:0}p{margin:0 0 10px}form{margin:12px 0 10px}section>form:first-child{margin-top:0}"
        + "dl{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:6px 16px;margin:0 0 10px;align-items:baseline}dt{color:var(--muted)}dd{margin:0;overflow-wrap:anywhere}"
        + ".copy{font-family:ui-monospace,Consolas,Menlo,monospace;font-size:12.5px;user-select:all}.large{font-size:26px;letter-spacing:2px}.quiet{color:var(--muted)}"
        + "ul.rows{list-style:none;margin:0 0 10px;padding:0}ul.rows li{display:flex;align-items:center;gap:12px;padding:9px 0;border-top:1px solid var(--hair)}ul.rows li:first-child{border-top:0}"
        + ".grow{flex:1;min-width:0;display:flex;flex-direction:column}.grow strong{font-weight:500}.grow .quiet{font-size:12.5px}ul.rows form{margin:0}.manage>section>form:not(.btns){display:inline-block;margin:6px 8px 6px 0}"
        + ".state{font-size:13px;white-space:nowrap;color:var(--muted)}.dot{display:inline-block;width:7px;height:7px;border-radius:50%;margin-right:6px;background:var(--muted)}.dot.wait{background:var(--warn)}"
        + ".code{font-family:ui-monospace,Consolas,Menlo,monospace;font-size:28px;letter-spacing:3px;margin:8px 0 12px}"
        + "a{color:var(--acc)}a.button,button{display:inline-block;font:inherit;font-size:13px;font-weight:600;padding:5px 13px;border-radius:var(--rb,7px);border:1px solid var(--btnline,transparent);background:var(--btn);color:var(--btnfg);text-decoration:none;white-space:nowrap;cursor:pointer}"
        + "a.button.quiet,button.quiet{background:transparent;color:var(--fg);border-color:var(--line);font-weight:400}button.danger{background:var(--danger);border-color:var(--danger);color:var(--on-danger)}"
        + ".notice{border-left:2px solid var(--acc);padding:6px 12px;background:var(--box);border-radius:0 6px 6px 0;margin:0 0 14px}a.cancel{margin-left:16px}"
        + "label{display:block;margin:0 0 4px}input[type=password],input[type=text],input:not([type]),select{font:inherit;color:var(--fg);background:var(--box);border:1px solid var(--line);border-radius:var(--rb,6px);padding:6px 9px;width:100%;max-width:360px;margin:0 0 12px;display:block}select option{color:#111}"
        + "pre{white-space:pre-wrap;overflow-wrap:anywhere;font:12px/1.5 ui-monospace,Consolas,Menlo,monospace;background:var(--box);border:1px solid var(--line);padding:12px;border-radius:var(--r,10px)}"
        // The tailnet card's steps: a command or a rule to copy, and a link into Tailscale's console, under a step's words.
        + ".copy.block{display:block;margin:6px 0 0;overflow-wrap:anywhere}.grow>a.button,.grow>.copy.block{align-self:flex-start}.grow>pre{margin:8px 0 6px}a.button.small{font-size:12.5px;font-weight:400;padding:3px 10px;margin-top:6px;background:transparent;color:var(--fg);border-color:var(--line)}"
        // The notifications card's steps and form.
        + "ol.steps{margin:0 0 12px;padding-left:22px}ol.steps li{margin:0 0 6px}input[type=file]{font:inherit;display:block;margin:0 0 12px;max-width:100%}"
        + PushStyle
        + "label.grow{cursor:pointer}"
        + "@media(max-width:520px){ul.rows li{flex-wrap:wrap}.more{padding-left:12px}}";

    private const string SiteName = "Messages Server";

    // The page around a body: the logo and the title, with `actions` at its end; `refresh` reloads it every so many seconds (the home page, so a request to
    // join shows up without a click). It reloads the plain home page, so a notice (removed, joined) is said once.
    private static Task PageAsync(HttpContext context, int status, string title, string body, int? refresh = null, string refreshTo = "/", string? actions = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        // Every page is the server's, so its name always comes first, and is the way back to the front page.
        var inner = title != SiteName;
        var whole = inner ? SiteName + " › " + title : SiteName;
        var crumbs = inner ? "<a href=\"/\">" + SiteName + "</a><span class=\"sep\" aria-hidden=\"true\">›</span>" + Html(title) : SiteName;
        var html = $"<!doctype html><html lang=\"en\" class=\"t-{context.Items[ThemeItem] ?? Themes[0].Id}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">"
            + (refresh is { } seconds ? $"<meta http-equiv=\"refresh\" content=\"{seconds};url={Html(refreshTo)}\">" : "")
            + $"<link rel=\"icon\" href=\"{LogoPath}\"><title>{Html(whole)}</title><style>{Style}{SetupStyle}</style></head><body><main>"
            + $"<header class=\"top\"><a href=\"/\" aria-label=\"The server's page\"><span class=\"ic logo\" aria-hidden=\"true\"></span></a><h1>{crumbs}</h1>{(actions is null ? "" : $"<span class=\"r\">{actions}</span>")}</header>{WithoutPasswordAgain(context, body)}</main></body></html>";
        return response.WriteAsync(html, Encoding.UTF8, context.RequestAborted);
    }

    private static Task PlainAsync(HttpContext context, int status, string text) =>
        PageAsync(context, status, status >= 500 ? "Something went wrong" : "That didn't work", Para(text) + "<p><a href=\"/\">Back to the server's page</a></p>");

    // The places on the page a link may name (`/?section=storage`). Only these exact words are taken, so a link can neither redirect elsewhere nor put anything into a page.
    private static readonly string[] Sections = ["accounts", "storage", "backups", ServerAccountProviders.GoogleMessages, ServerAccountProviders.Messenger, ServerAccountProviders.GoogleVoice, ServerAccountProviders.Instagram];

    internal static string? Section(string? name) => Sections.FirstOrDefault(known => known == name);

    private static string SectionPath(string? section) => section switch
    {
        null => "/",
        "storage" => "/manage/storage",
        "backups" => "/manage/backups",
        "accounts" => "/#accounts",
        // A service: its line, open (ServerPage.Home.cs).
        _ => "/?open=" + section + "#row-" + section,
    };

    private static void Redirect(HttpResponse response, string to)
    {
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = to;
        response.ContentLength = 0;
    }

    // What every answer carries: nothing cached or framed, no script but the notifications card's own few lines (allowed by their hash alone, so nothing put into a
    // page could run; the sign-in view and the log page each swap in their own), forms only to this page, and the connection closed after it.
    private static void Secure(HttpResponse response)
    {
        var headers = response.Headers;
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy = $"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src '{PushScriptHash}'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        // same-origin, not no-referrer: under no-referrer a browser sends a form post with Origin: null, which the origin check above refuses (checked in Chromium,
        // 2026-10-13). Other sites still get no referrer.
        headers["Referrer-Policy"] = "same-origin";
        headers.Connection = "close";
    }

    private static byte[] Mac(byte[] key, string text) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));
    private static byte[] Mac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
