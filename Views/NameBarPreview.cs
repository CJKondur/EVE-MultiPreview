using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Views;

/// <summary>Where the name bar shows the live picture of the client under the pointer (IDEAS #38). A fake stands in for it in
/// tests; the window is <see cref="NameBarPreview"/>.</summary>
public interface IPreviewSurface : IDisposable
{
    /// <summary>Shows the live picture of the window <paramref name="source"/> in <paramref name="dest"/> (screen DIPs).</summary>
    void Show(IntPtr source, Rect dest);

    /// <summary>Takes the picture away.</summary>
    void Hide();
}

/// <summary>
/// IDEAS #38: the small window below the name bar that shows one client live while the pointer rests on its button. It draws
/// the picture with a DWM thumbnail registered for the hover only: registered by <c>Show</c>, unregistered by <c>Hide</c>, by
/// Dispose and when the window closes, so no handle outlives the hover. The window is OPAQUE and not layered: a DWM source on an
/// AllowsTransparency window can go black (see ThumbnailWindow's header). It never takes the focus (in the topmost band by SetWindowPos alone,
/// never by WPF's Topmost, raised when it shows and every 250 ms while shown) and it sends the client nothing: it only looks.
/// </summary>
public sealed class NameBarPreview : Window, IPreviewSurface
{
    /// <summary>The picture stays this far inside the window, so the frame the look draws shows around it (device pixels).</summary>
    private const int Inset = 2;

    private readonly DispatcherTimer _raise = new() { Interval = HotkeySheetWindow.RaiseEvery };
    private readonly Func<IntPtr, IntPtr, uint, bool> _setWindowPos;
    private IntPtr _thumb = IntPtr.Zero;
    private bool _closed;

    /// <summary>Whether a live picture is registered with DWM now (tests).</summary>
    public bool HasPicture => _thumb != IntPtr.Zero;

    /// <param name="setWindowPos">The z-order call (tests record it); default is user32's SetWindowPos.</param>
    public NameBarPreview(Func<IntPtr, IntPtr, uint, bool>? setWindowPos = null)
    {
        _setWindowPos = setWindowPos ?? ((h, after, flags) => User32.SetWindowPos(h, after, 0, 0, 0, 0, flags));
        UiStyles.Attach(this, StyledAs.Surface);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = false;                          // opaque: see the type's remarks
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");
        Content = new System.Windows.Controls.Border();      // the frame; the picture is drawn over the window by DWM
        var frame = (System.Windows.Controls.Border)Content;
        frame.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        frame.BorderThickness = new Thickness(1);
        BornTopmost.Apply(this);

        _raise.Tick += (_, _) => ReRaise();
        Closed += (_, _) => { _closed = true; _raise.Stop(); Unregister(); };
    }

    /// <summary>Puts the window back at the top of the topmost band without activating it.</summary>
    public void ReRaise()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) _setWindowPos(handle, User32.HWND_TOPMOST, HotkeySheetWindow.RaiseFlags);
    }

    void IPreviewSurface.Show(IntPtr source, Rect dest) => ShowPicture(source, dest);

    void IPreviewSurface.Hide() => HidePicture();

    /// <summary>Opens the window at <paramref name="dest"/> and registers the live picture of <paramref name="source"/> in it,
    /// fitted to the client's own shape. A picture DWM refuses leaves the empty frame. The window's thread.</summary>
    public void ShowPicture(IntPtr source, Rect dest)
    {
        if (_closed) return;
        Unregister();
        Left = dest.Left;
        Top = dest.Top;
        Width = dest.Width;
        Height = dest.Height;
        if (!IsVisible) base.Show();
        _raise.Start();
        ReRaise();
        var own = new WindowInteropHelper(this).Handle;
        if (own == IntPtr.Zero || source == IntPtr.Zero) return;
        _thumb = DwmApi.RegisterThumbnail(own, source);
        Fit();
    }

    /// <summary>Takes the picture away and hides the window: the DWM registration goes with it.</summary>
    public void HidePicture()
    {
        _raise.Stop();
        Unregister();
        if (!_closed && IsVisible) base.Hide();
    }

    /// <summary>Pushes the picture's place to DWM: the window's inside, the picture as large as fits without changing the
    /// client's shape, centred.</summary>
    private void Fit()
    {
        if (_thumb == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX)) - 2 * Inset;
        int height = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY)) - 2 * Inset;
        var (sw, sh) = DwmApi.QuerySourceSize(_thumb);
        int w = width, h = height;
        if (sw > 0 && sh > 0)
        {
            double scale = Math.Min((double)width / sw, (double)height / sh);
            w = Math.Max(1, (int)(sw * scale));
            h = Math.Max(1, (int)(sh * scale));
        }
        int x = Inset + (width - w) / 2, y = Inset + (height - h) / 2;
        var props = new DwmApi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmApi.DWM_TNP.RECTDESTINATION | DwmApi.DWM_TNP.VISIBLE | DwmApi.DWM_TNP.OPACITY | DwmApi.DWM_TNP.SOURCECLIENTAREAONLY,
            rcDestination = new DwmApi.RECT(x, y, x + w, y + h),
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };
        DwmApi.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    private void Unregister()
    {
        var id = _thumb;
        _thumb = IntPtr.Zero;
        DwmApi.UnregisterThumbnail(id);
    }

    /// <summary>Takes the picture away and closes the window, once.</summary>
    public void Dispose()
    {
        _raise.Stop();
        Unregister();
        if (_closed) return;
        _closed = true;
        Close();
    }
}
