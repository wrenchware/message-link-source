using MessageLink.App.Services;
using MessageLink.Protocol;

namespace MessageLink.Server.Windows;

/// <summary>The Windows server's logs by name (protocol 18), for its page: its own and its providers', in the data root where it writes them (Diagnostics.ProviderLogFile).</summary>
internal static class ServerLogs
{
    public const string File = "server.log";

    public static IReadOnlyDictionary<string, string> Paths(string root) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [HostLogNames.Server] = Path.Combine(root, File),
        [HostLogNames.ServerPrevious] = LogRotation.PreviousPath(Path.Combine(root, File)),
        [HostLogNames.GoogleMessages] = Path.Combine(root, "GoogleMessages", File),
        [HostLogNames.GoogleMessagesPrevious] = LogRotation.PreviousPath(Path.Combine(root, "GoogleMessages", File)),
        [HostLogNames.GoogleSignInHistory] = Path.Combine(root, "GoogleMessages", "sign-in-history." + File),
        [HostLogNames.GoogleVoice] = Path.Combine(root, "GoogleVoice", File),
        [HostLogNames.GoogleVoicePrevious] = LogRotation.PreviousPath(Path.Combine(root, "GoogleVoice", File)),
        [HostLogNames.Instagram] = Path.Combine(root, "Instagram", File),
        [HostLogNames.InstagramPrevious] = LogRotation.PreviousPath(Path.Combine(root, "Instagram", File)),
        [HostLogNames.Messenger] = Path.Combine(root, "Messenger", File),
        [HostLogNames.MessengerHelper] = Path.Combine(root, "Messenger", "helper.log"),
        [HostLogNames.MessengerHelperPrevious] = LogRotation.PreviousPath(Path.Combine(root, "Messenger", "helper.log")),
    };
}

/// <summary>
/// The PC backup handed over to the server's (SWITCHBOARD.md 10.7, owner D13: the PC backup retires once the PC runs its own server). The first time the server starts on a
/// root whose PC backup had a folder, that folder becomes the server's, and the PC backup's password (sealed to this Windows user, as the server seals its own) becomes
/// the server's backup password, so the person's backups go on where they were, under the password they know. Nothing is taken when the server has its own already,
/// and the PC backup's own files are left as they are. Logged as fixed words.
/// </summary>
internal static class PcBackupHandover
{
    public const string PcSettings = "backup-settings.json", PcPassword = "backup-password.protected";

    public static void Take(string root, MessageLink.Server.Backup.ServerBackupPaths paths, string folderChoice, Action<string> log)
    {
        try
        {
            var folder = false;
            var password = false;
            if (!System.IO.File.Exists(folderChoice) && System.IO.File.Exists(Path.Combine(root, PcSettings))
                && MessageLink.App.Services.Backup.BackupSettings.Load(Path.Combine(root, PcSettings)).Folder is { Length: > 0 } pcFolder && Path.IsPathFullyQualified(pcFolder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folderChoice)!);
                System.IO.File.WriteAllText(folderChoice, pcFolder);
                folder = true;
            }
            if (!System.IO.File.Exists(paths.BackupPassword) && System.IO.File.Exists(Path.Combine(root, PcPassword)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.BackupPassword)!);
                System.IO.File.Copy(Path.Combine(root, PcPassword), paths.BackupPassword, overwrite: false);
                password = true;
            }
            if (folder || password) log($"server backup handover folder={(folder ? "taken" : "none")} password={(password ? "taken" : "none")}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log($"server backup handover failed type={failure.GetType().Name}");
        }
    }
}
