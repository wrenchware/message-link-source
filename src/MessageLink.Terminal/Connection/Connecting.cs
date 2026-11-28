using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageLink.App.Services;
using MessageLink.Core.Services;
using MessageLink.Protocol;

namespace MessageLink.Terminal.Connection;

/// <summary>
/// <see cref="ServerConnector"/>'s work, in order: the server <c>mlink join</c> saved; else, on Windows, this PC's own server over the pipe (started when it runs
/// none), or the server elsewhere this PC moved to, which this terminal may ask to join; else, off Windows, a server on 127.0.0.1 (mlink inside the server's
/// own container), which it may ask to join too. mlink keeps no offline copy, so a server it cannot connect to is said and nothing opens. Once connected, a
/// later loss is the <see cref="ProtocolClient"/>'s own reconnecting.
/// </summary>
internal static class Connecting
{
    public const string LocalId = "local";
    public const string LocalName = "this PC's server";

    public static async Task<ServerLink?> ConnectAsync(MlinkContext context, ServerSearch search, CancellationToken cancellationToken)
    {
        var store = new JoinedServerStore(context.Folder, context.Protector);
        if (store.Exists) return await SavedAsync(context, store, search, cancellationToken).ConfigureAwait(false);
        return search.Windows
            ? await ThisPcAsync(context, search, cancellationToken).ConfigureAwait(false)
            : await LoopbackAsync(context, search, cancellationToken).ConfigureAwait(false);
    }

    // ---- The server mlink join saved ----

    private static async Task<ServerLink?> SavedAsync(MlinkContext context, JoinedServerStore store, ServerSearch search, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync().ConfigureAwait(false) is not { } saved)
        {
            context.Out.WriteLine("This terminal's saved server can't be read. Join it again with mlink join, or forget it with mlink leave.");
            context.Log("mlink connect via=saved outcome=unreadable");
            return null;
        }
        // The machine's own network: no Tailscale node of mlink's own, so a Tailscale name works only through a Tailscale app on this machine.
        var attempts = new Attempts();
        var connector = attempts.Watch(NetworkEndpoint.Connector(saved.Socket, saved.Fingerprint, _ => store.Token.ReadAsync(), context.Log));
        var state = new HostStateCache();
        var client = new ProtocolClient(connector, context.Version, MlinkContext.ClientWord, log: context.Log, cache: state);
        var clock = Stopwatch.StartNew();
        if (await FirstAsync(client, search.ConnectWait, cancellationToken).ConfigureAwait(false))
        {
            context.Log($"mlink connect via=saved outcome=connected ms={clock.ElapsedMilliseconds} server={saved.Tag}");
            return new ServerLink(client, state, saved.ServerId, saved.Name, IsLoopback(saved.Socket.Host));
        }
        var (sentence, word) = client.Refused ? (VersionSentence($"Message Link Server at {saved.Name}"), "version") : attempts.Last switch
        {
            ServerRestoredException => ($"{saved.Name} was restored from an earlier backup and doesn't know this terminal. Join it again: mlink join {saved.Name}", "restored"),
            CertificateMismatchException => ($"The server at {saved.Name} has a different certificate than the one this terminal trusts. If the server was set up again, join it again: mlink join {saved.Name}", "certificate"),
            UnauthorizedAccessException => ($"This terminal is no longer allowed on {saved.Name}. Join it again: mlink join {saved.Name}", "refused"),
            DeviceNotPairedException => ($"This terminal's key for {saved.Name} can't be read. Join it again: mlink join {saved.Name}", "unpaired"),
            _ => ($"Message Link Server at {saved.Name} can't be reached. Check that it's running, and that this computer can reach it.", "unreachable"),
        };
        client.Dispose();
        if (cancellationToken.IsCancellationRequested) return null;
        context.Out.WriteLine(sentence);
        context.Log($"mlink connect via=saved outcome={word} ms={clock.ElapsedMilliseconds} server={saved.Tag}");
        return null;
    }

    // ---- This PC (Windows) ----

    private static async Task<ServerLink?> ThisPcAsync(MlinkContext context, ServerSearch search, CancellationToken cancellationToken)
    {
        var root = search.DataRoot;
        if (root is null || !Directory.Exists(root))
        {
            context.Out.WriteLine("Message Link isn't set up on this PC.");
            context.Out.WriteLine(Joining.HowToJoin);
            context.Log("mlink connect via=pipe outcome=no_data");
            return null;
        }
        var mark = MoveMarker.Read(root);
        var running = search.LockHeld();
        if (MoveMarker.RunsOwnServer(mark) || running)
            return await PipeAsync(context, search, root, mayStart: MoveMarker.RunsOwnServer(mark), running, cancellationToken).ConfigureAwait(false);
        switch (mark.State)
        {
            case MoveState.Moved:
                return await MovedAsync(context, search, root, cancellationToken).ConfigureAwait(false);
            case MoveState.Moving when !MoveMarker.IsLocalServer(mark):
            case MoveState.Unreadable:
                context.Out.WriteLine("Message Link on this PC can't tell which server it uses. Open Message Link to see what to do.");
                context.Log($"mlink connect via=pipe outcome=moving state={mark.State.ToString().ToLowerInvariant()}");
                return null;
            default:
                context.Out.WriteLine("There's no Message Link Server on this PC.");
                context.Out.WriteLine(Joining.HowToJoin);
                context.Log("mlink connect via=pipe outcome=no_server");
                return null;
        }
    }

    private static async Task<ServerLink?> PipeAsync(MlinkContext context, ServerSearch search, string root, bool mayStart, bool running, CancellationToken cancellationToken)
    {
        // The token the server makes for its clients, sealed for this user: the pipe already admits only this user, and the token is what it checks.
        var token = new ClientToken(Path.Combine(root, ServerSearch.ServerFolder), search.AppProtector ?? context.Protector);
        var attempts = new Attempts();
        var connector = attempts.Watch(PipeEndpoint.Connector(PipeEndpoint.PipeNameForCurrentUser(search.PipeSuffix), token, context.Log, search.PipeOpenWait));
        var state = new HostStateCache();
        // A try is a pipe open, nothing more: a server that is starting is tried every tenth of a second.
        var client = new ProtocolClient(connector, context.Version, MlinkContext.ClientWord, log: context.Log, cache: state) { CallRetryInterval = TimeSpan.FromMilliseconds(100) };
        var clock = Stopwatch.StartNew();
        if (await FirstAsync(client, search.ConnectWait, cancellationToken).ConfigureAwait(false)) return Connected("attached");
        if (cancellationToken.IsCancellationRequested) return Gone(null, null);
        if (client.Refused) return Gone(VersionSentence("this PC's Message Link Server"), "version");
        if (attempts.Last is UnauthorizedAccessException) return Gone(TokenSentence, "refused");

        ServerStart? start = null;
        if (!running)
        {
            if (!mayStart || search.StartServer is null) return Gone("Message Link Server isn't running. Open Message Link, then try again.", "not_running");
            start = search.StartServer();
            context.Log($"mlink server start outcome={(start.Started ? "started" : "failed")}" + (start.Error is null ? "" : $" reason={start.Error}"));
            if (!start.Started) return Gone("Message Link Server isn't running, and mlink couldn't start it. Open Message Link, then try again.", "not_started");
            context.Out.WriteLine("Starting Message Link Server.");
        }
        var waited = Stopwatch.StartNew();
        while (!client.IsConnected && waited.Elapsed < search.StartWait && !cancellationToken.IsCancellationRequested)
        {
            if (client.Refused) return Gone(VersionSentence("this PC's Message Link Server"), "version");
            if (attempts.Last is UnauthorizedAccessException) return Gone(TokenSentence, "refused");
            if (start?.Exited is { IsCompleted: true } exited)
            {
                // Refused because another start of the same server got there first (the app's): that one is reached instead.
                if (exited.Result == 3 && search.LockHeld()) start = start with { Exited = null };
                else return Gone("Message Link Server stopped as it started. Open Message Link, then try again.", "exited");
            }
            client.Hurry();
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        if (client.IsConnected) return Connected(start is null ? "attached_late" : "started");
        if (cancellationToken.IsCancellationRequested) return Gone(null, null);
        return Gone("This PC's Message Link Server didn't answer. Try again in a moment.", "timeout");

        ServerLink Connected(string word)
        {
            context.Log($"mlink connect via=pipe outcome={word} ms={clock.ElapsedMilliseconds}");
            return new ServerLink(client, state, LocalId, LocalName, true);
        }

        ServerLink? Gone(string? sentence, string? word)
        {
            client.Dispose();
            if (sentence is null) return null;
            context.Out.WriteLine(sentence);
            context.Log($"mlink connect via=pipe outcome={word} ms={clock.ElapsedMilliseconds}");
            return null;
        }
    }

    private const string TokenSentence = "This PC's Message Link Server didn't accept mlink. Open Message Link, then try again.";

    // This PC moved its choices to a server elsewhere: its address is read from the app's saved server (never its key: mlink is a device of its own) and the
    // person is offered to ask that server to let this terminal in.
    private static async Task<ServerLink?> MovedAsync(MlinkContext context, ServerSearch search, string root, CancellationToken cancellationToken)
    {
        var socket = await RemoteAddressAsync(root, search.AppProtector ?? context.Protector).ConfigureAwait(false);
        if (socket is null)
        {
            context.Out.WriteLine("This PC uses a Message Link Server elsewhere.");
            context.Out.WriteLine(Joining.HowToJoin);
            context.Log("mlink connect via=pipe outcome=moved address=unknown");
            return null;
        }
        var name = JoinedServer.NameOf(socket);
        context.Out.WriteLine($"This PC uses Message Link Server at {name}.");
        context.Log("mlink connect via=pipe outcome=moved");
        return await OfferAsync(context, search, socket, $"Ask {name} to let this terminal in now? (y/n) ",
            $"To join it later, run mlink join {name}, or use the link from the server's page: Add a device, then PC.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The address in the app's <c>remote.settings</c> (<c>v1\nsocket\npin</c>, or <c>v2</c> with three more lines), or null.</summary>
    internal static async Task<Uri?> RemoteAddressAsync(string root, IStateProtector protector)
    {
        try
        {
            var path = Path.Combine(root, ServerSearch.ServerFolder, "remote.settings");
            if (!File.Exists(path)) return null;
            var parts = Encoding.UTF8.GetString(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false)).Split('\n');
            return parts.Length >= 3 && parts[0] is "v1" or "v2" ? NetworkEndpoint.SocketUri(parts[1]) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    // ---- Inside the server's own container (Linux) ----

    private static async Task<ServerLink?> LoopbackAsync(MlinkContext context, ServerSearch search, CancellationToken cancellationToken)
    {
        if (!await AnswersAsync(search.LoopbackPort, cancellationToken).ConfigureAwait(false))
        {
            context.Out.WriteLine("There's no Message Link Server on this machine.");
            context.Out.WriteLine(Joining.HowToJoin);
            context.Log("mlink connect via=loopback outcome=no_server");
            return null;
        }
        context.Out.WriteLine("Message Link Server is running on this machine.");
        context.Log("mlink connect via=loopback outcome=found");
        var socket = new UriBuilder("wss", IPAddress.Loopback.ToString(), search.LoopbackPort, NetworkEndpoint.Target).Uri;
        return await OfferAsync(context, search, socket, "Ask it to let this terminal in now? (y/n) ",
            $"To join it later, run mlink join {JoinedServer.NameOf(socket)}.", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> AnswersAsync(int port, CancellationToken cancellationToken)
    {
        using var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await connection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception failure) when (failure is SocketException or OperationCanceledException) { return false; }
    }

    // Asks whether to ask the server to let this terminal in; once it has, connects to it as the saved server it now is.
    private static async Task<ServerLink?> OfferAsync(MlinkContext context, ServerSearch search, Uri socket, string question, string later, CancellationToken cancellationToken)
    {
        var answer = context.Ask(question)?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            context.Out.WriteLine(later);
            context.Log("mlink join offer outcome=declined");
            return null;
        }
        if (await Joining.AskAsync(context, socket, search.Join, thenOpening: true, cancellationToken).ConfigureAwait(false) != Joining.Result.Joined) return null;
        return await SavedAsync(context, new JoinedServerStore(context.Folder, context.Protector), search, cancellationToken).ConfigureAwait(false);
    }

    // ---- Shared ----

    // The first connection, bounded. False when it was not made in time (the client's own attempt goes on, so a caller disposes it).
    private static async Task<bool> FirstAsync(ProtocolClient client, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(wait);
        try
        {
            await client.ConnectAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string VersionSentence(string who) => $"This version of Message Link can't talk to {who}. Update Message Link or the server, then try again.";

    private static bool IsLoopback(string host) => IPAddress.TryParse(host.Trim('[', ']'), out var address) ? IPAddress.IsLoopback(address)
        : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    // Why the last attempt failed, which ProtocolClient does not hand on (it says only that the connection is not there).
    private sealed class Attempts
    {
        private volatile Exception? last;
        public Exception? Last => last;

        public Func<CancellationToken, Task<IFrameChannel>> Watch(Func<CancellationToken, Task<IFrameChannel>> inner) => async cancellationToken =>
        {
            try
            {
                var channel = await inner(cancellationToken).ConfigureAwait(false);
                last = null;
                return channel;
            }
            catch (Exception failure)
            {
                last = failure;
                throw;
            }
        };
    }
}
