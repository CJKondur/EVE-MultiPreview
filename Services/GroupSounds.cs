using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The sounds groups play for alerts (IDEAS #58), as one line of JSON text in one settings key: a group's
/// name, and under it an event's key (or <see cref="EveryEvent"/>) and the sound file it plays. Shaped like
/// <see cref="GroupAlertChoices"/>, and with the same promises: one line, reading never throws, the limits are
/// reading's and a change that would cross one is not made (the old text comes back).</summary>
public static class GroupSoundChoices
{
    /// <summary>The event key under which a group's sound for every event is kept.</summary>
    public const string EveryEvent = "*";

    public const int MaxText = GroupChoicesText<string>.MaxText, MaxGroups = GroupChoicesText<string>.MaxGroups,
        MaxEvents = GroupChoicesText<string>.MaxEvents, MaxName = GroupChoicesText<string>.MaxName, MaxPath = 1024;

    private static readonly GroupChoicesText<string> Text = new(ReadPath);

    /// <summary>The sounds, by group name (compared exactly, as group names are) and event key.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Read(string? json) => Text.Read(json);

    /// <summary>A sound: a text. Only a file on a drive of this PC (CustomRuleCheck.LocalSound): a settings file can
    /// come from another player, and a path to another computer would make Windows log in to it when File.Exists asks.</summary>
    private static bool ReadPath(JsonElement value, out string path)
    {
        path = "";
        if (value.ValueKind != JsonValueKind.String) return false;
        path = CustomRuleCheck.LocalSound(value.GetString());
        return path.Length > 0 && path.Length <= MaxPath;
    }

    /// <summary>The key's new text once a group's sound for an event (or <see cref="EveryEvent"/>) is set, or cleared
    /// with a null or blank path. The old text when the sound can't be kept (see <see cref="GroupChoicesText{T}.Written"/>).</summary>
    public static string Set(string? json, string group, string eventKey, string? path)
    {
        path = path?.Trim();
        return Text.Set(json, group, eventKey, path ?? "", string.IsNullOrEmpty(path));
    }

    /// <summary>A group is gone: so are its sounds.</summary>
    public static string Forget(string? json, string group) => Text.Forget(json, group);

    /// <summary>An event is gone for good (a rule of the player's own was deleted): so is what every group chose for it.</summary>
    public static string ForgetEvent(string? json, string eventKey) => Text.ForgetEvent(json, eventKey);

    /// <summary>The sounds text after a group was made, renamed or deleted, from the names in use before and now (the
    /// cases of <see cref="GroupAlertChoices.Follow(string?, IReadOnlySet{string}, IReadOnlySet{string}, string?, string?)"/>).
    /// A name no group goes by holds no sound, and a name that has just come into being starts with none. A rename
    /// onto a free name takes the sounds along; a group still going by the old name keeps them too. A rename onto
    /// another group's name makes one group of the two: it keeps its own sounds and gets the renamed group's where
    /// it has none. The same text comes back when there is nothing to do, and when the new one would not read back whole.</summary>
    public static string Follow(string? json, IReadOnlySet<string> before, IReadOnlySet<string> now, string? from = null, string? to = null)
    {
        var groups = Text.Parse(json);
        if (groups.Count == 0 || before == null || now == null) return json ?? "";
        bool changed = false;
        bool renamed = from != null && to != null && from != to && before.Contains(from) && now.Contains(to) && to.Length <= MaxName;
        Dictionary<string, string>? moved = null;
        if (renamed) groups.TryGetValue(from!, out moved);
        foreach (var name in groups.Keys.ToList())
            if (!now.Contains(name) || !before.Contains(name)) changed |= groups.Remove(name);
        if (renamed && moved != null)
        {
            var one = new Dictionary<string, string>(moved, StringComparer.Ordinal);
            if (before.Contains(to!) && groups.TryGetValue(to!, out var theirs))
                foreach (var (key, path) in theirs) one[key] = path;     // the group that had the name keeps its own
            groups[to!] = one;
            changed = true;
        }
        return changed ? Text.Written(groups, json) : json ?? "";
    }
}

/// <summary>Sounds per group (IDEAS #58): the sound a group's members' alerts play. A group may have a sound for one
/// event and one for every event. For a character: a sound for that event in any of its groups beats a sound for
/// every event, and among equals the first group on the Groups page decides. <see cref="Publish"/> builds an
/// unchanging picture on the window's thread; <see cref="For"/> reads only the picture, on whatever thread an
/// alert is raised. While no group has a sound there is no picture and <see cref="For"/> answers null at once.</summary>
public sealed class GroupSounds
{
    private sealed class Picture
    {
        public static readonly Picture None = new();
        public Dictionary<string, Dictionary<string, string>> Specific = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Every = new(StringComparer.OrdinalIgnoreCase);
        public bool Any;
    }

    private Picture _now = Picture.None;

    /// <summary>The groups that count: they exist (in the Groups page's order), have a member, and have a sound.</summary>
    private static List<(string Name, IReadOnlyList<string> Members, IReadOnlyDictionary<string, string> Sounds)> Counting(AppSettings s, string? sounds)
    {
        var counting = new List<(string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>)>();
        var read = GroupSoundChoices.Read(sounds);
        if (read.Count == 0) return counting;
        foreach (var group in SettingsUi.MergedGroups.Build(s))
            if (group.Members.Count > 0 && read.TryGetValue(group.Name, out var chosen)) counting.Add((group.Name, group.Members, chosen));
        return counting;
    }

    /// <summary>Takes the settings as they are now. Called on the window's thread.</summary>
    /// <param name="sounds">The sounds text (the settings key that holds it).</param>
    public void Publish(AppSettings s, string? sounds)
    {
        var counting = Counting(s, sounds);
        if (counting.Count == 0)
        {
            Volatile.Write(ref _now, Picture.None);
            return;
        }
        var picture = new Picture { Any = true };
        foreach (var (_, members, chosen) in counting)
            foreach (var member in members)
            {
                if (string.IsNullOrEmpty(member)) continue;
                if (!picture.Specific.TryGetValue(member, out var theirs)) picture.Specific[member] = theirs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, path) in chosen)
                {
                    if (key == GroupSoundChoices.EveryEvent) picture.Every.TryAdd(member, path);   // the first group decides
                    else theirs.TryAdd(key, path);
                }
            }
        Volatile.Write(ref _now, picture);
    }

    /// <summary>Takes the settings' own sounds text (<see cref="AppSettings.GroupAlertSounds"/>). Called on the window's thread.</summary>
    public void Publish(AppSettings s) => Publish(s, s.GroupAlertSounds);

    /// <summary>Some group that exists and has a member has a sound.</summary>
    public bool Any => Volatile.Read(ref _now).Any;

    /// <summary>The sound file the groups of a character give an event, or null where none applies. Any thread; never throws.</summary>
    public string? For(string character, string eventKey)
    {
        var now = Volatile.Read(ref _now);
        if (!now.Any || string.IsNullOrEmpty(character)) return null;
        if (eventKey != null && now.Specific.TryGetValue(character, out var theirs) && theirs.TryGetValue(eventKey, out var own)) return own;
        return now.Every.TryGetValue(character, out var every) ? every : null;
    }

    /// <summary><see cref="Overlaps(AppSettings, string?, string)"/> for the settings' own sounds text.</summary>
    public static IReadOnlyList<(string Member, string Group)> Overlaps(AppSettings s, string group) => Overlaps(s, s.GroupAlertSounds, group);

    /// <summary>For the editor: the members of <paramref name="group"/> that an earlier group on the Groups page
    /// reaches with a sound of its own, and that group: one entry per member, the first such group. The members as
    /// the group spells them, in its order. Empty for a name no group has. The window's thread.</summary>
    public static IReadOnlyList<(string Member, string Group)> Overlaps(AppSettings s, string? sounds, string group)
    {
        var overlaps = new List<(string, string)>();
        var earlier = new List<(string Name, IReadOnlyList<string> Members)>();
        var read = GroupSoundChoices.Read(sounds);
        foreach (var g in SettingsUi.MergedGroups.Build(s))
        {
            if (g.Name == group) break;
            if (g.Members.Count > 0 && read.ContainsKey(g.Name)) earlier.Add((g.Name, g.Members));
        }
        if (earlier.Count == 0) return overlaps;
        var own = SettingsUi.MergedGroups.Build(s).Where(g => g.Name == group).SelectMany(g => g.Members)
            .Where(member => !string.IsNullOrEmpty(member)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var member in own)
            if (earlier.FirstOrDefault(g => g.Members.Contains(member, StringComparer.OrdinalIgnoreCase)).Name is { } reaches)
                overlaps.Add((member, reaches));
        return overlaps;
    }
}
