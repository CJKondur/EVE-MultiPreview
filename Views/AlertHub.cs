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
    private readonly Window _hubWindow;
    private readonly Border _hubBadge;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _hubIcon;
    private readonly List<AlertToast> _activeToasts = new();
    private int _unreadCount = 0;
    private readonly AppSettings _settings;
    private Window? _directionPicker;
    private bool _isSuspended = false;
    private System.Windows.Point _mouseDownPoint;

    // Focus-aware z-ordering (AHK: 300ms timer)
    private DispatcherTimer? _focusTimer;

    // Toast stacking direction: 0=up, 1=right, 2=down, 3=left
    public int ToastDirection { get; set; }
    public int ToastDurationSeconds { get; set; } = 6;

    // Event for focusing EVE windows
    public event Action<string>? FocusCharacterRequested;

    // Event for requesting settings save (direction change, position change)
    public event Action? SaveRequested;

    public AlertHub(AppSettings settings)
    {
        _settings = settings;

        // Legacy direction clamp (AHK: direction >3 → 0)
        int dir = settings.AlertToastDirection;
        ToastDirection = Math.Clamp(dir, 0, 3);
        if (dir != ToastDirection)
            Debug.WriteLine($"[AlertHub:Direction] ⚠ Clamped direction {dir} → {ToastDirection}");

        ToastDurationSeconds = settings.AlertToastDuration;

        // Create the hub window (C6: 64x64 to match AHK hexagonal size)
        _hubWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 68,
            Height = 68,
            ResizeMode = ResizeMode.NoResize
        };

        // Create hub button with badge
        var grid = new Grid();

        // C6: Hub hexagon (matches AHK AlertHub hexagonal shape)
        var hubHexagon = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection
            {
                new System.Windows.Point(32, 0),   // top
                new System.Windows.Point(60, 16),  // top-right
                new System.Windows.Point(60, 48),  // bottom-right
                new System.Windows.Point(32, 64),  // bottom
                new System.Windows.Point(4, 48),   // bottom-left
                new System.Windows.Point(4, 16)    // top-left
            },
            Fill = new SolidColorBrush(Color.FromRgb(50, 50, 60)),
            Stroke = new SolidColorBrush(Color.FromRgb(80, 140, 220)),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        // C6: Default icon 🚨 (matches AHK)
        _hubIcon = new TextBlock
        {
            Text = "\U0001F6A8",  // 🚨
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Container for hexagon + icon overlay
        var hubContainer = new Grid { Width = 64, Height = 64 };
        hubContainer.Children.Add(hubHexagon);
        hubContainer.Children.Add(_hubIcon);

        // Badge (notification count)
        _hubBadge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };

        _badgeText = new TextBlock
        {
            FontSize = 9,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _hubBadge.Child = _badgeText;

        grid.Children.Add(hubContainer);
        grid.Children.Add(_hubBadge);
        _hubWindow.Content = grid;

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

        Debug.WriteLine($"[AlertHub:Focus] 🔧 AlertHub created, direction={ToastDirection}, duration={ToastDurationSeconds}s");
    }

    public void Show()
    {
        if (_settings.AlertHubEnabled)
        {
            _hubWindow.Show();
            _focusTimer?.Start();
            Debug.WriteLine("[AlertHub:Focus] ✅ AlertHub shown, focus timer started");
        }
    }

    public void Hide()
    {
        _hubWindow.Hide();
        _focusTimer?.Stop();
    }

    /// <summary>C8: Set suspended state — blocks toasts and changes icon to ⏸.</summary>
    public void SetSuspended(bool suspended)
    {
        _isSuspended = suspended;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _hubIcon.Text = suspended ? "⏸" : "\U0001F6A8"; // ⏸ or 🚨
        });
        Debug.WriteLine($"[AlertHub:Suspend] {(suspended ? "⏸ Suspended" : "▶ Resumed")}");
    }

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
            // Increment badge
            _unreadCount++;
            UpdateBadge();

            // Get severity color
            var color = GetSeverityColor(severity);

            // Create toast window
            var toast = new AlertToast(characterName, alertType, severity, color, ToastDurationSeconds);
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

    /// <summary>Focus-aware z-ordering: only Topmost when EVE or our app is foreground.</summary>
    private void UpdateFocusAwareZOrder()
    {
        try
        {
            var fgHwnd = Interop.User32.GetForegroundWindow();
            string? fgProc = null;
            try { fgProc = Interop.User32.GetProcessName(fgHwnd); } catch { }

            bool shouldBeTopmost = fgProc is "exefile" or "EveMultiPreview" or "devenv" or "dotnet";

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
        double spacing = 5;
        double toastH = 50;
        double toastW = 200;
        double hubW = 68;
        double hubH = 68;
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
            _hubIcon.Text = _unreadCount > 0 ? "🚨" : "🔔";
            return;
        }

        _hubIcon.Text = "✕"; // Swap to ✕ while picker is open

        var accent = Color.FromRgb(227, 106, 13); // #E36A0D
        var dimmed = Color.FromRgb(100, 100, 110);

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

        // Create directional arrows — all solid equilateral triangles for consistency
        string[] arrows = { "▲", "▶", "▼", "◀" };
        // Center each 28x28 button in 100x100 canvas (center=50, offset=50-14=36)
        double[,] positions = { { 36, 2 }, { 70, 36 }, { 36, 70 }, { 2, 36 } };

        for (int i = 0; i < 4; i++)
        {
            int dir = i;
            bool isCurrentDir = ToastDirection == dir;

            var arrowBtn = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(isCurrentDir ? accent : dimmed),
                BorderBrush = new SolidColorBrush(isCurrentDir ? accent : Color.FromRgb(60, 60, 70)),
                BorderThickness = new Thickness(isCurrentDir ? 2 : 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock
                {
                    Text = arrows[i],
                    FontSize = 14,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            Canvas.SetLeft(arrowBtn, positions[i, 0]);
            Canvas.SetTop(arrowBtn, positions[i, 1]);

            arrowBtn.MouseLeftButtonDown += (_, _) =>
            {
                ToastDirection = dir;
                _settings.AlertToastDirection = dir;
                SaveRequested?.Invoke();
                ReflowToasts();
                popup.Close();
                _hubIcon.Text = _unreadCount > 0 ? "🚨" : "🔔";
                _directionPicker = null;
                Debug.WriteLine($"[AlertHub:Direction] ✅ Toast direction set to {dir} ({arrows[dir]})");
            };

            canvas.Children.Add(arrowBtn);
        }

        // Close button in center
        var closeBtn = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 70)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 110)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(closeBtn, 37);
        Canvas.SetTop(closeBtn, 37);
        closeBtn.MouseLeftButtonDown += (_, _) =>
        {
            popup.Close();
            _hubIcon.Text = _unreadCount > 0 ? "🚨" : "🔔";
            _directionPicker = null;
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
            _hubIcon.Text = _unreadCount > 0 ? "🚨" : "🔔";
            _directionPicker = null;
        };

        popup.Show();
        _directionPicker = popup;
        Debug.WriteLine($"[AlertHub:Direction] 🔧 Direction picker opened (current={ToastDirection})");
    }

    private void UpdateBadge()
    {
        if (_unreadCount > 0)
        {
            _badgeText.Text = _unreadCount > 9 ? "9+" : _unreadCount.ToString();
            _hubBadge.Visibility = Visibility.Visible;

            // Hub emoji swap (AHK: 🚨 when active)
            _hubIcon.Text = "🚨";

            // Badge pulse animation (AHK: 4×200ms white↔red)
            var pulse = new ColorAnimation(
                Colors.Red, Colors.White,
                TimeSpan.FromMilliseconds(200))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(4)
            };
            _hubBadge.Background = new SolidColorBrush(Colors.Red);
            ((SolidColorBrush)_hubBadge.Background).BeginAnimation(SolidColorBrush.ColorProperty, pulse);
        }
        else
        {
            _hubBadge.Visibility = Visibility.Collapsed;
            _hubIcon.Text = "🔔";
        }
    }

    private Color GetSeverityColor(string severity)
    {
        if (_settings.SeverityColors.TryGetValue(severity, out var hex))
            return ThumbnailManager.ParseColor(hex);

        return severity switch
        {
            "critical" => Color.FromRgb(255, 51, 51),
            "warning" => Color.FromRgb(255, 165, 0),
            "info" => Color.FromRgb(74, 158, 255),
            _ => Color.FromRgb(150, 150, 150)
        };
    }

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

/// <summary>Individual toast notification window.</summary>
internal class AlertToast : IDisposable
{
    public Window Window { get; }
    public event Action? Clicked;
    private readonly DispatcherTimer _dismissTimer;

    public AlertToast(string characterName, string alertType, string severity, Color severityColor, int durationSeconds)
    {
        Window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 200,
            Height = 50,
            ResizeMode = ResizeMode.NoResize
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 40)),
            BorderBrush = new SolidColorBrush(severityColor),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var stack = new StackPanel();

        // Alert type + severity icon
        string icon = severity switch
        {
            "critical" => "🚨",
            "warning" => "⚠️",
            "info" => "ℹ️",
            _ => "🔔"
        };

        string displayType = alertType switch
        {
            "attack" => "Under Attack!",
            "warp_scramble" => "Warp Scrambled!",
            "decloak" => "Decloaked!",
            "fleet_invite" => "Fleet Invite",
            "convo_request" => "Convo Request",
            "system_change" => "System Changed",
            "mine_cargo_full" => "Cargo Full!",
            "mine_asteroid_depleted" => "Asteroid Depleted",
            "mine_crystal_broken" => "Crystal Broken!",
            "mine_module_stopped" => "Module Stopped",
            _ => alertType
        };

        stack.Children.Add(new TextBlock
        {
            Text = $"{icon} {displayType}",
            Foreground = new SolidColorBrush(severityColor),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        });

        stack.Children.Add(new TextBlock
        {
            Text = characterName,
            Foreground = Brushes.White,
            FontSize = 10,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Opacity = 0.8
        });

        border.Child = stack;

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
