using Microsoft.Extensions.DependencyInjection;
using MessageLink.App;
using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.Core.Services;
using MessageLink.Server;
using MessageLink.Protocol;
using MessageLink.WindowsPlatform;

namespace MessageLink.Server.Windows;

/// <summary>
/// The PC's server (<see cref="ServerComposition"/>) with what only Windows has: DPAPI, Windows' imaging, the renewal's WebView2 on the shell's STA thread,
/// and its own administration. Building it connects nothing; the providers are started only by <see cref="RestoreAsync"/>, and only when the server was
/// started with <c>--restore</c>.
/// </summary>
internal sealed partial class WindowsServerStartup : IDisposable
{
    private readonly ComposedServer server;
    private readonly Action<string> log;
    private readonly IStaQueue sta;

    private WindowsServerStartup(ComposedServer server, IStaQueue sta, Action<string> log)
    {
        this.server = server; this.sta = sta; this.log = log;
    }

    public ServiceProvider Services => server.Services;
    public ProtocolServer Host => server.Host;

    /// <summary>The server's own backup (on Windows): its schedule runs beside the server, into the folder the person chose.</summary>
    public MessageLink.Server.Backup.ServerBackupService? Backup => server.Backup;

    /// <param name="log">Where the composition's words go.</param>
    /// <param name="sta">The shell's STA thread (<see cref="ServerShell.Sta"/>), where the Google renewal's browser runs.</param>
    /// <param name="restore">Whether <see cref="RestoreAsync"/> will run (the server was started with <c>--restore</c>), so that a client's restore calls wait for it.</param>
    public static WindowsServerStartup Compose(Action<string> log, IStaQueue sta, bool restore = false)
    {
        var layout = ServerLayout.Windows(AppPaths.DataRoot);
        var folderChoice = Path.Combine(layout.BackupPaths.Backup, "folder.txt");
        var version = typeof(WindowsServerStartup).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var platform = new ServerPlatform(new WindowsStateProtector(), new WindowsPhotoReencoder(),
            // The renewal's browser is WebView2 on the shell's STA thread, the app's own code (MessageLink.Windows). Building it loads nothing:
            // WebView2 starts only when something asks for a refresh, and nothing does until the keeper starts, which only a restore does.
            new WebViewGoogleBrowser(sta), new MessengerSealedCells(),
            // The PC's own server is administered as the Linux one is: its status, its accounts and their removal, for its page and its owner.
            backends => WindowsServerAdmin.Over(backends, version, log),
            ServerFeeds.All,
            // The server restores its own accounts only with --restore. The slots exist from here, before the pipe opens, so a client that asks for a restore
            // ahead of the server's own begins waits for it (RestoreOnce).
            restore ? RestoreOnce.ForAll() : RestoreOnce.None,
            new ProtectedChatSnoozeStore(AppPaths.Combine(ProtectedChatSnoozeStore.FolderName)),
            new ServerLogFiles(ServerLogs.Paths(AppPaths.DataRoot)), version,
            // The server's own backup goes into the folder the person chose on the page, the PC backup's folder and password taken over the first time: once
            // the backends are built, just before the backup is made, so a start that fails before then has taken nothing over.
            () =>
            {
                PcBackupHandover.Take(AppPaths.DataRoot, layout.BackupPaths, folderChoice, log);
                return MessageLink.Server.Backup.ServerBackupService.ReadFolderChoice(folderChoice);
            }, log)
        { VideoFrames = new WindowsVideoFrameExtractor() };
        // The sign-in keeper is not started here: it listens for Google's refusals and renews, so it starts with the restore and only with --restore
        // (RestoreAsync), the one thing that lets a provider touch the network.
        var startup = new WindowsServerStartup(ServerComposition.Compose(layout, platform), sta, log);
        startup.ConfigureAccounts();
        return startup;
    }

    /// <summary>
    /// The launch's restore, in the app's order: the other providers start first and are not awaited before the phone, so a stale
    /// Google session never holds them up, and each is restored once, so Messenger and Instagram never log in twice. Then the feed is let go.
    /// </summary>
    public async Task RestoreAsync(CancellationToken stop)
    {
        try
        {
            // Inside the try: a stop during the added accounts' restore still answers a client waiting on the server's, rather than holding the stop's drain.
            await Services.GetRequiredService<ProviderAccounts>().RestoreAsync(stop);
            // The window starts the keeper as it is built, ahead of the launch's connect, so a refusal that connect hears is renewed. Same here.
            var signIn = StartSignInKeeper(stop);
            var others = Task.WhenAll(server.RestoreMessengerAndInstagramAsync(stop), server.RestoreVoiceAsync(stop));
            // Held for the connect, as the window's Connect command is busy for its own: a renewal that ends meanwhile does not connect again beside it.
            try { await server.RestoreGoogleAsync(stop, signIn.BeginConnect); }
            // However it ended, or never began: a renewal that waited for the launch's connect goes on.
            finally { signIn.EndStartupLoad(); }
            // A feed let go connects Google's saved pairing by itself, so a stop that has begun leaves it shut.
            if (!stop.IsCancellationRequested) server.OpenFeed();
            await others;
        }
        // A stop is not a failed start: logged as the restore's outcome, as the Linux server's is.
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { log("server restore outcome=stopped"); }
        // However the restore ended, a client waiting on a provider it never reached is answered (skipped) rather than left waiting.
        finally { server.EndRestores(); }
    }

    /// <summary>The server's restore will not run (the server was stopped first): a client waiting on it is answered skipped.</summary>
    public void EndRestores() => server.EndRestores();

    /// <summary>The start's restore is skipped or held: every feed but Google's own goes now, so Hermes and an account connected meanwhile are heard.</summary>
    public void OpenPassiveFeed() => server.OpenPassiveFeed();

    /// <summary>Whether the start's restore would log a Messenger or Instagram account in (<see cref="ComposedServer.RestoresAccounts"/>).</summary>
    public bool RestoresAccounts => server.RestoresAccounts;

    // Google's sign-in renewal: the keeper (age timer, early renewal, the checks after two hours, the refusal-driven refresh) with the server as its host.
    // It is started once, by the restore, so a server without --restore opens no browser and makes no request.
    private ServerSignInHost StartSignInKeeper(CancellationToken stop)
    {
        // A renewal's end tells every client to re-read Google Messages, in the host's own event order (ServerSignInHost.ResumeSync).
        var signIn = new ServerSignInHost(sta, Services.GetRequiredService<ServerClientState>(), ConnectGoogleAsync, stop, log,
            () => Host.EventLog.Append(new RealtimeEvent(RealtimeKind.ResyncRequired, Scope: ProviderScopes.Google)));
        Services.GetRequiredService<GoogleSignInKeeper>().Start(signIn);
        log("server sign-in keeper started");
        return signIn;
    }

    // What the window's Connect command does for Google Messages: find the saved pairing and list its accounts.
    private async Task ConnectGoogleAsync(CancellationToken stop)
    {
        var backend = Services.GetRequiredService<MessageLink.Core.Backend.IChatBackend>();
        await backend.DiscoverAsync(stop);
        await backend.GetAccountsAsync(stop);
    }

    public void Dispose() => server.Dispose();
}
