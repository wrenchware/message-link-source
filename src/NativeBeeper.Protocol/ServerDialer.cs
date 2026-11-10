using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace NativeBeeper.Protocol;

/// <summary>
/// One way to reach a server: <see cref="Connect"/> opens a plain stream to its port, over which TLS then runs end to end. <see cref="Kind"/> is a log word:
/// <c>home</c> (straight to its address) or <c>tailnet</c> (through the tailnet node's door, <see cref="TailnetDial"/>).
/// </summary>
public sealed record ServerRoute(string Kind, Func<CancellationToken, Task<Stream>> Connect);

/// <summary>Why one route did not reach the server, as a log word: <c>timeout</c>, <c>refused</c>, <c>certificate</c>, <c>door_&lt;answer&gt;</c> or <c>unreachable</c>.</summary>
public sealed record RouteFailure(string Route, string Reason, Exception Error);

/// <summary>
/// No route reached the server with a certificate it trusts. <see cref="Exception.InnerException"/> is the failure that says the most (a tailnet door that
/// answered something other than "off" or "no such device" over a home address that simply did not answer); <see cref="Failures"/> has each route's.
/// </summary>
public sealed class ServerUnreachableException(IReadOnlyList<RouteFailure> failures, Exception? inner = null)
    : IOException("The server could not be reached.", inner)
{
    public IReadOnlyList<RouteFailure> Failures { get; } = failures;
}

/// <summary>
/// Opens the TLS connection to a server over whichever of its routes answers first (happy eyeballs): the route that won last time first, then the others in
/// the order given, each started when the one before it fails or after <see cref="HeadStart"/>, whichever comes first. The first that finishes TLS with a
/// certificate <c>trust</c> accepts wins, and every other attempt is cancelled and its connection closed. A wrong certificate on one route never stops
/// another from being tried: at home the address may answer with something else, and away it may be any device on another network.
/// </summary>
public sealed class ServerDialer
{
    private readonly IReadOnlyList<ServerRoute> routes;
    private readonly Action<string>? log;
    private readonly Dictionary<string, int> failed = new(StringComparer.Ordinal);
    private string? lastLogged;

    public ServerDialer(IReadOnlyList<ServerRoute> routes, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        this.routes = [.. routes];
        this.log = log;
    }

    public IReadOnlyList<ServerRoute> Routes => routes;

    /// <summary>How long one route has to itself before the next is started alongside it.</summary>
    public TimeSpan HeadStart { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The route tried first: the one that won last (set by each success, or by its owner).</summary>
    public string? Preferred { get; set; }

    /// <summary>The route of the most recent connection that opened.</summary>
    public string? LastRoute { get; private set; }

    /// <summary>
    /// The connection, TLS done with <paramref name="host"/> as the name, and the route that carried it. Throws <see cref="CertificateMismatchException"/> when
    /// every route answered with a certificate <paramref name="trust"/> refused, <see cref="ServerUnreachableException"/> (an <see cref="IOException"/>) when
    /// none opened otherwise or <paramref name="timeout"/> ran out first, and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> was cancelled.
    /// </summary>
    public async Task<(Stream Stream, string Route)> OpenAsync(string host, Func<X509Certificate?, bool> trust, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        var order = Order();
        if (order.Count == 0) throw new ServerUnreachableException([]);
        var clock = Stopwatch.StartNew();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } bound) race.CancelAfter(bound);
        var running = new List<Task<Attempt>>();
        var failures = new List<RouteFailure>();
        var next = 0;
        Task? headStart = null;
        void StartNext()
        {
            running.Add(AttemptAsync(order[next++], host, trust, race.Token));
            headStart = next < order.Count ? Task.Delay(HeadStart, race.Token) : null;
        }

        StartNext();
        try
        {
            while (running.Count > 0)
            {
                var done = headStart is null ? await Task.WhenAny(running).ConfigureAwait(false) : await Task.WhenAny([.. running, headStart]).ConfigureAwait(false);
                if (done == headStart)
                {
                    if (headStart.IsCompletedSuccessfully) StartNext();
                    else headStart = null;
                    continue;
                }
                var finished = (Task<Attempt>)done;
                running.Remove(finished);
                var attempt = await finished.ConfigureAwait(false);
                if (attempt.Stream is { } stream)
                {
                    Preferred = attempt.Route.Kind;
                    LastRoute = attempt.Route.Kind;
                    if (!string.Equals(lastLogged, attempt.Route.Kind, StringComparison.Ordinal))
                    {
                        lastLogged = attempt.Route.Kind;
                        log?.Invoke($"protocol route outcome=ok route={attempt.Route.Kind} ms={clock.ElapsedMilliseconds}");
                    }
                    return (stream, attempt.Route.Kind);
                }
                // Cancelled by the caller: nothing failed.
                if (cancellationToken.IsCancellationRequested) continue;
                var reason = Reason(attempt.Error!);
                failures.Add(new RouteFailure(attempt.Route.Kind, reason, attempt.Error!));
                Failed(attempt.Route.Kind, reason);
                if (next < order.Count && !race.IsCancellationRequested) StartNext();
            }
        }
        finally
        {
            race.Cancel();
            // A loser that finished TLS after the winner (or after the caller gave up) is closed: only one connection is ever handed out.
            foreach (var loser in running)
                _ = loser.ContinueWith(static task => { if (task.IsCompletedSuccessfully) task.Result.Stream?.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (failures.Count > 0 && failures.All(failure => failure.Error is CertificateMismatchException)) throw new CertificateMismatchException();
        throw new ServerUnreachableException(failures, Telling(failures));
    }

    /// <summary>
    /// Whether the server with the certificate <paramref name="pin"/> answers on any of this dialer's routes: the TLS handshake only, closed straight after,
    /// so nothing (no request, no token) is written to whatever answers. False for another certificate or no answer.
    /// </summary>
    public async Task<bool> ProbeAsync(string host, byte[] pin, CancellationToken cancellationToken)
    {
        try
        {
            var (stream, _) = await OpenAsync(host, certificate => NetworkEndpoint.IsPinned(certificate, pin), cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (IOException) { return false; }
        catch (System.Security.Authentication.AuthenticationException) { return false; }
    }

    /// <summary>
    /// Whether the server with the certificate <paramref name="pin"/> answers on <paramref name="route"/>: the TLS handshake only, closed straight after,
    /// so nothing (no request, no token) is written to whatever answers. False for another certificate or no answer; how the server is found on the tailnet.
    /// </summary>
    public static async Task<bool> ProbeAsync(ServerRoute route, string host, byte[] pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        var attempt = await AttemptAsync(route, host, certificate => NetworkEndpoint.IsPinned(certificate, pin), cancellationToken).ConfigureAwait(false);
        if (attempt.Stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>A failure as its log word.</summary>
    public static string Reason(Exception error) => error switch
    {
        CertificateMismatchException => "certificate",
        TailnetDoorException door => "door_" + TailnetDial.Word(door.Answer),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "refused",
        SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException or OperationCanceledException => "timeout",
        IOException { InnerException: { } inner } when inner is SocketException or TimeoutException or OperationCanceledException => Reason(inner),
        _ => "unreachable",
    };

    private List<ServerRoute> Order()
    {
        var preferred = Preferred;
        return [.. routes.Where(route => route.Kind == preferred), .. routes.Where(route => route.Kind != preferred)];
    }

    // What says the most about why the server could not be reached: a door that found the node signed out, the device busy or another device in its place
    // tells the person what to do, where a home address that did not answer away from home says only that they are away.
    private static Exception? Telling(List<RouteFailure> failures) =>
        failures.OrderBy(failure => failure.Error switch
        {
            TailnetDoorException { Answer: not (TailnetAnswer.Off or TailnetAnswer.NoDevice) } => 0,
            CertificateMismatchException => 3,
            TailnetDoorException => 2,
            _ => 1,
        }).Select(failure => failure.Error).FirstOrDefault();

    // The first of each route's reason, then one in a hundred with the count, as NetworkEndpoint's refusals are.
    private void Failed(string route, string reason)
    {
        int count;
        lock (failed)
        {
            failed.TryGetValue(route + " " + reason, out count);
            failed[route + " " + reason] = ++count;
        }
        if (count == 1 || count % 100 == 0) log?.Invoke($"protocol route failed route={route} reason={reason}{(count == 1 ? "" : $" count={count}")}");
    }

    private sealed record Attempt(ServerRoute Route, Stream? Stream, Exception? Error);

    // One route: its connection, then TLS over it. Never throws, so a loser that fails after the race is decided leaves no unobserved exception.
    private static async Task<Attempt> AttemptAsync(ServerRoute route, string host, Func<X509Certificate?, bool> trust, CancellationToken cancellationToken)
    {
        Stream? raw = null;
        var mismatch = 0;
        try
        {
            raw = await route.Connect(cancellationToken).ConfigureAwait(false);
            var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
            raw = ssl;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host.Trim('[', ']'),
                // The pin is the whole of the trust, as on the direct connection: chain and name errors are expected for a self-signed certificate.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (trust(certificate)) return true;
                    Volatile.Write(ref mismatch, 1);
                    return false;
                },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
            return new Attempt(route, ssl, null);
        }
        catch (Exception ex)
        {
            if (raw is not null) await raw.DisposeAsync().ConfigureAwait(false);
            return new Attempt(route, null, Volatile.Read(ref mismatch) == 1 ? new CertificateMismatchException() : ex);
        }
    }
}
