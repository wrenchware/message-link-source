using System.Reflection;
using System.Text;
using MessageLink.Terminal;
using MessageLink.Terminal.Connection;
using MessageLink.Terminal.Session;
using MessageLink.Terminal.Vt;

// mlink (SWITCHBOARD.md stage 13): Message Link's chat console in a terminal, talking straight to Message Link Server.

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

var version = typeof(MlinkContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
var command = args.Length == 0 ? "" : args[0].ToLowerInvariant();

switch (command)
{
    case "--version" or "-v" or "version":
        Console.Out.WriteLine($"Message Link {version}");
        return 0;
    case "--help" or "-h" or "-?" or "/?" or "help":
        Console.Out.Write(Usage.Text);
        return 0;
    case "terminal-profile":
        return MessageLink.Terminal.Install.TerminalProfile.Run(args[1..], Console.Out);
    case "" or "join" or "leave":
        break;
    default:
        Console.Error.WriteLine($"mlink: unknown command \"{args[0]}\".");
        Console.Error.Write(Usage.Text);
        return 2;
}
if (command == "join" && args.Length != 2)
{
    Console.Error.WriteLine("mlink join needs the join link from your server's page, or its address.");
    return 2;
}
if (command == "leave" && args.Length != 1 || command == "" && args.Length != 0)
{
    Console.Error.Write(Usage.Text);
    return 2;
}


string? folder = null;
try
{
    folder = MlinkPaths.DefaultFolder();
    folder = MlinkPaths.EnsureFolder(folder);
}
catch (Exception)
{
    Console.Error.WriteLine(folder is null
        ? $"Message Link couldn't find a folder for its files. Set {MlinkPaths.HomeVariable} to a folder you can write to."
        : $"Message Link couldn't make its folder {folder}. Check that you can write there, or set {MlinkPaths.HomeVariable} to a folder you can.");
    return 1;
}
MlinkLog log;
MessageLink.Core.Services.IStateProtector protector;
try { log = new MlinkLog(folder); }
catch (Exception)
{
    Console.Error.WriteLine($"Message Link couldn't open its files in {folder}. Check that you can read and write there.");
    return 1;
}
using var logged = log;
try { protector = MlinkProtector.Create(folder); }
catch (Exception failure)
{
    log.Write($"mlink protector failed type={failure.GetType().Name}");
    Console.Error.WriteLine($"Message Link couldn't open the key that protects its files in {folder}. Check that you can read and write there.");
    return 1;
}
log.Write($"mlink started version={version} command={(command.Length == 0 ? "console" : command)} os={(OperatingSystem.IsWindows() ? "windows" : "linux")}");
var context = new MlinkContext(folder, protector, log.Write, version, Console.Out, question =>
{
    Console.Out.Write(question);
    return Console.In.ReadLine();
});

using var quit = new CancellationTokenSource();
switch (command)
{
    case "join":
        return await JoinCommand.RunAsync(context, args[1], quit.Token);
    case "leave":
        return await JoinCommand.LeaveAsync(context);
}

// The console: the session and the terminal live on one thread, as the window's console lived on the interface thread.
return TerminalLoop.Run(async () =>
{
    var link = await ServerConnector.ConnectAsync(context, quit.Token);
    if (link is null) return 1;
    await using (link)
    {
        using var front = TerminalFront.OpenSystem(log.Write);
        await using var session = await MlinkSession.CreateAsync(link, context, front.Copy, quit.Token);
        var code = await front.RunAsync(session.Console, session.Typing, quit.Token);
        log.Write($"mlink ended code={code}");
        return code;
    }
});


internal static class Usage
{
    public const string Text = """
        Message Link in a terminal.

        Usage:
          mlink                 Open your conversations. Connects to the server you joined, or to this PC's own server.
          mlink join <link>     Join a server elsewhere. Copy the link from the server's page: Add a device, then PC.
                                An address alone asks the server to let this terminal in; approve it on the server's page.
          mlink leave           Forget the server you joined.
          mlink --version       Show the version.

        In the console, /help lists commands.

        """;
}
