using System.Text.Json;

namespace MessageLink.App.Services;

/// <summary>Which server this data root uses (SWITCHBOARD.md 10.7, "There is always a server"), as its record says.</summary>
internal enum MoveState
{
    /// <summary>No record: a root that has not been launched yet (ProtocolMode.Settle marks it as running its own server at its first launch).</summary>
    Local,
    /// <summary>A move of an earlier version began and was never confirmed (the move itself is gone since 2026-11-21). Nothing on this PC was changed or deleted by it.</summary>
    Moving,
    /// <summary>The root uses the server the record names: this PC's own (<see cref="MoveMarker.LocalServer"/>), or one elsewhere it joined or an earlier version moved it to.</summary>
    Moved,
    /// <summary>The file is there and cannot be read. It counts as neither: nothing that needs to know the server goes ahead.</summary>
    Unreadable,
}

/// <summary>
/// What the record says. <paramref name="Server"/> is <see cref="MoveMarker.LocalServer"/> or a server's pinned certificate fingerprint in lower-case hex
/// (<c>ReplicatedStateStore.ServerIdOf</c>), <paramref name="Id"/> a random id of the switch or join that wrote it, and <paramref name="At"/> UTC milliseconds.
/// </summary>
internal sealed record MoveMark(MoveState State, string? Id = null, string? Server = null, long At = 0)
{
    public static readonly MoveMark Absent = new(MoveState.Local);
}

/// <summary>
/// The one file that says which server a data root uses: <c>&lt;root&gt;\Server\moved.json</c> (its name is from when it recorded a move), a fixed name, so it is found
/// whatever the PC's Google pairing or profile is (a profile-keyed row would be hidden by a re-pairing). It is plain JSON because it holds nothing secret (a fingerprint is
/// public and an id is random), is never shared with a server, never in a backup (a backup is an allowlist of stores and this is none of them) and never read by a
/// provider. Written beside itself and moved into place, so it is always whole. No file is the same as <see cref="MoveState.Local"/>; a file that cannot be read
/// is <see cref="MoveState.Unreadable"/> and never mistaken for a confirmed one.
/// </summary>
internal static class MoveMarker
{
    public const string Folder = "Server";
    public const string FileName = "moved.json";
    private const int MaxBytes = 4096;

    // A record written by an earlier version may also hold an epoch, a rev and a key of its move; they are not read.
    private sealed record Stored(int Version, string State, string? Id, string? Server, long At);

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, Folder, FileName);

    /// <summary>
    /// The marker. A file that is locked for a moment (a scanner, a backup reading it as Messages starts) is tried again for up to a second, as the preferences file is
    /// (<paramref name="attempts"/> of 100 ms), before it counts as unreadable; a damaged one is unreadable at once.
    /// </summary>
    public static MoveMark Read(string dataRoot, int attempts = 10)
    {
        var path = PathFor(dataRoot);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Only an absence the file system confirms is "nothing moved": File.Exists answers false for a file it was not allowed to look at, which would let a root whose
                // marker is out of reach be run as if it had never moved. GetAttributes tells them apart (not found against denied), in the one call the common case costs.
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException) { return MoveMark.Absent; }
                // A folder where the file should be is something wrong, not nothing.
                if ((attributes & FileAttributes.Directory) != 0) return new(MoveState.Unreadable);
                var info = new FileInfo(path);
                if (info.Length is 0 or > MaxBytes) return new(MoveState.Unreadable);
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path));
                if (stored is not { Version: 1, Id.Length: > 0, Server.Length: > 0 }) return new(MoveState.Unreadable);
                return stored.State switch
                {
                    "moving" => new(MoveState.Moving, stored.Id, stored.Server, stored.At),
                    "moved" => new(MoveState.Moved, stored.Id, stored.Server, stored.At),
                    _ => new(MoveState.Unreadable),
                };
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException && attempt < attempts && failure is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(100);
            }
            // Not knowing is not "local": a locked or damaged marker must not let the real data be used as if it had never moved.
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new(MoveState.Unreadable);
            }
        }
    }

    /// <summary>
    /// The server a PC's own Message Link Server is named by in the marker (SWITCHBOARD.md 10.7): the same word the client's replica is filed under
    /// (<c>ReplicatedStateStore.LocalServer</c>), never a fingerprint, so a marker naming it is never mistaken for a server on the network.
    /// </summary>
    public const string LocalServer = "local";

    /// <summary>The marker is about this PC's own server, <c>MessageLinkServer.exe</c> on this data root.</summary>
    public static bool IsLocalServer(MoveMark mark) => string.Equals(mark.Server, LocalServer, StringComparison.Ordinal);

    /// <summary>This root runs its own Message Link Server: the window is that server's client and the server serves this root.</summary>
    public static bool RunsOwnServer(MoveMark mark) => mark.State == MoveState.Moved && IsLocalServer(mark);

    /// <summary>
    /// Whether <c>MessageLinkServer.exe</c> may open the real data of <paramref name="dataRoot"/> (SWITCHBOARD.md 10.7, the lift of 3.2b): only a root that runs its own
    /// server (<see cref="RunsOwnServer"/>). Anything else, an unreadable marker included, stays refused.
    /// </summary>
    public static bool ServerMayOpen(string dataRoot) => RunsOwnServer(Read(dataRoot));

    /// <summary>Whether the marker says the root uses the server named by <paramref name="fingerprint"/> (lower-case hex).</summary>
    public static bool MovedTo(string dataRoot, string? fingerprint) =>
        fingerprint is { Length: > 0 } && Read(dataRoot) is { State: MoveState.Moved, Server: { } server } && string.Equals(server, fingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the marker, whole or not at all: beside itself, flushed, then moved over the old one.</summary>
    public static void Write(string dataRoot, MoveMark mark)
    {
        if (mark.State is not (MoveState.Moving or MoveState.Moved) || string.IsNullOrEmpty(mark.Id) || string.IsNullOrEmpty(mark.Server))
            throw new ArgumentException("Only a record with its id and server is written.", nameof(mark));
        var path = PathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new Stored(1, mark.State == MoveState.Moved ? "moved" : "moving", mark.Id, mark.Server, mark.At)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }
}
