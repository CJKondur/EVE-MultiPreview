using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Two uses whose keys collide. Key is the first use's hotkey string.</summary>
public sealed record HotkeyClash(string Key, HotkeyUse First, HotkeyUse Second)
{
    /// <summary>The key to show: both when the two uses' keys differ (wildcard modifiers), e.g. "A / Ctrl + A".</summary>
    public string KeyText =>
        string.Equals(First.Hotkey.Trim(), Second.Hotkey.Trim(), StringComparison.OrdinalIgnoreCase)
            ? HotkeyText.ToDisplay(First.Hotkey)
            : HotkeyText.ToDisplay(First.Hotkey) + " / " + HotkeyText.ToDisplay(Second.Hotkey);
}

/// <summary>The conflict check (spec §5, replaces the Hotkey Conflict Audit), with the capture
/// dialog's clash rule.</summary>
public static class HotkeyConflicts
{
    public static List<HotkeyClash> Find(IReadOnlyList<HotkeyUse> uses, bool wildcard)
    {
        var clashes = new List<HotkeyClash>();
        for (int i = 0; i < uses.Count; i++)
            for (int j = i + 1; j < uses.Count; j++)
                if (HotkeyText.Clashes(uses[i].Hotkey, uses[j].Hotkey, wildcard) && !OnPurpose(uses[i], uses[j]))
                    clashes.Add(new HotkeyClash(uses[i].Hotkey, uses[i], uses[j]));
        return clashes;
    }

    /// <summary>Shared on purpose, so not a conflict (HotkeyService.RegisterFromSettings): characters
    /// with the same key take turns, and groups with the same next (or previous) key share one list.</summary>
    public static bool OnPurpose(HotkeyUse a, HotkeyUse b)
    {
        if (!string.Equals(a.Hotkey.Trim(), b.Hotkey.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        static bool Character(HotkeyUse u) => u.Id.StartsWith(HotkeyUses.CharacterPrefix, StringComparison.Ordinal);
        if (Character(a) && Character(b)) return true;
        return HotkeyUses.TryParseGroup(a.Id, out _, out var aNext)
               && HotkeyUses.TryParseGroup(b.Id, out _, out var bNext)
               && aNext == bNext;
    }

    /// <summary>Where a use is set: (page, card to highlight). A key on two pages opens the one
    /// outside Home. Null when nothing sets it.</summary>
    public static (string PageId, string? ItemId)? Locate(string useId, IEnumerable<PageDef> pages)
    {
        if (useId.StartsWith(HotkeyUses.CharacterPrefix, StringComparison.Ordinal))
            return (CharacterPage.IdFor(useId[HotkeyUses.CharacterPrefix.Length..]), "character.hotkey");
        if (HotkeyUses.TryParseGroup(useId, out _, out _))
            return (ClientsPages.GroupsId, "clients.groups.editor");
        foreach (var page in pages.Where(p => p.Group != NavGroup.Home).Concat(pages.Where(p => p.Group == NavGroup.Home)))
            foreach (var item in page.Sections.SelectMany(s => s.Items).OfType<SettingItem>())
                if (item.Setting.Kind == SettingKind.Hotkey && HotkeyUses.IdOf(item.Setting) == useId)
                    return (page.Id, item.Id);
        return null;
    }
}
