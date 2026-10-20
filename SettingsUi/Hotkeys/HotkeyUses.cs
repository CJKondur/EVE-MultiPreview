using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>One place a hotkey is used. Id is the use id (HotkeyUses.IdOf, CharacterId, GroupNextId).</summary>
public sealed record HotkeyUse(string Id, string Label, string Hotkey);

/// <summary>Every hotkey in use, for the capture dialog's clash warning (spec §6) and the conflict
/// check (Clients › Hotkeys &amp; switching).</summary>
public static class HotkeyUses
{
    public const string CharacterPrefix = "character:";
    public const string GroupPrefix = "group:";
    private const string NextSuffix = ":next";
    private const string PreviousSuffix = ":previous";

    /// <summary>Uses that aren't hotkey cards: character keys and group keys (registered by the
    /// Clients pages). A source may return uses with no key; All() drops them.</summary>
    public static readonly List<Func<AppSettings, IEnumerable<HotkeyUse>>> Sources = new() { CharacterKeys, GroupKeys };

    /// <summary>This profile's character keys, set or not (Clients › Characters).</summary>
    public static IEnumerable<HotkeyUse> CharacterKeys(AppSettings s) =>
        CharacterDirectory.Current(s).Hotkeys.Select(kv =>
            new HotkeyUse(CharacterId(kv.Key), string.Format(SettingsContext.Str("L.S.Use.Character"), kv.Key), kv.Value?.Key ?? ""));

    /// <summary>This profile's group keys, set or not (Clients › Groups).</summary>
    public static IEnumerable<HotkeyUse> GroupKeys(AppSettings s) =>
        s.HotkeyGroups.SelectMany(kv => new[]
        {
            new HotkeyUse(GroupNextId(kv.Key), string.Format(SettingsContext.Str("L.S.Use.GroupNext"), kv.Key), kv.Value.ForwardsHotkey ?? ""),
            new HotkeyUse(GroupPreviousId(kv.Key), string.Format(SettingsContext.Str("L.S.Use.GroupPrevious"), kv.Key), kv.Value.BackwardsHotkey ?? ""),
        });

    public static string IdOf(SettingDescriptor d) => d.UseId ?? d.Id;
    public static string CharacterId(string name) => CharacterPrefix + name;
    public static string GroupNextId(string group) => GroupPrefix + group + NextSuffix;
    public static string GroupPreviousId(string group) => GroupPrefix + group + PreviousSuffix;

    public static bool TryParseGroup(string id, out string group, out bool next)
    {
        group = "";
        next = false;
        if (!id.StartsWith(GroupPrefix, StringComparison.Ordinal)) return false;
        if (id.EndsWith(NextSuffix, StringComparison.Ordinal))
        {
            next = true;
            group = id[GroupPrefix.Length..^NextSuffix.Length];
            return true;
        }
        if (id.EndsWith(PreviousSuffix, StringComparison.Ordinal))
        {
            group = id[GroupPrefix.Length..^PreviousSuffix.Length];
            return true;
        }
        return false;
    }

    /// <summary>Every use with a key, each use id once: hotkey cards that are shown (a hidden card,
    /// like the char-select keys while that's off, isn't registered either), then the sources.</summary>
    /// <param name="masked">IDEAS #19: for a screen that can be shared (the hotkey cheat sheet, the refused-keys balloon) a
    /// character's or group's name in the label follows streamer mode. The settings pages leave it off: they list the real names.</param>
    public static IEnumerable<HotkeyUse> All(AppSettings s, IEnumerable<SettingDescriptor> settings, bool masked = false)
    {
        var uses = settings.Where(d => d.Kind == SettingKind.Hotkey && (d.ShowIf?.Invoke(s) ?? true))
                .Select(d => new HotkeyUse(IdOf(d), SettingsContext.Str(d.LabelKey), d.Get(s) as string ?? ""))
                .Concat(Sources.SelectMany(source => source(s)))
                .Where(u => u.Hotkey.Trim().Length > 0)
                .DistinctBy(u => u.Id);
        return masked && StreamerMask.IsOn ? uses.Select(Masked) : uses;
    }

    /// <summary>The use with its character's or group's name drawn as streamer mode says. The id keeps the real name.</summary>
    private static HotkeyUse Masked(HotkeyUse u)
    {
        if (u.Id.StartsWith(CharacterPrefix, StringComparison.Ordinal))
            return u with { Label = string.Format(SettingsContext.Str("L.S.Use.Character"), StreamerMask.Name(StreamerKind.Character, u.Id[CharacterPrefix.Length..])) };
        if (TryParseGroup(u.Id, out var group, out var next))
            return u with { Label = string.Format(SettingsContext.Str(next ? "L.S.Use.GroupNext" : "L.S.Use.GroupPrevious"), StreamerMask.Name(StreamerKind.Group, group)) };
        return u;
    }

    public static List<HotkeyUse> Clashes(AppSettings s, IEnumerable<SettingDescriptor> settings, string candidate, string exceptId) =>
        All(s, settings).Where(u => u.Id != exceptId && HotkeyText.Clashes(u.Hotkey, candidate, s.HotkeyWildcardModifiers)).ToList();

    /// <summary>The capture dialog's title for a use id: the card's label, "Switch to Logi",
    /// "Group Miners: next client", or a source's label. Null when nothing has that id.</summary>
    public static string? LabelFor(AppSettings s, IEnumerable<SettingDescriptor> settings, string id)
    {
        var card = settings.FirstOrDefault(d => d.Kind == SettingKind.Hotkey && IdOf(d) == id);
        if (card != null) return SettingsContext.Str(card.LabelKey);
        if (id.StartsWith(CharacterPrefix, StringComparison.Ordinal))
            return string.Format(SettingsContext.Str("L.S.Use.Character"), id[CharacterPrefix.Length..]);
        if (TryParseGroup(id, out var group, out var next))
            return string.Format(SettingsContext.Str(next ? "L.S.Use.GroupNext" : "L.S.Use.GroupPrevious"), group);
        return Sources.SelectMany(source => source(s)).FirstOrDefault(u => u.Id == id)?.Label;
    }
}
