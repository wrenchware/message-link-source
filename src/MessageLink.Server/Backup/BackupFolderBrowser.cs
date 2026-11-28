using System.Text;

namespace MessageLink.Server.Backup;

/// <summary>One folder the browser offers: its name as shown, where it is, a short word about it ("Removable drive"), and whether it is one of Linux's mounts (or its
/// root), which the page names by whether the server runs in a container. At the start a drive or mount also says its free space (and a local drive its size,
/// where known), what it is ("drive", "network" or "folder"), and whether it can be opened now (a mapped drive whose server is off can't).</summary>
internal sealed record BrowsedFolder(string Name, string Path, string? Note = null, bool Mount = false, long? Free = null, long? Total = null, string Kind = "folder",
    bool Available = true);

/// <summary>
/// What the browser shows at one place: the place (null at the start, where the drives or mapped folders are), the place above it ("" for the start, null at
/// the start itself), the folders in it, whether more were left out, how it went ("ok", or why it shows the start or nothing), and whether the place is in
/// storage a person mapped (false only for a Linux folder on no mount of its own, which a container loses when it is replaced). <paramref name="Root"/> is the
/// drive or mount the place is on, as the start lists it (<paramref name="RootName"/>), so the page can name each folder on the way; <paramref name="Backups"/>
/// says how many backup files the place holds and the newest one's time.
/// </summary>
internal sealed record FolderListing(string? Place, string? Up, IReadOnlyList<BrowsedFolder> Folders, string Outcome, bool More = false, bool Mapped = true,
    string? Root = null, string? RootName = null, BackupsFound? Backups = null);

/// <summary>How many backup files (.msgbackup) a folder holds, and when the newest was written: what Restore would find there.</summary>
internal sealed record BackupsFound(int Count, DateTimeOffset? Newest);

/// <summary>One place on a file server, for the browser: how it went ("ok" or why not), the shares or folders in it (the first <see cref="BackupFolderBrowser.Most"/>
/// by name; <paramref name="More"/> when some were left out), and the backups in it.</summary>
internal sealed record ShareListing(string Outcome, IReadOnlyList<string> Names, BackupsFound? Backups = null, bool More = false);

/// <summary>The backup location's browser, apart from any one server: which mounted folders Linux offers at the start.</summary>
internal static class BackupFolderBrowser
{
    /// <summary>The most folders one place lists; past that the person types the path.</summary>
    public const int Most = 500;

    // The kernel's own file systems, the container's root and memory-backed ones: a backup written to any of them is lost with the container or the boot.
    private static readonly HashSet<string> NotStorage = new(StringComparer.Ordinal)
    {
        "overlay", "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "mqueue", "cgroup", "cgroup2", "securityfs", "debugfs", "tracefs", "pstore", "bpf",
        "configfs", "fusectl", "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "ramfs", "squashfs", "efivarfs", "rpc_pipefs", "fuse.lxcfs",
    };

    private static readonly string[] SystemPlaces = ["/proc", "/sys", "/dev", "/run", "/etc", "/boot", "/usr", "/var/lib/docker"];

    /// <summary>
    /// The mount points in a /proc/self/mountinfo that hold storage a person put there: in a container, its mapped folders. A mapped file (resolv.conf) is
    /// among them until the caller keeps only folders.
    /// </summary>
    public static IReadOnlyList<string> MountedFolders(string mountinfo)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in mountinfo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ');
            var dash = Array.IndexOf(fields, "-");
            if (dash < 6 || dash + 1 >= fields.Length) continue;
            var place = Unescape(fields[4]);
            if (place is "/" || !place.StartsWith('/') || NotStorage.Contains(fields[dash + 1])) continue;
            // Read-only to this process (a Docker :ro mapping; under systemd's ProtectSystem=strict, everything but the service's own state folders).
            if (fields[5].Split(',').Contains("ro")) continue;
            if (SystemPlaces.Any(system => place == system || place.StartsWith(system + "/", StringComparison.Ordinal))) continue;
            found.Add(place);
        }
        return found.ToArray();
    }

    // mountinfo writes a space, tab, newline and backslash in a path as three octal digits after a backslash.
    private static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var plain = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && text.AsSpan(i + 1, 3) is var digits && !digits.ContainsAnyExceptInRange('0', '7'))
            {
                plain.Append((char)Convert.ToInt32(digits.ToString(), 8));
                i += 3;
            }
            else plain.Append(text[i]);
        }
        return plain.ToString();
    }
}
