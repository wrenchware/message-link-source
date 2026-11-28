using System.Runtime.InteropServices;

namespace MessageLink.Server.Windows;

// The few user32 calls the hidden window needs. Plain signatures: no strings cross except the class and title, which are Unicode.
internal static class Native
{
    public const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_QUIT = 0x0012, WM_APP = 0x8000;
    public const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x4, PM_REMOVE = 0x1;

    public delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[]? handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShutdownBlockReasonCreate(nint window, string reason);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShutdownBlockReasonDestroy(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    // Sent, not posted, for the quit command: the window procedure's answer says whether the server took it (ServerShell.QuitTaken / QuitRefused).
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeoutMilliseconds, out nint result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern nint FindWindowW(string className, string? title);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern nint GetModuleHandleW(string? name);
}
