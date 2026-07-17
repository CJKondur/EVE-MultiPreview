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

    // One-time guard for the post-creation forced rebind (issue #80). A crop
    // registered during the app-startup discovery burst can bind to a source
    // EVE window before DWM has it composited, painting nothing.
    private bool _initialRebindDone;

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

        // Drop the implicit OWNER that WPF gives this window because of
        // ShowInTaskbar="False" — WPF keeps such a window off the taskbar by parenting
        // it to a hidden dummy owner. An OWNED window's z-order is slaved to its owner,
        // so it cannot hold the topmost band on its own: SetWindowPos(HWND_TOPMOST)
        // appeared to succeed but WS_EX_TOPMOST never stuck, and the crop sat behind the
        // EVE client forever (#80/#87 — a user's [Crop:Stuck] log showed every stuck crop
        // with topmost=False, child=False and a distinct owner= we never set).
        // WS_EX_TOOLWINDOW above already keeps it out of the taskbar AND alt-tab, so the
        // dummy owner buys nothing. Unowned, the crop can be genuinely topmost.
        User32.SetWindowLongPtr(_ownHwnd, User32.GWLP_HWNDPARENT, IntPtr.Zero);

        source.AddHook(WndProc);
        RegisterDwmThumbnail();
        CreateTextOverlay();
        ApplyClickThrough();
    }

    private void CreateTextOverlay()
    {
        _textOverlay = new TextOverlayWindow();
        _textOverlay.Left = Left;
        _textOverlay.Top = Top;
        _textOverlay.Width = Math.Max(40, Width);
        _textOverlay.Height = Math.Max(30, Height);
        _textOverlay.Topmost = _isTopmost;   // our tracked band, not WPF's cached view
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
            // GetWindowRect returns PHYSICAL pixels; WPF Left/Top are DIPs. On a
            // scaled display, assigning physical values directly makes the window
            // jump on release. Convert physical → DIP before syncing back.
            User32.GetWindowRect(_ownHwnd, out var rect);
            double scale = DpiHelper.GetScaleFactor(this);
            Left = DpiHelper.PhysicalToDip(rect.Left, scale);
            Top = DpiHelper.PhysicalToDip(rect.Top, scale);
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
    public void UpdateThumbnailDestination() => PushThumbnailProperties();

    /// <summary>Push rcDestination + rcSource + opacity to DWM. Returns the
    /// HRESULT so callers (the health check) can detect a stale registration.</summary>
    private int PushThumbnailProperties()
    {
        if (_thumbId == IntPtr.Zero || Definition == null) return 0;

        int w = Math.Max(1, (int)Width);
        int h = Math.Max(1, (int)Height);

        int sx = Math.Max(0, Definition.SourceX);
        int sy = Math.Max(0, Definition.SourceY);
        int sw = Math.Max(1, Definition.SourceWidth);
        int sh = Math.Max(1, Definition.SourceHeight);

        // Per-crop opacity (percent → 0-255). Clamp floor at 10% so a crop can't be
        // made fully invisible by accident (issue #62).
        byte opacity = (byte)(Math.Clamp(Definition.Opacity, 10, 100) * 255 / 100);

        var props = new DwmApi.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmApi.DWM_TNP.RECTDESTINATION | DwmApi.DWM_TNP.RECTSOURCE |
                      DwmApi.DWM_TNP.OPACITY | DwmApi.DWM_TNP.VISIBLE |
                      DwmApi.DWM_TNP.SOURCECLIENTAREAONLY,
            rcDestination = new DwmApi.RECT(0, 0, w, h),
            rcSource = new DwmApi.RECT(sx, sy, sx + sw, sy + sh),
            opacity = opacity,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };

        int hr = DwmApi.DwmUpdateThumbnailProperties(_thumbId, ref props);
        if (hr != 0)
            DiagnosticsService.LogDwm($"[Crop:DWM] UpdateThumbnailProperties hr=0x{hr:X8} (W:{w} H:{h} src={sx},{sy} {sw}x{sh})");
        return hr;
    }

    /// <summary>
    /// Self-heal a crop whose DWM thumbnail went stale (issue #64: crops vanish at
    /// random and only come back when the user toggles crops off/on). Called
    /// periodically by CropManager. If there's no registration, (re)create it; if
    /// re-pushing properties fails, the registration is dead — rebuild it so the
    /// crop reappears on its own.
    /// </summary>
    public void EnsureThumbnailHealthy()
    {
        if (_eveHwnd == IntPtr.Zero || _ownHwnd == IntPtr.Zero || Definition == null) return;

        if (_thumbId == IntPtr.Zero)
        {
            RegisterDwmThumbnail();
            return;
        }

        // A crop registered during the app-startup discovery burst can bind to a
        // source EVE window before DWM has it composited: the registration reports
        // success (non-null id) and PushThumbnailProperties returns hr==0, yet the
        // thumbnail paints nothing. Because the stale-check below only rebuilds on
        // hr!=0 — and CropManager.CropsInSync only checks the popup exists — neither
        // recovery path ever fires, so the crop stays blank until the user toggles
        // crops off/on (a full destroy+recreate). Force one rebind against the
        // by-now-composited source on the first health pass so a startup-race crop
        // recovers on its own within one health-check cycle (issue #80).
        if (!_initialRebindDone)
        {
            _initialRebindDone = true;
            ForceRebind();
            return;
        }

        int hr = PushThumbnailProperties();
        if (hr != 0)
        {
            DiagnosticsService.LogDwm($"[Crop:DWM] Heal: stale thumbnail hr=0x{hr:X8} for '{CharacterName}' — re-registering.");
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
            RegisterDwmThumbnail();
        }
    }

    /// <summary>Call-site compatibility shim kept for older callers.</summary>
    public void UpdateCaptureViewbox() => UpdateThumbnailDestination();

    /// <summary>Apply the crop's click-through state (issue #62): when on, the popup
    /// (and its label overlay) carry WS_EX_TRANSPARENT so mouse input passes through
    /// to whatever is behind — at the cost of not being draggable/resizable until
    /// turned off. Reads <see cref="CropDefinition.ClickThrough"/>.</summary>
    public void ApplyClickThrough()
    {
        if (_ownHwnd == IntPtr.Zero || Definition == null) return;
        bool clickThrough = Definition.ClickThrough;

        void Toggle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            int updated = clickThrough
                ? ex | User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED
                : ex & ~User32.WS_EX_TRANSPARENT;
            if (updated != ex)
                User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, updated);
        }

        Toggle(_ownHwnd);
        if (_textOverlay != null)
            Toggle(new WindowInteropHelper(_textOverlay).Handle);
    }

    /// <summary>The desired topmost band, tracked explicitly. The WPF <c>Topmost</c>
    /// property is NOT reliable on this AllowsTransparency (layered) window — setting
    /// it does not consistently apply WS_EX_TOPMOST, and BringToFront reading it back
    /// then picked HWND_TOP instead of HWND_TOPMOST, so the crop never actually entered
    /// the topmost band and fell behind every client (issue #80/#87 — confirmed from a
    /// user's debug_dwm.log: cropsTopmost was 0 while desiredTopmost was true). We now
    /// drive z-order the way the working WinForms ThumbnailWindow does: a tracked bool
    /// plus raw SetWindowPos, never the framework property.</summary>
    private bool _isTopmost;

    /// <summary>
    /// Whether the window ACTUALLY carries WS_EX_TOPMOST right now — read from the OS,
    /// not from a cached flag.
    ///
    /// This must not be a cached bool. A crop created during a client-launch burst can
    /// have its SetWindowPos(HWND_TOPMOST) fail to stick (the HWND isn't fully realized
    /// straight after Show(), and WPF can re-apply its own Topmost=false from the XAML
    /// afterwards). If we then remembered "I set it to topmost", CropManager's
    /// convergence guard (IsTopmostState != desired) would believe the crop was already
    /// topmost and NEVER retry — leaving it stuck behind the client forever. That is
    /// exactly what a user's log showed: crops grew 6 -> 15 while cropsTopmost stayed
    /// frozen at 6 (#80/#87). Reading the real bit makes the guard self-healing: any
    /// crop that isn't actually topmost gets fixed on the next pass.
    /// </summary>
    public bool IsTopmostState
    {
        get
        {
            var h = OwnHwnd;
            if (h == IntPtr.Zero) return _isTopmost;   // not realized yet — fall back
            return (User32.GetWindowLong(h, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        }
    }

    /// <summary>The crop's own top-level HWND, resolved robustly (OnLoaded may not have
    /// cached it yet on very early calls).</summary>
    private IntPtr OwnHwnd =>
        _ownHwnd != IntPtr.Zero ? _ownHwnd : new WindowInteropHelper(this).Handle;

    /// <summary>Set the crop (and its label overlay) into the topmost or normal band via
    /// RAW SetWindowPos — the WPF Topmost property alone does not reliably apply
    /// WS_EX_TOPMOST on this AllowsTransparency window. SWP_NOACTIVATE so we never steal
    /// focus. We ALSO assign the WPF property: leaving it at its XAML default of false
    /// lets WPF re-assert NOTOPMOST after Show() and silently undo the raw call, which is
    /// how freshly-created crops ended up stuck behind the client (#80/#87).</summary>
    // Outcome of the LAST raw SetWindowPos on the crop itself. We ignored the return
    // value for eight fix attempts; the diagnostic reports it so we can finally see
    // whether Windows is refusing the call or silently accepting-and-ignoring it.
    private bool _lastSwpOk;
    private int _lastSwpErr;

    public void SetTopmost(bool topmost)
    {
        _isTopmost = topmost;
        var band = topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;

        // NEVER touch the WPF Topmost property here — manage the z-band with raw
        // SetWindowPos ONLY, exactly like ThumbnailWindow, the one window in this app
        // that has never had a topmost bug (see its SetTopmost comment).
        //
        // Mixing the two is what kept #80/#87 alive. Raw SetWindowPos is invisible to
        // WPF's Topmost DependencyProperty, so WPF's cached value drifts from the real
        // WS_EX_TOPMOST bit. Once WPF caches "true", `Topmost = true` is a silent no-op,
        // and WPF re-applies its own cached value on later window events (already
        // observed in this file — see the IsTopmostState comment: "WPF can re-apply its
        // own Topmost=false from the XAML"). Measured signature from the [Crop:Stuck]
        // log: swpOk=True swpErr=0 wpfTopmost=True, yet the real topmost bit was False —
        // the call succeeded and the bit still did not stick.
        //
        // The window is now created topmost (Topmost="True" in XAML) so WPF's cached
        // view starts in the band we want and it never has to be *promoted* after the
        // fact. Reads go through IsTopmostState, which checks the real bit, not WPF.
        var own = OwnHwnd;
        if (own != IntPtr.Zero)
        {
            _lastSwpOk = User32.SetWindowPos(own, band, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            _lastSwpErr = _lastSwpOk ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }

        if (_textOverlay != null)
        {
            var overlayHwnd = new WindowInteropHelper(_textOverlay).Handle;
            if (overlayHwnd != IntPtr.Zero)
                User32.SetWindowPos(overlayHwnd, band, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }

    }

    private const int WS_CHILD = unchecked((int)0x40000000);

    /// <summary>One-line dump of why this crop may not be honouring topmost. Only used
    /// by the DWM-debug diagnostic when a crop reads NOT topmost despite us setting it
    /// (issue #80/#87: a subset of freshly-created crops stayed non-topmost and repeated
    /// SetWindowPos(HWND_TOPMOST) never took — cause unknown, so measure it).</summary>
    public string TopmostDiag()
    {
        var h = OwnHwnd;
        if (h == IntPtr.Zero) return $"'{CharacterName}' hwnd=0 (window not realized)";
        // Is the cached _ownHwnd still a real window, and is it the SAME one WPF is
        // using now? A stale handle would make every SetWindowPos a silent no-op.
        var live = new WindowInteropHelper(this).Handle;
        bool alive = User32.IsWindow(h);
        int ex = User32.GetWindowLong(h, User32.GWL_EXSTYLE);
        int style = User32.GetWindowLong(h, User32.GWL_STYLE);
        var owner = User32.GetWindowLongPtr(h, User32.GWLP_HWNDPARENT);
        bool topmost = (ex & User32.WS_EX_TOPMOST) != 0;
        bool child = (style & WS_CHILD) != 0;
        bool srcIconic = _eveHwnd != IntPtr.Zero && User32.IsIconic(_eveHwnd);
        return $"'{CharacterName}' hwnd=0x{h.ToInt64():X} live=0x{live.ToInt64():X} alive={alive} " +
               $"vis={User32.IsWindowVisible(h)} topmost={topmost} child={child} owner=0x{owner.ToInt64():X} " +
               $"swpOk={_lastSwpOk} swpErr={_lastSwpErr} wpfTopmost={Topmost} " +
               $"src=0x{_eveHwnd.ToInt64():X} srcIconic={srcIconic}";
    }

    /// <summary>Re-assert this crop's z-order above the EVE clients. A topmost flag
    /// alone does not survive a client being activated on top of the crop — the client
    /// raises over it and nothing pulls it back — so a crop vanishes underneath the
    /// moment you tab into that client (issue #80). Mirrors ThumbnailWindow.BringToFront:
    /// a raw SWP_NOACTIVATE z-order re-insert (no focus theft) using the TRACKED band,
    /// not the framework Topmost property, with the label overlay lifted with it.</summary>
    public void BringToFront()
    {
        IntPtr zOrder = _isTopmost ? User32.HWND_TOPMOST : User32.HWND_TOP;

        var own = OwnHwnd;
        if (own != IntPtr.Zero)
            User32.SetWindowPos(own, zOrder, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

        if (_textOverlay != null)
        {
            var overlayHwnd = new WindowInteropHelper(_textOverlay).Handle;
            if (overlayHwnd != IntPtr.Zero)
                User32.SetWindowPos(overlayHwnd, zOrder, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Whether this crop is currently hidden by a Hide/Show toggle (issue #66).</summary>
    public bool IsHiddenByToggle { get; private set; }

    /// <summary>Hide or show the crop popup and its companion label overlay as one
    /// unit (issue #66 — Hide/Show All keybind + dedicated Hide/Show Crops keybind).
    /// The DWM thumbnail registration lives on the popup HWND and survives the
    /// WPF Hide/Show, so re-showing restores the live preview without a rebind.</summary>
    public void SetHidden(bool hidden)
    {
        IsHiddenByToggle = hidden;
        if (hidden)
        {
            Hide();
            _textOverlay?.Hide();
        }
        else
        {
            Show();
            _textOverlay?.Show();
            // Re-assert source rect / opacity in case the registration idled while hidden.
            UpdateThumbnailDestination();
        }
    }

    /// <summary>Source EVE window the DWM thumbnail is composing from.</summary>
    public IntPtr EveHwnd => _eveHwnd;

    /// <summary>Tear down the current DWM thumbnail and immediately re-register a
    /// fresh one against the same source. Used on minimize-restore transitions
    /// where the registration goes stale but the source HWND is unchanged so
    /// <see cref="Rebind"/> would no-op (issue #65).</summary>
    public void ForceRebind()
    {
        if (_thumbId != IntPtr.Zero)
        {
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
        }
        RegisterDwmThumbnail();
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
