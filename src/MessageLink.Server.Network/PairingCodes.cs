using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MessageLink.Server.Network;

/// <summary>
/// The one-time code a device pairs with (SWITCHBOARD.md stage 4). At most one is open at a time: <see cref="Issue"/> replaces it. It is
/// <see cref="Length"/> characters from an alphabet with nothing that reads as something else (no 0, 1, I, L or O), printed in two groups as
/// <c>server pairing code=XXXXX-XXXXX expires_s=600</c>, and is good for one pairing within <see cref="Lifetime"/>. Comparisons are in fixed time. The
/// printed line is the only place a code is written; what a peer presented never is.
/// <para>
/// Wrong codes do not burn it: anyone on the home network could otherwise spend the owner's code with a few wrong tries, and a new one used to need a
/// restart (which reconnects the accounts). Guessing is paced instead (<see cref="TryPacing"/>, as the page's password is). A code is checked at most once per <see cref="GlobalSpacing"/> from anywhere, and
/// each source (an IPv4 address, or an IPv6 /64, which one machine can fill with addresses) waits <see cref="SourceSpacing"/> after a wrong code, doubling
/// from the <see cref="FreeWrong"/>th wrong one up to <see cref="LongestWait"/>. A try made too soon is not looked at, so it tells the peer nothing and
/// costs the code nothing. At the global pace a peer gets at most 600 guesses at a 31^10 (about 8 * 10^14) code before it expires.
/// </para>
/// </summary>
/// <param name="show">
/// Where the line carrying the code goes, when that is not the log: the Linux host prints it to the container's output only, so the log file on the data
/// volume never holds a live code (it gets <c>server pairing code issued expires_s=600</c> instead).
/// </param>
/// <param name="setup">
/// The page's one-time setup code (SWITCHBOARD.md 10.4) rather than a pairing code: the same shape and pacing, good for <see cref="SetupLifetime"/>, printed as
/// <c>server setup code=XXXXX-XXXXX expires_s=</c> and logged as <c>server setup_code issued</c> (a word install scripts do not take for the code).
/// </param>
internal sealed class PairingCodes(TimeProvider time, Action<string> log, Action<string>? show = null, bool setup = false)
{
    /// <summary>How long a setup code is good for: a day, so one printed at the install is still good when the page is first opened.</summary>
    public static readonly TimeSpan SetupLifetime = TimeSpan.FromDays(1);

    private readonly TimeSpan life = setup ? SetupLifetime : Lifetime;
    private readonly string shownWords = setup ? "server setup code" : "server pairing code";
    private readonly string loggedWords = setup ? "server setup_code" : "server pairing code";

    /// <summary>How long this code is good for once issued.</summary>
    public TimeSpan CodeLifetime => life;

    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 10;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two codes checked, from any source.</summary>
    public static readonly TimeSpan GlobalSpacing = TryPacing.GlobalSpacing;
    /// <summary>The least time a source waits after a wrong code, for its first <see cref="FreeWrong"/> wrong codes.</summary>
    public static readonly TimeSpan SourceSpacing = TryPacing.SourceSpacing;
    /// <summary>Wrong codes from one source before its wait starts doubling (a person mistyping gets these at the plain pace).</summary>
    public const int FreeWrong = TryPacing.FreeWrong;
    /// <summary>The longest a source is made to wait.</summary>
    public static readonly TimeSpan LongestWait = TryPacing.LongestWait;
    /// <summary>How many sources are remembered at once; past it, a new source waits until one is forgotten.</summary>
    public const int MaxSources = TryPacing.MaxSources;

    private readonly object gate = new();
    private readonly TryPacing pacing = new(time, PacingRules.Code);
    private byte[]? code;
    private DateTimeOffset expires;

    /// <summary>Whether a code is open now (issued, not taken and not past its time).</summary>
    public bool Open
    {
        get
        {
            lock (gate) return code is not null && time.GetUtcNow() < expires;
        }
    }

    /// <summary>When the open code stops working; null when none is open.</summary>
    public DateTimeOffset? Expires
    {
        get
        {
            lock (gate) return code is not null && time.GetUtcNow() < expires ? expires : null;
        }
    }

    /// <summary>Opens a new code in place of any other, prints it, and returns it as printed.</summary>
    public string Issue()
    {
        var chars = new char[Length];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        lock (gate)
        {
            code = Encoding.ASCII.GetBytes(chars);
            expires = time.GetUtcNow() + life;
        }
        var shown = Display(chars);
        var line = $"{shownWords}={shown} expires_s={(int)life.TotalSeconds}";
        if (show is null) log(line);
        else
        {
            show(line);
            log($"{loggedWords} issued expires_s={(int)life.TotalSeconds}");
        }
        return shown;
    }

    /// <summary>The same as <see cref="Take(string?, IPAddress?, out bool)"/> from a source nobody else shares (the harness's own checks).</summary>
    public byte[]? Take(string? presented) => Take(presented, null, out _);

    /// <summary>
    /// Takes the open code if <paramref name="presented"/> is it, which closes it: one code, one connection. Returns null otherwise. <paramref name="paced"/>
    /// says the try came too soon (from anywhere, or from <paramref name="source"/>) and was not looked at; a wrong code makes its source wait.
    /// </summary>
    public byte[]? Take(string? presented, IPAddress? source, out bool paced)
    {
        paced = false;
        var candidate = Normalize(presented);
        lock (gate)
        {
            if (code is null) return null;
            var now = time.GetUtcNow();
            if (now >= expires)
            {
                code = null;
                log($"{loggedWords} closed reason=expired");
                return null;
            }
            if (!pacing.TryClaim(Key(source), out var claim))
            {
                // Not compared at all: nothing is learnt, and nothing about the waits moves, so knocking faster does not lengthen anyone's.
                paced = true;
                return null;
            }
            if (candidate is not null && CryptographicOperations.FixedTimeEquals(candidate, code))
            {
                pacing.Right(claim);
                var taken = code;
                code = null;
                log($"{loggedWords} closed reason=taken");
                return taken;
            }
            pacing.Wrong(claim);
            return null;
        }
    }

    /// <summary>How long a source waits after its <paramref name="wrong"/>th wrong code.</summary>
    public static TimeSpan Wait(int wrong) => TryPacing.Wait(wrong);

    /// <summary>
    /// What a peer's address counts as: the address itself for IPv4 (an IPv4-mapped IPv6 address included), its /64 for IPv6, except a tailnet device's
    /// (fd7a:115c:a1e0::/48, from the tailnet door), which counts as itself: Tailscale puts every device of a tailnet in one /64.
    /// </summary>
    public static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        return "v6:" + Convert.ToHexStringLower(bytes, 0, TailnetRelay.IsTailnet(address) ? 16 : 8);
    }

    /// <summary>Whether <paramref name="presented"/> is the code <paramref name="taken"/> (from <see cref="Take(string?)"/>), in fixed time.</summary>
    public static bool Same(byte[] taken, string? presented) => Normalize(presented) is { } candidate && CryptographicOperations.FixedTimeEquals(candidate, taken);

    // As typed: any case, with or without the dash or spaces. Anything that cannot be a code is no code (and still a wrong attempt).
    private static byte[]? Normalize(string? presented)
    {
        if (presented is null || presented.Length > 64) return null;
        var chars = presented.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray();
        if (chars.Length != Length || chars.Any(c => !Alphabet.Contains(c))) return null;
        return Encoding.ASCII.GetBytes(chars);
    }

    private static string Display(char[] chars) => new string(chars, 0, Length / 2) + "-" + new string(chars, Length / 2, Length - Length / 2);
}
