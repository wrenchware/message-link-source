using MessageLink.App.Services;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Services;

namespace MessageLink.Server.Backup;

/// <summary>
/// A PC's own server (MessageLinkServer.exe) shares its data root with the window, so a restore or a reset chosen on its page is applied only while neither has
/// anything open there: by the window's launch, holding the data lock with no server running (the window never takes the server's lease), before anything reads
/// the folder; or by a server start holding the lease and, for as long as it applies, the window's data lock. A server that starts while a launching window
/// applies it finds the data lock held and waits for the plan to clear. A reset is a fresh install (owner, 2026-11-22): everything in the root goes except the keys, the
/// tailnet door's state folder, the move marker that lets the server open the root, a backup folder chosen inside it, the logs, the lease and the run markers. A door
/// that ran when Reset server was chosen has signed out of Tailscale and emptied that folder first (ServerPage.ResetAsync, owner 2026-11-22), so the PC leaves the
/// owner's tailnet console; one that was off keeps its node for the next time it is turned on.
/// </summary>
internal static class PcServerRestore
{
    /// <summary>Where the server's own keys live: the network door's certificate, the page's password, the devices' generation.</summary>
    public static string KeysFolder(string root) => Path.Combine(root, "Server", "Keys");

    /// <summary>The backup's view of a PC's root: its keys folder, and the whole history in <c>Server\history.db</c>, where this server keeps it.</summary>
    public static ServerBackupPaths Paths(string root) =>
        ServerLayout.Windows(root).BackupPaths;

    /// <summary>Whether a reset or a restore waits for a start, or a restore's moves were begun and not finished.</summary>
    public static bool Pending(string root)
    {
        var paths = Paths(root);
        return ServerRestore.ResetPlanned(paths) || ServerRestore.Planned(paths) || ServerRestore.IsUnfinished(paths);
    }

    /// <summary>
    /// The app's server page window's browser profile (ServerPageWindow), kept by a reset: it holds no sign-in (each opening signs in afresh with a one-time code), and
    /// the browser of the window that chose the reset may still hold files in it when the next launch applies it.
    /// </summary>
    public const string ServerPageProfile = "ServerPage";

    // Kept by a reset, by their path under the root ('/' between folders). The tailnet door's identity is TailnetNode.StateFolderName, in the app.
    private static HashSet<string> Keep() => new(StringComparer.OrdinalIgnoreCase)
    {
        "Server/Keys", MoveMarker.Folder + "/" + MoveMarker.FileName, "Tailnet", ServerLease.FileName, "ServerLogs", ServerPageProfile,
        "app.log", LogRotation.PreviousPath("app.log"), "server.log", LogRotation.PreviousPath("server.log"),
        "run.marker", "run.marker.version", "server-run.marker", "server-run.marker.version",
    };

    /// <summary>
    /// Applies what waits, if anything, on a root that runs its own server (read again here, under the caller's locks: a root switched to a server elsewhere is never
    /// touched). The caller holds the window's data lock, and no other server is running. False when a reset or a restore's moves stopped part way: nothing may open the root
    /// until a later start finishes them.
    /// </summary>
    public static bool Apply(string root, IStateProtector protector, Action<string> log)
    {
        var paths = Paths(root);
        if (!Pending(root)) return true;
        if (!MoveMarker.ServerMayOpen(root))
        {
            log("server restore skipped reason=not_own_server");
            return true;
        }
        var folder = ServerBackupService.ReadFolderChoice(Path.Combine(paths.Backup, "folder.txt"));
        var keep = Keep();
        // A backup folder inside the root (one the PC's own backup could choose, PcBackupHandover) holds the backups a reset says it keeps.
        if (folder is not null && Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(folder)) is var inside
            && !inside.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(inside) && inside != ".")
            keep.Add(inside.Replace('\\', '/'));
        try
        {
            ServerRestore.ApplyPendingAsync(paths, protector, folder, SignedIn, log, keep: keep, signedInForReset: SignedInForReset).GetAwaiter().GetResult();
        }
        catch (Exception failure)
        {
            log($"server restore failed type={failure.GetType().Name}");
            // A reset or a restore's moves that threw stopped part way, whatever is left of the plan.
            return false;
        }
        return !ServerRestore.IsUnfinished(paths) && !ServerRestore.ResetPlanned(paths);
    }

    // What Reset server signs out before it is applied; Google Messages is unpaired by the reset itself.
    private static bool SignedInForReset() => MessengerBackend.HasSavedSession || GoogleVoiceAccounts.HasSavedSessions
        || MessageLink.Storage.InstagramSessionStore.Exists(AppPaths.Combine("Instagram"));

    private static bool SignedIn() => SignedInForReset() || GoogleSessionProfile.HasSavedSession;
}
