using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MessageLink.Terminal.Connection;

/// <summary>
/// Starts this PC's own <c>MessageLinkServer.exe --background</c> from beside mlink, as the app starts it for a data folder that runs its own server: headless,
/// nobody's to stop, with mlink's environment (so a development copy's <c>MESSAGELINK_DATA_ROOT</c> goes with it) and nothing of mlink's inherited. It is
/// started outside any job mlink is in (<c>CREATE_BREAKAWAY_FROM_JOB</c>), because a terminal puts what it runs in a job that ends with its window, and closing
/// the terminal must not stop the server. A job that forbids breakaway answers access denied, and the start is then made the ordinary way.
/// </summary>
internal static class LocalServerStart
{
    public const string FileName = "MessageLinkServer.exe";
    /// <summary>Development only, as the app reads it: the server to start in place of the one beside mlink.</summary>
    public const string OverrideVariable = "MESSAGELINK_SERVER_EXE";
    public const string BackgroundArgument = "--background";

    public static ServerStart StartBeside(string baseDirectory)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "not_windows");
        var executable = Locate(baseDirectory, OverridePath());
        if (executable is null) return new(false, "missing");
        try { return new(true, null, Spawn(executable, [BackgroundArgument], Path.GetDirectoryName(executable))); }
        catch (Exception failure) { return new(false, failure.GetType().Name); }
    }

    // An override must be a file of the server's name that exists; otherwise nothing is started rather than the installed server in its place.
    internal static string? Locate(string baseDirectory, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            string full;
            try { full = Path.GetFullPath(overridePath); }
            catch (Exception) { return null; }
            return string.Equals(Path.GetFileName(full), FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        var beside = Path.Combine(baseDirectory, FileName);
        return File.Exists(beside) ? beside : null;
    }

    // A Release build always starts the server beside it: a variable must not be able to name another program to run with the user's data folder.
    private static string? OverridePath()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(OverrideVariable);
#else
        return null;
#endif
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    private const uint CreateNoWindow = 0x08000000, CreateBreakawayFromJob = 0x01000000;
    private const int AccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static Task<int> Spawn(string executable, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var line = new StringBuilder(Quote(executable));
        foreach (var argument in arguments) line.Append(' ').Append(Quote(argument));
        if (!TryCreate(executable, line.ToString(), workingDirectory, CreateNoWindow | CreateBreakawayFromJob, out var information, out var error))
        {
            if (error != AccessDenied) throw new Win32Exception(error);
            if (!TryCreate(executable, line.ToString(), workingDirectory, CreateNoWindow, out information, out error)) throw new Win32Exception(error);
        }
        CloseHandle(information.Thread);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(information.Process, ownsHandle: true) };
        var handle = information.Process;
        ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            var code = GetExitCodeProcess(handle, out var value) ? unchecked((int)value) : -1;
            wait.Dispose();
            exited.TrySetResult(code);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return exited.Task;
    }

    private static bool TryCreate(string executable, string line, string? workingDirectory, uint flags, out ProcessInformation information, out int error)
    {
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var ok = CreateProcess(executable, new StringBuilder(line), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
            string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref startup, out information);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') quoted.Append('\\', slashes * 2 + 1).Append('"');
            else quoted.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}
