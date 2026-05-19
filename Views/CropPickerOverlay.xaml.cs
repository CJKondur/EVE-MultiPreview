using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using EveMultiPreview.Interop;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace EveMultiPreview.Views;

/// <summary>
/// Transparent overlay positioned over a ThumbnailWindow to let the user
/// click-and-drag a rubberband rectangle. The selection is reported back
/// in source-client coordinates (i.e. pixels on the EVE window itself).
/// </summary>
public partial class CropPickerOverlay : Window
{
    private readonly double _clientWidth;
    private readonly double _clientHeight;
    private readonly int _physX;
    private readonly int _physY;
    private readonly int _physW;
    private readonly int _physH;
    private bool _dragging;
    private Point _dragStart;

    /// <summary>Fires with the selection in EVE client coordinates on successful drag. Null on cancel.</summary>
    public event Action<(int X, int Y, int W, int H)?>? Completed;

    public CropPickerOverlay(int screenX, int screenY,
                              int screenWidth, int screenHeight,
                              int clientWidth, int clientHeight)
    {
        InitializeComponent();

        _clientWidth = Math.Max(1, clientWidth);
        _clientHeight = Math.Max(1, clientHeight);

        // The caller passes the EVE client area in PHYSICAL screen pixels. WPF's
        // Left/Top/Width/Height are DIPs, so assigning physical values to them
        // mis-places and (on a scaled display) over-sizes the overlay. Instead we
        // position the HWND directly with SetWindowPos in OnLoaded — that API is
        // DPI-immune. Once the window is sized in physical pixels, the existing
        // _clientWidth / ActualWidth scale math resolves the selection correctly.
        _physX = screenX;
        _physY = screenY;
        _physW = Math.Max(40, screenWidth);
        _physH = Math.Max(30, screenHeight);

        OverlayCanvas.MouseLeftButtonDown += OnMouseDown;
        OverlayCanvas.MouseMove += OnMouseMove;
        OverlayCanvas.MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Place AND size the overlay in physical pixels so it exactly covers the
        // EVE client area regardless of DPI scaling, then pin it topmost. Doing
        // this before the window is rendered avoids a one-frame flash at the wrong
        // location. WPF Topmost alone loses to foreground-app juggling, hence the
        // explicit HWND_TOPMOST.
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            User32.SetWindowPos(helper.Handle, User32.HWND_TOPMOST,
                _physX, _physY, _physW, _physH,
                User32.SWP_SHOWWINDOW);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelAndClose();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(OverlayCanvas);
        Canvas.SetLeft(Rubberband, _dragStart.X);
        Canvas.SetTop(Rubberband, _dragStart.Y);
        Rubberband.Width = 0;
        Rubberband.Height = 0;
        Rubberband.Visibility = Visibility.Visible;
        OverlayCanvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(OverlayCanvas);

        double x = Math.Min(p.X, _dragStart.X);
        double y = Math.Min(p.Y, _dragStart.Y);
        double w = Math.Abs(p.X - _dragStart.X);
        double h = Math.Abs(p.Y - _dragStart.Y);

        Canvas.SetLeft(Rubberband, x);
        Canvas.SetTop(Rubberband, y);
        Rubberband.Width = w;
        Rubberband.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        OverlayCanvas.ReleaseMouseCapture();

        double rx = Canvas.GetLeft(Rubberband);
        double ry = Canvas.GetTop(Rubberband);
        double rw = Rubberband.Width;
        double rh = Rubberband.Height;

        if (rw < 4 || rh < 4)
        {
            // Treat as click — cancel without selection
            CancelAndClose();
            return;
        }

        double scaleX = _clientWidth / OverlayCanvas.ActualWidth;
        double scaleY = _clientHeight / OverlayCanvas.ActualHeight;

        int sx = (int)Math.Round(rx * scaleX);
        int sy = (int)Math.Round(ry * scaleY);
        int sw = (int)Math.Round(rw * scaleX);
        int sh = (int)Math.Round(rh * scaleY);

        // Clamp to source bounds
        sx = Math.Max(0, Math.Min(sx, (int)_clientWidth - 1));
        sy = Math.Max(0, Math.Min(sy, (int)_clientHeight - 1));
        sw = Math.Max(1, Math.Min(sw, (int)_clientWidth - sx));
        sh = Math.Max(1, Math.Min(sh, (int)_clientHeight - sy));

        Completed?.Invoke((sx, sy, sw, sh));
        Close();
    }

    private void CancelAndClose()
    {
        Completed?.Invoke(null);
        Close();
    }

    /// <summary>Helper: query the client size of an EVE HWND in pixels.</summary>
    public static (int W, int H) QueryClientSize(IntPtr eveHwnd)
    {
        if (eveHwnd == IntPtr.Zero) return (0, 0);
        if (!User32.GetClientRect(eveHwnd, out var rc)) return (0, 0);
        return (rc.Width, rc.Height);
    }

    /// <summary>Query the EVE client area in screen coordinates (excludes title bar / borders).</summary>
    public static (int X, int Y, int W, int H) QueryClientScreenRect(IntPtr eveHwnd)
    {
        if (eveHwnd == IntPtr.Zero) return (0, 0, 0, 0);
        if (!User32.GetClientRect(eveHwnd, out var rc)) return (0, 0, 0, 0);
        var pt = new User32.POINT { X = 0, Y = 0 };
        if (!User32.ClientToScreen(eveHwnd, ref pt)) return (0, 0, 0, 0);
        return (pt.X, pt.Y, rc.Width, rc.Height);
    }
}
