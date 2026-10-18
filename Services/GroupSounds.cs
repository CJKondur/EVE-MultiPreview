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

    public const int MaxText = 64 * 1024, MaxGroups = 100, MaxEvents = 200, MaxName = 200, MaxPath = 1024;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The sounds, by group name (compared exactly, as group names are) and event key.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Read(string? json)
    {
        var groups = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (name, events) in Parse(json)) groups[name] = events;
        return groups;
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string? json)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxText) return groups;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return groups;
            foreach (var group in doc.RootElement.EnumerateObject())
            {
                if (groups.Count >= MaxGroups) break;
                if (group.Name.Length is 0 or > MaxName || group.Value.ValueKind != JsonValueKind.Object) continue;
                var events = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var choice in group.Value.EnumerateObject())
                {
                    if (events.Count >= MaxEvents) break;
                    if (choice.Name.Length is 0 or > MaxName || choice.Value.ValueKind != JsonValueKind.String) continue;
                    // Only a file on a drive of this PC (CustomRuleCheck.LocalSound): a settings file can come from another
                    // player, and a path to another computer would make Windows log in to it when File.Exists asks.
                    var path = CustomRuleCheck.LocalSound(choice.Value.GetString());
                    if (path.Length > 0 && path.Length <= MaxPath) events[choice.Name] = path;
                }
                if (events.Count > 0) groups[group.Name] = events;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not the sounds text: no sound at all (see GroupAlertChoices.Parse for the three ways in).
            groups.Clear();
        }
        return groups;
    }

    private static string Write(Dictionary<string, Dictionary<string, string>> groups)
    {
        var ordered = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (name, events) in groups)
            if (events.Count > 0) ordered[name] = new SortedDictionary<string, string>(events, StringComparer.Ordinal);
        return ordered.Count == 0 ? "" : JsonSerializer.Serialize(ordered, Json);
    }

    /// <summary>The text for these sounds, or <paramref name="old"/> where that text would not read back whole (a
    /// table that is full never reads as empty, to be written over by the next choice). The limits are
    /// <see cref="Parse"/>'s, asked of it and stated nowhere else.</summary>
    private static string Written(Dictionary<string, Dictionary<string, string>> groups, string? old)
    {
        var text = Write(groups);
        var back = Parse(text);
        int kept = 0;
        foreach (var (name, events) in groups)
        {
            if (events.Count == 0) continue;
            kept++;
            if (!back.TryGetValue(name, out var read) || read.Count != events.Count) return old ?? "";
            foreach (var (key, path) in events)
                if (!read.TryGetValue(key, out var was) || was != path) return old ?? "";
        }
        return back.Count == kept ? text : old ?? "";
    }

    /// <summary>The key's new text once a group's sound for an event (or <see cref="EveryEvent"/>) is set, or cleared
    /// with a null or blank path. The old text when the sound can't be kept (see <see cref="Written"/>).</summary>
    public static string Set(string? json, string group, string eventKey, string? path)
    {
        var groups = Parse(json);
        group ??= "";
        eventKey ??= "";
        path = path?.Trim();
        if (!groups.TryGetValue(group, out var events))
        {
            if (string.IsNullOrEmpty(path)) return Written(groups, json);
            groups[group] = events = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (string.IsNullOrEmpty(path)) events.Remove(eventKey);
        else events[eventKey] = path;
        return Written(groups, json);
    }

    /// <summary>A group is gone: so are its sounds.</summary>
    public static string Forget(string? json, string group)
    {
        var groups = Parse(json);
        return groups.Remove(group ?? "") ? Write(groups) : json ?? "";
    }

    /// <summary>An event is gone for good (a rule of the player's own was deleted): so is what every group chose for it.</summary>
    public static string ForgetEvent(string? json, string eventKey)
    {
        var groups = Parse(json);
        bool any = false;
        foreach (var events in groups.Values) any |= events.Remove(eventKey ?? "");
        return any ? Write(groups) : json ?? "";
    }

    /// <summary>The sounds text after a group was made, renamed or deleted, from the names in use before and now (the
    /// cases of <see cref="GroupAlertChoices.Follow(string?, IReadOnlySet{string}, IReadOnlySet{string}, string?, string?)"/>).
    /// A name no group goes by holds no sound, and a name that has just come into being starts with none. A rename
    /// onto a free name takes the sounds along; a group still going by the old name keeps them too. A rename onto
    /// another group's name makes one group of the two: it keeps its own sounds and gets the renamed group's where
    /// it has none. The same text comes back when there is nothing to do, and when the new one would not read back whole.</summary>
    public static string Follow(string? json, IReadOnlySet<string> before, IReadOnlySet<string> now, string? from = null, string? to = null)
    {
        var groups = Parse(json);
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
        return changed ? Written(groups, json) : json ?? "";
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
