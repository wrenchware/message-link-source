using System.Net;
using System.Security.Cryptography;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// The devices asking to join (protocol 21, SWITCHBOARD.md 4.6): a device with nothing but the server's address connects without a token on a join connection
/// (<see cref="JoinSession"/>), and the request is held here until the owner approves it on the server's page,
/// turns it away, it runs out after <see cref="Lifetime"/>, or the device leaves. Nobody could otherwise reach anything without a token, so the door is narrow: at most
/// <see cref="MaxWaiting"/> join connections at once, one per source (an IPv4 address, or an IPv6 /64, as <see cref="PairingCodes.Key"/> counts them), a source asking
/// again only after <see cref="SourceSpacing"/> (longer after an unanswered request), and any two let in <see cref="GlobalSpacing"/> apart. A request is shown only
/// once its code is known and its wait arrived; an approval pairs on that connection only after acknowledgement.
/// <para>Logs counts and fixed words only (<c>server join refused reason=</c>, sampled; <c>server join waiting</c>, and the session's <c>outcome=</c>): never a name, a code or an address.</para>
/// </summary>
internal sealed class JoinRequests(TimeProvider time, Action<string> log, string? pausePath = null)
{
    /// <summary>The most join connections open at once, a request or not yet.</summary>
    public const int MaxWaiting = 4;
    /// <summary>How long a request holds.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    /// <summary>The least time between two join connections let in, from anywhere.</summary>
    public static readonly TimeSpan GlobalSpacing = TimeSpan.FromSeconds(1);
    /// <summary>The minimum spacing; a deny or abandoned request imposes <see cref="UnansweredSpacing"/> from its end.</summary>
    public static readonly TimeSpan SourceSpacing = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan UnansweredSpacing = TimeSpan.FromMinutes(5);
    /// <summary>How long an approval waits to hear that the device kept its token (under the app's 20 s for an action).</summary>
    public static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(15);
    /// <summary>How long an approved device of protocol <see cref="ProtocolVersion.JoinFinish"/> has for its person to type the finish code (<see cref="JoinSession"/>).</summary>
    public static readonly TimeSpan FinishWait = TimeSpan.FromMinutes(5);
    private const int MaxSources = 1024;

    internal enum Admission { Admitted, Paced, Full }

    internal enum Outcome { Approved, Declined, Expired, Withdrawn }

    internal sealed class Request
    {
        public required string Id { get; init; }
        public required string SourceKey { get; init; }
        public required string Source { get; init; }
        public required DateTimeOffset Asked { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public required byte[] ServerNonce { get; init; }
        public string Name { get; set; } = "";
        /// <summary>Set once both nonces are known; visibility also requires Waiting.</summary>
        public string? Code { get; set; }
        public bool Waiting { get; set; }
        /// <summary>The version the join connection negotiated: from <see cref="ProtocolVersion.JoinFinish"/>, an approval makes a finish code instead of pairing at once.</summary>
        public int Protocol { get; set; }
        /// <summary>What the code and the finish code's proof cover besides the nonce above: the device's nonce and the certificate the server answered with.</summary>
        public byte[]? ClientNonce { get; set; }
        public byte[]? CertificateHash { get; set; }
        /// <summary>Made on approval for a device of <see cref="ProtocolVersion.JoinFinish"/>; shown only to the owner, never logged.</summary>
        public string? FinishCode { get; set; }
        /// <summary>Old entries of this device the owner chose to remove, once it has joined.</summary>
        public IReadOnlyList<string> Replaces { get; set; } = [];
        public TaskCompletionSource<Outcome> Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// The device id once the device said it kept its token; null when it could not be handed over, or when the approval stopped waiting first (whichever is set
        /// first stands: the session reads it before it pairs and before it counts the device as joined, so the owner is never told the opposite of what happened).
        /// </summary>
        public TaskCompletionSource<string?> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly List<Request> open = [];
    private readonly Dictionary<string, DateTimeOffset> lastAsk = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> refusals = new(StringComparer.Ordinal);
    private DateTimeOffset next;
    private bool paused = pausePath is not null && File.Exists(pausePath);
    public bool Paused { get { lock (gate) return paused; } }

    // Kept across nightly restarts. Existing paired devices and explicit pairing codes are unaffected.
    public void SetPaused(bool value)
    {
        lock (gate)
        {
            if (pausePath is not null)
            {
                if (value) { Directory.CreateDirectory(Path.GetDirectoryName(pausePath)!); File.WriteAllText(pausePath, "paused", System.Text.Encoding.UTF8); }
                else File.Delete(pausePath);
            }
            paused = value;
            if (value)
                foreach (var request in open) request.Decided.TrySetResult(Outcome.Declined);
        }
        log($"server join requests={(value ? "paused" : "open")}");
        Changed?.Invoke();
    }

    /// <summary>Raised when the requests shown change (one asked, was answered, ran out or left), from the thread that changed them.</summary>
    public event Action? Changed;

    /// <summary>Now, by the clock requests run out by.</summary>
    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Lets a join connection in, or says why not. One let in holds its place until <see cref="Leave"/>.</summary>
    public Admission Admit(IPAddress? address, out Request? request)
    {
        request = null;
        var key = PairingCodes.Key(address);
        var now = time.GetUtcNow();
        lock (gate)
        {
            SweepLocked(now);
            if (paused) { SampledLocked("paused"); return Admission.Paced; }
            if (open.Count >= MaxWaiting)
            {
                SampledLocked("full");
                return Admission.Full;
            }
            if (open.Any(waiting => waiting.SourceKey == key) || now < next || lastAsk.TryGetValue(key, out var until) && now < until
                || lastAsk.Count >= MaxSources && !lastAsk.ContainsKey(key))
            {
                SampledLocked("paced");
                return Admission.Paced;
            }
            next = now + GlobalSpacing;
            lastAsk[key] = now + SourceSpacing;
            request = new Request
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), SourceKey = key, Source = Shown(address), Asked = now, Expires = now + Lifetime,
                ServerNonce = RandomNumberGenerator.GetBytes(JoinMatch.NonceLength),
            };
            open.Add(request);
            return Admission.Admitted;
        }
    }

    /// <summary>The device's name, as it asked (cleaned as a paired device's is).</summary>
    public void Start(Request request, string? name) => request.Name = DeviceRegistry.CleanName(name);

    /// <summary>A join connection that broke the steps (<c>server join refused reason=</c>): logged the first time and then one in a hundred, as the door's own refusals.</summary>
    public void Refused(string reason)
    {
        lock (gate) SampledLocked(reason);
    }

    /// <summary>Works out the request's code from the device's nonce and the server's own certificate, and shows the request to the owner from now on.</summary>
    public string Match(Request request, byte[] clientNonce, byte[] certificateHash)
    {
        lock (gate)
        {
            if (request.Code is null) { request.ClientNonce = clientNonce; request.CertificateHash = certificateHash; }
            request.Code ??= JoinMatch.Code(certificateHash, clientNonce, request.ServerNonce, request.Id);
        }
        return request.Code;
    }

    // A nonce alone is not a waiting device. Show it only after the wait request actually arrives.
    public void Wait(Request request)
    {
        lock (gate)
        {
            if (request.Code is null || request.Waiting || request.Decided.Task.IsCompleted || !open.Contains(request)) return;
            request.Waiting = true;
        }
        log("server join waiting");
        Changed?.Invoke();
    }

    /// <summary>The requests the owner can answer now: with a code, not yet answered, not run out.</summary>
    public IReadOnlyList<JoinRequest> Waiting()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            SweepLocked(now);
            return [.. open.Where(request => request.Waiting && request.Code is not null && !request.Decided.Task.IsCompleted)
                .Select(request => new JoinRequest(request.Id, request.Name, request.Source, request.Asked, request.Expires, request.Code!))];
        }
    }

    /// <summary>
    /// Lets the device in. A device of protocol <see cref="ProtocolVersion.JoinFinish"/> is answered at once with the finish code its person types (it is paired once they
    /// have); an older one is paired on its own connection, and the answer says as what (or that it could not be, the device having gone).
    /// </summary>
    public async Task<JoinApproved> ApproveAsync(string? id, string via, IReadOnlyList<string>? replaces = null)
    {
        var request = Answerable(id);
        if (request is null) return new(false);
        string? finish;
        // Under the gate, so two approvals at once (two of the page's tabs) cannot both make a code: the second finds the first's answer and leaves its code alone.
        // Made before the session can see the approval, so it proves the code the owner was given; a refusal that wins meanwhile leaves a code nothing reads.
        lock (gate)
        {
            if (request.Decided.Task.IsCompleted) return new(false);
            finish = request.Protocol >= ProtocolVersion.JoinFinish ? JoinMatch.FinishCode() : null;
            request.FinishCode = finish;
            request.Replaces = replaces ?? [];
            if (!request.Decided.TrySetResult(Outcome.Approved)) return new(false);
        }
        log($"server join approved via={via}");
        Changed?.Invoke();
        if (finish is not null) return new(true, null, finish);
        string? device;
        try { device = await request.Delivered.Task.WaitAsync(DeliveryWait).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Given up, and said so first: the session then pairs nothing, or removes what it paired, rather than let in a device the owner was told did not join.
            request.Delivered.TrySetResult(null);
            device = await request.Delivered.Task.ConfigureAwait(false);
        }
        return new(device is not null, device);
    }

    /// <summary>Turns the request away; the device is told so.</summary>
    public JoinDenied Deny(string? id, string via)
    {
        var request = Answerable(id);
        lock (gate)
        {
            if (request is null || !request.Decided.TrySetResult(Outcome.Declined)) return new(false);
            lastAsk[request.SourceKey] = time.GetUtcNow() + UnansweredSpacing;
        }
        log($"server join declined via={via}");
        Changed?.Invoke();
        return new(true);
    }

    /// <summary>The connection is done: its place in the door is free, and a request still unanswered is withdrawn.</summary>
    public void Leave(Request request)
    {
        lock (gate)
        {
            open.Remove(request);
            if (!request.Delivered.Task.IsCompletedSuccessfully || request.Delivered.Task.Result is null)
                lastAsk[request.SourceKey] = time.GetUtcNow() + UnansweredSpacing;
        }
        if (request.Decided.TrySetResult(Outcome.Withdrawn)) Changed?.Invoke();
        request.Delivered.TrySetResult(null);
    }

    /// <summary>Ends the requests that have run out (the network host calls it on its pairing check; a check calls it after moving the clock).</summary>
    public void Sweep()
    {
        lock (gate) SweepLocked(time.GetUtcNow());
    }

    private void SweepLocked(DateTimeOffset now)
    {
        var ended = false;
        foreach (var request in open.Where(request => now >= request.Expires)) ended |= request.Decided.TrySetResult(Outcome.Expired);
        foreach (var stale in lastAsk.Where(pair => now >= pair.Value).Select(pair => pair.Key).ToArray()) lastAsk.Remove(stale);
        if (ended) Changed?.Invoke();
    }

    private Request? Answerable(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var now = time.GetUtcNow();
        lock (gate)
        {
            SweepLocked(now);
            return open.FirstOrDefault(request => request.Id == id && request.Waiting && request.Code is not null && !request.Decided.Task.IsCompleted);
        }
    }

    // Under the gate: the first and then one in a hundred of each reason, as the door's own refusals.
    private void SampledLocked(string reason)
    {
        var total = refusals[reason] = refusals.GetValueOrDefault(reason) + 1;
        if (total == 1 || total % 100 == 0) log($"server join refused reason={reason}{(total == 1 ? "" : $" count={total}")}");
    }

    // The address as the owner reads it: IPv4 plainly (a mapped one too), IPv6 as written.
    private static string Shown(IPAddress? address)
    {
        if (address is null) return "an unknown address";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }
}
