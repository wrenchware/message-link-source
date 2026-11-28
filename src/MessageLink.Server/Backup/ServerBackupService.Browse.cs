using MessageLink.App.Services.Backup;

namespace MessageLink.Server.Backup;

// The backup location's browser on the server's own disks: the server page shows what it lists, so a person picks a folder instead of typing its path. It runs as
// the server does, so it lists what the server itself can open. Nothing here is logged but an outcome: folder names are the person's own.
internal sealed partial class ServerBackupService
{
    private static readonly EnumerationOptions Listing = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        // Dot-folders count as hidden on Linux; Windows' own (System Volume Information, $RECYCLE.BIN) are hidden and system.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    // Backup files by their extension in any case, as Restore lists them: Linux matches a pattern by case unless told not to.
    private static readonly EnumerationOptions BackupFiles = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false, MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    // How long the start waits for the drives (or mounts) to say their names and free space: a mapped drive whose server is off answers only after Windows' own
    // long timeout, and a dead network mount on Linux never.
    private static readonly TimeSpan DriveWait = TimeSpan.FromSeconds(2);

    /// <summary>The folders at one place; an empty one is the start. A place that isn't there opens the nearest one above it that is.</summary>
    public FolderListing BrowseFolders(string? place)
    {
        place = place?.Trim().Trim('"');
        if (string.IsNullOrEmpty(place)) return Start("ok");
        string full;
        try
        {
            if (place.Length > 1024 || !Path.IsPathFullyQualified(place) || DevicePath(place)) return Start("not_full");
            full = Path.GetFullPath(place);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Start("not_full"); }
        // A typo (/backup for /backups) is said, not silently swapped for the folder above it.
        var outcome = "ok";
        while (!Directory.Exists(full))
            if (Path.GetDirectoryName(full) is { } above) (full, outcome) = (above, "nearest");
            else return Start("missing");
        var root = Path.GetPathRoot(full)!;
        if (full.Length > root.Length) full = Path.TrimEndingDirectorySeparator(full);
        if (Own(full)) return Start("internal");
        // Only where the server's own folders are somewhere below is each folder listed looked at by its final path too: elsewhere none can be one by any spelling
        // (but through a link, which Inside follows).
        var ownBelow = OwnBelow(full);
        var up = Path.GetDirectoryName(full) ?? "";
        var mounts = Mounts();
        var mapped = mounts is null || Mounted(full, mounts);
        var (top, topName) = RootOf(full, root, mounts);
        try
        {
            // Every name is read and sorted first, so the ones shown are the first by name; the server's own folders are left out only among those, since
            // telling them apart looks up each part of a path (a network drive's round trips).
            var folders = new DirectoryInfo(full).EnumerateDirectories("*", Listing).Select(folder => new BrowsedFolder(folder.Name, folder.FullName))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Where(folder => !Inside(folder.Path, paths.Data) && !Inside(folder.Path, paths.Keys) && !(ownBelow && OwnByFinalPath(folder.Path)))
                .Take(BackupFolderBrowser.Most + 1).ToArray();
            return new(full, up, folders.Take(BackupFolderBrowser.Most).ToArray(), outcome, folders.Length > BackupFolderBrowser.Most, mapped, top, topName, BackupsIn(full));
        }
        catch (UnauthorizedAccessException) { return new(full, up, [], "denied", Mapped: mapped, Root: top, RootName: topName); }
        catch (IOException) { return new(full, up, [], "unavailable", Mapped: mapped, Root: top, RootName: topName); }
    }

    // The drive or mount a place is on, and its name as the start lists it: Windows' drive by its label, Linux's deepest mount the place is in (else the root).
    private static (string Root, string Name) RootOf(string full, string root, IReadOnlyList<string>? mounts)
    {
        if (mounts is not null)
        {
            var mount = mounts.Where(mount => full == mount || full.StartsWith(mount + "/", StringComparison.Ordinal)).MaxBy(mount => mount.Length) ?? "/";
            return (mount, mount);
        }
        if (!OperatingSystem.IsWindows() || root.StartsWith(@"\\", StringComparison.Ordinal)) return (root, Path.TrimEndingDirectorySeparator(root));
        try { return (root, DriveFacts(new DriveInfo(root)).Name ?? root.TrimEnd('\\')); }
        catch (ArgumentException) { return (root, root.TrimEnd('\\')); }
    }

    // The backup files right in a folder (not its folders'), as Restore lists them: by their extension, never through a link.
    private static BackupsFound BackupsIn(string folder)
    {
        try
        {
            var count = 0;
            DateTimeOffset? newest = null;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + BackupFile.Extension, BackupFiles))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || !file.Name.EndsWith(BackupFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                var written = new DateTimeOffset(file.LastWriteTimeUtc);
                if (newest is null || written > newest) newest = written;
            }
            return new(count, newest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(0, null); }
    }

    /// <summary>Makes a folder by a plain name inside a place the browser shows: "made", "exists", "file" (a file has the name), "name", "missing", "internal",
    /// "denied" or "unsaved".</summary>
    public string MakeFolder(string? place, string? name)
    {
        name = name?.Trim();
        place = place?.Trim().Trim('"');
        if (!BackupDestination.Segment(name)) return "name";
        if (string.IsNullOrEmpty(place) || !Path.IsPathFullyQualified(place) || DevicePath(place)) return "missing";
        try
        {
            // Only inside a folder that is there: a missing Linux mount is never made on the container's own disk.
            if (!Directory.Exists(place)) return "missing";
            var full = Path.GetFullPath(place);
            var made = Path.Combine(full, name!);
            if (Own(made)) return "internal";
            if (Directory.Exists(made)) return "exists";
            if (File.Exists(made)) return "file";
            var mounted = Mounts() is { } before && Mounted(full, before);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(made);
            else if (MakeOne(made) is { } refused) return refused;
            // A mount taken away between the check and the folder leaves its empty mount point, so the folder went onto the container's own disk: taken back.
            if (mounted && Mounts() is { } after && !Mounted(full, after))
            {
                try { Directory.Delete(made); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return "missing";
            }
            log("server backup browse folder outcome=made");
            return "made";
        }
        catch (UnauthorizedAccessException) { return "denied"; }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { return "unsaved"; }
    }

    // One folder in a folder that must be there, never the folders above it: Directory.CreateDirectory would make a mount that went away between the check above
    // and this on the container's own disk. Null when made, else why not.
    private static string? MakeOne(string path)
    {
        if (mkdir(path, Convert.ToUInt32("777", 8)) == 0) return null;
        return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() switch
        {
            17 => "exists",           // EEXIST
            2 or 20 => "missing",     // ENOENT, ENOTDIR
            1 or 13 or 30 => "denied", // EPERM, EACCES, EROFS
            _ => "unsaved",
        };
    }

    // The mode is narrowed by the process's umask, as Directory.CreateDirectory's is.
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int mkdir([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);

    // Where a person starts: the drives on Windows, each with its free space; on Linux the mounted folders (in a container, the ones mapped in), then the whole tree.
    private FolderListing Start(string outcome)
    {
        var folders = new List<BrowsedFolder>();
        if (OperatingSystem.IsWindows())
        {
            // Each drive asked at once and for at most two seconds. One that doesn't answer in time is still listed, by its letter, without its free space.
            var drives = DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(drive => (Drive: drive, Facts: Task.Run(() => DriveFacts(drive)))).ToArray();
            Task.WaitAll(drives.Select(drive => (Task)drive.Facts).ToArray(), DriveWait);
            foreach (var (drive, facts) in drives)
            {
                var (name, ready, free, total) = facts.IsCompletedSuccessfully ? facts.Result : (drive.Name.TrimEnd('\\'), true, null, null);
                if (name is null) continue;
                var network = drive.DriveType == DriveType.Network;
                // A network drive's size is the share's quota or the whole array's, depending on the server: only its free space is said.
                folders.Add(new(name, drive.RootDirectory.FullName, drive.DriveType == DriveType.Removable ? "Removable drive" : null, Free: free, Total: network ? null : total,
                    Kind: network ? "network" : "drive", Available: ready));
            }
        }
        else
        {
            // Everything that touches a mount (whether it is there, whether it is the server's own, its free space) waits at most the same two seconds, since a
            // network mount whose server is gone never answers. One that doesn't answer in time is listed without its free space, unless its path is the server's own.
            var mounts = (Mounts() ?? []).Select(mount => (Mount: mount, Facts: Task.Run(() =>
                Directory.Exists(mount) && !Inside(mount, paths.Data) && !Inside(mount, paths.Keys) ? (Show: true, Free: FreeAt(mount)) : (Show: false, Free: (long?)null)))).ToArray();
            Task.WaitAll(mounts.Select(mount => (Task)mount.Facts).ToArray(), DriveWait);
            foreach (var (mount, facts) in mounts)
            {
                if (facts.IsCompletedSuccessfully ? !facts.Result.Show : Lexically(mount, paths.Data) || Lexically(mount, paths.Keys)) continue;
                folders.Add(new(mount, mount, Mount: true, Free: facts.IsCompletedSuccessfully ? facts.Result.Free : null));
            }
            folders.Add(new("/", "/", Mount: true));
        }
        return new(null, null, folders, outcome);
    }

    // As File Explorer names a drive ("Data (D:)", "Local Disk (C:)"), with its free space and size; no name for one with nothing in it (an empty card reader),
    // and a network drive whose server is off is named by its letter and isn't ready.
    private static (string? Name, bool Ready, long? Free, long? Total) DriveFacts(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        try
        {
            if (!drive.IsReady) return drive.DriveType == DriveType.Network ? (letter, false, null, null) : (null, false, null, null);
            var label = drive.VolumeLabel;
            var name = label.Length > 0 ? $"{label} ({letter})" : drive.DriveType == DriveType.Fixed ? $"Local Disk ({letter})" : letter;
            try { return (name, true, drive.AvailableFreeSpace, drive.TotalSize); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (name, true, null, null); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (letter, true, null, null); }
    }

    // Inside a folder by its path alone, for a mount that didn't answer: links can't be followed there.
    private static bool Lexically(string path, string root)
    {
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == boundary || path.StartsWith(boundary + "/", StringComparison.Ordinal);
    }

    // The free space where a Linux folder is (statvfs), or null when it can't be told.
    private static long? FreeAt(string folder)
    {
        try { return new DriveInfo(folder).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // Linux's mounted folders, or null where there are none to tell apart (Windows).
    private static IReadOnlyList<string>? Mounts()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return BackupFolderBrowser.MountedFolders(File.ReadAllText("/proc/self/mountinfo")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
