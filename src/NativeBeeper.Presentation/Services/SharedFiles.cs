namespace NativeBeeper.Presentation.Services;

// Files handed to Messages from outside it: Explorer's Send to menu (a shortcut the installer puts there, which adds
// --share before the paths), or files dropped on Messages.exe or a shortcut to it (the paths alone). The Windows Share
// sheet lists only packaged apps, and Messages is not one, so these are the ways in. Nothing here sends: a share ends
// staged in the composer of a conversation the user picks, and the user presses Send.
public sealed record ShareRequest(IReadOnlyList<string> Paths)
{
    public const string Argument = "--share";
    // More than any share could stage; the rest are counted, not kept, so a huge selection is refused as too many.
    public const int KeptPaths = 64;

    // How many paths arrived, kept or not.
    public int Offered { get; init; }

    // Arguments without the program's own path (Environment.GetCommandLineArgs().Skip(1)). A path naming this program is
    // never a share: a launch passed on to a relaunch carries the program's path at its head (Program.StartAgain).
    public static ShareRequest? Parse(IEnumerable<string> arguments, string programFileName)
    {
        var shared = false;
        var paths = new List<string>();
        var offered = 0;
        foreach (var raw in arguments)
        {
            var argument = raw?.Trim().Trim('"') ?? "";
            if (argument.Length == 0) continue;
            if (argument == Argument) { shared = true; continue; }
            // Every other launch argument is a flag (--open-chat=…, --background, ----AppNotificationActivated:, -Embedding).
            if (argument.StartsWith('-')) continue;
            if (IsProgram(argument, programFileName)) continue;
            // Explorer hands over full paths. Anything else after --share is kept so that it is refused as not found,
            // rather than read relative to whichever folder this process happened to start in.
            if (!shared && !Path.IsPathFullyQualified(argument)) continue;
            offered++;
            if (paths.Count < KeptPaths && !paths.Contains(argument, StringComparer.OrdinalIgnoreCase)) paths.Add(argument);
        }
        return paths.Count == 0 ? null : new ShareRequest(paths) { Offered = offered };
    }

    // A whole command line, as a redirected launch carries it, with or without the program's path at its head.
    public static ShareRequest? ParseCommandLine(string? commandLine, string programFileName)
        => commandLine is null ? null : Parse(Split(commandLine), programFileName);

    private static bool IsProgram(string argument, string programFileName)
    {
        try { return programFileName.Length > 0 && string.Equals(Path.GetFileName(argument), programFileName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    // Windows' own rules for splitting a command line into arguments (those GetCommandLineArgs uses): whitespace separates
    // arguments outside quotes; 2n backslashes before a quote are n backslashes and the quote opens or closes; 2n+1 are n
    // backslashes and a literal quote; backslashes anywhere else are themselves; a doubled quote inside quotes is one quote.
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var run = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { run++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1) current.Append('"');
                    else { quoted = !quoted; }
                }
                else { current.Append('\\', run); i--; }
                started = true;
                continue;
            }
            if (c == '"')
            {
                if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
                started = true;
                continue;
            }
            if (!quoted && (c == ' ' || c == '\t' || c == '\r' || c == '\n'))
            {
                if (started) { arguments.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            current.Append(c);
            started = true;
        }
        if (started) arguments.Add(current.ToString());
        return arguments;
    }
}

public enum SharedKind { Photo, File, Link }

// One shared path as it will be staged: a photo or other file attached as it is, or the web address an internet shortcut
// (.url) holds, which goes into the composer as text.
public sealed record SharedItem(SharedKind Kind, string Path, string? Text = null);

// What a share holds once looked at, or why it cannot be staged at all. Refusal is shown to the user as it is; Reason is a
// fixed word for the log. A share is staged whole or not at all.
public sealed record ShareCheck(IReadOnlyList<SharedItem> Items, string? Refusal = null, string Reason = "")
{
    public int Photos => Items.Count(item => item.Kind == SharedKind.Photo);
    public int Files => Items.Count(item => item.Kind == SharedKind.File);
    public int Links => Items.Count(item => item.Kind == SharedKind.Link);
    public IReadOnlyList<string> AttachmentPaths => [.. Items.Where(item => item.Kind != SharedKind.Link).Select(item => item.Path)];
    public string? Text => Links == 0 ? null : string.Join("\r", Items.Where(item => item.Kind == SharedKind.Link).Select(item => item.Text));

    // "2 photos and 1 link", for the conversation picker.
    public string Describe()
    {
        var parts = new List<string>();
        if (Photos > 0) parts.Add(Photos == 1 ? "1 photo" : $"{Photos} photos");
        if (Files > 0) parts.Add(Files == 1 ? "1 file" : $"{Files} files");
        if (Links > 0) parts.Add(Links == 1 ? "1 link" : $"{Links} links");
        return parts.Count switch { 0 => "", 1 => parts[0], _ => string.Join(", ", parts[..^1]) + " and " + parts[^1] };
    }

    // "photos=2 files=0 links=1" and the refusal's word: counts and kinds only, never a name or a path.
    public string LogFields => $"photos={Photos} files={Files} links={Links}" + (Refusal is null ? "" : $" refused={Reason}");
}

// Looks at each shared path on disk, off the interface thread. The limits are the composer's own (AttachmentBatchRules):
// up to ten photos together, or one other file, each under 50 MB. Kinds are told here by extension so that a share which
// cannot be staged is refused before a conversation is picked for it; staging still reads each file's real type and
// applies the same rules again, and the service's own check after that.
public static class ShareInspection
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp" };
    // A .url is a few lines; anything this long is not one Messages will read.
    private const long ShortcutLimit = 64 * 1024;

    public static ShareCheck Inspect(ShareRequest request)
    {
        if (request.Offered > AttachmentBatchRules.ItemLimit || request.Paths.Count > AttachmentBatchRules.ItemLimit)
            return Refuse($"Share up to {AttachmentBatchRules.ItemLimit} photos at a time.", "too_many");
        var items = new List<SharedItem>();
        var sizes = new List<long>();
        foreach (var path in request.Paths)
        {
            if (!Path.IsPathFullyQualified(path)) return Missing(request);
            if (Directory.Exists(path)) return Refuse("Messages can attach files, not folders.", "folder");
            FileInfo file;
            try { file = new FileInfo(path); } catch (Exception) { return Missing(request); }
            if (!file.Exists) return Missing(request);
            var extension = file.Extension;
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return Refuse("Share the file itself rather than a shortcut to it.", "shortcut");
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadLink(file) is not { } link) return Refuse("That shortcut doesn't hold a web link Messages can add.", "link");
                items.Add(new(SharedKind.Link, path, link));
                continue;
            }
            items.Add(new(PhotoExtensions.Contains(extension) ? SharedKind.Photo : SharedKind.File, path));
            sizes.Add(file.Length);
        }
        var check = new ShareCheck(items);
        if (check.Files > 1 || check.Files == 1 && check.Photos > 0)
            return Refuse("Share photos together, or one other file at a time.", "mixed");
        if (sizes.Any(size => size > AttachmentBatchRules.ItemBytesLimit)) return Refuse("Share files under 50 MB each.", "too_large");
        if (sizes.Sum() > AttachmentBatchRules.BatchBytesLimit) return Refuse("Keep the photos under 100 MB together.", "too_large");
        return check;
    }

    private static ShareCheck Missing(ShareRequest request) => Refuse(request.Paths.Count == 1
        ? "Messages couldn't find the shared file." : "Messages couldn't find one of the shared files.", "missing");
    private static ShareCheck Refuse(string text, string reason) => new([], text, reason);

    private static string? ReadLink(FileInfo file)
    {
        try { return file.Length <= ShortcutLimit ? LinkFromShortcut(File.ReadAllText(file.FullName)) : null; }
        catch (Exception) { return null; }
    }

    // The URL= line of an internet shortcut's [InternetShortcut] section, when it is a web address. Anything else a .url
    // can point at (file:, a program's own scheme, script) is not added.
    public static string? LinkFromShortcut(string content)
    {
        var inSection = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || !line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[4..].Trim();
            return value.Length is > 0 and <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? value : null;
        }
        return null;
    }
}
