using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.SettingsUi;

/// <summary>The title bar of a styled window (UI styles spec §8.5, D9): Windows draws it; the look colours it and picks
/// its corners. Windows 10 knows only the dark mode and ignores the rest (the calls return an error, which is ignored).</summary>
public static class TitleBar
{
    public const int DarkMode = 20, CornerPreference = 33, BorderColour = 34, CaptionColour = 35, TextColour = 36;

    /// <summary>DWMWA_COLOR_DEFAULT: Windows draws that part itself (its own caption, with Mica, its own text and edge).</summary>
    public const int SystemColour = unchecked((int)0xFFFFFFFF);

    /// <summary>The attributes for a palette, in the order they are set: dark caption buttons for a dark caption, the
    /// corners, the edge, the caption and its text (COLORREF: 0x00BBGGRR). A title bar colour of the look whose alpha is 0
    /// means "the system's own" and is set as <see cref="SystemColour"/>; Windows 11 says so for all three, so its title
    /// bar is exactly Windows'. The corners of a round look are left to the system (DWMWCP_DEFAULT), which rounds a
    /// window like these; the menus, which the system leaves square, ask for the round corners themselves.</summary>
    public static IReadOnlyList<(int Attribute, int Value)> Attributes(UiPalette palette)
    {
        var caption = palette.Colour("Fx.CaptionColor");
        var dark = caption.A == 0 ? palette.Colour("Fx.Bg") : caption;
        return new[]
        {
            (DarkMode, Luminance(dark) < 0.5 ? 1 : 0),
            (CornerPreference, palette.Corners == WindowCorners.Round ? 0 : Views.FluentMenu.CornerPreference(palette.Corners)),
            (BorderColour, Value(palette.Colour("Fx.WindowBorder"))),
            (CaptionColour, Value(caption)),
            (TextColour, Value(palette.Colour("Fx.CaptionText"))),
        };
    }

    /// <summary>Raised for each window whose title bar was coloured (tests).</summary>
    public static event Action<Window>? Applied;

    /// <summary>Colours the window's title bar with the current look; a window without a handle yet is left alone (the
    /// applier asks again when it has one).</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        foreach (var (attribute, value) in Attributes(UiPalette.Current))
        {
            int v = value;
            DwmSetWindowAttribute(hwnd, attribute, ref v, sizeof(int));
        }
        Applied?.Invoke(window);
    }

    private static int Value(Color c) => c.A == 0 ? SystemColour : ColorRef(c);

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
