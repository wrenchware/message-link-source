using System.Text.Json;
using MessageLink.App.Services;

namespace MessageLink.Server.Windows;

/// <summary>
/// The one preference the server honours, read from the file the app writes it to (<c>app-preferences.json</c>, AppPreferences). The logs are shared
/// code that reads only <see cref="Diagnostics.Recording"/>, which is true until a host lowers it, so a server that never read the choice would
/// record for someone who turned diagnostics off.
/// </summary>
internal static class ServerPreferences
{
    public const string FileName = "app-preferences.json";

    /// <summary>Lowers <see cref="Diagnostics.Recording"/> when the saved choice says off. Before the first line of any log, and so before a refusal is written.</summary>
    public static void Apply() => Diagnostics.Recording = RecordDiagnostics(AppPaths.Combine(FileName));

    /// <summary>
    /// On when never chosen (the app's default). A file that exists and cannot be read counts as off: the app reads a torn file as all defaults, but
    /// the app is the one that can say what was meant, and the server records nothing until it can.
    /// </summary>
    public static bool RecordDiagnostics(string file)
    {
        try
        {
            if (!File.Exists(file)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return !(document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("RecordDiagnostics", out var value) && value.ValueKind == JsonValueKind.False);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}
