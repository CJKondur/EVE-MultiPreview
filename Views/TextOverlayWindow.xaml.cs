using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;

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

    // Streamer mode (IDEAS #19): the setters keep the real text and draw StreamerMask.Name of it, so turning the mode on or
    // off (ApplyStreamerMask) redraws what is shown without being told the text again. The real text never leaves here.
    private string _rawName = "";
    private string? _rawAnnotation;
    private string? _rawSystem;

    // Thumbnail text templates (IDEAS P2). With a template the name block shows the composed text and the label, system and
    // timer blocks stay hidden; every setter below keeps its value and composes again. No template (the default) leaves
    // every block exactly as it always was. The values are the masked display text, composed from the raw text kept above.
    private string _template = "";
    private bool _nameOn = true;               // "show character name": a template's {name} is empty while it is off
    private string? _timerText;                // the session timer as drawn, null while it is off
    private string? _systemTransitionText;     // "from → to" while a system change is on show, else null

    /// <summary>The name as drawn. The login screen's thumbnail is named "EVE" (nobody is logged in): that is no player's
    /// name, so it keeps its word instead of taking a "Client N" number a character or a crop should have.</summary>
    private string ShownName() =>
        ThumbnailMenu.HasCharacter(_rawName) ? StreamerMask.Name(StreamerKind.Character, _rawName) : _rawName;

    /// <summary>Sets the text template (empty = the four separate blocks, as always). Called with every settings apply.</summary>
    public void SetTextTemplate(string? template)
    {
        string clean = TextTemplate.Clean(template);
        if (clean == _template) return;
        _template = clean;
        if (_template.Length > 0)
        {
            // The three blocks a template folds into the name block hold nothing either: hidden means gone, not just collapsed.
            AnnotationOverlay.Text = "";
            SystemOverlay.Text = "";
            TimerOverlay.Text = "";
            AnnotationOverlay.Visibility = Visibility.Collapsed;
            SystemOverlay.Visibility = Visibility.Collapsed;
            TimerOverlay.Visibility = Visibility.Collapsed;
            CancelSystemTransitionText();
            Compose();
            return;
        }

        // Back to the separate blocks: draw each from the text kept.
        string name = ShownName();
        NameOverlay.Text = name;
        NameOverlay.Visibility = _nameOn && !string.IsNullOrEmpty(name) ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnnotation(_rawAnnotation);
        UpdateSystemName(_rawSystem);
        if (_timerText != null)
        {
            TimerOverlay.Text = _timerText;
            TimerOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CancelSystemTransitionText()
    {
        _systemTransitionTimer?.Stop();
        _systemTransitionTimer = null;
        _systemTransitionText = null;
    }

    /// <summary>The template filled with what the blocks would show, in the name block. Nothing to show hides it.</summary>
    private void Compose()
    {
        if (_template.Length == 0) return;
        var values = new System.Collections.Generic.Dictionary<string, string?>
        {
            [TextTemplate.NameToken] = _nameOn ? ShownName() : "",
            [TextTemplate.LabelToken] = StreamerMask.Name(StreamerKind.Label, _rawAnnotation),
            [TextTemplate.SystemToken] = _systemTransitionText ?? StreamerMask.Name(StreamerKind.System, _rawSystem),
            [TextTemplate.TimerToken] = _timerText,
        };
        string text = TextTemplate.Render(_template, values);
        if (NameOverlay.Text != text) NameOverlay.Text = text;
        NameOverlay.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateCharacterName(string name)
    {
        _rawName = name ?? "";
        if (_template.Length > 0) { Compose(); return; }
        string shown = ShownName();
        if (!string.IsNullOrEmpty(shown))
        {
            NameOverlay.Text = shown;
            NameOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            NameOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateAnnotation(string? text)
    {
        _rawAnnotation = text;
        if (_template.Length > 0) { Compose(); return; }
        string shown = StreamerMask.Name(StreamerKind.Label, text);
        if (!string.IsNullOrEmpty(shown))
        {
            AnnotationOverlay.Text = shown;
            AnnotationOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            AnnotationOverlay.Text = "";   // hidden means gone: nothing of the real label stays behind a collapsed element
            AnnotationOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Streamer mode was switched on or off: draws the name, the label and the system again from the text kept. The
    /// name's visibility is left to <see cref="SetTextOverlayVisible"/> (a hidden name stays hidden); a system change
    /// being shown settles at once.</summary>
    public void ApplyStreamerMask()
    {
        if (_template.Length > 0)
        {
            CancelSystemTransitionText();   // a system change in flight settles, as it does without a template
            Compose();
            return;
        }
        string name = ShownName();
        if (!string.IsNullOrEmpty(name)) NameOverlay.Text = name;
        UpdateAnnotation(_rawAnnotation);
        UpdateSystemName(_rawSystem);
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
        _systemTransitionText = null;
        SystemOverlay.BeginAnimation(OpacityProperty, null);
        SystemOverlay.Opacity = 1.0;

        _rawSystem = systemName;
        if (_template.Length > 0) { Compose(); return; }
        string shown = StreamerMask.Name(StreamerKind.System, systemName);
        if (!string.IsNullOrEmpty(shown))
        {
            SystemOverlay.Text = shown;
            SystemOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            SystemOverlay.Text = "";   // hidden means gone: nothing of the real system stays behind a collapsed element
            SystemOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Display "from → to" for <paramref name="durationMs"/>ms with a
    /// short opacity pulse, then settle to <paramref name="to"/> only (#25).
    /// Replaces any in-flight transition.</summary>
    public void AnimateSystemTransition(string from, string to, int durationMs = 3500)
    {
        // Streamer mode hides both ends of the change: nothing to animate, and nothing shown.
        string maskedTo = StreamerMask.Name(StreamerKind.System, to);
        string maskedFrom = StreamerMask.Name(StreamerKind.System, from);
        if (string.IsNullOrEmpty(maskedTo))
        {
            UpdateSystemName(string.IsNullOrEmpty(to) ? null : to);
            return;
        }
        _rawSystem = to;

        if (_template.Length > 0)
        {
            // In a template the change reads "from → to" inside the {system} token, then settles to the new system.
            _systemTransitionTimer?.Stop();
            _systemTransitionText = string.IsNullOrEmpty(maskedFrom) ? maskedTo : $"{maskedFrom} → {maskedTo}";
            Compose();
            _systemTransitionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            _systemTransitionTimer.Tick += (_, _) =>
            {
                CancelSystemTransitionText();
                Compose();
            };
            _systemTransitionTimer.Start();
            return;
        }

        // Cancel previous transition if still running
        _systemTransitionTimer?.Stop();
        SystemOverlay.BeginAnimation(OpacityProperty, null);

        // Show the transition text immediately
        SystemOverlay.Text = string.IsNullOrEmpty(maskedFrom) ? maskedTo : $"{maskedFrom} → {maskedTo}";
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
            SystemOverlay.Text = maskedTo;
        };
        _systemTransitionTimer.Start();
    }

    public void UpdateSessionTimer(TimeSpan elapsed)
    {
        _timerText = $"{elapsed:hh\\:mm\\:ss}";
        if (_template.Length > 0) { Compose(); return; }
        TimerOverlay.Text = _timerText;
        TimerOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>The session timer is turned off: hide it (it starts hidden in the XAML).</summary>
    public void HideSessionTimer()
    {
        _timerText = null;
        if (_template.Length > 0) { Compose(); return; }
        TimerOverlay.Visibility = Visibility.Collapsed;
    }

    public void SetTextOverlayVisible(bool visible)
    {
        _nameOn = visible;
        if (_template.Length > 0) { Compose(); return; }
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

    public void SetTextStyle(string fontFamily, double fontSize, Color color, double fpsFontSizePt = 0)
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
        // 0 = follow the thumbnail text size, as it always did (#103).
        FpsOverlay.FontSize = fpsFontSizePt > 0 ? fpsFontSizePt * (96.0 / 72.0) : dipSize;
        FpsOverlay.Foreground = brush;

        // Alert text: same face, a step larger so it reads at a glance; its colour is the alert's.
        AlertTextOverlay.FontFamily = font;
        AlertTextOverlay.FontSize = dipSize + (2 * (96.0 / 72.0));
    }

    public void SetTextMargins(int marginX, int marginY, int fpsMarginX = -1, int fpsMarginY = -1)
    {
        // TextOverlayPanel is HorizontalAlignment="Left", so the RIGHT margin positions
        // nothing — it only subtracts from the available width. Passing marginX as both
        // capped the label's usable travel at half the thumbnail width and then clipped
        // it away entirely (#102). FpsOverlay below really is Right-aligned, so marginX
        // as its right inset is correct there and stays.
        TextOverlayPanel.Margin = new Thickness(marginX, marginY, 0, 0);
        // FpsOverlay is anchored to the RIGHT, so driving its inset from the same
        // setting as the left-aligned label moved the two toward each other and they
        // overlapped in the middle with no collision handling (#102). Before the label
        // fix above it was squeezed out before it could reach the counter, which hid
        // this. The counter now keeps its own fixed inset; only the vertical margin,
        // which applies to both edges the same way, is still shared.
        // Both counter insets are opt-in (#103): -1 keeps the shared vertical
        // margin and the fixed 5px right inset that #102 settled on, so an
        // untouched config renders exactly as before.
        FpsOverlay.Margin = new Thickness(
            0,
            fpsMarginY >= 0 ? fpsMarginY : marginY,
            fpsMarginX >= 0 ? fpsMarginX : 5,
            0);
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

    /// <summary>Show or hide the cycle-exclusion badge (issue #16 / #27 / #40).
    /// Drawn in the WPF overlay rather than the WinForms ThumbnailWindow because
    /// the DWM thumbnail composites over the underlying form's client area and
    /// would hide the indicator entirely. Replaced the original full-thumbnail
    /// red X with a small top-left badge per #40.</summary>
    public void SetCycleExcluded(bool excluded)
    {
        ExclusionBadge.Visibility = excluded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Move the cycle-exclusion badge to one of nine anchor points so
    /// it can be kept clear of the character name / system / process-stats
    /// overlays. Issue #41.</summary>
    public void SetCycleExclusionPosition(string position) => Anchor(ExclusionBadge, position ?? "TopLeft");

    /// <summary>Place an overlay element at one of nine anchor points, inset from the edge.</summary>
    private static void Anchor(FrameworkElement el, string position)
    {
        const double inset = 4;
        switch (position.Trim())
        {
            case "Top":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                el.Margin = new Thickness(0, inset, 0, 0);
                break;
            case "TopRight":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                el.Margin = new Thickness(0, inset, inset, 0);
                break;
            case "Left":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                el.Margin = new Thickness(inset, 0, 0, 0);
                break;
            case "Center":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                el.Margin = new Thickness(0);
                break;
            case "Right":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                el.Margin = new Thickness(0, 0, inset, 0);
                break;
            case "BottomLeft":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                el.Margin = new Thickness(inset, 0, 0, inset);
                break;
            case "Bottom":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                el.Margin = new Thickness(0, 0, 0, inset);
                break;
            case "BottomRight":
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                el.Margin = new Thickness(0, 0, inset, inset);
                break;
            case "TopLeft":
            default:
                el.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                el.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                el.Margin = new Thickness(inset, inset, 0, 0);
                break;
        }
    }

    /// <summary>Show what the current alert is on the thumbnail (#115); null hides it.
    /// Called every flash tick, so an unchanged text/colour is a no-op.</summary>
    public void SetAlertText(string? text, Color color)
    {
        if (string.IsNullOrEmpty(text))
        {
            AlertTextOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (AlertTextOverlay.Text != text) AlertTextOverlay.Text = text;
        if (AlertTextOverlay.Foreground is not SolidColorBrush b || b.Color != color)
            AlertTextOverlay.Foreground = new SolidColorBrush(color);
        AlertTextOverlay.Visibility = Visibility.Visible;
    }

    public void SetAlertTextPosition(string position) => Anchor(AlertTextOverlay, position ?? "Bottom");

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

    /// <summary>Update the unread-alert badge in the top-right corner.
    /// count = 0 hides it; count >= 10 displays as "9+". colorHex is the
    /// severity-tracked colour (`#RRGGBB` form).</summary>
    public void SetAlertBadge(int count, string colorHex)
    {
        if (count <= 0)
        {
            AlertBadge.Visibility = Visibility.Collapsed;
            return;
        }

        AlertBadgeText.Text = count >= 10 ? "9+" : count.ToString();
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
            AlertBadge.Background = new SolidColorBrush(c);
        }
        catch
        {
            AlertBadge.Background = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));
        }
        AlertBadge.Visibility = Visibility.Visible;
    }

    /// <summary>IDEAS #22, #47: shows what the client itself is doing ("Not responding", "Disconnected") in the middle,
    /// over every other overlay; null or empty hides it.</summary>
    public void SetClientMark(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            ClientMarkBadge.Visibility = Visibility.Collapsed;
            return;
        }
        if (ClientMarkText.Text != text) ClientMarkText.Text = text;
        ClientMarkBadge.Visibility = Visibility.Visible;
    }

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
