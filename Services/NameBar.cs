using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>One button of the name bar (IDEAS #38): a running character, the colour of its first group, the severity of its
/// worst unseen alert, and whether its client is the one in front.</summary>
public sealed record NameBarEntry(string Name, string? GroupColour, string? Severity, bool Active);

/// <summary>IDEAS #38, the compact name bar: which buttons it shows and in which order. Pure: App hands it what it knows (the
/// running characters, the colour groups, the unseen alerts, the client in front, the saved card order) and the window draws
/// the answer. It asks Windows nothing.</summary>
public static class NameBar
{
    /// <summary>The buttons: every running character once (a name is the same name in any case), the saved quick-switch card
    /// order first (<paramref name="order"/>, as the wheel has it), the rest alphabetical. A button carries the colour of the
    /// first group (in the order given) the character is in, the severity of the most severe alert the player has not looked
    /// at (<paramref name="severityOf"/> answers it: null or an unknown word is none) and whether it is the client in front
    /// (<paramref name="active"/>, "" for none). A client at character select (an empty name) has no button.</summary>
    public static IReadOnlyList<NameBarEntry> Entries(IReadOnlyList<string> running,
        IReadOnlyList<(string Name, string? Colour, IReadOnlyList<string> Members)> groups,
        Func<string, string?> severityOf, string active, IReadOnlyList<string> order)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in running)
            if (!string.IsNullOrEmpty(name) && seen.Add(name)) names.Add(name);

        var ordered = new List<string>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in order)
        {
            var match = names.FirstOrDefault(n => string.Equals(n, saved, StringComparison.OrdinalIgnoreCase));
            if (match != null && placed.Add(match)) ordered.Add(match);
        }
        ordered.AddRange(names.Where(n => !placed.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal));

        return ordered.Select(name =>
        {
            string? colour = null;
            foreach (var group in groups)
                if (group.Members.Contains(name, StringComparer.OrdinalIgnoreCase)) { colour = group.Colour; break; }
            var severity = severityOf(name);
            if (AlertAttention.Rank(severity) == 0) severity = null;
            return new NameBarEntry(name, string.IsNullOrWhiteSpace(colour) ? null : colour, severity,
                active.Length > 0 && string.Equals(name, active, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
}
