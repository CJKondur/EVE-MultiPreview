using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>One group as the player sees it (spec §8.3, D3): the colour group (global, shared by
/// every profile) and this profile's hotkey group with the same name. Either half may be missing.</summary>
public sealed class MergedGroup
{
    public MergedGroup(string name, ThumbnailGroup? colour, HotkeyGroup? keys)
    {
        Name = name;
        Colour = colour;
        Keys = keys;
    }

    public string Name { get; internal set; }
    /// <summary>The AppSettings.ThumbnailGroups entry: name, colour, members. Every profile.</summary>
    public ThumbnailGroup? Colour { get; internal set; }
    /// <summary>The current profile's Profile.HotkeyGroups entry: keys and switching order.</summary>
    public HotkeyGroup? Keys { get; internal set; }

    public string NextKey => Keys?.ForwardsHotkey ?? "";
    public string PreviousKey => Keys?.BackwardsHotkey ?? "";

    /// <summary>Spec §8.3: the hotkey group's order, then colour-group members not already listed.</summary>
    public IReadOnlyList<string> Members
    {
        get
        {
            var list = new List<string>(Keys?.Characters ?? new List<string>());
            foreach (var m in Colour?.Members ?? new List<string>())
                if (!list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
            return list;
        }
    }
}

/// <summary>Pairs and edits merged groups. Building never writes; every edit writes only the
/// half it must (spec §8.3).</summary>
public static class MergedGroups
{
    /// <summary>Colour groups in saved order (the first of several with one name pairs with the
    /// hotkey group of that exact name), then hotkey-only groups in this profile's order.</summary>
    public static IReadOnlyList<MergedGroup> Build(AppSettings s)
    {
        var keys = s.HotkeyGroups;
        var paired = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<MergedGroup>();
        foreach (var colour in s.ThumbnailGroups)
        {
            HotkeyGroup? half = null;
            if (paired.Add(colour.Name)) keys.TryGetValue(colour.Name, out half);
            list.Add(new MergedGroup(colour.Name, colour, half));
        }
        foreach (var (name, group) in keys)
            if (!paired.Contains(name)) list.Add(new MergedGroup(name, null, group));
        return list;
    }

    /// <summary>Free when no colour group and no hotkey group of ANY profile uses the name.</summary>
    public static bool IsNameFree(AppSettings s, string name) =>
        !s.ThumbnailGroups.Any(g => g.Name == name)
        && !s.HotkeyGroups.ContainsKey(name)
        && !s.Profiles.Values.Any(p => p.HotkeyGroups.ContainsKey(name));

    /// <summary>"New group": an empty hotkey half in this profile. Null for a blank or used name.</summary>
    public static MergedGroup? New(AppSettings s, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || !IsNameFree(s, name)) return null;
        var before = Services.GroupAlertChoices.GroupNames(s);
        var keys = new HotkeyGroup();
        s.HotkeyGroups[name] = keys;
        // IDEAS #31: a group that comes into being under a free name starts with no alert choices, whatever an
        // earlier group left under that name.
        Services.GroupAlertChoices.Follow(s, before);
        return new MergedGroup(name, null, keys);
    }

    /// <summary>Creates the colour half (with the members shown) or recolours it.</summary>
    public static void SetColor(AppSettings s, MergedGroup g, string color)
    {
        if (g.Colour == null)
        {
            var colour = new ThumbnailGroup { Name = g.Name, Color = color, Members = g.Members.ToList() };
            s.ThumbnailGroups.Add(colour);
            g.Colour = colour;
        }
        else g.Colour.Color = color;
    }

    /// <summary>Creates this profile's hotkey half (in the order shown) or sets one key. False when
    /// another group with the same name already owns the hotkey half (a duplicate colour group).</summary>
    public static bool SetKey(AppSettings s, MergedGroup g, bool next, string key)
    {
        if (g.Keys == null)
        {
            if (key.Length == 0) return true;   // clearing a key on a group with no key half: nothing to clear
            if (s.HotkeyGroups.ContainsKey(g.Name)) return false;
            g.Keys = new HotkeyGroup { Characters = g.Members.ToList() };
            s.HotkeyGroups[g.Name] = g.Keys;
        }
        if (next) g.Keys.ForwardsHotkey = key;
        else g.Keys.BackwardsHotkey = key;
        return true;
    }

    /// <summary>Writes the same list to both halves when both exist (spec §8.3).</summary>
    public static void SetMembers(MergedGroup g, IEnumerable<string> members)
    {
        var list = members.ToList();
        if (g.Keys != null) g.Keys.Characters = new List<string>(list);
        if (g.Colour != null) g.Colour.Members = new List<string>(list);
    }

    public static bool AddMember(MergedGroup g, string name)
    {
        if (g.Members.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        SetMembers(g, g.Members.Append(name));
        return true;
    }

    public static void RemoveMember(MergedGroup g, string name) =>
        SetMembers(g, g.Members.Where(m => !string.Equals(m, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Moves a member to another member's place, or to the end when <paramref name="target"/>
    /// is null (the old pill drag: remove, then insert at the target's old index).</summary>
    public static void MoveMember(MergedGroup g, string name, string? target)
    {
        var list = g.Members.ToList();
        int from = list.FindIndex(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        int to = target == null ? list.Count - 1 : list.FindIndex(m => string.Equals(m, target, StringComparison.OrdinalIgnoreCase));
        if (to < 0 || to == from) return;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        SetMembers(g, list);
    }

    /// <summary>Renames both halves: the colour group (global) and the key half in EVERY profile
    /// that has one. False for a blank name or one any group in any profile uses.</summary>
    public static bool Rename(AppSettings s, MergedGroup g, string newName)
    {
        newName = newName.Trim();
        if (newName == g.Name) return true;
        if (newName.Length == 0 || !IsNameFree(s, newName)) return false;
        var before = Services.GroupAlertChoices.GroupNames(s);
        string oldName = g.Name;
        foreach (var profile in s.Profiles.Values)
            if (profile.HotkeyGroups.Remove(g.Name, out var half)) profile.HotkeyGroups[newName] = half;
        if (g.Colour != null) g.Colour.Name = newName;
        // IDEAS #31: the group's alert choices are kept under its name, and go with it. Through the helper every
        // path uses (RE1-m4): where another group still goes by the old name (a second colour group of that name),
        // that group keeps the choices too, so the rename changes nobody's alerts.
        Services.GroupAlertChoices.Follow(s, before, oldName, newName);
        g.Name = newName;
        return true;
    }

    /// <summary><paramref name="everywhere"/>: the colour group (every profile) and this profile's
    /// keys. Otherwise only this profile's keys and order; the colour group stays.</summary>
    public static void Delete(AppSettings s, MergedGroup g, bool everywhere)
    {
        if (g.Keys != null && s.HotkeyGroups.TryGetValue(g.Name, out var k) && ReferenceEquals(k, g.Keys))
            s.HotkeyGroups.Remove(g.Name);
        g.Keys = null;
        if (everywhere && g.Colour != null)
        {
            s.ThumbnailGroups.Remove(g.Colour);   // reference equality: ThumbnailGroup has no Equals
            g.Colour = null;
        }
        // IDEAS #31: while a group of that name is left (its colour half, or its keys in another profile), so are
        // its alert choices and (IDEAS #58) its sounds.
        if (IsNameFree(s, g.Name))
        {
            s.GroupAlertProfiles = Services.GroupAlertChoices.Forget(s.GroupAlertProfiles, g.Name);
            s.GroupAlertSounds = Services.GroupSoundChoices.Forget(s.GroupAlertSounds, g.Name);
        }
    }
}
