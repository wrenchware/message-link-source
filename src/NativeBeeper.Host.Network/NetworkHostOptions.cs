using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// How a <see cref="NetworkHost"/> listens and where it keeps what it owns. The certificate and its key go in <c>tls/</c> under
/// <see cref="KeysFolder"/> (<see cref="ServerCertificate"/>), and the paired devices in <c>&lt;DataFolder&gt;/devices.json</c>, protected through <paramref name="Protector"/>
/// (<see cref="DeviceRegistry"/>). <paramref name="Log"/> is the host's log; nothing secret is written to it except the one pairing-code line, which is the
/// point of that line.
/// </summary>
internal sealed record NetworkHostOptions(string DataFolder, IStateProtector Protector, Action<string> Log)
{
    /// <summary>Every interface, by default: the home network reaches the host by its address. A test binds <c>127.0.0.1</c>.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>The one TCP port, HTTPS only; 0 takes any free port (the harness), which <see cref="NetworkHost.Port"/> then names.</summary>
    public int Port { get; init; } = NetworkEndpoint.DefaultPort;

    /// <summary>The names and addresses clients reach the host by, put in a certificate made on the first start (with the machine's name). The pin is what a client trusts, not these.</summary>
    public IReadOnlyList<string> HostNames { get; init; } = [];

    /// <summary>Print a pairing code at start even though a device is paired (the integrator's <c>--pair</c>). With no device paired, one is printed anyway.</summary>
    public bool Pair { get; init; }

    /// <summary>How many connections may be in the door at once: connected but not yet through TLS and an admitted upgrade, as the pipe's door.</summary>
    public int MaxPending { get; init; } = 8;

    /// <summary>
    /// How many of those may come from one address (an IPv6 /64 counts as one), so a single machine cannot fill the door. Two: a device's own connection
    /// and its pairing, or a reconnect overlapping a connection still in its handshake.
    /// </summary>
    public int MaxPendingPerAddress { get; init; } = 2;

    /// <summary>
    /// How many upgraded (admitted) WebSocket connections the host carries at once, all devices and pairings together: Kestrel stops counting a connection
    /// against its connection limit when it upgrades. A few devices at <see cref="MaxConnectionsPerDevice"/> each fit with room to spare; a connection
    /// over it fails its upgrade.
    /// </summary>
    public int MaxUpgradedConnections { get; init; } = 64;

    /// <summary>
    /// How many connections one device may hold open. One is real use; a few cover a reconnect that overlaps a connection the host has not yet noticed
    /// is dead. A connection over it ends the device's oldest open one (answering what that one had accepted first) rather than being refused: a
    /// client that lost its network and came back must not be locked out by its own half-open leftovers, and a device that opens many is still held to
    /// this many.
    /// </summary>
    public int MaxConnectionsPerDevice { get; init; } = 4;

    /// <summary>
    /// Where the host keeps what is a secret in itself or must not roll back with the data: the TLS certificate and its private key (<c>tls/</c>) and the
    /// paired devices' generation (<see cref="DeviceRegistry.GenerationFile"/>). <see cref="DataFolder"/> when not given; the Linux host keeps them with its
    /// keys, off the data volume, so a backup of the data put back cannot quietly bring back a revoked device.
    /// </summary>
    public string? KeysFolder { get; init; }

    /// <summary>Where the line carrying a pairing code is printed, when not <see cref="Log"/> (see <see cref="PairingCodes"/>).</summary>
    public Action<string>? ShowCode { get; init; }

    /// <summary>
    /// The password of the server's own page (<see cref="ServerPage"/>, <c>MESSAGES_ADMIN_PASSWORD</c>); null leaves the page off, and so does one shorter than
    /// <see cref="ServerPage.MinimumPassword"/>. Never logged.
    /// </summary>
    public string? PagePassword { get; init; }

    /// <summary>
    /// The server's first run (SWITCHBOARD.md 10.4): with no <see cref="PagePassword"/>, the page is on all the same, set up by a one-time setup code printed where
    /// <see cref="ShowCode"/> prints, then a password chosen on it and kept as a hash with the keys. Off (the harness's hosts, the sample server) leaves the page off
    /// without a password, as before.
    /// </summary>
    public bool FirstRun { get; init; }

    /// <summary>Whether an address counts as the home network for the page and asking to join; <see cref="ServerPage.FromHome(System.Net.IPAddress?)"/> unless a check says otherwise.</summary>
    internal Func<System.Net.IPAddress?, bool>? HomeCheck { get; init; }

    /// <summary>What "Fix it for me" calls Tailscale's API through: the harness's stand-in for it; null for the network.</summary>
    internal HttpMessageHandler? TailscaleHandler { get; init; }

    /// <summary>
    /// A Unix socket for the server's door on the owner's tailnet (<see cref="TailnetRelay"/>, SWITCHBOARD.md 4.7): a second listener with the same TLS, door and
    /// routes as the port, whose connections each begin with a PROXY line naming the tailnet device. Null, the default, opens none. Its folder is made private and
    /// a socket left there removed (<see cref="TailnetRelay.Prepare"/>).
    /// </summary>
    public string? RelaySocket { get; init; }

    /// <summary>
    /// What the server's tailnet door reports, for the page's "Your tailnet" card (<see cref="TailnetBoard"/>, SWITCHBOARD.md 4.7). Null (the harness's hosts, the
    /// sample server) shows no card; a board not <see cref="TailnetBoard.Enabled"/> shows how to turn the door on.
    /// </summary>
    public TailnetBoard? Tailnet { get; init; }

    /// <summary>The server's pushes to phones (SWITCHBOARD.md 7.5): the owner's Firebase project (FirebaseTransport.TryLoad), or null for none, when nothing is offered.</summary>
    public PushSetup? Push { get; init; }

    /// <summary>
    /// Where the server's page keeps the two Firebase files the owner gives it, to turn pushes on without a restart (<see cref="PushSwitch"/>); null shows no
    /// notifications card. The Linux server's is in the keys volume, since one of the files is the project's private key.
    /// </summary>
    public string? PushFolder { get; init; }

    /// <summary>What the transports the page makes send through: the harness's stand-in for Google; null for the network.</summary>
    internal HttpMessageHandler? PushHandler { get; init; }

    /// <summary>The server's backup, restore and reset as its page offers them (SWITCHBOARD.md stage 10); null shows no Backups card. Its stale notice goes to the phones' pushes.</summary>
    internal ServerPageBackups? Backups { get; init; }

    /// <summary>The record of the restore this server was made from, when it was (<see cref="NativeBeeper.Host.Backup.RestoredMark"/>): an unknown token is then told so.</summary>
    internal NativeBeeper.Host.Backup.RestoredMark? Restored { get; init; }

    /// <summary>
    /// The Windows server on its own PC (SWITCHBOARD.md 10.7): takes the one-time code Messages opens its page with (<see cref="NativeBeeper.Host.LocalPageLink"/>), from a
    /// browser on this PC only. Null on every other host.
    /// </summary>
    public Func<string?, bool>? OnceSignIn { get; init; }

    /// <summary>
    /// The Windows server on its own PC: a signed-in browser on this PC (loopback, never a relayed tailnet device) is not asked for the page's password again. That
    /// browser's session came from Messages' one-time code or the password, and the PC's own user is who runs this server. False on every other host.
    /// </summary>
    public bool DesktopTrust { get; init; }

    /// <summary>
    /// The certificate's private key is kept sealed with <see cref="Protector"/> (the Windows server's DPAPI, SWITCHBOARD.md 10.7), as its other secrets are; false keeps
    /// it plain PEM in the keys folder (the Linux host's keys volume).
    /// </summary>
    public bool SealTlsKey { get; init; }

    /// <summary>
    /// The Windows server on its own PC: Connect on its page, from a browser on this PC, opens that service's native sign-in on the desktop (it asks Messages, which owns
    /// those windows). Takes the server's word for the service (<c>ServerAccountProviders</c>); false when it could not ask. Null on every other host.
    /// </summary>
    public Func<string, bool>? DesktopSignIn { get; init; }
    public Func<string, string?, Func<bool>, Task<bool>>? DesktopAccountSignIn { get; init; }

    // A record's own text prints every member, the password with them: this one names only what is safe to see.
    public override string ToString() => $"NetworkHostOptions {{ BindAddress = {BindAddress}, Port = {Port}, Page = {(PagePassword is null ? "off" : "set")}, Relay = {(RelaySocket is null ? "off" : "on")} }}";

    /// <summary>The clock pairing codes expire by; a test moves it.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
