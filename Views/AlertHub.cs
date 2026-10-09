using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.Views;

/// <summary>
/// Floating alert hub with toast notifications.
/// AHK parity: focus-aware z-ordering, hub position persistence,
/// direction picker, badge dismiss all, emoji swap, legacy direction clamp.
/// Debug logging with [AlertHub:*] tags.
/// </summary>
public class AlertHub : IDisposable
{
    /// <summary>Translated name of an alert event, shared by the hub toast and the
    /// text written on the alerting thumbnail (#115).</summary>
    public static string EventDisplayName(string alertType) =>
        Services.LocalizationService.Str("L.AlertEvt." + alertType, alertType switch
        {
            "attack" => "Under attack",
            "warp_scramble" => "Warp scrambled",
            "decloak" => "Decloaked",
            "fleet_invite" => "Fleet invite",
            "convo_request" => "Convo request",
            "system_change" => "System changed",
            "mine_cargo_full" => "Cargo full",
            "mine_asteroid_depleted" => "Asteroid depleted",
            "mine_crystal_broken" => "Crystal broken",
            "mine_module_stopped" => "Miner stopped",
            "follow_warp" => "Fleet warp",
            "regroup" => "Regrouping",
            _ => alertType
        });

    private readonly Window _hubWindow;
    private readonly AlertHubVisuals.HubParts _hub;
    private readonly List<AlertToast> _activeToasts = new();
    private int _unreadCount = 0;
    private readonly AppSettings _settings;
    private Window? _directionPicker;
    private bool _isSuspended = false;
    private System.Windows.Point _mouseDownPoint;

    // Focus-aware z-ordering (AHK: 300ms timer)
    private DispatcherTimer? _focusTimer;
    private DispatcherTimer? _autoHideTimer;

    // Toast stacking direction: 0=up, 1=right, 2=down, 3=left
    public int ToastDirection { get; set; }
    public int ToastDurationSeconds { get; set; } = 6;

    // Event for focusing EVE windows
    public event Action<string>? FocusCharacterRequested;

    // Event for requesting settings save (direction change, position change)
    public event Action? SaveRequested;

    /// <summary>The system a character was last seen in, for the toast (App sets ThumbnailManager.GetCharacterSystem).</summary>
    public Func<string, string?>? SystemLookup { get; set; }

    /// <summary>The bell, its badge and the icon (tests render them; the window itself is never shown in a test).</summary>
    public AlertHubVisuals.HubParts Parts => _hub;

    public AlertHub(AppSettings settings)
    {
        _settings = settings;

        // Legacy direction clamp (AHK: direction >3 → 0)
        int dir = settings.AlertToastDirection;
        ToastDirection = Math.Clamp(dir, 0, 3);
        if (dir != ToastDirection)
            Debug.WriteLine($"[AlertHub:Direction] ⚠ Clamped direction {dir} → {ToastDirection}");

        ToastDurationSeconds = Math.Max(1, settings.AlertToastDuration);

        // The hub window (68 × 68, as before, so saved positions and the toast stack don't move) holds the round bell
        // and its count badge (Plan 4, the F2 reference). Transparent around the bell: clicks there reach the window below.
        _hubWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false, // Passive overlay: showing it must never take focus from the EVE client (#110).
            Topmost = true,
            Width = AlertHubVisuals.HubWindowSize,
            Height = AlertHubVisuals.HubWindowSize,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = AlertHubVisuals.TextFont,
        };
        _hub = AlertHubVisuals.BuildHub();
        var hubContainer = _hub.Bell;
        _hubWindow.Content = _hub.Root;

        // Position from settings
        if (settings.AlertHubX != 0 || settings.AlertHubY != 0)
        {
            _hubWindow.Left = settings.AlertHubX;
            _hubWindow.Top = settings.AlertHubY;
        }
        else
        {
            _hubWindow.Left = SystemParameters.PrimaryScreenWidth - 60;
            _hubWindow.Top = SystemParameters.PrimaryScreenHeight / 2;
        }

        // ── Hub position persistence (save on drag) ──
        _hubWindow.LocationChanged += (_, _) =>
        {
            settings.AlertHubX = (int)_hubWindow.Left;
            settings.AlertHubY = (int)_hubWindow.Top;
            SaveRequested?.Invoke();
            Debug.WriteLine($"[AlertHub:Position] 📍 Hub moved to ({settings.AlertHubX}, {settings.AlertHubY})");
        };

        // Right-click shows direction picker (AHK: circular overlay with ▲►▼◄)
        hubContainer.MouseRightButtonDown += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                _hubWindow.DragMove(); // Ctrl+right for drag
            else
                ShowDirectionPicker();
        };

        // C7: Left-click with drag detection (move >3px = drag, else dismiss)
        hubContainer.MouseLeftButtonDown += (_, e) =>
        {
            _mouseDownPoint = e.GetPosition(_hubWindow);
            hubContainer.CaptureMouse();
        };
        hubContainer.MouseLeftButtonUp += (_, e) =>
        {
            hubContainer.ReleaseMouseCapture();
            var upPoint = e.GetPosition(_hubWindow);
            double dx = Math.Abs(upPoint.X - _mouseDownPoint.X);
            double dy = Math.Abs(upPoint.Y - _mouseDownPoint.Y);

            if (dx > 3 || dy > 3)
            {
                // Was a drag — position already updated via DragMove
                return;
            }

            // Was a click — dismiss all toasts + clear badge
            _unreadCount = 0;
            UpdateBadge();
            foreach (var toast in _activeToasts.ToList())
            {
                try { toast.Window.Close(); } catch { }
            }
            _activeToasts.Clear();
            Debug.WriteLine("[AlertHub:Dismiss] ✅ All toasts dismissed via hub click");
        };
        hubContainer.MouseMove += (_, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                var currentPos = e.GetPosition(_hubWindow);
                double dx = Math.Abs(currentPos.X - _mouseDownPoint.X);
                double dy = Math.Abs(currentPos.Y - _mouseDownPoint.Y);
                if (dx > 3 || dy > 3)
                {
                    if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                    hubContainer.ReleaseMouseCapture();
                    try { _hubWindow.DragMove(); } catch (InvalidOperationException) { }
                }
            }
        };

        // ── Focus-aware z-ordering timer (faster than AHK's 300ms) ──
        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _focusTimer.Tick += (_, _) => UpdateFocusAwareZOrder();

        // ── Auto-hide timer (issue #42 suggestion) ──
        // When AlertHubAutoHide is on, the hub hides itself after a quiet
        // period and re-shows on the next alert. Timer is reset on every
        // toast — so a burst of alerts keeps the hub up; a quiet pause
        // hides it. One-shot per cycle, restarted by KickAutoHide().
        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (_settings.AlertHubAutoHide) Hide();
        };

        Debug.WriteLine($"[AlertHub:Focus] 🔧 AlertHub created, direction={ToastDirection}, duration={ToastDurationSeconds}s");
    }

    public void Show()
    {
        if (_settings.AlertHubEnabled)
        {
            _hubWindow.Show();
            _focusTimer?.Start();
            // If auto-hide is on, start the countdown immediately so a hub
            // that came up with no traffic (e.g. on app launch) goes away.
            // Subsequent toasts will reset the timer via KickAutoHide.
            if (_settings.AlertHubAutoHide) KickAutoHide();
            Debug.WriteLine("[AlertHub:Focus] ✅ AlertHub shown, focus timer started");
        }
    }

    public void Hide()
    {
        _hubWindow.Hide();
        _focusTimer?.Stop();
        _autoHideTimer?.Stop();
    }

    /// <summary>Re-reads the toast settings the constructor read once: AlertToastDuration, and
    /// AlertToastDirection clamped to 0–3 as at startup. App calls this on every settings apply, so a
    /// change shows on the next toast instead of after a restart; toasts already up move to the new side.</summary>
    public void ApplySettings()
    {
        ToastDurationSeconds = Math.Max(1, _settings.AlertToastDuration);
        int dir = Math.Clamp(_settings.AlertToastDirection, 0, 3);
        if (dir == ToastDirection) return;
        ToastDirection = dir;
        ReflowToasts();
    }

    /// <summary>The hub's place when none is saved (AlertHubX and AlertHubY both 0): the right edge of the
    /// primary screen, halfway down, as the constructor places it.</summary>
    public static System.Windows.Point DefaultPosition =>
        new(SystemParameters.PrimaryScreenWidth - 60, SystemParameters.PrimaryScreenHeight / 2);

    /// <summary>Settings › Alert hub › Reset position: the hub moves back to its default place now and the saved
    /// place is forgotten (0, 0). The old reset only cleared the saved place, so the hub stayed put until a restart.</summary>
    public void ResetPosition()
    {
        var p = DefaultPosition;
        _hubWindow.Left = p.X;
        _hubWindow.Top = p.Y;
        _settings.AlertHubX = 0;   // LocationChanged may just have stored the default's coordinates
        _settings.AlertHubY = 0;
        ReflowToasts();
    }

    /// <summary>Where the hub window is (tests).</summary>
    public System.Windows.Point HubPosition => new(_hubWindow.Left, _hubWindow.Top);

    /// <summary>Reset the auto-hide countdown. Called whenever activity
    /// happens (new toast). No-op when AlertHubAutoHide is off.</summary>
    private void KickAutoHide()
    {
        if (!_settings.AlertHubAutoHide || _autoHideTimer == null) return;
        int seconds = _settings.AlertHubAutoHideSeconds > 0 ? _settings.AlertHubAutoHideSeconds : 5;
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoHideTimer.Start();
    }

    /// <summary>C8: Set suspended state — blocks toasts and shows the pause sign on the hub.</summary>
    public void SetSuspended(bool suspended)
    {
        _isSuspended = suspended;
        _hubWindow.Dispatcher.Invoke(RefreshIcon);
        Debug.WriteLine($"[AlertHub:Suspend] {(suspended ? "⏸ Suspended" : "▶ Resumed")}");
    }

    /// <summary>Whether toasts are paused (tests).</summary>
    public bool IsSuspended => _isSuspended;

    /// <summary>Toasts pause whenever hotkeys pause, whichever way they were paused: the pause hotkey, Home's quick
    /// action or the tray (IDEAS P37). One rule, at the one place the pause state changes.</summary>
    public void FollowPauseOf(HotkeyService hotkeys) =>
        hotkeys.SuspendToggled += () => SetSuspended(hotkeys.IsSuspended);

    /// <summary>The hub's icon follows one rule (AlertHubVisuals.HubGlyph): picker open, paused, else the bell.</summary>
    private void RefreshIcon() => _hub.Icon.Text = AlertHubVisuals.HubGlyph(_directionPicker != null, _isSuspended);

    /// <summary>Show a toast notification for an alert event.</summary>
    public void ShowToast(string characterName, string alertType, string severity)
    {
        // C8: Block toasts when suspended
        if (_isSuspended)
        {
            Debug.WriteLine($"[AlertHub:Suspend] ⛔ Toast blocked (suspended): {alertType} for '{characterName}'");
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            // If auto-hide stashed the hub, bring it back. Show() is a no-op
            // when already visible. Then reset the auto-hide countdown so
            // bursts of toasts keep the hub up until things go quiet.
            if (_settings.AlertHubAutoHide && !_hubWindow.IsVisible)
                Show();
            KickAutoHide();

            // Increment badge
            _unreadCount++;
            UpdateBadge();

            // The focus-aware state now, then the toast with it: above EVE, never above another program (IDEAS P38).
            UpdateFocusAwareZOrder();
            var toast = NewToast(characterName, alertType, severity);

            // No more toasts than fit between the hub and the screen edge (IDEAS P39): the oldest closes early. Its
            // alert is still on its thumbnail and counted on the bell.
            int fit = AlertHubVisuals.ToastsThatFit(ToastDirection, HubBounds, HubWorkArea());
            while (_activeToasts.Count >= fit)
            {
                var oldest = _activeToasts[0];
                _activeToasts.RemoveAt(0);
                try { oldest.Window.Close(); } catch { }
            }

            toast.Window.Closed += (_, _) =>
            {
                _activeToasts.RemoveAll(t => t == toast);
                ReflowToasts();
            };
            toast.Clicked += () =>
            {
                Debug.WriteLine($"[AlertHub:Toast] 🎯 Toast clicked — focusing '{characterName}'");
                FocusCharacterRequested?.Invoke(characterName);
            };

            _activeToasts.Add(toast);
            PositionToast(toast, _activeToasts.Count - 1);
            toast.Show();

            Debug.WriteLine($"[AlertHub:Toast] 📢 Toast shown: {alertType} [{severity}] for '{characterName}' (active={_activeToasts.Count})");
        });
    }

    /// <summary>A toast for an alert, not shown yet. It starts with the hub's focus-aware Topmost (IDEAS P38): it used to
    /// start topmost and stay over a browser in front until it closed.</summary>
    private AlertToast NewToast(string characterName, string alertType, string severity) =>
        new(characterName, alertType, ToastColor(alertType, severity), ToastDurationSeconds,
            SystemLookup?.Invoke(characterName), topmost: _hubWindow.Topmost);

    /// <summary>The hub window's place and size, in WPF units.</summary>
    private Rect HubBounds => new(_hubWindow.Left, _hubWindow.Top, AlertHubVisuals.HubWindowSize, AlertHubVisuals.HubWindowSize);

    /// <summary>The work area of the monitor the hub is on, in WPF units; the primary one's before the hub has a window.</summary>
    private Rect HubWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(_hubWindow).Handle;
        var source = PresentationSource.FromVisual(_hubWindow);
        if (handle == IntPtr.Zero || source?.CompositionTarget == null) return SystemParameters.WorkArea;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var toWpf = source.CompositionTarget.TransformFromDevice;
        return new Rect(toWpf.Transform(new System.Windows.Point(area.Left, area.Top)),
                        toWpf.Transform(new System.Windows.Point(area.Right, area.Bottom)));
    }

    /// <summary>Focus-aware z-ordering: only Topmost when EVE or our app is foreground.</summary>
    private void UpdateFocusAwareZOrder()
    {
        try
        {
            var fgHwnd = Interop.User32.GetForegroundWindow();
            string? fgProc = null;
            try { fgProc = Interop.User32.GetProcessName(fgHwnd); } catch { }

            bool shouldBeTopmost = Interop.User32.IsEveOrAppProcess(fgProc) || fgProc is "devenv" or "dotnet";

            if (_hubWindow.Topmost != shouldBeTopmost)
            {
                _hubWindow.Topmost = shouldBeTopmost;
                foreach (var toast in _activeToasts)
                    toast.Window.Topmost = shouldBeTopmost;

                Debug.WriteLine($"[AlertHub:Focus] 🔄 Z-order changed: topmost={shouldBeTopmost} (fg={fgProc})");
            }
        }
        catch { }
    }

    private void PositionToast(AlertToast toast, int index)
    {
        double spacing = AlertHubVisuals.ToastGap;
        double toastH = AlertHubVisuals.ToastHeight;
        double toastW = AlertHubVisuals.ToastWidth;
        double hubW = AlertHubVisuals.HubWindowSize;
        double hubH = AlertHubVisuals.HubWindowSize;
        double hubCenterX = _hubWindow.Left + hubW / 2;
        double hubCenterY = _hubWindow.Top + hubH / 2;

        switch (ToastDirection)
        {
            case 0: // Up — centered horizontally, stacking upward from hub top
                toast.Window.Left = hubCenterX - toastW / 2;
                toast.Window.Top = _hubWindow.Top - spacing - toastH - index * (toastH + spacing);
                break;
            case 1: // Right — centered vertically, stacking rightward from hub right
                toast.Window.Left = _hubWindow.Left + hubW + spacing + index * (toastW + spacing);
                toast.Window.Top = hubCenterY - toastH / 2;
                break;
            case 2: // Down — centered horizontally, stacking downward from hub bottom
                toast.Window.Left = hubCenterX - toastW / 2;
                toast.Window.Top = _hubWindow.Top + hubH + spacing + index * (toastH + spacing);
                break;
            case 3: // Left — centered vertically, stacking leftward from hub left
                toast.Window.Left = _hubWindow.Left - spacing - toastW - index * (toastW + spacing);
                toast.Window.Top = hubCenterY - toastH / 2;
                break;
        }
    }

    private void ReflowToasts()
    {
        for (int i = 0; i < _activeToasts.Count; i++)
        {
            PositionToast(_activeToasts[i], i);
        }
    }

    // ── Direction Picker Popup (AHK: circular overlay with ▲►▼◄) ──

    private void ShowDirectionPicker()
    {
        if (_directionPicker != null && _directionPicker.IsVisible)
        {
            _directionPicker.Close();
            _directionPicker = null;
            RefreshIcon();
            return;
        }

        var popup = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 100,
            Height = 100,
            ResizeMode = ResizeMode.NoResize
        };

        var canvas = new Canvas
        {
            Width = 100,
            Height = 100
        };

        // Up, right, down, left: 28 × 28 buttons centred on the edges of the 100 × 100 picker (centre 50, 50 − 14 = 36).
        string[] arrows = { AlertHubVisuals.Glyphs.Up, AlertHubVisuals.Glyphs.Right, AlertHubVisuals.Glyphs.Down, AlertHubVisuals.Glyphs.Left };
        string[] names = { "up", "right", "down", "left" };
        double[,] positions = { { 36, 2 }, { 70, 36 }, { 36, 70 }, { 2, 36 } };

        for (int i = 0; i < 4; i++)
        {
            int dir = i;
            var arrowBtn = AlertHubVisuals.BuildPickerButton(arrows[i], current: ToastDirection == dir);
            Canvas.SetLeft(arrowBtn, positions[i, 0]);
            Canvas.SetTop(arrowBtn, positions[i, 1]);

            arrowBtn.MouseLeftButtonDown += (_, _) =>
            {
                ToastDirection = dir;
                _settings.AlertToastDirection = dir;
                SaveRequested?.Invoke();
                ReflowToasts();
                popup.Close();
                _directionPicker = null;
                RefreshIcon();
                Debug.WriteLine($"[AlertHub:Direction] ✅ Toast direction set to {dir} ({names[dir]})");
            };

            canvas.Children.Add(arrowBtn);
        }

        // Close button in the centre
        var closeBtn = AlertHubVisuals.BuildPickerButton(AlertHubVisuals.Glyphs.Close, current: false, size: 26, round: true);
        Canvas.SetLeft(closeBtn, 37);
        Canvas.SetTop(closeBtn, 37);
        closeBtn.MouseLeftButtonDown += (_, _) =>
        {
            popup.Close();
            _directionPicker = null;
            RefreshIcon();
        };
        canvas.Children.Add(closeBtn);

        popup.Content = canvas;
        // Center the 100x100 popup over the hub window
        double hubCenterX = _hubWindow.Left + (_hubWindow.Width / 2);
        double hubCenterY = _hubWindow.Top + (_hubWindow.Height / 2);
        popup.Left = hubCenterX - 50;
        popup.Top = hubCenterY - 50;
        popup.Deactivated += (_, _) =>
        {
            try { popup.Close(); } catch { }
            _directionPicker = null;
            RefreshIcon();
        };

        _directionPicker = popup;
        RefreshIcon();   // ✕ while the picker is open
        popup.Show();
        Debug.WriteLine($"[AlertHub:Direction] 🔧 Direction picker opened (current={ToastDirection})");
    }

    private void UpdateBadge()
    {
        AlertHubVisuals.SetBadge(_hub, _unreadCount);
        RefreshIcon();
        if (_unreadCount == 0) return;

        // Badge pulse (AHK: 4×200ms badge colour ↔ white)
        var badgeColour = FluentPalette.Color("Fx.AlertBadge");
        var pulse = new ColorAnimation(badgeColour, Colors.White, TimeSpan.FromMilliseconds(200))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(4)
        };
        var background = new SolidColorBrush(badgeColour);
        _hub.Badge.Background = background;
        background.BeginAnimation(SolidColorBrush.ColorProperty, pulse);
    }

    /// <summary>The toast bar's colour: the colour the thumbnail flashes for this alert (AlertColours.Resolve).</summary>
    public Color ToastColor(string alertType, string severity) =>
        ThumbnailManager.ParseColor(AlertColours.Resolve(_settings, alertType, severity));

    public void Dispose()
    {
        _focusTimer?.Stop();
        foreach (var toast in _activeToasts.ToList())
            toast.Dispose();
        _activeToasts.Clear();
        _hubWindow.Close();
        Debug.WriteLine("[AlertHub:Dispose] 🛑 AlertHub disposed");
    }
}

/// <summary>One toast window (Plan 4: the reference's toast, built by AlertHubVisuals). Left-click switches to the
/// character, right-click dismisses, and it closes by itself after the toast length.</summary>
internal class AlertToast : IDisposable
{
    public Window Window { get; }
    public event Action? Clicked;
    private readonly DispatcherTimer _dismissTimer;

    /// <param name="topmost">The hub's focus-aware state when the toast is made (IDEAS P38); the hub's timer flips it
    /// with the hub's own afterwards.</param>
    public AlertToast(string characterName, string alertType, Color severityColor, int durationSeconds, string? system = null,
        bool topmost = true)
    {
        Window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false, // Passive overlay: showing it must never take focus from the EVE client (#110).
            Topmost = topmost,
            Width = AlertHubVisuals.ToastWidth,
            Height = AlertHubVisuals.ToastHeight,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = AlertHubVisuals.TextFont,
        };

        var border = AlertHubVisuals.BuildToast(characterName, alertType, severityColor, system);

        // Click to focus character
        border.MouseLeftButtonDown += (_, _) =>
        {
            Clicked?.Invoke();
            Window.Close();
        };

        // Right-click to dismiss
        border.MouseRightButtonDown += (_, _) => Window.Close();

        Window.Content = border;

        // Auto-dismiss timer
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            try { Window.Close(); } catch { }
        };
    }

    public void Show()
    {
        Window.Show();
        _dismissTimer.Start();

        // Fade-in animation
        Window.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        Window.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public void Dispose()
    {
        _dismissTimer.Stop();
        try { Window.Close(); } catch { }
    }
}
