using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EveMultiPreview.Services;

/// <summary>The text behind a table of choices by group (IDEAS P111): one line of JSON text in one settings key, a
/// group's name, and under it an event's key and what the group chose for it. <see cref="GroupAlertChoices"/> (on or
/// off, IDEAS #31) and <see cref="GroupSoundChoices"/> (a sound file, IDEAS #58) are this with their own value, and
/// keep what is theirs: how a value reads, how a rename and a changed set of group names are followed.
/// The promises are the same for both: one line; reading never throws; the limits are <see cref="Parse"/>'s, and a
/// change that would cross one is not made (the old text comes back), so that what was chosen can always be read
/// back whole.</summary>
internal sealed class GroupChoicesText<T>
{
    internal const int MaxText = 64 * 1024, MaxGroups = 100, MaxEvents = 200, MaxName = 200;

    /// <summary>Reads one choice's value; false where it is no choice (the choice is then skipped).</summary>
    internal delegate bool ValueReader(JsonElement value, out T result);

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ValueReader _read;

    internal GroupChoicesText(ValueReader read) => _read = read;

    /// <summary>The choices, by group name (compared exactly, as group names are) and event key.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>> Read(string? json)
    {
        var groups = new Dictionary<string, IReadOnlyDictionary<string, T>>(StringComparer.Ordinal);
        foreach (var (name, events) in Parse(json)) groups[name] = events;
        return groups;
    }

    internal Dictionary<string, Dictionary<string, T>> Parse(string? json)
    {
        var groups = new Dictionary<string, Dictionary<string, T>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxText) return groups;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return groups;
            foreach (var group in doc.RootElement.EnumerateObject())
            {
                if (groups.Count >= MaxGroups) break;
                if (group.Name.Length is 0 or > MaxName || group.Value.ValueKind != JsonValueKind.Object) continue;
                var events = new Dictionary<string, T>(StringComparer.Ordinal);
                foreach (var choice in group.Value.EnumerateObject())
                {
                    if (events.Count >= MaxEvents) break;
                    if (choice.Name.Length is 0 or > MaxName) continue;
                    if (_read(choice.Value, out var value)) events[choice.Name] = value;
                }
                if (events.Count > 0) groups[group.Name] = events;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not the choices text: no choice at all. A text that is not JSON fails before the first group is taken.
            // A name that can't be read (half a character written as an escape: InvalidOperationException) fails
            // while the groups are taken, and a text .NET can't take at all (half a character as it is:
            // ArgumentException) before that; whatever was taken until then goes too.
            groups.Clear();
        }
        return groups;
    }

    internal static string Write(Dictionary<string, Dictionary<string, T>> groups)
    {
        var ordered = new SortedDictionary<string, SortedDictionary<string, T>>(StringComparer.Ordinal);
        foreach (var (name, events) in groups)
            if (events.Count > 0) ordered[name] = new SortedDictionary<string, T>(events, StringComparer.Ordinal);
        return ordered.Count == 0 ? "" : JsonSerializer.Serialize(ordered, Json);
    }

    /// <summary>The text for these choices, or <paramref name="old"/> where that text would not read back whole: it
    /// would be longer than a text may be, hold more groups or more events than are read, or a name that is empty,
    /// too long or can't be written as it is. The limits are <see cref="Parse"/>'s, asked of it and stated nowhere
    /// else. A table that is full never reads as empty, to be written over by the next choice.</summary>
    internal string Written(Dictionary<string, Dictionary<string, T>> groups, string? old)
    {
        var text = Write(groups);
        var back = Parse(text);
        int kept = 0;
        foreach (var (name, events) in groups)
        {
            if (events.Count == 0) continue;
            kept++;
            if (!back.TryGetValue(name, out var read) || read.Count != events.Count) return old ?? "";
            foreach (var (key, value) in events)
                if (!read.TryGetValue(key, out var was) || !EqualityComparer<T>.Default.Equals(was, value)) return old ?? "";
        }
        return back.Count == kept ? text : old ?? "";
    }

    /// <summary>The key's new text once a group's choice for an event is set, or cleared (<paramref name="clear"/>:
    /// the value is then ignored). The old text when the choice can't be kept (see <see cref="Written"/>).</summary>
    internal string Set(string? json, string group, string eventKey, T value, bool clear)
    {
        var groups = Parse(json);
        group ??= "";
        eventKey ??= "";
        if (!groups.TryGetValue(group, out var events))
        {
            if (clear) return Written(groups, json);
            groups[group] = events = new Dictionary<string, T>(StringComparer.Ordinal);
        }
        if (clear) events.Remove(eventKey);
        else events[eventKey] = value;
        return Written(groups, json);
    }

    /// <summary>A group is gone: so are its choices.</summary>
    internal string Forget(string? json, string group)
    {
        var groups = Parse(json);
        return groups.Remove(group ?? "") ? Write(groups) : json ?? "";
    }

    /// <summary>An event is gone for good (a rule of the player's own was deleted, and its id never comes back): so
    /// is what every group chose about it.</summary>
    internal string ForgetEvent(string? json, string eventKey)
    {
        var groups = Parse(json);
        bool any = false;
        foreach (var events in groups.Values) any |= events.Remove(eventKey ?? "");
        return any ? Write(groups) : json ?? "";
    }
}
