namespace MessageLink.Presentation.Services;

// Settings and Unread are never parsed from a command line: the shell uses them to bring Messages forward on a screen
// (the tray menu, the console). Appended, so the parsed three keep their values.
public enum LaunchAction { NewChat, Search, OpenChat, Settings, Unread, SignIn }

// Command-line actions used by the taskbar jump list. A running app receives them through activation redirection. SignIn is the PC's own Messages Server asking
// for an account's native sign-in window (Connect on its page on this PC); Service names the account by the server's own word.
public sealed record LaunchCommand(LaunchAction Action, string? ChatId = null, string? Service = null)
{
    private const string SignInPrefix = "--sign-in=";

    /// <summary>The services a sign-in can be asked for, by the server's words (ServerAccountProviders).</summary>
    public static readonly IReadOnlyList<string> SignInServices = ["google-messages", "messenger", "instagram", "google-voice"];

    public static string SignInArgument(string service)
        => SignInServices.Contains(service) ? SignInPrefix + service : throw new ArgumentException("Unsupported service.", nameof(service));

    public const string NewChatArgument = "--new-chat";
    public const string SearchArgument = "--search";
    private const string OpenChatPrefix = "--open-chat=";
    // Started at sign-in: Messages connects and notifies but shows no window until the user asks for it. It is not a
    // command, so Parse returns null for it.
    public const string BackgroundArgument = "--background";

    // Asked by tools (tools\Stop-MessageLink.ps1) before a build or an install: the running Messages quits as the tray's Quit
    // does, so the next launch does not take it for a crash. Not a command either; only Program.Main and App act on it.
    public const string QuitArgument = "--quit";
    // A person asking, from the taskbar's jump list: the tray's Quit, which shows what is keeping it open or asks about unsaved messages. --quit refuses those cases in the log
    // alone, for a tool to go on from, and for a person that was a menu item that did nothing (audit, 2026-11-01).
    public const string QuitFromMenuArgument = "--quit-from-menu";
    // Asked by tools (tools\Stop-MessageLink.ps1) through a Store package's execution alias: the server beside this program is asked to quit, and its answer is the exit
    // code. A packaged MessageLinkServer.exe started by its path runs without the package's identity, so its --quit looks for another data root's server (#131); this
    // program started by the alias has the identity, and the server's --quit it starts inherits it. Not a command either; only Program.Main acts on it.
    public const string QuitServerArgument = "--quit-server";

    // Started again by Windows after a Microsoft Store update replaced the running app (WindowsStoreUpdates.RegisterRestart),
    // whose window was open: a background launch that then shows the window as it was (owner, 2026-11-14: an update keeps
    // the last state). Restored shows it without activating it, so nothing is read until the person turns to it: the
    // Store's own updates come whenever it likes. RestoredFront is for the person's own Restart to update, made from the
    // open window: it comes forward as opening it from the tray does. Not commands either.
    public const string RestoredArgument = "--restored";
    public const string RestoredFrontArgument = "--restored-front";

    // Asked by this PC's own server, from Manage server in its icon's menu while no window is open (owner, 2026-11-22): Message Link starts in the tray, as at
    // sign-in, and opens only the server's page window. Not a command either.
    public const string ManageServerArgument = "--manage-server";

    public static bool IsManageServer(IEnumerable<string> arguments)
        => arguments.Any(raw => raw is not null && raw.Trim('"') == ManageServerArgument);

    // Exactly the argument, quoted or not; "--background=1" and "--backgroundx" are not it.
    public static bool IsBackground(IEnumerable<string> arguments)
        => arguments.Any(raw => raw is not null && raw.Trim('"') == BackgroundArgument);

    // Which restored launch this is (RestoredArgument or RestoredFrontArgument), or null for any other.
    public static string? Restored(IEnumerable<string> arguments)
        => arguments.Select(raw => raw?.Trim('"')).FirstOrDefault(argument => argument is RestoredArgument or RestoredFrontArgument);
    // A redirected launch's whole command line is this one argument, with or without the program's path at its head, split
    // as Windows splits it. Splitting on spaces found " --quit " inside a shared file's name (Send to "a --quit b.txt")
    // and quit instead of staging the file; the same went for --background, which dropped the file.
    public static bool IsOnly(string? commandLine, string argument, string programFileName)
    {
        if (commandLine is null) return false;
        var parts = ShareRequest.Split(commandLine).ToList();
        // Up to and including the program's own path, which an unquoted path with a space in it splits in two.
        var program = parts.FindIndex(part => IsProgram(part, programFileName));
        if (program >= 0 && program < parts.Count - 1) parts.RemoveRange(0, program + 1);
        return parts is [var only] && only == argument;
    }

    private static bool IsProgram(string argument, string programFileName)
    {
        try { return programFileName.Length > 0 && string.Equals(Path.GetFileName(argument), programFileName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    public static string OpenChatArgument(string chatId)
        => IsChatId(chatId) ? OpenChatPrefix + chatId : throw new ArgumentException("Unsupported chat id.", nameof(chatId));

    // Accepts a whole command line, with or without the executable path.
    public static LaunchCommand? Parse(string? commandLine)
        => commandLine is null ? null : Parse(commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static LaunchCommand? Parse(IEnumerable<string> arguments)
    {
        foreach (var raw in arguments)
        {
            var argument = raw.Trim('"');
            if (argument == NewChatArgument) return new(LaunchAction.NewChat);
            if (argument == SearchArgument) return new(LaunchAction.Search);
            if (argument.StartsWith(OpenChatPrefix, StringComparison.Ordinal) && argument[OpenChatPrefix.Length..] is var id && IsChatId(id))
                return new(LaunchAction.OpenChat, id);
            if (argument.StartsWith(SignInPrefix, StringComparison.Ordinal) && argument[SignInPrefix.Length..] is var service && SignInServices.Contains(service))
                return new(LaunchAction.SignIn, Service: service);
        }
        return null;
    }

    public static bool IsChatId(string? id)
        => id is { Length: > 0 and <= 512 } && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"');
}
