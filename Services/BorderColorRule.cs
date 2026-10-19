using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Which colour a thumbnail's border gets, and whether an inactive one is drawn at all: the rule ThumbnailManager
/// follows, kept apart from its windows so it can be tested. A character's own colour comes first, then its group's
/// (while group borders are on), then the automatic colour (IDEAS P19, while "Automatic border colours" is on), then
/// the default. An automatic colour is treated as a group colour in both places: it counts where a group colour does,
/// and "keep the highlight inside a coloured group" holds it back from the client you are playing too.
/// </summary>
public static class BorderColorRule
{
    /// <summary>The colour as text ("#RRGGBB", "0xRRGGBB" or "RRGGBB"), for ThumbnailManager.ParseColor. <paramref name="running"/>
    /// is asked only when an automatic colour is wanted.</summary>
    public static string Resolve(AppSettings s, string charName, bool isActive, Func<IReadOnlyCollection<string>> running)
    {
        // 1. Custom per-character color
        if (s.CustomColorsActive && s.CustomColors.TryGetValue(charName, out var custom))
        {
            if (isActive && !string.IsNullOrEmpty(custom.Border))
                return custom.Border;
            if (!isActive && !string.IsNullOrEmpty(custom.InactiveBorder))
                return custom.InactiveBorder;
        }

        // 2. Thumbnail group color (only when Show Group Borders is enabled). Optionally
        //    not for the active client, so it stands out from the rest of its group (#115).
        bool highlightKept = isActive && s.HighlightOverridesGroupColor;
        if (s.ShowAllColoredBorders && !highlightKept)
        {
            foreach (var group in s.ThumbnailGroups)
            {
                if (group.Members.Any(m => m.Equals(charName, StringComparison.OrdinalIgnoreCase)))
                    return group.Color;
            }
        }

        // 3. Automatic color (P19): behaves as a group color
        if (s.AutoCharacterBorders && !highlightKept && !string.IsNullOrWhiteSpace(charName))
            return AutoBorderColors.Hex(AutoBorderColors.For(charName, running()));

        // 4. Default colors
        return isActive ? s.ClientHighlightColor : s.InactiveClientBorderColor;
    }

    /// <summary>Whether an inactive border is drawn for this character: it has a colour of its own, a group colour
    /// (group borders on), an automatic colour, or the default colour is not empty.</summary>
    public static bool ShowsInactiveBorder(AppSettings s, string charName)
    {
        // Custom per-character inactive border — always show if configured
        if (s.CustomColorsActive && s.CustomColors.TryGetValue(charName, out var custom)
            && !string.IsNullOrEmpty(custom.InactiveBorder))
            return true;

        // Group color — show if enabled and character is in a group
        if (s.ShowAllColoredBorders)
        {
            foreach (var group in s.ThumbnailGroups)
            {
                if (group.Members.Any(m => m.Equals(charName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        // Automatic color — every named character has one
        if (s.AutoCharacterBorders && !string.IsNullOrWhiteSpace(charName))
            return true;

        // Default — empty means no border
        return !string.IsNullOrWhiteSpace(s.InactiveClientBorderColor);
    }
}
