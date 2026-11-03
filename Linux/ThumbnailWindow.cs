using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EveMultiPreview.Linux;

/// <summary>How a thumbnail is drawn this frame, worked out from the settings.</summary>
public sealed record ThumbnailLook(IBrush Border, double BorderThickness, string Label, bool ShowLabel,
    IBrush TextBrush, double TextSize, string Font, double Opacity, bool Topmost);

/// <summary>One client's live thumbnail. Left click switches to the client; right drag moves the thumbnail unless
/// positions are locked.</summary>
public sealed class ThumbnailWindow : Window
{
    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _name;
    private readonly WriteableBitmap _bitmap;
    private ThumbnailLook? _look;

    public EveClient Client { get; }
    public bool CanDrag { get; set; } = true;
    public event Action<EveClient>? Clicked;
    /// <summary>The thumbnail was moved by the player (not by the app placing it).</summary>
    public event Action<ThumbnailWindow>? Moved;

    public ThumbnailWindow(EveClient client, PixelRect rect)
    {
        Client = client;
        Title = $"MultiPreview - {client.Character}";
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
        // behaves the same on every desktop; the new place is reported once, on release.
        PixelPoint? dragFrom = null;
        PixelPoint startPosition = default;
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed && CanDrag)
            {
                dragFrom = this.PointToScreen(e.GetPosition(this));
                startPosition = Position;
                e.Pointer.Capture(_frame);
            }
            else if (point.Properties.IsLeftButtonPressed) Clicked?.Invoke(Client);
        };
        PointerMoved += (_, e) =>
        {
            if (dragFrom is not { } from) return;
            var now = this.PointToScreen(e.GetPosition(this));
            Position = new PixelPoint(startPosition.X + now.X - from.X, startPosition.Y + now.Y - from.Y);
        };
        PointerReleased += (_, e) =>
        {
            if (dragFrom == null) return;
            dragFrom = null;
            e.Pointer.Capture(null);
            if (Position != startPosition) Moved?.Invoke(this);
        };
    }

    public void Apply(ThumbnailLook look)
    {
        if (look == _look) return;
        _look = look;
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
