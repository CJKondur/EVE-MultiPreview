using System;
using System.Windows;
using System.Windows.Interop;
using User32 = EveMultiPreview.Interop.User32;

namespace EveMultiPreview.Views;

/// <summary>The extended style of a window that sits over the thumbnails and never takes the focus from the game (the name bar and
/// its preview, IDEAS #38): no-activate, tool window, and the topmost bit. The topmost band is never asked of WPF's Topmost: a
/// window's z-band is managed one way only, raw SetWindowPos, which its owner calls at the first frame (the bar's Loaded, the
/// preview's Show) and again every 250 ms. (Windows drops a topmost bit set on a window that is not yet in the band, so the
/// first SetWindowPos is what makes it stick; measured on a non-layered window.) Nothing here activates, raises or focuses a
/// window. HotkeySheetWindow keeps its own copy of the same three bits.</summary>
public static class BornTopmost
{
    /// <summary>The extended style with the three bits added.</summary>
    public static int With(int ex) => ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST;

    /// <summary>Gives <paramref name="window"/> the style as soon as it has a handle, before it is shown. Call it before the
    /// window is shown.</summary>
    public static void Apply(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, With(ex));
    };
}
