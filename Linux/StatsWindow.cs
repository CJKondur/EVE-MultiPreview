using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>S7: one character's stats overlay (the Windows StatOverlayWindow, keep in sync): the character's name over the
/// numbers StatTrackerService works out from the game logs. Right drag moves it, the left button pressed too resizes it
/// (Ctrl held: only this one, else every stats window follows), unless positions are locked; the place is snapped on release
/// and reported once. It never takes the focus from the game (not activated; the app marks it no-focus on the X server).
/// The app sets its text and look once a second, and only what changed is drawn again.</summary>
public sealed class StatsWindow : Window
{
    /// <summary>The Windows window's size and place before the player gave it one.</summary>
    public const int DefaultWidth = 280, DefaultHeight = 120, DefaultX = 100, DefaultY = 100;

    /// <summary>The smallest size a resize gives (the Windows window's 80 x 40).</summary>
    public const int ResizeMinWidth = 80, ResizeMinHeight = 40;

    private readonly Border _root;
    private readonly TextBlock _header, _numbers;
    private string _style = "";
    private Gesture _mode = Gesture.None;

    /// <summary>The real character name: the key the app finds this window by. The header may show a streamer name instead.</summary>
    public string Character { get; }

    /// <summary>Right drag moves and resizes it (false while positions are locked).</summary>
    public bool CanDrag { get; set; } = true;

    /// <summary>The player is moving or resizing it right now.</summary>
    public bool Busy => _mode != Gesture.None;

    /// <summary>The player let go after a move or a resize: its place and size are to be saved.</summary>
    public event Action<StatsWindow>? PositionAndSizeChanged;

    /// <summary>A resize step without Ctrl: every other stats window takes this size (the Windows ResizeAll).</summary>
    public event Action<StatsWindow, int, int>? ResizeAll;

    /// <summary>Given (x, y, width, height) on release, the place it snaps to (the app's corner snap), or null: none.</summary>
    public Func<int, int, int, int, (int X, int Y)?>? SnapPosition { get; set; }

    /// <summary>The pointer on the screen and the X server's button and modifier mask; a resize follows the mouse through it.</summary>
    public Func<(int X, int Y, ushort Mask)?>? PointerSource { get; set; }

    public StatsWindow(string character, PixelRect rect)
    {
        Character = character;
        Title = "MultiPreview - " + StreamerMask.Name(StreamerKind.Character, character) + " (stats)";
        Width = rect.Width;
        Height = rect.Height;
        Position = rect.Position;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // The Windows window: the header in 9 pt bold #FAC57A, the numbers in a monospace font at the chosen size.
        _header = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xC5, 0x7A)), FontWeight = FontWeight.Bold };
        _numbers = new TextBlock { Text = "0", FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Liberation Mono, monospace"), FontWeight = FontWeight.SemiBold };
        var panel = new StackPanel();
        panel.Children.Add(_header);
        panel.Children.Add(_numbers);
        _root = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xAA)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3),
            ClipToBounds = true,
            Child = panel,
        };
        Content = _root;
        ShowName();
        Gestures();
    }

    /// <summary>The header again (streamer mode switched on or off). Nothing is drawn when it did not change.</summary>
    public void ShowName()
    {
        var name = StreamerMask.Name(StreamerKind.Character, Character);
        if (_header.Text != name) _header.Text = name;
    }

    /// <summary>The numbers' lines (StatTrackerService.GetOverlayText, or the waiting line).</summary>
    public void SetText(string text)
    {
        if (_numbers.Text != text) _numbers.Text = text;
    }

    public string Numbers => _numbers.Text ?? "";

    /// <summary>The look of every stats window (Tools › Stats overlay › Look). The same values again do nothing.</summary>
    public void SetLook(int fontSize, int opacity, string background, string text)
    {
        var style = $"{fontSize}|{opacity}|{background}|{text}";
        if (style == _style) return;
        _style = style;
        double dip = fontSize * (96.0 / 72.0);
        _numbers.FontSize = dip;
        _header.FontSize = dip + 96.0 / 72.0;   // the header one point larger (Windows: Segoe UI 9 pt over Consolas 8 pt)
        Opacity = Math.Clamp(opacity, 0, 255) / 255.0;
        // An unreadable colour keeps the one before, as on Windows; the background is always 0xD0 opaque.
        if (Views.StatOverlayWindow.ParseOverlayColor(background) is { } bg) _root.Background = new SolidColorBrush(Color.FromArgb(0xD0, bg.R, bg.G, bg.B));
        else _root.Background ??= new SolidColorBrush(Color.FromArgb(0xD0, 0x1A, 0x1A, 0x2E));
        if (Views.StatOverlayWindow.ParseOverlayColor(text) is { } fg) _numbers.Foreground = new SolidColorBrush(Color.FromArgb(fg.A, fg.R, fg.G, fg.B));
        else _numbers.Foreground ??= new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x88));
    }

    /// <summary>Its place and size now.</summary>
    public PixelRect Rect => new(Position, new PixelSize((int)Width, (int)Height));

    /// <summary>Another stats window was resized by the player: this one takes its size (not reported, as on Windows).</summary>
    public void SetSize(int width, int height)
    {
        if (width == (int)Width && height == (int)Height) return;
        Width = width;
        Height = height;
    }

    // ── Right drag moves, right + left resizes (ThumbnailWindow's gestures: Avalonia reports a second button as a move, and a
    // resize is followed through the X server's pointer every 16 ms) ──

    private enum Gesture { None, Move, Resize }

    private const ushort BothButtons = 0x100 | 0x400, ControlKey = 0x04;   // Button1Mask | Button3Mask, ControlMask

    private void Gestures()
    {
        PixelPoint from = default, startPosition = default;
        int startWidth = 0, startHeight = 0;
        bool moved = false;
        IPointer? grabbed = null;
        DispatcherTimer resizeTimer = null!;
        resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (_mode != Gesture.Resize) return;
            if (PointerSource?.Invoke() is not { } p || (p.Mask & BothButtons) != BothButtons) End();
            else Resize(p.X, p.Y, (p.Mask & ControlKey) != 0);
        });
        Closed += (_, _) => resizeTimer.Stop();
        PixelPoint Now(PointerEventArgs e) => PointerSource?.Invoke() is { } p ? new PixelPoint(p.X, p.Y) : this.PointToScreen(e.GetPosition(this));
        void Begin(PointerEventArgs e, Gesture gesture)
        {
            _mode = gesture;
            from = Now(e);
            startPosition = Position;
            (startWidth, startHeight) = ((int)Width, (int)Height);
            if (gesture == Gesture.Move) moved = false;
            grabbed = e.Pointer;
            grabbed.Capture(_root);
            if (gesture == Gesture.Resize && PointerSource != null) resizeTimer.Start();
        }
        void End()
        {
            var ended = _mode;
            _mode = Gesture.None;
            resizeTimer.Stop();
            grabbed?.Capture(null);
            // As on Windows: a move snaps on release (only one past the threshold), and either gesture is reported.
            if (moved && SnapPosition?.Invoke(Position.X, Position.Y, (int)Width, (int)Height) is { } snap) Position = new PixelPoint(snap.X, snap.Y);
            if (ended != Gesture.None) PositionAndSizeChanged?.Invoke(this);
        }
        void Resize(int x, int y, bool ctrl)
        {
            int w = Math.Max(ResizeMinWidth, startWidth + x - from.X), h = Math.Max(ResizeMinHeight, startHeight + y - from.Y);
            if (w == (int)Width && h == (int)Height) return;
            Width = w;
            Height = h;
            if (!ctrl) ResizeAll?.Invoke(this, w, h);
        }
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (!CanDrag) return;
            if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed && _mode == Gesture.None)
                Begin(e, point.Properties.IsLeftButtonPressed ? Gesture.Resize : Gesture.Move);
            else if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && _mode == Gesture.Move)
                Begin(e, Gesture.Resize);
        };
        PointerMoved += (_, e) =>
        {
            var props = e.GetCurrentPoint(this).Properties;
            bool both = props.IsLeftButtonPressed && props.IsRightButtonPressed;
            if (_mode == Gesture.None) return;
            if (_mode == Gesture.Move && both) { Begin(e, Gesture.Resize); return; }
            var now = Now(e);
            if (_mode == Gesture.Move)
            {
                int dx = now.X - from.X, dy = now.Y - from.Y;
                if (!moved && (Math.Abs(dx) > ThumbnailWindow.DragThreshold || Math.Abs(dy) > ThumbnailWindow.DragThreshold)) moved = true;
                if (moved) Position = new PixelPoint(startPosition.X + dx, startPosition.Y + dy);
            }
            else if (PointerSource == null)
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
        };
    }

    /// <summary>The Windows corner snap (ThumbnailManager's SnapPosition delegate, AHK _SnapStatWindow: keep in sync): the
    /// nearest pair of corners, one of the window and one of a target (other stats windows, the thumbnails), within
    /// <paramref name="range"/> pixels moves the window so they meet; else null.</summary>
    public static (int X, int Y)? SnapCorner(int x, int y, int w, int h, IEnumerable<PixelRect> targets, int range)
    {
        var mine = new[] { (x, y), (x + w, y), (x, y + h), (x + w, y + h) };
        bool[] isRight = { false, true, false, true }, isBottom = { false, false, true, true };
        double best = range + 1;
        (int X, int Y)? to = null;
        foreach (var t in targets)
        {
            var theirs = new[] { (t.X, t.Y), (t.X + t.Width, t.Y), (t.X, t.Y + t.Height), (t.X + t.Width, t.Y + t.Height) };
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double dx = mine[i].Item1 - theirs[j].Item1, dy = mine[i].Item2 - theirs[j].Item2, dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= range && dist < best)
                    {
                        best = dist;
                        to = (theirs[j].Item1 - (isRight[i] ? w : 0), theirs[j].Item2 - (isBottom[i] ? h : 0));
                    }
                }
        }
        return to;
    }
}
