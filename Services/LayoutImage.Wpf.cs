using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Rectangle = System.Drawing.Rectangle;

namespace EveMultiPreview.Services;

/// <summary>The WPF half of LayoutImage: the monitors as Windows lists them, and the drawing of a plan (Render, Png). The
/// pure half (Resolve, Plan) is shared with the Linux app, which draws the plan with Avalonia.</summary>
public static partial class LayoutImage
{
    /// <summary>The monitors now, in Screen.AllScreens' order (the order a layout's slots count them in).</summary>
    public static IReadOnlyList<LayoutMonitor> CurrentMonitors() =>
        System.Windows.Forms.Screen.AllScreens.Select(s => new LayoutMonitor(s.Bounds, s.WorkingArea, s.Primary)).ToList();

    private static readonly Brush Background = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x1F, 0x27)));
    private static readonly Brush MonitorFill = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x27, 0x2D, 0x38)));
    private static readonly Brush MonitorText = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x77, 0x89)));
    private static readonly Pen MonitorPen = Frozen(new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x66, 0x7A)), 2));
    private static readonly Brush BoxFill = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x5E, 0x9E)));
    private static readonly Pen BoxPen = Frozen(new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0xB8, 0xF0)), 1.5));
    private static readonly Brush BoxText = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF6, 0xFC)));

    private static T Frozen<T>(T f) where T : System.Windows.Freezable { f.Freeze(); return f; }

    /// <summary>Draws <paramref name="plan"/>: the background, the monitors with their numbers, then the boxes with their
    /// text. Drawn text only; no window or client is read. Needs the UI thread.</summary>
    public static BitmapSource Render(LayoutImagePlan plan)
    {
        if (plan.Width < 1 || plan.Height < 1) throw new InvalidOperationException("Nothing to draw.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new System.Windows.Rect(0, 0, plan.Width, plan.Height));
            foreach (var monitor in plan.Monitors)
            {
                var r = Rect(monitor.Rect);
                dc.DrawRectangle(MonitorFill, MonitorPen, r);
                var number = Text(monitor.Label, 16, MonitorText, r.Width - 12, r.Height - 8, System.Windows.TextAlignment.Left);
                dc.DrawText(number, new System.Windows.Point(r.X + 8, r.Y + 6));
            }
            foreach (var box in plan.Boxes)
            {
                var r = Rect(box.Rect);
                dc.DrawRectangle(BoxFill, BoxPen, r);
                var text = Text(box.Label, Math.Max(9, Math.Min(22, r.Height * 0.28)), BoxText, Math.Max(1, r.Width - 6), Math.Max(1, r.Height - 4), System.Windows.TextAlignment.Center);
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawText(text, new System.Windows.Point(r.X + 3, r.Y + Math.Max(2, (r.Height - text.Height) / 2)));
                dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(plan.Width, plan.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The picture as the bytes of a PNG file.</summary>
    public static byte[] Png(LayoutImagePlan plan)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(plan)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static System.Windows.Rect Rect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Player text goes in as the text to draw, never as a format.</summary>
    private static FormattedText Text(string text, double size, Brush brush, double maxWidth, double maxHeight, System.Windows.TextAlignment align) =>
        new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = Math.Max(1, maxHeight),
            Trimming = System.Windows.TextTrimming.CharacterEllipsis,
            TextAlignment = align,
        };
}
