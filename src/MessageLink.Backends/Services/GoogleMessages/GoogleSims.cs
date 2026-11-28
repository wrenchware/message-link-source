using System.Text.Json;

namespace MessageLink.App.Services.GoogleMessages;

/// <summary>One of a dual-SIM phone's SIMs as the composer's chip names it: "SIM 2 · Carrier", and its colour.</summary>
internal sealed record GoogleSim(string Label, string? Color)
{
    /// <summary>What the chip's tooltip and screen readers say.</summary>
    public string Hint => $"Sends from {Label}. Change the SIM for this conversation on your phone.";
}

/// <summary>
/// Which SIM each Google Messages conversation sends from, for a phone with more than one (2026-10-04). Display only:
/// the phone decides, and nothing here changes a conversation's SIM.
/// </summary>
/// <remarks>
/// The helper lists the phone's SIMs once, keyed by each SIM's own participant id (sims.go), and a conversation names its
/// SIM by that id (GoogleChat.OutgoingId), so a conversation listed before the phone's settings arrived still finds its
/// SIM. Only a SIM's place, carrier name and colour arrive; never its number. A phone with one SIM sends an empty list,
/// and an empty list shows nothing. A new pairing may be another phone, so it starts over empty.
/// </remarks>
internal sealed class GoogleSims
{
    private readonly object sync = new();
    private Dictionary<string, GoogleSim> byId = new(StringComparer.Ordinal);

    public int Count { get { lock (sync) return byId.Count; } }

    /// <summary>The SIM a conversation sends from, or null for a single-SIM phone or a SIM not listed.</summary>
    public GoogleSim? For(string? outgoingId)
    {
        if (string.IsNullOrEmpty(outgoingId)) return null;
        lock (sync) return byId.GetValueOrDefault(outgoingId);
    }

    /// <summary>Takes a sims event from the helper. True when what is shown changed.</summary>
    public bool Apply(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("sims", out var listed) || listed.ValueKind != JsonValueKind.Array) return false;
        var next = new Dictionary<string, GoogleSim>(StringComparer.Ordinal);
        foreach (var sim in listed.EnumerateArray())
        {
            if (sim.ValueKind != JsonValueKind.Object) continue;
            var id = sim.TryGetProperty("id", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            var slot = sim.TryGetProperty("slot", out var place) && place.TryGetInt32(out var number) ? number : 0;
            if (id is not { Length: > 0 and <= 64 } || slot is < 1 or > 8) continue;
            var carrier = sim.TryGetProperty("carrier", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            var color = sim.TryGetProperty("color", out var tint) && tint.ValueKind == JsonValueKind.String ? Color(tint.GetString()) : null;
            next[id] = new(Label(slot, carrier), color);
        }
        // One SIM is nothing to tell apart.
        if (next.Count < 2) next.Clear();
        return Replace(next);
    }

    /// <summary>A new pairing, which may be another phone. True when something was shown.</summary>
    public bool Forget() => Replace(new(StringComparer.Ordinal));

    private bool Replace(Dictionary<string, GoogleSim> next)
    {
        lock (sync)
        {
            if (next.Count == byId.Count && next.All(pair => byId.TryGetValue(pair.Key, out var was) && was == pair.Value)) return false;
            byId = next;
            return true;
        }
    }

    /// <summary>"SIM 1", or "SIM 1 · Carrier". A carrier name that looks like a phone number is left out (the helper does too).</summary>
    internal static string Label(int slot, string? carrier)
    {
        var name = new string((carrier ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Count(char.IsAsciiDigit) >= 5) name = "";
        if (name.Length > 32) name = name[..32].TrimEnd();
        return name.Length == 0 ? $"SIM {slot}" : $"SIM {slot} · {name}";
    }

    /// <summary>The colour as #RRGGBB, or null for anything else.</summary>
    internal static string? Color(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(char.IsAsciiHexDigit) ? hex.ToUpperInvariant() : null;
}
