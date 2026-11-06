using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NativeBeeper.Protocol;

/// <summary>
/// What this PC runs, as a client's hello tells the server (<see cref="HelloFrame.Os"/>), for the server page's list of devices. Read once; anything that
/// cannot be read is left out rather than guessed.
/// </summary>
public sealed record ClientSystem(string? Os, string? OsVersion, string? Model)
{
    private static readonly Lazy<ClientSystem> current = new(Read);

    public static ClientSystem Current => current.Value;

    private static ClientSystem Read()
    {
        try { return OperatingSystem.IsWindows() ? ReadWindows() : new ClientSystem(null, null, null); }
        catch (Exception) { return new ClientSystem(null, null, null); }
    }

    [SupportedOSPlatform("windows")]
    private static ClientSystem ReadWindows()
    {
        var build = Environment.OSVersion.Version.Build;
        // Windows 11 still reports 10.0; its builds start at 22000. ProductName says "Windows 10" on 11 too, so the edition comes from EditionID.
        var os = build >= 22000 ? "Windows 11" : "Windows 10";
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = (nt?.GetValue("EditionID") as string) switch
        {
            "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
            "Professional" => "Pro",
            "ProfessionalWorkstation" => "Pro for Workstations",
            "ProfessionalEducation" => "Pro Education",
            "Enterprise" or "EnterpriseS" => "Enterprise",
            "Education" => "Education",
            _ => null,
        };
        if (edition is not null) os += " " + edition;
        var release = nt?.GetValue("DisplayVersion") as string;
        var revision = nt?.GetValue("UBR") is int ubr ? "." + ubr : "";
        var version = (string.IsNullOrWhiteSpace(release) ? "" : release + ", ") + "build " + build + revision;
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var maker = Clean(bios?.GetValue("SystemManufacturer") as string);
        var product = Clean(bios?.GetValue("SystemProductName") as string);
        var model = product is null ? null : maker is null || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? product : maker + " " + product;
        return new ClientSystem(os, version, model);
    }

    // Firmware fills these with placeholders on home-built PCs.
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase) || value.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
            || value.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase) || value.Trim() is "Default string" or "O.E.M." ? null : value.Trim();
}
