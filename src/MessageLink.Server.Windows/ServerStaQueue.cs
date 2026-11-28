using System.Runtime.InteropServices;
using MessageLink.WindowsPlatform;
using Windows.System;

namespace MessageLink.Server.Windows;

/// <summary>
/// The server's STA thread as the Google browser sees it (<see cref="IStaQueue"/>): a Windows.System.DispatcherQueue made on the thread that
/// pumps the shell's window, with no Windows App SDK (the SDK's own dispatcher is one more package; CoreMessaging makes the same kind of queue without it).
/// The shell's message loop drains it, so the renewal's WebView2 controllers live on the thread that also answers a quit, Restart Manager and
/// the end of the session. Proved by a spike before it was built.
/// </summary>
internal sealed class ServerStaQueue : IStaQueue
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    // Held for the thread's life: the controller keeps the queue alive.
    private readonly DispatcherQueueController controller;
    private readonly DispatcherQueue queue;

    private ServerStaQueue(DispatcherQueueController controller) { this.controller = controller; queue = controller.DispatcherQueue; }

    /// <summary>Makes the queue for the calling thread, which must be an STA thread that will pump messages. Throws if Windows will not make one.</summary>
    public static ServerStaQueue Create()
    {
        // DQTYPE_THREAD_CURRENT; DQTAT_COM_NONE because .NET has already made this thread an STA.
        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 0 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var raw));
        return new ServerStaQueue(WinRT.MarshalInspectable<DispatcherQueueController>.FromAbi(raw));
    }

    public bool OnThread => queue.HasThreadAccess;

    public bool TryEnqueue(Action work) => queue.TryEnqueue(() => work());

    /// <summary>Keeps the controller reachable for as long as the shell is (a collected controller would end the queue).</summary>
    public DispatcherQueueController Controller => controller;
}
