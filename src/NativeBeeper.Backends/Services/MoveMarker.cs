using System.Text.Json;

namespace NativeBeeper.App.Services;

/// <summary>Where this data root's choices live (SWITCHBOARD.md stage 6 slice 13a).</summary>
internal enum MoveState
{
    /// <summary>No marker: the choices are this PC's own, as they always were.</summary>
    Local,
    /// <summary>A move to a Messages Server began and has not been confirmed. Nothing on this PC was changed or deleted by it.</summary>
    Moving,
    /// <summary>The server holds a verified copy of this PC's choices and this root's client may use that server (the narrow lift of the real-data refusal).</summary>
    Moved,
    /// <summary>The file is there and cannot be read. It counts as neither local nor moved: nothing that needs a confirmed move goes ahead.</summary>
    Unreadable,
}

/// <summary>
/// What the marker says. <paramref name="Server"/> is the server's pinned certificate fingerprint in lower-case hex (<c>ReplicatedStateStore.ServerIdOf</c>),
/// <paramref name="Id"/> the move's id (the <c>state.import</c> operation id). <paramref name="Epoch"/> and <paramref name="Rev"/> are where the server's shared
/// state stood once the import was in (a confirmed move only): what a move back asks it for changes since. <paramref name="At"/> is UTC milliseconds. <paramref name="Key"/> is the
/// key this PC's choices were moved under: once the move has deleted the Messenger and Instagram sign-ins it was derived from, nothing else on the PC can say it (null in a marker from before it was kept).
/// </summary>
internal sealed record MoveMark(MoveState State, string? Id = null, string? Server = null, string? Epoch = null, long Rev = 0, long At = 0, string? Key = null)
{
    public static readonly MoveMark Absent = new(MoveState.Local);
}

/// <summary>
/// The one file that says a data root's choices were moved to a server: <c>&lt;root&gt;\Server\moved.json</c>, a fixed name, so it is found whatever the PC's
/// Google pairing or profile is (a profile-keyed row would be hidden by a re-pairing). It is plain JSON because it holds nothing secret (a fingerprint is public, the key is a one-way name,
/// and an id is random), is never shared with a server, never in a backup (a backup is an allowlist of stores and this is none of them) and never read by a
/// provider. Written beside itself and moved into place, so it is always whole. No file is the same as <see cref="MoveState.Local"/>; a file that cannot be read
/// is <see cref="MoveState.Unreadable"/> and never mistaken for a confirmed move. The in-process app only ever reads it.
/// </summary>
internal static class MoveMarker
{
    public const string Folder = "Server";
    public const string FileName = "moved.json";
    private const int MaxBytes = 4096;

    private sealed record Stored(int Version, string State, string? Id, string? Server, string? Epoch, long Rev, long At, string? Key = null);

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
                    "moving" => new(MoveState.Moving, stored.Id, stored.Server, stored.Epoch, stored.Rev, stored.At, stored.Key),
                    "moved" => new(MoveState.Moved, stored.Id, stored.Server, stored.Epoch, stored.Rev, stored.At, stored.Key),
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

    /// <summary>The file a move's keys are recorded in, beside the marker: what a move back removes when the server no longer lists a key that was moved.</summary>
    public static string KeysPathFor(string dataRoot) => Path.Combine(dataRoot, Folder, KeysFileName);
    public const string KeysFileName = "moved.keys";
    private const long MaxKeysBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Records the keys a move took to the server, one a line, whole or not at all. Never shared and never in a backup, like the marker; keys are one-way digests. Written
    /// before the marker says <c>moved</c>, so a confirmed move always has them.
    /// </summary>
    public static void WriteKeys(string dataRoot, IEnumerable<string> keys)
    {
        var path = KeysPathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
                foreach (var key in keys) writer.Write(key + "\n");
                writer.Flush();
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

    /// <summary>The keys the move recorded, or null when there is no record that can be read (a move from before it was kept, or a damaged file).</summary>
    public static IReadOnlySet<string>? ReadKeys(string dataRoot)
    {
        var path = KeysPathFor(dataRoot);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxKeysBytes) return null;
            return File.ReadAllLines(path).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The file a move back records, beside the marker, the keys it ADDED (this PC had none) while the server's space was started again: see <see cref="AppendAdded"/>.</summary>
    public static string AddedPathFor(string dataRoot) => Path.Combine(dataRoot, Folder, AddedFileName);
    public const string AddedFileName = "moved.back-added";

    /// <summary>
    /// Records, flushed to disk before the write it is for, a key a move back is about to add to this PC while the server's space was started again. In such a space nothing can be
    /// told to be newer, so a move back adds only what this PC lacks; but a move back that stopped after adding (it refused at its last look, or was interrupted) must not have
    /// the next attempt take its additions for this PC's own choices and keep them over what the server holds now. Never shared and never in a backup; keys are one-way digests.
    /// Gone with the marker.
    /// </summary>
    public static void AppendAdded(string dataRoot, string key)
    {
        var path = AddedPathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(key + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The keys earlier attempts of the move back added (<see cref="AppendAdded"/>); none when there is no record, and an unreadable one is an error, not none.</summary>
    public static IReadOnlySet<string> ReadAdded(string dataRoot)
    {
        var path = AddedPathFor(dataRoot);
        if (!File.Exists(path)) return new HashSet<string>();
        if (new FileInfo(path).Length > MaxKeysBytes) throw new InvalidDataException("The record of added choices is too large.");
        return File.ReadAllLines(path).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The server a PC's own Messages Server is named by in the marker (SWITCHBOARD.md 10.7): the same word the client's replica is filed under
    /// (<c>ReplicatedStateStore.LocalServer</c>), never a fingerprint, so a marker naming it is never mistaken for a server on the network.
    /// </summary>
    public const string LocalServer = "local";

    /// <summary>The marker is about this PC's own server: its choices are (or are being) moved to <c>MessagesServer.exe</c> on this data root.</summary>
    public static bool IsLocalServer(MoveMark mark) => string.Equals(mark.Server, LocalServer, StringComparison.Ordinal);

    /// <summary>This root runs its own Messages Server: the switch was made and confirmed, so the window is that server's client and the server serves this root.</summary>
    public static bool RunsOwnServer(MoveMark mark) => mark.State == MoveState.Moved && IsLocalServer(mark);

    /// <summary>
    /// Whether <c>MessagesServer.exe</c> may open the real data of <paramref name="dataRoot"/> (SWITCHBOARD.md 10.7, the lift of 3.2b). Only a root that was
    /// switched to its own server (<see cref="RunsOwnServer"/>), for any start; and a root whose switch is under way, only for the window's own start that moves its
    /// choices (<paramref name="connects"/> false: no account connects and nothing starts on its own). Anything else, an unreadable marker included, stays refused.
    /// </summary>
    public static bool ServerMayOpen(string dataRoot, bool connects)
    {
        var mark = Read(dataRoot);
        return RunsOwnServer(mark) || mark.State == MoveState.Moving && IsLocalServer(mark) && !connects;
    }

    /// <summary>Whether the marker says the choices are on the server named by <paramref name="fingerprint"/> (lower-case hex).</summary>
    public static bool MovedTo(string dataRoot, string? fingerprint) =>
        fingerprint is { Length: > 0 } && Read(dataRoot) is { State: MoveState.Moved, Server: { } server } && string.Equals(server, fingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the marker, whole or not at all: beside itself, flushed, then moved over the old one.</summary>
    public static void Write(string dataRoot, MoveMark mark)
    {
        if (mark.State is not (MoveState.Moving or MoveState.Moved) || string.IsNullOrEmpty(mark.Id) || string.IsNullOrEmpty(mark.Server))
            throw new ArgumentException("Only a move in progress or a confirmed move is written.", nameof(mark));
        var path = PathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new Stored(1, mark.State == MoveState.Moved ? "moved" : "moving", mark.Id, mark.Server, mark.Epoch, mark.Rev, mark.At, mark.Key)));
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

    /// <summary>Removes the marker (a move back). A root with none is local; removing what is not there is done.</summary>
    public static void Remove(string dataRoot)
    {
        // The keys go first: a marker with no keys is a move that can still be brought back (by the position alone), but keys with no marker are only clutter.
        try { File.Delete(KeysPathFor(dataRoot)); }
        catch (Exception failure) when (failure is DirectoryNotFoundException or FileNotFoundException) { }
        try { File.Delete(AddedPathFor(dataRoot)); }
        catch (Exception failure) when (failure is DirectoryNotFoundException or FileNotFoundException) { }
        try { File.Delete(PathFor(dataRoot)); }
        catch (Exception failure) when (failure is DirectoryNotFoundException or FileNotFoundException) { }
    }
}
