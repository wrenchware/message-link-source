using System.Diagnostics;
using NativeBeeper.App.Services;
using NativeBeeper.WindowsPlatform;

namespace NativeBeeper.Server;

/// <summary>What the shell needs from the server it carries: whether a quit may go ahead, and how to stop.</summary>
internal interface IServerLifecycle
{
    /// <summary>Null when a quit asked for by a tool may go ahead; otherwise the one word that says why not (logged, then the tool waits and stops what is left).</summary>
    string? RefuseQuit();

    /// <summary>Stops the server's work, bounded by <paramref name="budget"/>. Called once; it must not throw.</summary>
    Task StopAsync(string reason, ShutdownBudget budget);

    /// <summary>Windows asked to end the session and the server agreed (the run marker says so until it is cancelled).</summary>
    void SessionQuery();

    void SessionCancel();
}

/// <summary>
/// The server's one STA thread: a Win32 message loop and a top-level window that is never shown. It is the process's address for the things only
/// a window can receive: <c>MessagesServer.exe --quit</c> from another process (a registered message), Restart Manager's close when an installer
/// replaces the program, and the end of the Windows session (SWITCHBOARD.md stage 3). A message-only window would hear none of the last two,
/// since Windows broadcasts them to top-level windows. The Google renewal's WebView2 controllers live on this thread too: it carries a
/// DispatcherQueue (<see cref="Sta"/>) that the same loop drains, and a synchronization context that brings awaits back to it (3.1c).
/// </summary>
internal sealed class ServerShell
{
    public const string WindowClassName = "MessagesServer";
    private const uint StoppedMessage = Native.WM_APP + 1;

    public static string WindowTitle => "Messages Server" + AppPaths.InstanceSuffix;
    public static string MutexName => AppPaths.ServerLockName;
    public static uint QuitMessage => Native.RegisterWindowMessageW("Messages.Server.Quit" + AppPaths.InstanceSuffix);

    // Held for the process: the window class keeps its pointer.
    private static readonly Native.WindowProcedure Procedure = Dispatch;
    private static ServerShell? current;

    private readonly uint quitMessage = QuitMessage;
    private readonly IServerLifecycle lifecycle;
    private readonly Action<string> log;
    private readonly object stopGate = new();
    private nint window;
    private Task? stopping;
    // Set by the stop itself, so a StoppedMessage that anything else posts to the window is not taken for its end.
    private volatile bool finished;

    public ServerShell(IServerLifecycle lifecycle, Action<string> log) { this.lifecycle = lifecycle; this.log = log; }

    /// <summary>The shell's thread as a queue of work, once <see cref="Run"/> has made it (it is set before <c>started</c> is called). Null before.</summary>
    public IStaQueue? Sta { get; private set; }

    /// <summary>Creates the window, calls <paramref name="started"/> from the thread that will pump, and pumps until the window is destroyed. Call from an STA thread.</summary>
    public int Run(Action started)
    {
        current = this;
        var step = new StepClock(log);
        var instance = Native.GetModuleHandleW(null);
        var windowClass = new Native.WindowClass
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WindowClass>(),
            Procedure = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance,
            ClassName = WindowClassName,
        };
        if (Native.RegisterClassExW(ref windowClass) == 0) { log("server shell failed step=class"); return 1; }
        // Never shown: no WS_VISIBLE, no size, no ShowWindow.
        window = Native.CreateWindowExW(0, WindowClassName, WindowTitle, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (window == 0) { log("server shell failed step=window"); return 1; }
        step.Took("window");
        // Before anything can ask for the browser. WebView2's objects belong to the thread that makes them, and without a context of its own
        // this thread would send every await in the renewal to the pool (WinUI installs one for the app's thread by itself).
        try
        {
            var queue = ServerStaQueue.Create();
            SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext(queue));
            Sta = queue;
        }
        catch (Exception failure) { log($"server shell failed step=queue error={failure.GetType().Name}"); return 1; }
        step.Took("queue");
        started();
        while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
        return 0;
    }

    /// <summary>Stops the server from inside (a failed start), as a quit would. Only after <see cref="Run"/> has made the window.</summary>
    public void Stop(string reason) => BeginStop(reason, ShutdownBudget.Normal);

    /// <summary>Starts the stop once; whichever path asks first decides the budget, and the others wait on the same task.</summary>
    private Task BeginStop(string reason, ShutdownBudget budget)
    {
        // Two paths can ask at once (a quit and the end of the session, or a failed start): the check and the start are one step.
        lock (stopGate)
        {
            if (stopping is not null) return stopping;
            var hidden = window;
            return stopping = Task.Run(async () =>
            {
                try { await lifecycle.StopAsync(reason, budget); }
                catch (Exception failure) { log($"server stop failed error={failure.GetType().Name}"); }
                finally
                {
                    finished = true;
                    Native.PostMessageW(hidden, StoppedMessage, 0, 0);
                }
            });
        }
    }

    private static nint Dispatch(nint handle, uint message, nint wParam, nint lParam)
    {
        var shell = current;
        if (shell is null) return Native.DefWindowProcW(handle, message, wParam, lParam);
        if (message == shell.quitMessage) return shell.QuitFromCommand();
        switch (message)
        {
            // Any process can post a message to this window; only the stop's own end destroys it.
            case StoppedMessage:
                if (shell.finished) Native.DestroyWindow(handle);
                return 0;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return 0;
            // Restart Manager closes a program by asking its windows to close; destroying the window here would end the loop with nothing stopped.
            case Native.WM_CLOSE:
                shell.log("server close requested");
                shell.BeginStop("close", ShutdownBudget.Normal);
                return 0;
            case Native.WM_QUERYENDSESSION:
                shell.lifecycle.SessionQuery();
                return 1;
            case Native.WM_ENDSESSION:
                if (wParam == 0) { shell.lifecycle.SessionCancel(); return 0; }
                shell.EndSession();
                return 0;
        }
        return Native.DefWindowProcW(handle, message, wParam, lParam);
    }

    // The tray's Quit without anything shown (as the app's Messages.exe --quit): where it would have to ask, this refuses and says why in the log.
    // The window procedure's answer goes back to the process that sent the message (MessagesServer.exe --quit), so a refusal is told at once rather
    // than waited out: QuitTaken when the stop is under way (begun now or earlier), QuitRefused when something is still being sent.
    public const nint QuitTaken = 1, QuitRefused = 2;

    private nint QuitFromCommand()
    {
        if (Volatile.Read(ref stopping) is not null) { log("quit command ignored shutting_down=True"); return QuitTaken; }
        if (lifecycle.RefuseQuit() is { } reason) { log($"quit command refused reason={reason}"); return QuitRefused; }
        log("quit command outcome=closing");
        BeginStop("command", ShutdownBudget.Normal);
        return QuitTaken;
    }

    // Windows may end the process the moment this returns, so the close on its short budget is waited for here, as the app's session end waits: with
    // a reason Windows shows while it waits, and pumping messages, so that anything that needs this thread (a WebView2 renewal in the restore) can finish.
    private void EndSession()
    {
        log("session end closing");
        var budget = ShutdownBudget.Session;
        var limit = budget.Total + TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();
        var blocked = Native.ShutdownBlockReasonCreate(window, "Closing connections");
        Task closing;
        try { closing = BeginStop("session_end", budget); }
        catch (Exception) { closing = Task.CompletedTask; }
        var quit = Pump(closing, clock, limit);
        if (blocked) Native.ShutdownBlockReasonDestroy(window);
        if (!closing.IsCompleted) log($"session end unfinished ms={clock.ElapsedMilliseconds}");
        // The window the stop destroyed asked the loop to end while this one was running; the real loop must see that too.
        if (quit is { } code) Native.PostQuitMessage(code);
    }

    // The quit code if WM_QUIT was taken off the queue here, so it can be posted again for the real message loop.
    private static int? Pump(Task work, Stopwatch clock, TimeSpan limit)
    {
        while (!work.IsCompleted && clock.Elapsed < limit)
        {
            _ = Native.MsgWaitForMultipleObjectsEx(0, null, 50, Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
            while (!work.IsCompleted && clock.Elapsed < limit && Native.PeekMessageW(out var message, 0, 0, 0, Native.PM_REMOVE))
            {
                if (message.Id == Native.WM_QUIT) return (int)message.WParam;
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }
        }
        return null;
    }
}
