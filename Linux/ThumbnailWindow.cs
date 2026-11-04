using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>How a thumbnail is drawn this frame, worked out from the settings.</summary>
public sealed record ThumbnailLook(IBrush Border, double BorderThickness, string Label, bool ShowLabel,
    IBrush TextBrush, double TextSize, string Font, double Opacity, bool Topmost);

/// <summary>One client's live thumbnail. Left click switches to the client; right drag moves the thumbnail and
/// right drag with the left button pressed too resizes it (the Windows app's gestures), unless positions are locked.</summary>
public sealed class ThumbnailWindow : Window
{
    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _name;
    private readonly Border _mark;
    private readonly TextBlock _markText;
    private readonly Border _badge;
    private WriteableBitmap _bitmap;
    private ThumbnailLook? _look;
    private Gesture _mode = Gesture.None;

    public EveClient Client { get; }
    public bool CanDrag { get; set; } = true;
    /// <summary>The smallest size a resize gives (ThumbnailManager.ResizeFloor).</summary>
    public (int Width, int Height) ResizeFloor { get; set; } = (80, 50);
    /// <summary>A resize keeps the thumbnail's own width-to-height ratio (LockResizeAspect).</summary>
    public bool LockResizeAspect { get; set; }
    public event Action<EveClient>? Clicked;
    /// <summary>The thumbnail was moved by the player (not by the app placing it).</summary>
    public event Action<ThumbnailWindow>? Moved;
    /// <summary>The player let go of a resize: the new size is to be saved.</summary>
    public event Action<ThumbnailWindow>? ResizeEnded;
    /// <summary>The thumbnail has a new size while the player resizes it, and Ctrl is not held: the others follow
    /// (the Windows app's ResizeAll). Raised on every step of the drag.</summary>
    public event Action<ThumbnailWindow, int, int>? ResizeAll;
    /// <summary>A plain right-click: the right button went down and up again without the thumbnail being dragged past the
    /// threshold and without a resize (the Windows app's rule, also while positions are locked). The menu is to open.</summary>
    public event Action<ThumbnailWindow>? MenuRequested;

    /// <summary>How far the mouse moves, in pixels on either axis, before a right drag moves the thumbnail (the Windows app's 3).</summary>
    public const int DragThreshold = 3;

    /// <summary>A character's second thumbnail (PiP): its own place, size and opacity, no border, no alert enlargement.</summary>
    public bool IsSecond { get; }

    /// <summary>A crop window's id (CropDefinition.Id), or null for a thumbnail. A crop window shows <see cref="Area"/> of
    /// its client.</summary>
    public string? CropId { get; }

    /// <summary>The part of the client shown (a crop window's source, client pixels); null: the whole client.</summary>
    public EveClients.Area? Area { get; set; }

    /// <summary>The player is moving or resizing the window right now.</summary>
    public bool Busy => _mode != Gesture.None;

    public ThumbnailWindow(EveClient client, PixelRect rect, bool second = false, string? cropId = null)
    {
        Client = client;
        IsSecond = second;
        CropId = cropId;
        Title = TitleOf(client);
        Width = rect.Width;
        Height = rect.Height;
        Position = rect.Position;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;   // a thumbnail shown again (hidden while its client was in front) must not take the focus from the client
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        // See-through at the thumbnail opacity, as Windows' layered windows are: a transparent window that the desktop's
        // compositor blends. Without a compositor the see-through parts are black, as an opaque window would be.
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;   // its right-click menu is dark, as the settings window is

        _bitmap = new WriteableBitmap(new PixelSize(rect.Width, rect.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        _name = new TextBlock
        {
            Margin = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _markText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _mark = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x8B, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _markText,
            IsVisible = false,
        };
        // The cycle-exclusion badge (the Windows TextOverlayWindow's ExclusionBadge): a red disc with ✕ at one of nine places.
        _badge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0xCC, 0, 0)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1), IsVisible = false, IsHitTestVisible = false,
            Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _frame = new Border { Child = new Panel { Children = { _image, _name, _mark, _badge } } };
        Content = _frame;

        // Right drag moves the thumbnail by itself (pointer captured), not through the window manager's move, so it
        // behaves the same on every desktop; the new place is reported once, on release. As on Windows, pressing the
        // left button as well (in either order) turns the drag into a resize from the top left corner: the size is the
        // size at that moment plus the mouse's movement since, and it is reported when either button is let go.
        // Avalonia reports a second button pressed or released while another is down as a pointer move (only the first
        // press and the last release are PointerPressed / PointerReleased), so the buttons are also read in PointerMoved.
        // And once the window has been resized under the pointer, Avalonia's picture of the buttons and the motion
        // events cannot be trusted any more (it reports a button as let go that is still down), so a resize is followed
        // the way the Windows app follows it (its 16 ms timer): the pointer, its buttons and Ctrl are asked of the X
        // server every 16 ms (PointerSource), and the resize ends when that says a button is up.
        PixelPoint from = default, startPosition = default;
        int startWidth = 0, startHeight = 0;
        IPointer? grabbed = null;
        bool menuArmed = false, dragging = false;   // a right press that may still be a plain click; a move past the threshold
        DispatcherTimer resizeTimer = null!;
        resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (_mode != Gesture.Resize) return;
            if (PointerSource?.Invoke() is not { } p || (p.Mask & BothButtons) != BothButtons) End();
            else Resize(p.X, p.Y, (p.Mask & ControlKey) != 0);
        });
        Closed += (_, _) => resizeTimer.Stop();
        void Begin(PointerEventArgs e, Gesture gesture)
        {
            var pressed = this.PointToScreen(e.GetPosition(this));   // before the window moves back to its own place
            _mode = gesture;
            dragging = false;
            if (gesture == Gesture.Resize) menuArmed = false;
            // A drag or resize starts from the thumbnail's own size and place (IDEAS #13), never from the enlarged or zoomed one.
            // Read before going back: Position reads the old place until the X server has moved the window.
            var own = OwnRectangle();
            UnZoom();
            Shrink();
            from = pressed;
            startPosition = new PixelPoint(own.X, own.Y);
            (startWidth, startHeight) = (own.Width, own.Height);
            grabbed = e.Pointer;
            grabbed.Capture(_frame);
            if (PointerSource?.Invoke() is { } p) from = new PixelPoint(p.X, p.Y);
            if (gesture == Gesture.Resize) resizeTimer.Start();
        }
        void End()
        {
            var ended = _mode;
            _mode = Gesture.None;
            resizeTimer.Stop();
            grabbed?.Capture(null);
            if (ended == Gesture.Resize) ResizeEnded?.Invoke(this);
            else if (ended == Gesture.Move && dragging && Position != startPosition) Moved?.Invoke(this);
        }
        void Resize(int x, int y, bool ctrl)
        {
            var (w, h) = ResizedTo(startWidth, startHeight, x - from.X, y - from.Y, ResizeFloor,
                LockResizeAspect ? RatioOf(startWidth, startHeight) : null);
            if (w == (int)Width && h == (int)Height) return;
            SetSize(w, h);
            if (!ctrl) ResizeAll?.Invoke(this, w, h);
        }
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            switch (point.Properties.PointerUpdateKind)
            {
                case PointerUpdateKind.RightButtonPressed when _mode == Gesture.None:
                    menuArmed = !point.Properties.IsLeftButtonPressed;
                    if (CanDrag) Begin(e, point.Properties.IsLeftButtonPressed ? Gesture.Resize : Gesture.Move);
                    break;
                case PointerUpdateKind.LeftButtonPressed when _mode == Gesture.Move:
                    Begin(e, Gesture.Resize);
                    break;
                case PointerUpdateKind.LeftButtonPressed when _mode == Gesture.None:
                    // While enlarged only the thumbnail's own place takes the click: the part that grew is not this thumbnail (AlertEnlarge.InOwnPlace).
                    var at = PointerSource?.Invoke() is { } xp ? new PixelPoint(xp.X, xp.Y) : this.PointToScreen(e.GetPosition(this));
                    ClickModifiers = e.KeyModifiers;   // Ctrl / Shift + click: the player's own click actions (ThumbnailManager)
                    if (!_grown || AlertEnlarge.InOwnPlace(OwnRectangle(), at.X, at.Y)) Clicked?.Invoke(Client);
                    break;
            }
        };
        PointerMoved += (_, e) =>
        {
            var props = e.GetCurrentPoint(this).Properties;
            bool both = props.IsLeftButtonPressed && props.IsRightButtonPressed;
            if (_mode == Gesture.None)
            {
                if (both) menuArmed = false;
                if (both && CanDrag) Begin(e, Gesture.Resize);   // the second button went down while the first was held
                return;
            }
            if (_mode == Gesture.Move && both) { Begin(e, Gesture.Resize); return; }
            // The X server's pointer when it answers: Avalonia's position is relative to where it last saw the window, which is
            // wrong once the window moved under the pointer (a zoom or an alert enlargement ended as the drag began).
            var now = PointerSource?.Invoke() is { } xp ? new PixelPoint(xp.X, xp.Y) : this.PointToScreen(e.GetPosition(this));
            if (_mode == Gesture.Move)
            {
                int dx = now.X - from.X, dy = now.Y - from.Y;
                if (!dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold)) (dragging, menuArmed) = (true, false);
                if (dragging) Position = new PixelPoint(startPosition.X + dx, startPosition.Y + dy);
            }
            else if (PointerSource == null)   // no timer: follow the events
            {
                if (!both) End();
                else Resize(now.X, now.Y, (e.KeyModifiers & KeyModifiers.Control) != 0);
            }
        };
        PointerReleased += (_, e) =>
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (_mode == Gesture.Resize && PointerSource == null && kind is PointerUpdateKind.LeftButtonReleased or PointerUpdateKind.RightButtonReleased) End();
            else if (_mode == Gesture.Move && kind == PointerUpdateKind.RightButtonReleased) End();
            if (kind == PointerUpdateKind.RightButtonReleased && menuArmed)
            {
                menuArmed = false;
                MenuRequested?.Invoke(this);
            }
        };
    }

    private const ushort BothButtons = 0x100 | 0x400, ControlKey = 0x04;   // the X server's Button1Mask | Button3Mask, ControlMask

    /// <summary>Where the pointer is on the screen and which buttons and modifiers are down (the X server's mask), or null
    /// when that cannot be asked. Set by the app; a resize follows the mouse through it.</summary>
    public Func<(int X, int Y, ushort Mask)?>? PointerSource { get; set; }

    /// <summary>The window title (a window list, a capture program's source list can show it): the character as streamer mode shows it.</summary>
    private string TitleOf(EveClient client) =>
        $"MultiPreview - {StreamerMask.Name(StreamerKind.Character, client.Character)}" + (CropId != null ? " (crop)" : IsSecond ? " (2)" : "");

    private enum Gesture { None, Move, Resize }

    /// <summary>The largest side a resize gives: Win32's own limit, which the Windows app keeps; it also stops an absurd ratio from overflowing.</summary>
    private const int Limit = short.MaxValue;

    /// <summary>The size a resize gives (ThumbnailWindow.ResizedTo on Windows): the start size plus the mouse movement,
    /// never below the floor. With a <paramref name="ratio"/> (width / height) the width follows the mouse and the height
    /// follows the ratio; the axis whose movement is larger wins (dy counts as dy * ratio of width, a tie goes to dx), and a
    /// result under the floor is scaled up, keeping the ratio, until both sides reach it.</summary>
    public static (int Width, int Height) ResizedTo(int startWidth, int startHeight, int dx, int dy, (int Width, int Height) floor, double? ratio = null)
    {
        if (ratio is not { } r || !double.IsFinite(r) || r <= 0)
            return (Math.Min(Math.Max(startWidth + dx, floor.Width), Limit), Math.Min(Math.Max(startHeight + dy, floor.Height), Limit));
        double byY = (double)dy * r, move = Math.Abs(byY) > Math.Abs((double)dx) ? byY : dx;
        double w = Math.Max(startWidth + move, 1), h = w / r;
        double scale = Math.Max(1, Math.Max(floor.Width / w, floor.Height / Math.Max(h, double.Epsilon)));
        return ((int)Math.Min(Math.Max(Math.Round(w * scale), floor.Width), Limit),
                (int)Math.Min(Math.Max(Math.Round(h * scale), floor.Height), Limit));
    }

    /// <summary>The ratio (width / height) of a size, or null for one that has none.</summary>
    public static double? RatioOf(double w, double h) =>
        w > 0 && h > 0 && double.IsFinite(w) && double.IsFinite(h) && double.IsFinite(w / h) && w / h > 0 ? w / h : null;

    /// <summary>The smallest size a resize gives (ThumbnailManager.ResizeFloor): 80 x 50, until the player turns on the
    /// minimum size; then that size, held to 20-2000.</summary>
    public static (int Width, int Height) FloorOf(AppSettings s) =>
        s.ThumbnailMinimumSizeEnabled && s.ThumbnailMinimumSize is { } min
            ? (Math.Clamp((int)Math.Round(min.Width), FloorMin, FloorMax), Math.Clamp((int)Math.Round(min.Height), FloorMin, FloorMax))
            : (80, 50);

    /// <summary>The range the minimum size is held to (ThumbnailManager.MinFloor / MaxFloor).</summary>
    public const int FloorMin = 20, FloorMax = 2000;

    /// <summary>Gives the thumbnail this size and a picture of that size; its place stays. The picture is drawn into the
    /// new bitmap by the next <see cref="Update"/> (EveClients.Grab builds its scaling steps again for the new size). A
    /// thumbnail that is enlarged for an alert gets this as its own size and stands enlarged around it again.</summary>
    public void SetSize(int width, int height)
    {
        UnZoom();   // the next tick zooms it again at its new size while the pointer is still on it
        if (_grown)
        {
            var own = OwnRectangle();
            Stand(new System.Drawing.Rectangle(own.X, own.Y, width, height));
            return;
        }
        if (width == (int)Width && height == (int)Height) return;
        Width = width;
        Height = height;
        var old = _bitmap;
        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;
        old.Dispose();
    }

    // ── Enlarged for an alert (IDEAS #13, the Windows ThumbnailWindow's rules) ──

    private double _alertScale = 1.0;   // what the app asked for; 1.0 = its own size
    private bool _grown;                // the window is enlarged for an alert right now
    private System.Drawing.Rectangle _own;   // the thumbnail's own place and size while it is enlarged

    /// <summary>The thumbnail is enlarged for an alert right now.</summary>
    public bool IsAlertEnlarged => _grown;

    /// <summary>The thumbnail was asked to be enlarged and has not been asked back. It need not be enlarged right now: a
    /// drag or the pointer on it makes it wait.</summary>
    public bool IsAlertEnlargementAsked => _alertScale > 1.0;

    /// <summary>The thumbnail's own place and size, which the settings keep: while it is enlarged, not the enlarged window's.</summary>
    public PixelPoint OwnPosition => OwnRectangle() is var r ? new PixelPoint(r.X, r.Y) : default;
    public int OwnWidth => OwnRectangle().Width;
    public int OwnHeight => OwnRectangle().Height;

    private System.Drawing.Rectangle OwnRectangle() =>
        _grown ? _own : _zoomed ? _zoomOwn : new System.Drawing.Rectangle(Position.X, Position.Y, (int)Width, (int)Height);

    /// <summary>The work area of the screen a rectangle is on, or null when there is none to ask. A test hands in its own.</summary>
    public Func<System.Drawing.Rectangle, System.Drawing.Rectangle?>? WorkAreaFor { get; set; }

    private System.Drawing.Rectangle? WorkArea(System.Drawing.Rectangle own)
    {
        if (WorkAreaFor != null) return WorkAreaFor(own);
        var screen = Screens.ScreenFromPoint(new PixelPoint(own.X + own.Width / 2, own.Y + own.Height / 2)) ?? Screens.Primary;
        if (screen == null) return null;
        var area = screen.WorkingArea;
        return new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height);
    }

    /// <summary>The app's word (AlertEnlarge.Tick): enlarge to this scale, or 1.0 to go back. It is done now if nothing
    /// else has the thumbnail, and otherwise when the drag ends or the pointer leaves (<see cref="Follow"/>).</summary>
    public void SetAlertScale(double scale)
    {
        _alertScale = scale > 1.0 ? scale : 1.0;
        Follow();
    }

    /// <summary>Makes the window match what was asked: enlarged when it is wanted (AlertEnlarge.Wanted: not during a drag
    /// or resize, and never changing under the pointer), its own size otherwise. Asked at every tick, because a drag
    /// that ended or a pointer that left lets a waiting enlargement happen.</summary>
    public void Follow()
    {
        bool over = PointerInside();
        FollowHover(over);
        bool wanted = AlertEnlarge.Wanted(_alertScale, _mode != Gesture.None || MenuOpen, _zoomed, over, _grown);
        if (wanted == _grown) return;
        if (wanted) Stand(new System.Drawing.Rectangle(Position.X, Position.Y, (int)Width, (int)Height));
        else Shrink();
    }

    /// <summary>Stands the window, enlarged, around a place of the thumbnail's own: where AlertEnlarge.Place puts it on
    /// the screen that place is on. A place with no room to grow stands as it is, not enlarged.</summary>
    private void Stand(System.Drawing.Rectangle own)
    {
        var area = WorkArea(own);
        var grown = area is { } a ? AlertEnlarge.Place(own, _alertScale, a) : own;
        bool enlarged = grown != own;
        if (!enlarged && !_grown && own.Size == new System.Drawing.Size((int)Width, (int)Height)) return;
        _own = own;
        _grown = enlarged;
        Fit(grown);
        if (enlarged) RaiseWhileEnlarged?.Invoke(this);
    }

    /// <summary>Back to the thumbnail's own size and place.</summary>
    private void Shrink()
    {
        if (!_grown) return;
        _grown = false;
        Fit(_own);
    }

    /// <summary>Puts the window on its own place and size from the settings (a crop window edited on the settings page).
    /// The caller asks only when the settings changed: Avalonia's Position reads stale right after a move.</summary>
    public void PlaceAt(int x, int y, int width, int height)
    {
        UnZoom();
        Shrink();
        Fit(new System.Drawing.Rectangle(x, y, width, height));
    }

    /// <summary>Puts the window and its picture on this rectangle. Not a move by the player: nothing is reported.</summary>
    private void Fit(System.Drawing.Rectangle r)
    {
        if (r.Width != (int)Width || r.Height != (int)Height)
        {
            Width = r.Width;
            Height = r.Height;
            var old = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
            old.Dispose();
        }
        Position = new PixelPoint(r.X, r.Y);
    }

    // ── Hover zoom and "solid under the mouse" (the Windows ThumbnailWindow's OnMouseEnter / UnHover) ──

    /// <summary>How much a thumbnail grows while the pointer rests on it (HoverScale while ResizeThumbnailsOnHover is on);
    /// 1.0 or less: no zoom.</summary>
    public double HoverScale { get; set; } = 1.0;

    /// <summary>The pointer is on the thumbnail (its window as it is now), as of the last tick.</summary>
    public bool Hovered { get; private set; }

    private bool _zoomed;                        // grown by HoverScale right now
    private System.Drawing.Rectangle _zoomOwn;   // the thumbnail's own place and size while it is zoomed
    private int _overTicks;

    /// <summary>The pointer is inside the window, asked of the X server (Avalonia's own idea of it goes wrong once the
    /// window changes size under the pointer); Avalonia's when there is no X answer.</summary>
    private bool PointerInside()
    {
        if (PointerSource?.Invoke() is not { } p) return IsPointerOver;
        return p.X >= Position.X && p.X < Position.X + (int)Width && p.Y >= Position.Y && p.Y < Position.Y + (int)Height;
    }

    /// <summary>At every tick: zooms once the pointer has rested on the thumbnail for a tick (the Windows app's 100 ms), and
    /// goes back the moment it is off it, a drag or resize starts, or a menu opens. Never while enlarged for an alert.
    /// Polled, so a missed "pointer left" can never leave a thumbnail zoomed over its neighbours (Windows issue #95).</summary>
    private void FollowHover(bool over)
    {
        Hovered = over;
        _overTicks = over ? _overTicks + 1 : 0;
        if (_zoomed)
        {
            if (!over || _mode != Gesture.None || MenuOpen || HoverScale <= 1.0) UnZoom();
            return;
        }
        if (HoverScale > 1.0 && _overTicks >= 2 && _mode == Gesture.None && !MenuOpen && !_grown) Zoom();
    }

    private void Zoom()
    {
        var own = new System.Drawing.Rectangle(Position.X, Position.Y, (int)Width, (int)Height);
        int w = Math.Min((int)(own.Width * HoverScale), Limit), h = Math.Min((int)(own.Height * HoverScale), Limit);
        _zoomOwn = own;
        _zoomed = true;
        Fit(new System.Drawing.Rectangle(own.X - (w - own.Width) / 2, own.Y - (h - own.Height) / 2, w, h));
        RaiseWhileEnlarged?.Invoke(this);
    }

    private void UnZoom()
    {
        if (!_zoomed) return;
        _zoomed = false;
        Fit(_zoomOwn);
    }

    /// <summary>A thumbnail menu is open (any thumbnail's): no thumbnail grows over it until it closes (the Windows app's NoteMenu).</summary>
    public bool MenuOpen { get; set; }

    /// <summary>Asked to put the enlarged window above its neighbours (the app raises it on the X server).</summary>
    public Action<ThumbnailWindow>? RaiseWhileEnlarged { get; set; }

    public void Apply(ThumbnailLook look)
    {
        if (look == _look) return;
        _look = look;
        Title = TitleOf(Client);   // streamer mode may have changed the name
        _frame.BorderBrush = look.Border;
        _frame.BorderThickness = new Thickness(look.BorderThickness);
        _name.Text = look.Label;
        _name.IsVisible = look.ShowLabel;
        _name.Foreground = look.TextBrush;
        _name.FontSize = look.TextSize;
        _name.FontFamily = new FontFamily(look.Font);
        Opacity = look.Opacity;
        Topmost = look.Topmost;
    }

    /// <summary>Whether the window lets the mouse through now (click-through), as last set; null before it was set.</summary>
    public bool? PassesClicks { get; set; }

    /// <summary>The modifiers held at the last left click (read by Clicked's handler).</summary>
    public KeyModifiers ClickModifiers { get; private set; }

    private string? _badgeAt;

    /// <summary>The cycle-exclusion badge (TextOverlayWindow.SetCycleExcluded / SetCycleExclusionPosition): shown while the
    /// character is left out of cycling, at one of the nine anchors (ThumbnailFields.Anchor), 4 px in from the edge.</summary>
    public void SetCycleBadge(bool excluded, string position)
    {
        _badge.IsVisible = excluded;
        if (!excluded || position == _badgeAt) return;
        _badgeAt = position;
        const double inset = 4;
        bool left = position is "TopLeft" or "Left" or "BottomLeft", right = position is "TopRight" or "Right" or "BottomRight";
        bool top = position is "TopLeft" or "Top" or "TopRight", bottom = position is "BottomLeft" or "Bottom" or "BottomRight";
        _badge.HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        _badge.VerticalAlignment = top ? VerticalAlignment.Top : bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        _badge.Margin = new Thickness(left ? inset : 0, top ? inset : 0, right ? inset : 0, bottom ? inset : 0);
    }

    /// <summary>IDEAS #22, #47: what ClientWatch says of the client ("Not responding", "Disconnected") over the middle of the
    /// picture, or null for nothing.</summary>
    public void SetMark(string? text)
    {
        if (text == _markText.Text && _mark.IsVisible == (text != null)) return;
        _markText.Text = text;
        _mark.IsVisible = text != null;
    }

    /// <summary>Copies the client's current picture into the bitmap and shows it. False when the client is gone.</summary>
    public unsafe bool Update(EveClients clients)
    {
        bool ok;
        using (var fb = _bitmap.Lock())
            ok = clients.Grab(Client.Window, (byte*)fb.Address, fb.Size.Width, fb.Size.Height, fb.RowBytes, Area);
        if (ok) _image.InvalidateVisual();
        return ok;
    }
}
