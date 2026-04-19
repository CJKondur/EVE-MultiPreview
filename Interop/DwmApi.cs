using System;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Interop;

/// <summary>
/// P/Invoke wrappers for the Desktop Window Manager (DWM) thumbnail API.
/// These are the core Win32 calls that make live window previews possible.
/// </summary>
public static class DwmApi
{
    // ── DWM Thumbnail Lifecycle ──────────────────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmRegisterThumbnail(IntPtr destHwnd, IntPtr srcHwnd, out IntPtr thumbId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUnregisterThumbnail(IntPtr thumbId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumbId, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbId, out SIZE size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // DWM window attributes
    public const uint DWMWA_CLOAKED = 14;
    public const uint DWMWA_NCRENDERING_ENABLED = 1;

    // ── Structures ───────────────────────────────────────────────────

    [Flags]
    public enum DWM_TNP : uint
    {
        RECTDESTINATION = 0x00000001,
        RECTSOURCE      = 0x00000002,
        OPACITY         = 0x00000004,
        VISIBLE         = 0x00000008,
        SOURCECLIENTAREAONLY = 0x00000010,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public DWM_TNP dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    // ── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Register a DWM thumbnail relationship between two windows.
    /// Returns the thumbnail handle, or IntPtr.Zero on failure.
    /// </summary>
    public static IntPtr RegisterThumbnail(IntPtr destHwnd, IntPtr srcHwnd)
    {
        int hr = DwmRegisterThumbnail(destHwnd, srcHwnd, out IntPtr thumbId);
        EveMultiPreview.Services.DiagnosticsService.LogDwm($"[RegisterThumbnail] Dest: {destHwnd}, Src: {srcHwnd} -> HResult: 0x{hr:X8}, ThumbID: {thumbId}");
        return hr == 0 ? thumbId : IntPtr.Zero;
    }

    /// <summary>
    /// Unregister a previously registered thumbnail. Safe to call with IntPtr.Zero.
    /// </summary>
    public static void UnregisterThumbnail(IntPtr thumbId)
    {
        if (thumbId != IntPtr.Zero)
        {
            int hr = DwmUnregisterThumbnail(thumbId);
            EveMultiPreview.Services.DiagnosticsService.LogDwm($"[UnregisterThumbnail] ThumbID: {thumbId} -> HResult: 0x{hr:X8}");
        }
    }

    /// <summary>
    /// Update the thumbnail to fill the destination window, with optional opacity.
    /// </summary>
    public static bool UpdateThumbnail(IntPtr thumbId, int destWidth, int destHeight, byte opacity = 255, bool clientAreaOnly = true)
    {
        if (thumbId == IntPtr.Zero) return false;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP.RECTDESTINATION | DWM_TNP.VISIBLE | DWM_TNP.OPACITY | DWM_TNP.SOURCECLIENTAREAONLY,
            rcDestination = new RECT(0, 0, destWidth, destHeight),
            opacity = opacity,
            fVisible = true,
            fSourceClientAreaOnly = clientAreaOnly
        };

        int hr = DwmUpdateThumbnailProperties(thumbId, ref props);
        EveMultiPreview.Services.DiagnosticsService.LogDwm($"[UpdateThumbnail] ThumbID: {thumbId}, W:{destWidth} H:{destHeight}, Opacity:{opacity} -> HResult: 0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>
    /// Update the thumbnail with an inset border. The DWM thumbnail is shrunk by
    /// the border amount on all sides, revealing the window background as a visible frame.
    /// </summary>
    public static bool UpdateThumbnailInset(IntPtr thumbId, int destWidth, int destHeight, int border = 0, byte opacity = 255, bool clientAreaOnly = true)
    {
        if (thumbId == IntPtr.Zero) return false;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP.RECTDESTINATION | DWM_TNP.VISIBLE | DWM_TNP.OPACITY | DWM_TNP.SOURCECLIENTAREAONLY,
            rcDestination = new RECT(border, border, destWidth - border, destHeight - border),
            opacity = opacity,
            fVisible = true,
            fSourceClientAreaOnly = clientAreaOnly
        };

        int hr = DwmUpdateThumbnailProperties(thumbId, ref props);
        EveMultiPreview.Services.DiagnosticsService.LogDwm($"[UpdateThumbnailInset] ThumbID: {thumbId}, W:{destWidth} H:{destHeight}, Inset:{border}, Opacity:{opacity} -> HResult: 0x{hr:X8}");
        return hr == 0;
    }

    /// <summary>
    /// Query the source window's size for the registered thumbnail.
    /// </summary>
    public static (int Width, int Height) QuerySourceSize(IntPtr thumbId)
    {
        if (thumbId == IntPtr.Zero) return (0, 0);
        int hr = DwmQueryThumbnailSourceSize(thumbId, out SIZE size);
        return hr == 0 ? (size.cx, size.cy) : (0, 0);
    }
}
