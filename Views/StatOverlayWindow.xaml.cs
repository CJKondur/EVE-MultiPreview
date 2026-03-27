using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;

using Point = System.Windows.Point;

namespace EveMultiPreview.Views;

/// <summary>
/// Standalone stat overlay window showing DPS, logi, mining, or ratting stats
/// for a specific character. Matches AHK stat window behavior:
///   - Right-click drag to move
///   - Right+Left click to resize (matches ThumbnailWindow pattern)
///   - Position persistence
///   - Configurable opacity and font size
/// </summary>
public partial class StatOverlayWindow : Window
{
    private IntPtr _ownHwnd;

    // Drag/resize state machine (matches ThumbnailWindow pattern exactly)
    private enum DragMode { None, Drag, Resize }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private double _dragStartLeft, _dragStartTop;
    private double _resizeStartWidth, _resizeStartHeight;
    private bool _dragMovedPastThreshold;
    private DispatcherTimer? _dragTimer;

    public string CharacterName { get; private set; } = "";
    public string StatType { get; private set; } = "DPS"; // DPS, Logi, Mining, Ratting
    public bool LockPositions { get; set; } = false;

    /// <summary>Fires when position or size changes so manager can save it.</summary>
    public event Action<StatOverlayWindow, int, int, int, int>? PositionAndSizeChanged;

    /// <summary>Fires during resize when all stat windows should resize uniformly (Ctrl NOT held).</summary>
    public event Action<StatOverlayWindow, int, int>? ResizeAll;

    /// <summary>Snap delegate: given (x, y, w, h), returns snapped (x, y).
    /// ThumbnailManager sets this to provide snap targets (stat + primary windows).</summary>
    public Func<double, double, double, double, (double x, double y)>? SnapPosition;

    public StatOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void Initialize(string characterName, string statType, int x, int y)
    {
        CharacterName = characterName;
        StatType = statType;
        Left = x;
        Top = y;

        CharNameLabel.Text = characterName;
        StatLabel.Text = "0";
        StatUnit.Text = GetUnitLabel(statType);
    }

    /// <summary>Update with multi-line overlay text (C10 parity).</summary>
    public void UpdateOverlayText(string multiLineText)
    {
        Dispatcher.Invoke(() =>
        {
            StatLabel.Text = multiLineText;
        });
    }

    /// <summary>Legacy single-value update with K/M/B/T formatting.</summary>
    public void UpdateValue(double value)
    {
        Dispatcher.Invoke(() =>
        {
            StatLabel.Text = StatTrackerService.FormatNumber(value);
        });
    }

    public void SetFontSize(int size)
    {
        Dispatcher.Invoke(() =>
        {
            double dipSize = size * (96.0 / 72.0);
            StatLabel.FontSize = dipSize;
            CharNameLabel.FontSize = dipSize + (1 * (96.0 / 72.0)); // Header slightly larger (AHK: Segoe UI 9pt vs Consolas 8pt)
            StatUnit.FontSize = dipSize;
        });
    }

    public void SetOpacity(byte opacity)
    {
        Dispatcher.Invoke(() => Opacity = opacity / 255.0);
    }

    public void SetBackgroundColor(string hex)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                RootBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD0, color.R, color.G, color.B));
            }
            catch { }
        });
    }

    public void SetTextColor(string hex)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
                StatLabel.Foreground = brush;
                StatUnit.Foreground = brush;
            }
            catch { }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        _ownHwnd = source.Handle;

        // Non-activating
        int exStyle = User32.GetWindowLong(_ownHwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(_ownHwnd, User32.GWL_EXSTYLE,
            exStyle | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);

        source.AddHook(WndProc);
    }

    // ── Win32 Message Hook (right-click drag/resize) ────────────────

    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_RBUTTONDOWN:
                OnRightButtonDown();
                handled = true;
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                OnRightButtonUp();
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnLeftButtonDown();
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnLeftButtonUp();
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void OnRightButtonDown()
    {
        if (LockPositions)
        {
            System.Diagnostics.Debug.WriteLine("[StatOverlay] 🔒 Positions locked — drag/resize blocked");
            return;
        }
        if (_dragMode != DragMode.None) return;

        var screenPos = GetScreenMousePos();
        _dragStartScreen = screenPos;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragMovedPastThreshold = false;
        _dragMode = DragMode.Drag;

        // Start polling timer (matches ThumbnailWindow 16ms tick)
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    private void OnRightButtonUp()
    {
        if (_dragMode == DragMode.Drag || _dragMode == DragMode.Resize)
        {
            // AHK snaps on release, not during drag
            if (_dragMovedPastThreshold && SnapPosition != null)
            {
                var (sx, sy) = SnapPosition(Left, Top, Width, Height);
                Left = sx;
                Top = sy;
            }
            StopDrag();
            SavePosition();
        }
    }

    private void OnLeftButtonDown()
    {
        if (_dragMode == DragMode.Drag)
        {
            // Right is held, left just pressed → switch to resize mode
            _dragMode = DragMode.Resize;
            var screenPos = GetScreenMousePos();
            _dragStartScreen = screenPos;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
        }
    }

    private void OnLeftButtonUp()
    {
        if (_dragMode == DragMode.Resize)
        {
            StopDrag();
            SavePosition();
        }
    }

    private void DragTick(object? sender, EventArgs e)
    {
        var mouse = GetScreenMousePos();

        if (_dragMode == DragMode.Drag)
        {
            // Check if right button is still held
            if (!User32.IsKeyDown(User32.VK_RBUTTON))
            {
                OnRightButtonUp();
                return;
            }

            // Check if left button is now held → switch to resize
            if (User32.IsKeyDown(User32.VK_LBUTTON))
            {
                _dragMode = DragMode.Resize;
                _dragStartScreen = mouse;
                _resizeStartWidth = Width;
                _resizeStartHeight = Height;
                return;
            }

            double dx = mouse.X - _dragStartScreen.X;
            double dy = mouse.Y - _dragStartScreen.Y;

            if (!_dragMovedPastThreshold && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                _dragMovedPastThreshold = true;

            if (_dragMovedPastThreshold)
            {
                Left = _dragStartLeft + dx;
                Top = _dragStartTop + dy;
            }
        }
        else if (_dragMode == DragMode.Resize)
        {
            // End resize if either button released
            if (!User32.IsKeyDown(User32.VK_LBUTTON) || !User32.IsKeyDown(User32.VK_RBUTTON))
            {
                StopDrag();
                SavePosition();
                return;
            }

            double dx = mouse.X - _dragStartScreen.X;
            double dy = mouse.Y - _dragStartScreen.Y;

            Width = Math.Max(80, _resizeStartWidth + dx);
            Height = Math.Max(40, _resizeStartHeight + dy);

            // Ctrl+resize = individual only; no Ctrl = resize all stat windows
            if (!User32.IsKeyDown(User32.VK_LCONTROL))
                ResizeAll?.Invoke(this, (int)Width, (int)Height);
        }
    }

    private void StopDrag()
    {
        _dragMode = DragMode.None;
        if (_dragTimer != null)
        {
            _dragTimer.Stop();
            _dragTimer.Tick -= DragTick;
            _dragTimer = null;
        }
    }

    private void SavePosition()
    {
        PositionAndSizeChanged?.Invoke(this, (int)Left, (int)Top, (int)Width, (int)Height);
    }

    private static Point GetScreenMousePos()
    {
        var pt = System.Windows.Forms.Cursor.Position;
        return new Point(pt.X, pt.Y);
    }

    /// <summary>Bring stat overlay to front using Win32 SetWindowPos.
    /// WPF Topmost alone is insufficient against EVE's DirectX surface.
    /// Called once at EVE focus transitions, matching ThumbnailWindow pattern.</summary>
    public void BringToFront()
    {
        if (_ownHwnd == IntPtr.Zero) return;
        var zOrder = Topmost ? User32.HWND_TOPMOST : User32.HWND_TOP;
        User32.SetWindowPos(_ownHwnd, zOrder,
            0, 0, 0, 0,
            User32.SWP_NOACTIVATE | User32.SWP_NOMOVE | User32.SWP_NOSIZE);
    }

    private static string GetUnitLabel(string statType) => statType switch
    {
        "DPS" => "DPS",
        "Logi" => "HP/s",
        "Mining" => "m³/min",
        "Ratting" => "ISK/h",
        _ => ""
    };
}
