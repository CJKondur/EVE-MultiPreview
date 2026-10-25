using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>Plan 38 (IDEAS P126): the switches of a live-state reply that are not one character's. <c>Profile</c> is the real
/// profile name, <c>ProfileShown</c> the name as it is drawn (streamer mode masks it); <c>WorstUnseen</c> is the most severe
/// alert of any character the player has not looked at (null or a word nobody knows: none).</summary>
public sealed record LiveGlobals(bool DoNotDisturb, bool FocusMode, bool ThumbnailsHidden, bool NameBar, bool Streamer,
    string? WorstUnseen, string Profile, string ProfileShown);

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
/// c=1|critical|4FC3F7|shown name|real name      (one per character: in front 1|0, worst unseen alert, group colour RRGGBB or -)
/// </code></summary>
public static class LiveState
{
    public const int Version = 1;

    /// <summary>The longest line, without its line break, in UTF-8 bytes. A longer line is left out, never cut.</summary>
    public const int MaxLineBytes = 200;

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

    /// <summary>The lines of a reply, the nine of the switches first, then one for each of <paramref name="entries"/> in its order.
    /// <paramref name="shown"/> gives the name as it is drawn (streamer mode masks it); the real name always stands last. A line
    /// that <see cref="IsLine"/> refuses is not removed here: <see cref="QueryPayload.Frame"/> leaves it out.</summary>
    public static IReadOnlyList<string> Lines(LiveGlobals g, IReadOnlyList<NameBarEntry> entries, Func<string, string> shown)
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
        foreach (var entry in entries)
            lines.Add("c=" + (entry.Active ? "1" : "0") + "|" + Severity(entry.Severity) + "|" + Colour(entry.GroupColour) + "|"
                + shown(entry.Name) + "|" + entry.Name);
        return lines;
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
    /// format, line or paragraph separator character and no lone surrogate; the key one of the ten, its value one of that key's
    /// words. A <c>c=</c> line has five fields (no <c>|</c> in a name), the first three from the fixed sets, the shown and the real
    /// name each exactly as <see cref="RemoteCommand.Clean"/> keeps it. A <c>profile=</c> value is empty or passes Clean.</summary>
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
            _ => false,
        };
    }

    private static bool IsSeverity(string word) => word is "critical" or "warning" or "info" or "none";

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
