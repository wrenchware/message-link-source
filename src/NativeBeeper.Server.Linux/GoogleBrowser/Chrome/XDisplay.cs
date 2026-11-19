using System.Runtime.InteropServices;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// One X display, as the server's Chrome sits on it (SWITCHBOARD.md 5.4, Chrome on the server): what the person sees is a picture of its pixels, and what the person does is real X input
/// (the XTEST extension), so the browser is never driven through DevTools and nothing marks the page as automated. A small interface so that the pointer, key and text logic can be checked
/// against a fake; the real one is <see cref="XcbDisplay"/>.
/// </summary>
internal interface IXDisplay : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>
    /// The whole display as tightly packed 32-bit B, G, R, unused pixels (<c>Width * Height * 4</c> bytes), or null when it could not be read this time. The array
    /// may be the one the last capture returned, filled again: it is the caller's only until its next Capture (one caller, the screen stream).
    /// </summary>
    byte[]? Capture();
    /// <summary>The pointer to pixel (<paramref name="x"/>, <paramref name="y"/>) of the display.</summary>
    void MotionTo(int x, int y);
    /// <summary>X button 1 left, 2 middle, 3 right, 4 and 5 the wheel up and down, 6 and 7 left and right.</summary>
    void Button(int button, bool press);
    void Key(int keycode, bool press);
    /// <summary>The keyboard as the server has it now (read when the display was opened, and kept in step by <see cref="SetKeysym"/>).</summary>
    KeyboardMap Keyboard { get; }
    /// <summary>Puts <paramref name="keysym"/> (0: none) on every level of <paramref name="keycode"/>.</summary>
    void SetKeysym(int keycode, long keysym);
    /// <summary>Gives the browser's window the keyboard focus (there is no window manager to do it). False when no window was found.</summary>
    bool FocusBrowser();
    /// <summary>Sends everything queued and waits until the X server has handled it (the browser then has the events).</summary>
    void Sync();
}

/// <summary>Opens the display Chrome is on. The real one is <see cref="XcbConnector"/>; the harness gives a fake.</summary>
internal interface IXConnector
{
    /// <exception cref="IOException">The display cannot be reached or refuses the key (a fixed word, <c>x_unavailable</c>).</exception>
    IXDisplay Open(int displayNumber, string authorityFile);
}

/// <summary>The keys of an X keyboard: which keysyms each keycode makes at each level. Plain data, read once.</summary>
internal sealed class KeyboardMap(int firstKeycode, int perKeycode, long[] keysyms)
{
    public int FirstKeycode { get; } = firstKeycode;
    public int PerKeycode { get; } = perKeycode;
    public int LastKeycode => FirstKeycode + Keysyms.Length / Math.Max(1, PerKeycode) - 1;
    public long[] Keysyms { get; } = keysyms;

    /// <summary>The keysym of <paramref name="keycode"/> at <paramref name="level"/> (0 unshifted, 1 shifted), 0 when there is none.</summary>
    public long At(int keycode, int level)
    {
        if (keycode < FirstKeycode || keycode > LastKeycode || level < 0 || level >= PerKeycode) return 0;
        return Keysyms[(keycode - FirstKeycode) * PerKeycode + level];
    }

    /// <summary>The lowest keycode with <paramref name="keysym"/> at <paramref name="level"/>, or 0.</summary>
    public int Find(long keysym, int level)
    {
        for (var keycode = FirstKeycode; keycode <= LastKeycode; keycode++)
            if (At(keycode, level) == keysym) return keycode;
        return 0;
    }

    /// <summary>A keycode with no keysym on any level, to borrow for a character with no key; the last keycode when every one is used; 0 for an empty map.</summary>
    public int Spare()
    {
        for (var keycode = LastKeycode; keycode >= FirstKeycode; keycode--)
        {
            var free = true;
            for (var level = 0; level < PerKeycode && free; level++) free = At(keycode, level) == 0;
            if (free) return keycode;
        }
        return LastKeycode >= FirstKeycode ? LastKeycode : 0;
    }
}

/// <summary>
/// The X display through libxcb (<c>libxcb.so.1</c>, which Chrome needs anyway, and the XTEST binding <c>libxcb-xtest.so.0</c>). Not Xlib: Xlib ends the whole process when the display goes away
/// (its I/O error handler may not return), and Chrome's display does go away whenever Chrome is restarted; xcb reports it as an error and nothing else. Authenticates with the cookie in the
/// display's own key file, read here and handed to the connection (<c>xcb_connect_to_display_with_auth_info</c>), so the process environment is never touched. One instance is used by one
/// thread at a time (its callers hold a lock).
/// </summary>
internal sealed class XcbDisplay : IXDisplay
{
    private const string Xcb = "libxcb.so.1", XcbTest = "libxcb-xtest.so.0";

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthInfo { public int NameLength; public IntPtr Name; public int DataLength; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator { public IntPtr Data; public int Rem; public int Index; }

    [DllImport(Xcb)] private static extern IntPtr xcb_connect_to_display_with_auth_info([MarshalAs(UnmanagedType.LPStr)] string display, ref AuthInfo auth, out int screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(IntPtr connection);
    [DllImport(Xcb)] private static extern void xcb_disconnect(IntPtr connection);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_setup(IntPtr connection);
    [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
    [DllImport(Xcb)] private static extern uint xcb_get_image(IntPtr connection, byte format, uint drawable, short x, short y, ushort width, ushort height, uint planeMask);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_image_reply(IntPtr connection, uint cookie, out IntPtr error);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_image_data(IntPtr reply);
    [DllImport(Xcb)] private static extern int xcb_get_image_data_length(IntPtr reply);
    [DllImport(Xcb)] private static extern uint xcb_get_keyboard_mapping(IntPtr connection, byte first, byte count);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_keyboard_mapping_reply(IntPtr connection, uint cookie, out IntPtr error);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_keyboard_mapping_keysyms(IntPtr reply);
    [DllImport(Xcb)] private static extern int xcb_get_keyboard_mapping_keysyms_length(IntPtr reply);
    [DllImport(Xcb)] private static extern uint xcb_change_keyboard_mapping_checked(IntPtr connection, byte keycodeCount, byte firstKeycode, byte perKeycode, int[] keysyms);
    [DllImport(Xcb)] private static extern IntPtr xcb_request_check(IntPtr connection, uint cookie);
    [DllImport(Xcb)] private static extern uint xcb_get_input_focus(IntPtr connection);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_input_focus_reply(IntPtr connection, uint cookie, out IntPtr error);
    [DllImport(Xcb)] private static extern int xcb_flush(IntPtr connection);
    [DllImport(Xcb)] private static extern uint xcb_query_tree(IntPtr connection, uint window);
    [DllImport(Xcb)] private static extern IntPtr xcb_query_tree_reply(IntPtr connection, uint cookie, out IntPtr error);
    [DllImport(Xcb)] private static extern IntPtr xcb_query_tree_children(IntPtr reply);
    [DllImport(Xcb)] private static extern int xcb_query_tree_children_length(IntPtr reply);
    [DllImport(Xcb)] private static extern uint xcb_get_window_attributes(IntPtr connection, uint window);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_window_attributes_reply(IntPtr connection, uint cookie, out IntPtr error);
    [DllImport(Xcb)] private static extern uint xcb_set_input_focus(IntPtr connection, byte revertTo, uint window, uint time);
    [DllImport(XcbTest)] private static extern uint xcb_test_fake_input(IntPtr connection, byte type, byte detail, uint time, uint root, short rootX, short rootY, byte deviceId);
    [DllImport("libc", EntryPoint = "free")] private static extern void Free(IntPtr pointer);

    // XTEST event types (the core protocol's event codes).
    private const byte KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;

    private IntPtr connection;
    private readonly uint root;
    private KeyboardMap keyboard;

    public int Width { get; }
    public int Height { get; }
    public KeyboardMap Keyboard => keyboard;

    private XcbDisplay(IntPtr connection, uint root, int width, int height, KeyboardMap keyboard)
    {
        this.connection = connection; this.root = root; Width = width; Height = height; this.keyboard = keyboard;
    }

    /// <summary>Connects to display <paramref name="number"/> with the cookie in <paramref name="authorityFile"/>.</summary>
    public static XcbDisplay Open(int number, string authorityFile)
    {
        var entry = XAuthority.ReadEntry(authorityFile) ?? throw new IOException("x_unavailable");
        var name = Marshal.AllocHGlobal(Math.Max(1, entry.Method.Length));
        var data = Marshal.AllocHGlobal(Math.Max(1, entry.Cookie.Length));
        IntPtr handle = IntPtr.Zero;
        try
        {
            Marshal.Copy(entry.Method, 0, name, entry.Method.Length);
            Marshal.Copy(entry.Cookie, 0, data, entry.Cookie.Length);
            var auth = new AuthInfo { NameLength = entry.Method.Length, Name = name, DataLength = entry.Cookie.Length, Data = data };
            handle = xcb_connect_to_display_with_auth_info(":" + number.ToString(System.Globalization.CultureInfo.InvariantCulture), ref auth, out _);
            if (handle == IntPtr.Zero || xcb_connection_has_error(handle) != 0) throw new IOException("x_unavailable");
            var setup = xcb_get_setup(handle);
            var screens = xcb_setup_roots_iterator(setup);
            if (screens.Data == IntPtr.Zero || screens.Rem <= 0) throw new IOException("x_unavailable");
            // xcb_screen_t: root window at 0, width_in_pixels at 20, height_in_pixels at 22. xcb_setup_t: min_keycode at 34, max_keycode at 35.
            var rootWindow = (uint)Marshal.ReadInt32(screens.Data, 0);
            var width = (ushort)Marshal.ReadInt16(screens.Data, 20);
            var height = (ushort)Marshal.ReadInt16(screens.Data, 22);
            int first = Marshal.ReadByte(setup, 34), last = Marshal.ReadByte(setup, 35);
            var map = ReadKeyboard(handle, first, last);
            var display = new XcbDisplay(handle, rootWindow, width, height, map);
            handle = IntPtr.Zero;
            return display;
        }
        finally
        {
            if (handle != IntPtr.Zero) xcb_disconnect(handle);
            // The cookie is not kept anywhere in this process once the connection has made its handshake.
            for (var i = 0; i < entry.Cookie.Length; i++) Marshal.WriteByte(data, i, 0);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(data);
            Array.Clear(entry.Cookie);
        }
    }

    private static KeyboardMap ReadKeyboard(IntPtr handle, int first, int last)
    {
        var count = last - first + 1;
        if (count <= 0) throw new IOException("x_unavailable");
        var reply = xcb_get_keyboard_mapping_reply(handle, xcb_get_keyboard_mapping(handle, (byte)first, (byte)count), out var error);
        if (reply == IntPtr.Zero) { if (error != IntPtr.Zero) Free(error); throw new IOException("x_unavailable"); }
        try
        {
            // xcb_get_keyboard_mapping_reply_t: keysyms_per_keycode at offset 1.
            var per = Marshal.ReadByte(reply, 1);
            var length = xcb_get_keyboard_mapping_keysyms_length(reply);
            var raw = xcb_get_keyboard_mapping_keysyms(reply);
            var symbols = new long[length];
            for (var i = 0; i < length; i++) symbols[i] = (uint)Marshal.ReadInt32(raw, i * 4);
            return new KeyboardMap(first, per, symbols);
        }
        finally { Free(reply); }
    }

    // A connection that was closed, or that xcb has marked broken (the X server went away: Chrome was restarted, or Xvfb died), is an IOException for the caller to end on; xcb itself never
    // ends the process, but without this a dead display would answer null for ever and a window would sit frozen.
    private void ThrowIfDead()
    {
        if (connection == IntPtr.Zero || xcb_connection_has_error(connection) != 0) throw new IOException("x_unavailable");
    }

    private byte[]? frame;

    public byte[]? Capture()
    {
        ThrowIfDead();
        var cookie = xcb_get_image(connection, 2 /* ZPixmap */, root, 0, 0, (ushort)Width, (ushort)Height, uint.MaxValue);
        var reply = xcb_get_image_reply(connection, cookie, out var error);
        if (reply == IntPtr.Zero) { if (error != IntPtr.Zero) Free(error); ThrowIfDead(); return null; }
        try
        {
            var length = xcb_get_image_data_length(reply);
            if (length < Width * Height * 4) return null;
            // One array for every look: a new 4 MB one every 100 ms of a sign-in was garbage on the large object heap.
            var pixels = frame is { } kept && kept.Length == Width * Height * 4 ? kept : frame = new byte[Width * Height * 4];
            Marshal.Copy(xcb_get_image_data(reply), pixels, 0, pixels.Length);
            return pixels;
        }
        finally { Free(reply); }
    }

    public void MotionTo(int x, int y) => Fake(MotionNotify, 0, (short)Math.Clamp(x, 0, Width - 1), (short)Math.Clamp(y, 0, Height - 1));
    public void Button(int button, bool press) => Fake(press ? ButtonPress : ButtonRelease, (byte)button, 0, 0);
    public void Key(int keycode, bool press) => Fake(press ? KeyPress : KeyRelease, (byte)keycode, 0, 0);

    // A key or button names no position; the pointer is wherever MotionTo last put it. Root 0 means the window the pointer is in.
    private void Fake(byte type, byte detail, short x, short y)
    {
        ThrowIfDead();
        xcb_test_fake_input(connection, type, detail, 0, type == MotionNotify ? root : 0, x, y, 0);
    }

    public void SetKeysym(int keycode, long keysym)
    {
        ThrowIfDead();
        var per = keyboard.PerKeycode;
        var symbols = new int[per];
        for (var i = 0; i < per; i++) symbols[i] = unchecked((int)keysym);
        var check = xcb_request_check(connection, xcb_change_keyboard_mapping_checked(connection, 1, (byte)keycode, (byte)per, symbols));
        if (check != IntPtr.Zero) { Free(check); throw new IOException("x_unavailable"); }
        keyboard.Keysyms.AsSpan((keycode - keyboard.FirstKeycode) * per, per).Fill(keysym);
    }

    public bool FocusBrowser()
    {
        ThrowIfDead();
        var tree = xcb_query_tree_reply(connection, xcb_query_tree(connection, root), out var error);
        if (tree == IntPtr.Zero) { if (error != IntPtr.Zero) Free(error); return false; }
        uint chosen = 0;
        try
        {
            var children = xcb_query_tree_children(tree);
            var count = xcb_query_tree_children_length(tree);
            // The topmost window that is on screen (map_state, offset 26 of the attributes' reply, is 2 for viewable): with no window manager there is nothing else on the display but the browser's.
            for (var i = count - 1; i >= 0 && chosen == 0; i--)
            {
                var window = (uint)Marshal.ReadInt32(children, i * 4);
                var attributes = xcb_get_window_attributes_reply(connection, xcb_get_window_attributes(connection, window), out var problem);
                if (attributes == IntPtr.Zero) { if (problem != IntPtr.Zero) Free(problem); continue; }
                try { if (Marshal.ReadByte(attributes, 26) == 2) chosen = window; }
                finally { Free(attributes); }
            }
        }
        finally { Free(tree); }
        if (chosen == 0) return false;
        xcb_set_input_focus(connection, 1 /* PointerRoot */, chosen, 0);
        xcb_flush(connection);
        return true;
    }

    public void Sync()
    {
        ThrowIfDead();
        xcb_flush(connection);
        var reply = xcb_get_input_focus_reply(connection, xcb_get_input_focus(connection), out var error);
        if (reply != IntPtr.Zero) Free(reply);
        else { if (error != IntPtr.Zero) Free(error); throw new IOException("x_unavailable"); }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref connection, IntPtr.Zero);
        if (handle != IntPtr.Zero) xcb_disconnect(handle);
    }
}

/// <summary>The real <see cref="IXConnector"/>.</summary>
internal sealed class XcbConnector : IXConnector
{
    public IXDisplay Open(int displayNumber, string authorityFile)
    {
        try { return XcbDisplay.Open(displayNumber, authorityFile); }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { throw new IOException("x_unavailable"); }
    }
}
