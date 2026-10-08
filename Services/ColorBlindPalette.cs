using System.Collections.Generic;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Colour-blind mode's palettes: Wong's colour-blind-safe set and the standard one, written over the severity,
/// alert flash, highlight, stats text, thumbnail text and login-mark colours. Turning it on also clears the
/// per-event alert colours. Moved unchanged from SettingsWindow.ApplyColorBlindPalette so both settings windows
/// write exactly the same colours.
/// </summary>
public static class ColorBlindPalette
{
    // Normal defaults
    private static readonly Dictionary<string, string> NormalSeverityColors = new()
    {
        ["critical"] = "#FF0000", ["warning"] = "#FFA500", ["info"] = "#4A9EFF"
    };

    // Wong's universally safe color-blind palette
    private static readonly Dictionary<string, string> CbSeverityColors = new()
    {
        ["critical"] = "#D55E00",  // vermillion — distinct for all CB types
        ["warning"] = "#F0E442",   // yellow — visible to all
        ["info"] = "#0072B2"       // blue — safe for all
    };

    private const string NormalFlashColor = "0xff0000";
    private const string CbFlashColor = "0xCC79A7";           // reddish-pink — CB-safe
    private const string NormalHighlightColor = "#E36A0D";
    private const string CbHighlightColor = "#56B4E9";        // sky blue — CB-safe
    private const string NormalStatOverlayText = "#00FF88";
    private const string CbStatOverlayText = "#56B4E9";       // sky blue — green is red-green CB problem
    private const string NormalThumbnailText = "#FAC57A";
    private const string CbThumbnailText = "#FFFFFF";          // white — universally readable
    private const string NormalNotLoggedIn = "#555555";
    private const string CbNotLoggedIn = "#888888";            // brighter gray — better contrast for low vision

    public static void Apply(AppSettings s, bool cbMode)
    {
        // Severity tier colors
        var sevPalette = cbMode ? CbSeverityColors : NormalSeverityColors;
        foreach (var kv in sevPalette)
            s.SeverityColors[kv.Key] = kv.Value;

        // Alert flash + active client highlight
        s.AlertFlashColor = cbMode ? CbFlashColor : NormalFlashColor;
        s.ClientHighlightColor = cbMode ? CbHighlightColor : NormalHighlightColor;

        // Stat overlay text (green → sky blue)
        s.StatOverlayTextColor = cbMode ? CbStatOverlayText : NormalStatOverlayText;

        // Thumbnail text overlay
        s.ThumbnailTextColor = cbMode ? CbThumbnailText : NormalThumbnailText;

        // Not-logged-in indicator
        s.NotLoggedInColor = cbMode ? CbNotLoggedIn : NormalNotLoggedIn;

        // Clear per-alert color overrides so they pick up the new severity defaults
        if (cbMode)
            s.AlertColors.Clear();
    }
}
