using MessageLink.Server.Backup;

namespace MessageLink.Server;

/// <summary>
/// Where a server keeps its stores: read by <see cref="ServerComposition"/> to open them and by <see cref="ServerBackupPaths"/> to back them up and restore them,
/// so the two never disagree. The platforms differ only in where the whole history sits and whether the certificate's key is sealed; nothing has moved
/// between them, and nothing migrates.
/// </summary>
internal sealed record ServerLayout(string Root, string Keys, string History, bool TlsKeySealed)
{
    private const string ServerFolder = "Server", HistoryName = "history.db";

    /// <summary>The PC's own server: its keys in <c>Server\Keys</c> and its whole history in <c>Server\history.db</c>, the certificate's key sealed with its protector.</summary>
    public static ServerLayout Windows(string root) => new(root, PcServerRestore.KeysFolder(root), Path.Combine(root, ServerFolder, HistoryName), TlsKeySealed: true);

    /// <summary>The Linux image: its keys in their own volume, the whole history beside the other stores, the certificate's key plain PEM in the keys volume.</summary>
    public static ServerLayout Linux(string root, string keys) => new(root, keys, BesideTheStores(root), TlsKeySealed: false);

    /// <summary>The whole history where a server keeps it when it names no other place.</summary>
    internal static string BesideTheStores(string root) => Path.Combine(root, HistoryName);

    public string State => Path.Combine(Root, "state.db");
    public string Media => Path.Combine(Root, "media.db");
    public string Archive => Path.Combine(Root, "archive.db");
    public string SavedPhotos => Path.Combine(Root, "saved-photos.db");
    /// <summary>What the server knows of its clients (<see cref="ServerClientState"/>).</summary>
    public string ClientState => Path.Combine(Root, "LastKnownClient-v1", "state.protected");
    public string Shared => Path.Combine(Root, ServerFolder, SharedStateStore.FileName);
    public string Space => Path.Combine(Root, ServerFolder, SharedSpace.FileName);
    public string Journal => Path.Combine(Root, ServerFolder, "journal.protected");
    public string Uploads => Path.Combine(Root, ServerFolder, "uploads");

    /// <summary>What the server's backup reads and its restore writes, over these same places.</summary>
    public ServerBackupPaths BackupPaths => new(Root, Keys) { HistoryFile = History, TlsKeySealed = TlsKeySealed };
}
