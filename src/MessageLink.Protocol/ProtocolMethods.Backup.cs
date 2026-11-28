namespace MessageLink.Protocol;

// The server's own backup (protocol 26): one encrypted file a day into a folder the server's host maps, with the server's
// data and identity and never a service's sign-in. Any device may read how it stands (`server.backup.status`), so a PC can show it in Settings and a phone can
// tell nobody has backed up lately; starting one and setting its password are the server page's, and are refused to every device on a server. Neither of those is journaled: a second Back up now while one runs
// is answered busy, and setting the password twice sets it twice. A password is never logged on either end and is never in a journal (a mutation's journal keeps
// a digest of its params). The `backup` account signal (AccountSignals.Backup) tells a connection of 26 or later that the status changed.
public static partial class ProtocolMethods
{
    /// <summary>How the server's backup stands (<see cref="ServerBackupStatus"/>), to any device.</summary>
    public const string ServerBackupStatusRead = "server.backup.status";
    /// <summary>Starts a backup now, on a server with no paired devices (a server's page starts its own); answered at once, the backup's end is told by the <c>backup</c> signal.</summary>
    public const string ServerBackupRun = "server.backup.run";
    /// <summary>Sets the password new backups are sealed with, on a server with no paired devices (a server's page sets its own). A lost password means the backups made with it are lost.</summary>
    public const string ServerBackupPassword = "server.backup.password";

    private static Entry[] BackupEntries() =>
    [
        new(ServerBackupStatusRead, "IServerBackup.StatusAsync", null, typeof(ServerBackupStatus), ProtocolVersion.ServerBackup),
        new(ServerBackupRun, "IServerBackup.RunAsync", null, typeof(ServerBackupStarted), ProtocolVersion.ServerBackup),
        new(ServerBackupPassword, "IServerBackup.SetPasswordAsync", typeof(ServerBackupPasswordParams), typeof(Done), ProtocolVersion.ServerBackup),
    ];
}

/// <summary>
/// How the server's backup stands. <paramref name="State"/> is one of <see cref="ServerBackupStates"/>. <paramref name="LastGood"/> is when the newest backup was written,
/// <paramref name="Next"/> when the next is due (null when none will run), <paramref name="Bytes"/> the newest's size. <paramref name="Failure"/> is the last attempt's
/// failure, when it failed after the newest good one (one of <see cref="ServerBackupFailures"/>); <paramref name="Check"/> what reading the newest back last found
/// (<c>ok</c>, <c>unreadable</c>, <c>unsupported</c>, <c>retired</c> (an older format, #123), <c>missing</c>, <c>old_password</c>), when it has been read back. <paramref name="Stale"/> says no good backup
/// was made in the last two days although backups are on. Fixed words, times and a count: nothing a person typed, and no path.
/// </summary>
public sealed record ServerBackupStatus(string State, bool Enabled = false, bool HasFolder = false, bool HasPassword = false, DateTimeOffset? LastGood = null,
    DateTimeOffset? Next = null, long Bytes = 0, string? Failure = null, DateTimeOffset? FailedAt = null, string? Check = null, DateTimeOffset? CheckedAt = null,
    bool Stale = false, int Kept = 0);

/// <summary>The words <see cref="ServerBackupStatus.State"/> takes.</summary>
public static class ServerBackupStates
{
    /// <summary>No folder for backups is mapped to the server (Docker's <c>/backups</c>, or <c>MESSAGES_BACKUPS</c>).</summary>
    public const string NoFolder = "no_folder";
    /// <summary>Turned off on the server's page.</summary>
    public const string Off = "off";
    /// <summary>On, with no password set yet: nothing runs until one is.</summary>
    public const string NoPassword = "no_password";
    /// <summary>On and working: the newest attempt worked, or none has been made yet.</summary>
    public const string On = "on";
    /// <summary>The newest attempt failed (<see cref="ServerBackupStatus.Failure"/> says why); it is tried again within the hour.</summary>
    public const string Failing = "failing";
    /// <summary>A backup is being written now.</summary>
    public const string Running = "running";
    /// <summary>A restore is being made ready or applied.</summary>
    public const string Restoring = "restoring";
    public static IReadOnlyList<string> All { get; } = [NoFolder, Off, NoPassword, On, Failing, Running, Restoring];
}

/// <summary>The words <see cref="ServerBackupStatus.Failure"/> and <see cref="ServerBackupStarted.Refusal"/> take.</summary>
public static class ServerBackupFailures
{
    public const string Folder = "folder", Space = "space", Password = "password", Messenger = "messenger", Local = "local", Write = "write", Busy = "busy",
        NoFolder = "no_folder", NoPassword = "no_password";
    public static IReadOnlyList<string> All { get; } = [Folder, Space, Password, Messenger, Local, Write, Busy, NoFolder, NoPassword];
}

/// <summary>
/// How a server's backup stands for whoever reads it: the server's page (Needs attention, its Daily backup line) and a PC's Settings → Backup row, which must agree.
/// </summary>
public static class ServerBackupStanding
{
    /// <summary>
    /// Whether the backup wants a look: no folder chosen (owner, 2026-11-25), no password, the newest attempt failed, or none has worked for two days. Turned off
    /// is a choice, and running or restoring is under way.
    /// </summary>
    public static bool WantsALook(ServerBackupStatus status) =>
        status.Stale || status.State is ServerBackupStates.NoFolder or ServerBackupStates.NoPassword or ServerBackupStates.Failing;

    /// <summary>
    /// A failed attempt's reason, as a sentence. Beside <see cref="ServerBackupFailures"/>, the server writes the codes a network share or its own disk gives
    /// (<c>auth</c>, <c>denied</c>, <c>share</c>, <c>missing</c>, <c>unavailable</c>, <c>local_space</c>, <c>settings</c>).
    /// </summary>
    public static string Failure(string? code) => code switch
    {
        ServerBackupFailures.Folder => "The backup folder isn't available.",
        ServerBackupFailures.Space => "The backup folder is full.",
        "local_space" => "The server's own disk is full. Free space there before trying again.",
        ServerBackupFailures.Password => "The saved password can't be read. Set it again.",
        ServerBackupFailures.Messenger => "A partial backup was kept without Messenger history. It doesn't count as a complete backup.",
        "auth" => "The network share refused the saved sign-in. Enter its credentials again.",
        "denied" => "The destination refused access. Check its permissions.",
        "share" or "missing" => "The share or folder wasn't found.",
        "unavailable" => "The file server didn't answer.",
        "settings" => "The server couldn't save the backup result. Check its own disk space and permissions.",
        ServerBackupFailures.Local => "The server's own data couldn't be read.",
        _ => "The backup couldn't be written.",
    };
}

/// <summary>Whether a backup started: <paramref name="Refusal"/> (one of <see cref="ServerBackupFailures"/>) says why not.</summary>
public sealed record ServerBackupStarted(bool Started, string? Refusal = null);

/// <summary>The password new backups are sealed with: at least <see cref="ServerBackupLimits.MinPassword"/> characters. Never logged.</summary>
public sealed record ServerBackupPasswordParams(string Password);

public static class ServerBackupLimits
{
    public const int MinPassword = 8, MaxPassword = 256;
}

/// <summary>
/// The server's side of its backup. Only a server that keeps one has it (the Linux server); every other server answers not supported. Who may start one or set its
/// password is the dispatcher's business.
/// </summary>
public interface IServerBackup
{
    Task<ServerBackupStatus> StatusAsync(CancellationToken cancellationToken = default);
    Task<ServerBackupStarted> RunAsync(CancellationToken cancellationToken = default);
    /// <summary>Throws <see cref="ArgumentException"/> for a password outside <see cref="ServerBackupLimits"/>.</summary>
    Task SetPasswordAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>A server's backup as a device asks for it (protocol 26). Nothing is kept.</summary>
public sealed class ServerBackupProxy(ProtocolClient client) : IServerBackup
{
    public Task<ServerBackupStatus> StatusAsync(CancellationToken cancellationToken = default) => client.CallAsync<ServerBackupStatus>(ProtocolMethods.ServerBackupStatusRead, cancellationToken);

    public Task<ServerBackupStarted> RunAsync(CancellationToken cancellationToken = default) => client.CallAsync<ServerBackupStarted>(ProtocolMethods.ServerBackupRun, cancellationToken);

    public Task SetPasswordAsync(string password, CancellationToken cancellationToken = default) =>
        client.CallAsync<ServerBackupPasswordParams, Done>(ProtocolMethods.ServerBackupPassword, new ServerBackupPasswordParams(password), cancellationToken);
}
