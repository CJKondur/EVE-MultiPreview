using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Services;

/// <summary>
/// Flashes a large "Monitor N" badge on each physical screen for 2.5 s, using MultiPreview's OWN
/// 1-based numbering (the order of the monitor lists). It need not match Windows' display numbers,
/// so this shows which screen each number is (issue #70). Moved unchanged from
/// SettingsWindow.OnIdentifyMonitors so both settings windows share it; the badge text comes from
/// the caller, so the new window can translate it.
/// </summary>
public static class MonitorIdentify
{
    /// <param name="label">(1-based monitor number, is the primary monitor) → the badge text.</param>
    public static void Show(Func<int, bool, string> label)
    {
        var screens = WinForms.Screen.AllScreens;
        var overlays = new List<Window>();

        for (int i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds; // physical pixels

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0xA0, 0x85)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(48, 28, 48, 28),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = label(i + 1, screens[i].Primary),
                    Foreground = Brushes.White,
                    FontSize = 72,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = System.Windows.TextAlignment.Center
                }
            };

            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                Content = badge
            };

            int sx = bounds.X, sy = bounds.Y, sw = bounds.Width, sh = bounds.Height;
            win.SourceInitialized += (_, _) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                // Click-through + no-activate so the brief overlay can't steal focus
                // or block clicks while it's up.
                int ex = Interop.User32.GetWindowLong(h, Interop.User32.GWL_EXSTYLE);
                Interop.User32.SetWindowLong(h, Interop.User32.GWL_EXSTYLE,
                    ex | Interop.User32.WS_EX_TRANSPARENT | Interop.User32.WS_EX_NOACTIVATE | Interop.User32.WS_EX_TOOLWINDOW);
                Interop.User32.SetWindowPos(h, Interop.User32.HWND_TOPMOST, sx, sy, sw, sh,
                    Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_SHOWWINDOW);
            };
            win.Show();
            overlays.Add(win);
        }

        if (overlays.Count == 0) return;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var w in overlays) { try { w.Close(); } catch { } }
        };
        timer.Start();
    }
}
