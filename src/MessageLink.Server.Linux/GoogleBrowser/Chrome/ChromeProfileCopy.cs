namespace MessageLink.Server.Linux;

/// <summary>
/// A copy of Chrome's profile folder, so that a sign-in can be made on the copy and replace the original only when the phone has accepted it (SWITCHBOARD.md 5.4, Chrome on the server). Made while Chrome
/// is closed, so the copy is whole. Left out: the caches (large, and rebuilt), the display's key and number, the lock and socket Chrome leaves for the next start, our own cookie copies, and the
/// browser's home (its caches; the launcher makes it again). Symbolic links are never followed or copied. Every folder and file is the server account's alone.
/// </summary>
internal static class ChromeProfileCopy
{
    private static readonly string[] SkippedFolders = ["Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache", "Crashpad", "component_crx_cache", "Service Worker", ".home", ChromeCookieStore.CopyFolder];
    private static readonly string[] SkippedFiles = [".xauth", ".display", ".display-number", ".display-number.tmp", "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile"];

    /// <exception cref="IOException">The copy could not be made; what was copied so far is left for the caller to delete with the destination.</exception>
    public static void Copy(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new IOException("profile_missing");
        CopyFolder(new DirectoryInfo(source), destination, top: true);
    }

    private static void CopyFolder(DirectoryInfo from, string to, bool top)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(to);
        else Directory.CreateDirectory(to, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in from.EnumerateFiles())
        {
            if (SkippedFiles.Contains(file.Name, StringComparer.Ordinal) || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null) continue;
            using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var output = new FileStream(Path.Combine(to, file.Name), options);
            input.CopyTo(output);
        }
        foreach (var folder in from.EnumerateDirectories())
        {
            if (SkippedFolders.Contains(folder.Name, StringComparer.Ordinal) || folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || folder.LinkTarget is not null) continue;
            CopyFolder(folder, Path.Combine(to, folder.Name), top: false);
        }
    }
}
