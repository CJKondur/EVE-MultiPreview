using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What a press of the focus key comes to (IDEAS P124, final review M4 of Plan 14).</summary>
public enum FocusPress { Off, On, NoGroup, NoSuchGroup }

/// <summary>IDEAS #35, focus mode: which characters' windows it holds hidden (each one's main thumbnail, second thumbnails,
/// crop windows and stats windows: they all ask <see cref="Holds"/>). The group is the one the player chose by its
/// name, or the first group (in the Groups page's order) the character of the client he is in belongs to, so it follows
/// his switches. A client outside the group with an alert he has not looked at can show through. App asks it on its
/// half-second timer while focus mode is on, on the window's thread; it asks Windows nothing.</summary>
public static class FocusMode
{
    /// <summary>Nothing held: focus mode off, or no group to show.</summary>
    public static readonly IReadOnlySet<string> Nobody = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the settings say: the chosen group ("" = the group of the client you are in) and whether an unseen
    /// alert shows through. Read by App only while focus mode is on.</summary>
    public static (string Group, bool AlertsShow) Options(AppSettings? s) =>
        s == null ? ("", true) : (s.FocusModeGroup ?? "", s.FocusModeShowAlerts);

    /// <summary>The groups as the Groups page has them (MergedGroups.Build): each one's name and members.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Groups(AppSettings s) =>
        SettingsUi.MergedGroups.Build(s).Select(g => (g.Name, g.Members)).ToList();

    /// <summary>The group focus mode shows: the chosen one by its exact name, else the first group the active character
    /// is in (ignoring case). Null: the chosen group does not exist, or the client is in no group or at character select.</summary>
    public static (string Name, IReadOnlyList<string> Members)? GroupFor(string chosen, string active,
        IReadOnlyList<(string Name, IReadOnlyList<string> Members)> groups)
    {
        if (chosen.Length > 0)
        {
            foreach (var group in groups)
                if (string.Equals(group.Name, chosen, StringComparison.Ordinal)) return group;
            return null;
        }
        if (active.Length == 0) return null;
        foreach (var group in groups)
            if (group.Members.Contains(active, StringComparer.OrdinalIgnoreCase)) return group;
        return null;
    }

    /// <summary>The running characters whose windows are held hidden: every one outside the group, except one with an unseen
    /// alert while that shows through, and one that logged in a moment ago (<paramref name="arrived"/>, IDEAS P124: so the
    /// player sees it arrive). A client at character select ("") is never held. No group: nobody.</summary>
    public static IReadOnlySet<string> Held((string Name, IReadOnlyList<string> Members)? group, IEnumerable<string> running,
        bool alertsShow, Func<string, bool> alerting, Func<string, bool>? arrived = null)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (group is not { } shown) return held;
        var members = new HashSet<string>(shown.Members, StringComparer.OrdinalIgnoreCase);
        foreach (var name in running)
        {
            if (string.IsNullOrEmpty(name) || members.Contains(name)) continue;
            if (alertsShow && alerting(name)) continue;
            if (arrived != null && arrived(name)) continue;
            held.Add(name);
        }
        return held;
    }

    /// <summary>IDEAS P124: whether this character's windows are held, whichever kind they are (main thumbnail, second
    /// thumbnail, crop window, stats window): they all ask the one answer. A client at character select is never held.</summary>
    public static bool Holds(IReadOnlySet<string> held, string? character) =>
        !string.IsNullOrEmpty(character) && held.Contains(character);

    /// <summary>A press: while on it always turns off; while off it turns on only when there is a group to show. With none
    /// (the chosen group is gone, or the client is in no group or at character select) it stays off and says why.</summary>
    public static FocusPress Press(bool isOn, string chosen, (string Name, IReadOnlyList<string> Members)? group) =>
        isOn ? FocusPress.Off
        : group != null ? FocusPress.On
        : chosen.Length > 0 ? FocusPress.NoSuchGroup
        : FocusPress.NoGroup;

    /// <summary>The note a press says near the cursor: its catalog key and the English the code falls back to.</summary>
    public static (string Key, string English) Note(FocusPress press) => press switch
    {
        FocusPress.Off => ("L.Focus.Off", "Focus mode off"),
        FocusPress.On => ("L.Focus.On", "Focus mode: {0}"),
        FocusPress.NoSuchGroup => ("L.Focus.NoSuchGroup", "Focus mode stays off: there is no group called {0}"),
        _ => ("L.Focus.NoGroup", "Focus mode stays off: this client is in no group"),
    };

    /// <summary>The note in the player's language; <paramref name="name"/> (the group shown, or the group that is gone) is
    /// only ever an argument of the text.</summary>
    public static string Say(FocusPress press, string name)
    {
        var (key, english) = Note(press);
        var text = LocalizationService.Str(key, english);
        return press is FocusPress.On or FocusPress.NoSuchGroup ? string.Format(text, StreamerMask.Name(StreamerKind.Group, name)) : text;   // streamer mode (IDEAS #19)
    }
}
