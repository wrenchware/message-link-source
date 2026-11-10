using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.Protocol;

/// <summary>
/// Pairing a device with a host on the network (SWITCHBOARD.md stage 4). A device has no token until it pairs: it opens the socket with the one-time code
/// the host printed in <see cref="Header"/> instead of a token, says hello, and makes the one call such a connection may make, <see cref="ClaimMethod"/>,
/// which answers the device's own token; then the host closes the connection. The method is not in <see cref="ProtocolMethods.Catalog"/>, because no
/// dispatcher serves it: only the network host's door does, to a connection that has nothing else.
/// </summary>
public static class NetworkPairing
{
    public const string ClaimMethod = "pair.claim";
    /// <summary>The upgrade header that carries the pairing code, in place of <c>Authorization</c>.</summary>
    public const string Header = "X-Messages-Pair";
    /// <summary>
    /// The header a refusal of a token names its reason in, when it has one a device should say (stage 10, protocol 26): <see cref="RefusalRestored"/>, the server was restored
    /// from a backup made before this device joined (or the restore forgot it), so it asks to join again. Any server version may send it; a device that does not know it reads
    /// a refused token as before.
    /// </summary>
    public const string RefusalHeader = "X-Messages-Refusal";
    public const string RefusalRestored = "restored";
    /// <summary>The longest device name a host keeps; a longer one is cut.</summary>
    public const int MaxDeviceName = 64;

    /// <summary>
    /// The upgrade header of a join connection (protocol <see cref="ProtocolVersion.JoinRequests"/>), whose value is <c>1</c>: a device with nothing but the server's address
    /// asks to join on it, and waits there for the owner's answer. Such a connection may say hello and make <see cref="JoinStartMethod"/>, <see cref="JoinNonceMethod"/>,
    /// <see cref="JoinWaitMethod"/> and <see cref="JoinAckMethod"/>, in that order, and nothing else; like a pairing connection, no dispatcher serves it.
    /// </summary>
    public const string JoinHeader = "X-Messages-Join";
    /// <summary>Asks to join as a device name: answers the request's id and the server's commitment to its nonce (SHA-256 of it, hex).</summary>
    public const string JoinStartMethod = "pair.join";
    /// <summary>The asking device's nonce: answers the server's nonce, which must hash to the commitment. Both ends then work out the request's code (<see cref="JoinMatch"/>).</summary>
    public const string JoinNonceMethod = "pair.joinNonce";
    /// <summary>
    /// Waits for the owner: once approved, answers the device's token (<see cref="PairClaimed"/>) to a connection that negotiated less than
    /// <see cref="ProtocolVersion.JoinFinish"/>, and to one of it or later the proof of the finish code (<see cref="PairJoinApproved"/>), which the device checks against
    /// the code its person types before it asks for its token with <see cref="JoinFinishMethod"/>. Answers an <c>account_failed</c> error with <see cref="JoinWords"/> otherwise.
    /// </summary>
    public const string JoinWaitMethod = "pair.joinWait";
    /// <summary>
    /// Protocol <see cref="ProtocolVersion.JoinFinish"/>: the device checked the finish code its person typed against the server's proof, and asks for its token
    /// (<see cref="PairClaimed"/>). Only then is the device added on the server. A device that never asks (its person could not type the code, or it gave up) is never added.
    /// </summary>
    public const string JoinFinishMethod = "pair.joinFinish";
    /// <summary>
    /// Says the device kept its token: only then does the server count it as joined. A device that never says so (it went, or could not save the token) is removed again,
    /// so a request that ended badly never leaves a paired device nobody holds the key of.
    /// </summary>
    public const string JoinAckMethod = "pair.joinAck";
}

public sealed record PairJoinParams(string DeviceName);
/// <param name="Commitment">SHA-256 of the server's nonce, hex: fixed before the asking device's nonce is known, so neither end can steer the code.</param>
public sealed record PairJoinStarted(string RequestId, string Commitment);
public sealed record PairJoinNonceParams(string RequestId, string ClientNonce);
/// <param name="ExpiresSeconds">How long the request holds from now.</param>
public sealed record PairJoinNonce(string ServerNonce, int ExpiresSeconds);
public sealed record PairJoinWaitParams(string RequestId);
/// <param name="Proof">The finish code's proof (<see cref="JoinMatch.FinishProof"/>), hex: what the code the owner's screen shows gives over this request's own transcript.</param>
/// <param name="FinishSeconds">How long the server waits for <see cref="NetworkPairing.JoinFinishMethod"/> from now.</param>
public sealed record PairJoinApproved(string Proof, int FinishSeconds);
public sealed record PairJoinFinishParams(string RequestId);
public sealed record PairJoinAckParams(string RequestId);

/// <summary>The fixed words a join connection ends a request with (as <c>account_failed</c>).</summary>
public static class JoinWords
{
    /// <summary>The owner turned it away.</summary>
    public const string Declined = "join_declined";
    /// <summary>Nobody answered in time.</summary>
    public const string Expired = "join_expired";
    /// <summary>The server could not pair the device it let in (its device list full, or its file not saved).</summary>
    public const string Failed = "join_failed";
    public static IReadOnlyList<string> All { get; } = [Declined, Expired, Failed];
}

/// <summary>
/// The short code an asking device shows and the owner matches before approving (protocol <see cref="ProtocolVersion.JoinRequests"/>), in place of comparing the server's
/// long fingerprint by hand. Both ends work it out from the certificate the device saw (the server from its own), both nonces and the request's id: a party in between
/// shows the device its own certificate, which gives a different code, and the server's commitment to its nonce, fixed before the device's nonce is known, keeps a party in
/// between from choosing nonces until two codes agree. Six characters from <see cref="Alphabet"/> (about 30 bits), shown as <c>XXX-XXX</c>.
/// </summary>
public static class JoinMatch
{
    /// <summary>Nothing that reads as something else: no 0, 1, I, L or O (the pairing codes' own alphabet).</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int NonceLength = 16;

    public static string Code(byte[] certificateHash, byte[] clientNonce, byte[] serverNonce, string requestId)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"messages-join-v1|{Convert.ToHexStringLower(certificateHash)}|{Convert.ToHexStringLower(clientNonce)}|{Convert.ToHexStringLower(serverNonce)}|{requestId}"));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest);
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[(int)(value % (ulong)Alphabet.Length)];
            value /= (ulong)Alphabet.Length;
        }
        return new string(chars, 0, 3) + "-" + new string(chars, 3, 3);
    }

    /// <summary>The commitment to a nonce: its SHA-256, hex.</summary>
    public static string Commit(byte[] nonce) => Convert.ToHexStringLower(SHA256.HashData(nonce));

    /// <summary>A nonce as sent: <see cref="NonceLength"/> bytes in hex, or null when the text is not one.</summary>
    public static byte[]? ParseNonce(string? hex) =>
        hex is { Length: NonceLength * 2 } && hex.All(char.IsAsciiHexDigit) ? Convert.FromHexString(hex) : null;

    /// <summary>
    /// The finish code (protocol <see cref="ProtocolVersion.JoinFinish"/>): made by the server when the owner approves, shown only on the owner's screen, and typed on the
    /// joining device. The matching code above tells the owner the request is the device's; this tells the device the approval is the owner's. Without it the device took
    /// whoever answered at the address as its server (an adversarial review, 2026-10-14): something on the home network answering for the server's name could have said
    /// "approved" with nobody approving, been pinned, and then been offered the device's accounts by Move to this server. Random, from the same alphabet and shape.
    /// </summary>
    public static string FinishCode()
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars[..3]) + "-" + new string(chars[3..]);
    }

    /// <summary>
    /// What the finish code gives over the request's transcript (the certificate, both nonces and the id, as the matching code covers): HMAC-SHA256 keyed by the code, hex.
    /// The server sends it before the device's person types anything, so something that never saw the code has one guess at it, and the device sends nothing made from
    /// what was typed, so a wrong guess teaches it nothing.
    /// </summary>
    public static string FinishProof(string finishCode, byte[] certificateHash, byte[] clientNonce, byte[] serverNonce, string requestId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NormalFinish(finishCode)), System.Text.Encoding.UTF8.GetBytes(
            $"messages-join-finish-v1|{Convert.ToHexStringLower(certificateHash)}|{Convert.ToHexStringLower(clientNonce)}|{Convert.ToHexStringLower(serverNonce)}|{requestId}")));

    /// <summary>Whether the typed code gives the proof the server sent, compared in fixed time. Case, spaces and dashes in what was typed do not matter.</summary>
    public static bool FinishMatches(string? typed, string? proof, byte[] certificateHash, byte[] clientNonce, byte[] serverNonce, string requestId)
    {
        if (NormalFinish(typed) is not { Length: 6 } || proof is not { Length: 64 } || !proof.All(char.IsAsciiHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(FinishProof(typed!, certificateHash, clientNonce, serverNonce, requestId)),
            System.Text.Encoding.ASCII.GetBytes(proof.ToLowerInvariant()));
    }

    // The code as compared: its letters and digits only, in capitals.
    private static string NormalFinish(string? code) => new((code ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// The finish code typed on the device did not give the server's proof, after the tries the device allows: a mistyped code, or an answer from something that is not the
/// server the owner approved on. Nothing was kept and the server added nothing.
/// </summary>
public sealed class JoinFinishMismatchException() : IOException("No finish code that matches the server's was typed in time.");

/// <summary>The owner turned the request to join away.</summary>
public sealed class JoinDeclinedException() : UnauthorizedAccessException("The server's owner declined the request to join.");

/// <summary>Nobody answered the request to join before it ran out.</summary>
public sealed class JoinExpiredException() : TimeoutException("Nobody answered the request to join in time.");

/// <summary>The server's nonce was not the one it committed to: the one sign that something between this device and the server chose it. Nothing was kept.</summary>
public sealed class JoinUntrustedException() : IOException("The server's answer to the request to join could not be trusted.");

/// <summary>The server takes no requests to join: it is older than protocol <see cref="ProtocolVersion.JoinRequests"/>, or has nobody who could approve one yet.</summary>
public sealed class JoinNotOfferedException(bool noApprover = false, bool notHome = false) : NotSupportedException("The server does not take requests to join.")
{
    /// <summary>The server takes requests but has no device and no page to approve one (a first device pairs with the code the server printed).</summary>
    public bool NoApprover { get; } = noApprover;
    /// <summary>The server takes requests only from its home network, and this device asked from elsewhere.</summary>
    public bool NotHome { get; } = notHome;
}

/// <summary>What a device that asked to join and was let in keeps: its id, its token and the server's certificate as it saw it (what it pins from now on).</summary>
public sealed record JoinedDevice(string DeviceId, byte[] Token, byte[] Pin);

/// <param name="Code">The code the host printed, as the owner typed it (case and dashes do not matter).</param>
/// <param name="DeviceName">What the host lists the device as (<c>devices.list</c>).</param>
public sealed record PairClaimParams(string Code, string DeviceName);

/// <param name="Token">The device's token in its text form (<see cref="ClientToken.Encode"/>). The host keeps only a hash of it; this is the one time it crosses.</param>
public sealed record PairClaimed(string DeviceId, string Token);

/// <summary>The host turned the pairing code away: wrong, used or expired. Nothing about the code is in the message.</summary>
public sealed class PairingRefusedException() : UnauthorizedAccessException("The server did not accept the pairing code.");

/// <summary>The server refused this device's token because it was restored from a backup made before the device joined, or the restore forgot the device: it asks to join again.</summary>
public sealed class ServerRestoredException() : UnauthorizedAccessException("This server was restored from an earlier backup. Ask to join again.");

/// <summary>
/// The host did not look at the pairing code because a code was tried too recently (from anywhere, or from this address after wrong ones). The code is
/// not spent: a later try may succeed, so this is a connection that failed, not a refusal.
/// </summary>
public sealed class PairingPacedException() : IOException("The server asked to wait before trying a pairing code again.");

/// <summary>The host's certificate is not the one pinned. The connection ended in the TLS handshake, before a request or a frame was sent.</summary>
public sealed class CertificateMismatchException() : AuthenticationException("The server's certificate is not the one this device trusts.");

/// <summary>No device key is kept to present (never paired, or one this PC can no longer read): a connection that is not there, as any <see cref="IOException"/>, which a caller can tell apart.</summary>
public sealed class DeviceNotPairedException() : IOException("There is no device token to present.");

/// <summary>
/// The client's end of the network transport (SWITCHBOARD.md stage 4): one WebSocket, <c>wss://host:port/v1/socket</c>, carrying the same text frames
/// as the pipe. The host's certificate is self-signed, so it is trusted by its SHA-256 fingerprint alone: a certificate whose fingerprint matches the
/// pinned one is accepted whatever its chain or names say, and every other certificate is refused, whatever its chain or names say. The token goes
/// only to a host that passed that check, because the TLS handshake ends before the request that carries it is written.
/// </summary>
public static class NetworkEndpoint
{
    public const string Target = "/v1/socket";
    public const int DefaultPort = 8787;

    /// <summary>How long a connection may take to be open (TCP, TLS and the upgrade).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How many finish codes a person may type before a request to join stops (<see cref="JoinAsync"/>): a slip of the finger, not a way to guess.</summary>
    public const int FinishTries = 3;

    /// <summary>How often either end checks that the other is still there, and how long it waits for the answer: a network can drop a peer without a word.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>The socket's address for a server given as <c>wss://host[:port]</c>, or null when that is not one. Only <c>wss</c> is accepted: nothing of the protocol crosses in the clear.</summary>
    public static Uri? SocketUri(string? server)
    {
        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "wss" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return new UriBuilder("wss", uri.Host, uri.IsDefaultPort ? DefaultPort : uri.Port, Target).Uri;
    }

    /// <summary>The 32 bytes of a SHA-256 fingerprint written as hex (colons and spaces allowed, any case), or null when the text is not one.</summary>
    public static byte[]? ParseFingerprint(string? text)
    {
        if (text is null) return null;
        var hex = new string([.. text.Where(c => c is not (':' or ' ' or '-'))]);
        if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit)) return null;
        return Convert.FromHexString(hex);
    }

    /// <summary>A certificate's SHA-256 fingerprint as lowercase hex: what a host logs and an owner pins.</summary>
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>Whether <paramref name="certificate"/> is the pinned one, compared in fixed time. No certificate is never a match.</summary>
    public static bool IsPinned(X509Certificate? certificate, byte[] pinned) =>
        certificate is not null && pinned.Length == 32 && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pinned);

    /// <summary>
    /// A connector for <see cref="ProtocolClient"/>: opens the socket to <paramref name="socket"/> (from <see cref="SocketUri"/>), trusting only the pinned
    /// certificate, and presents the token <paramref name="token"/> answers with the upgrade. It throws <see cref="CertificateMismatchException"/> for a
    /// certificate that is not the pinned one, <see cref="UnauthorizedAccessException"/> for a token the host refused, and <see cref="DeviceNotPairedException"/> (an <see cref="IOException"/>) when there
    /// is no token to present; <see cref="ProtocolClient"/> treats each as a connection that is not there. Refusals are logged as
    /// <c>protocol remote refused reason=fingerprint|token|unpaired</c>, the first and then one in a hundred. With <paramref name="dialer"/>, the connection is
    /// opened over whichever of its routes answers first (the socket's host is still the name TLS and the upgrade use), and the channel's
    /// <see cref="WebSocketFrameChannel.Route"/> says which.
    /// </summary>
    public static Func<CancellationToken, Task<IFrameChannel>> Connector(Uri socket, byte[] fingerprint, Func<CancellationToken, Task<byte[]?>> token,
        Action<string>? log = null, TimeSpan? connectTimeout = null, ServerDialer? dialer = null)
    {
        var refusals = 0;
        void Refused(string reason)
        {
            var count = Interlocked.Increment(ref refusals);
            if (count == 1 || count % 100 == 0) log?.Invoke($"protocol remote refused reason={reason}{(count == 1 ? "" : $" count={count}")}");
        }
        return async cancellationToken =>
        {
            var bearer = await token(cancellationToken).ConfigureAwait(false);
            if (bearer is null)
            {
                Refused("unpaired");
                throw new DeviceNotPairedException();
            }
            try
            {
                var opened = await OpenAsync(socket, fingerprint, "Authorization", ClientToken.HeaderValue(bearer), connectTimeout ?? ConnectTimeout, cancellationToken, dialer).ConfigureAwait(false);
                return new WebSocketFrameChannel(opened.Socket, log) { Route = opened.Route };
            }
            catch (CertificateMismatchException) { Refused("fingerprint"); throw; }
            catch (ServerRestoredException) { Refused("restored"); throw; }
            catch (UnauthorizedAccessException) { Refused("token"); throw; }
        };
    }

    /// <summary>
    /// Pairs this device: opens the socket with <paramref name="code"/> in place of a token, claims a token for <paramref name="deviceName"/>, and returns it.
    /// Throws <see cref="PairingRefusedException"/> when the host turned the code away (a definite answer: the code is spent or wrong, and asking again with
    /// it will not help), <see cref="CertificateMismatchException"/> before anything is sent to a host that is not the pinned one, and anything else for a
    /// connection that failed (which may be tried again).
    /// </summary>
    public static async Task<(string DeviceId, byte[] Token)> ClaimAsync(Uri socket, byte[] fingerprint, string code, string deviceName, string appVersion,
        CancellationToken cancellationToken = default, TimeSpan? connectTimeout = null, ServerDialer? dialer = null)
    {
        Opened opened;
        try { opened = await OpenAsync(socket, fingerprint, NetworkPairing.Header, code.Trim(), connectTimeout ?? ConnectTimeout, cancellationToken, dialer).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { throw new PairingRefusedException(); }
        var channel = new WebSocketFrameChannel(opened.Socket) { Route = opened.Route };
        using var client = new ProtocolClient(channel, appVersion);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(connectTimeout ?? ConnectTimeout);
            await client.ConnectAsync(limit.Token).ConfigureAwait(false);
            var claimed = await client.CallAsync<PairClaimParams, PairClaimed>(NetworkPairing.ClaimMethod,
                new PairClaimParams(code.Trim(), deviceName), limit.Token).ConfigureAwait(false);
            var token = ClientToken.Decode(claimed.Token);
            if (token is not { Length: ClientToken.Length } || string.IsNullOrEmpty(claimed.DeviceId)) throw new InvalidDataException("The server's pairing answer could not be read.");
            return (claimed.DeviceId, token);
        }
        catch (BackendException refused) when (refused.Failure == BackendFailure.Forbidden) { throw new PairingRefusedException(); }
        finally { channel.Close(); }
    }

    /// <summary>
    /// Asks to join the server at <paramref name="socket"/> with nothing pinned (protocol <see cref="ProtocolVersion.JoinFinish"/>): the certificate it sees is recorded,
    /// <paramref name="showCode"/> is given the code to show (<see cref="JoinMatch"/>) once both nonces are known, and the call then waits for the owner. Once approved,
    /// <paramref name="finishCode"/> is asked for the finish code the owner's screen shows (its argument says whether the last one typed did not match; null gives up)
    /// until one gives the server's proof, at most <see cref="FinishTries"/> times (<see cref="JoinFinishMismatchException"/> after that). Returns the
    /// device's id and token and the certificate to pin once approved, after <paramref name="keep"/> has kept them and the server was told so (a device that does not say
    /// so is removed again; <paramref name="keep"/> throwing ends the request that way). Throws <see cref="JoinDeclinedException"/>, <see cref="JoinExpiredException"/>,
    /// <see cref="JoinNotOfferedException"/> (a server older than <see cref="ProtocolVersion.JoinFinish"/>, which cannot prove an approval, or one with nobody to approve),
    /// <see cref="PairingPacedException"/> (the server holds as many requests as it
    /// takes, or this address asked too recently), <see cref="JoinUntrustedException"/>, <see cref="InvalidOperationException"/> with <see cref="JoinWords.Failed"/>, and
    /// anything else for a connection that failed. Cancelling withdraws the request: its connection closes. With <paramref name="dialer"/>, the request goes over
    /// whichever of its routes answers first, and the certificate recorded is the one on the connection that won.
    /// </summary>
    public static async Task<JoinedDevice> JoinAsync(Uri socket, string deviceName, string appVersion, Action<string> showCode, CancellationToken cancellationToken = default,
        TimeSpan? connectTimeout = null, Func<JoinedDevice, Task>? keep = null, Func<bool, CancellationToken, Task<string?>>? finishCode = null, ServerDialer? dialer = null)
    {
        byte[]? seen = null;
        Opened opened;
        try
        {
            // Nothing is pinned yet: whatever certificate answers is let through and recorded, and the code the owner matches is what tells the right one from another.
            // Through a dialer routes race, and a loser's handshake may end after the winner's, so the certificate is taken from the winning connection instead.
            opened = await OpenAsync(socket, certificate =>
            {
                if (certificate is null) return false;
                if (dialer is null) seen = certificate.GetCertHash(HashAlgorithmName.SHA256);
                return true;
            }, NetworkPairing.JoinHeader, "1", connectTimeout ?? ConnectTimeout, cancellationToken, dialer).ConfigureAwait(false);
        }
        // An older server reads a connection with no token and no code as one without a token.
        catch (UnauthorizedAccessException) { throw new JoinNotOfferedException(); }
        if (dialer is not null) seen = opened.Certificate;
        var channel = new WebSocketFrameChannel(opened.Socket) { Route = opened.Route };
        using var client = new ProtocolClient(channel, appVersion);
        try
        {
            // The steps before the wait take a server moments: one that does not answer them in time is a connection that failed, not a request that ran out.
            using var steps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            steps.CancelAfter(connectTimeout ?? ConnectTimeout);
            PairJoinStarted started;
            byte[] mine;
            PairJoinNonce theirs;
            try
            {
                await client.ConnectAsync(steps.Token).ConfigureAwait(false);
                // Not one that cannot prove the owner's approval: something that is not the server would say it is older to skip the proof.
                if (client.Version is not >= ProtocolVersion.JoinFinish) throw new JoinNotOfferedException();
                started = await client.CallAsync<PairJoinParams, PairJoinStarted>(NetworkPairing.JoinStartMethod, new(deviceName), steps.Token).ConfigureAwait(false);
                mine = RandomNumberGenerator.GetBytes(JoinMatch.NonceLength);
                theirs = await client.CallAsync<PairJoinNonceParams, PairJoinNonce>(NetworkPairing.JoinNonceMethod, new(started.RequestId, Convert.ToHexStringLower(mine)),
                    steps.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("The server did not answer in time."); }
            // The server's nonce must be the one it committed to before it knew this one; otherwise it (or a party in between) could have chosen it to fit a code.
            if (seen is null || JoinMatch.ParseNonce(theirs.ServerNonce) is not { } committed
                || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(JoinMatch.Commit(committed)), System.Text.Encoding.ASCII.GetBytes(started.Commitment)))
                throw new JoinUntrustedException();
            showCode(JoinMatch.Code(seen, mine, committed, started.RequestId));
            // The server ends the request at its own time; this only keeps a server that went quiet from holding the device for ever.
            PairJoinApproved approved;
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                waiting.CancelAfter(TimeSpan.FromSeconds(Math.Max(theirs.ExpiresSeconds, 0)) + TimeSpan.FromSeconds(30));
                try { approved = await client.CallAsync<PairJoinWaitParams, PairJoinApproved>(NetworkPairing.JoinWaitMethod, new(started.RequestId), waiting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new JoinExpiredException(); }
            }
            // Approved, says the server: its proof must come from the finish code the owner's screen shows, typed here, before anything is asked for or kept.
            if (finishCode is null) throw new InvalidOperationException("Joining needs a way to ask for the finish code.");
            using (var typing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                typing.CancelAfter(TimeSpan.FromSeconds(Math.Max(approved.FinishSeconds, 0)));
                for (var tries = 0; ; tries++)
                {
                    if (tries == FinishTries) throw new JoinFinishMismatchException();
                    string? typed;
                    try { typed = await finishCode(tries > 0, typing.Token).ConfigureAwait(false); }
                    // Not typed in time, or given up: no right code, so nothing is asked for or kept.
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new JoinFinishMismatchException(); }
                    if (typed is null) throw new JoinFinishMismatchException();
                    if (JoinMatch.FinishMatches(typed, approved.Proof, seen, mine, committed, started.RequestId)) break;
                }
            }
            PairClaimed claimed;
            using (var finishing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                finishing.CancelAfter(connectTimeout ?? ConnectTimeout);
                try { claimed = await client.CallAsync<PairJoinFinishParams, PairClaimed>(NetworkPairing.JoinFinishMethod, new(started.RequestId), finishing.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("The server did not answer in time."); }
            }
            var token = ClientToken.Decode(claimed.Token);
            if (token is not { Length: ClientToken.Length } || string.IsNullOrEmpty(claimed.DeviceId)) throw new InvalidDataException("The server's answer to the request to join could not be read.");
            var joined = new JoinedDevice(claimed.DeviceId, token, seen);
            // Kept first, then said: a device that could not keep its key says nothing, and the server removes it again.
            if (keep is not null) await keep(joined).ConfigureAwait(false);
            using (var ack = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                ack.CancelAfter(connectTimeout ?? ConnectTimeout);
                try { await client.CallAsync<PairJoinAckParams, Done>(NetworkPairing.JoinAckMethod, new(started.RequestId), ack.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("The server did not answer in time."); }
            }
            return joined;
        }
        catch (InvalidOperationException word) when (word.Message == JoinWords.Declined) { throw new JoinDeclinedException(); }
        catch (InvalidOperationException word) when (word.Message == JoinWords.Expired) { throw new JoinExpiredException(); }
        finally { channel.Close(); }
    }

    // One socket, open and upgraded, with `header: value` on the upgrade. The certificate is checked before the request that carries the header is written.
    private static Task<Opened> OpenAsync(Uri socket, byte[] fingerprint, string header, string value, TimeSpan timeout, CancellationToken cancellationToken, ServerDialer? dialer) =>
        OpenAsync(socket, certificate => IsPinned(certificate, fingerprint), header, value, timeout, cancellationToken, dialer);

    // The open socket, the route a dialer carried it on, and (through a dialer) the SHA-256 of the certificate on the connection that won.
    private sealed record Opened(WebSocket Socket, string? Route, byte[]? Certificate);

    private static async Task<Opened> OpenAsync(Uri socket, Func<X509Certificate?, bool> trust, string header, string value, TimeSpan timeout, CancellationToken cancellationToken,
        ServerDialer? dialer)
    {
        if (dialer is not null) return await OpenDialedAsync(socket, dialer, trust, header, value, timeout, cancellationToken).ConfigureAwait(false);
        var client = new ClientWebSocket();
        var mismatch = 0;
        try
        {
            var options = Configure(client, header, value);
            // Straight to the host: a proxy in between would be a party to the connection that nobody chose.
            options.Proxy = null;
            // The pin is the whole of the trust: chain and name errors are expected for a self-signed certificate and are ignored only when the
            // fingerprint matches; nothing else is accepted, a certificate a public authority vouches for included.
            options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (trust(certificate)) return true;
                Volatile.Write(ref mismatch, 1);
                return false;
            };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            await client.ConnectAsync(socket, limit.Token).ConfigureAwait(false);
            return new Opened(client, null, null);
        }
        catch (Exception ex)
        {
            var refusal = Refusal(client, ex, Volatile.Read(ref mismatch) == 1, cancellationToken);
            client.Dispose();
            if (refusal is not null) throw refusal;
            throw;
        }
    }

    // The same socket over a connection a dialer opened, its TLS already done and its certificate already trusted: the WebSocket speaks plain HTTP/1.1 over
    // that TLS stream, so the request with the header is still written only to a server that passed the check.
    private static async Task<Opened> OpenDialedAsync(Uri socket, ServerDialer dialer, Func<X509Certificate?, bool> trust, string header, string value, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        // The dialer's own deadline rather than a cancelled token, so a route that failed telling (a signed-out node) is not lost to another route's timeout.
        var (stream, route) = await dialer.OpenAsync(socket.IdnHost, trust, cancellationToken, timeout).ConfigureAwait(false);
        var certificate = (stream as SslStream)?.RemoteCertificate?.GetCertHash(HashAlgorithmName.SHA256);
        var handed = 0;
        // The handler's only connection is the dialed one. .NET leaves an upgraded HTTP/1.1 connection to the WebSocket, which disposes it with itself, and
        // never disposes an invoker it was given; disposing the handler once the upgrade is done leaves the socket working (both measured on .NET 10 and held
        // by ServerRouteChecks). A refused upgrade puts the connection back in the handler's pool, where only disposing the handler would close it.
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = (_, _) => Interlocked.Exchange(ref handed, 1) == 0
                ? ValueTask.FromResult(stream)
                : ValueTask.FromException<Stream>(new IOException("The dialed connection was already used.")),
        }, disposeHandler: true);
        var client = new ClientWebSocket();
        try
        {
            // Not the proxy, the certificate callback, credentials or cookies: .NET refuses those beside an invoker (ArgumentException), and none applies to a
            // connection that is already open and trusted.
            Configure(client, header, value);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var left = timeout - clock.Elapsed;
            limit.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            await client.ConnectAsync(new UriBuilder("ws", socket.Host, socket.Port, socket.AbsolutePath).Uri, invoker, limit.Token).ConfigureAwait(false);
            return new Opened(client, route, certificate);
        }
        catch (Exception ex)
        {
            var refusal = Refusal(client, ex, Mismatch(ex), cancellationToken);
            client.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            if (refusal is not null) throw refusal;
            throw;
        }
        finally { invoker.Dispose(); }
    }

    private static ClientWebSocketOptions Configure(ClientWebSocket client, string header, string value)
    {
        var options = client.Options;
        options.HttpVersion = HttpVersion.Version11;
        options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        options.KeepAliveInterval = KeepAlive;
        options.KeepAliveTimeout = KeepAlive;
        options.CollectHttpResponseDetails = true;
        options.SetRequestHeader(header, value);
        return options;
    }

    private static bool Mismatch(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is CertificateMismatchException) return true;
        return false;
    }

    // What an upgrade that failed means, from the answer it got; null to let the failure go as it is.
    private static Exception? Refusal(ClientWebSocket client, Exception ex, bool mismatch, CancellationToken cancellationToken)
    {
        var status = client.HttpStatusCode;
        var restored = client.HttpResponseHeaders?.TryGetValue(NetworkPairing.RefusalHeader, out var words) == true && words.Contains(NetworkPairing.RefusalRestored);
        if (mismatch) return new CertificateMismatchException();
        if (status == HttpStatusCode.Unauthorized && restored) return new ServerRestoredException();
        if (status == HttpStatusCode.Unauthorized) return new UnauthorizedAccessException("The server refused the token.");
        if (status == HttpStatusCode.TooManyRequests) return new PairingPacedException();
        // Only a join connection is answered 409 (the server has nobody, no paired device and no page, who could approve it) or 403 (it asked from outside the home network).
        if (status == HttpStatusCode.Conflict) return new JoinNotOfferedException(noApprover: true);
        if (status == HttpStatusCode.Forbidden) return new JoinNotOfferedException(notHome: true);
        if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested) return new IOException("The server did not answer in time.");
        return null;
    }
}
