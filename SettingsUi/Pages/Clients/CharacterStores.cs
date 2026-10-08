using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Per-character reads and writes shared by the Characters table, Home tiles and the
/// character page. Each writes one store, as the old window's lists did.</summary>
public static class CharacterStores
{
    private static Profile Cp(AppSettings s) => CharacterDirectory.Current(s);

    public static string HotkeyOf(AppSettings s, string name) =>
        Cp(s).Hotkeys.TryGetValue(name, out var b) ? b.Key ?? "" : "";

    /// <summary>A key creates the entry; clearing keeps an existing entry with no key (HotkeyService
    /// skips it) and never creates one.</summary>
    public static void SetHotkey(AppSettings s, string name, string key)
    {
        var hotkeys = Cp(s).Hotkeys;
        if (hotkeys.TryGetValue(name, out var b)) b.Key = key;
        else if (key.Length > 0) hotkeys[name] = new HotkeyBinding { Key = key };
    }

    /// <summary>The capture dialog for a character's key. Stores it; App re-registers on Applied.</summary>
    public static bool CaptureHotkey(SettingsContext ctx, string name)
    {
        var key = ctx.Host.CaptureHotkey(HotkeyUses.CharacterId(name), HotkeyOf(ctx.S, name));
        if (key == null) return false;
        SetHotkey(ctx.S, name, key);
        ctx.Applier.Request(ApplyScope.Save);
        return true;
    }

    /// <summary>Profile.ThumbnailVisibility: 0 or missing = shown, anything else = hidden.</summary>
    public static bool IsShown(AppSettings s, string name) =>
        !(s.ThumbnailVisibility.TryGetValue(name, out var v) && v != 0);

    public static void StoreShown(AppSettings s, string name, bool shown) => s.ThumbnailVisibility[name] = shown ? 0 : 1;

    /// <summary>As the old visibility list: store, show or hide that one thumbnail now, and only
    /// save. A full re-apply resized every thumbnail and re-showed hidden ones (#50, #51).</summary>
    public static void SetShown(SettingsContext ctx, string name, bool shown)
    {
        StoreShown(ctx.S, name, shown);
        ctx.Services.Thumbnails?.SetCharacterVisibility(name, shown);
        ctx.Applier.Request(ApplyScope.Save);
    }

    public static string? SystemOf(SettingsContext ctx, string name) => ctx.Services.Thumbnails?.GetCharacterSystem(name);

    public static IReadOnlyList<string> GroupsOf(AppSettings s, string name) =>
        MergedGroups.Build(s).Where(g => g.Members.Contains(name, StringComparer.OrdinalIgnoreCase)).Select(g => g.Name).ToList();

    /// <summary>The session-only cycle exclusion (Shift+click by default). Null without a ThumbnailManager.</summary>
    public static bool? IsInCycle(SettingsContext ctx, string name) =>
        ctx.Services.Thumbnails is { } tm ? !tm.IsExcludedFromCycle(name) : null;

    public static void SetInCycle(SettingsContext ctx, string name, bool included)
    {
        var tm = ctx.Services.Thumbnails;
        if (tm != null && tm.IsExcludedFromCycle(name) == included) tm.ToggleCycleExclusion(name);
    }

    /// <summary>Old OnHotkeyAddAllActive: an empty key for each running character without an entry.</summary>
    public static int AddRunning(AppSettings s, IEnumerable<string> running)
    {
        var hotkeys = Cp(s).Hotkeys;
        int added = 0;
        foreach (var name in running)
        {
            if (hotkeys.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;
            hotkeys[name] = new HotkeyBinding();
            added++;
        }
        return added;
    }

    /// <summary>Old OnHotkeyAdd: an empty key entry, so the name stays on the list.</summary>
    public static bool Add(AppSettings s, string name)
    {
        name = name.Trim();
        var hotkeys = Cp(s).Hotkeys;
        if (name.Length == 0 || hotkeys.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) return false;
        hotkeys[name] = new HotkeyBinding();
        return true;
    }

    /// <summary>Spec §6: search covers characters; each entry opens that character's page.</summary>
    public static IEnumerable<SearchEntry> SearchEntries(IEnumerable<string> names, Func<string, string> resolve)
    {
        var path = resolve("L.S.Nav.Clients") + " › " + resolve("L.S.Page." + ClientsPages.CharactersId);
        return names.Select(n => new SearchEntry(SearchKind.Character, n, "", path, CharacterPage.IdFor(n), null)).ToList();
    }
}
