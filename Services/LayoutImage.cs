using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EveMultiPreview.Models;
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
public static partial class LayoutImage
{
    /// <summary>The longest side of the picture, margin not counted; a larger desktop is scaled down to fit.</summary>
    public const int MaxSide = 1600;

    /// <summary>Space around the monitors.</summary>
    public const int Margin = 12;

    /// <summary>A name longer than this is cut: the box could not show more, and a file can hold anything.</summary>
    public const int MaxLabel = 40;

    /// <summary>Where each slot of <paramref name="preset"/> lands on these monitors: its fractions of its monitor's work
    /// area, the way applying the layout reads them (a monitor the PC does not have is the primary one; a box is at least
    /// 40 x 30, at most its work area, and kept inside it). A thumbnail hidden when the layout was saved is left out of a layout that
    /// remembers visibility, because the wall would not show it. No monitors: nothing can be placed.
    /// KEEP IN SYNC with ThumbnailManager.ResolveSlotRect, which applies a layout to the live wall: the same fractions of the
    /// same work area and the same 40 x 30 minimum. The clamp differs on purpose: that one runs EnsureOnScreen (any monitor
    /// that holds the place), this one keeps a box inside its own monitor's work area and caps it to that area.</summary>
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

    /// <summary>The name without control characters, line and paragraph separators (U+2028/2029) or format characters (the
    /// bidirectional marks and overrides U+200E/F, U+202A-E, U+2066-9 among them, which could reorder the text around the box),
    /// cut to <see cref="MaxLabel"/> (an ellipsis shows the cut, and a surrogate pair is never split by it).</summary>
    private static string Clean(string? name)
    {
        var text = new string((name ?? "").Where(c => !Unseen(c)).ToArray()).Trim();
        if (text.Length <= MaxLabel) return text;
        int keep = MaxLabel - 1;
        if (char.IsHighSurrogate(text[keep - 1])) keep--;   // the pair's second half is past the cut: drop the first as well
        return text.Substring(0, keep) + "…";
    }

    private static bool Unseen(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>A layout's name as the stem of a file name: characters a file name cannot hold are dropped, and an empty
    /// result is "layout".</summary>
    public static string FileStem(string? layoutName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string((layoutName ?? "").Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray()).Trim().TrimEnd('.');
        return stem.Length == 0 ? "layout" : stem.Length > 80 ? stem.Substring(0, 80) : stem;
    }
}
