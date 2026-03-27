using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;

using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace EveMultiPreview.Views;

/// <summary>
/// A borderless, transparent WPF window that hosts a live DWM thumbnail
/// of an EVE Online client. Matches AHK ThumbWindow.ahk behavior:
///   - Right-click drag to move
///   - Right-click + Left-click drag to resize
///   - Ctrl+Right-click drag to move ALL thumbnails
///   - Click (left or right release without drag) to switch window
///   - Ctrl+click to minimize
///   - Text overlay with character name, system name, session timer
///   - Border overlay with active/inactive/custom/group colors
/// </summary>
public partial class ThumbnailWindow : Window
{
    private IntPtr _thumbId = IntPtr.Zero;
    private IntPtr _eveHwnd;
    private IntPtr _ownHwnd;

    // Base dimensions (before hover zoom)
    private int _baseWidth;
    private int _baseHeight;

    // Drag/resize state machine (matches AHK _DragState pattern)
    private enum DragMode { None, Drag, Resize }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private bool _dragMovedPastThreshold;
    private bool _isDragging; // Suppresses expensive SetWindowPos during drag
    public bool IsDragging => _isDragging;

    // Hover zoom
    private double _hoverScale = 1.0;
    private bool _isHovered;

    // Timer for polling drag state (matches AHK 16ms tick)
    private DispatcherTimer? _dragTimer;

    // Session tracking
    private DateTime _sessionStart = DateTime.Now;

    // Separate overlay window (DWM thumbnails occlude WPF content)
    private TextOverlayWindow? _textOverlay;

    // Opacity-based visibility (used instead of Hide/Show to preserve owned text overlay)
    private byte _savedOpacity = 255;
    private bool _isDwmHidden = false;

    // Border inset — DWM thumbnail is inset by this many pixels so window background shows as border
    private int _borderThickness = 0;

    // M1: Overlay position cache — skip SetWindowPos when nothing changed
    private (double Left, double Top, double Width, double Height) _lastSyncPos;

    // M2: Reusable brush for under-fire pulse (avoids GC pressure from 50ms allocation)
    private SolidColorBrush? _underFireBrush;

    // Public properties
    public IntPtr EveHwnd => _eveHwnd;
    public string CharacterName { get; private set; } = string.Empty;
    public bool IsLocked { get; set; }

    // Events
    /// <summary>Fires when drag finishes, with final position and size.</summary>
    public event Action<ThumbnailWindow, int, int, int, int>? PositionChanged;

    /// <summary>Fires when user clicks to switch EVE window.</summary>
    public event Action<ThumbnailWindow>? SwitchRequested;

    /// <summary>Fires when user Ctrl+clicks to minimize.</summary>
    public event Action<ThumbnailWindow>? MinimizeRequested;

    /// <summary>Fires during drag so manager can move all thumbnails.</summary>
    public event Action<ThumbnailWindow, double, double>? DragMoveAll;

    /// <summary>Fires during resize so manager can resize all thumbnails.</summary>
    public event Action<ThumbnailWindow, int, int>? ResizeAll;



    public ThumbnailWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// Initialize the thumbnail for a specific EVE window.
    /// Must be called before Show().
    /// </summary>
    public void Initialize(IntPtr eveHwnd, string characterName, int width, int height, int x, int y)
    {
        _eveHwnd = eveHwnd;
        CharacterName = characterName;
        _baseWidth = width;
        _baseHeight = height;
        Width = width;
        Height = height;
        Left = x;
        Top = y;
        _sessionStart = DateTime.Now;

        // Create overlay immediately (so ApplySettings can set text properties)
        // but defer Show() to OnLoaded
        _textOverlay = new TextOverlayWindow();
        _textOverlay.Width = width;
        _textOverlay.Height = height;
        _textOverlay.Left = x;
        _textOverlay.Top = y;
        if (!string.IsNullOrEmpty(characterName))
            _textOverlay.UpdateCharacterName(characterName);
    }

    /// <summary>Configure hover-zoom scale. Set to 1.0 to disable.</summary>
    public void SetHoverScale(double scale) => _hoverScale = scale;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        _ownHwnd = source.Handle;

        // Make window non-activating + layered (matches AHK +E0x08000000)
        int exStyle = User32.GetWindowLong(_ownHwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(_ownHwnd, User32.GWL_EXSTYLE,
            exStyle | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | User32.WS_EX_LAYERED);

        Debug.WriteLine($"[Thumb:Load] \u2705 Window loaded: hwnd=0x{_ownHwnd:X}, char='{CharacterName}'");

        // Hook mouse messages at the Win32 level (WPF doesn't handle right-click drag well)
        source.AddHook(WndProc);

        RegisterThumbnail();

        // Use Win32 GWLP_HWNDPARENT to keep overlay above thumbnail.
        // WPF native Owner forces AllowsTransparency="True" windows to act Topmost
        // when their owner is obscured by non-WPF applications like browsers.
        if (_textOverlay != null)
        {
            // Force synchronous HWND creation so we don't pass IntPtr.Zero
            var helper = new WindowInteropHelper(_textOverlay);
            helper.EnsureHandle(); 
            User32.SetWindowLongPtr(helper.Handle, User32.GWLP_HWNDPARENT, _ownHwnd);
            _textOverlay.Show();
        }
    }

    // ── Win32 Message Hook (right-click drag/resize) ────────────────

    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;


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
                OnLeftButtonDown(wParam);
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnLeftButtonUp(wParam);
                handled = true;
                return IntPtr.Zero;


        }

        return IntPtr.Zero;
    }

    private void OnRightButtonDown()
    {
        if (IsLocked)
        {
            // Show tooltip feedback
            Debug.WriteLine("[Thumb:Lock] 🔒 Drag attempt blocked — positions locked");
            return;
        }
        if (_dragMode != DragMode.None) return;

        // Get screen-space mouse position
        var screenPos = GetScreenMousePos();
        _dragStartScreen = screenPos;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragMovedPastThreshold = false;
        _dragMode = DragMode.Drag;
        _isDragging = true;

        // Start polling timer (matches AHK ~60fps non-blocking pattern)
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    private void OnRightButtonUp()
    {
        if (_dragMode == DragMode.Drag)
        {
            // If we didn't move past threshold, treat as click-to-switch
            if (!_dragMovedPastThreshold)
            {
                StopDrag();
                SwitchToEveClient();
                return;
            }

            StopDrag();
            SavePosition();
        }
        else if (_dragMode == DragMode.Resize)
        {
            StopDrag();
            SavePosition();
        }
    }

    private void OnLeftButtonDown(IntPtr wParam)
    {
        if (_dragMode == DragMode.Drag)
        {
            // Right is held, left just pressed → switch to resize mode
            _dragMode = DragMode.Resize;
            var screenPos = GetScreenMousePos();
            _dragStartScreen = screenPos;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            return;
        }

        // Simple left click (not dragging) — handled on mouse up
    }

    private void OnLeftButtonUp(IntPtr wParam)
    {
        if (_dragMode == DragMode.Resize)
        {
            // End resize
            StopDrag();
            SavePosition();
            return;
        }

        if (_dragMode == DragMode.None)
        {
            // Simple left click
            if (User32.IsKeyDown(User32.VK_LCONTROL))
            {
                MinimizeRequested?.Invoke(this);
            }
            else
            {
                SwitchToEveClient();
            }
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
                int newX = (int)(_dragStartLeft + dx);
                int newY = (int)(_dragStartTop + dy);

                // Direct Win32 move — bypasses WPF layout engine entirely (matches AHK WinMove)
                if (_ownHwnd != IntPtr.Zero)
                    User32.SetWindowPos(_ownHwnd, IntPtr.Zero, newX, newY, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

                // Move text overlay in lockstep
                var overlayHwnd = _textOverlay?.GetHwnd() ?? IntPtr.Zero;
                if (overlayHwnd != IntPtr.Zero)
                    User32.SetWindowPos(overlayHwnd, IntPtr.Zero, newX, newY, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

                // Ctrl+drag → move all thumbnails
                if (User32.IsKeyDown(User32.VK_LCONTROL))
                {
                    DragMoveAll?.Invoke(this, dx, dy);
                }
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

            int newW = Math.Max((int)(_resizeStartWidth + dx), 80);
            int newH = Math.Max((int)(_resizeStartHeight + dy), 50);

            Width = newW;
            Height = newH;
            _baseWidth = newW;
            _baseHeight = newH;
            UpdateThumbnailSize();
            _textOverlay?.SyncPosition(Left, Top, Width, Height);

            // Ctrl+resize toggles individual/uniform behavior
            bool resizeAll = !User32.IsKeyDown(User32.VK_LCONTROL);
            if (resizeAll)
                ResizeAll?.Invoke(this, newW, newH);
        }
    }

    private void StopDrag()
    {
        _dragMode = DragMode.None;
        _isDragging = false;
        if (_dragTimer != null)
        {
            _dragTimer.Stop();
            _dragTimer.Tick -= DragTick;
            _dragTimer = null;
        }

        // Sync WPF Left/Top from actual Win32 position (drag used SetWindowPos, not WPF properties)
        if (_ownHwnd != IntPtr.Zero)
        {
            User32.GetWindowRect(_ownHwnd, out var rect);
            Left = rect.Left;
            Top = rect.Top;
            _textOverlay?.SyncPosition(Left, Top, Width, Height);
        }
    }

    private void SavePosition()
    {
        PositionChanged?.Invoke(this, (int)Left, (int)Top, (int)Width, (int)Height);
    }

    private void SwitchToEveClient()
    {
        if (_eveHwnd == IntPtr.Zero) return;
        SwitchRequested?.Invoke(this);
    }

    // ── Hover Zoom ──────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_hoverScale <= 1.0 || _dragMode != DragMode.None) return;
        _isHovered = true;

        double newWidth = _baseWidth * _hoverScale;
        double newHeight = _baseHeight * _hoverScale;
        double offsetX = (newWidth - _baseWidth) / 2;
        double offsetY = (newHeight - _baseHeight) / 2;

        Width = newWidth;
        Height = newHeight;
        Left -= offsetX;
        Top -= offsetY;
        UpdateThumbnailSize();
        _textOverlay?.SyncPosition(Left, Top, Width, Height);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isHovered) return;
        _isHovered = false;

        double offsetX = (Width - _baseWidth) / 2;
        double offsetY = (Height - _baseHeight) / 2;

        Width = _baseWidth;
        Height = _baseHeight;
        Left += offsetX;
        Top += offsetY;
        UpdateThumbnailSize();
        _textOverlay?.SyncPosition(Left, Top, Width, Height);
    }

    // ── DWM Thumbnail Management ────────────────────────────────────

    private void RegisterThumbnail()
    {
        if (_eveHwnd == IntPtr.Zero || _ownHwnd == IntPtr.Zero) return;
        DwmApi.UnregisterThumbnail(_thumbId);

        _thumbId = DwmApi.RegisterThumbnail(_ownHwnd, _eveHwnd);
        if (_thumbId != IntPtr.Zero)
            UpdateThumbnailSize();
    }

    public void UpdateThumbnailSize()
    {
        if (_thumbId == IntPtr.Zero) return;
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        int b = _borderThickness;
        // Inset the DWM thumbnail by border thickness — window background shows as border frame
        DwmApi.UpdateThumbnailInset(_thumbId, w, h, b);
    }

    // ── Overlay Updates ─────────────────────────────────────────────

    public void UpdateCharacterName(string name)
    {
        CharacterName = name;
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.UpdateCharacterName(name);
        });
    }

    public void UpdateAnnotation(string? text)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.UpdateAnnotation(text);
        });
    }

    public void UpdateSystemName(string? systemName)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.UpdateSystemName(systemName);
        });
    }

    public void UpdateSessionTimer(TimeSpan elapsed)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.UpdateSessionTimer(elapsed);
        });
    }

    public void UpdateProcessStats(string? text)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.UpdateProcessStats(text);
        });
    }

    public void SetProcessStatsTextSize(double fontSize)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.SetProcessStatsTextSize(fontSize);
        });
    }

    /// <summary>Get the process ID for the EVE window this thumbnail is tracking.</summary>
    public int GetProcessId()
    {
        User32.GetWindowThreadProcessId(_eveHwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Set Not-Logged-In indicator. Supports 4 modes: text, dim, border, color.</summary>
    public void SetNotLoggedIn(bool isCharSelect, string mode, Color? borderColor = null, int dim = 80)
    {
        Dispatcher.Invoke(() =>
        {
            if (isCharSelect && mode != "none")
            {
                switch (mode.ToLowerInvariant())
                {
                    case "text":
                        NotLoggedInOverlay.Visibility = Visibility.Visible;
                        break;
                    case "dim":
                        Opacity = dim / 100.0;
                        NotLoggedInOverlay.Visibility = Visibility.Collapsed;
                        break;
                    case "border":
                        if (borderColor.HasValue)
                            SetBorder(borderColor.Value, 3);
                        NotLoggedInOverlay.Visibility = Visibility.Collapsed;
                        break;
                    case "color":
                        if (borderColor.HasValue)
                            SetBorder(borderColor.Value, 3);
                        NotLoggedInOverlay.Visibility = Visibility.Visible;
                        break;
                    default:
                        NotLoggedInOverlay.Visibility = Visibility.Visible;
                        break;
                }
                Debug.WriteLine($"[Thumb:NotLoggedIn] 🔧 '{CharacterName}' mode={mode}");
            }
            else
            {
                NotLoggedInOverlay.Visibility = Visibility.Collapsed;
                Opacity = 1.0; // Reset dim
            }
        });
    }

    public void SetBorder(Color color, int thickness)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Window background IS the border — DWM thumbnail is inset to reveal it
            BackgroundPanel.Background = thickness > 0
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Color.FromRgb(0x57, 0x50, 0x4E)); // default bg

            // Only re-inset DWM rect when thickness actually changes (expensive DWM API call)
            if (_borderThickness != thickness)
            {
                _borderThickness = thickness;
                UpdateThumbnailSize();
            }
        });
    }

    // ── Under Fire Indicator ────────────────────────────────────────
    private bool _isUnderFire;
    private DispatcherTimer? _underFirePulseTimer;
    private double _underFirePhase = 0;

    /// <summary>
    /// Activate/deactivate the "under fire" pulsing red border overlay.
    /// When active, BorderOverlay pulses between red and transparent.
    /// </summary>
    public void SetUnderFire(bool active)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (active && !_isUnderFire)
            {
                _isUnderFire = true;
                _underFirePhase = 0;
                BorderOverlay.BorderThickness = new Thickness(3);

                if (_underFirePulseTimer == null)
                {
                    _underFirePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    _underFireBrush = new SolidColorBrush(Color.FromArgb(255, 255, 40, 40));
                    _underFirePulseTimer.Tick += (_, _) =>
                    {
                        _underFirePhase += 0.15;
                        // Sine wave pulse: alpha oscillates 80-255
                        byte alpha = (byte)(168 + 87 * Math.Sin(_underFirePhase));
                        _underFireBrush.Color = Color.FromArgb(alpha, 255, 40, 40);
                        BorderOverlay.BorderBrush = _underFireBrush;
                    };
                }
                _underFirePulseTimer.Start();
            }
            else if (!active && _isUnderFire)
            {
                _isUnderFire = false;
                _underFirePulseTimer?.Stop();
                BorderOverlay.BorderBrush = System.Windows.Media.Brushes.Transparent;
                BorderOverlay.BorderThickness = new Thickness(0);
            }
        });
    }

    public void SetBackgroundColor(Color color)
    {
        Dispatcher.Invoke(() =>
        {
            BackgroundPanel.Background = new SolidColorBrush(color);
        });
    }

    public void SetOpacity(byte opacity)
    {
        _savedOpacity = opacity;
        // Use window-level transparency (matches AHK WinSetTransparent)
        // NOT DWM thumbnail opacity, which causes hazy/foggy appearance
        if (_ownHwnd != IntPtr.Zero && !_isDwmHidden)
            User32.SetLayeredWindowAttributes(_ownHwnd, 0, opacity, User32.LWA_ALPHA);
    }

    public void SetTextOverlayVisible(bool visible)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.SetTextOverlayVisible(visible);
        });
    }

    public void SetTextStyle(string fontFamily, double fontSize, Color color)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.SetTextStyle(fontFamily, fontSize, color);
        });
    }

    public void SetTextMargins(int marginX, int marginY)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.SetTextMargins(marginX, marginY);
        });
    }

    // ── Position/Size helpers ───────────────────────────────────────

    /// <summary>Resize the thumbnail to new dimensions, updating base size.</summary>
    public void Resize(int width, int height)
    {
        _baseWidth = width;
        _baseHeight = height;
        if (!_isHovered)
        {
            Width = width;
            Height = height;
            UpdateThumbnailSize();
            _textOverlay?.SyncPosition(Left, Top, Width, Height);
        }
    }

    /// <summary>Move window to specific position.</summary>
    public void MoveTo(int x, int y)
    {
        Left = x;
        Top = y;
        _textOverlay?.SyncPosition(Left, Top, Width, Height);
    }

    /// <summary>Get the current session duration.</summary>
    public TimeSpan SessionDuration => DateTime.Now - _sessionStart;

    // ── Visibility (syncs overlay + main window) ────────────────────

    // (ShowWithOverlay is defined below alongside HideWithOverlay)

    /// <summary>Hide ONLY the DWM thumbnail using opacity 0.
    /// Text overlay stays visible because we use opacity (not Hide)
    /// and the Win32 owner relationship keeps it above. Matches AHK HideActiveThumbnail.</summary>
    public void HideDwmOnly()
    {
        if (_isDwmHidden) return;
        _isDwmHidden = true;
        if (_ownHwnd != IntPtr.Zero)
            User32.SetLayeredWindowAttributes(_ownHwnd, 0, 0, User32.LWA_ALPHA);
    }

    /// <summary>Restore DWM thumbnail visibility (reverse of HideDwmOnly).</summary>
    public void ShowDwmOnly()
    {
        if (!_isDwmHidden) return;
        _isDwmHidden = false;
        if (_ownHwnd != IntPtr.Zero)
            User32.SetLayeredWindowAttributes(_ownHwnd, 0, _savedOpacity, User32.LWA_ALPHA);
    }

    /// <summary>Sync text overlay position AND z-order with thumbnail.
    /// Called every 50ms from UpdateActiveBorders to keep overlay above thumbnail.
    /// WPF AllowsTransparency windows don't respect Win32 ownership for z-order,
    /// so we enforce it continuously.</summary>
    public void SyncOverlayPosition(bool eveFocused = true)
    {
        if (_textOverlay == null) return;

        // M1: Skip SetWindowPos if position hasn't changed
        var current = (Left, Top, Width, Height);
        if (current == _lastSyncPos) return;
        _lastSyncPos = current;

        var overlayHwnd = _textOverlay.GetHwnd();
        if (overlayHwnd == IntPtr.Zero) return;

        // Position-only sync — z-order is asserted one-time by BringToFront()
        // at transition points (EVE gains focus, thumbnail switch), not every tick.
        // This prevents the overlay from constantly fighting browser z-order.
        User32.SetWindowPos(overlayHwnd, IntPtr.Zero,
            (int)Left, (int)Top, (int)Width, (int)Height,
            User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
    }

    /// <summary>Bring both thumbnail and overlay to the front of the z-order.
    /// Called once at transition points (EVE gains focus, thumbnail switch),
    /// NOT on every tick. This allows browsers to cover overlays by being clicked later.</summary>
    public void BringToFront()
    {
        // Bring thumbnail to front
        var thumbHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (thumbHwnd != IntPtr.Zero)
        {
            var zOrder = Topmost ? User32.HWND_TOPMOST : User32.HWND_TOP;
            User32.SetWindowPos(thumbHwnd, zOrder,
                0, 0, 0, 0,
                User32.SWP_NOACTIVATE | User32.SWP_NOMOVE | User32.SWP_NOSIZE);
        }

        // Bring overlay above thumbnail
        if (_textOverlay != null)
        {
            var overlayHwnd = _textOverlay.GetHwnd();
            if (overlayHwnd != IntPtr.Zero)
            {
                var zOrder = Topmost ? User32.HWND_TOPMOST : User32.HWND_TOP;
                User32.SetWindowPos(overlayHwnd, zOrder,
                    (int)Left, (int)Top, (int)Width, (int)Height,
                    User32.SWP_NOACTIVATE);
            }
        }
    }

    /// <summary>Ensure text overlay is visible and positioned correctly.
    /// Re-asserts z-order after SetForegroundWindow may have buried the overlay.</summary>
    public void EnsureOverlayZOrder()
    {
        if (_textOverlay == null) return;
        if (!_textOverlay.IsVisible)
            _textOverlay.Show();

        var overlayHwnd = _textOverlay.GetHwnd();
        if (overlayHwnd != IntPtr.Zero)
        {
            // Re-assert overlay Topmost to match thumbnail (eve-o-preview pattern)
            var zOrder = Topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
            User32.SetWindowPos(overlayHwnd, zOrder,
                (int)Left, (int)Top, (int)Width, (int)Height,
                User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        }
    }

    /// <summary>Set the topmost state for this thumbnail and its text overlay.
    /// Matches eve-o-preview pattern: explicitly set TopMost on both windows.</summary>
    public void SetTopmost(bool topmost)
    {
        Topmost = topmost;
        if (_textOverlay != null)
            _textOverlay.Topmost = topmost;
    }

    /// <summary>Hide thumbnail AND text overlay. Used for full visibility toggle.</summary>
    public void HideWithOverlay()
    {
        Hide();
        _textOverlay?.Hide();
    }

    /// <summary>Show thumbnail AND text overlay. Used for full visibility toggle.</summary>
    public void ShowWithOverlay()
    {
        Show();
        _textOverlay?.Show();
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Cleanup(); // Delegate to Cleanup — guard prevents double-execution
    }

    private bool _cleanedUp;

    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        StopDrag();
        DwmApi.UnregisterThumbnail(_thumbId);
        _thumbId = IntPtr.Zero;
        _textOverlay?.Close();
        _textOverlay = null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static Point GetScreenMousePos()
    {
        System.Drawing.Point pt = System.Windows.Forms.Cursor.Position;
        return new Point(pt.X, pt.Y);
    }
}
