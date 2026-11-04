using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Google Messages on the Linux host (SWITCHBOARD.md 4.3), on only with the hidden <c>MESSAGES_GOOGLE=1</c>. Decided once at start, before anything is
/// composed: a host asked for it that has no helper (or no restore) says so in one line and runs without it, refused as before, rather than taking sign-ins (a missing browser no longer refuses it: a QR pairing needs none, SWITCHBOARD.md 5.4;
/// it could not keep for two hours).
/// <para>
/// The profiles go to <c>&lt;keys&gt;/Google</c>: a profile is a working sign-in, unsealed (Chromium's cookie store on Linux without a keyring is
/// protected by its folder alone), so it sits with the other secrets on the keys volume, private to the server's account, never on the data folder.
/// </para>
/// </summary>
internal sealed record LinuxGoogle(ChromiumGoogleBrowser Browser, string ProfilesRoot, IChromiumSignInLauncher? SignIn = null, ChromeGoogle? Chrome = null)
{
    /// <summary>What the keeper renews through: the server's own Chrome when it runs one (SWITCHBOARD.md 5.4, Chrome on the server), else the Chromium that is started for each renewal.</summary>
    public IGoogleBrowser KeeperBrowser => Chrome is not null ? Chrome.Browser : Browser;

    /// <summary>Whether this host signs Google Messages in in a browser of its own: the hand-over from a client's window is then refused.</summary>
    public bool RemoteSignInOffered => Chrome is not null ? Chrome.SignInOffered : SignIn is not null;

    /// <summary>
    /// Whether a sign-in handed up from a client's window is refused (<c>sign_in_use_server</c>). Always in Chrome mode, offered sign-in or not: the hand-over would seed a profile by starting the browser
    /// with DevTools attached, and no Chrome the server runs is ever started that way.
    /// </summary>
    public bool RefusesHandOver => Chrome is not null || RemoteSignInOffered;

    /// <summary>The one guard every Google sign-in on this server (remote, or the legacy hand-over) runs under.</summary>
    public SignInGuard Guard { get; init; } = new();

    /// <summary>The server's own Chrome's sandbox as last read (<see cref="ChromeSandboxReading"/>); null when it runs no Chrome of its own.</summary>
    public ChromeSandboxReading? Sandbox { get; init; }

    /// <summary>The helper this adds to the one the host starts.</summary>
    public const string Helper = "gmessages-helper";
    /// <summary>Google Voice's helper, which the host starts only beside its own Chrome (SWITCHBOARD.md 5.4, Google Voice on the server): Voice's sign-in and renewal need that browser.</summary>
    public const string VoiceHelper = "gvoice-helper";
    public const string ProfilesFolder = "Google";

    /// <summary>Whether Google Voice runs on this host: only with the server's own Chrome, which signs it in and renews it on a profile of each account's own.</summary>
    public bool VoiceRuns => Chrome is not null;

    /// <summary>
    /// The saved Voice sessions' profile ids for the start's sweep (<see cref="SweepProfiles"/>), read with this host's key; null when Voice does not run here, which then keeps none.
    /// </summary>
    public Func<IReadOnlySet<string>?>? VoiceKept(HostKeys keys) =>
        VoiceRuns ? () => GoogleVoiceAccounts.SavedProfileIdsAsync(new FileKeyStateProtector(keys.StateKey)).GetAwaiter().GetResult() : null;

    /// <summary>The one line a host that runs Google says it started with; <paramref name="options"/> decide both words.</summary>
    internal static string EnabledLine(LinuxServerOptions options) =>
        $"server google enabled sandbox={(options.ChromiumNoSandbox ? "off" : "on")} headed={(options.ChromiumHeaded ? "yes" : "no")}";

    /// <param name="findXvfb">Where Xvfb is, or null; the harness gives its own answer. The real one looks in the browser's own folders.</param>
    /// <param name="interfaces">The network interfaces in view, or the real list; the harness gives its own.</param>
    public static LinuxGoogle? Prepare(LinuxServerOptions options, HostKeys keys, Action<string> log, Func<string?>? findXvfb = null, Func<IReadOnlyList<string>>? interfaces = null)
    {
        if (!options.Google) return null;
        // Only the restore starts the keeper, and a sign-in nobody renews dies at two hours; a host that would take one and seed a browser for it
        // anyway (ProfileKeepingGoogleAccount) without ever renewing it is refused instead.
        if (!options.Restore) { log("server google refused reason=no_restore"); return null; }
        // Chrome mode (SWITCHBOARD.md 5.4, MESSAGES_GOOGLE_CHROME=1) has its own refusals (no_browser, no_xvfb, no_policy, no_helper): Chrome is never started without its policy. Refused, it
        // is not what runs, and the host falls through to the lines below as a host without the switch would; the Chromium lines say what is left.
        if (options.GoogleChrome && PrepareChrome(options, keys, log, findXvfb, interfaces) is { } chrome) return chrome;
        // A missing browser, or a headed one with no Xvfb, does not refuse Google; it only leaves the browser-based ways (a Gaia pairing's renewal, the sign-in in the server's
        // own browser) unavailable, said in a word at start. What was asked for is then not what runs, and the line below says what does.
        var headed = options.ChromiumHeaded;
        if (!File.Exists(options.Chromium)) { log("server google browser=none reason=no_browser"); headed = false; }
        else if (headed && (findXvfb ?? (() => ChromiumLauncher.FindXvfb()))() is null) { log("server google browser headed=no reason=no_xvfb"); headed = false; }
        if (!File.Exists(HelperExecutables.PathFor(Helper))) { log("server google refused reason=no_helper"); return null; }
        options = options with { ChromiumHeaded = headed };
        log(EnabledLine(options));
        var launcher = new ChromiumLauncher(options.Chromium, options.ChromiumNoSandbox, options.ChromiumHeaded);
        // The sign-in in the server's own browser needs a window to show, so only a headed host offers it (and Xvfb is there: checked above). And not on the host's own network:
        // its DevTools port is on 127.0.0.1, which there every process on the machine can reach (HostNetwork), unless the owner says it is wanted anyway.
        return new(new ChromiumGoogleBrowser(launcher, log) { Headed = options.ChromiumHeaded }, Path.Combine(keys.Folder, ProfilesFolder), OfferSignIn(options, launcher, interfaces, log));
    }

    /// <summary>The launcher the remote sign-in uses, or null when this host does not offer it (not headed, or on the host's own network without the owner's say-so).</summary>
    internal static IChromiumSignInLauncher? OfferSignIn(LinuxServerOptions options, IChromiumSignInLauncher launcher, Func<IReadOnlyList<string>>? interfaces, Action<string> log) =>
        options.ChromiumHeaded && NetworkAllowsSignIn(options, interfaces, log) ? launcher : null;

    /// <summary>
    /// Whether the sign-in's browser may be shown from this container: the owner's say-so, else positive proof of a network namespace of its own. Chrome has no debugging port, but its display
    /// and its profile are the same secret as before, and the rule is kept: the sign-in is offered only where the container is isolated.
    /// </summary>
    internal static bool NetworkAllowsSignIn(LinuxServerOptions options, Func<IReadOnlyList<string>>? interfaces, Action<string> log)
    {
        // The owner's explicit say-so, whatever the network looks like.
        if (options.RemoteSignInOnHostNetwork) return true;
        var names = (interfaces ?? HostNetwork.Interfaces)();
        if (HostNetwork.Looks(names))
        {
            log("server google remote_signin refused reason=host_network");
            return false;
        }
        // Fails closed: offered only with positive evidence of a namespace of its own, which is the install script's word AND interfaces that could be read and agree. Nothing readable, or
        // nothing said, proves nothing.
        if (!options.NetworkIsolated || names.Count == 0)
        {
            log("server google remote_signin refused reason=network_unproven");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Google Messages through the server's own Chrome (SWITCHBOARD.md 5.4, <c>MESSAGES_GOOGLE_CHROME=1</c>): refused, with a fixed word, without Chrome, Xvfb, its policy file or the helper. The old
    /// renewal browser is built beside it and never started: the host's types ask for one, and the hand-over from a client's window is refused whenever the sign-in is offered.
    /// </summary>
    internal static LinuxGoogle? PrepareChrome(LinuxServerOptions options, HostKeys keys, Action<string> log, Func<string?>? findXvfb, Func<IReadOnlyList<string>>? interfaces,
        IChromeLauncher? launcher = null, IXConnector? connector = null, IChromeCookieSource? cookies = null, IFrameEncoder? encoder = null, ChromeBuild? build = null,
        Func<string?>? sandboxProbe = null)
    {
        if (launcher is null && !File.Exists(options.Chrome)) { log("server google refused reason=no_browser"); return null; }
        if ((findXvfb ?? (() => ChromiumLauncher.FindXvfb()))() is null) { log("server google refused reason=no_xvfb"); return null; }
        if (!ChromePolicy.Present(options.ChromePolicy)) { log("server google refused reason=no_policy"); return null; }
        // Chrome with its own sandbox on (the default; MESSAGES_CHROMIUM_NO_SANDBOX=1 is the owner's word that it cannot be) is started only where a user namespace can be made, which is what
        // that sandbox is built from. Otherwise it would be started to fail, or tempt someone to switch the sandbox off to make it start. The harness's fake launcher skips the real probe.
        var real = launcher is null;
        if (!options.ChromiumNoSandbox && (real || sandboxProbe is not null))
        {
            var problem = (sandboxProbe ?? (() => ChromeSandbox.Preflight(ChromeSandbox.ReadFirstLine, ChromeSandbox.UnshareWorks)))();
            if (problem is not null) { log($"server google refused reason=sandbox_unavailable detail={problem}"); return null; }
        }
        if (!File.Exists(HelperExecutables.PathFor(Helper))) { log("server google refused reason=no_helper"); return null; }
        build ??= ChromeBuild.ReadAsync(options.Chrome, options.ChromeBuilt, ChromeBuild.ProgramFile, CancellationToken.None).GetAwaiter().GetResult();
        log($"server google enabled browser=chrome sandbox={(options.ChromiumNoSandbox ? "off" : "on")}");
        log(build.Line(DateTimeOffset.UtcNow));
        // What host.status says of the sandbox: off when the owner started Chrome without it, else what the inspection below logs.
        var sandbox = new ChromeSandboxReading(options.ChromiumNoSandbox ? NativeBeeper.Protocol.ChromeSandboxWords.Off : NativeBeeper.Protocol.ChromeSandboxWords.Unknown);
        var persistent = new PersistentChrome(launcher ?? new ChromeLauncher(options.Chrome, options.ChromiumNoSandbox), connector ?? new XcbConnector(), log,
            real && !options.ChromiumNoSandbox ? (running, stop) => ChromeSandbox.InspectAsync(running, sandbox.Tee(log), stop) : null);
        // A pairing that replaces the kept profile's sign-in (QR) forgets the profile, and a sign-in moves the pointer: Chrome is closed before either, never left running on a folder that goes.
        GoogleMessagesBrowserProfile.BeforeChange = () => persistent.StopAsync();
        var store = cookies ?? new ChromeCookieStore();
        var chrome = new ChromeGoogle(persistent, new ChromeKeptBrowser(persistent, store, log), store, encoder ?? new SkiaFrameEncoder(), build, NetworkAllowsSignIn(options, interfaces, log));
        // Never started: it names no program that exists, so even a path that reached it could not launch Chrome, with or without DevTools.
        var unused = new ChromiumGoogleBrowser(new ChromiumLauncher("/nonexistent/unused-browser", options.ChromiumNoSandbox, headed: true), log);
        return new(unused, Path.Combine(keys.Folder, ProfilesFolder), null, chrome) { Sandbox = sandbox };
    }

    /// <summary>Where every Google profile goes from now on; once, before anything is composed (it is process-wide).</summary>
    public void UseProfilesFolder() => GoogleSignInProfile.UseRoot(ProfilesRoot);

    /// <summary>Where Messenger's and Instagram's throwaway sign-in profiles go (<see cref="ThrowawayChromeProfile"/>): beside the Google profiles on the keys volume.</summary>
    public string SignInProfilesRoot => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ProfilesRoot))!, ThrowawayChromeProfile.FolderName);

    /// <summary>
    /// At start, before anything can open a profile: every profile on the keys volume that nothing names any more is deleted. Each is a full Google
    /// sign-in, and they are left behind by a seeding the server stopped in the middle of, by a data folder that was cleared (its pointer with it), or
    /// by a sign-in replaced while a delete could not finish. Google Messages keeps only the profile its pointer names, and only while a pairing is
    /// saved. Google Voice keeps the profiles its saved accounts name (<paramref name="voiceKept"/>, given when Voice runs here beside the server's own Chrome) and loses only the
    /// others; a list that cannot be read deletes nothing. Where Voice does not run (none given), none of its profiles is kept. Counts only.
    /// <para>
    /// Only what is positively known to be unreferenced is deleted. A pointer or a pairing that exists but cannot be read (a permission, a disk
    /// error, a pointer that does not hold an id) is not "nothing kept": deleting on it would destroy the one profile that can renew a pairing that
    /// still exists. The sweep of that service is then skipped whole, and said once (<c>browser sweep skipped reason=pointer_unreadable</c>).
    /// </para>
    /// </summary>
    public static void SweepProfiles(Action<string> log, Func<IReadOnlySet<string>?>? voiceKept = null)
    {
        SweepMessages(GoogleSessionProfile.SessionPath, GoogleMessagesBrowserProfile.PointerPath, log);
        if (voiceKept is null) { Sweep(GoogleSignInProfile.VoiceService, (string?)null, log); return; }
        SweepVoice(voiceKept(), log);
    }

    /// <summary>Google Voice's half of <see cref="SweepProfiles"/>, given the profile ids its saved sessions name (null: they could not be read, so nothing is deleted).</summary>
    internal static void SweepVoice(IReadOnlySet<string>? named, Action<string> log)
    {
        if (named is null) { log($"browser sweep skipped reason=sessions_unreadable service={GoogleSignInProfile.VoiceService}"); return; }
        Sweep(GoogleSignInProfile.VoiceService, named, log);
    }

    /// <summary>Google Messages' half of <see cref="SweepProfiles"/>, given where the pairing and the pointer are.</summary>
    internal static void SweepMessages(string sessionPath, string pointerPath, Action<string> log)
    {
        var service = GoogleSignInProfile.MessagesService;
        if (Presence(sessionPath) is not { } paired) { log($"browser sweep skipped reason=session_unreadable service={service}"); return; }
        if (!ReadPointer(pointerPath, out var named)) { log($"browser sweep skipped reason=pointer_unreadable service={service}"); return; }
        Sweep(service, paired ? named : null, log);
    }

    // Whether the file is there: true, false, or null when that cannot be told. File.Exists says false for a file it is not allowed to see, which is
    // not the same as absent, so the folder is listed instead, and a folder that cannot be listed (or whose own presence cannot be told) is unknown.
    internal static bool? Presence(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (!Directory.Exists(folder))
            {
                var parent = Path.GetDirectoryName(folder);
                if (parent is null) return null;
                var name = Path.GetFileName(folder);
                // The parent lists without it: really absent. The parent lists it, yet it did not show as a folder: something stands in the way.
                return Directory.EnumerateFileSystemEntries(parent).Any(entry => Path.GetFileName(entry) == name) ? null : false;
            }
            var file = Path.GetFileName(path);
            return Directory.EnumerateFileSystemEntries(folder).Any(entry => Path.GetFileName(entry) == file);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    // The pointer's id: true with null when there is no pointer, true with the id when it holds one, false when it exists and cannot be read or does
    // not hold an id (the profile it meant cannot be told from the others). The id is a profile's opaque name, not a secret.
    internal static bool ReadPointer(string path, out string? id)
    {
        id = null;
        switch (Presence(path))
        {
            case null: return false;
            case false: return true;
        }
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (!Guid.TryParseExact(text, "N", out var parsed) || parsed.ToString("N") != text) return false;
            id = text;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal static void Sweep(string service, string? keep, Action<string> log) => Sweep(service, keep is null ? new HashSet<string>() : new HashSet<string>(StringComparer.Ordinal) { keep }, log);

    internal static void Sweep(string service, IReadOnlySet<string> keep, Action<string> log)
    {
        int kept = 0, removed = 0, left = 0, other = 0;
        foreach (var name in GoogleSignInProfile.Names(service))
        {
            if (keep.Contains(name)) { kept++; continue; }
            // Only what is shaped as a profile is ever deleted (TryOpen's rules: an opaque id, a real folder, never a link).
            if (!GoogleSignInProfile.TryOpen(name, out _, service)) { other++; continue; }
            GoogleSignInProfile.DeleteAsync(name, service).GetAwaiter().GetResult();
            if (GoogleSignInProfile.TryOpen(name, out _, service)) left++;
            else removed++;
        }
        if (kept + removed + left + other > 0) log($"server google profiles service={service} kept={kept} removed={removed} left={left} other={other}");
    }
}
