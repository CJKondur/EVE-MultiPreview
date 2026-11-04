using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EveMultiPreview.Linux;

/// <summary>A crop window's "Edit area…" (the Windows CropPickerOverlay): a borderless window over the client showing a
/// still picture of it, where the player drags over the part the crop window is to show. The picture is taken just before,
/// so the picker works with or without a compositor. Esc, a plain click (under 4 × 4 pixels) or closing it cancels.</summary>
internal sealed class CropPicker : Window
{
    private readonly Border _box;
    private readonly WriteableBitmap _bitmap;
    private Point? _from;
    private bool _done, _closed;

    /// <summary>Once, when the picker ends: the picked part in client pixels, or null when cancelled.</summary>
    public event Action<(int X, int Y, int W, int H)?>? Done;

    public unsafe CropPicker(PixelPoint at, int width, int height, byte[] bgra)
    {
        Title = "MultiPreview - " + Ui.T("L.CropPick.Title", "Pick Crop Area");
        Position = at;
        Width = width;
        Height = height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Cursor = new Cursor(StandardCursorType.Cross);

        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = _bitmap.Lock())
            for (int y = 0; y < height; y++)
                new ReadOnlySpan<byte>(bgra, y * width * 4, width * 4).CopyTo(new Span<byte>((byte*)fb.Address + (long)y * fb.RowBytes, width * 4));

        _box = new Border
        {
            BorderBrush = Brushes.Gold,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD7, 0x00)),
            IsVisible = false,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = Ui.T("L.CropPick.Hint", "Drag to select crop area — press Esc to cancel"), Foreground = Brushes.White },
        };
        var canvas = new Canvas();
        canvas.Children.Add(_box);
        Content = new Panel { Children = { new Image { Source = _bitmap, Stretch = Stretch.None }, canvas, hint } };

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _from = e.GetPosition(this);
            e.Pointer.Capture(this);
            Show(_from.Value, _from.Value);
        };
        PointerMoved += (_, e) => { if (_from is { } from) Show(from, e.GetPosition(this)); };
        PointerReleased += (_, e) =>
        {
            if (_from is not { } from) return;
            _from = null;
            var r = Picked(from, e.GetPosition(this), width, height);
            Finish(r.W < 4 || r.H < 4 ? null : r);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Closed += (_, _) => { _closed = true; Finish(null); _bitmap.Dispose(); };
        Opened += (_, _) => Activate();
    }

    /// <summary>The rectangle between two points, in whole client pixels, cut to the client.</summary>
    public static (int X, int Y, int W, int H) Picked(Point a, Point b, int width, int height)
    {
        int x0 = (int)Math.Clamp(Math.Floor(Math.Min(a.X, b.X)), 0, width), y0 = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, b.Y)), 0, height);
        int x1 = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, b.X)), 0, width), y1 = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, b.Y)), 0, height);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private void Show(Point a, Point b)
    {
        _box.IsVisible = true;
        Canvas.SetLeft(_box, Math.Min(a.X, b.X));
        Canvas.SetTop(_box, Math.Min(a.Y, b.Y));
        _box.Width = Math.Abs(a.X - b.X);
        _box.Height = Math.Abs(a.Y - b.Y);
    }

    private void Finish((int X, int Y, int W, int H)? picked)
    {
        if (_done) return;
        _done = true;
        try { Done?.Invoke(picked); }
        finally { if (!_closed) Close(); }
    }
}
