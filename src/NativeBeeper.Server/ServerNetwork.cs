using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Host.Network;
using NativeBeeper.Server.Linux;

namespace NativeBeeper.Server;

/// <summary>
/// What the Windows server listens on besides its pipe (SWITCHBOARD.md 10.7, the Windows half of 4.7), read from <c>&lt;root&gt;\Server\network.json</c> at each start:
/// the port of its page and door on 127.0.0.1, and whether its own door on the owner's tailnet runs (off until turned on: a node on Tailscale is the owner's choice),
/// and under what name. A file that is not there or cannot be read is the defaults; nothing here is secret.
/// </summary>
internal sealed record ServerNetworkSettings(int Port, bool Tailnet, string TailnetName)
{
    public const string FileName = "network.json";
    private sealed record Stored(int? Port, bool? Tailnet, string? TailnetName);

    /// <summary>
    /// The settings of <paramref name="root"/>. A development copy (<paramref name="isolated"/>) listens on any free port unless told otherwise, so it never takes the
    /// installed server's. The tailnet's name is <c>messages-</c> and this PC's name, as the phone finds it.
    /// </summary>
    public static ServerNetworkSettings Load(string root, bool isolated, string machine)
    {
        Stored? stored = null;
        try
        {
            var path = Path.Combine(root, "Server", FileName);
            if (File.Exists(path) && new FileInfo(path).Length <= 4096) stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { }
        var port = stored?.Port is >= 0 and <= 65535 ? stored.Port.Value : isolated ? 0 : NativeBeeper.Protocol.NetworkEndpoint.DefaultPort;
        return new(port, stored?.Tailnet == true, stored?.TailnetName is { } name && TailnetNames.IsLabel(name) ? name : TailnetNames.ForMachine(machine));
    }

    /// <summary>Keeps <paramref name="settings"/> for the next start, written whole. A development copy's port 0 is kept as 0 (any free port).</summary>
    public static void Save(string root, ServerNetworkSettings settings)
    {
        var path = Path.Combine(root, "Server", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Stored(settings.Port, settings.Tailnet, settings.TailnetName), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Tailnet node names: one DNS label, lower case.</summary>
internal static class TailnetNames
{
    public static bool IsLabel(string name) =>
        name.Length is > 0 and <= 63 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && name[0] != '-' && name[^1] != '-';

    /// <summary><c>messages-</c> and the machine's name as a label (<c>messages-server</c> when nothing of it is left).</summary>
    public static string ForMachine(string machine)
    {
        var label = new StringBuilder();
        foreach (var c in machine.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) label.Append(c);
            else if (label.Length > 0 && label[^1] != '-') label.Append('-');
        }
        var name = ("messages-" + label.ToString().Trim('-')).TrimEnd('-');
        if (name.Length > 63) name = name[..63].TrimEnd('-');
        return name == "messages" ? "messages-server" : name;
    }
}

/// <summary>
/// The Windows server's door on the network (SWITCHBOARD.md 10.7): the same <see cref="NetworkHost"/> as Linux's, on 127.0.0.1 only, with its page (opened from Messages
/// with a one-time code, <see cref="LocalPageLink"/>), pairing and asking to join for phones, pushes, the server's backup to a folder of the person's, and the tailnet
/// door (<c>messages-tailnet.exe</c>, Tailscale in userspace) when it is turned on. What is secret is kept in <c>&lt;root&gt;\Server\Keys</c>: the certificate and its
/// key, the page's key, the push project; the page's password and the door's node state sealed to the user with DPAPI. A port that is taken leaves the pipe serving and
/// says so; the network is never why the server does not start.
/// </summary>
internal sealed class ServerNetwork : IAsyncDisposable
{
    public const string KeysFolderName = "Keys";
    public const string PagePasswordFile = "page-password.protected";

    private readonly NetworkHost host;
    private readonly DoorSwitch doors;

    private ServerNetwork(NetworkHost host, DoorSwitch doors) { this.host = host; this.doors = doors; }

    public int Port => host.Port;

    public static string KeysFolder(string root) => Path.Combine(root, "Server", KeysFolderName);

    /// <summary>
    /// The relay socket the door hands tailnet devices to: in the user's own temporary folder (another account cannot reach it), one for each data root, short enough
    /// for a Unix socket's path.
    /// </summary>
    public static string RelaySocket(string instanceSuffix)
    {
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceSuffix)))[..8];
        return Path.Combine(Path.GetTempPath(), "messages-tailnet-" + tag, "relay.sock");
    }

    /// <summary>
    /// Asks Messages, beside this program, to open a service's sign-in (<c>Messages.exe --sign-in=&lt;service&gt;</c>): a running Messages takes it through its own
    /// activation, and one that is not running starts and opens it. With this process's environment, so a development copy's data folder is the same one.
    /// </summary>
    internal static bool AskMessagesToSignIn(string service, Action<string> log)
    {
        var messages = Path.Combine(AppContext.BaseDirectory, "Messages.exe");
        if (!File.Exists(messages) || !NativeBeeper.Presentation.Services.LaunchCommand.SignInServices.Contains(service)) return false;
        try
        {
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(messages, NativeBeeper.Presentation.Services.LaunchCommand.SignInArgument(service))
                { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            return started is not null;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"server desktop sign_in failed type={failure.GetType().Name}");
            return false;
        }
    }

    /// <summary>The page's password: made once, 24 random characters, sealed to the user; Messages signs the page in with a one-time code, so nobody types it.</summary>
    public static async Task<string> PagePasswordAsync(string keys, IStateProtector protector)
    {
        var path = Path.Combine(keys, PagePasswordFile);
        if (File.Exists(path))
        {
            try { return Encoding.UTF8.GetString(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false)); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CryptographicException or System.Runtime.InteropServices.COMException) { }
        }
        const string letters = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var password = string.Create(24, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)]; });
        Directory.CreateDirectory(keys);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, await protector.ProtectAsync(Encoding.UTF8.GetBytes(password)).ConfigureAwait(false)).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        return password;
    }

    public static async Task<ServerNetwork?> StartAsync(ProtocolHost protocolHost, NativeBeeper.Host.Backup.ServerBackupService? serverBackup, string root, IStateProtector protector,
        Action<string> log, CancellationToken stop)
    {
        var settings = ServerNetworkSettings.Load(root, AppPaths.IsIsolated, Environment.MachineName);
        var keys = KeysFolder(root);
        var push = Path.Combine(keys, "push");
        // Always listened on, so the page's switch can start the door without starting the server again: a socket in the user's own temporary folder, which only the
        // door beside the server reaches.
        var relay = RelaySocket(AppPaths.InstanceSuffix);
        var tailnet = new TailnetBoard(settings.Tailnet, settings.Port);
        var backups = serverBackup is { } backup && protocolHost.Services.Admin is { } admin
            ? new ServerPageBackups(backup, async ct => (await admin.AccountsAsync(ct).ConfigureAwait(false)).Count, async ct =>
            {
                int signedOut = 0, left = 0;
                foreach (var account in await admin.AccountsAsync(ct).ConfigureAwait(false))
                {
                    if (!account.Removable) { left++; continue; }
                    try { if ((await admin.RemoveAccountAsync(account.Provider, account.Key, ct).ConfigureAwait(false)).Removed) signedOut++; }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        log($"server reset sign_out failed provider={account.Provider} type={failure.GetType().Name}");
                        return (signedOut, left, true);
                    }
                }
                return (signedOut, left, false);
            },
            // A restore or a reset is applied by the next start of the server, which this one cannot make by itself: the page says it waits for it.
            _ => false)
            : null;
        NetworkHost network;
        try
        {
            var restored = await NativeBeeper.Host.Backup.RestoredMark.LoadAsync(Path.Combine(root, NativeBeeper.Host.Backup.RestoredMark.FileName), protector).ConfigureAwait(false);
            network = await NetworkHost.StartAsync(protocolHost, new NetworkHostOptions(root, protector, log)
            {
                // This PC only: phones reach it through the tailnet door, never a port on the home network.
                BindAddress = "127.0.0.1", Port = settings.Port, KeysFolder = keys,
                // A pairing code is shown on the page when it asks for one; nothing is printed anywhere else.
                ShowCode = _ => { },
                PagePassword = await PagePasswordAsync(keys, protector).ConfigureAwait(false),
                OnceSignIn = code => LocalPageLink.TryRedeem(root, code, DateTimeOffset.UtcNow), DesktopTrust = true,
                // The certificate's key sealed to the user, like the server's other secrets here (the backup reads it as HostStartup.BackupPaths says).
                SealTlsKey = true,
                // Connect on the page opens the native sign-in on this desktop, through Messages (the server draws nothing).
                DesktopSignIn = service => AskMessagesToSignIn(service, log),
                RelaySocket = relay, Tailnet = tailnet,
                Push = FirebaseTransport.TryLoad(push, log), PushFolder = push,
                Backups = backups, Restored = restored,
            }, stop).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A port another program holds (an address in use) is the common case. The pipe still serves this PC.
            log($"server network failed type={failure.GetType().Name} port={settings.Port.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }
        LocalPageLink.WritePort(root, network.Port);
        // Inside the pipe's own bound at the stop (ServerRun.StopNetworkAsync): the door has two seconds to close its node before it is ended.
        TailnetDoor.StopWait = TimeSpan.FromSeconds(2);
        log($"server listening transport=network port={network.Port.ToString(CultureInfo.InvariantCulture)} tailnet={(settings.Tailnet ? "on" : "off")}");
        var opened = new ServerNetwork(network, new DoorSwitch(root, settings, keys, relay, tailnet, log));
        // The page's Tailscale card is this switch on Windows: kept in Server\network.json and applied at once.
        tailnet.Turn = opened.doors.Turn;
        if (settings.Tailnet) opened.doors.Turn(true);
        return opened;
    }

    /// <summary>
    /// The tailnet door, on or off as the page's switch says (SWITCHBOARD.md 10.7): the choice written to <c>Server\network.json</c> first, so a failed start is still the
    /// person's choice at the next start, then the door started or stopped. One change at a time.
    /// </summary>
    internal sealed class DoorSwitch(string root, ServerNetworkSettings settings, string keys, string relay, TailnetBoard board, Action<string> log)
    {
        private readonly object gate = new();
        private TailnetDoor? running;
        private ServerNetworkSettings current = settings;

        public bool Turn(bool on)
        {
            TailnetDoor? stopping = null;
            lock (gate)
            {
                try { ServerNetworkSettings.Save(root, current = current with { Tailnet = on }); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    log($"server tailnet turn failed type={failure.GetType().Name}");
                    return false;
                }
                if (on && running is null)
                {
                    running = TailnetDoor.Start(Path.Combine(AppContext.BaseDirectory, TailnetDoor.Executable + ".exe"), Path.Combine(keys, TailnetDoor.StateFolder), relay,
                        current.TailnetName, current.Port == 0 ? NativeBeeper.Protocol.NetworkEndpoint.DefaultPort : current.Port, log,
                        // The sign-in link and the Tailnet Lock key: the page shows them (its Tailscale card reads the board); a Windows server has no console to print them to.
                        _ => { }, board: board);
                    if (running is null) return false;
                }
                else if (!on && running is not null) (stopping, running) = (running, null);
                board.SetEnabled(on);
            }
            // Its node closed outside the lock, within the door's own bound.
            stopping?.StopAsync().GetAwaiter().GetResult();
            log($"server tailnet turned on={(on ? "yes" : "no")}");
            return true;
        }

        public async Task StopAsync()
        {
            TailnetDoor? door;
            lock (gate) (door, running) = (running, null);
            if (door is not null) await door.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The stop, after the pipe's: the door's clients get the same drain, then the tailnet door closes its node.</summary>
    public async Task StopAsync(TimeSpan drain)
    {
        try { await host.StopAsync(drain).ConfigureAwait(false); } catch (Exception) { }
        await doors.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.Zero).ConfigureAwait(false);
}
