using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace EveMultiPreview.Interop;

/// <summary>
/// Converts between WPF DIPs (Device Independent Pixels) and Win32 physical pixels.
/// WPF properties (Left, Top, Width, Height, ActualWidth) are in DIPs.
/// Win32 APIs (SetWindowPos, GetWindowRect, DWM thumbnail, Cursor.Position) use physical pixels.
/// At 150% scaling: 100 DIPs = 150 physical pixels.
/// </summary>
public static class DpiHelper
{
    /// <summary>
    /// Get the DPI scale factor for a specific WPF visual.
    /// Returns 1.0 at 100%, 1.25 at 125%, 1.5 at 150%, 2.0 at 200%.
    /// </summary>
    public static double GetScaleFactor(Visual visual)
    {
        try
        {
            return VisualTreeHelper.GetDpi(visual).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            return GetSystemScaleFactor();
        }
    }

    /// <summary>
    /// Get the system-wide DPI scale factor (fallback when no visual is available).
    /// </summary>
    public static double GetSystemScaleFactor()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            try
            {
                int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                return dpiX / 96.0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        return 1.0;
    }

    /// <summary>
    /// Get the DPI scale factor for a specific monitor, identified by a point on it.
    /// Uses GetDpiForMonitor (Win 8.1+) for per-monitor accuracy in mixed-DPI setups.
    /// </summary>
    public static double GetScaleFactorForPoint(int x, int y)
    {
        IntPtr hMonitor = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero &&
            GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            return dpiX / 96.0;
        return GetSystemScaleFactor();
    }

    /// <summary>Convert WPF DIPs to physical pixels.</summary>
    public static int DipToPhysical(double dip, double scale) =>
        (int)Math.Round(dip * scale);

    /// <summary>Convert physical pixels to WPF DIPs.</summary>
    public static double PhysicalToDip(double pixels, double scale) =>
        scale > 0 ? pixels / scale : pixels;

    private const int LOGPIXELSX = 88;
    private const uint MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, uint dpiType, out uint dpiX, out uint dpiY);
}
