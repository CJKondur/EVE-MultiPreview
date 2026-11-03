using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EveMultiPreview.Linux;

/// <summary>One client's live thumbnail. Left click switches to the client; right drag moves the thumbnail.</summary>
public sealed class ThumbnailWindow : Window
{
    private static readonly IBrush ActiveBorder = new SolidColorBrush(Color.Parse("#4fc3f7"));
    private static readonly IBrush IdleBorder = new SolidColorBrush(Color.Parse("#30343c"));

    private readonly Border _frame;
    private readonly Image _image;
    private readonly WriteableBitmap _bitmap;

    public EveClient Client { get; }
    public event Action<EveClient>? Clicked;

    public ThumbnailWindow(EveClient client, int width, int height)
    {
        Client = client;
        Title = $"MultiPreview - {client.Character}";
        Width = width;
        Height = height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Black;

        _bitmap = NewBitmap(width, height);
        _image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        var name = new TextBlock
        {
            Text = client.Character,
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = IdleBorder,
            Child = new Panel { Children = { _image, name } },
        };
        Content = _frame;

        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed) BeginMoveDrag(e);
            else if (point.Properties.IsLeftButtonPressed) Clicked?.Invoke(Client);
        };
    }

    private static WriteableBitmap NewBitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    public bool ClientInFront { set => _frame.BorderBrush = value ? ActiveBorder : IdleBorder; }

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
