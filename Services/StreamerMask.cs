using System;
using System.Collections.Generic;
using System.Globalization;

namespace EveMultiPreview.Services;

/// <summary>What a piece of player text is, which decides how streamer mode (IDEAS #19) masks it.</summary>
public enum StreamerKind
{
    /// <summary>A character name: "Client 1", "Client 2" ...</summary>
    Character,
    /// <summary>A label the player typed on a thumbnail: hidden.</summary>
    Label,
    /// <summary>A solar system name, also each end of a system change: hidden.</summary>
    System,
    /// <summary>A group name: "Group 1" ...</summary>
    Group,
    /// <summary>A profile name: "Profile 1" ...</summary>
    Profile,
    /// <summary>A layout preset name: "Layout 1" ...</summary>
    Layout,
}

/// <summary>Streamer mode (IDEAS #19): the one rule for what a name looks like on a screen that is being shared. With the
/// mode off every call returns the text it was given. With it on a character, group, profile or layout name becomes
/// "Client 1", "Group 2" ... (the number is the order the name was first seen in since MultiPreview started, the same
/// name, in any letter case, always gets the same number) and a label or a system name is hidden (empty).
///
/// Names are lookup keys all over the app (settings, alerts, snoozes): the mask is applied only where text is drawn,
/// never to the value that is kept, compared or saved. The text asked about is only ever a dictionary key here, never a
/// format string, and is never logged.</summary>
public static class StreamerMask
{
    private static readonly object Gate = new();
    private static readonly Dictionary<StreamerKind, Dictionary<string, int>> Seen = new();
    private static Func<bool> _on = () => false;

    /// <summary>Whether the mode is on right now.</summary>
    public static bool IsOn => _on();

    /// <summary>Follows the saved setting (App, once, at start): the mode is whatever <c>StreamerMode</c> says each time it is asked.</summary>
    public static void Follow(SettingsService settings) => Follow(() => settings.Settings.StreamerMode);

    /// <summary>Follows another source of the mode (tests).</summary>
    public static void Follow(Func<bool> on) => _on = on ?? (() => false);

    /// <summary>Off, and no number handed out yet (tests).</summary>
    public static void Reset()
    {
        _on = () => false;
        lock (Gate) Seen.Clear();
    }

    /// <summary>The text to draw for <paramref name="raw"/>. The mode off, or nothing to mask (null, empty or blank): the
    /// text as it came. Hidden kinds give "". A name that is first seen now takes the next number of its kind.</summary>
    public static string Name(StreamerKind kind, string? raw)
    {
        if (!IsOn || string.IsNullOrWhiteSpace(raw)) return raw ?? "";
        if (kind is StreamerKind.Label or StreamerKind.System) return "";

        int n;
        lock (Gate)
        {
            if (!Seen.TryGetValue(kind, out var numbers))
                Seen[kind] = numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string key = raw.Trim();
            if (!numbers.TryGetValue(key, out n)) numbers[key] = n = numbers.Count + 1;
        }
        var (id, english) = kind switch
        {
            StreamerKind.Group => ("L.Streamer.Group", "Group {0}"),
            StreamerKind.Profile => ("L.Streamer.Profile", "Profile {0}"),
            StreamerKind.Layout => ("L.Streamer.Layout", "Layout {0}"),
            _ => ("L.Streamer.Client", "Client {0}"),
        };
        return string.Format(CultureInfo.CurrentCulture, LocalizationService.Str(id, english), n.ToString(CultureInfo.InvariantCulture));
    }
}
