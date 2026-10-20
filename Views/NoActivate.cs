using System;
using System.Windows;
using System.Windows.Interop;
using User32 = EveMultiPreview.Interop.User32;

namespace EveMultiPreview.Views;

/// <summary>The no-activate style a toast has (IDEAS P80 (7)): a click on the window must not make it the foreground
/// window, or the keys stop reaching the EVE client for as long as it is the one in front. ShowActivated = false covers
/// only the moment a window is shown. The alert hub's own window and its direction picker take it from here; the
/// toast keeps its own copy, which a test of the hub's file reads as exact text.
///
/// It adds two bits to the extended style the window has and leaves every other bit, the topmost one with it, as WPF
/// set it: a window's z-order is managed one way only (the hub's focus-aware Topmost). Nothing here activates,
/// raises or focuses a window.</summary>
public static class NoActivate
{
    /// <summary>The extended style with the two bits added.</summary>
    public static int With(int ex) => ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;

    /// <summary>Gives <paramref name="window"/> the style as soon as it has a handle, before it is shown. Call it before
    /// the window is shown.</summary>
    public static void Apply(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, With(ex));
    };
}
