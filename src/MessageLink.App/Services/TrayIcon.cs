using System.Runtime.InteropServices;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services;

// What one tray callback means (Decode).
internal enum TrayEvent { None, Open, Menu }

// Notification-area icon: a click (or Enter/Space on the focused icon) opens Messages, and the context menu is built from
// the window's current state each time it opens (MenuProvider). A hidden window on the UI thread receives its messages.
// The icon switches to a red-dot variant while there are unread conversations. At sign-in the notification area may not
// be ready yet, so a failed add is retried every two seconds, fifteen times, and again whenever Explorer announces a
// new taskbar; the window decides what to do if it never appears.
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = 0x8000 + 1;
    public const int RetryIntervalMs = 2000, MaxRetries = 15;
    private const uint MouseLeftUp = 0x0202, MouseRightUp = 0x0205, ContextMenuMessage = 0x007B, SelectEvent = 0x0400, KeySelectEvent = 0x0401;
    private const uint AddIcon = 0, ModifyIcon = 1, DeleteIcon = 2, SetVersion = 4, MessageFlag = 1, IconFlag = 2, TipFlag = 4, ShowTipFlag = 0x80;
    private const uint ReturnCommand = 0x0100, RightButton = 0x0002, StringItem = 0, GrayedItem = 0x0001, PopupItem = 0x0010, SeparatorItem = 0x0800;
    private const uint TimerMessage = 0x0113, CloseMessage = 0x0010, NullMessage = 0;
    private const uint Version4 = 4;
    private const int SmallIconWidth = 49, ImageIcon = 1, RetryTimer = 1, TipLength = 127;
    private const uint LoadFromFile = 0x0010;

    private readonly WindowProcedure procedure;
    private readonly IntPtr window, normalIcon, unreadIcon;
    private readonly uint taskbarCreated;
    private string tip;
    private bool unread, paused, disposed, menuOpen, retrying;
    // Whether the icon is meant to be in the notification area: false between Hide and Show, when no add is tried and a new taskbar is not answered.
    private bool wanted;
    private uint version;
    private int retries, attempts;
    public event Action? Open;
    public event Action? Quit;
    // Raised when a chosen menu entry has a handler; without one, Open and Quit are raised for the two entries they name.
    public event Action<TrayMenuItem>? Command;
    // Raised when the icon appears after a failed add, or is lost and could not be put back.
    public event Action? AddedChanged;
    // Builds the menu as it opens, on this (the UI) thread. Null gives the plain Open / Quit menu.
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }
    // The menu when there is no provider, or building one fails.
    public IReadOnlyList<TrayMenuItem> Fallback { get; set; } = TrayMenuModel.Fallback;
    // Whether the icon is in the notification area. The window carries its title only while it is: the title is how this data root's server finds a
    // Message Link icon showing (TrayPresence.FindApp), so a window whose icon the shell refused is never taken for one.
    public bool Added
    {
        get => added;
        private set
        {
            added = value;
            if (window != IntPtr.Zero) SetWindowTextW(window, value ? presenceTitle : "");
        }
    }
    private bool added;
    private readonly string presenceTitle;
    // NOTIFYICON_VERSION_4 once the shell accepted it (keyboard access, NIN_SELECT), otherwise 0.
    public uint Version => version;
    // Every add tried so far, the constructor's included.
    public int Attempts => attempts;

    /// <summary>
    /// The window class of the app's tray window. Its title is <see cref="AppWindowTitle"/>, so the server of the same data root finds it (FindWindow) and knows a
    /// Message Link window is open there, whose icon is then the only one (owner, 2026-11-22). A class belongs to the process that registers it, so the server's own
    /// tray window registers <see cref="ServerWindowClass"/> instead and is never taken for the app's.
    /// </summary>
    public const string AppWindowClass = "MessageLink.Tray", ServerWindowClass = "MessageLinkServer.Tray";

    public static string AppWindowTitle => "Message Link" + AppPaths.InstanceSuffix;

    /// <summary>The app's icon: <see cref="AppWindowClass"/>, shown at once, saying "Message Link".</summary>
    public TrayIcon(string iconPath, string unreadIconPath) : this(iconPath, unreadIconPath, AppWindowClass, AppWindowTitle, TipText(0, false), show: true) { }

    /// <param name="show">False makes the window and its icon without adding it: <see cref="Show"/> adds it later.</param>
    public TrayIcon(string iconPath, string? unreadIconPath, string className, string windowTitle, string tipText, bool show)
    {
        procedure = WindowProc;
        tip = tipText;
        presenceTitle = windowTitle;
        wanted = show;
        var instance = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 0, Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            ClassExtra = 0, WindowExtra = 0, Instance = instance, Icon = IntPtr.Zero, Cursor = IntPtr.Zero, Background = IntPtr.Zero,
            MenuName = null, ClassName = className, SmallIcon = IntPtr.Zero
        };
        if (RegisterClassExW(ref windowClass) == 0) throw new InvalidOperationException("Tray window class could not be registered.");
        // A hidden top-level window (not message-only) also receives "TaskbarCreated" when Explorer restarts.
        window = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("Tray window could not be created.");
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var size = GetSystemMetrics(SmallIconWidth);
        normalIcon = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, size, size, LoadFromFile);
        unreadIcon = unreadIconPath is null ? IntPtr.Zero : LoadImageW(IntPtr.Zero, unreadIconPath, ImageIcon, size, size, LoadFromFile);
        if (!wanted) return;
        // No exception when the shell refuses: at sign-in the notification area may simply not be there yet.
        if (TryAdd()) Added = true;
        else StartRetrying();
    }

    /// <summary>Puts the icon in the notification area (again), retrying as the first add does when the shell refuses it.</summary>
    public void Show()
    {
        if (disposed || wanted) return;
        wanted = true;
        retries = 0;
        if (TryAdd()) BecameAdded();
        else StartRetrying();
    }

    /// <summary>Takes the icon out of the notification area; the window stays, so <see cref="Show"/> can put it back.</summary>
    public void Hide()
    {
        if (disposed || !wanted) return;
        wanted = false;
        StopRetrying();
        if (!Added) return;
        var data = Data(0);
        Shell_NotifyIconW(DeleteIcon, ref data);
        Added = false;
        version = 0;
        AppLog.Write("tray icon removed");
    }

    /// <summary>Sets the tooltip as it is (clipped to what the shell keeps), for an icon whose words are not the app's unread count.</summary>
    public void SetTip(string text)
    {
        if (disposed) return;
        var clipped = text.Length <= TipLength ? text : text[..TipLength];
        if (clipped == tip) return;
        tip = clipped;
        if (!Added) return;
        var data = Data(TipFlag);
        Shell_NotifyIconW(ModifyIcon, ref data);
    }

    // While attempts remain after this many retries.
    public static bool ShouldRetry(int retriesDone) => retriesDone < MaxRetries;

    // Keeps whatever SetState last said about the pause.
    public void SetUnread(int count) => SetState(count, paused);

    public void SetState(int unreadCount, bool pausedNow)
    {
        if (disposed) return;
        unread = unreadCount > 0;
        paused = pausedNow;
        tip = TipText(unreadCount, paused);
        if (!Added) return; // The next add carries it.
        var data = Data(IconFlag | TipFlag);
        Shell_NotifyIconW(ModifyIcon, ref data);
    }

    public static string TipText(int unreadCount, bool paused)
    {
        var text = unreadCount switch
        {
            <= 0 => "Message Link",
            1 => "Message Link · 1 unread conversation",
            _ => $"Message Link · {unreadCount} unread conversations"
        };
        if (paused) text += " · notifications paused";
        return text.Length <= TipLength ? text : text[..TipLength];
    }

    // What a callback means. Version 4 reports the event in LOWORD(lParam) and, for the menu, where to open it in
    // wParam; a click arrives as NIN_SELECT after its WM_LBUTTONUP, so the button-up itself does nothing (one click never
    // opens twice). Version 0 reports mouse messages in lParam and the menu opens at the cursor (int.MinValue).
    public static (TrayEvent Event, int X, int Y) Decode(uint version, nint wParam, nint lParam)
    {
        var message = (uint)(lParam & 0xFFFF);
        if (version >= Version4)
        {
            return message switch
            {
                SelectEvent or KeySelectEvent => (TrayEvent.Open, 0, 0),
                ContextMenuMessage => (TrayEvent.Menu, (int)(short)(wParam & 0xFFFF), (int)(short)((wParam >> 16) & 0xFFFF)),
                _ => (TrayEvent.None, 0, 0),
            };
        }
        return message switch
        {
            MouseLeftUp => (TrayEvent.Open, 0, 0),
            MouseRightUp or ContextMenuMessage => (TrayEvent.Menu, int.MinValue, int.MinValue),
            _ => (TrayEvent.None, 0, 0),
        };
    }

    private bool TryAdd()
    {
        attempts++;
        var data = Data(MessageFlag | IconFlag | TipFlag | ShowTipFlag);
        if (!Shell_NotifyIconW(AddIcon, ref data)) return false;
        // The version belongs to this add; a re-added icon starts at 0 again.
        var versioned = Data(0);
        versioned.TimeoutOrVersion = Version4;
        version = Shell_NotifyIconW(SetVersion, ref versioned) ? Version4 : 0;
        AppLog.Write($"tray icon version={version}");
        return true;
    }

    private void StartRetrying()
    {
        retries = 0;
        retrying = SetTimer(window, (UIntPtr)RetryTimer, RetryIntervalMs, IntPtr.Zero) != UIntPtr.Zero;
        if (!retrying) AppLog.Write("tray icon retry unavailable");
    }

    private void StopRetrying()
    {
        if (!retrying) return;
        retrying = false;
        KillTimer(window, (UIntPtr)RetryTimer);
    }

    private void Retry()
    {
        if (disposed || Added || !wanted) { StopRetrying(); return; }
        retries++;
        if (TryAdd()) { StopRetrying(); BecameAdded(); return; }
        if (!ShouldRetry(retries)) StopRetrying();
    }

    private void BecameAdded()
    {
        Added = true;
        AppLog.Write($"tray icon added attempt={attempts}");
        AddedChanged?.Invoke();
    }

    // Explorer restarted, or (on some changes of display) announced its taskbar again with the icon still in place.
    private void TaskbarRecreated()
    {
        if (disposed || !wanted) return;
        if (TryAdd()) { StopRetrying(); if (!Added) BecameAdded(); return; }
        // Still there: nothing was lost.
        var data = Data(IconFlag | TipFlag | ShowTipFlag);
        if (Added && Shell_NotifyIconW(ModifyIcon, ref data)) return;
        if (Added) { Added = false; AppLog.Write("tray icon lost"); AddedChanged?.Invoke(); }
        StopRetrying();
        StartRetrying();
    }

    private NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
        // Version 4 hides the standard tooltip unless it is asked for.
        Flags = (flags & TipFlag) != 0 && version >= Version4 ? flags | ShowTipFlag : flags,
        CallbackMessage = CallbackMessage,
        Icon = unread && unreadIcon != IntPtr.Zero ? unreadIcon : normalIcon, Tip = tip, State = 0, StateMask = 0, Info = "",
        TimeoutOrVersion = 0, InfoTitle = "", InfoFlags = 0, Item = Guid.Empty, BalloonIcon = IntPtr.Zero
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var (kind, x, y) = Decode(version, wParam, lParam);
            if (kind == TrayEvent.Open) Open?.Invoke();
            else if (kind == TrayEvent.Menu) ShowMenu(x, y);
            return IntPtr.Zero;
        }
        if (message == TimerMessage && wParam == (IntPtr)RetryTimer)
        {
            Retry();
            return IntPtr.Zero;
        }
        if (taskbarCreated != 0 && message == taskbarCreated)
        {
            TaskbarRecreated();
            return IntPtr.Zero;
        }
        // Leave close handling to the owning window or server shell; DefWindowProc would destroy this window without removing the icon.
        if (message == CloseMessage) return IntPtr.Zero;
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        // A second request while the menu is up (its own modal loop still pumps this window) is ignored.
        if (menuOpen || disposed) return;
        // Captured before the menu is shown: whatever changes while it is open, the id chosen means what was drawn.
        IReadOnlyList<TrayMenuItem> items;
        try { items = MenuProvider?.Invoke() ?? Fallback; }
        catch (Exception error)
        {
            AppLog.Write($"tray menu failed error={error.GetType().Name}");
            items = Fallback;
        }
        var menu = CreateMenu(items);
        if (menu == IntPtr.Zero) return;
        int command;
        menuOpen = true;
        try
        {
            if (x == int.MinValue || y == int.MinValue)
            {
                var point = new Point { X = 0, Y = 0 };
                GetCursorPos(ref point);
                (x, y) = (point.X, point.Y);
            }
            // Foreground first so the menu closes when clicking elsewhere.
            SetForegroundWindow(window);
            command = TrackPopupMenuEx(menu, ReturnCommand | RightButton, x, y, window, IntPtr.Zero);
            PostMessageW(window, NullMessage, IntPtr.Zero, IntPtr.Zero);
        }
        finally { menuOpen = false; DestroyMenu(menu); }
        if (TrayMenuModel.Find(items, command) is not { } chosen) return;
        if (Command is { } handler) handler(chosen);
        else if (chosen.Command == TrayCommand.Open) Open?.Invoke();
        else if (chosen.Command == TrayCommand.Quit) Quit?.Invoke();
    }

    // The HMENU for a model, submenus attached (DestroyMenu on the result frees them too). The real menu and the harness
    // both build it here. Zero when Windows could not create the menu.
    internal static IntPtr CreateMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            Append(menu, items);
            if (items.FirstOrDefault(item => item.IsDefault && item.Id != 0) is { } fallback) SetMenuDefaultItem(menu, (uint)fallback.Id, 0);
            return menu;
        }
        catch
        {
            DestroyMenu(menu);
            throw;
        }
    }

    private static void Append(IntPtr menu, IReadOnlyList<TrayMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator) { AppendMenuW(menu, SeparatorItem, UIntPtr.Zero, null); continue; }
            var state = item.Enabled ? 0u : GrayedItem;
            if (item.Children is { } children)
            {
                var sub = CreatePopupMenu();
                if (sub == IntPtr.Zero) continue;
                Append(sub, children);
                // Once attached, the parent owns it.
                if (!AppendMenuW(menu, PopupItem | state, (UIntPtr)(nuint)sub, item.Text)) DestroyMenu(sub);
                continue;
            }
            AppendMenuW(menu, StringItem | state, (UIntPtr)(uint)item.Id, item.Text);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopRetrying();
        var data = Data(0);
        if (Added) Shell_NotifyIconW(DeleteIcon, ref data);
        DestroyWindow(window);
        if (normalIcon != IntPtr.Zero) DestroyIcon(normalIcon);
        if (unreadIcon != IntPtr.Zero) DestroyIcon(unreadIcon);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size; public uint Style; public IntPtr Procedure; public int ClassExtra; public int WindowExtra; public IntPtr Instance;
        public IntPtr Icon; public IntPtr Cursor; public IntPtr Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id; public uint Flags; public uint CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowTextW(IntPtr window, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint elapse, IntPtr function);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(ref Point point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
