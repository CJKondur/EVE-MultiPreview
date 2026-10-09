using System;
using System.Collections.Generic;
using System.Globalization;
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

    public enum ColorPart { Border, Inactive, Text }

    /// <summary>The colour a card shows. Text: the one the thumbnail paints, the profile's text colour until one is chosen
    /// (ThumbnailManager.TextColourFor).</summary>
    public static string Color(AppSettings s, string name, ColorPart part)
    {
        s.CustomColors.TryGetValue(name, out var e);
        return part switch
        {
            ColorPart.Border => string.IsNullOrEmpty(e?.Border) ? DefaultBorder : e.Border,
            ColorPart.Inactive => string.IsNullOrEmpty(e?.InactiveBorder) ? DefaultInactive : e.InactiveBorder,
            _ => e != null && CustomColorEntry.HasOwnText(e.Text) ? e.Text : s.ThumbnailTextColor,
        };
    }

    /// <summary>The first edit makes the entry. A border edit seeds the old defaults for the other two; a text-colour edit
    /// leaves both borders empty (ThumbnailManager skips an empty border), so choosing a text colour never changes a
    /// border. A text colour is kept as picked (#RRGGBB): the seed 0xfac57a means "none chosen", so a chosen #FAC57A
    /// must stay apart from it.</summary>
    public static void SetColor(AppSettings s, string name, ColorPart part, string color)
    {
        if (!s.CustomColors.TryGetValue(name, out var e))
        {
            bool textOnly = part == ColorPart.Text;
            s.CustomColors[name] = e = new CustomColorEntry
            {
                Char = name, Text = DefaultText,
                Border = textOnly ? "" : DefaultBorder, InactiveBorder = textOnly ? "" : DefaultInactive,
            };
        }
        switch (part)
        {
            case ColorPart.Border: e.Border = Normalize(color); break;
            case ColorPart.Inactive: e.InactiveBorder = Normalize(color); break;
            default: e.Text = color.Trim(); break;
        }
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

    // ── Client window (Profile.DontMinimizeClients, PerClientCores, PerClientAudioVolume) ──

    public static bool NeverMinimize(AppSettings s, string name) =>
        CharacterDirectory.Current(s).DontMinimizeClients.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>At most once, whatever the case (the old Add had no duplicate check).</summary>
    public static void SetNeverMinimize(AppSettings s, string name, bool on)
    {
        var list = CharacterDirectory.Current(s).DontMinimizeClients;
        list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (on) list.Add(name);
    }

    public const int AutoCore = -1;

    /// <summary>A core this PC doesn't have reads as Automatic, as the old core table showed it.</summary>
    public static int Core(AppSettings s, string name) =>
        s.PerClientCores.TryGetValue(name, out var c) && c >= 0 && c < Environment.ProcessorCount ? c : AutoCore;

    /// <summary>A stored pin this PC can't use (a core it doesn't have, or a negative number), else null. The affinity
    /// pass can't apply it either, and Core() reads it as Automatic, so without this it could never be cleared.</summary>
    public static int? StalePin(AppSettings s, string name) =>
        s.PerClientCores.TryGetValue(name, out var c) && c != AutoCore && (c < 0 || c >= Environment.ProcessorCount) ? c : null;

    public static void SetCore(AppSettings s, string name, int core)
    {
        if (core < 0) s.PerClientCores.Remove(name);
        else s.PerClientCores[name] = core;
    }

    public static IReadOnlyList<ChoiceOption> CoreOptions() =>
        new[] { new ChoiceOption(AutoCore, "L.S.Opt.CoreAuto") }
            .Concat(Enumerable.Range(0, Environment.ProcessorCount).Select(i =>
                new ChoiceOption(i, "") { Literal = string.Format(CultureInfo.CurrentCulture, SettingsContext.Str("L.S.Opt.CoreN"), i) }))
            .ToList();

    public static int Volume(AppSettings s, string name) =>
        s.PerClientAudioVolume.TryGetValue(name, out var v) ? Math.Clamp(v, 0, 100) : 100;

    /// <summary>ThumbnailManager.SetClientVolume's rule: 100 % (or more) = no entry.</summary>
    public static void SetVolume(AppSettings s, string name, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent >= 100) s.PerClientAudioVolume.Remove(name);
        else s.PerClientAudioVolume[name] = percent;
    }

    // ── Stats overlay by category (AppSettings.PerCharacterStats) ──

    public static readonly IReadOnlyList<ChoiceOption> StatsOptions = new[]
    {
        new ChoiceOption(0, "L.S.Opt.StatsDefault"),
        new ChoiceOption(1, "L.S.Opt.StatsOn"),
        new ChoiceOption(2, "L.S.Opt.StatsOff"),
    };

    /// <summary>0 = default (follows the numbers chosen for everyone), 1 = all on, 2 = all off,
    /// 3 = mixed (numbers set one by one; no option selected).</summary>
    public static int StatsCategory(AppSettings s, string name, StatMetrics mask)
    {
        if (!s.PerCharacterStats.TryGetValue(name, out var c)) return 0;
        var on = c.ForcedOn & mask;
        var off = c.ForcedOff & mask;
        if (on == 0 && off == 0) return 0;
        if (on == mask && off == 0) return 1;
        if (off == mask && on == 0) return 2;
        return 3;
    }

    /// <summary>The old editor's category buttons (All on / All off / Inherit). No entry is kept
    /// once nothing is forced.</summary>
    public static void SetStatsCategory(AppSettings s, string name, StatMetrics mask, int state)
    {
        if (state is < 0 or > 2) return;
        if (!s.PerCharacterStats.TryGetValue(name, out var c)) s.PerCharacterStats[name] = c = new CharacterStatSettings();
        c.SetOverrideState(mask, state == 1 ? true : state == 2 ? false : null);
        if (c.ForcedOn == StatMetrics.None && c.ForcedOff == StatMetrics.None) s.PerCharacterStats.Remove(name);
    }
}
