using System;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Services;

/// <summary>Monitor facts for the fixed-slot editors of both settings windows (moved unchanged
/// from SettingsWindow.Slots.cs).
/// EVE is system-DPI-aware (spike, 2026-09-24): on a monitor whose Windows scaling differs from
/// the primary's, Windows rescales its image, and EVE believes its window is larger than it is.
/// This app is system-aware too, so per-monitor DPI queries all return the primary's DPI. The
/// reliable signal is the monitor's real display-mode resolution (EnumDisplaySettings is never
/// virtualized) versus the bounds this process sees: they differ exactly when the scaling differs.</summary>
public static class MonitorInfo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>The monitor's real resolution; falls back to the bounds this process sees.</summary>
    public static (int W, int H) PhysicalResolution(WinForms.Screen sc)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(sc.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            return (dm.dmPelsWidth, dm.dmPelsHeight);
        return (sc.Bounds.Width, sc.Bounds.Height);
    }

    /// <summary>The work area (screen minus taskbar), in this process's pixels, of the monitor the window is mostly on.</summary>
    public static System.Drawing.Rectangle WorkAreaOf(IntPtr hwnd) => WinForms.Screen.FromHandle(hwnd).WorkingArea;

    /// <summary>True when this monitor's Windows scaling differs from the primary's.</summary>
    public static bool ScalingDiffersFromPrimary(WinForms.Screen sc)
    {
        if (sc.Primary) return false;
        var (pw, _) = PhysicalResolution(sc);
        if (pw != sc.Bounds.Width) return true;   // bounds virtualized: system-aware process
        // Per-monitor-aware process: bounds are physical, so compare effective DPIs.
        var prim = WinForms.Screen.PrimaryScreen;
        if (prim == null) return false;
        double a = Interop.DpiHelper.GetScaleFactorForPoint(sc.Bounds.Left + sc.Bounds.Width / 2, sc.Bounds.Top + sc.Bounds.Height / 2);
        double b = Interop.DpiHelper.GetScaleFactorForPoint(prim.Bounds.Left + prim.Bounds.Width / 2, prim.Bounds.Top + prim.Bounds.Height / 2);
        return Math.Abs(a - b) > 0.01;
    }
}
