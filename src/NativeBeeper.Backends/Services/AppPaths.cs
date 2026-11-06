namespace NativeBeeper.App.Services;

// Where this copy of the app keeps its data. Installed and ordinary builds use %LOCALAPPDATA%\Messages for Windows. Through
// 0.1.361 that folder was %LOCALAPPDATA%\NativeBeeper, the project's code name, which a person could see in File Explorer;
// the first launch of a later version renames it (Settle, DataFolderMove). A development build started with
// NATIVEBEEPER_DATA_ROOT (tools\Run-DevBuild.ps1) keeps everything in that folder instead and takes its own single-instance
// identity, so it runs beside the installed app without touching its sessions, caches or archive. A copy installed from the
// Microsoft Store (an MSIX package, installer\Build-StorePackage.ps1) keeps its data in the package's own LocalState folder
// and takes its own identity too, so it runs beside the unpackaged install and never reads or moves that copy's data.
internal static class AppPaths
{
    internal const string FolderName = "Messages for Windows";
    internal const string LegacyFolderName = "NativeBeeper";

    private static readonly string? Override = Environment.GetEnvironmentVariable("NATIVEBEEPER_DATA_ROOT") is { Length: > 0 } root
        ? Path.GetFullPath(root) : null;

    public static bool IsIsolated => Override is not null;

    // The package family this process runs as, or null for the unpackaged install and every development build. Read once:
    // a process's identity never changes while it runs.
    public static string? PackageFamily { get; } = PackageIdentity.CurrentFamilyName();
    // Installed from the Microsoft Store (or a locally installed test package of it). The Store updates it, and the package
    // declares its startup task and notification activator, so the registry paths the unpackaged install uses do not apply.
    public static bool IsPackaged => PackageFamily is not null;

    // The real data. Nothing here reads them: a probe or a development copy refuses all of them. A package's writes to
    // %LOCALAPPDATA% are redirected for new files but land in place for existing ones (MSIX file-system virtualization), so a
    // packaged copy opening the unpackaged folder would share the owner's live stores; it uses the folder Windows keeps for
    // the package instead, which is not virtualized, and which uninstalling the package removes.
    public static string DefaultRoot => PackageFamily is { } family ? PackageRoot(family) : UnpackagedRoot;
    public static string UnpackagedRoot => Path.Combine(LocalAppData, FolderName);
    public static string LegacyRoot => Path.Combine(LocalAppData, LegacyFolderName);
    internal static string PackageRoot(string family) => Path.Combine(LocalAppData, "Packages", family, "LocalState");
    // Whether a folder is the real data, or inside it, under either name. Naming the real folder in NATIVEBEEPER_DATA_ROOT is still
    // the real data (review, 2026-10-08), and so is a junction or symbolic link to it, or to a folder above it, or a folder that is
    // the same directory by another route (review, 2026-10-09): Path.GetFullPath resolves none of those, so the paths are compared
    // as the file system names them (FinalPath). A path that cannot be resolved counts as the real data, since the answer
    // decides whether a server may read the owner's accounts.
    public static bool IsRealData(string path) => IsRealData(path, [DefaultRoot, UnpackagedRoot, LegacyRoot]);

    // The roots are a parameter so the harness can stand in for the real folder; nothing else calls this.
    internal static bool IsRealData(string path, IReadOnlyList<string> realRoots)
    {
        try
        {
            var full = Normal(path);
            var reals = realRoots.Select(Normal).ToList();
            if (reals.Any(real => Inside(full, real))) return true;
            // Off Windows nothing here can resolve a link yet (the Linux server will need realpath): not knowing is the real data.
            if (!OperatingSystem.IsWindows()) return true;
            var resolved = FinalPath.Resolve(full);
            var identities = new List<FinalPath.Identity>();
            foreach (var real in reals)
            {
                if (Inside(resolved, FinalPath.Resolve(real))) return true;
                if (FinalPath.TryIdentity(real) is { } identity) identities.Add(identity);
            }
            // The same directory reached by a route whose name does not match (a mount point, a link in the real folder's own parents): the
            // given folder, or any folder above it, is the real folder itself.
            for (var ancestor = full; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (FinalPath.TryIdentity(ancestor) is { } identity && identities.Contains(identity)) return true;
            return false;
        }
        catch (Exception) { return true; }
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Inside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    // A given root that is a scratch folder: not the default, and not the real data named by hand.
    public static bool IsScratch => Override is { } given && !IsRealData(given);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Fixed once, by Settle or by the first read, and the same for the rest of the run: every store, log and helper of a
    // run uses one folder. It used to be computed as this type was first touched, and the app touches it before its data
    // lock is taken (the instance key's suffix), so the move has to be a step of its own. A host that never settles (the
    // harness, the diagnostics) reads whichever folder exists, moving nothing.
    private static string? root;
    private static readonly Lock Gate = new();
    public static string DataRoot => Volatile.Read(ref root) ?? Settle(null).Root;
    public static string Combine(params string[] parts) => Path.Combine([DataRoot, .. parts]);

    // A host whose own configuration names its folder (the Linux server's MESSAGES_DATA), before anything reads a path.
    // Nothing is moved and nothing here reads NATIVEBEEPER_DATA_ROOT: that variable means "a development copy" on Windows.
    public static void UseHostRoot(string folder)
    {
        lock (Gate)
        {
            if (root is not null) throw new InvalidOperationException("The data folder was already chosen.");
            Volatile.Write(ref root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        }
    }

    // How this run's folder was chosen, once Program.Main has settled it; App writes it to the log.
    public static DataFolderMove.Result? Settled { get; private set; }

    // Program.Main, holding the data lock and before anything reads a path: the one place that may move the folder.
    // helpersRunning answers whether helpers of this install folder from before this launch are still running; they would
    // hold files open in the old folder, so the move waits for a later launch. Null never moves anything.
    public static DataFolderMove.Result Settle(Func<bool>? helpersRunning)
    {
        lock (Gate)
        {
            // Read before this was asked to settle: whatever was read stands for the whole run.
            if (root is { } chosen) return new(chosen, "unchanged", "read_before_settle");
            // A package never had the old folder name, and the unpackaged install's folders are not its to move.
            var result = Override is { } isolated ? new DataFolderMove.Result(isolated, "isolated", "none")
                : IsPackaged ? new DataFolderMove.Result(DefaultRoot, Directory.Exists(DefaultRoot) ? "already" : "new", "packaged")
                : DataFolderMove.Decide(LegacyRoot, DefaultRoot, helpersRunning);
            Volatile.Write(ref root, result.Root);
            if (helpersRunning is not null) Settled = result;
            return result;
        }
    }

    // Appended to the instance key and the data lock: empty for the normal app, a stable tag for an isolated folder or a package. One folder
    // is one tag however it is written: with or without a trailing separator, in any case (review, 2026-10-08). Named objects are not
    // virtualized for a packaged desktop app, so without a tag of its own a Store copy would find the unpackaged install's lock and refuse
    // to start beside it.
    public static string InstanceSuffix { get; } = (Override is { } given ? Path.TrimEndingDirectorySeparator(given).ToUpperInvariant()
            : PackageFamily is { } family ? "PACKAGE:" + family.ToUpperInvariant() : null) is not { } tagged ? "" : Tag(tagged);

    private static string Tag(string tagged) =>
        "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tagged)))[..12];

    // The mutex the window holds for its whole run (Program.Main). MessagesServer.exe looks for it until the window takes the host lease itself
    // (SWITCHBOARD.md 3.2), so that a server never opens the stores and providers of a data root the window is using.
    public static string AppDataLockName => @"Local\NativeBeeper.Data" + InstanceSuffix;
    // The same lock of the copy started with folder as its NATIVEBEEPER_DATA_ROOT: how a sample's leftover folder is told from one a
    // sample still running holds (SampleMode).
    public static string DataLockNameFor(string folder) =>
        @"Local\NativeBeeper.Data" + Tag(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant());
    // Held by a running MessagesServer.exe for this data root (ServerShell). The app looks for it before it believes a held host lease means a server.
    public static string ServerLockName => @"Local\Messages.Server" + InstanceSuffix;
}

// The package identity Windows gave this process, if any. An unpackaged process gets APPMODEL_ERROR_NO_PACKAGE (15700) and
// null here; off Windows there is no such thing.
internal static class PackageIdentity
{
    public static string? CurrentFamilyName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != InsufficientBuffer || length == 0) return null;
            var buffer = new char[length];
            if (GetCurrentPackageFamilyName(ref length, buffer) != 0) return null;
            return new string(buffer, 0, (int)length - 1) is { Length: > 0 } name ? name : null;
        }
        catch (Exception) { return null; }
    }

    private const int InsufficientBuffer = 122;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, char[]? name);
}

// A path as the file system names it once every junction, symbolic link and subst is followed, and the identity of a folder (volume and file
// index), which two routes to one directory share. Both open the folder for its attributes only: no content is read and nothing is changed.
internal static class FinalPath
{
    internal readonly record struct Identity(uint Volume, uint IndexHigh, uint IndexLow);

    // The deepest part of the path that exists, resolved, with the part that does not exist appended unchanged. A missing part cannot be an
    // alias; anything else that stops the lookup (denied, a bad drive, a filesystem error) throws, and IsRealData reads that as real.
    public static string Resolve(string path)
    {
        var tail = new Stack<string>();
        for (var current = path; ; )
        {
            using (var handle = TryOpen(current))
                if (handle is not null)
                {
                    var final = Final(handle);
                    while (tail.Count > 0) final = Path.Combine(final, tail.Pop());
                    return Path.TrimEndingDirectorySeparator(final);
                }
            var parent = Path.GetDirectoryName(current) ?? throw new IOException("No part of the path exists.");
            tail.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    // Null when nothing is there; throws when it could not be told.
    public static Identity? TryIdentity(string path)
    {
        using var handle = TryOpen(path);
        if (handle is null) return null;
        if (!GetFileInformationByHandle(handle, out var information)) throw new IOException("The folder's identity could not be read.");
        return new(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    private const uint ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
    private const int FileNotFound = 2, PathNotFound = 3;

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? TryOpen(string path)
    {
        var handle = CreateFileW(path, ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (!handle.IsInvalid) return handle;
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is FileNotFound or PathNotFound) return null;
        throw new IOException("The path could not be opened.", error);
    }

    private static string Final(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new System.Text.StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length > buffer.Capacity) throw new IOException("The final path could not be read.");
        var name = buffer.ToString();
        return name.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + name[8..]
            : name.StartsWith(@"\\?\", StringComparison.Ordinal) ? name[4..] : name;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path, uint length, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation information);
}

// The one-time rename of the data folder from %LOCALAPPDATA%\NativeBeeper to %LOCALAPPDATA%\Messages for Windows. Both are
// in the same folder, so Directory.Move is a rename: nothing is copied, and it either happens whole or not at all. What the
// folder holds survives the rename: every path is worked out from the root each run (the helpers' too, given it as
// NATIVEBEEPER_DATA_ROOT), Windows' protection of the session files is bound to the user rather than to a path, and the
// browser profiles under it take their folder from the app each time they open.
internal static class DataFolderMove
{
    // outcome: moved | kept_old (this run uses the old folder and the next launch tries again) | already (the new folder
    // exists; old_remains when the old one does too, which is then left alone) | new (neither exists: a first install).
    internal sealed record Result(string Root, string Outcome, string Reason, string? Error = null)
    {
        public string LogLine => $"data folder outcome={Outcome} reason={Reason}" + (Error is null ? "" : $" error={Error}");
    }

    public static Result Decide(string legacy, string current, Func<bool>? helpersRunning, Action<string, string>? move = null)
    {
        var hasNew = Directory.Exists(current);
        var hasOld = Directory.Exists(legacy);
        // A later downgrade recreates the old folder; the new one, which has the accounts, still wins.
        if (hasNew) return new(current, "already", hasOld ? "old_remains" : "none");
        if (!hasOld) return new(current, "new", "none");
        if (helpersRunning is null) return new(legacy, "kept_old", "not_settling");
        bool running;
        try { running = helpersRunning(); }
        catch (Exception failure) { return new(legacy, "kept_old", "helpers_unknown", failure.GetType().Name); }
        if (running) return new(legacy, "kept_old", "helpers_running");
        try
        {
            (move ?? Directory.Move)(legacy, current);
            return new(current, "moved", "none");
        }
        // Something has a file open in it (a crashed run's browser, an Explorer window, a scan): this run uses it where it is.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return new(legacy, "kept_old", "move_failed", failure.GetType().Name);
        }
    }
}
