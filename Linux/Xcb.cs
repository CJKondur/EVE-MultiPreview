using System;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Linux;

/// <summary>The few libxcb calls the app needs. Every reply xcb hands back is malloc'd and is freed here.</summary>
internal static unsafe class Xcb
{
    private const string Lib = "libxcb.so.1";
    private const string CompositeLib = "libxcb-composite.so.0";
    private const string ShapeLib = "libxcb-shape.so.0";

    public const uint AnyPropertyType = 0;
    public const uint AtomWmName = 39;
    public const byte ImageFormatZPixmap = 2;
    public const byte ClientMessage = 33;
    public const uint SubstructureNotify = 1u << 19;
    public const uint SubstructureRedirect = 1u << 20;
    public const byte CompositeRedirectAutomatic = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenIterator { public IntPtr Data; public int Rem; public int Index; }

    [DllImport(Lib)] public static extern IntPtr xcb_connect(string? displayName, out int screen);
    [DllImport(Lib)] public static extern int xcb_connection_has_error(IntPtr c);
    [DllImport(Lib)] public static extern void xcb_disconnect(IntPtr c);
    [DllImport(Lib)] public static extern int xcb_flush(IntPtr c);
    [DllImport(Lib)] private static extern IntPtr xcb_get_setup(IntPtr c);
    [DllImport(Lib)] private static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
    [DllImport(Lib)] private static extern void xcb_screen_next(ref ScreenIterator i);

    [DllImport(Lib)] private static extern uint xcb_intern_atom(IntPtr c, byte onlyIfExists, ushort nameLen, byte[] name);
    [DllImport(Lib)] private static extern IntPtr xcb_intern_atom_reply(IntPtr c, uint cookie, IntPtr e);

    [DllImport(Lib)] private static extern uint xcb_get_property(IntPtr c, byte delete, uint window, uint property, uint type, uint longOffset, uint longLength);
    [DllImport(Lib)] private static extern IntPtr xcb_get_property_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(Lib)] private static extern IntPtr xcb_get_property_value(IntPtr reply);
    [DllImport(Lib)] private static extern int xcb_get_property_value_length(IntPtr reply);

    [DllImport(Lib)] private static extern uint xcb_get_geometry(IntPtr c, uint drawable);
    [DllImport(Lib)] private static extern IntPtr xcb_get_geometry_reply(IntPtr c, uint cookie, IntPtr e);

    [DllImport(Lib)] private static extern uint xcb_get_image(IntPtr c, byte format, uint drawable, short x, short y, ushort width, ushort height, uint planeMask);
    [DllImport(Lib)] private static extern IntPtr xcb_get_image_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(Lib)] private static extern IntPtr xcb_get_image_data(IntPtr reply);
    [DllImport(Lib)] private static extern int xcb_get_image_data_length(IntPtr reply);

    [DllImport(Lib)] public static extern uint xcb_generate_id(IntPtr c);
    [DllImport(Lib)] private static extern uint xcb_create_pixmap(IntPtr c, byte depth, uint pid, uint drawable, ushort width, ushort height);
    [DllImport(Lib)] public static extern uint xcb_free_pixmap(IntPtr c, uint pixmap);
    [DllImport(Lib)] private static extern uint xcb_get_window_attributes(IntPtr c, uint window);
    [DllImport(Lib)] private static extern IntPtr xcb_get_window_attributes_reply(IntPtr c, uint cookie, IntPtr e);

    [DllImport(Lib)] private static extern uint xcb_query_pointer(IntPtr c, uint window);
    [DllImport(Lib)] private static extern IntPtr xcb_query_pointer_reply(IntPtr c, uint cookie, IntPtr e);

    [DllImport(Lib)] private static extern uint xcb_send_event(IntPtr c, byte propagate, uint destination, uint eventMask, byte* evt);

    [DllImport(Lib)] private static extern uint xcb_configure_window(IntPtr c, uint window, ushort valueMask, uint* values);

    [DllImport(Lib)] private static extern uint xcb_change_property(IntPtr c, byte mode, uint window, uint property, uint type, byte format, uint dataLen, void* data);
    [DllImport(Lib)] private static extern uint xcb_translate_coordinates(IntPtr c, uint srcWindow, uint dstWindow, short srcX, short srcY);
    [DllImport(Lib)] private static extern IntPtr xcb_translate_coordinates_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(ShapeLib)] private static extern uint xcb_shape_rectangles(IntPtr c, byte operation, byte destinationKind, byte ordering, uint destinationWindow, short xOffset, short yOffset, uint rectanglesLen, void* rectangles);
    [DllImport(ShapeLib)] private static extern uint xcb_shape_mask(IntPtr c, byte operation, byte destinationKind, uint destinationWindow, short xOffset, short yOffset, uint sourceBitmap);

    [DllImport(CompositeLib)] private static extern uint xcb_composite_query_version(IntPtr c, uint major, uint minor);
    [DllImport(CompositeLib)] private static extern IntPtr xcb_composite_query_version_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(CompositeLib)] private static extern uint xcb_composite_redirect_window(IntPtr c, uint window, byte update);

    public static uint Root(IntPtr c, int screen)
    {
        var it = xcb_setup_roots_iterator(xcb_get_setup(c));
        for (int i = 0; i < screen && it.Rem > 0; i++) xcb_screen_next(ref it);
        return *(uint*)it.Data;   // xcb_screen_t starts with its root window
    }

    /// <summary>The pointer on the screen and the buttons and modifiers that are down (the X server's mask: Button1 0x100,
    /// Button3 0x400, Control 0x04), or null when the server does not answer.</summary>
    public static (int X, int Y, ushort Mask)? Pointer(IntPtr c, uint root)
    {
        var reply = xcb_query_pointer_reply(c, xcb_query_pointer(c, root), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        var result = (X: (int)*(short*)(reply + 16), Y: (int)*(short*)(reply + 18), Mask: *(ushort*)(reply + 24));
        NativeMemory.Free((void*)reply);
        return result;
    }

    public static uint Atom(IntPtr c, string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        var reply = xcb_intern_atom_reply(c, xcb_intern_atom(c, 0, (ushort)bytes.Length, bytes), IntPtr.Zero);
        if (reply == IntPtr.Zero) return 0;
        uint atom = *(uint*)(reply + 8);
        NativeMemory.Free((void*)reply);
        return atom;
    }

    /// <summary>The property's raw bytes, or null when the window or property is gone.</summary>
    public static byte[]? Property(IntPtr c, uint window, uint property, uint type = AnyPropertyType)
    {
        var reply = xcb_get_property_reply(c, xcb_get_property(c, 0, window, property, type, 0, 1 << 16), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        try
        {
            int len = xcb_get_property_value_length(reply);
            var bytes = new byte[len];
            Marshal.Copy(xcb_get_property_value(reply), bytes, 0, len);
            return bytes;
        }
        finally { NativeMemory.Free((void*)reply); }
    }

    public static (int Width, int Height, byte Depth)? Size(IntPtr c, uint drawable)
    {
        var reply = xcb_get_geometry_reply(c, xcb_get_geometry(c, drawable), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        int w = *(ushort*)(reply + 16), h = *(ushort*)(reply + 18);
        byte depth = *(byte*)(reply + 1);
        NativeMemory.Free((void*)reply);
        return (w, h, depth);
    }

    /// <summary>The window's visual, or 0 when the window is gone.</summary>
    public static uint Visual(IntPtr c, uint window)
    {
        var reply = xcb_get_window_attributes_reply(c, xcb_get_window_attributes(c, window), IntPtr.Zero);
        if (reply == IntPtr.Zero) return 0;
        uint visual = *(uint*)(reply + 8);
        NativeMemory.Free((void*)reply);
        return visual;
    }

    public static uint CreatePixmap(IntPtr c, byte depth, uint drawable, int width, int height)
    {
        uint pid = xcb_generate_id(c);
        xcb_create_pixmap(c, depth, pid, drawable, (ushort)width, (ushort)height);
        return pid;
    }

    /// <summary>A ZPixmap image of the drawable's <paramref name="width"/> × <paramref name="height"/> pixels from
    /// (<paramref name="x"/>, <paramref name="y"/>); the caller frees it with <see cref="Free"/>.</summary>
    public static IntPtr Image(IntPtr c, uint drawable, int width, int height, out byte* data, out int length, int x = 0, int y = 0)
    {
        var reply = xcb_get_image_reply(c, xcb_get_image(c, ImageFormatZPixmap, drawable, (short)x, (short)y, (ushort)width, (ushort)height, uint.MaxValue), IntPtr.Zero);
        data = null; length = 0;
        if (reply == IntPtr.Zero) return IntPtr.Zero;
        data = (byte*)xcb_get_image_data(reply);
        length = xcb_get_image_data_length(reply);
        return reply;
    }

    public static void Free(IntPtr reply) => NativeMemory.Free((void*)reply);

    /// <summary>Asks the window manager to activate <paramref name="window"/>, as a pager would (EWMH source 2).</summary>
    public static void RequestActivate(IntPtr c, uint root, uint window, uint netActiveWindow)
    {
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = netActiveWindow;
        *(uint*)(evt + 12) = 2;   // source indication: pager
        xcb_send_event(c, 0, root, SubstructureRedirect | SubstructureNotify, evt);
        xcb_flush(c);
    }

    /// <summary>Asks the window manager to close the window (_NET_CLOSE_WINDOW, EWMH): it sends the client its
    /// WM_DELETE_WINDOW, as the title bar's close button does, so EVE closes as it would by its own close button.</summary>
    public static void RequestClose(IntPtr c, uint root, uint window, uint netCloseWindow)
    {
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = netCloseWindow;
        *(uint*)(evt + 12) = 0;   // timestamp: CurrentTime
        *(uint*)(evt + 16) = 2;   // source indication: pager
        xcb_send_event(c, 0, root, SubstructureRedirect | SubstructureNotify, evt);
        xcb_flush(c);
    }

    /// <summary>Asks the window manager to minimize the window (ICCCM WM_CHANGE_STATE to IconicState), as its minimize button does.</summary>
    public static void RequestIconify(IntPtr c, uint root, uint window, uint wmChangeState)
    {
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = wmChangeState;
        *(uint*)(evt + 12) = 3;   // IconicState
        xcb_send_event(c, 0, root, SubstructureRedirect | SubstructureNotify, evt);
        xcb_flush(c);
    }

    /// <summary>Asks the window manager to move (and, with a width and height, size) a client window, as a pager would
    /// (_NET_MOVERESIZE_WINDOW, EWMH). StaticGravity: x and y are where the client's own area starts on the screen, the
    /// point Rectangle reads back, so a saved place comes back where it was whatever frame the window manager draws.</summary>
    public static void RequestMoveResize(IntPtr c, uint root, uint window, uint netMoveResize, int x, int y, int width = 0, int height = 0)
    {
        const uint staticGravity = 10, hasX = 1u << 8, hasY = 1u << 9, hasW = 1u << 10, hasH = 1u << 11, pager = 2u << 12;
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = netMoveResize;
        *(uint*)(evt + 12) = staticGravity | hasX | hasY | (width > 0 ? hasW : 0) | (height > 0 ? hasH : 0) | pager;
        *(int*)(evt + 16) = x;
        *(int*)(evt + 20) = y;
        *(int*)(evt + 24) = Math.Max(0, width);
        *(int*)(evt + 28) = Math.Max(0, height);
        xcb_send_event(c, 0, root, SubstructureRedirect | SubstructureNotify, evt);
        xcb_flush(c);
    }

    /// <summary>Asks the window manager to add (or remove) two states of a client window at once (_NET_WM_STATE, EWMH), as a
    /// pager would: the maximized pair.</summary>
    public static void RequestState(IntPtr c, uint root, uint window, uint netWmState, bool add, uint first, uint second)
    {
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = netWmState;
        *(uint*)(evt + 12) = add ? 1u : 0u;
        *(uint*)(evt + 16) = first;
        *(uint*)(evt + 20) = second;
        *(uint*)(evt + 24) = 2;   // source indication: pager
        xcb_send_event(c, 0, root, SubstructureRedirect | SubstructureNotify, evt);
        xcb_flush(c);
    }

    /// <summary>Writes the states a window asks for (_NET_WM_STATE, a list of atoms) on a window that is not mapped: the window
    /// manager reads them when it maps the window (EWMH). A round trip after it, so the server has the list before another
    /// connection (Avalonia's) maps the window.</summary>
    public static void SetStatesBeforeMap(IntPtr c, uint window, uint netWmState, ReadOnlySpan<uint> states)
    {
        const uint atomType = 4;
        const byte replace = 0;
        fixed (uint* data = states) xcb_change_property(c, replace, window, netWmState, atomType, 32, (uint)states.Length, data);
        xcb_flush(c);
        _ = Property(c, window, netWmState);   // the round trip
    }

    /// <summary>Stacks the window above its siblings (ConfigureWindow, stack mode Above). The window manager decides.</summary>
    public static void Raise(IntPtr c, uint window)
    {
        uint above = 0;   // StackMode Above
        xcb_configure_window(c, window, 0x40, &above);   // 0x40: the StackMode bit
        xcb_flush(c);
    }

    /// <summary>Where the window's top left corner is on the root window (the screen), or null when the window is gone.</summary>
    public static (int X, int Y)? Origin(IntPtr c, uint window, uint root)
    {
        var reply = xcb_translate_coordinates_reply(c, xcb_translate_coordinates(c, window, root, 0, 0), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        var at = ((int)*(short*)(reply + 12), (int)*(short*)(reply + 14));
        NativeMemory.Free((void*)reply);
        return at;
    }

    /// <summary>Lets the mouse through the window to whatever is under it (an empty input shape, SHAPE extension), or
    /// gives the window its whole area back. False when the server or the system has no SHAPE.</summary>
    public static bool PassClicks(IntPtr c, uint window, bool through)
    {
        const byte setOp = 0, inputKind = 2;
        if (_noShape) return false;
        try
        {
            if (through) xcb_shape_rectangles(c, setOp, inputKind, 0, window, 0, 0, 0, null);
            else xcb_shape_mask(c, setOp, inputKind, window, 0, 0, 0);   // no mask: the window's default (all of it)
            xcb_flush(c);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _noShape = true;   // asked once: the library doesn't appear later
            Console.WriteLine("Clicks can't pass through crop windows: libxcb-shape.so.0 is missing.");
            return false;
        }
    }

    private static bool _noShape;

    /// <summary>Tells the window manager never to give the window the keyboard focus (WM_HINTS, input False: the
    /// "no input" model), as a Windows WS_EX_NOACTIVATE window: a click on it leaves the client in front.</summary>
    public static void NoFocus(IntPtr c, uint window)
    {
        const uint wmHints = 35;
        const byte replace = 0;
        uint* hints = stackalloc uint[9];
        new Span<uint>(hints, 9).Clear();
        hints[0] = 1;   // flags: InputHint
        hints[1] = 0;   // input: False
        xcb_change_property(c, replace, window, wmHints, wmHints, 32, 9, hints);
        xcb_flush(c);
    }

    /// <summary>False when the server has no Composite extension.</summary>
    public static bool InitComposite(IntPtr c)
    {
        try
        {
            var reply = xcb_composite_query_version_reply(c, xcb_composite_query_version(c, 0, 4), IntPtr.Zero);
            if (reply == IntPtr.Zero) return false;
            NativeMemory.Free((void*)reply);
            return true;
        }
        catch (DllNotFoundException) { return false; }
    }

    /// <summary>Keeps the window's contents in an off-screen buffer, so a covered client still shows.</summary>
    public static void RedirectAutomatic(IntPtr c, uint window) => xcb_composite_redirect_window(c, window, CompositeRedirectAutomatic);
}
