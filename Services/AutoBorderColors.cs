using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.Services;

/// <summary>
/// IDEAS P19: the colour a character's border gets when the player turns on "Automatic border colours" and has set no
/// colour for it (a character's own colour and its group's win). Pure: no window, no setting is read here.
/// The colour of a name is a stable hash of its lower-cased name (FNV-1a, 32 bit) modulo the palette; names that
/// collide among the running characters are moved to the next free colour in (hash, name) order, so the same roster
/// gets the same colours on every start, whatever order the clients were found in.
/// </summary>
public static class AutoBorderColors
{
    /// <summary>
    /// Ten colours, each at least 3:1 against the thumbnail window's background, which the border is painted on
    /// (ThumbnailBackgroundColor, "#57504E" unless the player changed it; Views/ThumbnailWindow.OnPaint draws the
    /// border over it). The first four are Okabe and Ito's colour-blind-safe orange, sky blue, bluish green and
    /// yellow; their blue, vermillion and reddish purple are too dark for 3:1 on that grey, so those three are used
    /// lightened. The last three (lime, violet, white) are other hues that stay apart from the rest for the common
    /// kinds of colour blindness. A test asserts the contrast.
    /// </summary>
    public static IReadOnlyList<Color> Palette { get; } = new[]
    {
        Color.FromRgb(0xE6, 0x9F, 0x00),   // orange (Okabe-Ito)
        Color.FromRgb(0x56, 0xB4, 0xE9),   // sky blue (Okabe-Ito)
        Color.FromRgb(0x3D, 0xDB, 0xA8),   // bluish green (Okabe-Ito, lightened)
        Color.FromRgb(0xF0, 0xE4, 0x42),   // yellow (Okabe-Ito)
        Color.FromRgb(0x8C, 0xA2, 0xFF),   // blue (Okabe-Ito, lightened)
        Color.FromRgb(0xFF, 0x8A, 0x5C),   // vermillion (Okabe-Ito, lightened)
        Color.FromRgb(0xE5, 0x8F, 0xC4),   // reddish purple (Okabe-Ito, lightened)
        Color.FromRgb(0xB6, 0xE0, 0x4A),   // lime
        Color.FromRgb(0xC9, 0xA3, 0xFF),   // violet
        Color.FromRgb(0xFF, 0xFF, 0xFF),   // white
    };

    /// <summary>FNV-1a, 32 bit, over the UTF-8 bytes of the lower-cased name.</summary>
    public static uint Hash(string name)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    private static int Slot(uint hash) => (int)(hash % (uint)Palette.Count);

    /// <summary>The colour of <paramref name="name"/> among the characters in <paramref name="running"/>. A name that
    /// is not running gets the colour of its own hash and moves nobody.</summary>
    public static Color For(string name, IReadOnlyCollection<string> running)
    {
        var key = name.ToLowerInvariant();
        var roster = running.Select(n => n.ToLowerInvariant()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)
            .Select(n => (Name: n, Hash: Hash(n)))
            .OrderBy(n => n.Hash).ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
        if (!roster.Any(n => n.Name == key)) return Palette[Slot(Hash(key))];

        var taken = new bool[Palette.Count];
        int used = 0;
        foreach (var (rosterName, hash) in roster)
        {
            int slot = Slot(hash);
            // Once the palette is used up, the rest keep the colour of their hash: ten colours is all there are.
            if (used < Palette.Count)
            {
                while (taken[slot]) slot = (slot + 1) % Palette.Count;
                taken[slot] = true;
                used++;
            }
            if (rosterName == key) return Palette[slot];
        }
        return Palette[Slot(Hash(key))];       // not reached: the name is in the roster
    }

    /// <summary>"#RRGGBB", the form the other border colours are stored and parsed in.</summary>
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
