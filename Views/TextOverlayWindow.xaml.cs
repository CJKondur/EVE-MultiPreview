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
        // Stretch the strikeout lines whenever the overlay resizes (the
        // ThumbnailWindow drives this via SyncPositionPhysical/SyncPosition).
        SizeChanged += (_, _) =>
        {
            if (ExclusionStrikeout.Visibility == Visibility.Visible)
                UpdateExclusionLines();
        };
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

    // Active system-transition timer (issue #25). When a SystemChanged event
    // arrives we display "old → new" for a few seconds with an opacity pulse,
    // then settle to just the new system. A pending timer is replaced if a
    // second transition fires before the first finishes.
    private System.Windows.Threading.DispatcherTimer? _systemTransitionTimer;

    public void UpdateSystemName(string? systemName)
    {
        System.Diagnostics.Debug.WriteLine($"[TextOverlay] UpdateSystemName called with: '{systemName ?? "NULL"}'");

        // Cancel any in-flight transition animation — caller is forcing a plain set.
        _systemTransitionTimer?.Stop();
        _systemTransitionTimer = null;
        SystemOverlay.BeginAnimation(OpacityProperty, null);
        SystemOverlay.Opacity = 1.0;

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

    /// <summary>Display "from → to" for <paramref name="durationMs"/>ms with a
    /// short opacity pulse, then settle to <paramref name="to"/> only (#25).
    /// Replaces any in-flight transition.</summary>
    public void AnimateSystemTransition(string from, string to, int durationMs = 3500)
    {
        if (string.IsNullOrEmpty(to))
        {
            UpdateSystemName(null);
            return;
        }

        // Cancel previous transition if still running
        _systemTransitionTimer?.Stop();
        SystemOverlay.BeginAnimation(OpacityProperty, null);

        // Show the transition text immediately
        SystemOverlay.Text = string.IsNullOrEmpty(from) ? to : $"{from} → {to}";
        SystemOverlay.Visibility = Visibility.Visible;

        // Quick fade-in pulse to draw the eye (0.25 → 1.0 over 400ms)
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0.25,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        SystemOverlay.BeginAnimation(OpacityProperty, fade);

        // Schedule settle: after durationMs, replace the text with just `to`
        _systemTransitionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(durationMs),
        };
        _systemTransitionTimer.Tick += (_, _) =>
        {
            _systemTransitionTimer?.Stop();
            _systemTransitionTimer = null;
            SystemOverlay.BeginAnimation(OpacityProperty, null);
            SystemOverlay.Opacity = 1.0;
            SystemOverlay.Text = to;
        };
        _systemTransitionTimer.Start();
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

    // Per-label overrides (v2.0.6). When _labelColorOverride is non-null the
    // annotation foreground uses it; _labelSizePtOverride > 0 overrides the size.
    // Both reapply on each SetTextStyle call so a settings-wide color change
    // doesn't clobber the per-label override.
    private Color? _labelColorOverride;
    private int _labelSizePtOverride;
    private string _labelFontFamily = "Segoe UI";

    public void SetTextStyle(string fontFamily, double fontSize, Color color)
    {
        var brush = new SolidColorBrush(color);
        var font = new System.Windows.Media.FontFamily(fontFamily);
        double dipSize = fontSize * (96.0 / 72.0);

        _labelFontFamily = fontFamily;

        NameOverlay.FontFamily = font;
        NameOverlay.FontSize = dipSize;
        NameOverlay.Foreground = brush;

        AnnotationOverlay.FontFamily = font;
        AnnotationOverlay.FontSize = _labelSizePtOverride > 0
            ? _labelSizePtOverride * (96.0 / 72.0)
            : Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        AnnotationOverlay.Foreground = _labelColorOverride is Color lc
            ? new SolidColorBrush(lc)
            : brush;

        SystemOverlay.FontFamily = font;
        SystemOverlay.FontSize = Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        SystemOverlay.Foreground = brush;

        TimerOverlay.FontFamily = font;
        TimerOverlay.FontSize = Math.Max(dipSize - (2 * (96.0 / 72.0)), 8);
        TimerOverlay.Foreground = brush;

        FpsOverlay.FontFamily = font;
        FpsOverlay.FontSize = dipSize;
        FpsOverlay.Foreground = brush;
    }

    public void SetTextMargins(int marginX, int marginY)
    {
        TextOverlayPanel.Margin = new Thickness(marginX, marginY, marginX, 0);
        FpsOverlay.Margin = new Thickness(0, marginY, marginX, 0);
    }

    /// <summary>Fade the whole overlay window (text, annotation, all) to match
    /// the parent thumbnail's opacity. Value is 0.0 (transparent) to 1.0 (solid).
    /// Called from ThumbnailWindow.SetOpacity so the text fades with the frame
    /// instead of staying bright against a mostly-transparent thumbnail.</summary>
    public void SetWindowOpacity(double opacity)
    {
        if (opacity < 0) opacity = 0;
        if (opacity > 1) opacity = 1;
        Opacity = opacity;
    }

    /// <summary>Show or hide the diagonal cycle-exclusion strikeout (issue #16,
    /// fixes #27). Lines are drawn here (WPF overlay) rather than on the
    /// WinForms ThumbnailWindow because the DWM thumbnail composites over the
    /// underlying form's client area and would hide the strikeout entirely.</summary>
    public void SetCycleExcluded(bool excluded)
    {
        ExclusionStrikeout.Visibility = excluded ? Visibility.Visible : Visibility.Collapsed;
        if (excluded) UpdateExclusionLines();
    }

    private void UpdateExclusionLines()
    {
        // Lines span corner-to-corner of the overlay window, with a small
        // inset so the strokes don't get clipped at the edges.
        const double inset = 2;
        double w = Math.Max(0, ActualWidth - inset * 2);
        double h = Math.Max(0, ActualHeight - inset * 2);

        ExclusionLineA.X1 = inset; ExclusionLineA.Y1 = inset;
        ExclusionLineA.X2 = inset + w; ExclusionLineA.Y2 = inset + h;

        ExclusionLineB.X1 = inset + w; ExclusionLineB.Y1 = inset;
        ExclusionLineB.X2 = inset; ExclusionLineB.Y2 = inset + h;
    }

    /// <summary>Apply a per-label color and size override for the annotation.
    /// Pass empty <paramref name="colorHex"/> or <paramref name="sizePt"/>=0 to
    /// clear that override and fall back to the global thumbnail text style.</summary>
    public void SetLabelStyle(string? colorHex, int sizePt)
    {
        // Resolve color override
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            _labelColorOverride = null;
        }
        else
        {
            try
            {
                string hex = colorHex.Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = "#" + hex.Substring(2);
                if (!hex.StartsWith("#")) hex = "#" + hex;
                _labelColorOverride = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                _labelColorOverride = null;
            }
        }
        _labelSizePtOverride = sizePt > 0 ? sizePt : 0;

        // Reapply to AnnotationOverlay without disturbing the other overlays'
        // state. We don't have the caller's current global color/size cached,
        // so we apply directly from the override (color) and computed DIPs
        // (size); the next SetTextStyle call will re-integrate if globals change.
        if (_labelColorOverride is Color c)
            AnnotationOverlay.Foreground = new SolidColorBrush(c);
        if (_labelSizePtOverride > 0)
            AnnotationOverlay.FontSize = _labelSizePtOverride * (96.0 / 72.0);
    }

    // ── Process Stats ────────────────────────────────────────────────

    public void UpdateFpsStats(double fps, bool visible)
    {
        if (visible)
        {
            FpsOverlay.Text = $"{fps:0.0} fps";
            FpsOverlay.Visibility = Visibility.Visible;
            if (fps < 30) FpsOverlay.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)); // Red if low
            else if (fps < 50) FpsOverlay.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100)); // Yellow if medium
            else FpsOverlay.Foreground = NameOverlay.Foreground; // Default color if good
        }
        else
        {
            FpsOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateProcessStats(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            ProcessStatsOverlay.Text = text;
            ProcessStatsViewbox.Visibility = Visibility.Visible;
        }
        else
        {
            ProcessStatsViewbox.Visibility = Visibility.Collapsed;
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

        // Update Viewbox max width so it knows when to shrink text
        // Account for panel margins (5px each side default)
        double availableWidth = Math.Max(width - TextOverlayPanel.Margin.Left - TextOverlayPanel.Margin.Right, 20);
        ProcessStatsViewbox.MaxWidth = availableWidth;
    }

    /// <summary>Position this overlay from physical-pixel coordinates (WinForms
    /// parent). Uses Win32 SetWindowPos for exact HWND placement and updates
    /// the DIP-space Viewbox MaxWidth using the destination monitor's DPI.
    /// Required with PerMonitorV2 because ThumbnailWindow (WinForms) base.Left/
    /// base.Width are physical pixels, but WPF Left/Width are DIPs.</summary>
    public void SyncPositionPhysical(int left, int top, int width, int height)
    {
        if (_ownHwnd == IntPtr.Zero) return;
        User32.SetWindowPos(_ownHwnd, IntPtr.Zero, left, top, width, height,
            User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);

        double scale = DpiHelper.GetScaleFactorForPoint(left, top);
        double widthDip = DpiHelper.PhysicalToDip(width, scale);
        double availableWidth = Math.Max(widthDip - TextOverlayPanel.Margin.Left - TextOverlayPanel.Margin.Right, 20);
        ProcessStatsViewbox.MaxWidth = availableWidth;
    }

    /// <summary>Get the Win32 HWND for this window (for Win32 owner relationship).</summary>
    public IntPtr GetHwnd() => _ownHwnd;
}
