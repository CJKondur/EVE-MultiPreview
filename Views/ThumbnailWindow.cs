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
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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

    /// <summary>Raised by the right-click "Close client" item (#115).</summary>
    public event Action<ThumbnailWindow>? CloseClientRequested;

    /// <summary>Raised by the right-click "Open ‹character›'s settings" item (spec D6, Plan 4). Not offered on a
    /// login-screen thumbnail.</summary>
    public event Action<ThumbnailWindow>? SettingsRequested;

    /// <summary>The system a character was last seen in, for the right-click menu's caption. Set by ThumbnailManager
    /// (GetCharacterSystem).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? SystemLookup { get; set; }

    /// <summary>When this character's alerts are muted: the moment the snooze ends,
    /// DateTime.MaxValue for an indefinite mute, or null when not muted. Set by
    /// ThumbnailManager; read by the context menu to show remaining time.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DateTime? AlertMutedUntil { get; set; }

    /// <summary>The events snoozed for a character alone (IDEAS #30), for the right-click menu. Set by ThumbnailManager.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, System.Collections.Generic.IReadOnlyList<EveMultiPreview.Services.EventSnooze>>? SnoozeLookup { get; set; }

    /// <summary>Raised by the menu's Snoozed alerts items: the event's key, or null for all of this character's.</summary>
    public event Action<ThumbnailWindow, string?>? SnoozeEndRequested;

    /// <summary>When true, a drag is clamped to the monitor it started on (Ctrl is
    /// already taken by drag-all, so this is a setting, not a modifier). Set by
    /// ThumbnailManager from ConfineDragsToMonitor.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ConfineDragsToMonitor { get; set; }

    /// <summary>The smallest size a resize drag gives (IDEAS P26): 80 × 50 as it always was, or the minimum size the
    /// player turned on. Set by ThumbnailManager.ApplySettings (ResizeFloor).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public System.Drawing.Size ResizeFloor { get; set; } = new(80, 50);

    /// <summary>The size a resize drag gives: the start size plus the mouse movement, never below the floor.</summary>
    public static (int Width, int Height) ResizedTo(int startWidth, int startHeight, int dx, int dy, System.Drawing.Size floor) =>
        (Math.Max(startWidth + dx, floor.Width), Math.Max(startHeight + dy, floor.Height));

    /// <summary>Current per-client audio volume (0-100) for the right-click slider.
    /// Set by ThumbnailManager from the saved per-client volume.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int AudioVolume { get; set; } = 100;

    // Right-click menu (v2.0.6; Fluent look and "Open ‹character›'s settings" in Plan 4): Views/ThumbnailMenu. Built on
    // the first right-click and reused; it re-reads its labels and this thumbnail's state each time it opens.
    private ThumbnailMenu? _menu;

    /// <summary>Show the right-click menu at the cursor. Called from OnRightButtonUp when the press was a plain click
    /// (no drag).</summary>
    private void ShowContextMenu()
    {
        try
        {
            _menu ??= new ThumbnailMenu(
                editLabel: () => LabelEditRequested?.Invoke(this),
                mute: minutes => AlertMuteRequested?.Invoke(this, minutes),
                audio: code => { if (code >= 0) AudioVolume = code; AudioRequested?.Invoke(this, code); },
                openSettings: () => SettingsRequested?.Invoke(this),
                closeClient: () => CloseClientRequested?.Invoke(this),
                endSnooze: key => SnoozeEndRequested?.Invoke(this, key));
            _menu.Refresh(new ThumbnailMenuState(CharacterName, SystemLookup?.Invoke(CharacterName), AlertMutedUntil, AudioVolume, DateTime.Now)
            {
                Snoozes = SnoozeLookup?.Invoke(CharacterName) ?? Array.Empty<EveMultiPreview.Services.EventSnooze>(),
            });
            // IDEAS #13 (T6-I1): counted before it is shown, so that no thumbnail grows over it; taken away again when
            // it has closed (OnMenuClosed), when it did not open, and when showing it failed.
            _menu.Strip.Closed -= OnMenuClosed;
            _menu.Strip.Closed += OnMenuClosed;
            NoteMenu(true);
            _menu.Strip.Show(Cursor.Position);
            if (!_menu.Strip.Visible) NoteMenu(false);
        }
        catch (Exception ex)
        {
            App.LogError(ex);   // Debug.WriteLine is compiled out in Release
            Debug.WriteLine($"[Thumb:ContextMenu] ⚠ Show failed: {ex.Message}");
            NoteMenu(false);
        }
    }

    private void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e) => NoteMenu(false);

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

    // IDEAS #13: while the thumbnail is enlarged for an alert, all four are its OWN place and size, read and written.
    // A written place moves the enlarged window with it (StandAround); a written size is the thumbnail's own (Resize).
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new double Left
    {
        get => base.Left + (_alertGrown ? _alertDx : 0);
        set
        {
            if (_alertGrown) StandAround(new System.Drawing.Rectangle((int)value, base.Top + _alertDy, _baseWidth, _baseHeight));
            else base.Left = (int)value;
        }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new double Top
    {
        get => base.Top + (_alertGrown ? _alertDy : 0);
        set
        {
            if (_alertGrown) StandAround(new System.Drawing.Rectangle(base.Left + _alertDx, (int)value, _baseWidth, _baseHeight));
            else base.Top = (int)value;
        }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new double Width
    {
        get => _alertGrown ? _baseWidth : base.Width;
        set
        {
            if (_alertGrown) Resize((int)value, _baseHeight);
            else base.Width = (int)value;
        }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new double Height
    {
        get => _alertGrown ? _baseHeight : base.Height;
        set
        {
            if (_alertGrown) Resize(_baseWidth, (int)value);
            else base.Height = (int)value;
        }
    }
    // Tracks the intended topmost state. We cannot use the WinForms TopMost
    // property as the source of truth because its setter calls SetWindowPos
    // WITHOUT SWP_NOACTIVATE, which activates the thumbnail and steals
    // foreground from whatever had it (notably the Settings UI). All reads
    // and writes to topmost state must go through this field + SetTopmostNoActivate.
    private bool _isTopmost;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Topmost
    {
        get => _isTopmost;
        set => SetTopmost(value);
    }
    public bool IsVisible => Visible;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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
    // Double-click messages MUST be swallowed like the single-click ones (#95).
    // We handle the button up/down pairs ourselves and never call base.WndProc for
    // them. If a DBLCLK reaches base, WinForms treats it as a mouse-down and sets
    // Capture = true — and because our WM_*BUTTONUP case returns early, base never
    // runs the matching mouse-up that would release it. The window then holds mouse
    // capture forever, receiving every click on screen, so whichever thumbnail was
    // double-clicked "locks" and every later click switches to that same client.
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDBLCLK = 0x0206;
    private const int WM_SIZE = 0x0005;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    /// <summary>Layout of the MINMAXINFO Windows passes with WM_GETMINMAXINFO.
    /// ptMinTrackSize is the floor Windows enforces on a top-level window's size.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public User32.POINT ptReserved;
        public User32.POINT ptMaxSize;
        public User32.POINT ptMaxPosition;
        public User32.POINT ptMinTrackSize;
        public User32.POINT ptMaxTrackSize;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            // Windows applies a default MINIMUM TRACKING SIZE (SM_CXMINTRACK /
            // SM_CYMINTRACK — roughly 130x30) to every top-level window. Below that
            // the OS silently keeps the window bigger than we asked for, so a small
            // thumbnail ends up with invisible padding around its rendered content —
            // which is why snapping appeared to stop at a fixed edge instead of the
            // true one: it was aligning the oversized WINDOW, not what you can see.
            // Overriding the floor to 1x1 lets the window actually be as small as
            // requested, so edges snap flush at any size.
            case WM_GETMINMAXINFO:
                base.WndProc(ref m);
                try
                {
                    var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                    mmi.ptMinTrackSize.X = 1;
                    mmi.ptMinTrackSize.Y = 1;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, m.LParam, false);
                }
                catch { }
                return;
            // IDEAS #13: while the thumbnail is enlarged for an alert, only its own place takes the mouse. A point on
            // the part that grew answers "not me": Windows then asks the window under it if that is one of ours (a
            // neighbouring thumbnail), and otherwise the click is nobody's. So a thumbnail that grows under the
            // pointer can't switch clients by accident. Not enlarged: one read, and the window answers as it always did.
            case WM_NCHITTEST:
                if (_alertGrown && !AlertEnlarge.InOwnPlace(OwnPlace(), (short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)))
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
                break;
            case WM_RBUTTONDOWN:
            case WM_RBUTTONDBLCLK:
                OnRightButtonDown();
                return;
            case WM_RBUTTONUP:
                OnRightButtonUp();
                return;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
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

    /// <summary>Safety net for #95. A hovered thumbnail is enlarged by HoverScale and
    /// deliberately overlaps its neighbours. UnHover only runs from OnMouseLeave, so if
    /// WinForms ever misses that leave (window resized/moved out from under the cursor,
    /// capture quirks, the enlarged window swallowing the cursor), the thumbnail stays
    /// big and every click aimed at the thumbnails underneath lands on IT instead —
    /// which looks like "clicking any thumbnail always switches to the same client".
    /// Cheap poll: if we think we're hovered but the cursor isn't inside us, undo it.</summary>
    public void EnsureHoverStateValid()
    {
        if (!_isHovered) return;
        if (base.Bounds.Contains(Cursor.Position)) return;
        _isMouseOver = false;
        UnHover();
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

    // ── Enlarged for an alert (IDEAS #13) ─────────────────────────

    private double _alertScale = 1.0;   // what the manager asked for; 1.0 = its own size
    private bool _alertGrown;           // the window is enlarged for an alert right now
    private int _alertDx, _alertDy;     // how far the enlarged window stands left of and above the thumbnail's own place

    /// <summary>The thumbnail is enlarged for an alert right now.</summary>
    public bool IsAlertEnlarged => _alertGrown;

    /// <summary>The thumbnail was asked to be enlarged and has not been asked back. It need not be enlarged right
    /// now: a drag, the pointer on it, the hover zoom or an open menu makes it wait.</summary>
    public bool IsAlertEnlargementAsked => _alertScale > 1.0;

    /// <summary>The work area of the monitor a rectangle is on. A test hands in a monitor of its own.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<System.Drawing.Rectangle, System.Drawing.Rectangle> WorkAreaFor { get; set; } =
        own => System.Windows.Forms.Screen.FromRectangle(own).WorkingArea;

    /// <summary>The manager's word (AlertEnlarge.Tick): enlarge to this scale, or 1.0 to go back. It is done now if
    /// nothing else has the thumbnail, and otherwise when the drag ends or the pointer leaves.</summary>
    public void SetAlertScale(double scale)
    {
        _alertScale = scale > 1.0 ? scale : 1.0;
        FollowAlertScale();
    }

    /// <summary>The thumbnail's own place on the screen while it is enlarged.</summary>
    private System.Drawing.Rectangle OwnPlace() => new(base.Left + _alertDx, base.Top + _alertDy, _baseWidth, _baseHeight);

    private static int _openMenus;      // how many thumbnails have their right-click menu open; the window's thread only
    private bool _menuOpen;             // this one has
    // The thumbnails that are enlarged for an alert right now: what an opening menu puts back (RD-I1). The window's
    // thread only. A thumbnail is listed exactly while _alertGrown is true, and never after its Cleanup.
    private static readonly System.Collections.Generic.List<ThumbnailWindow> _enlargedNow = new();

    /// <summary>Some thumbnail's right-click menu is open (T6-I1, RD-I1). No thumbnail is enlarged meanwhile: an
    /// enlarged picture over a menu hides its items, and the part that grew lets a click fall through to them
    /// ("Close client" ends the game client without asking). None grows, those that were enlarged are put back as
    /// the menu opens, and none is raised as an enlarged one.</summary>
    public static bool MenuOpen => _openMenus > 0;

    /// <summary>This thumbnail's right-click menu opens (said before it is shown), or has closed. Said twice it
    /// counts once. When it opens, EVERY thumbnail that is enlarged for an alert goes back to its own size and
    /// place, at once (RD-I1): this one also where the press did not put it back (with "Lock positions" a
    /// right-click starts no drag), and a neighbour that was enlarged before. The manager's tick then tells them
    /// to go back and, when no menu is open any more, to grow again. When it has closed, this thumbnail is looked
    /// at again: it may still be asked to be enlarged. The window says this around its own menu; a test says it in
    /// the menu's place, because no test shows a menu.</summary>
    public void NoteMenu(bool open)
    {
        if (!CountMenu(open)) return;
        if (open) PutEnlargedBackForAMenu();
        else FollowAlertScale();
    }

    /// <summary>Counts this thumbnail's menu as open or closed, once however often it is said. False when it was
    /// counted so already.</summary>
    private bool CountMenu(bool open)
    {
        if (_menuOpen == open) return false;
        _menuOpen = open;
        _openMenus += open ? 1 : -1;
        return true;
    }

    /// <summary>Every thumbnail that is enlarged for an alert goes back, now. Each in a try of its own: one that
    /// Windows refuses to move must not cost the others their going back, nor the menu its opening. Such a
    /// thumbnail still counts as enlarged (it is not raised while a menu is open), and the manager's tick asks it
    /// back again within its 50 ms and reports the refusal by its type.</summary>
    private static void PutEnlargedBackForAMenu()
    {
        foreach (var thumb in _enlargedNow.ToArray())
        {
            if (thumb.IsDisposed)
            {
                _enlargedNow.Remove(thumb);
                continue;
            }
            try { thumb.ShrinkFromAlert(); }
            catch (Exception) { /* still enlarged, and it says so: the tick asks it back again and reports */ }
        }
    }

    /// <summary>Whether this thumbnail's right-click menu is on the screen. A test answers in the menu's place,
    /// because no test shows one.</summary>
    protected virtual bool MenuShown => _menu?.Strip.Visible == true;

    /// <summary>Safety net for the count of open menus (RD-m4), run for every thumbnail at each focus sweep, beside
    /// the hover zoom's: a menu that is counted as open while it is not on the screen is counted closed. If the
    /// menu's "closed" were ever lost, the count would stay up and no thumbnail would be enlarged until that
    /// thumbnail's next menu or until its client went. One read while its menu is not counted.</summary>
    public void EnsureMenuStateValid()
    {
        if (_menuOpen && !MenuShown) NoteMenu(false);
    }

    /// <summary>Whether raising the thumbnails is held off right now. Set by ThumbnailManager: while the settings
    /// window is in front it raises no thumbnail (SetSuppressTopmost), and an enlarged one is not raised either.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? RaiseSuppressed { get; set; }

    /// <summary>Puts this thumbnail above its neighbours if it is enlarged for an alert: when it grows, and on the
    /// manager's word behind every loop in which the manager raised all thumbnails (the neighbours raised behind
    /// this one in such a loop cover most of what the enlargement added). Only while the thumbnails are in the
    /// topmost band, which is "always on top", or "keep thumbnails above clients" while a client is in front: with
    /// both off an enlarged thumbnail is never raised and stands where it stood among the other windows. And not
    /// while raising is held off, and not while a thumbnail's right-click menu is open (RD-I1): no thumbnail is
    /// enlarged then, and one that Windows refused to put back must not be lifted over the menu. One read while it
    /// is not enlarged.</summary>
    public void RaiseWhileEnlarged()
    {
        if (_alertGrown && _isTopmost && !MenuOpen && RaiseSuppressed?.Invoke() != true) RaiseAboveNeighbours();
    }

    /// <summary>The raise itself: the window's own BringToFront, which is raw SetWindowPos without activation, as
    /// every z-order change of a thumbnail is. A test counts it here: a window without a handle has nothing to raise.</summary>
    protected virtual void RaiseAboveNeighbours() => BringToFront();

    private void FollowAlertScale()
    {
        bool wanted = AlertEnlarge.Wanted(_alertScale, _dragMode != DragMode.None, _isHovered, _isMouseOver, _alertGrown);
        if (wanted == _alertGrown) return;
        if (wanted)
        {
            if (!MenuOpen) GrowForAlert();   // T6-I1: never over an open menu; NoteMenu and the manager's tick ask again
            return;
        }
        ShrinkFromAlert();
        // T6-m13: asked back with the pointer on it. The hover zoom was held off while the thumbnail was enlarged,
        // and no "the pointer came" follows by itself: it is said once more, and the hover zoom begins as it does
        // when the pointer comes.
        if (_isMouseOver) OnMouseEnter(EventArgs.Empty);
    }

    private void GrowForAlert()
    {
        if (StandAround(new System.Drawing.Rectangle(base.Left, base.Top, _baseWidth, _baseHeight))) RaiseWhileEnlarged();
    }

    /// <summary>Stands the window, enlarged, around a place of the thumbnail's own: where AlertEnlarge.Place puts it
    /// on the monitor that place is on. So a thumbnail that is moved while it is enlarged (T6-m6) is fitted to the
    /// monitor again, as it was when it grew. False when there is no room to grow at that place: the window then
    /// stands at the place itself, at its own size. The window first, the note of it behind: if Windows refuses
    /// the move, what the manager reads (and saves) stays true of the window. That holds for a refusal that throws
    /// and for a move Windows ignores without a word (RD-m3): a window that stands exactly where it stood has not
    /// moved, so nothing is noted, and it is still what it was (not enlarged, or enlarged around its old place).</summary>
    private bool StandAround(System.Drawing.Rectangle own)
    {
        var grown = AlertEnlarge.Place(own, _alertScale, WorkAreaFor(own));
        bool enlarged = grown != own;
        if (!enlarged && !_alertGrown) return false;   // no room to grow on this monitor, and nothing to undo
        var before = base.Bounds;
        bool resized = grown.Size != base.Size;
        base.SetBounds(grown.Left, grown.Top, grown.Width, grown.Height);
        if (grown != before && base.Bounds == before) return _alertGrown;   // RD-m3: ignored without a word
        _alertDx = own.Left - grown.Left;
        _alertDy = own.Top - grown.Top;
        _alertGrown = enlarged;
        _enlargedNow.Remove(this);
        if (enlarged) _enlargedNow.Add(this);
        if (resized) UpdateThumbnailSize();   // a move alone leaves the picture as it is
        _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
        return enlarged;
    }

    private void ShrinkFromAlert()
    {
        if (!_alertGrown) return;
        var own = OwnPlace();
        // The window first, the note of it behind (T6-m1): a window Windows refuses to move still stands enlarged,
        // so it still counts as enlarged. It then still answers with its own place, and the manager's watchdog
        // asks it back again. The same when Windows ignores the move without a word (RD-m3): the window stands
        // where it stood, enlarged.
        var before = base.Bounds;
        base.SetBounds(own.Left, own.Top, own.Width, own.Height);
        if (own != before && base.Bounds == before) return;
        _alertGrown = false;
        _enlargedNow.Remove(this);
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
        ShrinkFromAlert();   // IDEAS #13: a drag starts from the thumbnail's own size and place

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
            // A plain click MUST land inside this thumbnail (#95). Logs showed
            // button-up arriving at a thumbnail ~800px from the cursor (cursor
            // y=535 vs rect y=1320..1440), so every click switched to that one
            // client no matter which thumbnail was aimed at. Whatever routes the
            // stray message (stuck mouse capture, stale hit-test), attributing a
            // click to a window the cursor isn't over is always wrong.
            // Resize keeps its early-return above: dragging outside is legitimate.
            // While the thumbnail is enlarged for an alert (IDEAS #13, T6-m8) "inside" is its OWN place: the part
            // that grew takes no click (WM_NCHITTEST), so a click there is as stray as one beside the window.
            if (!(_alertGrown ? OwnPlace() : base.Bounds).Contains(Cursor.Position))
            {
                // A stray click means this window is receiving mouse input it should
                // not be — the known cause is stuck mouse capture (#95). Drop it, so
                // the app recovers instead of staying wedged until restart.
                if (base.Capture) base.Capture = false;

                Services.DiagnosticsService.LogWindowHook(
                    $"[Thumb:Click] IGNORED stray click for '{CharacterName}' " +
                    $"cursor={Cursor.Position.X},{Cursor.Position.Y} " +
                    $"rect={base.Left},{base.Top},{base.Width}x{base.Height}" +
                    (_alertGrown ? " (enlarged for an alert)" : ""));
                return;
            }

            // Modifier+click actions are user-chosen (#115): Ctrl is also EVE's lock-target
            // key, so a pilot holding it who clicked a thumbnail minimized the client.
            var gs = Services.DiagnosticsService.GlobalSettings;
            int action = User32.IsKeyDown(User32.VK_LCONTROL) ? gs?.CtrlClickAction ?? 1
                : User32.IsKeyDown(User32.VK_LSHIFT) || User32.IsKeyDown(User32.VK_RSHIFT) ? gs?.ShiftClickAction ?? 2
                : 0;
            if (action == 1)
                MinimizeRequested?.Invoke(this);
            else if (action == 2)
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
            if (!User32.IsKeyDown(User32.VkSecondaryButton))
            {
                OnRightButtonUp();
                return;
            }

            if (User32.IsKeyDown(User32.VkPrimaryButton))
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
            if (!User32.IsKeyDown(User32.VkPrimaryButton) || !User32.IsKeyDown(User32.VkSecondaryButton))
            {
                StopDrag();
                SavePosition();
                return;
            }

            int dx = mouse.X - _dragStartScreen.X;
            int dy = mouse.Y - _dragStartScreen.Y;

            var (newW, newH) = ResizedTo(_resizeStartWidth, _resizeStartHeight, dx, dy, ResizeFloor);

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
        FollowAlertScale();   // IDEAS #13: the drag is over
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

    // ── Hover zoom ───────────────────────────────────────────────

    /// <summary>When true, hovering bumps the thumbnail to 100% opacity and
    /// restores the previous opacity on leave. Drives issue #37.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
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

        if (_hoverScale <= 1.0 || _dragMode != DragMode.None || _alertGrown) return;

        _hoverTimer?.Stop();
        _hoverTimer?.Dispose();
        _hoverTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            if (!_isMouseOver || _alertGrown) return;

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
        FollowAlertScale();   // IDEAS #13
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
    public void HideSessionTimer() => _textOverlay?.HideSessionTimer();
    public void UpdateProcessStats(string? text) => _textOverlay?.UpdateProcessStats(text);
    public void UpdateFpsStats(double fps, bool visible) => _textOverlay?.UpdateFpsStats(fps, visible);
    public void SetProcessStatsTextSize(double fontSize) => _textOverlay?.SetProcessStatsTextSize(fontSize);
    public void SetAlertBadge(int count, string colorHex) => _textOverlay?.SetAlertBadge(count, colorHex);
    public void SetAlertText(string? text, WpfColor color) => _textOverlay?.SetAlertText(text, color);
    public void SetAlertTextPosition(string position) => _textOverlay?.SetAlertTextPosition(position);

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
    public void SetTextStyle(string fontFamily, double fontSize, WpfColor color, double fpsFontSizePt = 0)
        => _textOverlay?.SetTextStyle(fontFamily, fontSize, color, fpsFontSizePt);
    public void SetTextMargins(int marginX, int marginY, int fpsMarginX = -1, int fpsMarginY = -1)
        => _textOverlay?.SetTextMargins(marginX, marginY, fpsMarginX, fpsMarginY);

    // ── Position/Size ────────────────────────────────────────────

    // 'Resize' hides Control.Resize event — ThumbnailManager invokes this as a method.
    public new void Resize(int width, int height)
    {
        // IDEAS #13: a new size is the thumbnail's own. An enlarged one goes back, takes it, and is enlarged again.
        bool enlarged = _alertGrown;
        if (enlarged) ShrinkFromAlert();
        _baseWidth = width;
        _baseHeight = height;
        if (!_isHovered)
        {
            base.Width = width;
            base.Height = height;
            UpdateThumbnailSize();
            _textOverlay?.SyncPositionPhysical(base.Left, base.Top, base.Width, base.Height);
        }
        if (enlarged) FollowAlertScale();
    }

    public void MoveTo(int x, int y)
    {
        // IDEAS #13: x and y are the thumbnail's own place. An enlarged window goes with it, and is fitted again to
        // the monitor that place is on (T6-m6): Reset positions or a preset must not leave it off the screen.
        if (_alertGrown)
        {
            StandAround(new System.Drawing.Rectangle(x, y, _baseWidth, _baseHeight));
            return;
        }
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
        // IDEAS #13 (T6-m2): a window that is closing is asked back first. StopDrag below asks "enlarge now?", and a
        // thumbnail that was told to grow during a drag would grow, and be raised, on its way out.
        _alertScale = 1.0;
        // RD-m7: the end of the drag is also the go-back of a window that closes enlarged, and Windows can refuse
        // that. A refusal must not skip what follows: the timers, the menu's count and the DWM thumbnail.
        try
        {
            StopDrag();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Thumb:Cleanup] the way back from an alert's enlargement failed: {ex.GetType().Name}");
        }
        _enlargedNow.Remove(this);   // a window that is gone is not asked back by the next menu

        _hoverTimer?.Stop();
        _hoverTimer?.Dispose();
        _hoverTimer = null;

        _underFirePulseTimer?.Stop();
        _underFirePulseTimer?.Dispose();
        _underFirePulseTimer = null;

        // IDEAS #13 (T6-I1): a window that goes while its menu is open takes its count with it, or no thumbnail
        // would grow again. Only the count (RD-m7): a window that goes is not looked at again for an enlargement.
        CountMenu(false);
        _menu?.Dispose();
        _menu = null;

        if (_thumbId != IntPtr.Zero)
        {
            DwmApi.UnregisterThumbnail(_thumbId);
            _thumbId = IntPtr.Zero;
        }

        _textOverlay?.Close();
        _textOverlay = null;
    }
}
