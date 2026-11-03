using System;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Linux;

/// <summary>The few libxcb calls the app needs. Every reply xcb hands back is malloc'd and is freed here.</summary>
internal static unsafe class Xcb
{
    private const string Lib = "libxcb.so.1";
    private const string CompositeLib = "libxcb-composite.so.0";

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

    [DllImport(Lib)] private static extern uint xcb_send_event(IntPtr c, byte propagate, uint destination, uint eventMask, byte* evt);

    [DllImport(CompositeLib)] private static extern uint xcb_composite_query_version(IntPtr c, uint major, uint minor);
    [DllImport(CompositeLib)] private static extern IntPtr xcb_composite_query_version_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(CompositeLib)] private static extern uint xcb_composite_redirect_window(IntPtr c, uint window, byte update);

    public static uint Root(IntPtr c, int screen)
    {
        var it = xcb_setup_roots_iterator(xcb_get_setup(c));
        for (int i = 0; i < screen && it.Rem > 0; i++) xcb_screen_next(ref it);
        return *(uint*)it.Data;   // xcb_screen_t starts with its root window
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

    public static (int Width, int Height)? Size(IntPtr c, uint drawable)
    {
        var reply = xcb_get_geometry_reply(c, xcb_get_geometry(c, drawable), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        int w = *(ushort*)(reply + 16), h = *(ushort*)(reply + 18);
        NativeMemory.Free((void*)reply);
        return (w, h);
    }

    /// <summary>A ZPixmap image of the whole drawable; the caller frees it with <see cref="Free"/>.</summary>
    public static IntPtr Image(IntPtr c, uint drawable, int width, int height, out byte* data, out int length)
    {
        var reply = xcb_get_image_reply(c, xcb_get_image(c, ImageFormatZPixmap, drawable, 0, 0, (ushort)width, (ushort)height, uint.MaxValue), IntPtr.Zero);
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
