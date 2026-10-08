using System;
using System.Collections.Generic;
using System.Linq;

using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EveMultiPreview.Views;

/// <summary>Where the quick-switch wheel's cards go (Plan 4, the F2 reference). <see cref="Ring"/>: around a centre disc of
/// <see cref="Disc"/> px, the first card at the top, then clockwise. When that ring wouldn't fit in <c>maxCanvas</c> (many
/// clients on a small screen) the cards go in rows under the disc's text instead, as the old card grid did.
/// <see cref="Slots"/> are card top-left corners in the card canvas; slot i holds the card with index i (number key i + 1).</summary>
public sealed record QuickSwitchLayout(bool Ring, IReadOnlyList<Point> Slots, Size Canvas, Point Centre, double Disc)
{
    public const double CardWidth = 118, CardHeight = 84, Gap = 10, DiscSize = 150;

    /// <summary>Rows never get wider than this many cards, so a wide screen doesn't turn them into one long strip.</summary>
    public const int MaxColumns = 8;

    /// <summary>Card centres sit at least this far outside the disc's edge, so no card corner touches it (a card's half
    /// diagonal is 72 px; the reference uses 180 px around a 150 px disc).</summary>
    public const double DiscClearance = 105;

    public static QuickSwitchLayout For(int count, Size maxCanvas, double disc = DiscSize)
    {
        if (count <= 0)
            return new QuickSwitchLayout(true, Array.Empty<Point>(), new Size(disc, disc), new Point(disc / 2, disc / 2), disc);

        // Neighbouring cards are a card diagonal plus a gap apart along the circle (chord = 2 r sin(π / n)), so they never
        // overlap at any angle.
        double chord = Math.Sqrt(CardWidth * CardWidth + CardHeight * CardHeight) + Gap;
        double minRadius = disc / 2 + DiscClearance;
        double radius = count < 2 ? minRadius : Math.Max(minRadius, chord / 2 / Math.Sin(Math.PI / count));
        var ringCanvas = new Size(2 * radius + CardWidth, 2 * radius + CardHeight);
        if (ringCanvas.Width <= maxCanvas.Width && ringCanvas.Height <= maxCanvas.Height)
        {
            var centre = new Point(ringCanvas.Width / 2, ringCanvas.Height / 2);
            var slots = Enumerable.Range(0, count).Select(i =>
            {
                double angle = -Math.PI / 2 + i * 2 * Math.PI / count;
                return new Point(centre.X + radius * Math.Cos(angle) - CardWidth / 2, centre.Y + radius * Math.Sin(angle) - CardHeight / 2);
            }).ToList();
            return new QuickSwitchLayout(true, slots, ringCanvas, centre, disc);
        }

        int columns = Math.Clamp((int)((maxCanvas.Width + Gap) / (CardWidth + Gap)), 1, Math.Min(count, MaxColumns));
        int rows = (int)Math.Ceiling(count / (double)columns);
        var grid = Enumerable.Range(0, count)
            .Select(i => new Point(i % columns * (CardWidth + Gap), i / columns * (CardHeight + Gap)))
            .ToList();
        var size = new Size(columns * CardWidth + (columns - 1) * Gap, rows * CardHeight + (rows - 1) * Gap);
        return new QuickSwitchLayout(false, grid, size, new Point(size.Width / 2, 0), disc);
    }

    /// <summary>The slot whose card area holds <paramref name="p"/>, or -1 (drag to reorder).</summary>
    public int SlotAt(Point p)
    {
        for (int i = 0; i < Slots.Count; i++)
            if (p.X >= Slots[i].X && p.X < Slots[i].X + CardWidth && p.Y >= Slots[i].Y && p.Y < Slots[i].Y + CardHeight)
                return i;
        return -1;
    }
}
