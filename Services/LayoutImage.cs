using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveMultiPreview.Models;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Rectangle = System.Drawing.Rectangle;

namespace EveMultiPreview.Services;

/// <summary>One monitor as the layout image sees it: where it is (<see cref="Bounds"/>), the part windows may use
/// (<see cref="WorkArea"/>, what a layout's slot fractions are of) and whether it is the primary one.</summary>
public readonly record struct LayoutMonitor(Rectangle Bounds, Rectangle WorkArea, bool Primary);

/// <summary>A rectangle of the image with the text drawn in it.</summary>
public readonly record struct LayoutImageBox(Rectangle Rect, string Label);

/// <summary>What <see cref="LayoutImage.Render"/> draws, in image pixels: the monitors, then the thumbnail boxes over them.
/// <see cref="Width"/> and <see cref="Height"/> are 0 when there is nothing to draw.</summary>
public sealed class LayoutImagePlan
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<LayoutImageBox> Monitors { get; init; } = Array.Empty<LayoutImageBox>();
    public IReadOnlyList<LayoutImageBox> Boxes { get; init; } = Array.Empty<LayoutImageBox>();

    /// <summary>True when the boxes carry their slot numbers instead of character names.</summary>
    public bool NamesHidden { get; init; }
}

/// <summary>IDEAS P18: the thumbnail layout as a picture to share or attach to a help request. It draws boxes and text and
/// nothing else: the monitors' outlines, the thumbnails' places, and the character names (or, with names hidden, the slot
/// numbers) as drawn text. It never reads a client's pixels or a DWM thumbnail, so nothing private can be in the image.
/// <see cref="Resolve"/> and <see cref="Plan"/> are pure (a layout and the monitors in, rectangles out) and need no screen;
/// <see cref="Render"/> and <see cref="Png"/> draw the plan. A character name is player text: it is only ever drawn, never
/// used as a format string, and never logged.</summary>
public static class LayoutImage
{
    /// <summary>The longest side of the picture, margin not counted; a larger desktop is scaled down to fit.</summary>
    public const int MaxSide = 1600;

    /// <summary>Space around the monitors.</summary>
    public const int Margin = 12;

    /// <summary>A name longer than this is cut: the box could not show more, and a file can hold anything.</summary>
    public const int MaxLabel = 40;

    /// <summary>The monitors now, in Screen.AllScreens' order (the order a layout's slots count them in).</summary>
    public static IReadOnlyList<LayoutMonitor> CurrentMonitors() =>
        System.Windows.Forms.Screen.AllScreens.Select(s => new LayoutMonitor(s.Bounds, s.WorkingArea, s.Primary)).ToList();

    /// <summary>Where each slot of <paramref name="preset"/> lands on these monitors: its fractions of its monitor's work
    /// area, the way applying the layout reads them (a monitor the PC does not have is the primary one; a box is at least
    /// 40 x 30, at most its work area, and kept inside it). A thumbnail hidden when the layout was saved is left out of a layout that
    /// remembers visibility, because the wall would not show it. No monitors: nothing can be placed.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> Resolve(LayoutPreset preset, IReadOnlyList<LayoutMonitor> monitors)
    {
        var list = new List<(string, Rectangle)>();
        if (monitors.Count == 0) return list;
        int primary = Math.Max(0, monitors.ToList().FindIndex(m => m.Primary));
        foreach (var slot in preset.Slots)
        {
            if (preset.IncludesVisibility && slot.Hidden) continue;
            var wa = monitors[slot.Monitor >= 0 && slot.Monitor < monitors.Count ? slot.Monitor : primary].WorkArea;
            int w = Math.Min(Math.Max(1, wa.Width), Math.Max(40, Px(slot.Fw * wa.Width))), h = Math.Min(Math.Max(1, wa.Height), Math.Max(30, Px(slot.Fh * wa.Height)));
            int x = wa.Left + Px(slot.Fx * wa.Width), y = wa.Top + Px(slot.Fy * wa.Height);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            list.Add((slot.Character ?? "", new Rectangle(x, y, w, h)));
        }
        return list;
    }

    /// <summary>A number from a file is not trusted: not-a-number and huge values become something a rectangle can hold.</summary>
    private static int Px(double v) => double.IsFinite(v) ? (int)Math.Max(-1_000_000, Math.Min(1_000_000, v)) : 0;

    /// <summary>The drawing for these thumbnail places on these monitors. The picture covers the monitors (the thumbnails'
    /// own extent when there are none), scaled down so its longest side is at most <see cref="MaxSide"/>. Each box carries
    /// the character's name, cleaned and cut to <see cref="MaxLabel"/>; with <paramref name="hideNames"/> it carries its
    /// slot number instead (1 for the first place of the list, and so on), and the names are not in the plan at all.</summary>
    public static LayoutImagePlan Plan(IReadOnlyList<(string Name, Rectangle Rect)> rects, IReadOnlyList<LayoutMonitor> monitors, bool hideNames)
    {
        var area = monitors.Select(m => m.Bounds).Where(b => b.Width > 0 && b.Height > 0).ToList();
        if (area.Count == 0) area = rects.Select(r => r.Rect).Where(b => b.Width > 0 && b.Height > 0).ToList();
        if (area.Count == 0) return new LayoutImagePlan { NamesHidden = hideNames };

        var union = area.Aggregate(Rectangle.Union);
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(union.Width, union.Height));
        var canvas = new Rectangle(0, 0, (int)Math.Ceiling(union.Width * scale) + 2 * Margin, (int)Math.Ceiling(union.Height * scale) + 2 * Margin);

        Rectangle Map(Rectangle r) => new(Margin + (int)Math.Round((r.X - union.X) * scale), Margin + (int)Math.Round((r.Y - union.Y) * scale),
            Math.Max(1, (int)Math.Round(r.Width * scale)), Math.Max(1, (int)Math.Round(r.Height * scale)));

        var screens = new List<LayoutImageBox>();
        for (int i = 0; i < monitors.Count; i++)
            if (Rectangle.Intersect(Map(monitors[i].Bounds), canvas) is { IsEmpty: false } inside)
                screens.Add(new LayoutImageBox(inside, (i + 1).ToString(CultureInfo.InvariantCulture)));

        var boxes = new List<LayoutImageBox>();
        for (int i = 0; i < rects.Count; i++)
        {
            if (Rectangle.Intersect(Map(rects[i].Rect), canvas) is not { IsEmpty: false } inside) continue;
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string label = hideNames ? number : Clean(rects[i].Name);
            boxes.Add(new LayoutImageBox(inside, label.Length == 0 ? number : label));
        }
        return new LayoutImagePlan { Width = canvas.Width, Height = canvas.Height, Monitors = screens, Boxes = boxes, NamesHidden = hideNames };
    }

    /// <summary>The name without control characters, cut to <see cref="MaxLabel"/> (an ellipsis shows the cut).</summary>
    private static string Clean(string? name)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > MaxLabel ? text.Substring(0, MaxLabel - 1) + "…" : text;
    }

    /// <summary>A layout's name as the stem of a file name: characters a file name cannot hold are dropped, and an empty
    /// result is "layout".</summary>
    public static string FileStem(string? layoutName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string((layoutName ?? "").Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray()).Trim().TrimEnd('.');
        return stem.Length == 0 ? "layout" : stem.Length > 80 ? stem.Substring(0, 80) : stem;
    }

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
