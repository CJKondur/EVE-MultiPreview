using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Point = System.Windows.Point;

namespace EveMultiPreview.Views;

/// <summary>
/// Borderless, transparent popup that displays a cropped client-area sub-rect
/// of an EVE client via a DWM thumbnail (rcSource + SOURCECLIENTAREAONLY).
/// Drag with right mouse button to move, right+left to resize. Position and
/// size persist to the backing <see cref="CropDefinition"/>.
/// </summary>
public partial class CropWindow : Window
{
    private IntPtr _thumbId;
    private IntPtr _eveHwnd;
    private IntPtr _ownHwnd;

    // Companion topmost window that paints the label ON TOP of the DWM thumbnail
    // (DWM thumbnails occlude in-window WPF content).
    private TextOverlayWindow? _textOverlay;

    private enum DragMode { None, Drag, Resize }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private bool _dragMovedPastThreshold;

    private DispatcherTimer? _dragTimer;

    public CropDefinition Definition { get; private set; } = null!;
    public string CharacterName { get; private set; } = string.Empty;

    /// <summary>Fired after drag/resize finishes so the manager can persist new bounds.</summary>
    public event Action<CropWindow>? BoundsChanged;

    /// <summary>Optional snap resolver. Given current Left/Top/Width/Height returns the
    /// snapped Left/Top (or unchanged values if no snap applies). Supplied by CropManager.</summary>
    public Func<double, double, double, double, (double x, double y)>? SnapPosition;

    public CropWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void Initialize(IntPtr eveHwnd, string characterName, CropDefinition def)
    {
        _eveHwnd = eveHwnd;
        CharacterName = characterName;
        Definition = def;

        Left = def.PopupX;
        Top = def.PopupY;
        Width = Math.Max(40, def.PopupWidth);
        Height = Math.Max(30, def.PopupHeight);

        ApplyLabel();
    }

    public void ApplyLabel()
    {
        if (Definition == null) return;
        // Keep the in-XAML label hidden — DWM thumbnails occlude it. Use the
        // companion topmost overlay instead (created in OnLoaded).
        LabelHost.Visibility = Visibility.Collapsed;

        if (_textOverlay != null)
        {
            bool show = Definition.ShowLabel && !string.IsNullOrWhiteSpace(Definition.Name);
            _textOverlay.UpdateCharacterName(show ? Definition.Name : string.Empty);
            _textOverlay.SetTextOverlayVisible(show);
        }
    }

    /// <summary>Apply font family + size + color used for the name label (mirrors ThumbnailWindow.SetTextStyle).</summary>
    public void SetLabelStyle(string fontFamily, double fontSize, Color color)
    {
        Dispatcher.Invoke(() =>
        {
            _textOverlay?.SetTextStyle(
                string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
                fontSize > 0 ? fontSize : 11,
                color);
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        _ownHwnd = source.Handle;

        int exStyle = User32.GetWindowLong(_ownHwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLong(_ownHwnd, User32.GWL_EXSTYLE,
            exStyle | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);

        source.AddHook(WndProc);
        RegisterDwmThumbnail();
        CreateTextOverlay();
    }

    private void CreateTextOverlay()
    {
        _textOverlay = new TextOverlayWindow();
        _textOverlay.Left = Left;
        _textOverlay.Top = Top;
        _textOverlay.Width = Math.Max(40, Width);
        _textOverlay.Height = Math.Max(30, Height);
        _textOverlay.Topmost = Topmost;
        _textOverlay.Show();

        // Parent the overlay's HWND to the crop popup's HWND so z-order follows
        // and the overlay stays above DWM thumbnail content.
        var overlayHelper = new WindowInteropHelper(_textOverlay);
        User32.SetWindowLongPtr(overlayHelper.Handle, User32.GWLP_HWNDPARENT, _ownHwnd);

        // Auto-sync on any WPF-driven location / size change (ApplyDefinitionEdits,
        // snap landing, numeric-field edits). Drag/resize paths sync manually.
        LocationChanged += (_, _) => SyncTextOverlay();
        SizeChanged     += (_, _) => SyncTextOverlay();

        ApplyLabel();
    }

    private void SyncTextOverlay(double? overrideLeft = null, double? overrideTop = null,
                                 double? overrideWidth = null, double? overrideHeight = null)
    {
        if (_textOverlay == null) return;
        _textOverlay.SyncPosition(
            overrideLeft  ?? Left,
            overrideTop   ?? Top,
            overrideWidth ?? Width,
            overrideHeight?? Height);
    }

    // ── Win32 message hook (matches ThumbnailWindow pattern) ────────
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_RBUTTONDOWN: OnRightButtonDown(); handled = true; return IntPtr.Zero;
            case WM_RBUTTONUP:   OnRightButtonUp();   handled = true; return IntPtr.Zero;
            case WM_LBUTTONDOWN: OnLeftButtonDown();  handled = true; return IntPtr.Zero;
            case WM_LBUTTONUP:   OnLeftButtonUp();    handled = true; return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void OnRightButtonDown()
    {
        if (_dragMode != DragMode.None) return;
        _dragStartScreen = GetScreenMousePos();
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragMovedPastThreshold = false;
        _dragMode = DragMode.Drag;

        _dragTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    private void OnRightButtonUp()
    {
        if (_dragMode == DragMode.Drag || _dragMode == DragMode.Resize)
        {
            bool shouldSnap = _dragMode == DragMode.Drag && _dragMovedPastThreshold;
            StopDrag();
            if (shouldSnap && SnapPosition != null)
            {
                var (sx, sy) = SnapPosition(Left, Top, Width, Height);
                Left = sx;
                Top = sy;
                SyncTextOverlay();
            }
            SaveBounds();
        }
    }

    private void OnLeftButtonDown()
    {
        if (_dragMode == DragMode.Drag)
        {
            _dragMode = DragMode.Resize;
            _dragStartScreen = GetScreenMousePos();
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
        }
    }

    private void OnLeftButtonUp()
    {
        if (_dragMode == DragMode.Resize)
        {
            StopDrag();
            SaveBounds();
        }
    }

    private void DragTick(object? sender, EventArgs e)
    {
        var mouse = GetScreenMousePos();

        if (_dragMode == DragMode.Drag)
        {
            if (!User32.IsKeyDown(User32.VK_RBUTTON))
            {
                OnRightButtonUp();
                return;
            }
            if (User32.IsKeyDown(User32.VK_LBUTTON))
            {
                _dragMode = DragMode.Resize;
                _dragStartScreen = mouse;
                _resizeStartWidth = Width;
                _resizeStartHeight = Height;
                return;
            }

            // Mouse delta is physical pixels; _dragStartLeft/Top are DIPs.
            double dx = mouse.X - _dragStartScreen.X;
            double dy = mouse.Y - _dragStartScreen.Y;
            double scale = DpiHelper.GetScaleFactor(this);
            double dipDx = DpiHelper.PhysicalToDip(dx, scale);
            double dipDy = DpiHelper.PhysicalToDip(dy, scale);

            if (!_dragMovedPastThreshold && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                _dragMovedPastThreshold = true;

            if (_dragMovedPastThreshold)
            {
                double newLeftDip = _dragStartLeft + dipDx;
                double newTopDip = _dragStartTop + dipDy;
                int newPhysX = DpiHelper.DipToPhysical(newLeftDip, scale);
                int newPhysY = DpiHelper.DipToPhysical(newTopDip, scale);
                if (_ownHwnd != IntPtr.Zero)
                    User32.SetWindowPos(_ownHwnd, IntPtr.Zero, newPhysX, newPhysY, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                SyncTextOverlay(overrideLeft: newLeftDip, overrideTop: newTopDip);
            }
        }
        else if (_dragMode == DragMode.Resize)
        {
            if (!User32.IsKeyDown(User32.VK_LBUTTON) || !User32.IsKeyDown(User32.VK_RBUTTON))
            {
                StopDrag();
                SaveBounds();
                return;
            }

            double dx = mouse.X - _dragStartScreen.X;
            double dy = mouse.Y - _dragStartScreen.Y;
            double scale = DpiHelper.GetScaleFactor(this);
            double dipDx = DpiHelper.PhysicalToDip(dx, scale);
            double dipDy = DpiHelper.PhysicalToDip(dy, scale);

            double newW = Math.Max(_resizeStartWidth + dipDx, 40);
            double newH = Math.Max(_resizeStartHeight + dipDy, 30);

            Width = newW;
            Height = newH;
            UpdateThumbnailDestination();
            SyncTextOverlay();
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

        if (_ownHwnd != IntPtr.Zero)
        {
            User32.GetWindowRect(_ownHwnd, out var rect);
            Left = rect.Left;
            Top = rect.Top;
        }
        SyncTextOverlay();
    }

    private void SaveBounds()
    {
        if (Definition == null) return;
        Definition.PopupX = (int)Left;
        Definition.PopupY = (int)Top;
        Definition.PopupWidth = (int)Width;
        Definition.PopupHeight = (int)Height;
        BoundsChanged?.Invoke(this);
    }

    // ── DWM thumbnail management ────────────────────────────────────

    /// <summary>
    /// Register the DWM thumbnail linking this popup HWND to the EVE client
    /// and push the initial properties (dest fill + source crop).
    /// </summary>
    private void RegisterDwmThumbnail()
    {
        if (_eveHwnd == IntPtr.Zero || _ownHwnd == IntPtr.Zero) return;
        if (_thumbId != IntPtr.Zero) return;

        _thumbId = DwmApi.RegisterThumbnail(_ownHwnd, _eveHwnd);
        if (_thumbId == IntPtr.Zero)
        {
            DiagnosticsService.LogDwm($"[Crop:DWM] RegisterThumbnail returned 0 for '{CharacterName}' src=0x{_eveHwnd.ToInt64():X}");
            return;
        }
        UpdateThumbnailDestination();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbnailDestination();

    /// <summary>
    /// Push rcDestination (full popup) and rcSource (the crop sub-rect from
    /// the CropDefinition) to DWM. SOURCECLIENTAREAONLY makes rcSource
    /// coordinates client-relative, matching how CropDefinition stores them.
    /// </summary>
    public void UpdateThumbnailDestination()
    {
        if (_thumbId == IntPtr.Zero || Definition == null) return;

        int w = Math.Max(1, (int)Width);
        int h = Math.Max(1, (int)Height);

        int sx = Math.Max(0, Definition.SourceX);
        int sy = Math.Max(0, Definition.SourceY);
        int sw = Math.Max(1, Definition.SourceWidth);
        int sh = Math.Max(1, Definition.SourceHeight);

        var props = new DwmApi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmApi.DWM_TNP.RECTDESTINATION | DwmApi.DWM_TNP.RECTSOURCE |
                      DwmApi.DWM_TNP.OPACITY | DwmApi.DWM_TNP.VISIBLE |
                      DwmApi.DWM_TNP.SOURCECLIENTAREAONLY,
            rcDestination = new DwmApi.RECT(0, 0, w, h),
            rcSource = new DwmApi.RECT(sx, sy, sx + sw, sy + sh),
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };

        int hr = DwmApi.DwmUpdateThumbnailProperties(_thumbId, ref props);
        if (hr != 0)
            DiagnosticsService.LogDwm($"[Crop:DWM] UpdateThumbnailProperties hr=0x{hr:X8} (W:{w} H:{h} src={sx},{sy} {sw}x{sh})");
    }

    /// <summary>Call-site compatibility shim kept for older callers.</summary>
    public void UpdateCaptureViewbox() => UpdateThumbnailDestination();

    /// <summary>Mirror the main window's Topmost flag onto the companion label overlay.</summary>
    public void SetTopmost(bool topmost)
    {
        Topmost = topmost;
        if (_textOverlay != null) _textOverlay.Topmost = topmost;
    }

    /// <summary>Rebind to a new EVE HWND (e.g. after the client was relaunched).</summary>
    public void Rebind(IntPtr eveHwnd)
    {
        if (_eveHwnd == eveHwnd) return;
        _eveHwnd = eveHwnd;

        if (_thumbId != IntPtr.Zero)
        {
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
        }
        RegisterDwmThumbnail();
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    private bool _cleanedUp;
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Cleanup();

    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        StopDrag();

        if (_thumbId != IntPtr.Zero)
        {
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
        }

        try { _textOverlay?.Close(); } catch { }
        _textOverlay = null;
    }

    private static Point GetScreenMousePos()
    {
        User32.GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }
}
