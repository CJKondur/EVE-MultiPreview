using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #10: which monitors the PC has, as one stable line of text. A layout saved with the line is the layout
/// for that arrangement. The line holds each monitor's bounds (position and size in pixels, never a device name), sorted,
/// so the order Windows lists the monitors in does not matter: "2|0,0,1920,1080|1920,0,2560,1440".</summary>
public static class MonitorFingerprint
{
    /// <summary>More monitors than this is not an arrangement the line describes: such a line is not valid.</summary>
    public const int MaxMonitors = 32;

    /// <summary>The line for these bounds. Order-independent; no monitors is "0".</summary>
    public static string Of(IEnumerable<Rectangle> bounds)
    {
        var sorted = bounds.OrderBy(b => b.X).ThenBy(b => b.Y).ThenBy(b => b.Width).ThenBy(b => b.Height).ToList();
        return string.Join("|", new[] { sorted.Count.ToString(CultureInfo.InvariantCulture) }.Concat(sorted.Select(b =>
            string.Join(",", new[] { b.X, b.Y, b.Width, b.Height }.Select(n => n.ToString(CultureInfo.InvariantCulture))))));
    }

    /// <summary>True for exactly what <see cref="Of"/> writes: a file's line is read from outside, so only a line that
    /// parses and writes back to itself is one (a changed order, a stray character or a wrong count is not).</summary>
    public static bool IsValid(string? line) => TryParse(line, out var bounds) && Of(bounds) == line;

    /// <summary>How many monitors a valid line describes; -1 for none given or a line that is not valid.</summary>
    public static int Count(string? line) => TryParse(line, out var bounds) && Of(bounds) == line ? bounds.Count : -1;

    private static bool TryParse(string? line, out List<Rectangle> bounds)
    {
        bounds = new List<Rectangle>();
        if (string.IsNullOrEmpty(line) || line.Length > 1024) return false;
        var parts = line.Split('|');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count > MaxMonitors || parts.Length != count + 1) return false;
        for (int i = 1; i < parts.Length; i++)
        {
            var n = parts[i].Split(',');
            if (n.Length != 4) return false;
            var v = new int[4];
            for (int k = 0; k < 4; k++)
                if (!int.TryParse(n[k], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v[k])) return false;
            bounds.Add(new Rectangle(v[0], v[1], v[2], v[3]));
        }
        return true;
    }
}
