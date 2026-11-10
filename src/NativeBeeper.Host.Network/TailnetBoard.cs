using System.Text.Json;
using System.Text.Json.Serialization;

namespace NativeBeeper.Host.Network;

/// <summary>
/// What the server's tailnet door last reported for the server's own page (SWITCHBOARD.md 4.7; experiments/Tailnet/door/page.go): its state, the sign-in link while
/// it needs one, its name and address, the owner it lets in, key expiry, Tailnet Lock, and what the tailnet's access rules let reach it. Held in memory for the page,
/// which only its signed-in owner sees from home, and never logged: the link decides whose tailnet the server joins, and the owner's login is a name.
/// </summary>
/// <param name="State">Tailscale's own word: <c>NeedsLogin</c>, <c>Starting</c>, <c>Running</c> and so on.</param>
/// <param name="Expiry"><c>off</c>, or when the node's key expires (RFC 3339).</param>
/// <param name="Lock"><c>off</c>, <c>signed</c>, <c>unsigned</c> (then <paramref name="NodeKey"/> is the key to sign) or <c>unknown</c>.</param>
/// <param name="Rules"><c>yours</c> (only the owner's own devices may reach the server's port), <c>open</c> (every device may, <paramref name="Everyone"/>, or
/// <paramref name="Others"/> devices that are not the owner's), <c>none</c>, or <c>unknown</c>.</param>
/// <param name="SignedOut">Tailscale signed the node out after it had been signed in (its key expired, or it was logged out): the sign-in is a sign-in again.</param>
/// <param name="Phones">The owner's Message Link devices on the tailnet (nodes named <c>messagelink-…</c> or <c>messages-…</c> other than a server), each with its
/// key expiry: phones, and PCs running Message Link's own Tailscale (<see cref="TailnetPhone.Pc"/>). The door's field keeps its first name.</param>
/// <param name="Users">How many people's devices the tailnet holds; 1 when only the owner's.</param>
internal sealed record TailnetView(string State, string? Login = null, string? Name = null, string? DnsName = null, string? Address = null, string? Owner = null,
    bool Tagged = false, string? Expiry = null, string Lock = "unknown", string? NodeKey = null, string Rules = "unknown", int Others = 0, bool Everyone = false,
    bool SignedOut = false, IReadOnlyList<TailnetPhone>? Phones = null, int Users = 0)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>The door's report (the JSON after <c>tailnet page </c>), or null when it is not one.</summary>
    public static TailnetView? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<TailnetView>(json, Json) is { State.Length: > 0 } view ? view : null; }
        catch (JsonException) { return null; }
    }

    [JsonIgnore] public bool Running => State == "Running";
    [JsonIgnore] public bool NeedsLogin => State == "NeedsLogin" && Login is { Length: > 0 };
    [JsonIgnore] public bool ExpiryOff => Expiry == "off";
    [JsonIgnore] public bool NeedsApproval => State == "NeedsMachineAuth";

    /// <summary>
    /// Whether only the owner's devices can reach the server's port: the access rules say so, or the tailnet is the owner's alone and its rule lets every device in
    /// (Tailscale's default), which then reaches only theirs.
    /// </summary>
    [JsonIgnore] public bool OnlyYours => Rules == "yours" || Rules == "open" && Everyone && Others == 0 && Users == 1;

    /// <summary>How many of the listed devices are phones rather than PCs.</summary>
    [JsonIgnore] public int PhoneCount => (Phones ?? []).Count(phone => !phone.Pc);

    /// <summary>The phones and PCs whose keys still expire.</summary>
    [JsonIgnore] public IEnumerable<TailnetPhone> PhonesExpiring => (Phones ?? []).Where(phone => !phone.ExpiryOff);

    /// <summary>
    /// Whether the owner has something to do: sign it in (or again), have it approved, sign it for Tailnet Lock, turn key expiry off for it, a phone or a PC, or let their
    /// devices reach it at all.
    /// </summary>
    [JsonIgnore] public bool NeedsAttention => NeedsLogin || State == "NeedsLogin" || NeedsApproval
        || Running && (Lock == "unsigned" || !ExpiryOff && Expiry is not null || Rules == "none" || Rules == "open" && !OnlyYours || PhonesExpiring.Any());

    /// <summary>Whether everything the page's checklist asks for is done.</summary>
    [JsonIgnore] public bool AllDone => Running && !NeedsAttention;
}

/// <summary>
/// One of the owner's Message Link devices on the tailnet, as the door names it: its node name, its address, <c>off</c> or when its key expires, and Tailscale's
/// word for its system (<c>android</c>, <c>windows</c>, …; absent from a door older than the field).
/// </summary>
internal sealed record TailnetPhone(string Name, string? Address = null, string? Expiry = null, string? Os = null)
{
    [JsonIgnore] public bool ExpiryOff => Expiry == "off";

    /// <summary>A Windows PC running Message Link's own Tailscale: it names its node like a phone, so only its system tells them apart.</summary>
    [JsonIgnore] public bool Pc => string.Equals(Os, "windows", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where the door's reports wait for the page: made before either (the network host starts first), given to both. <see cref="Enabled"/> says whether the server
/// was started with its tailnet door (<c>MESSAGES_TAILNET=1</c>); a server without it shows the page how to turn it on.
/// </summary>
internal sealed class TailnetBoard(bool enabled, int port = NativeBeeper.Protocol.NetworkEndpoint.DefaultPort)
{
    private volatile TailnetView? latest;

    private volatile bool enabled = enabled;

    public bool Enabled => enabled;

    /// <summary>
    /// The door turned on or off from the page, on a host that can (the Windows server, SWITCHBOARD.md 10.7: it keeps the choice in <c>Server\network.json</c> and starts
    /// or stops the door at once); null where it is a setting of the install (Docker's <c>MESSAGES_TAILNET</c>), which the page then explains instead. False when
    /// the change could not be made.
    /// </summary>
    public Func<bool, bool>? Turn { get; set; }

    /// <summary>Said by the host that turns the door on or off (<see cref="Turn"/>).</summary>
    public void SetEnabled(bool on)
    {
        enabled = on;
        if (!on) latest = null;
    }

    /// <summary>The port devices reach the server on through the door.</summary>
    public int Port { get; } = port;

    /// <summary>The last report, or null before the door's first.</summary>
    public TailnetView? Latest => latest;

    public void Set(TailnetView? view) => latest = view;

    /// <summary>
    /// "Use another Tailscale account": signs the door's node out of Tailscale and forgets its state, so it starts afresh and asks for a new sign-in (set by the door's
    /// supervisor, TailnetDoor). False when there is no door running to ask.
    /// </summary>
    public Func<bool>? SignOut { get; set; }
}
