using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The choices groups made about alerts (IDEAS #31), as one line of JSON text in one settings key
/// (AppSettings.GroupAlertProfiles): a group's name, and under it the events it switches on (true) or off (false).
/// An event a group says nothing about is "as everyone". One line, because the settings file holds one line per
/// key, as it does for the saved colours of colour-blind mode. Whatever the key holds, reading it never throws.</summary>
public static class GroupAlertChoices
{
    public const int MaxText = 64 * 1024, MaxGroups = 100, MaxEvents = 200, MaxName = 200;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The choices, by group name (compared exactly, as group names are) and event key.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> Read(string? json)
    {
        var groups = new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var (name, events) in Parse(json)) groups[name] = events;
        return groups;
    }

    private static Dictionary<string, Dictionary<string, bool>> Parse(string? json)
    {
        var groups = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxText) return groups;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return groups;
            foreach (var group in doc.RootElement.EnumerateObject())
            {
                if (groups.Count >= MaxGroups) break;
                if (group.Name.Length is 0 or > MaxName || group.Value.ValueKind != JsonValueKind.Object) continue;
                var events = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var choice in group.Value.EnumerateObject())
                {
                    if (events.Count >= MaxEvents) break;
                    if (choice.Name.Length is 0 or > MaxName) continue;
                    if (choice.Value.ValueKind == JsonValueKind.True) events[choice.Name] = true;
                    else if (choice.Value.ValueKind == JsonValueKind.False) events[choice.Name] = false;
                }
                if (events.Count > 0) groups[group.Name] = events;
            }
        }
        catch (JsonException)
        {
            // Not JSON: no choice at all. JsonDocument reads the whole text before the first group is taken, so
            // nothing of a broken text was added.
        }
        return groups;
    }

    private static string Write(Dictionary<string, Dictionary<string, bool>> groups)
    {
        var ordered = new SortedDictionary<string, SortedDictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var (name, events) in groups)
            if (events.Count > 0) ordered[name] = new SortedDictionary<string, bool>(events, StringComparer.Ordinal);
        return ordered.Count == 0 ? "" : JsonSerializer.Serialize(ordered, Json);
    }

    /// <summary>The key's new text once a group's choice for an event is set, or cleared with null ("as everyone").</summary>
    public static string Set(string? json, string group, string eventKey, bool? on)
    {
        var groups = Parse(json);
        if (string.IsNullOrEmpty(group) || group.Length > MaxName || string.IsNullOrEmpty(eventKey) || eventKey.Length > MaxName) return Write(groups);
        if (!groups.TryGetValue(group, out var events))
        {
            if (on == null) return Write(groups);
            groups[group] = events = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        if (on == null) events.Remove(eventKey);
        else events[eventKey] = on.Value;
        return Write(groups);
    }

    /// <summary>A group was renamed: its choices go with it. Choices an earlier group left under the new name are
    /// dropped, not inherited. A group without choices changes nothing.</summary>
    public static string Rename(string? json, string from, string to)
    {
        var groups = Parse(json);
        if (from == to || !groups.Remove(from, out var events)) return json ?? "";
        if (!string.IsNullOrEmpty(to) && to.Length <= MaxName) groups[to] = events;
        return Write(groups);
    }

    /// <summary>A group is gone: so are its choices.</summary>
    public static string Forget(string? json, string group)
    {
        var groups = Parse(json);
        return groups.Remove(group) ? Write(groups) : json ?? "";
    }
}

/// <summary>Alerts per group (IDEAS #31): for which characters an event is on. A group of Clients › Groups can
/// switch an event on or off for its members; an event it says nothing about, and every character in no group, has
/// the switch for everyone (Alerts › Events). For a character in several groups, the first of them on the Groups
/// page that has a choice for that event decides.
/// <see cref="Publish"/> builds an unchanging picture of all that on the window's thread; <see cref="IsOn"/>, the
/// gate AlertDispatcher asks first, reads only the picture, on whatever thread an alert is raised. While no group
/// has a choice there is no picture, and the gate answers "on" without looking anything up.</summary>
public sealed class AlertProfiles
{
    private sealed class Picture
    {
        public static readonly Picture None = new();
        /// <summary>The switch for everyone, for each built-in event.</summary>
        public Dictionary<string, bool> Everyone = new(StringComparer.Ordinal);
        /// <summary>For each character in a group that has choices: those groups' choices, in the Groups page's order.</summary>
        public Dictionary<string, List<IReadOnlyDictionary<string, bool>>> ByCharacter = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The events some group switches on.</summary>
        public HashSet<string> OnSomewhere = new(StringComparer.Ordinal);
        public bool Any;
    }

    private Picture _now = Picture.None;

    /// <summary>The groups that count: they exist (in the Groups page's order), have a member, and have a choice.</summary>
    private static List<(string Name, IReadOnlyList<string> Members, IReadOnlyDictionary<string, bool> Choices)> Counting(AppSettings s, string? choices)
    {
        var counting = new List<(string, IReadOnlyList<string>, IReadOnlyDictionary<string, bool>)>();
        var read = GroupAlertChoices.Read(choices);
        if (read.Count == 0) return counting;
        foreach (var group in SettingsUi.MergedGroups.Build(s))
        {
            var members = group.Members;
            if (members.Count > 0 && read.TryGetValue(group.Name, out var chosen)) counting.Add((group.Name, members, chosen));
        }
        return counting;
    }

    /// <summary>Takes the settings as they are now. Called on the window's thread: at startup, after every settings
    /// change, and at a profile switch (a group's members can differ per profile).</summary>
    /// <param name="choices">The choices text (AppSettings.GroupAlertProfiles).</param>
    public void Publish(AppSettings s, string? choices)
    {
        var counting = Counting(s, choices);
        if (counting.Count == 0)
        {
            Volatile.Write(ref _now, Picture.None);
            return;
        }
        var picture = new Picture { Any = true };
        foreach (var e in SettingsUi.AlertEvents.Listed) picture.Everyone[e.Key] = SettingsUi.AlertFields.IsOn(s, e.Key);
        foreach (var (_, members, chosen) in counting)
        {
            foreach (var (key, on) in chosen)
                if (on) picture.OnSomewhere.Add(key);
            foreach (var member in members)
            {
                if (string.IsNullOrEmpty(member)) continue;
                if (!picture.ByCharacter.TryGetValue(member, out var theirs)) picture.ByCharacter[member] = theirs = new List<IReadOnlyDictionary<string, bool>>();
                theirs.Add(chosen);
            }
        }
        Volatile.Write(ref _now, picture);
    }

    /// <summary>Takes the settings as they are now, with the choices they hold.</summary>
    public void Publish(AppSettings s) => Publish(s, s.GroupAlertProfiles);

    /// <summary>What the log monitor may raise, with the choices the settings hold; null while none counts.</summary>
    public static Dictionary<string, bool>? MonitorSet(AppSettings s) => MonitorSet(s, s.GroupAlertProfiles);

    /// <summary>The members of a group that an earlier group's choices reach first, with the choices the settings hold.</summary>
    public static IReadOnlyList<(string Character, string Group)> Overridden(AppSettings s, string group) => Overridden(s, s.GroupAlertProfiles, group);

    /// <summary>Some group that exists and has a member has a choice of its own.</summary>
    public bool Any => Volatile.Read(ref _now).Any;

    /// <summary>The gate: whether an event is on for a character. Any thread.</summary>
    public bool IsOn(string character, string eventKey)
    {
        var now = Volatile.Read(ref _now);
        if (!now.Any) return true;   // no group has a choice: what is off for everyone is not raised in the first place
        if (!string.IsNullOrEmpty(character) && now.ByCharacter.TryGetValue(character, out var theirs))
            foreach (var chosen in theirs)
                if (chosen.TryGetValue(eventKey, out var on)) return on;
        return !now.Everyone.TryGetValue(eventKey, out var everyone) || everyone;
    }

    /// <summary>Whether some group switches an event on (so that what looks for it must run although it is off for
    /// everyone: "Left behind").</summary>
    public bool OnForSomeone(string eventKey) => Volatile.Read(ref _now).OnSomewhere.Contains(eventKey);

    /// <summary>For what follows an alert that was shown (the repeats): whether its event is still on for that
    /// character. Without group choices that is the question asked until now (AlertEscalation.EventOn); with them,
    /// the gate's, and for a rule of the player's own also that the rule is still there and on.</summary>
    public bool StillOn(AppSettings s, string character, string eventKey)
    {
        if (!Any) return AlertEscalation.EventOn(s, eventKey);
        if (CustomAlertTypes.IsCustom(eventKey) && CustomAlertTypes.Find(eventKey) is not { Enabled: true }) return false;
        return IsOn(character, eventKey);
    }

    /// <summary>What the log monitor may raise while groups have choices: each built-in event that is on for
    /// everyone or that some group switches on; every other entry of the settings' table as it is. Null while no
    /// group choice counts: the log monitor is then handed the settings' own table, as it always was. The window's
    /// thread.</summary>
    public static Dictionary<string, bool>? MonitorSet(AppSettings s, string? choices)
    {
        var counting = Counting(s, choices);
        if (counting.Count == 0) return null;
        var set = new Dictionary<string, bool>(s.EnabledAlertTypes ?? new Dictionary<string, bool>());
        foreach (var e in SettingsUi.AlertEvents.Listed)
            set[e.Key] = SettingsUi.AlertFields.IsOn(s, e.Key) || counting.Any(g => g.Choices.TryGetValue(e.Key, out var on) && on);
        return set;
    }

    /// <summary>The members of a group that are also in a group which comes before it on the Groups page and has
    /// choices: for an event both groups chose about, the earlier group's choice is theirs. The editor says so, so
    /// that nobody thinks a character is covered by a choice that does not reach it. The window's thread.</summary>
    public static IReadOnlyList<(string Character, string Group)> Overridden(AppSettings s, string? choices, string group)
    {
        var overridden = new List<(string, string)>();
        var counting = Counting(s, choices);
        int at = counting.FindIndex(g => g.Name == group);
        if (at <= 0) return overridden;
        foreach (var member in counting[at].Members)
        {
            var earlier = counting.Take(at).FirstOrDefault(g => g.Members.Contains(member, StringComparer.OrdinalIgnoreCase));
            if (earlier.Name != null) overridden.Add((member, earlier.Name));
        }
        return overridden;
    }
}
