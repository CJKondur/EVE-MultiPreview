using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>Get/set rules of the character page's cards, each on one existing store and with the
/// old editor's rules. Reading never writes.</summary>
public static class CharacterFields
{
    // ── Label (AppSettings.ThumbnailAnnotations / ThumbnailLabelStyles, every profile) ──

    public static string Label(AppSettings s, string name) => s.ThumbnailAnnotations.TryGetValue(name, out var t) ? t : "";

    /// <summary>LabelEditorWindow: blank text removes the label.</summary>
    public static void SetLabel(AppSettings s, string name, string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) s.ThumbnailAnnotations.Remove(name);
        else s.ThumbnailAnnotations[name] = text;
    }

    public static string LabelColor(AppSettings s, string name) =>
        s.ThumbnailLabelStyles.TryGetValue(name, out var st) ? st.Color ?? "" : "";

    public static int LabelSize(AppSettings s, string name) =>
        s.ThumbnailLabelStyles.TryGetValue(name, out var st) ? st.Size : 0;

    public static void SetLabelColor(AppSettings s, string name, string? color) => SetStyle(s, name, Normalize(color?.Trim() ?? ""), LabelSize(s, name));
    public static void SetLabelSize(AppSettings s, string name, int size) => SetStyle(s, name, LabelColor(s, name), Math.Max(0, size));

    /// <summary>LabelEditorWindow: no colour and size 0 = no style entry.</summary>
    private static void SetStyle(AppSettings s, string name, string color, int size)
    {
        if (color.Length == 0 && size == 0) s.ThumbnailLabelStyles.Remove(name);
        else s.ThumbnailLabelStyles[name] = new ThumbnailLabelStyle { Color = color, Size = size };
    }

    /// <summary>The label editor's live push to the thumbnail; no re-apply needed.</summary>
    public static void PushLabel(SettingsContext ctx, string name)
    {
        var text = Label(ctx.S, name);
        var color = LabelColor(ctx.S, name);
        ctx.Services.Thumbnails?.UpdateCharacterLabel(name, text.Length == 0 ? null : text, color.Length == 0 ? null : color, LabelSize(ctx.S, name));
    }

    /// <summary>The colour picker returns #RRGGBB; the old window stored 0xrrggbb. Other formats stay as entered.</summary>
    public static string Normalize(string color) =>
        System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") ? "0x" + color[1..].ToLowerInvariant() : color;

    // ── Custom colours (Profile.CustomColors, gated by CustomColorsActive) ──

    /// <summary>The old window's colours for a new entry (P2:52): active border, text, inactive border.</summary>
    public const string DefaultBorder = "0xe36a0d", DefaultText = "0xfac57a", DefaultInactive = "0x505050";

    public enum ColorPart { Border, Inactive }

    public static string Color(AppSettings s, string name, ColorPart part)
    {
        if (!s.CustomColors.TryGetValue(name, out var e))
            return part == ColorPart.Border ? DefaultBorder : DefaultInactive;
        return part == ColorPart.Border ? e.Border : e.InactiveBorder;
    }

    /// <summary>The first edit makes the entry, seeded with the old defaults for the other two.</summary>
    public static void SetColor(AppSettings s, string name, ColorPart part, string color)
    {
        if (!s.CustomColors.TryGetValue(name, out var e))
            s.CustomColors[name] = e = new CustomColorEntry { Char = name, Border = DefaultBorder, Text = DefaultText, InactiveBorder = DefaultInactive };
        color = Normalize(color);
        if (part == ColorPart.Border) e.Border = color;
        else e.InactiveBorder = color;
    }

    // ── Second thumbnail (Profile.SecondaryThumbnails) ──

    public static bool PipOn(AppSettings s, string name) => s.SecondaryThumbnails.TryGetValue(name, out var p) && p.Enabled != 0;

    /// <summary>On creates the entry or re-enables it; off keeps it (place, size, opacity) disabled.</summary>
    public static void SetPip(AppSettings s, string name, bool on)
    {
        if (s.SecondaryThumbnails.TryGetValue(name, out var p)) p.Enabled = on ? 1 : 0;
        else if (on) s.SecondaryThumbnails[name] = new SecondaryThumbnailSettings();
    }

    public static int PipOpacity(AppSettings s, string name) =>
        OpacityPercent.FromByte(s.SecondaryThumbnails.TryGetValue(name, out var p) ? p.Opacity : new SecondaryThumbnailSettings().Opacity);

    /// <summary>Kept for later when there is no second thumbnail yet: the entry is made disabled.</summary>
    public static void SetPipOpacity(AppSettings s, string name, double percent)
    {
        if (!s.SecondaryThumbnails.TryGetValue(name, out var p))
            s.SecondaryThumbnails[name] = p = new SecondaryThumbnailSettings { Enabled = 0 };
        p.Opacity = OpacityPercent.ToByte(Math.Clamp(percent, 8, 100));
    }
}
