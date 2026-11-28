using System.Globalization;

namespace MessageLink.Presentation.Services;

public static class AvatarText
{
    private static readonly (byte R, byte G, byte B)[] Palette =
        // Restrained Fluent-like tones with similar brightness, rather than saturated provider-brand colors.
        // Each remains dark enough for the white initials used by ChatAvatar in both app themes.
        [(0x3B, 0x5F, 0x8A), (0x52, 0x67, 0x7A), (0x3F, 0x64, 0x70), (0x59, 0x6B, 0x5B),
         (0x55, 0x5F, 0x7D), (0x6B, 0x5D, 0x73), (0x46, 0x68, 0x72), (0x5F, 0x6A, 0x72)];

    // The name's first letter. Names without letters (phone numbers) get none.
    public static string Initials(string? name)
    {
        var letter = (name ?? "").FirstOrDefault(char.IsLetter);
        return letter == default ? "" : char.ToUpperInvariant(letter).ToString();
    }

    // Parses a supplied color for callers that need it; ChatAvatar intentionally requests the stable app palette.
    public static (byte R, byte G, byte B) Color(string? hex, string? seed)
    {
        var value = hex?.Trim().TrimStart('#') ?? "";
        if (value.Length is 6 or 8 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var hash = 17;
        foreach (var ch in seed ?? "") hash = unchecked(hash * 31 + ch);
        return Palette[(int)((uint)hash % Palette.Length)];
    }
}
