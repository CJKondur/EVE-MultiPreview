using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Interop;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;

using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;

namespace EveMultiPreview.Views;

/// <summary>
/// Borderless, transparent top-level window that hosts a live DWM thumbnail
/// of an EVE Online client.
///
/// Implemented as a System.Windows.Forms.Form (native HWND) — not a WPF Window —
/// because DWM thumbnails composited onto a WPF AllowsTransparency=True window
/// go black for sources that use a DXGI swap-chain (EVE "Fixed Windowed").
/// WPF's transparency path is UpdateLayeredWindow, which the DWM swap-chain
/// composition path does not support as a destination. WinForms uses plain
/// WS_EX_LAYERED + SetLayeredWindowAttributes(LWA_ALPHA), which DWM DOES
/// composite into correctly — the same pattern used by eve-o-preview (C# WinForms)
/// and eve-x-preview (AHK native HWND).
///
/// The colored frame is the border strip around the DWM thumbnail inset.
/// Text (character name, session timer, system name, etc.) lives in a separate
/// WPF TextOverlayWindow because DWM thumbnails occlude anything painted on
/// the destination HWND's client area.
/// </summary>
public class ThumbnailWindow : Form
{
    private IntPtr _thumbId;
    private IntPtr _eveHwnd;
    private IntPtr _ownHwnd;

    // Base dimensions (pre-hover)
    private int _baseWidth;
    private int _baseHeight;

    private enum DragMode { None, Drag, Resize }
    private DragMode _dragMode = DragMode.None;
    private System.Drawing.Point _dragStartScreen;
    private int _dragStartLeft;
    private int _dragStartTop;
    private int _resizeStartWidth;
    private int _resizeStartHeight;
    private bool _dragMovedPastThreshold;
    private bool _isDragging;
    public bool IsDragging => _isDragging;

    // Timestamp of the current right-button press. Used to distinguish a brief
    // click (→ open label editor) from a press-and-hold (→ drag).
    private DateTime _rightDownAtUtc;

    private double _hoverScale = 1.0;
    private bool _isHovered;
    private bool _isMouseOver;

    // Timers (WinForms UI-thread timers — same message pump as the Form)
    private System.Windows.Forms.Timer? _dragTimer;
    private System.Windows.Forms.Timer? _hoverTimer;
    private System.Windows.Forms.Timer? _underFirePulseTimer;

    private DateTime _sessionStart = DateTime.Now;

    private TextOverlayWindow? _textOverlay;

    private byte _savedOpacity = 255;
    private bool _isDwmHidden;

    // Frozen-frame snapshot shown when the source EVE window is minimized.
    // Bitmap is owned by FrozenFrameService — we only hold a reference.
    private Bitmap? _frozenFrame;

    private int _borderThickness;

    // Skip SetWindowPos when overlay position is unchanged.
    private (int Left, int Top, int Width, int Height) _lastSyncPos;

    // Public WPF-style properties preserved for ThumbnailManager contract.
    public IntPtr EveHwnd => _eveHwnd;
    public string CharacterName { get; private set; } = string.Empty;
    public bool IsLocked { get; set; }

    public event Action<ThumbnailWindow, int, int, int, int>? PositionChanged;
    public event Action<ThumbnailWindow>? SwitchRequested;
    public event Action<ThumbnailWindow>? MinimizeRequested;
    public event Action<ThumbnailWindow, double, double>? DragMoveAll;
    public event Action<ThumbnailWindow, int, int>? ResizeAll;
    public event Action<ThumbnailWindow>? CycleExclusionRequested;
    /// <summary>Fired when a menu item in the right-click context menu asks to
    /// open the label editor for this thumbnail (v2.0.6).</summary>
    public event Action<ThumbnailWindow>? LabelEditRequested;

    /// <summary>Raised when the user picks an alert mute/snooze duration from the
    /// right-click menu. minutes: &gt;0 snooze that many minutes, 0 unmute,
    /// int.MaxValue mute until explicitly cleared.</summary>
    public event Action<ThumbnailWindow, int>? AlertMuteRequested;

    /// <summary>Raised from the Audio submenu. code: -1 = mute, 0..100 = set volume %.</summary>
    public event Action<ThumbnailWindow, int>? AudioRequested;

    /// <summary>When this character's alerts are muted: the moment the snooze ends,
    /// DateTime.MaxValue for an indefinite mute, or null when not muted. Set by
    /// ThumbnailManager; read by the context menu to show remaining time.</summary>
    public DateTime? AlertMutedUntil { get; set; }

    /// <summary>When true, a drag is clamped to the monitor it started on (Ctrl is
    /// already taken by drag-all, so this is a setting, not a modifier). Set by
    /// ThumbnailManager from ConfineDragsToMonitor.</summary>
    public bool ConfineDragsToMonitor { get; set; }

    /// <summary>Current per-client audio volume (0-100) for the right-click slider.
    /// Set by ThumbnailManager from the saved per-client volume.</summary>
    public int AudioVolume { get; set; } = 100;

    // Right-click context menu (v2.0.6). Lazily built on first use, reused for
    // subsequent right-clicks. Extend by adding more ToolStripItems in
    // BuildContextMenu below.
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _muteMenu;
    private ToolStripMenuItem? _audioMenu;
    // Static context-menu items with localizable text, re-labelled on each open so
    // a language change applies without recreating the thumbnail (issue #86).
    private readonly System.Collections.Generic.List<(ToolStripItem item, string key, string en)> _ctxLoc = new();
    private TrackBar? _audioTrackBar;
    private bool _suppressAudioSlider;

    private void BuildContextMenu()
    {
        if (_contextMenu != null) return;
        _contextMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
        };

        // Set an item's text from the active language (English fallback) and
        // register it for re-labelling when the menu re-opens (issue #86).
        void CtxL(ToolStripItem it, string key, string en)
        {
            it.Text = Services.LocalizationService.Str(key, en);
            _ctxLoc.Add((it, key, en));
        }

        var editLabel = new ToolStripMenuItem();
        CtxL(editLabel, "L.Ctx.EditLabel", "✏ Edit Label…");
        editLabel.Click += (_, _) => LabelEditRequested?.Invoke(this);
        _contextMenu.Items.Add(editLabel);

        // Per-character alert mute / snooze — silence one alt's flash/badge/toast/
        // sound without disabling alerts globally (e.g. a noisy or known-safe client).
        // _muteMenu text is dynamic (set by RefreshMuteMenuState on open).
        _muteMenu = new ToolStripMenuItem();
        void AddMute(string key, string en, int minutes)
        {
            var item = new ToolStripMenuItem();
            CtxL(item, key, en);
            item.Click += (_, _) => AlertMuteRequested?.Invoke(this, minutes);
            _muteMenu.DropDownItems.Add(item);
        }
        AddMute("L.Ctx.Mute10", "For 10 minutes", 10);
        AddMute("L.Ctx.Mute30", "For 30 minutes", 30);
        AddMute("L.Ctx.Mute60", "For 1 hour", 60);
        AddMute("L.Ctx.MuteUntil", "Until I unmute", int.MaxValue);
        _muteMenu.DropDownItems.Add(new ToolStripSeparator());
        AddMute("L.Ctx.Unmute", "Unmute", 0);
        _contextMenu.Items.Add(_muteMenu);

        // Per-client audio (Windows per-process volume/mute, matched by PID).
        // _audioMenu text is dynamic (set by RefreshAudioMenuState on open).
        _audioMenu = new ToolStripMenuItem();

        // A real volume slider hosted inside the dropdown via ToolStripControlHost.
        _audioTrackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = 176,
            Height = 30,
        };
        _audioTrackBar.ValueChanged += (_, _) =>
        {
            if (_suppressAudioSlider) return;
            int v = _audioTrackBar.Value;
            AudioVolume = v;
            if (_audioMenu != null) _audioMenu.Text = string.Format(Services.LocalizationService.Str("L.Ctx.AudioFmt", "🔊 Audio — {0}%"), v);
            AudioRequested?.Invoke(this, v);
        };
        _audioMenu.DropDownItems.Add(new ToolStripControlHost(_audioTrackBar) { AutoSize = false, Width = 182, Height = 34 });
        _audioMenu.DropDownItems.Add(new ToolStripSeparator());

        var muteItem = new ToolStripMenuItem();
        CtxL(muteItem, "L.Ctx.AudioMute", "🔇 Mute");
        muteItem.Click += (_, _) => AudioRequested?.Invoke(this, -1);
        _audioMenu.DropDownItems.Add(muteItem);

        var fullItem = new ToolStripMenuItem();
        CtxL(fullItem, "L.Ctx.Audio100", "🔊 100% (unmute)");
        fullItem.Click += (_, _) => { AudioVolume = 100; AudioRequested?.Invoke(this, 100); };
        _audioMenu.DropDownItems.Add(fullItem);

        _contextMenu.Items.Add(_audioMenu);
    }

    /// <summary>Show the right-click context menu at the current cursor
    /// position. Called from OnRightButtonUp when the press was a plain click
    /// (no drag).</summary>
    private void ShowContextMenu()
    {
        BuildContextMenu();
        if (_contextMenu == null) return;
        // Re-label static items for the active language before showing (issue #86).
        foreach (var (it, key, en) in _ctxLoc)
            it.Text = Services.LocalizationService.Str(key, en);
        RefreshMuteMenuState();
        RefreshAudioMenuState();
        try
        {
            _contextMenu.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Thumb:ContextMenu] ⚠ Show failed: {ex.Message}");
        }
    }

    /// <summary>Update the Mute Alerts menu header to reflect the current mute state
    /// (re-evaluated each time the menu opens, since the menu is built once).</summary>
    private void RefreshMuteMenuState()
    {
        if (_muteMenu == null) return;
        bool muted = AlertMutedUntil.HasValue
            && (AlertMutedUntil.Value == DateTime.MaxValue || AlertMutedUntil.Value > DateTime.Now);
        if (!muted)
            _muteMenu.Text = Services.LocalizationService.Str("L.Ctx.MuteAlerts", "🔔 Mute Alerts");
        else if (AlertMutedUntil!.Value == DateTime.MaxValue)
            _muteMenu.Text = Services.LocalizationService.Str("L.Ctx.MutedUntilUnmuted", "🔇 Alerts muted (until unmuted)");
        else
        {
            int mins = Math.Max(1, (int)Math.Ceiling((AlertMutedUntil.Value - DateTime.Now).TotalMinutes));
            _muteMenu.Text = string.Format(Services.LocalizationService.Str("L.Ctx.MutedMinsLeft", "🔇 Alerts muted ({0}m left)"), mins);
        }
    }

    /// <summary>Sync the Audio submenu slider to the current per-client volume each
    /// time the menu opens (the menu is built once and reused).</summary>
    private void RefreshAudioMenuState()
    {
        if (_audioMenu == null || _audioTrackBar == null) return;
        int v = Math.Max(0, Math.Min(100, AudioVolume));
        _suppressAudioSlider = true;
        _audioTrackBar.Value = v;
        _suppressAudioSlider = false;
        _audioMenu.Text = string.Format(Services.LocalizationService.Str("L.Ctx.AudioFmt", "🔊 Audio — {0}%"), v);
    }

    // Session-only cycle-exclusion visual state (issue #16, drawing fixed in
    // #27). The strikeout itself is rendered in the WPF TextOverlayWindow —
    // drawing here in the WinForms client area is invisible because DWM
    // composites the thumbnail surface over it.
    private bool _cycleExcluded;
    public void SetCycleExcluded(bool excluded)
    {
        if (_cycleExcluded == excluded) return;
        _cycleExcluded = excluded;
        _textOverlay?.SetCycleExcluded(excluded);
    }

    public void SetCycleExclusionPosition(string position) => _textOverlay?.SetCycleExclusionPosition(position);

    // ── WPF-compat shims ────────────────────────────────────────────
    // ThumbnailManager treats these as WPF-style (double coords, Visibility enum,
    // Topmost/IsVisible spelling). Keep the contract bit-identical so callers
    // don't need to change when we swap the backing framework.

    public new double Left
    {
        get => base.Left;
        set => base.Left = (int)value;
    }
    public new double Top
    {
        get => base.Top;
        set => base.Top = (int)value;
    }
    public new double Width
    {
        get => base.Width;
        set => base.Width = (int)value;
    }
    public new double Height
    {
        get => base.Height;
        set => base.Height = (int)value;
    }
    // Tracks the intended topmost state. We cannot use the WinForms TopMost
    // property as the source of truth because its setter calls SetWindowPos
    // WITHOUT SWP_NOACTIVATE, which activates the thumbnail and steals
    // foreground from whatever had it (notably the Settings UI). All reads
    // and writes to topmost state must go through this field + SetTopmostNoActivate.
    private bool _isTopmost;
    public bool Topmost
    {
        get => _isTopmost;
        set => SetTopmost(value);
    }
    public bool IsVisible => Visible;
    public WpfVisibility Visibility
    {
        get => Visible ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        set
        {
            bool shouldShow = value == WpfVisibility.Visible;
            if (shouldShow) { if (!Visible) Show(); }
            else { if (Visible) Hide(); }
        }
    }

    // ── CreateParams — bake in WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE + WS_EX_LAYERED ─

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= User32.WS_EX_TOOLWINDOW
                        | User32.WS_EX_NOACTIVATE
                        | User32.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public ThumbnailWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = false;
        DoubleBuffered = true;
        BackColor = Color.Black;

        // Match WPF version's hit-test behavior: allow mouse on the window.
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        UpdateStyles();
    }

    /// <summary>Initialize for a specific EVE window. Must be called before Show().</summary>
    public void Initialize(IntPtr eveHwnd, string characterName, int width, int height, int x, int y)
    {
        _eveHwnd = eveHwnd;
        CharacterName = characterName;
        _baseWidth = width;
        _baseHeight = height;
        base.Width = width;
        base.Height = height;
        base.Left = x;
        base.Top = y;
        _sessionStart = DateTime.Now;

        // Create overlay immediately so ApplySettings can set text properties,
        // but defer Show() to OnHandleCreated (needs this Form's HWND).
        _textOverlay = new TextOverlayWindow
        {
            Width = width,
            Height = height,
            Left = x,
            Top = y,
        };
        if (!string.IsNullOrEmpty(characterName))
            _textOverlay.UpdateCharacterName(characterName);
    }

    public void SetHoverScale(double scale) => _hoverScale = scale;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ownHwnd = Handle;

        // Start fully opaque. LWA_ALPHA fades both the frame (this HWND's
        // background strip) AND the DWM-composited thumbnail on top.
        User32.SetLayeredWindowAttributes(_ownHwnd, 0, 255, User32.LWA_ALPHA);

        // Apply any topmost state that was set before the handle existed.
        // SetTopmost no-ops when _ownHwnd is zero, so callers (e.g. during
        // thumbnail setup) may have set _isTopmost=true without the HWND
        // reflecting it. Push the state through now that the handle is real.
        if (_isTopmost)
        {
            User32.SetWindowPos(_ownHwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }

        Debug.WriteLine($"[Thumb:Handle] OK hwnd=0x{_ownHwnd:X} char='{CharacterName}'");

        RegisterDwmThumbnail();

        // Parent overlay to this HWND so it stays above the thumbnail.
        if (_textOverlay != null)
        {
            var helper = new WindowInteropHelper(_textOverlay);
            helper.EnsureHandle();
            User32.SetWindowLongPtr(helper.Handle, User32.GWLP_HWNDPARENT, _ownHwnd);
            _textOverlay.Show();
        }
    }

    // ── Win32 message loop ────────────────────────────────────────

    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_SIZE = 0x0005;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_RBUTTONDOWN:
                OnRightButtonDown();
                return;
            case WM_RBUTTONUP:
                OnRightButtonUp();
                return;
            case WM_LBUTTONDOWN:
                OnLeftButtonDown();
                return;
            case WM_LBUTTONUP:
                OnLeftButtonUp();
                return;
            case WM_SIZE:
                base.WndProc(ref m);
                UpdateThumbnailSize();
                return;
        }
        base.WndProc(ref m);
    }

    private void UnHover()
    {
        if (!_isHovered) return;
        _isHovered = false;

        int offsetX = ((int)Width - _baseWidth) / 2;
        int offsetY = ((int)Height - _baseHeight) / 2;

        base.Width = _baseWidth;
        base.Height = _baseHeight;
        base.Left += offsetX;
        base.Top += offsetY;
        UpdateThumbnailSize();
        _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
    }

    private void OnRightButtonDown()
    {
        // Record press time in all cases so a locked thumbnail's plain right-click
        // still opens the label editor on release.
        _rightDownAtUtc = DateTime.UtcNow;

        if (IsLocked)
        {
            Debug.WriteLine("[Thumb:Lock] drag blocked");
            return;
        }
        if (_dragMode != DragMode.None) return;

        UnHover();

        var screenPos = Cursor.Position;
        _dragStartScreen = screenPos;
        _dragStartLeft = base.Left;
        _dragStartTop = base.Top;
        _dragMovedPastThreshold = false;
        _dragMode = DragMode.Drag;
        _isDragging = true;

        _dragTimer?.Stop();
        _dragTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    private void OnRightButtonUp()
    {
        if (_dragMode == DragMode.Drag)
        {
            if (!_dragMovedPastThreshold)
            {
                // Plain right-click (press + release with no drag movement) —
                // opens the thumbnail's right-click menu (v2.0.6). Drag intent
                // is preserved: any ≥3px movement before release takes the drag
                // path instead.
                StopDrag();
                ShowContextMenu();
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
        else if (IsLocked)
        {
            // Locked thumbnails never entered drag mode — plain right-click
            // should still show the menu.
            ShowContextMenu();
        }
    }

    private void OnLeftButtonDown()
    {
        if (_dragMode == DragMode.Drag)
        {
            UnHover();
            _dragMode = DragMode.Resize;
            _dragStartScreen = Cursor.Position;
            _resizeStartWidth = base.Width;
            _resizeStartHeight = base.Height;
        }
    }

    private void OnLeftButtonUp()
    {
        if (_dragMode == DragMode.Resize)
        {
            StopDrag();
            SavePosition();
            return;
        }

        if (_dragMode == DragMode.None)
        {
            if (User32.IsKeyDown(User32.VK_LCONTROL))
                MinimizeRequested?.Invoke(this);
            else if (User32.IsKeyDown(User32.VK_LSHIFT) || User32.IsKeyDown(User32.VK_RSHIFT))
                CycleExclusionRequested?.Invoke(this);  // Issue #16
            else
                SwitchToEveClient();
        }
    }

    private void DragTick(object? sender, EventArgs e)
    {
        var mouse = Cursor.Position;

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
                _resizeStartWidth = base.Width;
                _resizeStartHeight = base.Height;
                return;
            }

            int dx = mouse.X - _dragStartScreen.X;
            int dy = mouse.Y - _dragStartScreen.Y;

            if (!_dragMovedPastThreshold && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                _dragMovedPastThreshold = true;

            if (_dragMovedPastThreshold)
            {
                int newX = _dragStartLeft + dx;
                int newY = _dragStartTop + dy;

                // Keep the drag on the monitor it started on, if enabled.
                if (ConfineDragsToMonitor)
                {
                    var wa = System.Windows.Forms.Screen.FromRectangle(
                        new System.Drawing.Rectangle(_dragStartLeft, _dragStartTop, base.Width, base.Height)).WorkingArea;
                    newX = Math.Max(wa.Left, Math.Min(newX, wa.Right - base.Width));
                    newY = Math.Max(wa.Top, Math.Min(newY, wa.Bottom - base.Height));
                }

                if (_ownHwnd != IntPtr.Zero)
                    User32.SetWindowPos(_ownHwnd, IntPtr.Zero, newX, newY, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

                var overlayHwnd = _textOverlay?.GetHwnd() ?? IntPtr.Zero;
                if (overlayHwnd != IntPtr.Zero)
                    User32.SetWindowPos(overlayHwnd, IntPtr.Zero, newX, newY, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

                if (User32.IsKeyDown(User32.VK_LCONTROL))
                    DragMoveAll?.Invoke(this, dx, dy);
            }
        }
        else if (_dragMode == DragMode.Resize)
        {
            if (!User32.IsKeyDown(User32.VK_LBUTTON) || !User32.IsKeyDown(User32.VK_RBUTTON))
            {
                StopDrag();
                SavePosition();
                return;
            }

            int dx = mouse.X - _dragStartScreen.X;
            int dy = mouse.Y - _dragStartScreen.Y;

            int newW = Math.Max(_resizeStartWidth + dx, 80);
            int newH = Math.Max(_resizeStartHeight + dy, 50);

            base.Width = newW;
            base.Height = newH;
            _baseWidth = newW;
            _baseHeight = newH;
            UpdateThumbnailSize();
            _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);

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
            _dragTimer.Dispose();
            _dragTimer = null;
        }

        if (_ownHwnd != IntPtr.Zero)
        {
            User32.GetWindowRect(_ownHwnd, out var rect);
            base.Left = rect.Left;
            base.Top = rect.Top;
            _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
        }
    }

    private void SavePosition()
    {
        PositionChanged?.Invoke(this, base.Left, base.Top, base.Width, base.Height);
    }

    private void SwitchToEveClient()
    {
        if (_eveHwnd == IntPtr.Zero) return;
        SwitchRequested?.Invoke(this);
    }

    // ── Hover zoom ───────────────────────────────────────────────

    /// <summary>When true, hovering bumps the thumbnail to 100% opacity and
    /// restores the previous opacity on leave. Drives issue #37.</summary>
    public bool OpacityOnHover { get; set; }

    private byte _opacityBeforeHover = 255;
    private bool _opacityHoverActive;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isMouseOver = true;

        // Hover-to-full-opacity. Bump both the WinForms thumbnail (LWA_ALPHA)
        // AND the WPF text overlay (which is a separate window with its own
        // Opacity property) so the character name / system / CPU / RAM / VRAM
        // line snaps to fully opaque too. Snapshot the pre-hover values so
        // the leave handler restores them exactly. Not gated on _isDwmHidden:
        // when iconic, the frozen frame is faded by LWA_ALPHA the same way
        // the live DWM thumbnail is.
        if (OpacityOnHover && _currentVisualOpacity != 255 && !_opacityHoverActive)
        {
            _opacityBeforeHover = _currentVisualOpacity;
            _opacityHoverActive = true;
            ApplyVisualOpacity(255);
            _textOverlay?.SetWindowOpacity(1.0);
        }

        if (_hoverScale <= 1.0 || _dragMode != DragMode.None) return;

        _hoverTimer?.Stop();
        _hoverTimer?.Dispose();
        _hoverTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            if (!_isMouseOver) return;

            _isHovered = true;

            int newWidth = (int)(_baseWidth * _hoverScale);
            int newHeight = (int)(_baseHeight * _hoverScale);
            int offsetX = (newWidth - _baseWidth) / 2;
            int offsetY = (newHeight - _baseHeight) / 2;

            base.Width = newWidth;
            base.Height = newHeight;
            base.Left -= offsetX;
            base.Top -= offsetY;
            UpdateThumbnailSize();
            _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
        };
        _hoverTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isMouseOver = false;
        _hoverTimer?.Stop();

        // Restore the pre-hover opacity on both surfaces (issue #37).
        if (_opacityHoverActive)
        {
            _opacityHoverActive = false;
            ApplyVisualOpacity(_opacityBeforeHover);
            _textOverlay?.SetWindowOpacity(_opacityBeforeHover / 255.0);
        }

        UnHover();
    }

    // ── DWM thumbnail ────────────────────────────────────────────

    private void RegisterDwmThumbnail()
    {
        if (_eveHwnd == IntPtr.Zero || _ownHwnd == IntPtr.Zero) return;
        if (_thumbId != IntPtr.Zero) return;

        _thumbId = DwmApi.RegisterThumbnail(_ownHwnd, _eveHwnd);
        if (_thumbId == IntPtr.Zero)
        {
            DiagnosticsService.LogDwm($"[Thumb:DWM] RegisterThumbnail returned 0 for '{CharacterName}' src=0x{_eveHwnd.ToInt64():X}");
            return;
        }
        ApplyThumbnailPresentation();
    }

    public void UpdateThumbnailSize() => ApplyThumbnailPresentation();

    private void ApplyThumbnailPresentation()
    {
        if (_thumbId == IntPtr.Zero) return;

        // Reserve un-composited strip for BOTH the main border and the nested
        // inner alert border (#71); otherwise the DWM thumbnail would paint over
        // the inner band and hide it.
        int b = Math.Max(0, _borderThickness) + Math.Max(0, _alertBorderThickness);
        int w = Math.Max(1, base.Width);
        int h = Math.Max(1, base.Height);

        // Binary — actual fade is done by LWA_ALPHA on the destination HWND,
        // which DWM honors when compositing the thumbnail. Setting the DWM
        // opacity byte to anything other than 0 or 255 would compound with
        // LWA_ALPHA and produce a non-uniform fade.
        byte dwmOpacity = _isDwmHidden ? (byte)0 : (byte)255;
        DwmApi.UpdateThumbnailInset(_thumbId, w, h, b, dwmOpacity, clientAreaOnly: true);
    }

    // ── Overlay passthrough ──────────────────────────────────────

    public void UpdateCharacterName(string name)
    {
        CharacterName = name;
        _textOverlay?.UpdateCharacterName(name);
    }

    public void UpdateAnnotation(string? text) => _textOverlay?.UpdateAnnotation(text);

    /// <summary>Apply a per-character label color + size override (v2.0.6).
    /// Empty colorHex / sizePt==0 revert to the global thumbnail text style.</summary>
    public void SetLabelStyle(string? colorHex, int sizePt) =>
        _textOverlay?.SetLabelStyle(colorHex, sizePt);
    public void UpdateSystemName(string? systemName) => _textOverlay?.UpdateSystemName(systemName);

    /// <summary>Animate a system transition (issue #25): briefly show
    /// "old → new" before settling to just the new system name.</summary>
    public void AnimateSystemTransition(string from, string to, int durationMs = 3500) =>
        _textOverlay?.AnimateSystemTransition(from, to, durationMs);
    public void UpdateSessionTimer(TimeSpan elapsed) => _textOverlay?.UpdateSessionTimer(elapsed);
    public void UpdateProcessStats(string? text) => _textOverlay?.UpdateProcessStats(text);
    public void UpdateFpsStats(double fps, bool visible) => _textOverlay?.UpdateFpsStats(fps, visible);
    public void SetProcessStatsTextSize(double fontSize) => _textOverlay?.SetProcessStatsTextSize(fontSize);
    public void SetAlertBadge(int count, string colorHex) => _textOverlay?.SetAlertBadge(count, colorHex);

    public int GetProcessId()
    {
        User32.GetWindowThreadProcessId(_eveHwnd, out uint pid);
        return (int)pid;
    }

    // ── Opacity ──────────────────────────────────────────────────

    private byte _currentVisualOpacity = 255;
    private WpfColor? _baseBorderColor;
    private WpfColor _baseBackgroundColor = WpfColor.FromArgb(255, 0, 0, 0);

    public void ApplyVisualOpacity(byte opacity)
    {
        _currentVisualOpacity = opacity;

        // Single LWA_ALPHA byte fades the entire destination HWND uniformly,
        // including the DWM-composited thumbnail, which is the whole reason
        // we chose a native WinForms HWND over a WPF AllowsTransparency window.
        if (_ownHwnd != IntPtr.Zero)
            User32.SetLayeredWindowAttributes(_ownHwnd, 0, opacity, User32.LWA_ALPHA);

        if (_textOverlay != null)
            _textOverlay.Visibility = opacity == 0 ? WpfVisibility.Collapsed : WpfVisibility.Visible;

        ApplyThumbnailPresentation();
        if (IsHandleCreated) Invalidate();
    }

    // ── Not-Logged-In indicator ──────────────────────────────────

    private bool _notLoggedInVisible;

    public void SetNotLoggedIn(bool isCharSelect, string mode, WpfColor? borderColor = null, int dim = 80)
    {
        if (isCharSelect && mode != "none")
        {
            byte dimOpacity = (byte)(dim / 100.0 * _savedOpacity);
            switch (mode.ToLowerInvariant())
            {
                case "text":
                    _notLoggedInVisible = true;
                    if (!_isDwmHidden) ApplyVisualOpacity(_savedOpacity);
                    break;
                case "dim":
                    if (!_isDwmHidden) ApplyVisualOpacity(dimOpacity);
                    _notLoggedInVisible = false;
                    break;
                case "border":
                    if (borderColor.HasValue) SetBorder(borderColor.Value, 3);
                    _notLoggedInVisible = false;
                    if (!_isDwmHidden) ApplyVisualOpacity(_savedOpacity);
                    break;
                case "color":
                    if (borderColor.HasValue) SetBorder(borderColor.Value, 3);
                    _notLoggedInVisible = true;
                    if (!_isDwmHidden) ApplyVisualOpacity(_savedOpacity);
                    break;
                default:
                    _notLoggedInVisible = true;
                    if (!_isDwmHidden) ApplyVisualOpacity(_savedOpacity);
                    break;
            }
            Debug.WriteLine($"[Thumb:NotLoggedIn] '{CharacterName}' mode={mode}");
        }
        else
        {
            _notLoggedInVisible = false;
            if (!_isDwmHidden) ApplyVisualOpacity(_savedOpacity);
        }
        if (IsHandleCreated) Invalidate();
    }

    public void SetBorder(WpfColor color, int thickness)
    {
        _baseBorderColor = color;

        if (_borderThickness != thickness)
        {
            _borderThickness = thickness;
            ApplyThumbnailPresentation();
        }
        if (IsHandleCreated) Invalidate();
    }

    // ── Inner alert border (#71) ─────────────────────────────────
    // A SECOND border nested just inside the main highlight border. Alerts pulse
    // this inner border instead of overwriting the main one, so a fleet-wide red
    // flash never hides which client is currently selected. ApplyThumbnailPresentation
    // reserves DWM inset space for both bands; the thickness is held constant for the
    // duration of an alert (only the colour toggles while pulsing) so the composited
    // thumbnail doesn't jump on every pulse.
    private WpfColor? _alertBorderColor;
    private int _alertBorderThickness;

    public void SetAlertBorder(WpfColor? color, int thickness)
    {
        thickness = Math.Max(0, thickness);
        bool insetChanged = thickness != _alertBorderThickness;
        _alertBorderColor = color;
        _alertBorderThickness = thickness;
        if (insetChanged) ApplyThumbnailPresentation(); // resize DWM inset to fit the inner band
        if (IsHandleCreated) Invalidate();
    }

    // ── Under Fire Pulse ─────────────────────────────────────────

    private bool _isUnderFire;
    private double _underFirePhase;
    private int _borderThicknessBeforeUnderFire;
    private WpfColor? _underFireColor;

    public void SetUnderFire(bool active)
    {
        if (active && !_isUnderFire)
        {
            _isUnderFire = true;
            _underFirePhase = 0;
            _borderThicknessBeforeUnderFire = _borderThickness;

            if (_borderThickness < 3)
            {
                _borderThickness = 3;
                ApplyThumbnailPresentation();
            }

            _underFirePulseTimer?.Stop();
            _underFirePulseTimer?.Dispose();
            _underFirePulseTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _underFirePulseTimer.Tick += (_, _) =>
            {
                _underFirePhase += 0.15;
                byte alpha = (byte)(168 + 87 * Math.Sin(_underFirePhase));
                _underFireColor = WpfColor.FromArgb(alpha, 255, 40, 40);
                if (IsHandleCreated) Invalidate();
            };
            _underFirePulseTimer.Start();
        }
        else if (!active && _isUnderFire)
        {
            _isUnderFire = false;
            _underFirePulseTimer?.Stop();
            _underFirePulseTimer?.Dispose();
            _underFirePulseTimer = null;
            _underFireColor = null;

            _borderThickness = _borderThicknessBeforeUnderFire;
            ApplyThumbnailPresentation();
            if (IsHandleCreated) Invalidate();
        }
    }

    public void SetBackgroundColor(WpfColor color)
    {
        _baseBackgroundColor = color;
        BackColor = Color.FromArgb(255, color.R, color.G, color.B);
        if (IsHandleCreated) Invalidate();
    }

    public void SetClickThrough(bool enable)
    {
        if (_textOverlay == null) return;
        var source = (HwndSource)System.Windows.PresentationSource.FromVisual(_textOverlay);
        if (source == null) return;

        int exStyle = User32.GetWindowLong(source.Handle, User32.GWL_EXSTYLE);
        if (enable)
            User32.SetWindowLong(source.Handle, User32.GWL_EXSTYLE,
                exStyle | User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED);
        else
            User32.SetWindowLong(source.Handle, User32.GWL_EXSTYLE,
                exStyle & ~User32.WS_EX_TRANSPARENT);

        User32.SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE |
            User32.SWP_NOZORDER | User32.SWP_FRAMECHANGED | User32.SWP_NOACTIVATE);
    }

    public void SetOpacity(byte opacity)
    {
        _savedOpacity = opacity;
        // Always push to the layered-window alpha. When the source EVE client is
        // minimized, _isDwmHidden is true and OnPaint draws the frozen-frame
        // snapshot via GDI — that paint is faded by LWA_ALPHA, so skipping the
        // call here left iconic thumbnails stuck at their old opacity while the
        // text overlay (which has independent opacity) faded correctly.
        // _isDwmHidden is independent of LWA_ALPHA — HideDwmOnly only flips the
        // binary DWM thumbnail composition, so updating alpha here is safe.
        ApplyVisualOpacity(_savedOpacity);
        _textOverlay?.SetWindowOpacity(opacity / 255.0);
    }

    public void SetTextOverlayVisible(bool visible) => _textOverlay?.SetTextOverlayVisible(visible);
    public void SetTextStyle(string fontFamily, double fontSize, WpfColor color)
        => _textOverlay?.SetTextStyle(fontFamily, fontSize, color);
    public void SetTextMargins(int marginX, int marginY) => _textOverlay?.SetTextMargins(marginX, marginY);

    // ── Position/Size ────────────────────────────────────────────

    // 'Resize' hides Control.Resize event — ThumbnailManager invokes this as a method.
    public new void Resize(int width, int height)
    {
        _baseWidth = width;
        _baseHeight = height;
        if (!_isHovered)
        {
            base.Width = width;
            base.Height = height;
            UpdateThumbnailSize();
            _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
        }
    }

    public void MoveTo(int x, int y)
    {
        base.Left = x;
        base.Top = y;
        _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
    }

    public TimeSpan SessionDuration => DateTime.Now - _sessionStart;

    // ── Visibility ───────────────────────────────────────────────

    public void HideDwmOnly()
    {
        if (_isDwmHidden) return;
        _isDwmHidden = true;
        ApplyThumbnailPresentation();
        _notLoggedInVisible = false;
        if (IsHandleCreated) Invalidate();
    }

    public void ShowDwmOnly()
    {
        if (!_isDwmHidden) return;
        _isDwmHidden = false;
        ApplyVisualOpacity(_currentVisualOpacity);
    }

    public void SyncOverlayPosition(bool eveFocused = true)
    {
        if (_textOverlay == null) return;

        var current = (base.Left, base.Top, base.Width, base.Height);
        if (current == _lastSyncPos) return;
        _lastSyncPos = current;

        var overlayHwnd = _textOverlay.GetHwnd();
        if (overlayHwnd == IntPtr.Zero) return;

        User32.SetWindowPos(overlayHwnd, IntPtr.Zero,
            base.Left, base.Top, base.Width, base.Height,
            User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
    }

    public new void BringToFront()
    {
        var zOrder = _isTopmost ? User32.HWND_TOPMOST : User32.HWND_TOP;
        if (_ownHwnd != IntPtr.Zero)
        {
            User32.SetWindowPos(_ownHwnd, zOrder, 0, 0, 0, 0,
                User32.SWP_NOACTIVATE | User32.SWP_NOMOVE | User32.SWP_NOSIZE);
        }

        if (_textOverlay != null)
        {
            var overlayHwnd = _textOverlay.GetHwnd();
            if (overlayHwnd != IntPtr.Zero)
            {
                User32.SetWindowPos(overlayHwnd, zOrder,
                    base.Left, base.Top, base.Width, base.Height,
                    User32.SWP_NOACTIVATE);
            }
        }
    }

    public void EnsureOverlayZOrder()
    {
        if (_textOverlay == null) return;
        if (!_textOverlay.IsVisible)
            _textOverlay.Show();

        var overlayHwnd = _textOverlay.GetHwnd();
        if (overlayHwnd != IntPtr.Zero)
        {
            var zOrder = _isTopmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
            User32.SetWindowPos(overlayHwnd, zOrder,
                base.Left, base.Top, base.Width, base.Height,
                User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
        }
    }

    public void SetTopmost(bool topmost)
    {
        _isTopmost = topmost;
        if (_ownHwnd != IntPtr.Zero)
        {
            // Use SetWindowPos directly with SWP_NOACTIVATE to change z-band
            // without stealing foreground. The WinForms Form.TopMost setter
            // issues a SetWindowPos WITHOUT SWP_NOACTIVATE, which causes the
            // thumbnail to grab activation and kick the Settings UI into a
            // Activated/Deactivated flicker loop that makes it unclickable.
            var insertAfter = topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
            User32.SetWindowPos(_ownHwnd, insertAfter, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
        // Drop/raise the overlay's topmost via RAW SetWindowPos, NOT the WPF
        // Topmost property. BringToFront/EnsureOverlayZOrder raise the overlay
        // topmost with raw SetWindowPos, which WPF's internal Topmost state never
        // sees — so a later `_textOverlay.Topmost = false` is a WPF no-op ("already
        // false") and leaves WS_EX_TOPMOST set, stranding the label overlay on top
        // of other windows even though the thumbnail body dropped. Managing it the
        // same (raw) way here guarantees the topmost flag is actually cleared.
        if (_textOverlay != null)
        {
            var overlayHwnd = _textOverlay.GetHwnd();
            if (overlayHwnd != IntPtr.Zero)
            {
                var insertAfter = topmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST;
                User32.SetWindowPos(overlayHwnd, insertAfter, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }
            else
            {
                _textOverlay.Topmost = topmost; // handle not realized yet — set the WPF property
            }
        }
    }

    public void HideWithOverlay()
    {
        Hide();
        _textOverlay?.Hide();
    }

    /// <summary>Show a cached snapshot of the EVE window in the thumbnail area
    /// instead of the live DWM preview — used when the source is minimized.
    /// The bitmap is owned by the caller (FrozenFrameService); we just paint it.</summary>
    public void SetFrozenFrame(Bitmap? frame)
    {
        if (ReferenceEquals(_frozenFrame, frame)) return;
        _frozenFrame = frame;
        if (IsHandleCreated) Invalidate();
    }

    public void ClearFrozenFrame()
    {
        if (_frozenFrame == null) return;
        _frozenFrame = null;
        if (IsHandleCreated) Invalidate();
    }

    public void ShowWithOverlay()
    {
        Show();
        _textOverlay?.Show();
        // Re-assert z-order: a plain Show() re-inserts the window at a default
        // position that lands BEHIND a focused EVE client, so a thumbnail re-shown
        // after hide-on-lost-focus / hide-active / a visibility toggle would come
        // back hidden behind the game. BringToFront honors the topmost setting.
        BringToFront();
    }

    // ── Painting ─────────────────────────────────────────────────

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Fill with BackColor — BackgroundPanel equivalent. DWM thumbnail occludes
        // everything except the inset border strip, so most of this is hidden.
        using var bg = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(bg, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        // Frozen snapshot (shown when source EVE window is minimized and DWM
        // composition is blank). Drawn inside the border inset so the colored
        // frame still surrounds the content.
        if (_frozenFrame != null && _isDwmHidden)
        {
            int b = Math.Max(0, _borderThickness);
            var dest = new Rectangle(b, b,
                Math.Max(1, ClientSize.Width - 2 * b),
                Math.Max(1, ClientSize.Height - 2 * b));
            try
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_frozenFrame, dest);
            }
            catch { /* bitmap may have been disposed mid-paint by service — ignore */ }
        }

        // Under-fire pulse takes priority over the normal border.
        if (_isUnderFire && _underFireColor is WpfColor uc)
        {
            using var pen = new Pen(Color.FromArgb(uc.A, uc.R, uc.G, uc.B), _borderThickness)
            {
                Alignment = PenAlignment.Inset
            };
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
        else if (_borderThickness > 0 && _baseBorderColor is WpfColor bc)
        {
            // Honor the alpha channel set by the caller. ThumbnailManager.
            // ResolveAlertFlashColor multiplies AlertOpacityPercent into the
            // alpha when computing flash colors (issue #34); regular non-flash
            // borders set alpha=255 implicitly so they render unchanged.
            using var pen = new Pen(Color.FromArgb(bc.A, bc.R, bc.G, bc.B), _borderThickness)
            {
                Alignment = PenAlignment.Inset
            };
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        // Inner alert border (#71) — nested just inside the main border, drawn in
        // the extra inset reserved by ApplyThumbnailPresentation. Pulses on alert
        // without touching the main highlight, so the selected client stays
        // identifiable during a fleet-wide flash. Suppressed during under-fire pulse.
        if (!_isUnderFire && _alertBorderColor is WpfColor ac && _alertBorderThickness > 0)
        {
            int inset = Math.Max(0, _borderThickness);
            int iw = ClientSize.Width - 2 * inset - 1;
            int ih = ClientSize.Height - 2 * inset - 1;
            if (iw > 0 && ih > 0)
            {
                using var pen = new Pen(Color.FromArgb(ac.A, ac.R, ac.G, ac.B), _alertBorderThickness)
                {
                    Alignment = PenAlignment.Inset
                };
                g.DrawRectangle(pen, inset, inset, iw, ih);
            }
        }

        // (Cycle-exclusion strikeout is now drawn in the WPF TextOverlayWindow —
        //  see TextOverlayWindow.SetCycleExcluded. Drawing it here would be
        //  hidden by DWM composition.)

        // Not-Logged-In overlay — only visually effective when DWM thumbnail is
        // at partial opacity (LWA_ALPHA fades the whole HWND including this paint,
        // DWM composites the thumbnail on top at its own alpha).
        if (_notLoggedInVisible && !_isDwmHidden)
        {
            using var dim = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
            g.FillRectangle(dim, ClientRectangle);

            const string text = "Not Logged In";
            using var font = new Font("Segoe UI", 10f);
            var size = g.MeasureString(text, font);
            float tx = (ClientSize.Width - size.Width) / 2f;
            float ty = (ClientSize.Height - size.Height) / 2f;
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(text, font, textBrush, tx, ty);
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        Cleanup();
    }

    private bool _cleanedUp;

    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        StopDrag();

        _hoverTimer?.Stop();
        _hoverTimer?.Dispose();
        _hoverTimer = null;

        _underFirePulseTimer?.Stop();
        _underFirePulseTimer?.Dispose();
        _underFirePulseTimer = null;

        _contextMenu?.Dispose();
        _contextMenu = null;

        if (_thumbId != IntPtr.Zero)
        {
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
        }

        _textOverlay?.Close();
        _textOverlay = null;
    }
}
