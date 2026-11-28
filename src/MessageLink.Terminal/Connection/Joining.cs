using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using MessageLink.App.Services;
using MessageLink.Core.Backend;
using MessageLink.Protocol;

namespace MessageLink.Terminal.Connection;

/// <summary>
/// Joining a server: with the link from its page (a pairing code, flow A: <see cref="NetworkEndpoint.ClaimAsync"/>), or with its address alone (asking to join,
/// flow B: <see cref="NetworkEndpoint.JoinAsync"/>, which the owner approves on the server's page and this terminal's person finishes with the code shown there).
/// A server saved before is replaced only once the new one has let this terminal in. Nothing here logs an address, a code, a token or a name.
/// </summary>
internal static class Joining
{
    internal enum Result { Joined, NotJoined, NotUnderstood }

    public const string HowToJoin = "To use a server elsewhere, run mlink join <link>. Copy the link from the server's page: Add a device, then PC.";

    public static async Task<Result> RunAsync(MlinkContext context, string target, JoinOptions options, CancellationToken cancellationToken)
    {
        var text = target.Trim();
        if (JoinLink.Parse(text) is { } link) return await WithCodeAsync(context, link, options, cancellationToken).ConfigureAwait(false);
        if (SocketFor(text) is { } socket) return await AskAsync(context, socket, options, thenOpening: false, cancellationToken).ConfigureAwait(false);
        context.Out.WriteLine("That isn't a join link or a server address. Copy the link from the server's page: Add a device, then PC.");
        context.Log("mlink join outcome=not_understood");
        return Result.NotUnderstood;
    }

    /// <summary>The socket for an address as a person types it: <c>host</c>, <c>host:port</c> (8787 when none) or <c>wss://host:port</c>; null when it is not one.</summary>
    public static Uri? SocketFor(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var text = typed.Trim().TrimEnd('/');
        if (!text.Contains("://", StringComparison.Ordinal)) text = "wss://" + text;
        return NetworkEndpoint.SocketUri(text);
    }

    // Flow A. The link's home address first, then its Tailscale name (reached through a Tailscale app on this machine, if there is one): a connection that
    // never reached the server's upgrade spent nothing, so the code is still good for the next. One that did spent the code, so anything that fails after it is
    // said at once, for what it is: the next route would only be turned away for a code already used.
    private static async Task<Result> WithCodeAsync(MlinkContext context, JoinLink.Parsed link, JoinOptions options, CancellationToken cancellationToken)
    {
        var pin = Convert.FromHexString(link.Fingerprint);
        var tag = JoinedServer.TagOf(pin);
        var sockets = new[] { link.Address, link.TailnetAddress }.OfType<string>().Select(SocketFor).OfType<Uri>().DistinctBy(socket => socket.AbsoluteUri).ToList();
        if (sockets.Count == 0)
        {
            context.Out.WriteLine("That link has no address this terminal can use. Copy the link from the server's page again.");
            context.Log("mlink join flow=code outcome=no_address");
            return Result.NotJoined;
        }
        var store = new JoinedServerStore(context.Folder, context.Protector);
        var previous = await store.ReadAsync().ConfigureAwait(false);
        Uri? chosen = null;
        byte[]? token = null;
        foreach (var socket in sockets)
        {
            try
            {
                (_, token) = await NetworkEndpoint.ClaimAsync(socket, pin, link.Code, options.DeviceName, context.Version, cancellationToken, options.ConnectTimeout).ConfigureAwait(false);
                chosen = socket;
                break;
            }
            catch (PairingRefusedException) { return Said("The server didn't accept that pairing code. Make a new one on the server's page and try again.", "refused"); }
            catch (PairingPacedException) { return Said("The server asked to wait before another try. Try again in a minute.", "paced"); }
            catch (CertificateMismatchException) { return Said("That isn't the server with this fingerprint. Copy the link from the server's page again.", "certificate"); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Said("Stopped.", "canceled"); }
            catch (Exception failure) when (NeverUpgraded(failure)) { }
            catch (BackendException refused) when (refused is { Failure: BackendFailure.InvalidResponse, Message: ProtocolClient.VersionSentence })
            {
                return Said($"This version of Message Link can't talk to {JoinedServer.NameOf(socket)}. Update Message Link, then make a new link on the server's page and join again.",
                    "version");
            }
            catch (Exception failure)
            {
                context.Log($"mlink join flow=code failed type={failure.GetType().Name}");
                return Said($"Message Link reached {JoinedServer.NameOf(socket)}, but joining didn't finish. Make a new link on the server's page, then try again.", "interrupted");
            }
        }
        if (chosen is null || token is null)
            return Said($"Message Link couldn't reach {JoinedServer.NameOf(sockets[0])}. Check the address, and that the server is running.", "unreachable");
        var joined = new JoinedServer(chosen, pin);
        var snapshot = await store.SnapshotAsync().ConfigureAwait(false);
        try { await store.StoreAsync(joined, token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            await PutBackAsync(store, snapshot).ConfigureAwait(false);
            return Said("Joined, but this terminal couldn't save the server's details. Try again.", "not_saved");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(token); }
        if (previous is not null && previous.ServerId != joined.ServerId) ForgetReplica(context, previous.ServerId);
        context.Out.WriteLine(Joined(joined, previous, thenOpening: false));
        context.Log($"mlink join flow=code outcome=joined server={tag}{(sockets.Count > 1 && chosen != sockets[0] ? " route=second" : "")}");
        return Result.Joined;

        Result Said(string sentence, string word)
        {
            context.Out.WriteLine(sentence);
            context.Log($"mlink join flow=code outcome={word} server={tag}");
            return Result.NotJoined;
        }
    }

    /// <summary>The server's page at its requests to join, for a server reached at <paramref name="socket"/>.</summary>
    public static Uri PageFor(Uri socket) => new UriBuilder("https", socket.Host, socket.Port) { Query = "section=join" }.Uri;

    /// <summary>
    /// Flow B: asks the server at <paramref name="socket"/> to let this terminal in, shows the code its owner matches on the server's page, waits (Ctrl+C stops),
    /// then asks for the finish code the page shows, up to <see cref="NetworkEndpoint.FinishTries"/> times. <paramref name="thenOpening"/>: the console opens next, so
    /// the last sentence does not say to run mlink.
    /// </summary>
    public static async Task<Result> AskAsync(MlinkContext context, Uri socket, JoinOptions options, bool thenOpening, CancellationToken cancellationToken)
    {
        var name = JoinedServer.NameOf(socket);
        var store = new JoinedServerStore(context.Folder, context.Protector);
        var previous = await store.ReadAsync().ConfigureAwait(false);
        JoinedServerStore.Snapshot? snapshot = null;
        bool kept = false, notSaved = false, gaveUp = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Interrupted(object? sender, ConsoleCancelEventArgs press)
        {
            press.Cancel = true;
            stop.Cancel();
        }
        context.Out.WriteLine($"Asking {name} to let this terminal in.");
        Console.CancelKeyPress += Interrupted;
        JoinedDevice joined;
        try
        {
            joined = await NetworkEndpoint.JoinAsync(socket, options.DeviceName, context.Version, code =>
                {
                    context.Out.WriteLine($"On the server's page, approve \"{options.DeviceName}\". Check that it shows the code {code}.");
                    // Its address, which terminals make a link (Ctrl+click in Windows Terminal): the page opens at its requests to join.
                    context.Out.WriteLine($"The server's page: {PageFor(socket)}");
                    context.Out.WriteLine("Waiting for approval. Press Ctrl+C to stop.");
                }, stop.Token, options.ConnectTimeout, keep: async device =>
                {
                    snapshot = await store.SnapshotAsync().ConfigureAwait(false);
                    try { await store.StoreAsync(new JoinedServer(socket, device.Pin), device.Token).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    {
                        notSaved = true;
                        throw;
                    }
                    kept = true;
                }, finishCode: (again, _) =>
                {
                    var typed = context.Ask(again ? "That code didn't match. Enter the finish code again: " : "Approved. Enter the finish code from the server's page: ");
                    if (typed is null) gaveUp = true;
                    return Task.FromResult(typed);
                }).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Kept, and then the server was not told (or the save itself failed half way): what was saved before comes back, since this server will not have it.
            if ((kept || notSaved) && snapshot is not null) await PutBackAsync(store, snapshot).ConfigureAwait(false);
            var (sentence, word) = stop.IsCancellationRequested || gaveUp ? ("Stopped asking.", "canceled") : notSaved
                ? ("Joined, but this terminal couldn't save the server's details. Try again.", "not_saved") : Refusal(failure, name);
            context.Out.WriteLine(sentence);
            context.Log($"mlink join flow=ask outcome={word}");
            return Result.NotJoined;
        }
        finally { Console.CancelKeyPress -= Interrupted; }
        var server = new JoinedServer(socket, joined.Pin);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(joined.Token);
        if (previous is not null && previous.ServerId != server.ServerId) ForgetReplica(context, previous.ServerId);
        context.Out.WriteLine(Joined(server, previous, thenOpening));
        context.Log($"mlink join flow=ask outcome=joined server={server.Tag}");
        return Result.Joined;
    }

    private static (string Sentence, string Word) Refusal(Exception failure, string name) => failure switch
    {
        JoinDeclinedException => ("The server's owner turned the request away.", "declined"),
        JoinExpiredException => ("Nobody approved the request in time. Ask again when someone can approve it.", "expired"),
        JoinNotOfferedException { NoApprover: true } =>
            ("Nobody can approve a request on this server yet. Set a password for its page, or use the join link from the server's page instead.", "no_approver"),
        JoinNotOfferedException { NotHome: true } =>
            ("The server takes requests to join only from its home network. Ask from there, or use the join link from the server's page instead.", "not_home"),
        JoinNotOfferedException => ("This server can't take requests to join. Update the server, or use the join link from its page instead.", "not_offered"),
        JoinUntrustedException => ("The server answered in a way Message Link didn't expect, so it stopped and saved nothing. Check the address, then ask again.", "untrusted"),
        JoinFinishMismatchException =>
            ("The finish code didn't match the server's, so this terminal kept nothing. Ask again, and enter the code the server's page shows after approving.", "finish_mismatch"),
        PairingPacedException => ("Too many devices are asking to join right now, or this terminal just asked. Try again in a minute.", "busy"),
        InvalidOperationException { Message: JoinWords.Failed } =>
            ("The server approved this terminal but couldn't add it. Ask again, or remove a device the server no longer needs.", "failed"),
        _ => ($"Message Link couldn't reach {name}. Check the address, and that the server is running.", "unreachable"),
    };

    private static string Joined(JoinedServer server, JoinedServer? previous, bool thenOpening)
    {
        var replaced = previous is not null && previous.ServerId != server.ServerId ? $", in place of {previous.Name}" : "";
        return $"Joined {server.Name}{replaced}." + (thenOpening ? "" : " Run mlink to open your conversations.");
    }

    // What failed before the server's upgrade answered: the connection, TLS or the time to open it (NetworkEndpoint's own IOException for a socket that took too
    // long). The code was never presented to a server that took it. Everything after the upgrade fails as the protocol client's own exceptions, a reply that
    // could not be read, or its time running out.
    private static bool NeverUpgraded(Exception failure) => failure is WebSocketException or HttpRequestException or SocketException or AuthenticationException
        || failure.GetType() == typeof(IOException);

    /// <summary>
    /// Removes what this terminal kept of a server it no longer uses (its copy of the shared rows: names, saved messages, settings): the server's folder under
    /// mlink's own replica folder, and nothing else. Only a joined server's id (its pin, hex) names one; a failure is logged by its type.
    /// </summary>
    internal static void ForgetReplica(MlinkContext context, string serverId)
    {
        if (serverId.Length != 64 || !serverId.All(char.IsAsciiHexDigitLower)) return;
        var folder = Path.Combine(context.Folder, ReplicatedStateStore.FolderName, serverId);
        try
        {
            if (!Directory.Exists(folder)) return;
            Directory.Delete(folder, recursive: true);
            context.Log($"mlink replica forgot server={JoinedServer.TagOf(Convert.FromHexString(serverId))}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            context.Log($"mlink replica forget failed type={failure.GetType().Name}");
        }
    }

    private static async Task PutBackAsync(JoinedServerStore store, JoinedServerStore.Snapshot snapshot)
    {
        try { await store.RestoreAsync(snapshot).ConfigureAwait(false); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
