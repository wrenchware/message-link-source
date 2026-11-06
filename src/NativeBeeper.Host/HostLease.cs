namespace NativeBeeper.Host;

/// <summary>
/// The right to host the backends for one data root (SWITCHBOARD.md stage 3, "one host per data root"): whichever process runs the providers, the
/// in-process app or <c>MessagesServer.exe</c>, holds this for as long as it does. Two hosts on one account is the worst failure here (two
/// Messenger devices, a second Instagram login), so a second one does not start.
/// </summary>
/// <remarks>
/// The lease is the operating system's lock on <c>host.lock</c>, opened with no sharing and held open, not what the file says. That is why a
/// file left by a process that died is simply taken: the lock went with the process, and nothing here has to judge whether a recorded owner
/// is still alive. It works across Windows sessions, where a <c>Local\</c> mutex does not. The file is never deleted, since removing it while
/// another process is opening it is a race for nothing.
/// </remarks>
public sealed class HostLease : IDisposable
{
    public const string FileName = "host.lock";

    private readonly FileStream held;

    private HostLease(FileStream held) => this.held = held;

    /// <summary>Takes the lease for <paramref name="dataRoot"/>, or returns null when another process (or lease) holds it. Anything else that goes wrong is thrown.</summary>
    public static HostLease? TryAcquire(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        try
        {
            return new HostLease(new FileStream(Path.Combine(dataRoot, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION: held by another process, or by another lease in this one. Off Windows .NET takes
        // an flock for FileShare.None, and a held one is EWOULDBLOCK (11 on Linux), carried as the exception's HResult.
        catch (IOException failure) when (OperatingSystem.IsWindows() ? (failure.HResult & 0xFFFF) is 32 or 33 : failure.HResult == 11)
        {
            return null;
        }
    }

    public void Dispose() => held.Dispose();
}
