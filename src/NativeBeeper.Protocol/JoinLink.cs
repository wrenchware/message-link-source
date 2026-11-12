using System.Text;

namespace NativeBeeper.Protocol;

/// <summary>
/// What "Add a phone" and "Add a PC" hand a new device (SWITCHBOARD.md 10.4): the server's machine name on its Tailscale network, its address on the home network,
/// the SHA-256 of its certificate (pinned before anything is sent), a one-time pairing code, and the Tailscale account its door signed in with when it knows it.
/// <para>
/// The phone reads it as a link, <c>https://wrenchware.github.io/message-link/join#v=1&amp;n=…&amp;a=…&amp;f=…&amp;c=…&amp;u=…</c> (the phone's JoinLink.kt reads the same
/// form), shown as a QR code. The values are in the fragment, so the pairing code never reaches a web server. A PC takes the link pasted, or the shorter line
/// <see cref="Line"/> makes (address, fingerprint and code), into its server dialog.
/// </para>
/// </summary>
public static class JoinLink
{
    /// <summary>Where the link points: the website's join page, which says to open it in Messages.</summary>
    public const string Page = "https://wrenchware.github.io/message-link/join";

    /// <summary>The website's pictured guide to the Firebase project a phone's notifications need, which the server's page links to.</summary>
    public const string FirebaseGuide = "https://wrenchware.github.io/message-link/firebase/";

    public const string SetupGuide = "https://wrenchware.github.io/message-link/setup/";

    /// <summary>The original link version; a non-default Tailscale port needs version 2 so older phones ask for an update.</summary>
    public const string Version = "1";

    /// <summary>What a link or a pasted line held. <paramref name="Address"/> or <paramref name="TailnetName"/> is there, or both.</summary>
    /// <param name="Address">The home network's address, <c>host</c> or <c>host:port</c>.</param>
    /// <param name="Fingerprint">64 lowercase hex characters.</param>
    /// <param name="Code">The pairing code as the server prints it, <c>XXXXX-XXXXX</c>.</param>
    public sealed record Parsed(string? TailnetName, string? Address, string Fingerprint, string Code, string? TailscaleUser, int TailnetPort = NetworkEndpoint.DefaultPort)
    {
        public string? TailnetAddress => TailnetName is null ? null : TailnetPort == NetworkEndpoint.DefaultPort ? TailnetName : $"{TailnetName}:{TailnetPort}";
    }

    /// <summary>The link for a phone. Each value is percent-encoded; a missing one is left out.</summary>
    public static string Build(string? tailnetName, string? address, string fingerprint, string code, string? tailscaleUser, int tailnetPort = NetworkEndpoint.DefaultPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailnetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailnetPort, 65535);
        var customPort = !string.IsNullOrWhiteSpace(tailnetName) && tailnetPort != NetworkEndpoint.DefaultPort;
        var link = new StringBuilder(Page).Append("#v=").Append(customPort ? "2" : Version);
        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            link.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }
        Add("n", tailnetName);
        Add("a", address);
        Add("f", fingerprint.ToLowerInvariant());
        Add("c", code);
        Add("u", tailscaleUser);
        if (customPort) Add("p", tailnetPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return link.ToString();
    }

    /// <summary>The line for a PC: the address, the fingerprint and the code, separated by spaces, which <see cref="Parse"/> reads back by their shapes.</summary>
    public static string Line(string address, string fingerprint, string code) => $"{address.Trim()} {fingerprint.ToLowerInvariant()} {code}";

    /// <summary>
    /// A pasted link (<see cref="Build"/>) or line (<see cref="Line"/>), or null when the text is neither. In a line the parts may come in any order, each told by its shape:
    /// 64 hex characters (colons allowed) are the fingerprint, ten characters of the code's alphabet (a dash allowed) the code, and the rest the address.
    /// </summary>
    public static Parsed? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2048) return null;
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? ParseLink(trimmed) : ParseLine(trimmed);
    }

    private static Parsed? ParseLink(string link)
    {
        var hash = link.IndexOf('#');
        if (hash < 0) return null;
        var page = link[..hash];
        if (page.Contains('?')) page = page[..page.IndexOf('?')];
        if (!string.Equals(page.TrimEnd('/'), Page, StringComparison.OrdinalIgnoreCase)) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in link[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            string value;
            try { value = Uri.UnescapeDataString(equals < 0 ? "" : pair[(equals + 1)..]); }
            catch (UriFormatException) { return null; }
            values.TryAdd(key, value);
        }
        if (!values.TryGetValue("v", out var version) || version is not (Version or "2")) return null;
        var port = NetworkEndpoint.DefaultPort;
        if (version == "2" && (!int.TryParse(values.GetValueOrDefault("p"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return null;
        var name = Blank(values.GetValueOrDefault("n"));
        var address = Blank(values.GetValueOrDefault("a"));
        if (name is null && address is null) return null;
        if (Fingerprint(values.GetValueOrDefault("f")) is not { } fingerprint || PairingCode(values.GetValueOrDefault("c")) is not { } code) return null;
        return new(name, address, fingerprint, code, Blank(values.GetValueOrDefault("u")), port);
    }

    private static Parsed? ParseLine(string line)
    {
        string? address = null, fingerprint = null, code = null;
        foreach (var raw in line.Split((char[])[' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ? raw[6..].TrimEnd('/') : raw;
            if (fingerprint is null && Fingerprint(part) is { } pin) fingerprint = pin;
            else if (code is null && PairingCode(part) is { } found) code = found;
            else if (address is null && part.Length > 0 && !part.Any(c => c is '/' or '@' or '#' or '?')) address = part;
            else return null;
        }
        return address is not null && fingerprint is not null && code is not null ? new(null, address, fingerprint, code, null) : null;
    }

    /// <summary>A fingerprint as hex (colons allowed, any case), as lowercase hex; null when it is not one.</summary>
    public static string? Fingerprint(string? text) => NetworkEndpoint.ParseFingerprint(text) is { } bytes ? Convert.ToHexStringLower(bytes) : null;

    /// <summary>A pairing code as typed (any case, with or without its dash), as the server prints it; null when it is not one.</summary>
    public static string? PairingCode(string? text)
    {
        if (text is null) return null;
        var plain = new string([.. text.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
        const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        if (plain.Length != 10 || plain.Any(c => !Alphabet.Contains(c))) return null;
        return plain[..5] + "-" + plain[5..];
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
