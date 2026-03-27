using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Models;

/// <summary>
/// Intermediate model for automatically migrating legacy 'EVE-O Preview.json' files into our AppSettings.
/// Schema maps to EVE-O Preview's ThumbnailConfiguration.cs.
/// </summary>
public class EveOConfigRoot
{
    [JsonPropertyName("ThumbnailsOpacity")]
    public double ThumbnailsOpacity { get; set; } = 0.5;

    [JsonPropertyName("ShowThumbnailsAlwaysOnTop")]
    public bool ShowThumbnailsAlwaysOnTop { get; set; } = true;

    [JsonPropertyName("EnableClientLayoutTracking")]
    public bool EnableClientLayoutTracking { get; set; } = false;

    [JsonPropertyName("HideActiveClientThumbnail")]
    public bool HideActiveClientThumbnail { get; set; } = false;

    [JsonPropertyName("MinimizeInactiveClients")]
    public bool MinimizeInactiveClients { get; set; } = false;

    [JsonPropertyName("EnablePerClientThumbnailLayouts")]
    public bool EnablePerClientThumbnailLayouts { get; set; } = false;

    [JsonPropertyName("ThumbnailSize")]
    public string? ThumbnailSize { get; set; }

    [JsonPropertyName("ThumbnailMinimumSize")]
    public string? ThumbnailMinimumSize { get; set; }

    [JsonPropertyName("EnableThumbnailSnap")]
    public bool EnableThumbnailSnap { get; set; } = true;

    [JsonPropertyName("ThumbnailZoomEnabled")]
    public bool ThumbnailZoomEnabled { get; set; } = false;

    [JsonPropertyName("ThumbnailZoomFactor")]
    public int ThumbnailZoomFactor { get; set; } = 2;

    [JsonPropertyName("ShowThumbnailOverlays")]
    public bool ShowThumbnailOverlays { get; set; } = true;

    [JsonPropertyName("ShowThumbnailFrames")]
    public bool ShowThumbnailFrames { get; set; } = false;

    [JsonPropertyName("EnableActiveClientHighlight")]
    public bool EnableActiveClientHighlight { get; set; } = false;

    [JsonPropertyName("ActiveClientHighlightColor")]
    public string? ActiveClientHighlightColor { get; set; }

    [JsonPropertyName("ActiveClientHighlightThickness")]
    public int ActiveClientHighlightThickness { get; set; } = 3;

    [JsonPropertyName("PerClientLayout")]
    public Dictionary<string, Dictionary<string, string>>? PerClientLayout { get; set; }

    [JsonPropertyName("FlatLayout")]
    public Dictionary<string, string>? FlatLayout { get; set; }

    [JsonPropertyName("DisableThumbnail")]
    public Dictionary<string, bool>? DisableThumbnail { get; set; }

    // ── Conversion: EveOConfigRoot → AppSettings ────────────────────

    public AppSettings ToAppSettings()
    {
        var s = new AppSettings();

        // Safe parsing for "Width, Height" or "X, Y" WinForms string serializations
        var thumbSize = ParseSize(ThumbnailSize, 384, 216);
        var minSize = ParseSize(ThumbnailMinimumSize, 192, 108);

        s.ThumbnailStartLocation = new ThumbnailRect { X = 20, Y = 20, Width = thumbSize.W, Height = thumbSize.H };
        s.ThumbnailMinimumSize = new ThumbnailSize { Width = minSize.W, Height = minSize.H };
        s.ThumbnailSnap = EnableThumbnailSnap;
        s.HideActiveThumbnail = HideActiveClientThumbnail;

        var profile = new Profile();
        profile.ShowThumbnailsAlwaysOnTop = ShowThumbnailsAlwaysOnTop;
        profile.ThumbnailOpacity = (int)(ThumbnailsOpacity * 255);
        profile.MinimizeInactiveClients = MinimizeInactiveClients;
        profile.ShowClientHighlightBorder = EnableActiveClientHighlight;
        profile.ClientHighlightBorderThickness = ActiveClientHighlightThickness;
        profile.ShowThumbnailTextOverlay = ShowThumbnailOverlays;
        profile.ShowAllColoredBorders = ShowThumbnailFrames;

        if (!string.IsNullOrWhiteSpace(ActiveClientHighlightColor))
        {
            // WinForms colors might be named like "GreenYellow" or argb.
            // Mapping named colors robustly is complex without System.Drawing, so we default if hex parsing fails.
            if (ActiveClientHighlightColor.StartsWith("#"))
                profile.ClientHighlightColor = ActiveClientHighlightColor;
        }

        // Layouts
        if (FlatLayout != null)
        {
            foreach (var kvp in FlatLayout)
            {
                var pt = ParsePoint(kvp.Value);
                // Clean character name from "EVE - "
                string charName = ExtractCharacterName(kvp.Key);
                if (!string.IsNullOrEmpty(charName))
                {
                    profile.ThumbnailPositions[charName] = new ThumbnailRect 
                    { 
                        X = pt.X, 
                        Y = pt.Y, 
                        Width = thumbSize.W, 
                        Height = thumbSize.H 
                    };
                }
            }
        }

        if (DisableThumbnail != null)
        {
            foreach (var kvp in DisableThumbnail)
            {
                string charName = ExtractCharacterName(kvp.Key);
                if (!string.IsNullOrEmpty(charName))
                {
                    profile.ThumbnailVisibility[charName] = kvp.Value ? 0 : 1;
                }
            }
        }

        s.Profiles["Default"] = profile;
        s.LastUsedProfile = "Default";

        return s;
    }

    private static (int W, int H) ParseSize(string? val, int defW, int defH)
    {
        if (string.IsNullOrWhiteSpace(val)) return (defW, defH);
        var parts = val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            return (w, h);
        return (defW, defH);
    }

    private static (int X, int Y) ParsePoint(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return (0, 0);
        var parts = val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
            return (x, y);
        return (0, 0);
    }

    private static string ExtractCharacterName(string title)
    {
        const string prefix = "EVE - ";
        if (title.StartsWith(prefix))
            return title.Substring(prefix.Length).Trim();
        return title;
    }
}
