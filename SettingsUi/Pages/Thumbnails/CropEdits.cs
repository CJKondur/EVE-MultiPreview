using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>Crop window rules (the old crop panel, SettingsWindow.xaml.cs lines 2131–2553) on
/// Profile.Crops: character → its crop windows. Reading never writes.</summary>
public static partial class CropEdits
{
    /// <summary>The largest coordinate or size a box accepts.</summary>
    public const int Limit = 20000;

    /// <summary>Crop opacity in percent; CropWindow clamps to the same range (#62).</summary>
    public const int MinOpacity = 10, MaxOpacity = 100;

    /// <summary>The part of the client: client pixels from its top-left corner, at least 1 × 1.</summary>
    public static readonly (int X, int Y, int W, int H) SourceMin = (0, 0, 1, 1);

    /// <summary>The window on screen, in DIPs: negative on a monitor left of or above the main one;
    /// at least 40 × 30, CropManager's minimum.</summary>
    public static readonly (int X, int Y, int W, int H) PopupMin = (-Limit, -Limit, 40, 30);

    /// <summary>Every known character, running first: the picker offers a character before it has crops,
    /// and the character page can open this page on any of them.</summary>
    public static IReadOnlyList<string> Characters(AppSettings s, IEnumerable<string> running) => CharacterDirectory.Known(s, running);

    /// <summary>The stored key for this character, found ignoring case; the name itself when it has none.</summary>
    public static string KeyOf(AppSettings s, string character) =>
        s.Crops.Keys.FirstOrDefault(k => string.Equals(k, character, StringComparison.OrdinalIgnoreCase)) ?? character;

    public static IReadOnlyList<CropDefinition> Of(AppSettings s, string character) =>
        s.Crops.TryGetValue(KeyOf(s, character), out var list) ? list : Array.Empty<CropDefinition>();

    /// <summary>"Crop 1", "Crop 2"…: the next number for this character, in the given format.</summary>
    public static string NextName(AppSettings s, string character, string format) =>
        string.Format(CultureInfo.CurrentCulture, format, Of(s, character).Count + 1);

    /// <summary>Old OnAddCrop: a new window, staggered 30 px from the last.</summary>
    public static CropDefinition Add(AppSettings s, string character, string name)
    {
        var key = KeyOf(s, character);
        if (!s.Crops.TryGetValue(key, out var list)) s.Crops[key] = list = new List<CropDefinition>();
        var def = new CropDefinition { Name = name, PopupX = 100 + list.Count * 30, PopupY = 100 + list.Count * 30 };
        list.Add(def);
        return def;
    }

    /// <summary>Old DeleteCrop: the character's key goes with its last crop.</summary>
    public static bool Delete(AppSettings s, string character, string id)
    {
        var key = KeyOf(s, character);
        if (!s.Crops.TryGetValue(key, out var list)) return false;
        bool removed = list.RemoveAll(c => string.Equals(c.Id, id, StringComparison.Ordinal)) > 0;
        if (list.Count == 0) s.Crops.Remove(key);
        return removed;
    }

    /// <summary>Old CloneCrop (#82): a fresh id, everything else the same, the window at the same place.</summary>
    public static CropDefinition Clone(CropDefinition c) => new()
    {
        Name = c.Name,
        SourceX = c.SourceX, SourceY = c.SourceY, SourceWidth = c.SourceWidth, SourceHeight = c.SourceHeight,
        PopupX = c.PopupX, PopupY = c.PopupY, PopupWidth = c.PopupWidth, PopupHeight = c.PopupHeight,
        ShowLabel = c.ShowLabel, Opacity = c.Opacity, ClickThrough = c.ClickThrough,
    };

    /// <summary>Old CopyCropToTargets: one fresh copy per target. Returns how many were made. A copy replaces the
    /// target's thumbnail only when the source does and the target has no replacement yet (PR #117).</summary>
    public static int CopyTo(AppSettings s, CropDefinition source, IEnumerable<string> targets)
    {
        int made = 0;
        foreach (var target in targets)
        {
            var key = KeyOf(s, target);
            if (!s.Crops.TryGetValue(key, out var list)) s.Crops[key] = list = new List<CropDefinition>();
            var copy = Clone(source);
            copy.ReplaceThumbnail = source.ReplaceThumbnail && !list.Any(c => c.ReplaceThumbnail);
            list.Add(copy);
            made++;
        }
        return made;
    }

    /// <summary>The crop shown in the character's thumbnail instead of the whole client (PR #117): its first crop
    /// with "Replace thumbnail" on, while crop windows are on; null = the whole client.</summary>
    public static CropDefinition? ThumbnailCrop(AppSettings s, string character) =>
        s.CropEnabled && !string.IsNullOrEmpty(character) ? Of(s, character).FirstOrDefault(c => c.ReplaceThumbnail) : null;

    /// <summary>"Replace thumbnail" can be turned on for this crop: it is on already, or no other crop of the
    /// character has it (one per client).</summary>
    public static bool CanReplaceThumbnail(AppSettings s, string character, CropDefinition def) =>
        def.ReplaceThumbnail || !Of(s, character).Any(c => c.ReplaceThumbnail);

    /// <summary>Turns "Replace thumbnail" on or off. On turns it off for the character's other crops, so the
    /// saved file holds one per client however it is set.</summary>
    public static void SetReplaceThumbnail(AppSettings s, string character, CropDefinition def, bool on)
    {
        if (on)
            foreach (var other in Of(s, character))
                other.ReplaceThumbnail = false;
        def.ReplaceThumbnail = on;
    }

    public static void SetSource(CropDefinition d, (int X, int Y, int W, int H) r)
    {
        d.SourceX = Math.Max(SourceMin.X, r.X);
        d.SourceY = Math.Max(SourceMin.Y, r.Y);
        d.SourceWidth = Math.Max(SourceMin.W, r.W);
        d.SourceHeight = Math.Max(SourceMin.H, r.H);
    }

    public static void SetPopup(CropDefinition d, (int X, int Y, int W, int H) r)
    {
        d.PopupX = r.X;
        d.PopupY = r.Y;
        d.PopupWidth = Math.Max(PopupMin.W, r.W);
        d.PopupHeight = Math.Max(PopupMin.H, r.H);
    }

    /// <summary>The picked part (client pixels) becomes the source, and the window takes the same size on
    /// screen: client pixels over the client monitor's scale give DIPs (#56). At least 40 × 30.</summary>
    public static void ApplyPick(CropDefinition d, (int X, int Y, int W, int H) picked, double scale)
    {
        if (scale <= 0) scale = 1;
        d.SourceX = picked.X;
        d.SourceY = picked.Y;
        d.SourceWidth = picked.W;
        d.SourceHeight = picked.H;
        d.PopupWidth = Math.Max(PopupMin.W, (int)Math.Round(picked.W / scale));
        d.PopupHeight = Math.Max(PopupMin.H, (int)Math.Round(picked.H / scale));
    }

    /// <summary>Everything the editor shows for one character, to notice a crop window being dragged.</summary>
    public static string Signature(AppSettings s, string? character) =>
        character == null ? "" : string.Join("\n", Of(s, character).Select(d => string.Join(",",
            d.Id, d.Name, d.ShowLabel, d.SourceX, d.SourceY, d.SourceWidth, d.SourceHeight,
            d.PopupX, d.PopupY, d.PopupWidth, d.PopupHeight, d.Opacity, d.ClickThrough, d.ReplaceThumbnail)));
}

/// <summary>One "Edit area" (Plan 2 "After this plan"): the settings window hides when the pick starts
/// and comes back exactly once, however the picker ends: a drag, Esc, a plain click, Alt+F4, or a
/// picker that fails to open.</summary>
public sealed class CropPickSession
{
    private readonly Action _restore;

    public CropPickSession(Action hide, Action restore)
    {
        _restore = restore;
        hide();
    }

    public bool IsDone { get; private set; }

    /// <summary>The picker ended. A picked part is stored before the window comes back; null = cancelled.
    /// Only the first call counts.</summary>
    public void End((int X, int Y, int W, int H)? picked, Action<(int X, int Y, int W, int H)> store)
    {
        if (IsDone) return;
        IsDone = true;
        try
        {
            if (picked is { } r) store(r);
        }
        finally
        {
            _restore();
        }
    }
}
