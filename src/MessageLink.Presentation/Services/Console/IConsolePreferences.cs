namespace MessageLink.Presentation.Services;

/// <summary>
/// The console's own preferences, kept for this install by the program around it (mlink's ConsolePreferences). Nothing here is shared with the server or
/// another device.
/// </summary>
public interface IConsolePreferences
{
    /// <summary>How much the console shows.</summary>
    ConsoleVerbosity Verbosity { get; set; }
    /// <summary>The terminal color scheme by name (ConsoleThemes).</summary>
    string Theme { get; set; }
    /// <summary>When the console was last opened before this time, for the MOTD's "Last login".</summary>
    DateTimeOffset? LastOpened { get; set; }
    /// <summary>Saved aliases, name to command line. Kept protected: they can hold names.</summary>
    IReadOnlyDictionary<string, string> Aliases { get; }
    Task SaveAliasesAsync(IReadOnlyDictionary<string, string> aliases);
    /// <summary>The input history the session appends to while it runs, kept for the next session.</summary>
    List<string> Inputs { get; }
}
