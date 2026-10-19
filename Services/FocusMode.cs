using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #35, focus mode: which main thumbnails it holds hidden. The group is the one the player chose by its
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

    /// <summary>The running characters whose main thumbnails are held hidden: every one outside the group, except one with
    /// an unseen alert while that shows through. A client at character select ("") is never held. No group: nobody.</summary>
    public static IReadOnlySet<string> Held((string Name, IReadOnlyList<string> Members)? group, IEnumerable<string> running,
        bool alertsShow, Func<string, bool> alerting)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (group is not { } shown) return held;
        var members = new HashSet<string>(shown.Members, StringComparer.OrdinalIgnoreCase);
        foreach (var name in running)
        {
            if (string.IsNullOrEmpty(name) || members.Contains(name)) continue;
            if (alertsShow && alerting(name)) continue;
            held.Add(name);
        }
        return held;
    }
}
