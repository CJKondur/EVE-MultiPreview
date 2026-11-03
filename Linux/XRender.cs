using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Linux;

/// <summary>Shrinks a client's picture on the X server (XRender, bilinear) so only the thumbnail-sized picture
/// crosses the connection. A full 1080p client is 8 MB per frame without it.</summary>
internal static unsafe class XRender
{
    private const string Lib = "libxcb-render.so.0";
    private const byte OpSrc = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Transform
    {
        public int M11, M12, M13, M21, M22, M23, M31, M32, M33;
    }

    [DllImport(Lib)] private static extern uint xcb_render_query_version(IntPtr c, uint major, uint minor);
    [DllImport(Lib)] private static extern IntPtr xcb_render_query_version_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(Lib)] private static extern uint xcb_render_query_pict_formats(IntPtr c);
    [DllImport(Lib)] private static extern IntPtr xcb_render_query_pict_formats_reply(IntPtr c, uint cookie, IntPtr e);
    [DllImport(Lib)] private static extern uint xcb_render_create_picture(IntPtr c, uint pid, uint drawable, uint format, uint valueMask, uint* values);
    [DllImport(Lib)] private static extern uint xcb_render_free_picture(IntPtr c, uint picture);
    [DllImport(Lib)] private static extern uint xcb_render_set_picture_transform(IntPtr c, uint picture, Transform transform);
    [DllImport(Lib)] private static extern uint xcb_render_set_picture_filter(IntPtr c, uint picture, ushort filterLen, byte* filter, uint valuesLen, int* values);
    [DllImport(Lib)] private static extern uint xcb_render_composite(IntPtr c, byte op, uint src, uint mask, uint dst,
        short srcX, short srcY, short maskX, short maskY, short dstX, short dstY, ushort width, ushort height);

    /// <summary>Visual → picture format, from the server's format list. Null when the server has no Render.</summary>
    public static Dictionary<uint, uint>? Formats(IntPtr c)
    {
        try
        {
            var v = xcb_render_query_version_reply(c, xcb_render_query_version(c, 0, 11), IntPtr.Zero);
            if (v == IntPtr.Zero) return null;
            NativeMemory.Free((void*)v);
        }
        catch (DllNotFoundException) { return null; }

        var reply = xcb_render_query_pict_formats_reply(c, xcb_render_query_pict_formats(c), IntPtr.Zero);
        if (reply == IntPtr.Zero) return null;
        try
        {
            byte* p = (byte*)reply;
            uint numFormats = *(uint*)(p + 8), numScreens = *(uint*)(p + 12);
            byte* at = p + 32 + numFormats * 28;   // the screens follow the 28-byte format records
            var map = new Dictionary<uint, uint>();
            for (uint s = 0; s < numScreens; s++)
            {
                uint numDepths = *(uint*)at;
                at += 8;
                for (uint d = 0; d < numDepths; d++)
                {
                    ushort numVisuals = *(ushort*)(at + 2);
                    at += 8;
                    for (int i = 0; i < numVisuals; i++, at += 8)
                        map[*(uint*)at] = *(uint*)(at + 4);
                }
            }
            return map;
        }
        finally { NativeMemory.Free((void*)reply); }
    }

    public static uint CreatePicture(IntPtr c, uint drawable, uint format)
    {
        uint pid = Xcb.xcb_generate_id(c);
        xcb_render_create_picture(c, pid, drawable, format, 0, null);
        return pid;
    }

    public static void FreePicture(IntPtr c, uint picture) => xcb_render_free_picture(c, picture);

    /// <summary>Makes the picture read <paramref name="sx"/> × <paramref name="sy"/> source pixels per
    /// destination pixel, smoothed.</summary>
    public static void Scale(IntPtr c, uint picture, double sx, double sy)
    {
        xcb_render_set_picture_transform(c, picture, new Transform
        {
            M11 = Fixed(sx), M22 = Fixed(sy), M33 = Fixed(1),
        });
        var name = "bilinear"u8;
        fixed (byte* f = name) xcb_render_set_picture_filter(c, picture, (ushort)name.Length, f, 0, null);
    }

    public static void Copy(IntPtr c, uint src, uint dst, int width, int height) =>
        xcb_render_composite(c, OpSrc, src, 0, dst, 0, 0, 0, 0, 0, 0, (ushort)width, (ushort)height);

    private static int Fixed(double v) => (int)Math.Round(v * 65536);
}
