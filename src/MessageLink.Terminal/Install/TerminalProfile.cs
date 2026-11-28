using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Install;

/// <summary>
/// <c>mlink terminal-profile [--remove | --print [--packaged]]</c>: writes (or removes) Windows Terminal's "Message Link" profile fragment and its color
/// schemes, made from ConsoleThemes, for this user. The installer runs it after install; the Store package build prints it into the package
/// (Windows Terminal reads a packaged app's fragments from the folder its manifest names, not from LOCALAPPDATA).
/// </summary>
public static class TerminalProfile
{
    /// <summary>Windows Terminal's folder name for this app's fragments, under Fragments.</summary>
    public const string AppFolder = "Message Link";
    public const string FileName = "message-link.json";
    /// <summary>
    /// The UUIDv5 Windows Terminal itself derives for a fragment profile named "Message Link" from an app named "Message Link" (its documented scheme), fixed
    /// so a person's own changes to the profile, which Terminal keys by it, survive a reinstall. The Store package's fragment uses the same one.
    /// </summary>
    public const string ProfileGuid = "{2084d84d-d071-5db3-b60f-121174e97fca}";
    public const string ProfileName = "Message Link";
    /// <summary>What a packaged fragment runs: the package's execution alias, which Windows puts on the PATH (the package folder is not addressable).</summary>
    public const string PackagedCommandLine = "mlink.exe";
    /// <summary>A packaged fragment's icon, a file beside the JSON in the package's Public\Fragments (Windows Terminal 1.24 and later resolve it there).</summary>
    public const string PackagedIcon = "AppIcon.png";

    /// <param name="arguments">What followed <c>terminal-profile</c> on the command line.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(IReadOnlyList<string> arguments, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("mlink terminal-profile adds Message Link to Windows Terminal, so it is for Windows only.");
            return 0;
        }
        var processPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "mlink.exe");
        return Run(arguments, output, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), processPath, AppContext.BaseDirectory);
    }

    /// <summary>The same, with the folders given: <paramref name="localAppData"/> stands for %LOCALAPPDATA%, <paramref name="programDirectory"/> for the
    /// folder mlink.exe was installed in (where the icon is looked for).</summary>
    public static int Run(IReadOnlyList<string> arguments, TextWriter output, string localAppData, string programPath, string programDirectory)
    {
        bool remove = false, print = false, packaged = false;
        foreach (var argument in arguments)
            switch (argument.ToLowerInvariant())
            {
                case "--remove": remove = true; break;
                case "--print": print = true; break;
                case "--packaged": packaged = true; break;
                default:
                    output.WriteLine($"mlink terminal-profile: unknown option \"{argument}\". It takes --remove, or --print with --packaged.");
                    return 2;
            }
        if (remove && print || packaged && !print)
        {
            output.WriteLine("mlink terminal-profile: use --remove alone, or --print with or without --packaged.");
            return 2;
        }

        if (print)
        {
            output.Write(packaged ? Json(PackagedCommandLine, PackagedIcon) : Json(Quote(programPath), FindIcon(programDirectory)));
            return 0;
        }
        var folder = FragmentFolder(localAppData);
        try
        {
            if (remove)
            {
                if (!Directory.Exists(folder))
                {
                    output.WriteLine("Message Link was not in Windows Terminal.");
                    return 0;
                }
                Directory.Delete(folder, recursive: true);
                output.WriteLine("Removed Message Link from Windows Terminal.");
                return 0;
            }
            Write(folder, Json(Quote(programPath), FindIcon(programDirectory)));
            output.WriteLine("Added Message Link to Windows Terminal. Open a new tab to find it.");
            return 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Message Link could not be {(remove ? "removed from" : "added to")} Windows Terminal ({failure.GetType().Name}).");
            return 1;
        }
    }

    /// <summary>%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Message Link: Windows Terminal's documented place for a per-user install's fragments.</summary>
    public static string FragmentFolder(string localAppData) => Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", AppFolder);

    /// <summary>The icon shipped beside mlink.exe (the app's own, in the install folder's Assets), or null when there is none, as in a build folder.</summary>
    public static string? FindIcon(string programDirectory)
    {
        foreach (var name in new[] { "AppIcon.ico", "AppIcon.png" })
        {
            var path = Path.Combine(programDirectory, "Assets", name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    /// <summary>The scheme's name for a theme: the default is plain "Message Link", the others "Message Link Phosphor" and so on.</summary>
    public static string SchemeName(ConsoleTheme theme) =>
        theme.Name.Equals(ConsoleThemes.Default, StringComparison.OrdinalIgnoreCase) ? ProfileName
            : ProfileName + " " + char.ToUpperInvariant(theme.Name[0]) + theme.Name[1..];

    /// <summary>The fragment: one profile running <paramref name="commandLine"/>, and one color scheme per console theme.</summary>
    public static string Json(string commandLine, string? icon)
    {
        using var buffer = new MemoryStream();
        // Relaxed escaping keeps a path with letters outside ASCII readable; the file is only ever read by Windows Terminal as JSON.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteStartArray("profiles");
            json.WriteStartObject();
            json.WriteString("guid", ProfileGuid);
            json.WriteString("name", ProfileName);
            json.WriteString("commandline", commandLine);
            if (icon is not null) json.WriteString("icon", icon);
            json.WriteString("colorScheme", SchemeName(ConsoleThemes.Find(ConsoleThemes.Default)));
            json.WriteString("startingDirectory", "%USERPROFILE%");
            json.WriteBoolean("hidden", false);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartArray("schemes");
            foreach (var theme in ConsoleThemes.All) WriteScheme(json, theme);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\r\n") + "\r\n";
    }

    // Every one of the sixteen colors is required in a fragment's scheme. mlink draws in the theme's own colors, so these matter for the margins and for
    // anything written in the plain ANSI colors: each one is taken from the theme ink that means the same thing (red the error ink, green the ok ink), and
    // the two the themes have no ink for (blue, purple) are the accent turned to that hue at its own lightness, so they read on the surface as it does.
    private static void WriteScheme(Utf8JsonWriter json, ConsoleTheme theme)
    {
        var blue = Rotate(theme.Accent, 215);
        var purple = Rotate(theme.Accent, 285);
        json.WriteStartObject();
        json.WriteString("name", SchemeName(theme));
        json.WriteString("background", Hex(theme.Surface));
        json.WriteString("foreground", Hex(theme.Text));
        json.WriteString("cursorColor", Hex(theme.Accent));
        json.WriteString("selectionBackground", Hex(theme.Selection));
        json.WriteString("black", Hex(theme.Surface));
        json.WriteString("red", Hex(theme.Error));
        json.WriteString("green", Hex(theme.Ok));
        json.WriteString("yellow", Hex(theme.Warn));
        json.WriteString("blue", Hex(blue));
        json.WriteString("purple", Hex(purple));
        json.WriteString("cyan", Hex(theme.Accent));
        json.WriteString("white", Hex(theme.Text));
        json.WriteString("brightBlack", Hex(theme.Dim));
        json.WriteString("brightRed", Hex(theme.Error));
        json.WriteString("brightGreen", Hex(theme.Ok));
        json.WriteString("brightYellow", Hex(theme.Warn));
        json.WriteString("brightBlue", Hex(blue));
        json.WriteString("brightPurple", Hex(purple));
        json.WriteString("brightCyan", Hex(theme.Accent));
        json.WriteString("brightWhite", Hex(theme.Strong));
        json.WriteEndObject();
    }

    /// <summary>#RRGGBB from the themes' ARGB.</summary>
    public static string Hex(uint argb) => $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";

    /// <summary>The color with its hue set to <paramref name="hue"/> degrees, its saturation and lightness kept (a gray stays gray).</summary>
    public static uint Rotate(uint argb, double hue)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = hue / 60;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r1, g1, b1) = h switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = lightness - chroma / 2;
        static uint Channel(double value) => (uint)Math.Clamp(Math.Round(value * 255), 0, 255);
        return 0xFF000000 | Channel(r1 + m) << 16 | Channel(g1 + m) << 8 | Channel(b1 + m);
    }

    private static string Quote(string path) => path.Contains('"', StringComparison.Ordinal) ? path : "\"" + path + "\"";

    // Written beside and moved into place, so Windows Terminal (which watches the folder) never reads half a file.
    private static void Write(string folder, string json)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, FileName);
        var temporary = target + ".new";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
    }
}
