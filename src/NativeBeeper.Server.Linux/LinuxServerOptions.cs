namespace NativeBeeper.Server.Linux;

/// <summary>
/// What <c>messages-server</c> was started with. The configuration is the environment, as a container's is; the one argument is the same switch.
/// </summary>
/// <param name="DataRoot">
/// <c>MESSAGES_DATA</c>, or <c>/data</c> (the image's volume). The host refuses to start unless it exists and can be written; it is never created,
/// since a missing folder is most often a volume that was not mounted, and a host that made one would start empty beside the real data.
/// </param>
/// <param name="Restore">
/// <c>--restore</c>, or <c>MESSAGES_RESTORE=1</c>: reconnect the saved Messenger and Instagram sessions at start and let the provider feed go. Without it the host
/// serves its stores and connects nothing, as the Windows server does without <c>--restore</c>.
/// </param>
/// <param name="Port"><c>MESSAGES_PORT</c>, or 8787: the one HTTPS port on the home network.</param>
/// <param name="HostNames">
/// <c>MESSAGES_HOSTNAMES</c>, comma-separated: the names and addresses devices reach the host by, written into the certificate made on the first
/// start. What a device trusts is the pinned fingerprint, not these names.
/// </param>
/// <param name="Pair"><c>--pair</c>, or <c>MESSAGES_PAIR=1</c>: print a pairing code at start even when a device is already paired.</param>
/// <param name="KeysRootGiven">
/// <c>MESSAGES_KEYS</c>, or <c>/keys</c> (the image's second volume, a Docker named volume off every share): the keys and everything that is a secret in
/// itself (<see cref="HostKeys"/>). Never created, for the same reason as the data folder, and never the data folder or inside it.
/// </param>
/// <param name="Google">
/// <c>MESSAGES_GOOGLE=1</c>, and nothing else: run Google Messages here, renewing its sign-in through Chromium (<see cref="LinuxGoogle"/>,
/// SWITCHBOARD.md 4.3). Hidden and off by default until 5.4 has shown Google accepts it; without it Google Messages is refused as before, and with it
/// but without <see cref="Restore"/> too (only the restore starts the renewal).
/// </param>
/// <param name="ChromiumGiven"><c>MESSAGES_CHROMIUM</c>, or <c>/usr/bin/chromium</c> (the Google image's): the browser the renewal starts.</param>
/// <param name="ChromiumNoSandbox">
/// <c>MESSAGES_CHROMIUM_NO_SANDBOX=1</c>: start Chromium without its own sandbox, which a container with every capability dropped cannot give it.
/// Never assumed; the image that needs it says so.
/// </param>
/// <param name="ChromiumHeaded">
/// <c>MESSAGES_CHROMIUM_HEADED=1</c>: run Chromium with a window on a virtual X display (<c>Xvfb</c>, one per browser run) instead of headless, so it
/// says it is an ordinary Chrome and not <c>HeadlessChrome</c> (SWITCHBOARD.md 5.4: Google did not rotate the headless browser's sign-in). Hidden and off;
/// the host refuses Google (<c>no_xvfb</c>) when it is asked for and Xvfb is not there. Nothing about the browser is hidden either way.
/// </param>
/// <param name="RemoteSignInOnHostNetwork">
/// <c>MESSAGES_REMOTE_SIGNIN_HOST_NETWORK=1</c>: offer the Google sign-in in the server's own browser although the server shares the machine's network namespace
/// (<c>--network host</c>), where the browser's DevTools port on 127.0.0.1 is reachable by every process on the machine (<see cref="HostNetwork"/>). Off by default; without it the
/// host says <c>server google remote_signin refused reason=host_network</c> and clients sign in as they did.
/// </param>
/// <param name="NetworkIsolated">
/// <c>MESSAGES_NETWORK_ISOLATED=1</c>, set by <c>install-unraid.sh --google</c>, which publishes the port with <c>-p</c> on a bridge network: the owner's word that this container has a network
/// namespace of its own. It is half the proof the remote sign-in needs (the other half is the interfaces in view, <see cref="HostNetwork"/>); without it the sign-in is refused as
/// <c>network_unproven</c>, because <c>lo</c> and <c>eth0</c> are also what a bare host with one card shows.
/// </param>
/// <param name="GoogleChrome">
/// <c>MESSAGES_GOOGLE_CHROME=1</c> (with <c>MESSAGES_GOOGLE=1</c>; <c>install-unraid.sh --google</c> sets both): sign Google Messages in BY HAND in official Google Chrome kept running on a virtual display of its
/// own, let it renew Google's sign-in itself, and read its cookies from disk (SWITCHBOARD.md 5.4, chosen 2026-10-10). Replaces the Chromium that is started for each renewal.
/// </param>
/// <param name="ChromeGiven"><c>MESSAGES_CHROME</c>, or <c>/usr/bin/google-chrome-stable</c>.</param>
/// <param name="ChromeBuilt"><c>MESSAGES_CHROME_BUILT</c>, written into the image from its build time (<c>yyyyMMddTHHmmssZ</c>): the date the server's Chrome age counts from (<see cref="ChromeBuild"/>).</param>
/// <param name="ChromePolicyGiven">Test-only: the harness points the check at a file of its own. The server reads no environment variable for it, so it is always the image's managed policy file, and Chrome is not started without it (<see cref="ChromePolicy"/>).</param>
/// <param name="Tailnet">
/// <c>MESSAGES_TAILNET=1</c>: put the server on the owner's tailnet through a door of its own (<see cref="TailnetDoor"/>, SWITCHBOARD.md 4.7), a Tailscale node in this
/// container that devices reach on <see cref="Port"/> by its tailnet name. Off by default; the first start prints a Tailscale sign-in link in the container's log.
/// Its auth key for a headless setup (<c>MESSAGES_TAILNET_AUTHKEY</c>) is deliberately not an option: a record's text would print it (<see cref="LinuxServerRun.TailnetAuthKeyVariable"/>).
/// </param>
/// <param name="TailnetNameGiven"><c>MESSAGES_TAILNET_NAME</c>, or <c>messages-server</c>: the door's name on the tailnet (one DNS label).</param>
/// <param name="BackupsGiven">
/// <c>MESSAGES_BACKUPS</c>: the folder the server's daily backup goes to (SWITCHBOARD.md stage 10, D7). Without it, <c>/backups</c> when that folder exists (the image's mapping); otherwise
/// backups are off and the page says how to map a folder. Never created: a folder that is not there is a mapping that is missing.
/// </param>
internal sealed record LinuxServerOptions(string DataRoot, bool Restore, int Port = 8787, IReadOnlyList<string>? HostNamesGiven = null, bool Pair = false,
    string? KeysRootGiven = null, bool Google = false, string? ChromiumGiven = null, bool ChromiumNoSandbox = false, bool ChromiumHeaded = false, bool RemoteSignInOnHostNetwork = false, bool NetworkIsolated = false,
    bool GoogleChrome = false, string? ChromeGiven = null, string? ChromeBuilt = null, string? ChromePolicyGiven = null, bool Tailnet = false, string? TailnetNameGiven = null, string? BackupsGiven = null)
{
    public const string DefaultBackups = "/backups";

    /// <summary>Where backups go: the folder named, or <see cref="DefaultBackups"/> when it exists; null for none (backups off).</summary>
    public string? Backups(Func<string, bool> exists) => BackupsGiven ?? (exists(DefaultBackups) ? DefaultBackups : null);

    public const string DefaultTailnetName = "messagelink-server";

    public string TailnetName => TailnetNameGiven ?? DefaultTailnetName;

    public const string DefaultDataRoot = "/data";
    public const string DefaultKeysRoot = "/keys";
    public const string DefaultChromium = "/usr/bin/chromium";

    public IReadOnlyList<string> HostNames => HostNamesGiven ?? [];

    public string KeysRoot => KeysRootGiven ?? DefaultKeysRoot;

    public string Chromium => ChromiumGiven ?? DefaultChromium;

    public const string DefaultChrome = "/usr/bin/google-chrome-stable";

    /// <summary>Official Google Chrome (the Google image's), run persistently when <see cref="GoogleChrome"/> is on.</summary>
    public string Chrome => ChromeGiven ?? DefaultChrome;

    public string ChromePolicy => ChromePolicyGiven ?? NativeBeeper.Server.Linux.ChromePolicy.DefaultPath;

    /// <summary>The arguments the server knows. <c>--reset-space</c> is a run of its own (SpaceReset) and is never combined with another.</summary>
    public static readonly IReadOnlyList<string> KnownArguments = ["--restore", "--pair", "--reset-space", "--clear-held"];

    /// <summary>
    /// Whether the arguments are ones this server reads, or a typo that would otherwise be ignored and start a full server (<c>--reset-spce</c> would connect the accounts).
    /// Null when they are fine; otherwise a fixed sentence, which never echoes the argument.
    /// </summary>
    public static string? Problem(IReadOnlyList<string> args) =>
        args.Any(arg => !KnownArguments.Contains(arg, StringComparer.Ordinal)) ? "unknown argument; the server reads --restore, --pair, --reset-space and --clear-held only"
        : args.Contains("--reset-space") && args.Count > 1 ? "--reset-space is a run of its own and takes no other argument"
        : args.Contains("--clear-held") && args.Count > 1 ? "--clear-held is a run of its own and takes no other argument"
        : null;

    public static LinuxServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment) =>
        new(environment("MESSAGES_DATA") is { Length: > 0 } given ? given : DefaultDataRoot,
            args.Contains("--restore") || environment("MESSAGES_RESTORE") == "1",
            int.TryParse(environment("MESSAGES_PORT"), out var port) && port is > 0 and < 65536 ? port : 8787,
            (environment("MESSAGES_HOSTNAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } names ? names : null,
            args.Contains("--pair") || environment("MESSAGES_PAIR") == "1",
            environment("MESSAGES_KEYS") is { Length: > 0 } keys ? keys : null,
            environment("MESSAGES_GOOGLE") == "1",
            environment("MESSAGES_CHROMIUM") is { Length: > 0 } chromium ? chromium : null,
            environment("MESSAGES_CHROMIUM_NO_SANDBOX") == "1",
            environment("MESSAGES_CHROMIUM_HEADED") == "1",
            environment("MESSAGES_REMOTE_SIGNIN_HOST_NETWORK") == "1",
            environment("MESSAGES_NETWORK_ISOLATED") == "1",
            environment("MESSAGES_GOOGLE_CHROME") == "1",
            environment("MESSAGES_CHROME") is { Length: > 0 } chrome ? chrome : null,
            environment("MESSAGES_CHROME_BUILT") is { Length: > 0 } built ? built : null,
            Tailnet: environment("MESSAGES_TAILNET") == "1",
            TailnetNameGiven: environment("MESSAGES_TAILNET_NAME") is { Length: > 0 } tailnetName ? tailnetName : null,
            BackupsGiven: environment("MESSAGES_BACKUPS") is { Length: > 0 } backups ? backups : null);
}

/// <summary>Whether the data root can hold the host's state: it exists, and a file can be made and removed in it. A fixed word when not.</summary>
internal static class DataFolder
{
    public static string? Refusal(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return "data_missing";
            var probe = Path.Combine(root, ".write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return File.Exists(probe) ? "data_not_writable" : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "data_not_writable";
        }
    }
}
