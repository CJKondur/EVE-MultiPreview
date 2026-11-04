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
    private WriteableBitmap _bitmap;
    private ThumbnailLook? _look;

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

    public ThumbnailWindow(EveClient client, PixelRect rect)
    {
        Client = client;
        Title = TitleOf(client);
        Width = rect.Width;
        Height = rect.Height;
        Position = rect.Position;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;   // a thumbnail shown again (hidden while its client was in front) must not take the focus from the client
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Black;

        _bitmap = new WriteableBitmap(new PixelSize(rect.Width, rect.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        _name = new TextBlock
        {
            Margin = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _frame = new Border { Child = new Panel { Children = { _image, _name } } };
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
        var mode = Gesture.None;
        PixelPoint from = default, startPosition = default;
        int startWidth = 0, startHeight = 0;
        IPointer? grabbed = null;
        DispatcherTimer resizeTimer = null!;
        resizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (mode != Gesture.Resize) return;
            if (PointerSource?.Invoke() is not { } p || (p.Mask & BothButtons) != BothButtons) End();
            else Resize(p.X, p.Y, (p.Mask & ControlKey) != 0);
        });
        Closed += (_, _) => resizeTimer.Stop();
        void Begin(PointerEventArgs e, Gesture gesture)
        {
            mode = gesture;
            from = this.PointToScreen(e.GetPosition(this));
            startPosition = Position;
            (startWidth, startHeight) = ((int)Width, (int)Height);
            grabbed = e.Pointer;
            grabbed.Capture(_frame);
            if (gesture != Gesture.Resize) return;
            if (PointerSource?.Invoke() is { } p) from = new PixelPoint(p.X, p.Y);
            resizeTimer.Start();
        }
        void End()
        {
            var ended = mode;
            mode = Gesture.None;
            resizeTimer.Stop();
            grabbed?.Capture(null);
            if (ended == Gesture.Resize) ResizeEnded?.Invoke(this);
            else if (ended == Gesture.Move && Position != startPosition) Moved?.Invoke(this);
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
                case PointerUpdateKind.RightButtonPressed when CanDrag && mode == Gesture.None:
                    Begin(e, point.Properties.IsLeftButtonPressed ? Gesture.Resize : Gesture.Move);
                    break;
                case PointerUpdateKind.LeftButtonPressed when mode == Gesture.Move:
                    Begin(e, Gesture.Resize);
                    break;
                case PointerUpdateKind.LeftButtonPressed when mode == Gesture.None:
                    Clicked?.Invoke(Client);
                    break;
            }
        };
        PointerMoved += (_, e) =>
        {
            var props = e.GetCurrentPoint(this).Properties;
            bool both = props.IsLeftButtonPressed && props.IsRightButtonPressed;
            if (mode == Gesture.None)
            {
                if (both && CanDrag) Begin(e, Gesture.Resize);   // the second button went down while the first was held
                return;
            }
            if (mode == Gesture.Move && both) { Begin(e, Gesture.Resize); return; }
            var now = this.PointToScreen(e.GetPosition(this));
            if (mode == Gesture.Move)
                Position = new PixelPoint(startPosition.X + now.X - from.X, startPosition.Y + now.Y - from.Y);
            else if (PointerSource == null)   // no timer: follow the events
            {
                if (!both) End();
                else Resize(now.X, now.Y, (e.KeyModifiers & KeyModifiers.Control) != 0);
            }
        };
        PointerReleased += (_, e) =>
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (mode == Gesture.Resize && PointerSource == null && kind is PointerUpdateKind.LeftButtonReleased or PointerUpdateKind.RightButtonReleased) End();
            else if (mode == Gesture.Move && kind == PointerUpdateKind.RightButtonReleased) End();
        };
    }

    private const ushort BothButtons = 0x100 | 0x400, ControlKey = 0x04;   // the X server's Button1Mask | Button3Mask, ControlMask

    /// <summary>Where the pointer is on the screen and which buttons and modifiers are down (the X server's mask), or null
    /// when that cannot be asked. Set by the app; a resize follows the mouse through it.</summary>
    public Func<(int X, int Y, ushort Mask)?>? PointerSource { get; set; }

    /// <summary>The window title (a window list, a capture program's source list can show it): the character as streamer mode shows it.</summary>
    private static string TitleOf(EveClient client) => $"MultiPreview - {StreamerMask.Name(StreamerKind.Character, client.Character)}";

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
            ? (Math.Clamp((int)Math.Round(min.Width), 20, 2000), Math.Clamp((int)Math.Round(min.Height), 20, 2000))
            : (80, 50);

    /// <summary>Gives the thumbnail this size and a picture of that size; its place stays. The picture is drawn into the
    /// new bitmap by the next <see cref="Update"/> (EveClients.Grab builds its scaling steps again for the new size).</summary>
    public void SetSize(int width, int height)
    {
        if (width == (int)Width && height == (int)Height) return;
        Width = width;
        Height = height;
        var old = _bitmap;
        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = _bitmap;
        old.Dispose();
    }

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

    /// <summary>Copies the client's current picture into the bitmap and shows it. False when the client is gone.</summary>
    public unsafe bool Update(EveClients clients)
    {
        bool ok;
        using (var fb = _bitmap.Lock())
            ok = clients.Grab(Client.Window, (byte*)fb.Address, fb.Size.Width, fb.Size.Height, fb.RowBytes);
        if (ok) _image.InvalidateVisual();
        return ok;
    }
}
