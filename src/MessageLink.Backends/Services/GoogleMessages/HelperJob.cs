using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MessageLink.GoogleMessages.Spike;

// Ties every helper process to this one. Windows ends every process in a kill-on-close job the moment the last handle
// to the job closes, and this process holds the only handle, so however it ends — a normal exit, a crash, a fail-fast,
// Task Manager, the installer's Restart Manager — its helpers end with it. A helper sees end of input only between the
// calls it runs inline, so without this one that was mid-call outlived the app and a relaunch started a second helper
// on the same account's stores.
//
// Only helpers join, never this process, and never the WebView2 or browser sign-in windows: Chromium and Firefox build
// their own sandbox jobs, and a sign-in is not something to cut short because the app closed. No helper starts children
// of its own. A helper runs for the moment between Process.Start and the assignment outside the job, which is harmless
// for the same reason; Process.Start cannot create a process suspended to close that gap.
internal static class HelperJob
{
    // Created once, rooted in a static for the life of the process, and never closed: closing it is what kills every
    // helper, so a collected or disposed handle would drop every provider in the middle of a session. The extra
    // reference makes even an accidental Dispose leave the handle open.
    private static readonly Lazy<SafeFileHandle?> Shared = new(() =>
    {
        var job = Create(out var error);
        if (job is null) HelperConnection.Trace?.Invoke($"helper job unavailable reason={error}");
        else
        {
            var held = false;
            job.DangerousAddRef(ref held);
            HelperConnection.Trace?.Invoke("helper job ready");
        }
        return job;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // "ok", "unavailable" (no job could be made), or "failed_<code>" (Windows refused this process; a restrictive job
    // around the app can do that). A helper that is not in the job behaves as every helper did before it existed.
    // "parent" on Linux, where the helper watches this process instead (Start); "none" elsewhere.
    public static string Assign(Process process) => OperatingSystem.IsWindows() ? Assign(Shared.Value, process) : OperatingSystem.IsLinux() ? "parent" : "none";

    // The Linux side of the job (SWITCHBOARD.md 4.2). Each helper is started in this process's group, with this process's
    // id in MESSAGES_HELPER_PARENT_PID (HelperConnection.StartInfo); a helper given it asks Linux for a parent-death
    // signal and also ends when its parent changes (the Messenger helper's parentwatch_linux.go). Linux sends that signal
    // when the *thread* that started the child ends, not the process, and a pool thread retires after a while idle: a
    // helper started from one would be killed in the middle of a session. So on Linux every helper is started from one
    // thread that lives exactly as long as this process.
    public static Process Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsLinux()) return Process.Start(start) ?? throw new IOException("Helper could not start.");
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        Starter.Value.Add((start, started));
        // The start's own exception (a Win32Exception names why), as a direct Process.Start would have thrown it.
        return started.Task.GetAwaiter().GetResult();
    }

    private static readonly Lazy<System.Collections.Concurrent.BlockingCollection<(ProcessStartInfo Start, TaskCompletionSource<Process> Started)>> Starter = new(() =>
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<(ProcessStartInfo Start, TaskCompletionSource<Process> Started)>();
        // A background thread, so it never keeps the process alive; it ends only when the process does, and then every helper's signal is due.
        new Thread(() =>
        {
            foreach (var (start, started) in queue.GetConsumingEnumerable())
            {
                try { started.SetResult(Process.Start(start) ?? throw new IOException("Helper could not start.")); }
                catch (Exception failure) { started.SetException(failure); }
            }
        }) { IsBackground = true, Name = "helper starts" }.Start();
        return queue;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string Assign(SafeFileHandle? job, Process process) =>
        job is null || job.IsInvalid ? "unavailable"
        : AssignProcessToJobObject(job, process.SafeHandle) ? "ok" : "failed_" + Marshal.GetLastPInvokeError();

    // Not inheritable (no security attributes): Process.Start with redirected streams lets the child inherit every
    // inheritable handle, and a helper holding a copy of its own job's handle would keep the job from ever closing.
    internal static SafeFileHandle? Create(out int error)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { error = Marshal.GetLastPInvokeError(); job.Dispose(); return null; }
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        { error = Marshal.GetLastPInvokeError(); job.Dispose(); return null; }
        error = 0;
        return job;
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
