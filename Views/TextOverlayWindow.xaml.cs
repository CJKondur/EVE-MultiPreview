using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EveMultiPreview.Interop;

using Color = System.Windows.Media.Color;

namespace EveMultiPreview.Views;

/// <summary>
/// Separate overlay window for text ON TOP of DWM thumbnails.
/// Uses WPF AllowsTransparency (per-pixel alpha) for transparency.
/// Win32 ownership (GWL_HWNDPARENT) keeps it z-ordered above thumbnail.
/// </summary>
public partial class TextOverlayWindow : Window
{
    private IntPtr _ownHwnd;

    public TextOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        _ownHwnd = source.Handle;

        // Click-through + non-activating + toolwindow (matches AHK +E0x20)
        int exStyle = User32.GetWindowLong(_ownHwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(_ownHwnd, User32.GWL_EXSTYLE,
            exStyle | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE
                    | User32.WS_EX_TOOLWINDOW);
    }

    // ── Text Updates ─────────────────────────────────────────────────

    public void UpdateCharacterName(string name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            NameOverlay.Text = name;
            NameOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            NameOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateAnnotation(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            AnnotationOverlay.Text = text;
            AnnotationOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            AnnotationOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateSystemName(string? systemName)
    {
        if (!string.IsNullOrEmpty(systemName))
        {
            SystemOverlay.Text = systemName;
            SystemOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            SystemOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateSessionTimer(TimeSpan elapsed)
    {
        TimerOverlay.Text = $"{elapsed:hh\\:mm\\:ss}";
        TimerOverlay.Visibility = Visibility.Visible;
    }

    public void SetTextOverlayVisible(bool visible)
    {
        // Only controls the character name overlay — other overlays (system, timer, 
        // annotations, process stats) are managed independently by their own settings
        NameOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetTextStyle(string fontFamily, double fontSize, Color color)
    {
        var brush = new SolidColorBrush(color);
        var font = new System.Windows.Media.FontFamily(fontFamily);
        double dipSize = fontSize * (96.0 / 72.0);

        NameOverlay.FontFamily = font;
        NameOverlay.FontSize = dipSize;
        NameOverlay.Foreground = brush;

        AnnotationOverlay.FontFamily = font;
        AnnotationOverlay.FontSize = Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        AnnotationOverlay.Foreground = brush;

        SystemOverlay.FontFamily = font;
        SystemOverlay.FontSize = Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        SystemOverlay.Foreground = brush;

        TimerOverlay.FontFamily = font;
        TimerOverlay.FontSize = Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        TimerOverlay.Foreground = brush;
    }

    public void SetTextMargins(int marginX, int marginY)
    {
        TextOverlayPanel.Margin = new Thickness(marginX, marginY, marginX, 0);
    }

    // ── Process Stats ────────────────────────────────────────────────

    public void UpdateProcessStats(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            ProcessStatsOverlay.Text = text;
            ProcessStatsOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ProcessStatsOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void SetProcessStatsTextSize(double fontSize)
    {
        double dipSize = fontSize * (96.0 / 72.0);
        ProcessStatsOverlay.FontSize = Math.Max(dipSize, 6);
    }

    // ── Position/Size sync ──────────────────────────────────────────

    public void SyncPosition(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>Get the Win32 HWND for this window (for Win32 owner relationship).</summary>
    public IntPtr GetHwnd() => _ownHwnd;
}
