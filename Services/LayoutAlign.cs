using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>What the align editor can do to the ticked thumbnails (IDEAS #36).</summary>
public enum AlignOp { Left, Right, Top, Bottom, DistributeH, DistributeV, MatchWidth, MatchHeight, Nudge }

/// <summary>IDEAS #36: align, distribute, match size and nudge, over the places of the ticked thumbnails. Pure: a list of
/// (name, rectangle) in, a new list in the same order out; no window, no screen and no setting is read here. The first item
/// of the list is the reference of the two Match operations (the editor lists the thumbnails in the order they were
/// ticked). An operation with fewer items than <see cref="MinItems"/> gives the list back unchanged.</summary>
public static class LayoutAlign
{
    public static int MinItems(AlignOp op) => op switch
    {
        AlignOp.DistributeH or AlignOp.DistributeV => 3,
        AlignOp.Nudge => 1,
        _ => 2,
    };

    /// <summary>One operation, then <see cref="Clamp"/> when <paramref name="areas"/> (the screens' work areas) are given.
    /// <paramref name="dx"/>/<paramref name="dy"/> are for <see cref="AlignOp.Nudge"/>; <paramref name="keepAspect"/>
    /// (the aspect lock of Plan 19) makes a Match keep each thumbnail's own width-to-height ratio.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> Apply(AlignOp op, IReadOnlyList<(string Name, Rectangle Rect)> items,
        int dx = 0, int dy = 0, bool keepAspect = false, IReadOnlyList<Rectangle>? areas = null)
    {
        var moved = op switch
        {
            AlignOp.Left => AlignLeft(items),
            AlignOp.Right => AlignRight(items),
            AlignOp.Top => AlignTop(items),
            AlignOp.Bottom => AlignBottom(items),
            AlignOp.DistributeH => DistributeH(items),
            AlignOp.DistributeV => DistributeV(items),
            AlignOp.MatchWidth => MatchWidth(items, keepAspect),
            AlignOp.MatchHeight => MatchHeight(items, keepAspect),
            AlignOp.Nudge => Nudge(items, dx, dy),
            _ => items,
        };
        return areas == null || areas.Count == 0 || moved.Count < MinItems(op)
            ? moved
            : moved.Select(i => (i.Name, Clamp(i.Rect, areas))).ToList();
    }

    /// <summary>Every left edge goes to the leftmost one's.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> AlignLeft(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        AtLeast(items, 2) is { } list ? Map(list, r => new Rectangle(list.Min(i => i.Rect.Left), r.Y, r.Width, r.Height)) : items;

    /// <summary>Every right edge goes to the rightmost one's.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> AlignRight(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        AtLeast(items, 2) is { } list ? Map(list, r => new Rectangle(list.Max(i => i.Rect.Right) - r.Width, r.Y, r.Width, r.Height)) : items;

    /// <summary>Every top edge goes to the topmost one's.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> AlignTop(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        AtLeast(items, 2) is { } list ? Map(list, r => new Rectangle(r.X, list.Min(i => i.Rect.Top), r.Width, r.Height)) : items;

    /// <summary>Every bottom edge goes to the lowest one's.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> AlignBottom(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        AtLeast(items, 2) is { } list ? Map(list, r => new Rectangle(r.X, list.Max(i => i.Rect.Bottom) - r.Height, r.Width, r.Height)) : items;

    /// <summary>Three or more: in the order they stand left to right, the first and the last stay and the ones between get
    /// equal gaps (a negative gap, when they are wider than the span, overlaps them equally). Only x changes.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> DistributeH(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        Spread(items, horizontal: true);

    /// <summary>The same top to bottom; only y changes.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> DistributeV(IReadOnlyList<(string Name, Rectangle Rect)> items) =>
        Spread(items, horizontal: false);

    /// <summary>Every width becomes the first item's. With <paramref name="keepAspect"/> each height follows its own ratio.
    /// The top-left corner stays.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> MatchWidth(IReadOnlyList<(string Name, Rectangle Rect)> items, bool keepAspect = false)
    {
        if (AtLeast(items, 2) is not { } list) return items;
        int w = list[0].Rect.Width;
        return Map(list, r => new Rectangle(r.X, r.Y, w, keepAspect && r.Width > 0 && r.Height > 0 ? Math.Max(1, (int)Math.Round((double)w * r.Height / r.Width)) : r.Height));
    }

    /// <summary>Every height becomes the first item's. With <paramref name="keepAspect"/> each width follows its own ratio.
    /// The top-left corner stays.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> MatchHeight(IReadOnlyList<(string Name, Rectangle Rect)> items, bool keepAspect = false)
    {
        if (AtLeast(items, 2) is not { } list) return items;
        int h = list[0].Rect.Height;
        return Map(list, r => new Rectangle(r.X, r.Y, keepAspect && r.Width > 0 && r.Height > 0 ? Math.Max(1, (int)Math.Round((double)h * r.Width / r.Height)) : r.Width, h));
    }

    /// <summary>Every place moves by (dx, dy) pixels; sizes stay.</summary>
    public static IReadOnlyList<(string Name, Rectangle Rect)> Nudge(IReadOnlyList<(string Name, Rectangle Rect)> items, int dx, int dy) =>
        AtLeast(items, 1) is { } list ? Map(list, r => new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height)) : items;

    /// <summary>Keeps <paramref name="rect"/> inside one work area: the one it overlaps most (the nearest, when it overlaps
    /// none). The size is kept; a side longer than the area starts at the area's near edge. No areas: unchanged.</summary>
    public static Rectangle Clamp(Rectangle rect, IReadOnlyList<Rectangle> areas)
    {
        if (areas.Count == 0) return rect;
        var area = areas.OrderByDescending(a => OverlapArea(a, rect)).ThenBy(a => Distance(a, rect)).First();
        int x = rect.Width >= area.Width ? area.Left : Math.Max(area.Left, Math.Min(rect.X, area.Right - rect.Width));
        int y = rect.Height >= area.Height ? area.Top : Math.Max(area.Top, Math.Min(rect.Y, area.Bottom - rect.Height));
        return new Rectangle(x, y, rect.Width, rect.Height);
    }

    private static IReadOnlyList<(string Name, Rectangle Rect)> Spread(IReadOnlyList<(string Name, Rectangle Rect)> items, bool horizontal)
    {
        if (AtLeast(items, 3) is not { } list) return items;
        // Stable: the order they stand in, and the order given when two stand at the same place.
        var order = Enumerable.Range(0, list.Count)
            .OrderBy(i => horizontal ? list[i].Rect.Left : list[i].Rect.Top).ThenBy(i => i).ToList();
        int start = horizontal ? list[order[0]].Rect.Left : list[order[0]].Rect.Top;
        int end = horizontal ? list[order[^1]].Rect.Right : list[order[^1]].Rect.Bottom;
        int sizes = order.Sum(i => horizontal ? list[i].Rect.Width : list[i].Rect.Height);
        long free = (long)end - start - sizes;
        var result = list.ToArray();
        int before = 0;
        for (int n = 0; n < order.Count; n++)
        {
            var r = list[order[n]].Rect;
            int at = start + before + (int)Math.Round((double)n * free / (order.Count - 1));
            result[order[n]] = (list[order[n]].Name, horizontal ? new Rectangle(at, r.Y, r.Width, r.Height) : new Rectangle(r.X, at, r.Width, r.Height));
            before += horizontal ? r.Width : r.Height;
        }
        return result;
    }

    private static IReadOnlyList<(string Name, Rectangle Rect)>? AtLeast(IReadOnlyList<(string Name, Rectangle Rect)> items, int min) =>
        items.Count >= min ? items : null;

    private static IReadOnlyList<(string Name, Rectangle Rect)> Map(IReadOnlyList<(string Name, Rectangle Rect)> items, Func<Rectangle, Rectangle> change) =>
        items.Select(i => (i.Name, change(i.Rect))).ToList();

    private static long OverlapArea(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        return i.IsEmpty ? 0 : (long)i.Width * i.Height;
    }

    private static long Distance(Rectangle a, Rectangle b)
    {
        long dx = (a.Left + a.Width / 2) - (b.Left + b.Width / 2), dy = (a.Top + a.Height / 2) - (b.Top + b.Height / 2);
        return dx * dx + dy * dy;
    }
}
