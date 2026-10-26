using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>Plan 38 (IDEAS P126): the switches of a live-state reply that are not one character's. <c>Profile</c> is the real
/// profile name, <c>ProfileShown</c> the name as it is drawn (streamer mode masks it); <c>WorstUnseen</c> is the most severe
/// alert of any character the player has not looked at (null or a word nobody knows: none). <c>WorstEvent</c> is the event key of
/// that alert (<see cref="LiveState.EventKey"/> keeps only the allow-listed ones; null: none).</summary>
public sealed record LiveGlobals(bool DoNotDisturb, bool FocusMode, bool ThumbnailsHidden, bool NameBar, bool Streamer,
    string? WorstUnseen, string Profile, string ProfileShown, string? WorstEvent = null);

/// <summary>Plan 38 (IDEAS P126): the reply of the live-state query, which the Stream Deck plugin asks about once a second. It
/// is a short text of lines, each <c>key=value</c>; a character is one line of five fields divided by <c>|</c>. The keys and the
/// words are protocol, English in every language; the plugin reads this format byte for byte. Pure: App hands it what it
/// knows and it reads no file, no setting and no window. The same file is compiled into the thin sender.
///
/// <code>
/// v=1
/// do-not-disturb=on|off
/// focus-mode=on|off
/// thumbnails=shown|hidden
/// name-bar=on|off
/// streamer=on|off
/// alert=critical|warning|info|none
/// profile=real profile name
/// profile-shown=the profile name as it is drawn
/// alert-event=warp_scramble                     (only when the worst unseen alert has an event key of <see cref="EventKeys"/>)
/// c=1|critical|4FC3F7|shown name|real name      (one per character: in front 1|0, worst unseen alert, group colour RRGGBB or -)
/// e=real name|warp_scramble                     (after ALL c= lines; only for a character whose top unseen alert has an event key)
/// u=real name|warp_scramble|critical            (Plan 39; after ALL e= lines; one per unseen alert kind of a character)
/// </code>
/// The two event lines are the addendum's (Plan 38, EVE icons) and new keys only, so a plugin that does not know them ignores
/// them and a <c>c=</c> line keeps its five fields. An event key is a word of <see cref="EventKeys"/> or no line at all; what a
/// player typed never leaves.
///
/// Plan 39 adds <c>u=</c>: ONE line for each kind of alert a character has not looked at, so a key of the Stream Deck lights for
/// exactly that alert and that character. The name is the real name of that character's <c>c=</c> line (a join key, never
/// drawn); the key is a word of <see cref="EventKeys"/> (a <c>custom:</c> rule is <c>custom</c>); the severity is the worst one
/// any unseen alert of that kind has, <c>critical</c>, <c>warning</c> or <c>info</c> (never <c>none</c>). A kind is sent once for
/// a character; they stand in the order of the <c>c=</c> lines, the most severe kind first, then by key; only a character whose
/// <c>c=</c> line passes <see cref="IsLine"/> has any. They come after every <c>e=</c> line, so at the cap they go first.
/// Also the two lists the plugin asks for, <see cref="AlertTypeLines"/> (and <c>RemoteCommand.CommandLines</c>): pipe-only, one
/// line each, judged by <see cref="IsAlertTypeLine"/>.</summary>
public static class LiveState
{
    public const int Version = 1;

    /// <summary>The longest line, without its line break, in UTF-8 bytes. A longer line is left out, never cut.</summary>
    public const int MaxLineBytes = 200;

    /// <summary>The event keys a live-state line may carry: the built-in keys of <c>AlertEvents.Listed</c> and <c>custom</c>. A literal
    /// copy, because this file is also compiled into the thin sender, which cannot see the alert code; a test holds it to
    /// <c>AlertEvents.Listed</c> so a new event cannot be forgotten.</summary>
    public static IReadOnlyList<string> EventKeys { get; } = new[]
    {
        "attack", "warp_scramble", "decloak", "neutralized", "jammed", "dps_high", "reps_stopped", "drones_returning", "logi_idle",
        "fleet_invite", "convo_request", "follow_warp", "regroup",
        "system_change", "straggler",
        "mine_cargo_full", "mine_asteroid_depleted", "mine_crystal_broken", "mine_module_stopped",
        "client_memory", "client_hung", "client_closed", "char_idle", "client_ready",
        "custom",
    };

    private static readonly HashSet<string> EventKeySet = new(EventKeys, StringComparer.Ordinal);

    private const string CustomPrefix = "custom:";

    /// <summary>The event key a live-state line may carry for an alert type: the type itself when it is one of
    /// <see cref="EventKeys"/>, <c>custom</c> for any <c>custom:</c> + id (the rule's text and id stay home), else null.</summary>
    public static string? EventKey(string? alertType)
    {
        if (string.IsNullOrEmpty(alertType)) return null;
        if (alertType.StartsWith(CustomPrefix, StringComparison.Ordinal)) return "custom";
        return EventKeySet.Contains(alertType) ? alertType : null;
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Severity(string? severity) => AlertAttention.Rank(severity) switch
    {
        3 => "critical",
        2 => "warning",
        1 => "info",
        _ => "none",
    };

    /// <summary>A profile name as it may stand after <c>profile=</c>: as it is when <see cref="RemoteCommand.Clean"/> keeps it
    /// exactly, else nothing (the line is then <c>profile=</c>).</summary>
    private static string Value(string? text) => RemoteCommand.Clean(text) is { } clean && clean == text ? clean : "";

    /// <summary>The lines of a reply, the nine of the switches first (a tenth, <c>alert-event=</c>, after them when the worst unseen
    /// alert has an event key), then one for each of <paramref name="entries"/> in its order, then one <c>e=</c> line for each
    /// character whose top alert has an event key. <paramref name="shown"/> gives the name as it is drawn (streamer mode masks it);
    /// the real name always stands last. A line that <see cref="IsLine"/> refuses is not removed here (<see cref="QueryPayload.Frame"/>
    /// leaves it out), with one exception: an <c>e=</c> line is made only for a character whose <c>c=</c> line passes
    /// <see cref="IsLine"/>, so it never stands without its character. They come last so that at the cap they go first.
    /// Plan 39: <paramref name="unseen"/> (<c>AlertAttention.Kinds()</c>: character, raw event key, severity) adds the <c>u=</c> lines
    /// after all of those, under the same rule; null or empty adds nothing, and the reply is then exactly what it was without it.</summary>
    public static IReadOnlyList<string> Lines(LiveGlobals g, IReadOnlyList<NameBarEntry> entries, Func<string, string> shown,
        IReadOnlyList<(string Character, string EventKey, string Severity)>? unseen = null)
    {
        var lines = new List<string>(9 + entries.Count)
        {
            "v=" + Version.ToString(CultureInfo.InvariantCulture),
            "do-not-disturb=" + OnOff(g.DoNotDisturb),
            "focus-mode=" + OnOff(g.FocusMode),
            "thumbnails=" + (g.ThumbnailsHidden ? "hidden" : "shown"),
            "name-bar=" + OnOff(g.NameBar),
            "streamer=" + OnOff(g.Streamer),
            "alert=" + Severity(g.WorstUnseen),
            "profile=" + Value(g.Profile),
            "profile-shown=" + Value(g.ProfileShown),
        };
        if (EventKey(g.WorstEvent) is { } worstEvent && Severity(g.WorstUnseen) != "none") lines.Add("alert-event=" + worstEvent);
        List<string>? events = null;
        List<string>? present = unseen is { Count: > 0 } ? new List<string>() : null;   // the names whose c= line is sent
        foreach (var entry in entries)
        {
            var severity = Severity(entry.Severity);
            var line = "c=" + (entry.Active ? "1" : "0") + "|" + severity + "|" + Colour(entry.GroupColour) + "|"
                + shown(entry.Name) + "|" + entry.Name;
            lines.Add(line);
            var key = severity != "none" ? EventKey(entry.EventKey) : null;
            if ((key != null || present != null) && IsLine(line))
            {
                if (key != null) (events ??= new List<string>()).Add("e=" + entry.Name + "|" + key);
                present?.Add(entry.Name);
            }
        }
        if (events != null) lines.AddRange(events);
        if (present != null) AddUnseen(lines, present, unseen!);
        return lines;
    }

    /// <summary>The <c>u=</c> lines: for each name of <paramref name="present"/> (in its order), its unseen kinds as
    /// <see cref="Lines"/> describes them. A row whose character is none of those, whose key is not an allow-listed one, or whose
    /// severity is none or unknown makes no line; a line <see cref="IsLine"/> refuses (a name that makes it too long) is left out.</summary>
    private static void AddUnseen(List<string> lines, List<string> present, IReadOnlyList<(string Character, string EventKey, string Severity)> unseen)
    {
        var byCharacter = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (character, rawKey, rawSeverity) in unseen)
        {
            if (string.IsNullOrEmpty(character) || EventKey(rawKey) is not { } key) continue;
            var severity = Severity(rawSeverity);
            if (severity == "none") continue;
            if (!byCharacter.TryGetValue(character, out var kinds)) byCharacter[character] = kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!kinds.TryGetValue(key, out var held) || AlertAttention.Rank(severity) > AlertAttention.Rank(held)) kinds[key] = severity;
        }
        if (byCharacter.Count == 0) return;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in present)
        {
            if (!done.Add(name) || !byCharacter.TryGetValue(name, out var kinds)) continue;
            foreach (var (key, severity) in kinds.OrderByDescending(k => AlertAttention.Rank(k.Value)).ThenBy(k => k.Key, StringComparer.Ordinal))
            {
                var line = "u=" + name + "|" + key + "|" + severity;
                if (IsLine(line)) lines.Add(line);
            }
        }
    }

    /// <summary>Plan 39 (list-alert-types): one line <c>eventKey|label</c> for each of <see cref="EventKeys"/>, in its order.
    /// <paramref name="label"/> gives a key's name in MultiPreview's language (the catalog's <c>L.AlertEvt.&lt;key&gt;</c>, looked up
    /// by the caller: this file knows no string resources). A label <see cref="RemoteCommand.IsLabel"/> refuses (a line break, a
    /// control character, a <c>|</c>, too long) leaves its line out, never cut.</summary>
    public static IReadOnlyList<string> AlertTypeLines(Func<string, string> label)
    {
        var lines = new List<string>(EventKeys.Count);
        foreach (var key in EventKeys)
        {
            var line = key + "|" + (label(key) ?? "");
            if (IsAlertTypeLine(line)) lines.Add(line);
        }
        return lines;
    }

    /// <summary>Is the text a line of the list-alert-types reply? Two fields on <c>|</c>: a word of <see cref="EventKeys"/> and a
    /// label <see cref="RemoteCommand.IsLabel"/> keeps; at most <see cref="MaxLineBytes"/> bytes of UTF-8.</summary>
    public static bool IsAlertTypeLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var fields = line.Split('|');
        return fields.Length == 2 && EventKeySet.Contains(fields[0]) && RemoteCommand.IsLabel(fields[1])
            && Encoding.UTF8.GetByteCount(line) <= MaxLineBytes;
    }

    /// <summary>A group colour as six upper-case hex digits (<c>#4fc3f7</c> gives <c>4FC3F7</c>); a dash for no colour and for
    /// anything that is not <c>#</c> and six hex digits.</summary>
    public static string Colour(string? groupColour)
    {
        if (groupColour is not { Length: 7 } || groupColour[0] != '#') return "-";
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(groupColour[i])) return "-";
        return groupColour.Substring(1).ToUpperInvariant();
    }

    /// <summary>Is the text a line of this format and safe to send? At most <see cref="MaxLineBytes"/> bytes of UTF-8; no control,
    /// format, line or paragraph separator character and no lone surrogate; the key one of the twelve, its value one of that key's
    /// words. A <c>c=</c> line has five fields (no <c>|</c> in a name), the first three from the fixed sets, the shown and the real
    /// name each exactly as <see cref="RemoteCommand.Clean"/> keeps it. A <c>profile=</c> value is empty or passes Clean. An <c>alert-event=</c> value is one of <see cref="EventKeys"/>;
    /// an <c>e=</c> line has two fields, a name that passes Clean (so no <c>|</c>) and one of <see cref="EventKeys"/>; a <c>u=</c> line (Plan 39)
    /// has three, such a name, one of <see cref="EventKeys"/> and <c>critical</c>, <c>warning</c> or <c>info</c> (not <c>none</c>).</summary>
    public static bool IsLine(string? line)
    {
        if (string.IsNullOrEmpty(line) || Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return false;
        foreach (var rune in line.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) return false;   // a lone surrogate reads as the replacement character
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
        }
        int at = line.IndexOf('=');
        if (at < 0) return false;
        var value = line.Substring(at + 1);
        return line.Substring(0, at) switch
        {
            "v" => value == Version.ToString(CultureInfo.InvariantCulture),
            "do-not-disturb" or "focus-mode" or "name-bar" or "streamer" => value is "on" or "off",
            "thumbnails" => value is "shown" or "hidden",
            "alert" => IsSeverity(value),
            "profile" or "profile-shown" => value.Length == 0 || RemoteCommand.Clean(value) == value,
            "c" => IsCharacter(value),
            "alert-event" => EventKeySet.Contains(value),
            "e" => IsEvent(value),
            "u" => IsUnseen(value),
            _ => false,
        };
    }

    private static bool IsUnseen(string value)
    {
        var fields = value.Split('|');
        return fields.Length == 3 && RemoteCommand.Clean(fields[0]) == fields[0] && EventKeySet.Contains(fields[1])
            && fields[2] is "critical" or "warning" or "info";
    }

    private static bool IsSeverity(string word) => word is "critical" or "warning" or "info" or "none";

    private static bool IsEvent(string value)
    {
        var fields = value.Split('|');
        return fields.Length == 2 && RemoteCommand.Clean(fields[0]) == fields[0] && EventKeySet.Contains(fields[1]);
    }

    private static bool IsCharacter(string value)
    {
        var fields = value.Split('|');
        if (fields.Length != 5) return false;
        if (fields[0] is not ("1" or "0") || !IsSeverity(fields[1])) return false;
        if (fields[2] != "-")
        {
            if (fields[2].Length != 6) return false;
            foreach (char c in fields[2])
                if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F'))) return false;
        }
        return RemoteCommand.Clean(fields[3]) == fields[3] && RemoteCommand.Clean(fields[4]) == fields[4];
    }
}
