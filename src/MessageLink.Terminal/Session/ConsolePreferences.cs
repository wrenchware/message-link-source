using System.Globalization;
using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Terminal.Session;

/// <summary>
/// The console's own preferences in mlink's folder, for this install only: the color scheme, how much it shows and when it was last opened in
/// <c>console.json</c> (no name, text or id in it), and the aliases and the input history sealed with the context's protector, since both can hold names
/// and words someone typed. Nothing here is shared with the server or any other device.
/// </summary>
internal sealed class ConsolePreferences : IConsolePreferences
{
    public const string SettingsFile = "console.json", AliasesFile = "console-aliases.sealed", InputsFile = "console-inputs.sealed";
    private const long MaxSealedBytes = 1 << 20;

    private readonly string folder;
    private readonly IStateProtector protector;
    private readonly Action<string> log;
    private Settings settings = new();
    private Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Settings(string? Theme = null, string? Verbosity = null, DateTimeOffset? LastOpened = null);

    private ConsolePreferences(string folder, IStateProtector protector, Action<string> log)
    {
        this.folder = folder; this.protector = protector; this.log = log;
    }

    /// <summary>The input history the session appends to while it runs; saved, capped, when the session ends.</summary>
    public List<string> Inputs { get; } = [];

    public static async Task<ConsolePreferences> LoadAsync(string folder, IStateProtector protector, Action<string> log)
    {
        var preferences = new ConsolePreferences(folder, protector, log);
        try
        {
            var path = Path.Combine(folder, SettingsFile);
            if (File.Exists(path) && new FileInfo(path).Length < 64 * 1024)
                preferences.settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllBytesAsync(path)) ?? new();
        }
        // An unreadable file is the defaults: these are conveniences, never worth refusing to start for.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { log($"console preferences unreadable error={failure.GetType().Name}"); }
        if (await preferences.UnsealAsync<Dictionary<string, string>>(AliasesFile) is { } saved)
            preferences.aliases = new(saved, StringComparer.OrdinalIgnoreCase);
        if (await preferences.UnsealAsync<List<string>>(InputsFile) is { } inputs)
            preferences.Inputs.AddRange(inputs.TakeLast(ConsoleSessionViewModel.HistoryLimit));
        return preferences;
    }

    public string Theme
    {
        get => settings.Theme ?? ConsoleThemes.Default;
        set { settings = settings with { Theme = value }; SaveSettings(); }
    }

    public ConsoleVerbosity Verbosity
    {
        get => Enum.TryParse<ConsoleVerbosity>(settings.Verbosity, out var saved) ? saved : ConsoleVerbosity.Normal;
        set { settings = settings with { Verbosity = value.ToString() }; SaveSettings(); }
    }

    public DateTimeOffset? LastOpened
    {
        get => settings.LastOpened;
        set { settings = settings with { LastOpened = value }; SaveSettings(); }
    }

    public IReadOnlyDictionary<string, string> Aliases => aliases;

    public async Task SaveAliasesAsync(IReadOnlyDictionary<string, string> next)
    {
        aliases = new(next, StringComparer.OrdinalIgnoreCase);
        var saved = await SealAsync(AliasesFile, aliases);
        log($"console aliases saved count={aliases.Count.ToString(CultureInfo.InvariantCulture)} outcome={(saved ? "ok" : "failed")}");
    }

    public async Task SaveInputsAsync()
    {
        var kept = Inputs.TakeLast(ConsoleSessionViewModel.HistoryLimit).ToList();
        var saved = await SealAsync(InputsFile, kept);
        log($"console inputs saved count={kept.Count.ToString(CultureInfo.InvariantCulture)} outcome={(saved ? "ok" : "failed")}");
    }

    // Small and rare (a theme or a verbosity chosen by hand), so written at once, beside the file and moved over it.
    private void SaveSettings()
    {
        try { Replace(SettingsFile, JsonSerializer.SerializeToUtf8Bytes(settings)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"console preferences save failed error={failure.GetType().Name}"); }
    }

    private async Task<T?> UnsealAsync<T>(string name) where T : class
    {
        var path = Path.Combine(folder, name);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxSealedBytes) return null;
            var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path));
            try { return JsonSerializer.Deserialize<T>(plain); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        }
        // Another user's file, a damaged one or one sealed with a key that is gone: none, said by kind alone.
        catch (Exception failure) { log($"console sealed unreadable file={name} error={failure.GetType().Name}"); return null; }
    }

    private async Task<bool> SealAsync<T>(string name, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            Replace(name, await protector.ProtectAsync(plain));
            return true;
        }
        catch (Exception failure) { log($"console sealed save failed file={name} error={failure.GetType().Name}"); return false; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    private void Replace(string name, byte[] bytes)
    {
        var path = Path.Combine(folder, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
