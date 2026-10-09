using System;
using System.Collections.Generic;
using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Colour-blind mode's palettes: Wong's colour-blind-safe set and the standard one, written over the severity,
/// alert flash, highlight, stats text, thumbnail text and login-mark colours. Turning it on also clears the
/// per-event alert colours. Moved unchanged from SettingsWindow.ApplyColorBlindPalette so both settings windows
/// write exactly the same colours. Turning it on keeps the colours it replaces (ColorBlindSavedColors) and turning
/// it off gives them back (IDEAS P29); with none kept, off writes the standard set, as before.
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

    // Names in the kept set: the profile the three per-profile colours came from, setting names, "SeverityColors.x"
    // and "AlertColors.x".
    private const string ProfileKey = "Profile", SeverityPrefix = "SeverityColors.", EventPrefix = "AlertColors.";

    public static void Apply(AppSettings s, bool cbMode)
    {
        if (cbMode)
        {
            // Keep what is about to be replaced, unless a set is already kept: a second "on" would keep the
            // colour-blind colours instead of the player's.
            if (Saved(s) == null) s.ColorBlindSavedColors = Capture(s);
        }
        else if (Saved(s) is { } saved)
        {
            Restore(s, saved);
            s.ColorBlindSavedColors = "";
            return;
        }

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
        else
            s.ColorBlindSavedColors = "";   // an unreadable kept set goes with the standard colours
    }

    /// <summary>The colours turning colour-blind mode on would replace, as one line of JSON text.</summary>
    public static string Capture(AppSettings s)
    {
        var kept = new Dictionary<string, string>
        {
            [ProfileKey] = s.LastUsedProfile,
            [nameof(AppSettings.AlertFlashColor)] = s.AlertFlashColor,
            [nameof(AppSettings.StatOverlayTextColor)] = s.StatOverlayTextColor,
            [nameof(AppSettings.ClientHighlightColor)] = s.ClientHighlightColor,
            [nameof(AppSettings.ThumbnailTextColor)] = s.ThumbnailTextColor,
            [nameof(AppSettings.NotLoggedInColor)] = s.NotLoggedInColor,
        };
        foreach (var key in CbSeverityColors.Keys)
            if (s.SeverityColors.TryGetValue(key, out var colour)) kept[SeverityPrefix + key] = colour;
        foreach (var (alertType, colour) in s.AlertColors)
            kept[EventPrefix + alertType] = colour;
        return JsonSerializer.Serialize(kept);
    }

    /// <summary>The kept colours, or null when none are kept or a hand-edited file made them unreadable.</summary>
    public static Dictionary<string, string>? Saved(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ColorBlindSavedColors)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(s.ColorBlindSavedColors); }
        catch (JsonException) { return null; }
    }

    /// <summary>Gives the kept colours back. The three per-profile colours go back to the profile they came from (the
    /// current one if that profile is gone); another current profile keeps its own, which the mode never changed.</summary>
    private static void Restore(AppSettings s, Dictionary<string, string> kept)
    {
        string? Kept(string key) => kept.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        foreach (var key in CbSeverityColors.Keys)
        {
            if (Kept(SeverityPrefix + key) is { } colour) s.SeverityColors[key] = colour;
            else s.SeverityColors.Remove(key);   // it had none before
        }
        s.AlertFlashColor = Kept(nameof(AppSettings.AlertFlashColor)) ?? NormalFlashColor;
        s.StatOverlayTextColor = Kept(nameof(AppSettings.StatOverlayTextColor)) ?? NormalStatOverlayText;
        foreach (var (key, colour) in kept)
            if (key.StartsWith(EventPrefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(colour))
                s.AlertColors[key[EventPrefix.Length..]] = colour;

        var profile = Kept(ProfileKey) is { } name && s.Profiles.TryGetValue(name, out var p) ? p : null;
        if (profile == null)
        {
            s.ClientHighlightColor = Kept(nameof(AppSettings.ClientHighlightColor)) ?? NormalHighlightColor;
            s.ThumbnailTextColor = Kept(nameof(AppSettings.ThumbnailTextColor)) ?? NormalThumbnailText;
            s.NotLoggedInColor = Kept(nameof(AppSettings.NotLoggedInColor)) ?? NormalNotLoggedIn;
            return;
        }
        profile.ClientHighlightColor = Kept(nameof(AppSettings.ClientHighlightColor)) ?? NormalHighlightColor;
        profile.ThumbnailTextColor = Kept(nameof(AppSettings.ThumbnailTextColor)) ?? NormalThumbnailText;
        profile.NotLoggedInColor = Kept(nameof(AppSettings.NotLoggedInColor)) ?? NormalNotLoggedIn;
    }
}
