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
/// key, as it does for the saved colours of colour-blind mode. Whatever the key holds, reading it never throws.
/// The text has limits, and reading stops at them; a change that would cross one is not made (the old text comes
/// back), so that what was chosen can always be read back whole.</summary>
public static class GroupAlertChoices
{
    public const int MaxText = GroupChoicesText<bool>.MaxText, MaxGroups = GroupChoicesText<bool>.MaxGroups,
        MaxEvents = GroupChoicesText<bool>.MaxEvents, MaxName = GroupChoicesText<bool>.MaxName;

    private static readonly GroupChoicesText<bool> Text = new(ReadChoice);

    /// <summary>The choices, by group name (compared exactly, as group names are) and event key.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> Read(string? json) => Text.Read(json);

    /// <summary>A choice: true or false, or 1 or 0 as the rest of the settings file writes a switch. Anything else
    /// (another number, a text, null) is no choice.</summary>
    private static bool ReadChoice(JsonElement value, out bool on)
    {
        on = false;
        switch (value.ValueKind)
        {
            case JsonValueKind.True: on = true; return true;
            case JsonValueKind.False: return true;
            case JsonValueKind.Number when value.TryGetInt32(out int number) && number is 0 or 1: on = number == 1; return true;
            default: return false;
        }
    }

    /// <summary>The key's new text once a group's choice for an event is set, or cleared with null ("as everyone").
    /// The old text when the choice can't be kept (see <see cref="GroupChoicesText{T}.Written"/>).</summary>
    public static string Set(string? json, string group, string eventKey, bool? on) =>
        Text.Set(json, group, eventKey, on ?? false, on == null);

    /// <summary>A group was renamed: its choices go with it, an "on" as an "off". Whatever an earlier group left
    /// under the new name is dropped, never inherited, also when the renamed group has no choices of its own. A new
    /// name that can hold no choices (empty, or too long) takes none, and none stays behind under the old one. The
    /// old text when the new one would not read back whole (see <see cref="GroupChoicesText{T}.Written"/>).</summary>
    public static string Rename(string? json, string from, string to)
    {
        if (from == to) return json ?? "";
        var groups = Text.Parse(json);
        bool had = groups.Remove(from ?? "", out var events);
        bool left = groups.Remove(to ?? "");
        if (!had && !left) return json ?? "";
        if (had && !string.IsNullOrEmpty(to) && to.Length <= MaxName) groups[to] = events!;
        return Text.Written(groups, json);
    }

    /// <summary>A group is gone: so are its choices.</summary>
    public static string Forget(string? json, string group) => Text.Forget(json, group);

    /// <summary>An event is gone for good (a rule of the player's own was deleted, and its id never comes back): so
    /// is what every group chose about it.</summary>
    public static string ForgetEvent(string? json, string eventKey) => Text.ForgetEvent(json, eventKey);

    /// <summary>The names groups go by, in every profile: every colour group, and every profile's groups with keys.
    /// A name in it is in use; every other name is free (MergedGroups.IsNameFree asks the same of one name).</summary>
    public static HashSet<string> GroupNames(AppSettings s)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (s == null) return names;
        if (s.ThumbnailGroups != null)
            foreach (var colour in s.ThumbnailGroups)
                if (!string.IsNullOrEmpty(colour?.Name)) names.Add(colour.Name);
        if (s.HotkeyGroups != null) names.UnionWith(s.HotkeyGroups.Keys);
        if (s.Profiles != null)
            foreach (var profile in s.Profiles.Values)
                if (profile?.HotkeyGroups != null) names.UnionWith(profile.HotkeyGroups.Keys);
        return names;
    }

    /// <summary>THE helper of every path that makes, renames or deletes a group or a settings profile: the choices
    /// stay with their group, and what a group that is gone left behind never comes back to life under a new group
    /// of its name. Take <see cref="GroupNames"/> before the change and call this after it:
    /// a group that came into being under a name that was free in every profile starts with no choices;
    /// the choices of every name that is now free in every profile are forgotten (a deleted group, a deleted profile's
    /// own groups, a profile copied over);
    /// and a group renamed from <paramref name="from"/> to <paramref name="to"/> takes its choices along, an "on" as
    /// an "off", and never inherits (the cases are told at the text's own Follow, below).
    /// It changes the settings' texts and nothing else: saving and applying are the caller's, as for the change itself.
    /// The sounds per group (GroupAlertSounds, IDEAS #58) follow the same way, here and nowhere else, so that no path
    /// takes one text along and leaves the other.</summary>
    public static void Follow(AppSettings s, IReadOnlySet<string> before, string? from = null, string? to = null)
    {
        if (s == null || (string.IsNullOrEmpty(s.GroupAlertProfiles) && string.IsNullOrEmpty(s.GroupAlertSounds))) return;
        var now = GroupNames(s);
        if (!string.IsNullOrEmpty(s.GroupAlertProfiles)) s.GroupAlertProfiles = Follow(s.GroupAlertProfiles, before, now, from, to);
        if (!string.IsNullOrEmpty(s.GroupAlertSounds)) s.GroupAlertSounds = GroupSoundChoices.Follow(s.GroupAlertSounds, before, now, from, to);
    }

    /// <summary>The choices text after such a change, from the names in use before and now.
    /// A rename has three cases. The new name was free: the choices move to it, and whatever was left under it is
    /// dropped. A group still goes by the old name (the old window renames a colour group and leaves the keys of the
    /// same name where they are): that group keeps the choices too, so the rename changes nobody's alerts. The new
    /// name was another group's already (the old window allows that): the two are one group now, and what the
    /// player wants of it is not known, so the alert happens: "on" where either said "on", "off" only where both
    /// said "off", and no choice where one said "off" and the other nothing (also when that other had no choices
    /// at all: nobody is silenced by a rename).
    /// The same text comes back when there is nothing to do, and when the new one would not read back whole.</summary>
    public static string Follow(string? json, IReadOnlySet<string> before, IReadOnlySet<string> now, string? from = null, string? to = null)
    {
        var groups = Text.Parse(json);
        if (groups.Count == 0 || before == null || now == null) return json ?? "";
        bool changed = false;
        bool renamed = from != null && to != null && from != to && before.Contains(from) && now.Contains(to) && to.Length <= MaxName;
        Dictionary<string, bool>? moved = null;
        if (renamed) groups.TryGetValue(from!, out moved);
        // A name no group goes by holds no choices, and a name that has just come into being starts with none.
        foreach (var name in groups.Keys.ToList())
            if (!now.Contains(name) || !before.Contains(name)) changed |= groups.Remove(name);
        if (renamed && !before.Contains(to!))
        {
            // The group's new name, which was free: its choices, as they are.
            if (moved != null)
            {
                groups[to!] = new Dictionary<string, bool>(moved, StringComparer.Ordinal);
                changed = true;
            }
        }
        else if (renamed && (moved != null || groups.ContainsKey(to!)))
        {
            // The name was another group's: one group now. An "off" stays only where both said "off".
            groups.TryGetValue(to!, out var theirs);
            var one = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var chosen in new[] { moved, theirs })
                if (chosen != null)
                    foreach (var (key, on) in chosen)
                        if (on) one[key] = true;
            if (moved != null && theirs != null)
                foreach (var (key, on) in moved)
                    if (!on && theirs.TryGetValue(key, out var also) && !also) one[key] = false;
            if (one.Count > 0) groups[to!] = one;
            else groups.Remove(to!);
            changed = true;
        }
        return changed ? Text.Written(groups, json) : json ?? "";
    }
}

/// <summary>Alerts per group (IDEAS #31): for which characters an event is on. A group of Clients › Groups can
/// switch an event on or off for its members; an event it says nothing about, and every character in no group, has
/// the switch for everyone (Alerts › Events). For a character in several groups ON WINS: where its groups disagree
/// about an event, the event is on. One "on" among them is enough; an "off" holds only while none of them says "on";
/// a group that says nothing about the event counts for nothing. The order of the groups on the Groups page (which
/// a player does not choose: giving a group a colour moves it) changes no answer. When in doubt an alert happens.
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
        /// <summary>For each character in a group that has choices: what its groups chose, one answer for each event
        /// one of them chose about ("on" where one of them says "on").</summary>
        public Dictionary<string, Dictionary<string, bool>> ByCharacter = new(StringComparer.OrdinalIgnoreCase);
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
                if (!picture.ByCharacter.TryGetValue(member, out var theirs)) picture.ByCharacter[member] = theirs = new Dictionary<string, bool>(StringComparer.Ordinal);
                // ON WINS: one "on" among the character's groups is enough, whichever of them comes first.
                foreach (var (key, on) in chosen) theirs[key] = on || (theirs.TryGetValue(key, out var was) && was);
            }
        }
        Volatile.Write(ref _now, picture);
    }

    /// <summary>Takes the settings as they are now, with the choices they hold.</summary>
    public void Publish(AppSettings s) => Publish(s, s.GroupAlertProfiles);

    /// <summary>Hands out the settings as they are now, to the gate (its picture) and to the log monitor (what it
    /// may raise: MonitorSet's table, or the settings' own table while no group choice counts),
    /// in the order that lets nothing through in between. While a choice counts, the gate is first: it holds what is
    /// off for a character before the log monitor starts to raise what only a group wants. When none counts (the last
    /// choice was cleared, the group or its profile is gone), the log monitor is first: it stops raising what only a
    /// group wanted before the gate opens for everybody. The window's thread.</summary>
    /// <param name="s">Nothing happens without settings.</param>
    /// <param name="tellMonitor">LogMonitorService.SetEnabledAlertTypes; null while there is no log monitor yet
    /// (a profile switch in the first moments of a start): the gate is handed its picture all the same. It is not
    /// called while the settings hold no table.</param>
    public void Apply(AppSettings? s, Action<Dictionary<string, bool>>? tellMonitor)
    {
        if (s == null) return;
        var wanted = MonitorSet(s);
        if (wanted != null) Publish(s);
        var table = wanted ?? s.EnabledAlertTypes;
        if (tellMonitor != null && table != null) tellMonitor(table);
        if (wanted == null) Publish(s);
    }

    /// <summary>What the log monitor may raise, with the choices the settings hold; null while none counts.</summary>
    public static Dictionary<string, bool>? MonitorSet(AppSettings s) => MonitorSet(s, s.GroupAlertProfiles);

    /// <summary>The members of every group that exists, has a member and says "on" about an event: not the switch for everyone,
    /// and an "off" or no word does not count. "Logi idle" (IDEAS #28) reads its logi marks here: a group is marked as logi
    /// by setting that event to on for it. The window's thread.</summary>
    public static HashSet<string> MembersSayingOn(AppSettings s, string eventKey)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, group, chosen) in Counting(s, s.GroupAlertProfiles))
            if (chosen.TryGetValue(eventKey, out var on) && on)
                foreach (var member in group)
                    if (!string.IsNullOrEmpty(member)) members.Add(member);
        return members;
    }

    /// <summary>Where a member of a group gets something other than the group's own row says, with the choices the
    /// settings hold.</summary>
    public static IReadOnlyList<(string Member, string Event, bool On, string Group)> Overridden(AppSettings s, string group) => Overridden(s, s.GroupAlertProfiles, group);

    /// <summary>Some group that exists and has a member has a choice of its own.</summary>
    public bool Any => Volatile.Read(ref _now).Any;

    /// <summary>The gate: whether an event is on for a character. Any thread. Never throws: a character without a
    /// name is in no group (the switch for everyone decides), and an event without a key is on.</summary>
    public bool IsOn(string character, string eventKey)
    {
        var now = Volatile.Read(ref _now);
        if (!now.Any || eventKey == null) return true;   // no group has a choice: what is off for everyone is not raised in the first place
        if (!string.IsNullOrEmpty(character) && now.ByCharacter.TryGetValue(character, out var theirs) && theirs.TryGetValue(eventKey, out var on)) return on;
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

    /// <summary>For the editor: where a member of a group gets, for an event, something other than the group's own
    /// row says, and which group decides that. The row says "on" or "off" where the group chose, and otherwise what
    /// everyone has. So this lists a member whose "off" does not hold because another of its groups says "on", and a
    /// member whose "as everyone" is decided by another group's choice; never an event the two decide alike. What
    /// the member gets (<c>On</c>) is the gate's own answer, asked of a picture of these very settings.
    /// One entry per member and event: the members in the group's order and as the group spells them; the events as
    /// Alerts › Events lists them, then the player's own rules; of several groups that decide alike, the first on the
    /// Groups page. Empty for a group nobody else's choices reach, and for a name no group has. The window's thread.</summary>
    public static IReadOnlyList<(string Member, string Event, bool On, string Group)> Overridden(AppSettings s, string? choices, string group)
    {
        var differ = new List<(string, string, bool, string)>();
        var others = Counting(s, choices).Where(g => g.Name != group).ToList();
        if (others.Count == 0) return differ;

        var events = new List<string>();
        var chosen = new HashSet<string>(others.SelectMany(g => g.Choices.Keys), StringComparer.Ordinal);
        foreach (var e in SettingsUi.AlertEvents.Listed)
            if (chosen.Remove(e.Key)) events.Add(e.Key);
        events.AddRange(chosen.OrderBy(key => key, StringComparer.Ordinal));

        var gate = new AlertProfiles();
        gate.Publish(s, choices);
        var everyone = gate._now.Everyone;
        GroupAlertChoices.Read(choices).TryGetValue(group ?? "", out var own);
        var members = SettingsUi.MergedGroups.Build(s).Where(g => g.Name == group).SelectMany(g => g.Members)
            .Where(member => !string.IsNullOrEmpty(member)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var theirs = others.Where(g => g.Members.Contains(member, StringComparer.OrdinalIgnoreCase)).ToList();
            if (theirs.Count == 0) continue;
            foreach (var key in events)
            {
                bool gets = gate.IsOn(member, key);
                bool says = own != null && own.TryGetValue(key, out var stance) ? stance : !everyone.TryGetValue(key, out var all) || all;
                if (gets == says) continue;
                var decides = theirs.FirstOrDefault(g => g.Choices.TryGetValue(key, out var on) && on == gets);
                if (decides.Name != null) differ.Add((member, key, gets, decides.Name));
            }
        }
        return differ;
    }
}
