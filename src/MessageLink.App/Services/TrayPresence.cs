using System.Runtime.InteropServices;

namespace MessageLink.App.Services;

/// <summary>
/// One tray icon while anything runs (owner, 2026-11-22): a Message Link window owns it; this PC's own server shows the same icon whenever it runs with no window
/// open on its data root, and hides it again once one is. The two find each other by their hidden windows, so neither waits on the other or polls: the server
/// looks for the app's tray window when it starts and whenever the app it watched has ended, and the app tells the server's window when its own tray window is
/// made. Plain Win32, no WinUI, so the server links this file as well.
/// </summary>
internal static class TrayPresence
{
    /// <summary>The server's hidden window (<c>ServerShell</c>): its class, and its title, which names the data root.</summary>
    public const string ServerShellClass = "MessageLinkServer";

    public static string ServerShellTitle => "Message Link Server" + AppPaths.InstanceSuffix;

    /// <summary>Posted by the app to the server's window once its tray window is made: the server looks again and hides its icon.</summary>
    public static uint AppArrivedMessage => RegisterWindowMessageW("Messages.Server.AppTray" + AppPaths.InstanceSuffix);

    /// <summary>The process of the Message Link window open on this data root, by its tray window; null when there is none.</summary>
    public static int? FindApp()
    {
        var window = FindWindowW(TrayIcon.AppWindowClass, TrayIcon.AppWindowTitle);
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out var id);
        return id == 0 ? null : (int)id;
    }

    /// <summary>Tells this data root's server, if one runs, that a Message Link window now owns the icon. True when there was a server window to tell.</summary>
    public static bool AnnounceToServer()
    {
        var server = FindWindowW(ServerShellClass, ServerShellTitle);
        return server != 0 && PostMessageW(server, AppArrivedMessage, (nint)Environment.ProcessId, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
