using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>One place a hotkey is used: a hotkey setting, a character key, a group key.</summary>
public sealed record HotkeyUse(string Id, string Label, string Hotkey);

/// <summary>Every hotkey in use, for the capture dialog's clash warning (spec §6) and the
/// conflict check page (Plan 3).</summary>
public static class HotkeyUses
{
    /// <summary>Uses that aren't hotkey settings: character keys and group keys. Plan 3 adds them.</summary>
    public static readonly List<Func<AppSettings, IEnumerable<HotkeyUse>>> Sources = new();

    public static IEnumerable<HotkeyUse> All(AppSettings s, IEnumerable<SettingDescriptor> settings) =>
        settings.Where(d => d.Kind == SettingKind.Hotkey)
                .Select(d => new HotkeyUse(d.Id, SettingsContext.Str(d.LabelKey), d.Get(s) as string ?? ""))
                .Concat(Sources.SelectMany(source => source(s)))
                .Where(u => u.Hotkey.Trim().Length > 0);

    public static List<HotkeyUse> Clashes(AppSettings s, IEnumerable<SettingDescriptor> settings, string candidate, string exceptId) =>
        All(s, settings).Where(u => u.Id != exceptId && HotkeyText.Clashes(u.Hotkey, candidate, s.HotkeyWildcardModifiers)).ToList();
}
