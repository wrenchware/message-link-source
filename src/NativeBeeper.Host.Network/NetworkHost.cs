using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// The host's door on the network (SWITCHBOARD.md stage 4): Kestrel on one TCP port, HTTPS only (there is no plain listener at all), and one route,
/// <c>GET /v1/socket</c>, a WebSocket carrying the protocol's text frames (<see cref="WebSocketFrameChannel"/>, 4 MiB cap) to a
/// <see cref="ProtocolDispatcher"/> of its own over the one host, as the pipe's <c>ServerPipe</c> does. Beside it, when the owner set a password, the server's own page
/// (<see cref="ServerPage"/>, a few paths of server-made HTML). Everything else is 404; there are no files.
/// <para>
/// An upgrade is admitted by a device token (<c>Authorization: Bearer</c>, <see cref="DeviceRegistry"/>) or by the open pairing code
/// (<see cref="NetworkPairing.Header"/>, <see cref="PairingCodes"/>), which only ever reaches <see cref="PairingSession"/>. Nothing is reachable without one.
/// A request that is refused gets its status and the connection is closed, logged as <c>protocol connection refused reason=token|pairing|handshake</c>,
/// the first and then one in a hundred; a pairing code tried too soon is answered 429 without being looked at (<c>reason=pairing_paced</c>,
/// <see cref="PairingCodes"/>). No more than <see cref="NetworkHostOptions.MaxPending"/> connections may be in the door at once (not yet through TLS and
/// an admitted upgrade), and no more than <see cref="NetworkHostOptions.MaxPendingPerAddress"/> of them from one address; one past either is dropped
/// unread (<c>protocol connection dropped reason=pending|pending_address</c>, likewise). Kestrel and the WebSocket layer are given no log provider, so
/// nothing a peer sends reaches a log.
/// </para>
/// <para>
/// With <see cref="NetworkHostOptions.RelaySocket"/> a second listener, a Unix socket for the server's door on the owner's tailnet, serves the same routes behind the
/// same TLS and door, each connection first naming its tailnet device (<see cref="TailnetRelay"/>; one without a good line is dropped, <c>reason=relay</c>).
/// </para>
/// </summary>
internal sealed class NetworkHost : IAsyncDisposable
{
    /// <summary>How long a peer has for TLS, and then for the request head; what a peer holds a place in the door with.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long each step of a stop waits (closing the connections, then the server), as the pipe's.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    private const string DoorKey = "messages.door";
    private const string RelayKey = "messages.relay";
    private const int MaxRequestHead = 8 * 1024;

    /// <summary>A connection let in with a device token, as the serving delegate gets it.</summary>
    internal sealed class Admitted(string deviceId, WebSocketFrameChannel channel, IPairedDevices devices)
    {
        public string DeviceId { get; } = deviceId;
        public WebSocketFrameChannel Channel { get; } = channel;
        /// <summary>This connection's <c>devices.*</c>: the host's devices, with this one marked as the one asking.</summary>
        public IPairedDevices Devices { get; } = devices;
        /// <summary>Set by whatever serves the connection: refuse new requests from now on (see <see cref="ProtocolDispatcher.BeginDraining"/>).</summary>
        public Action? BeginDraining { get; set; }
        /// <summary>Set by whatever serves the connection: completes once every request it accepted has been answered.</summary>
        public Func<Task>? WhenAnswered { get; set; }

        /// <summary>Admission order across the host: the oldest connection of a device is the lowest.</summary>
        public long Serial { get; init; }

        private int ending;

        /// <summary>Whether <see cref="End"/> has been called: this connection is being answered out and closed.</summary>
        public bool Ending => Volatile.Read(ref ending) != 0;

        // Answers what was accepted (a revoke's own answer among them), then closes: the device's next upgrade is refused.
        public void End()
        {
            if (Interlocked.Exchange(ref ending, 1) != 0) return;
            BeginDraining?.Invoke();
            _ = Task.Run(async () =>
            {
                try { if (WhenAnswered is { } answered) await answered().WaitAsync(StopWait).ConfigureAwait(false); }
                catch (Exception) { }
                Channel.Close();
            });
        }
    }

    // Holds a connection's place in the door (and its address's) until it is admitted or gone, whichever is first.
    private sealed class DoorTicket(NetworkHost host, string source)
    {
        private int left;
        public long Arrived { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public void Leave()
        {
            if (Interlocked.Exchange(ref left, 1) != 0) return;
            Interlocked.Decrement(ref host.pending);
            host.LeaveSource(source);
        }
    }

    private readonly NetworkHostOptions options;
    private readonly Action<string> log;
    private readonly Func<Admitted, Task> serve;
    private readonly string version;
    private readonly X509Certificate2 certificate;
    private readonly DeviceRegistry devices;
    private readonly PairingCodes pairing;
    private readonly JoinRequests joins;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Admitted, bool> connections = new();
    private readonly Dictionary<string, int> pendingBySource = new(StringComparer.Ordinal);
    private readonly Action? flush;
    private readonly Timer pairingCheck;
    private readonly ServerPage? page;
    private WebApplication? app;
    private int pending, dropped, refused, stopped;
    private long admissions;
    private volatile bool draining;
    // Under its own lock: the push notifier can be added after the start, when pushes are turned on from the page.
    private readonly List<IAsyncDisposable> owns = [];
    private bool ownsClosed;

    /// <summary>The server's pushes as they are now, for the harness; null on a host started without them.</summary>
    internal PushSwitch? Pushes { get; private set; }

    /// <summary>How the server's pushes are doing (10.9), for the harness; null on a host started without them.</summary>
    internal PushHealth? PushHealth { get; private set; }

    /// <summary>Each device's push address and reports of a missing app, for the harness; null on a host started without them.</summary>
    internal PushRegistry? PushRegistrations { get; private set; }

    /// <summary>Raised with a device's id once it has been removed.</summary>
    internal event Action<string>? DeviceRevoked { add => devices.Revoked += value; remove => devices.Revoked -= value; }

    /// <summary>Something that lives as long as the host and stops with it (the push notifier).</summary>
    internal void Own(IAsyncDisposable owned)
    {
        lock (owns)
        {
            if (!ownsClosed) { owns.Add(owned); return; }
        }
        // Added as the host stopped: it stops at once.
        _ = owned.DisposeAsync().AsTask();
    }

    private NetworkHost(NetworkHostOptions options, Func<Admitted, Task> serve, string version, X509Certificate2 certificate, DeviceRegistry devices, Action? flush,
        IServerAdmin? admin, IHostLogs? logs, ServerPage.Pushes? pushes, IWholeHistory? history)
    {
        this.options = options; log = options.Log; this.serve = serve; this.version = version; this.certificate = certificate; this.devices = devices; this.flush = flush;
        pairing = new PairingCodes(options.Time, log, options.ShowCode);
        joins = new JoinRequests(options.Time, log, Path.Combine(options.KeysFolder ?? options.DataFolder, "join-requests-paused"));
        var keys = options.KeysFolder ?? options.DataFolder;
        // The first run (SWITCHBOARD.md 10.4): with no password where the server was installed, the page sets one up; its setup code is shown as a pairing code is.
        var setup = options.FirstRun
            ? new ServerPageSetup(options.ShowCode, options.Tailnet is { Enabled: true } ? new TailscaleAdmin(keys, options.Protector, log, options.TailscaleHandler) : null)
            : null;
        page = ServerPage.Create(options.PagePassword, keys, devices, pairing, joins, () => Fingerprint, admin, logs, options.Time, log,
            options.Tailnet, pushes, history, options.Backups, setup);
        // The Windows server on its own PC (SWITCHBOARD.md 10.7, ServerPage.Desktop.cs).
        if (page is not null)
        {
            page.OnceSignIn = options.OnceSignIn;
            page.DesktopTrust = options.DesktopTrust;
            page.DesktopSignIn = options.DesktopSignIn;
        }
        devices.Revoked += EndDevice;
        devices.Restored = options.Restored;
        // Real time, whatever clock the codes expire by: the check itself reads that clock (a test calls CheckPairing after moving it).
        pairingCheck = new Timer(_ => CheckPairing(), null, PairingCheckEvery, PairingCheckEvery);
    }

    /// <summary>How often the host looks for an unpaired host whose code has expired (and opens a new one).</summary>
    public static readonly TimeSpan PairingCheckEvery = TimeSpan.FromSeconds(30);

    /// <summary>The port it listens on (the one asked for, or the one taken when that was 0).</summary>
    public int Port { get; private set; }

    /// <summary>What Kestrel says it listens on: one <c>https://</c> address.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    /// <summary>The certificate's SHA-256 as lowercase hex: what a client pins.</summary>
    public string Fingerprint => NetworkEndpoint.Fingerprint(certificate);

    /// <summary>How many connections are in the door now, and how many were let go unread because it was full.</summary>
    public int Pending => Volatile.Read(ref pending);
    public int Dropped => Volatile.Read(ref dropped);

    /// <summary>How many requests have been refused at the door.</summary>
    public int Refused => Volatile.Read(ref refused);

    /// <summary>How many admitted devices' connections are open now.</summary>
    public int Connections => connections.Count;

    /// <summary>Raised when <see cref="Connections"/> changes, from the thread that changed it.</summary>
    public event Action? ConnectionsChanged;

    internal DeviceRegistry Devices => devices;
    internal PairingCodes Pairing => pairing;
    internal JoinRequests Joins => joins;
    internal bool HasPage => page is not null;

    /// <summary>Opens a new pairing code in place of any other and logs it (<c>server pairing code=</c>): what an integrator's <c>--pair</c> does on a host that is running.</summary>
    public string IssuePairingCode() => pairing.Issue();

    /// <summary>
    /// A host with no device paired and no code open opens a new one: nobody can reach it until something pairs, and a code that ran out must not need a
    /// restart (which reconnects the accounts) to replace. Runs every <see cref="PairingCheckEvery"/>.
    /// </summary>
    internal void CheckPairing()
    {
        if (Volatile.Read(ref stopped) != 0 || draining) return;
        try
        {
            if (devices.Count == 0 && !pairing.Open) pairing.Issue();
            // Requests to join that ran out end here too, within a check of their time.
            joins.Sweep();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Serves <paramref name="host"/> on the network: a dispatcher per admitted connection, every one over the host's services and its one journal, so a
    /// client that comes back finds what it started. Makes the certificate and opens a pairing code on the way when they are needed (see
    /// <see cref="NetworkHostOptions"/>).
    /// </summary>
    public static Task<NetworkHost> StartAsync(ProtocolHost host, NetworkHostOptions options, CancellationToken cancellationToken = default) =>
        StartAsync(host.Services, host.Journal, host.Version, options, cancellationToken);

    /// <summary>The same, over services and a journal given directly (the harness serves fakes this way).</summary>
    public static async Task<NetworkHost> StartAsync(ProtocolServices services, OperationJournal journal, string version, NetworkHostOptions options,
        CancellationToken cancellationToken = default)
    {
        var served = new ProtocolCallStats("protocol served", options.Log);
        // Each device's push address (SWITCHBOARD.md 7.5), and which of its connections are in front, so a push goes only to a phone not showing the app.
        // The device list is read first, so a push address counts only while its device is paired.
        Directory.CreateDirectory(options.DataFolder);
        var devices = await DeviceRegistry.LoadAsync(options.DataFolder, options.Protector, options.Time, options.Log, options.KeysFolder).ConfigureAwait(false);
        var pushes = await PushRegistry.LoadAsync(options.DataFolder, options.Protector, options.Log, devices.Has).ConfigureAwait(false);
        // Pushes as they are now: on from the start with the folder's files, or turned on later from the server's page.
        var pushSwitch = new PushSwitch(options.Push, options.PushFolder, options.Log, options.PushHandler);
        // How they are doing, for the page's card and push.status (10.9).
        var health = new PushHealth(options.Time, options.Log);
        var presence = new DevicePresence();
        var host = await StartAsync(admitted =>
        {
            var dispatcher = new ProtocolDispatcher(admitted.Channel, services with
            {
                Devices = admitted.Devices,
                Push = new DevicePush(() => pushSwitch.Current, pushes, admitted.DeviceId, health, () => devices.List(null), options.Time),
                ClientState = presence.For(admitted.DeviceId, services.ClientState),
            }, version, served, options.Log, journal);
            admitted.BeginDraining = dispatcher.BeginDraining;
            admitted.WhenAnswered = dispatcher.WhenAnsweredAsync;
            presence.Opened(dispatcher, admitted.DeviceId);
            var run = dispatcher.Start();
            _ = run.ContinueWith(_ => presence.Closed(dispatcher), TaskScheduler.Default);
            return run;
        }, version, options, cancellationToken, served.Flush, services.Admin, services.Logs, devices, new ServerPage.Pushes(pushSwitch, pushes, health), services.WholeHistory).ConfigureAwait(false);
        host.DeviceRevoked += device =>
        {
            _ = pushes.RemoveAsync(device, "device_removed");
            health.Forget(device);
        };
        host.Pushes = pushSwitch;
        host.PushHealth = health;
        host.PushRegistrations = pushes;
        // Signing in on the page runs through the same sign-ins as every connection's (services.SignIns).
        host.page?.OfferSignIn(services);
        // Another project's files: every phone's address was the old project's, so each is forgotten and signs up again (at once when it is open: below).
        pushSwitch.ProjectChanged += changedAt =>
        {
            foreach (var registration in pushes.All().Where(registration => registration.At < changedAt)) _ = pushes.RemoveAsync(registration.DeviceId, "project_changed", registration.Token);
        };
        // Pushes turned on, or the files replaced (10.9): the reports of a missing app were about the old files, and every open phone of 27 or later is told to ask
        // for the offer again, so it signs up now rather than at its next connection.
        pushSwitch.Installed += () =>
        {
            _ = pushes.ClearMissingAsync();
            if (services.Events is { } announced)
            {
                announced.AppendSignal(new AccountSignal(AccountSignals.Push));
                options.Log("push announced");
            }
        };
        if (services.Events is { } events && services.Chats is { } chats && services.Sender is { } sender)
        {
            // Started once pushes are on, at the start or when the page turns them on, and sending through the switch, so new files take over at once.
            var started = 0;
            PushNotifier? notifier = null;
            void Start()
            {
                if (Interlocked.Exchange(ref started, 1) == 0) host.Own(notifier = new PushNotifier(events, chats, sender, pushes, pushSwitch, presence.InFront, options.Log, services.SharedState,
                    quietFor: presence.AtAnotherDevice, health: health));
                if (services.Scheduled is HostScheduledSends schedule) schedule.OnHeld(chatId => notifier?.ScheduledHeld(chatId));
            }
            // Two days without a good backup (stage 10): every phone signed up for notifications is told; with none, nobody is, and the backup asks again later.
            if (options.Backups?.Service is { } backup) backup.StaleNotice = _ => Task.FromResult(notifier?.BackupStale() > 0);
            pushSwitch.TurnedOn += Start;
            if (pushSwitch.Current is not null) Start();
        }
        return host;
    }

    /// <summary>
    /// The door alone: <paramref name="serve"/> gets each admitted connection and returns what completes when it is done. It must set the connection's
    /// <see cref="Admitted.BeginDraining"/> and <see cref="Admitted.WhenAnswered"/> before it first awaits, or a revoke and a stop simply close it.
    /// </summary>
    /// <param name="loaded">The paired devices, when the caller has read them already (the push registrations are judged by them); otherwise read here.</param>
    internal static async Task<NetworkHost> StartAsync(Func<Admitted, Task> serve, string version, NetworkHostOptions options, CancellationToken cancellationToken = default,
        Action? flush = null, IServerAdmin? admin = null, IHostLogs? logs = null, DeviceRegistry? loaded = null, ServerPage.Pushes? pushes = null, IWholeHistory? history = null)
    {
        if (!IPAddress.TryParse(options.BindAddress, out var address)) throw new ArgumentException("The bind address is not an IP address.", nameof(options));
        Directory.CreateDirectory(options.DataFolder);
        var devices = loaded ?? await DeviceRegistry.LoadAsync(options.DataFolder, options.Protector, options.Time, options.Log, options.KeysFolder).ConfigureAwait(false);
        var certificate = ServerCertificate.LoadOrCreate(options.KeysFolder ?? options.DataFolder, options.HostNames, options.Log, options.SealTlsKey ? options.Protector : null);
        var host = new NetworkHost(options, serve, version, certificate, devices, flush, admin, logs, pushes, history);
        try
        {
            await host.ListenAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // No device yet means nobody can reach anything until one pairs, so a code is printed with no need to ask.
        if (options.Pair || devices.Count == 0) host.IssuePairingCode();
        return host;
    }

    private async Task ListenAsync(IPAddress address, CancellationToken cancellationToken)
    {
        // The empty builder: no configuration sources (so no ASPNETCORE_URLS or appsettings can add a listener), no log providers, no static files.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddLogging();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestHeadersTotalSize = MaxRequestHead;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxRequestBodySize = 0;
            kestrel.Limits.RequestHeadersTimeout = HandshakeTimeout;
            kestrel.Limits.KeepAliveTimeout = HandshakeTimeout;
            kestrel.Limits.MaxConcurrentConnections = 256;
            // An upgraded WebSocket stops counting against the limit above, so it needs its own: without one, connections that are through the door and
            // upgraded are limited by nothing but the per-device share below.
            kestrel.Limits.MaxConcurrentUpgradedConnections = options.MaxUpgradedConnections;
            kestrel.Listen(address, options.Port, listen =>
            {
                // WebSockets over HTTP/1.1 only: one request per connection, and the upgrade is the whole of the protocol.
                listen.Protocols = HttpProtocols.Http1;
                // Ahead of TLS, so a peer that never finishes its handshake still holds one of the few places.
                listen.Use(next => connection => DoorAsync(connection, next));
                listen.UseHttps(Tls());
            });
            // The tailnet door's connections (SWITCHBOARD.md 4.7): the same door, TLS and routes, each connection first naming its device (TailnetRelay).
            if (options.RelaySocket is { } relay)
            {
                TailnetRelay.Prepare(relay);
                kestrel.ListenUnixSocket(relay, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.Use(next => connection => RelayAsync(connection, next));
                    listen.Use(next => connection => DoorAsync(connection, next));
                    listen.UseHttps(Tls());
                });
            }
        });
        var built = builder.Build();
        app = built;
        built.UseWebSockets(new WebSocketOptions { KeepAliveInterval = NetworkEndpoint.KeepAlive, KeepAliveTimeout = NetworkEndpoint.KeepAlive });
        built.Run(HandleAsync);
        await built.StartAsync(cancellationToken).ConfigureAwait(false);
        Addresses = [.. built.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? []];
        // The port's address, not the relay socket's (Kestrel names that one https://unix:...).
        Port = Addresses.FirstOrDefault(bound => !bound.Contains("unix:", StringComparison.Ordinal)) is { } bound && Uri.TryCreate(bound, UriKind.Absolute, out var uri) ? uri.Port : options.Port;
        log($"server network listening port={Port}{(options.RelaySocket is null ? "" : " relay=on")}");
    }

    private HttpsConnectionAdapterOptions Tls() => new()
    {
        ServerCertificate = certificate,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateMode = ClientCertificateMode.NoCertificate,
        HandshakeTimeout = HandshakeTimeout,
    };

    // The relay socket's first line names the tailnet device, which from then on is the connection's address (TailnetRelay). The socket is reachable only from
    // this machine's account (the door), but its line is still read strictly and within the handshake's time, and a connection without a good one is dropped.
    private async Task RelayAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        IPEndPoint? device;
        using (var limit = new CancellationTokenSource(HandshakeTimeout))
        {
            try { device = await TailnetRelay.ReadAsync(connection.Transport.Input, limit.Token).ConfigureAwait(false); }
            catch (Exception) { device = null; }
        }
        if (device is null)
        {
            Drop(connection, "relay");
            return;
        }
        connection.RemoteEndPoint = device;
        connection.Items[RelayKey] = true;
        await next(connection).ConfigureAwait(false);
    }

    private async Task DoorAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        if (Interlocked.Increment(ref pending) > options.MaxPending)
        {
            Interlocked.Decrement(ref pending);
            // Unread and unanswered: whoever fills the door does not get to take the rest of the server's time.
            Drop(connection, "pending");
            return;
        }
        // Under the door's size, each address's own: one machine on the home network cannot hold every place and keep the owner's devices out.
        var source = PairingCodes.Key((connection.RemoteEndPoint as IPEndPoint)?.Address);
        if (!EnterSource(source))
        {
            Interlocked.Decrement(ref pending);
            Drop(connection, "pending_address");
            return;
        }
        var ticket = new DoorTicket(this, source);
        connection.Items[DoorKey] = ticket;
        try { await next(connection).ConfigureAwait(false); }
        finally { ticket.Leave(); }
    }

    private void Drop(ConnectionContext connection, string reason)
    {
        var count = Interlocked.Increment(ref dropped);
        if (count == 1 || count % 100 == 0) log($"protocol connection dropped reason={reason} count={count}");
        connection.Abort();
    }

    private bool EnterSource(string source)
    {
        lock (pendingBySource)
        {
            var held = pendingBySource.GetValueOrDefault(source);
            if (held >= options.MaxPendingPerAddress) return false;
            pendingBySource[source] = held + 1;
            return true;
        }
    }

    private void LeaveSource(string source)
    {
        lock (pendingBySource)
        {
            if (!pendingBySource.TryGetValue(source, out var held)) return;
            if (held <= 1) pendingBySource.Remove(source);
            else pendingBySource[source] = held - 1;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        // The page's own socket for signing in on it (SWITCHBOARD.md 10.6), to the home network only as the page is; the page itself asks for its cookie, its token and its origin.
        if (page is not null && string.Equals(request.Path.Value, ServerPage.SignInSocketPath, StringComparison.Ordinal) && FromTrustedNetwork(context))
        {
            await page.SignInSocketAsync(context, draining, () => Ticket(context)?.Leave(), stopping.Token).ConfigureAwait(false);
            return;
        }
        // The server's own page, when its owner set it a password (ServerPage): its few paths only, to the home network only, each answer closing its connection.
        if (page is not null && ServerPage.Serves(request.Path.Value) && FromTrustedNetwork(context))
        {
            await page.HandleAsync(context, draining).ConfigureAwait(false);
            return;
        }
        // Exactly the one path, compared as written (PathString's own comparison ignores case).
        if (!string.Equals(request.Path.Value, NetworkEndpoint.Target, StringComparison.Ordinal) || request.QueryString.HasValue || !HttpMethods.IsGet(request.Method))
        {
            await RefuseAsync(context, StatusCodes.Status404NotFound, "handshake").ConfigureAwait(false);
            return;
        }
        // A browser always names its page's origin; this app's clients never do. A page on the home network gets nothing here, not even a try at a code.
        if (request.Headers.ContainsKey("Origin") || !context.WebSockets.IsWebSocketRequest)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "handshake").ConfigureAwait(false);
            return;
        }
        if (draining)
        {
            await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "handshake").ConfigureAwait(false);
            return;
        }
        string? device = null;
        byte[]? code = null;
        JoinRequests.Request? joining = null;
        var authorization = request.Headers.Authorization;
        if (authorization.Count > 0)
        {
            // A token, and only a token: a request that also carries a code is judged by its token alone.
            var presentedToken = authorization.Count == 1 ? ClientToken.Presented(authorization[0]) : null;
            device = presentedToken is null ? null : devices.Authenticate(presentedToken);
            if (device is null)
            {
                // A server restored from a backup (stage 10) says so to a token it does not know, so the device can say to ask to join again (NetworkEndpoint).
                if (devices.RefusesAsRestored(presentedToken)) await RefuseAsync(context, StatusCodes.Status401Unauthorized, "restored", NetworkPairing.RefusalRestored).ConfigureAwait(false);
                else await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token").ConfigureAwait(false);
                return;
            }
        }
        else if (request.Headers.TryGetValue(NetworkPairing.Header, out var presented) && presented.Count == 1)
        {
            code = pairing.Take(presented[0], context.Connection.RemoteIpAddress, out var paced);
            if (code is null)
            {
                // Too soon is not a wrong code: the client tries again later, and the code is still good.
                if (paced) await RefuseAsync(context, StatusCodes.Status429TooManyRequests, "pairing_paced").ConfigureAwait(false);
                else await RefuseAsync(context, StatusCodes.Status401Unauthorized, "pairing").ConfigureAwait(false);
                return;
            }
        }
        else if (request.Headers.TryGetValue(NetworkPairing.JoinHeader, out var join) && join.Count == 1 && join[0] == "1")
        {
            // Asking to join (protocol 21), from the home network only, as the page is: nobody elsewhere may put a request in front of the owner. A server nobody could
            // approve a request on (no paired device, no page) says so, and the first device pairs with the printed code.
            if (!FromTrustedNetwork(context))
            {
                await RefuseAsync(context, StatusCodes.Status403Forbidden, "join_not_home").ConfigureAwait(false);
                return;
            }
            if (devices.Count == 0 && (page is null || page.SettingUp))
            {
                await RefuseAsync(context, StatusCodes.Status409Conflict, "join_no_approver").ConfigureAwait(false);
                return;
            }
            if (joins.Admit(context.Connection.RemoteIpAddress, out joining) != JoinRequests.Admission.Admitted)
            {
                await RefuseAsync(context, StatusCodes.Status429TooManyRequests, "join_paced").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, "token").ConfigureAwait(false);
            return;
        }

        WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = NetworkEndpoint.KeepAlive, KeepAliveTimeout = NetworkEndpoint.KeepAlive, DangerousEnableCompression = false,
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A join let in holds its place until its connection is done: one that never opened gives it back.
            if (joining is not null) joins.Leave(joining);
            throw;
        }
        var ticket = Ticket(context);
        ticket?.Leave();
        var channel = new WebSocketFrameChannel(socket, log);
        if (joining is not null)
        {
            log("protocol connection accepted transport=network purpose=join");
            await JoinSession.RunAsync(channel, joining, joins, devices, certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256), version, log, stopping.Token)
                .ConfigureAwait(false);
            await ClosedAsync(channel).ConfigureAwait(false);
            return;
        }
        if (code is not null)
        {
            log("protocol connection accepted transport=network purpose=pairing");
            await PairingSession.RunAsync(channel, code, devices, version, log, stopping.Token).ConfigureAwait(false);
            await ClosedAsync(channel).ConfigureAwait(false);
            return;
        }
        await ServeDeviceAsync(device!, channel, ticket?.Arrived, Relayed(context)).ConfigureAwait(false);
    }

    private async Task ServeDeviceAsync(string device, WebSocketFrameChannel channel, long? arrived, bool relayed)
    {
        var admitted = new Admitted(device, channel, new DeviceView(devices, device, pairing, joins, log, address => JoinLinkFor(address))) { Serial = Interlocked.Increment(ref admissions) };
        // Seen before serve starts the dispatcher: from then on the device's requests are answered on other threads while this one carries on below, so a
        // devices.list among its first could list the device asking as never connected (the harness caught it under load, 2026-10-16).
        devices.Seen(device);
        Task running;
        try { running = serve(admitted); }
        catch (Exception)
        {
            channel.Abort();
            throw;
        }
        connections[admitted] = true;
        // After it is in the set, so either a revoke or a stop finds it there, or this sees what they did: a connection admitted at the moment its device
        // was revoked, or the host began to stop, is never left open.
        if (!devices.Has(device) || draining) admitted.End();
        EndSurplus(device);
        // ms: from the TCP connection to here (TLS, the request, the upgrade, the dispatcher), so a slow first connection says which side was slow.
        // via=tailnet: through the server's door on the owner's tailnet (SWITCHBOARD.md 4.7), so its ms include the phone's tunnel.
        log($"protocol connection accepted transport=network device={device}{(arrived is { } at ? $" ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds}" : "")}{(relayed ? " via=tailnet" : "")}");
        ConnectionsChanged?.Invoke();
        try { await running.ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            connections.TryRemove(admitted, out _);
            ConnectionsChanged?.Invoke();
            await ClosedAsync(channel).ConfigureAwait(false);
        }
    }

    // A device holds at most MaxConnectionsPerDevice connections: the newest are kept, the oldest are answered out and closed. A paired device that opens
    // connections without end (a compromised one, or a client with a bug) is held to a few instead of to the host's memory and sockets.
    private void EndSurplus(string device)
    {
        var mine = connections.Keys.Where(connection => connection.DeviceId == device && !connection.Ending).OrderBy(connection => connection.Serial).ToArray();
        for (var i = 0; i < mine.Length - options.MaxConnectionsPerDevice; i++)
        {
            log($"protocol connection ended reason=device_limit device={device}");
            mine[i].End();
        }
    }

    // Kestrel ends the connection when the handler returns, so the handler stays until the close handshake is done (or the channel's own bound ends it).
    private static async Task ClosedAsync(WebSocketFrameChannel channel)
    {
        channel.Close();
        try { await channel.Completion.WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static DoorTicket? Ticket(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(DoorKey, out var ticket) == true ? ticket as DoorTicket : null;

    private static bool Relayed(HttpContext context) => context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(RelayKey) == true;
    // The private relay socket authenticates the tailnet path before TLS; an address or HTTP header cannot do that.
    private bool FromTrustedNetwork(HttpContext context) => Relayed(context) || (options.HomeCheck ?? ServerPage.FromHome)(context.Connection.RemoteIpAddress);

    private async Task RefuseAsync(HttpContext context, int status, string reason, string? word = null)
    {
        // The first, then one in a hundred, as the pipe's door: a peer that keeps knocking does not fill the log. The count is the total either way.
        var count = Interlocked.Increment(ref refused);
        if (count == 1 || count % 100 == 0) log($"protocol connection refused reason={reason}{(count == 1 ? "" : $" count={count}")}");
        var response = context.Response;
        response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized) response.Headers.WWWAuthenticate = "Bearer";
        if (word is not null) response.Headers[NetworkPairing.RefusalHeader] = word;
        // One answer per connection: a refused peer cannot keep its place in the door by asking again on it.
        response.Headers.Connection = "close";
        response.ContentLength = 0;
        await response.CompleteAsync().ConfigureAwait(false);
    }

    private void EndDevice(string device)
    {
        foreach (var admitted in connections.Keys.Where(admitted => admitted.DeviceId == device).ToArray()) admitted.End();
    }

    /// <summary>
    /// From now on every connection refuses its requests (<c>unavailable</c>), and an upgrade is refused; what each already accepted goes on to its answer.
    /// A stop begins with this; <see cref="StopAsync"/> repeats it harmlessly.
    /// </summary>
    public void BeginDraining()
    {
        draining = true;
        foreach (var admitted in connections.Keys.ToArray()) admitted.BeginDraining?.Invoke();
    }

    /// <summary>
    /// Stops the way a stopping server needs it: no new connection, each open one given up to <paramref name="drain"/> to have the answers to what it asked
    /// written, then every connection closed and the listener let go. Safe to call twice and after a failed start.
    /// </summary>
    public async Task StopAsync(TimeSpan drain)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        BeginDraining();
        var open = connections.Keys.ToArray();
        try { await Task.WhenAll(open.Select(admitted => admitted.WhenAnswered?.Invoke() ?? Task.CompletedTask)).WaitAsync(drain).ConfigureAwait(false); }
        catch (TimeoutException) { log("server stop clients_still_asking transport=network"); }
        catch (Exception) { }
        stopping.Cancel();
        foreach (var admitted in connections.Keys.ToArray()) admitted.Channel.Close();
        if (app is { } running)
        {
            using var limit = new CancellationTokenSource(StopWait);
            try { await running.StopAsync(limit.Token).ConfigureAwait(false); }
            catch (Exception) { }
            await running.DisposeAsync().ConfigureAwait(false);
        }
        devices.Revoked -= EndDevice;
        IAsyncDisposable[] owned;
        lock (owns) { ownsClosed = true; owned = [.. owns]; }
        foreach (var each in owned) await each.DisposeAsync().ConfigureAwait(false);
        await pairingCheck.DisposeAsync().ConfigureAwait(false);
        certificate.Dispose();
        flush?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.Zero).ConfigureAwait(false);

    // A new pairing code as the join link and the PC's line (protocol 27, SWITCHBOARD.md 10.4): the address the owner's PC reaches the server by (or the first name the
    // server was given), its tailnet name and Tailscale account when its door is signed in, and its fingerprint.
    private JoinLinkIssued JoinLinkFor(string? address)
    {
        address = string.IsNullOrWhiteSpace(address) ? options.HostNames.FirstOrDefault() is { Length: > 0 } named ? Port == NetworkEndpoint.DefaultPort ? named : $"{named}:{Port}" : null
            : address.Trim();
        var view = options.Tailnet?.Latest is { Running: true } running ? running : null;
        var code = pairing.Issue();
        var link = JoinLink.Build(view?.Name, address, Fingerprint, code, view is { Tagged: false } ? view.Owner : null);
        return new(link, address is null ? null : JoinLink.Line(address, Fingerprint, code), code, (int)PairingCodes.Lifetime.TotalSeconds, Fingerprint, address, view?.Name);
    }

    // One connection's devices.*: the host's list with this device marked. A device token is never an admin's (SWITCHBOARD.md's harvest rule): any device
    // may revoke (which ends this connection once the answer is written) or rename itself, and only the owner, the device paired first of those still paired
    // (DeviceRegistry.Owner), may revoke or rename another or open a code for a new one.
    private sealed class DeviceView(DeviceRegistry devices, string current, PairingCodes pairing, JoinRequests joins, Action<string> log, Func<string?, JoinLinkIssued> joinLink)
        : IPairedDevices
    {
        public Task<JoinLinkIssued> JoinLinkAsync(string? address, CancellationToken cancellationToken = default)
        {
            if (!devices.IsOwner(current))
            {
                log("server pairing refused reason=not_owner");
                throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden,
                    "Only the device paired first with this server can pair another device.");
            }
            // An address a person could not have typed is no address: the line and the link carry it to another device.
            if (address is { Length: > 0 } given && (given.Length > 300 || given.Any(c => char.IsWhiteSpace(c) || c is '/' or '#' or '?' or '@'))) address = null;
            return Task.FromResult(joinLink(address));
        }

        // Asking to join (protocol 21): the requests, and answering them, are the owner's as a new code is.
        public Task<IReadOnlyList<JoinRequest>> JoinRequestsAsync(CancellationToken cancellationToken = default)
        {
            RequireOwner("list");
            return Task.FromResult(joins.Waiting());
        }

        public Task<JoinApproved> ApproveJoinAsync(string id, CancellationToken cancellationToken = default)
        {
            RequireOwner("approve");
            return joins.ApproveAsync(id, "app");
        }

        public Task<JoinDenied> DenyJoinAsync(string id, CancellationToken cancellationToken = default)
        {
            RequireOwner("deny");
            return Task.FromResult(joins.Deny(id, "app"));
        }

        private void RequireOwner(string what)
        {
            if (devices.IsOwner(current)) return;
            log($"server join {what} refused reason=not_owner");
            throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden,
                "Only the device paired first with this server can answer requests to join.");
        }

        public Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(devices.List(current));

        public async Task<DeviceRevoked> RevokeAsync(string id, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, current, StringComparison.Ordinal) && !devices.IsOwner(current))
            {
                log("server device revoke refused reason=not_owner");
                throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden,
                    "Only the device paired first with this server can remove other devices.");
            }
            return new(await devices.RevokeAsync(id).ConfigureAwait(false));
        }

        // The same rule as a revoke: a device may rename itself, and only the owner another. No such device answers false with the name asked for, cleaned.
        public async Task<DeviceRenamed> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, current, StringComparison.Ordinal) && !devices.IsOwner(current))
            {
                log("server device rename refused reason=not_owner");
                throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden,
                    "Only the device paired first with this server can rename other devices.");
            }
            return await devices.RenameAsync(id, name).ConfigureAwait(false) is { } kept ? new(true, kept) : new(false, DeviceRegistry.CleanName(name));
        }

        public Task<PairingCodeIssued> PairNewAsync(CancellationToken cancellationToken = default)
        {
            if (!devices.IsOwner(current))
            {
                log("server pairing refused reason=not_owner");
                throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.Forbidden,
                    "Only the device paired first with this server can pair another device.");
            }
            // Printed in the server's log as every code is, and handed to the owner who asked.
            return Task.FromResult(new PairingCodeIssued(pairing.Issue(), (int)PairingCodes.Lifetime.TotalSeconds));
        }
    }
}
