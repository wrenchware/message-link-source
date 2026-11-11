namespace NativeBeeper.Presentation.Services;

/// <summary>
/// The console's colour schemes. The console is always a dark terminal surface whatever the Windows theme (high contrast
/// excepted, which uses the system's colours); a scheme changes the surface and text inks, never the service tag colours,
/// which stay each service's own. Every ink is chosen to read at 4.5:1 or better on its own surface.
/// </summary>
/// <param name="Surface">The terminal background.</param>
/// <param name="Bar">The status line and app title bars, a step off the surface.</param>
/// <param name="Text">Ordinary output.</param>
/// <param name="Dim">Times, hints, rules and secondary words.</param>
/// <param name="Accent">The prompt and headings.</param>
/// <param name="Selection">The selected row of a console app.</param>
public sealed record ConsoleTheme(string Name, string Description, uint Surface, uint Bar, uint Text, uint Dim, uint Accent,
    uint Ok, uint Warn, uint Error, uint Strong, uint Match, uint Selection);

public static class ConsoleThemes
{
    public const string Default = "campbell";

    // ARGB. Campbell is Windows Terminal's own default; the others are the classic terminals people still pick.
    public static IReadOnlyList<ConsoleTheme> All { get; } =
    [
        new("campbell", "Windows Terminal's near-black", 0xFF0C0C0C, 0xFF1B1B1B, 0xFFCCCCCC, 0xFF8A8A8A, 0xFF61D6D6,
            0xFF16C60C, 0xFFF9F1A5, 0xFFF0585D, 0xFFF2F2F2, 0xFFF9F1A5, 0xFF26343A),
        new("phosphor", "green screen, P1 phosphor", 0xFF050A05, 0xFF0D190D, 0xFF33FF66, 0xFF1FA344, 0xFF9DFFB4,
            0xFF7CFF9A, 0xFFE6FF66, 0xFFFF6B5E, 0xFFCCFFD9, 0xFFE6FF66, 0xFF143A1E),
        new("amber", "amber monochrome, P3 phosphor", 0xFF0D0802, 0xFF1C1206, 0xFFFFB000, 0xFFB37B00, 0xFFFFCC4D,
            0xFFFFD27A, 0xFFFFE9B3, 0xFFFF7A59, 0xFFFFE2A8, 0xFFFFE9B3, 0xFF3A2808),
        new("ice", "cool blue-black", 0xFF0A0E14, 0xFF151B24, 0xFFB3C3D6, 0xFF6F8399, 0xFF7FC8FF,
            0xFF7FD962, 0xFFFFD580, 0xFFFF7A85, 0xFFE6EEF7, 0xFFFFD580, 0xFF1F3347),
        new("mono", "black and white, no color but the services'", 0xFF000000, 0xFF161616, 0xFFD9D9D9, 0xFF8C8C8C, 0xFFFFFFFF,
            0xFFD9D9D9, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFF333333),
    ];

    public static ConsoleTheme Find(string? name) =>
        All.FirstOrDefault(theme => theme.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool Exists(string? name) => All.Any(theme => theme.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
