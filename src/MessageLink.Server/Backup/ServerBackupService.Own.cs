using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MessageLink.Server.Backup;

// The server's own data and keys folders are never a backup location, nor browsed or written into from the page. Windows names one folder many ways: a short
// 8.3 name (MESSAG~1), a substituted drive (subst X: …), a device path (\\?\, \\.\, \??\), a junction. Links are followed part by part (Inside); the rest are
// told apart by the folder's final path, as the file system names it once opened.
internal sealed partial class ServerBackupService
{
    // A device path names a folder by a spelling of its own: refused where a folder is typed.
    internal static bool DevicePath(string path) =>
        OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal));

    // In the server's own data or keys folder, by its links or (Windows) its final path.
    private bool Own(string path) => Inside(path, paths.Data) || Inside(path, paths.Keys) || OwnByFinalPath(path);

    private bool OwnByFinalPath(string path) =>
        OperatingSystem.IsWindows() && FinalPath(path) is { } final && (Under(final, FinalPath(paths.Data)) || Under(final, FinalPath(paths.Keys)));

    // Whether the server's own data or keys folder is in this folder, at any depth.
    private bool OwnBelow(string path) =>
        OperatingSystem.IsWindows() && FinalPath(path) is { } final && (Under(FinalPath(paths.Data), final) || Under(FinalPath(paths.Keys), final));

    private static bool Under(string? path, string? folder)
    {
        if (path is null || folder is null) return false;
        folder = Path.TrimEndingDirectorySeparator(folder);
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // On a Linux mount (or one of its folders).
    private static bool Mounted(string path, IReadOnlyList<string> mounts) => mounts.Any(mount => path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal));

    /// <summary>
    /// A Windows folder's final path: the nearest part of it that is there, opened and named by the file system (GetFinalPathNameByHandle), with the parts not
    /// made yet after it. Null where it can't be told (Linux, or nothing of it opens).
    /// </summary>
    internal static string? FinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var rest = new Stack<string>();
        var at = path;
        try
        {
            while (!Directory.Exists(at))
            {
                if (Path.GetDirectoryName(at) is not { } up) return null;
                rest.Push(Path.GetFileName(at));
                at = up;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException) { return null; }
        using var handle = CreateFileW(at, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var final = new string(buffer, 0, (int)length);
        final = final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        while (rest.TryPop(out var part)) final = Path.Combine(final, part);
        return final;
    }

    private const uint FileShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;

    // Opening a folder (not a file) needs backup semantics; no access is asked for, only its name.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
