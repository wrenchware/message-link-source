using System.Text.Json;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// The newest release as tools\Copy-Installer.ps1 describes it in latest.json, in the folder installers are shared from:
/// {"version":"0.1.366","installer":"Messages-Setup-0.1.366.exe","sha256":"…","published":"2026-10-03"}. Only the version
/// is required; anything else that does not read cleanly is left out rather than guessed.
/// </summary>
public sealed record UpdateManifest(Version Version, string? Installer, string? Sha256, DateOnly? Published)
{
    // A day between automatic checks: a release is at most a day or so old before it is noticed, and the folder is asked
    // nothing more often than that.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static UpdateManifest? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = document.RootElement;
            if (ReadVersion(String(root, "version")) is not { } version) return null;
            var sha = String(root, "sha256") is { Length: 64 } hash && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
            var installer = String(root, "installer") is { } name && name.StartsWith("Messages-Setup-", StringComparison.Ordinal) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(['/', '\\']) < 0 ? name : null;
            DateOnly? published = DateOnly.TryParseExact(String(root, "published"), "yyyy-MM-dd", out var day) ? day : null;
            return new(version, installer, sha, published);
        }
        // Drive answers a link it will not serve with a sign-in page, which is HTML: not an update, and not an error to show.
        catch (JsonException) { return null; }
    }

    // Three dotted numbers, as the app's own version is shown (Major.Minor.Build); a fourth part is ignored.
    public static Version? ReadVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('.');
        if (parts.Length is < 3 or > 4) return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new(numbers[0], numbers[1], numbers[2]);
    }

    public bool IsNewerThan(Version current) => Version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    // Due when never checked, a day after the last check, or when the clock has gone back past it (a corrected clock must
    // not silence checks for days).
    public static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked is not { } last || now - last >= CheckInterval || last > now + TimeSpan.FromMinutes(5);

    public static string Label(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
