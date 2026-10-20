using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #14: what the type-to-switch palette offers, built from the lists the remote commands' runner reads (the
/// running characters, the groups, the profiles) and the labels the player gave characters. Pure: the window and App only hand
/// the lists in. Streamer mode (IDEAS #19) masks the text a row shows and is matched against (matching real names would
/// leak them to anyone typing a prefix); the command of a row always names the real character, group or profile.</summary>
public static class CommandPalette
{
    /// <summary>The rows, in the order the palette lists them: running characters (<c>Focus</c>), then the labels of those
    /// characters (<c>Focus</c> on the character the label is on), then groups (<c>CycleGroup</c>: forward, like
    /// <c>--cycle</c>), then profiles (<c>Profile</c>). A name asked twice (in any letter case) is one row; a character with
    /// no label and a blank name make no row. In streamer mode a label is hidden: <see cref="StreamerMask.Name"/> gives it the
    /// empty text, so its entry has an empty <see cref="PaletteEntry.Name"/>, which <see cref="PaletteMatcher"/> filters out
    /// (it is never shown nor matched).</summary>
    public static IReadOnlyList<PaletteEntry> Entries(IEnumerable<string> running, IReadOnlyDictionary<string, string>? labels,
        IEnumerable<string> groups, IEnumerable<string> profiles)
    {
        var entries = new List<PaletteEntry>();
        var characters = Distinct(running);
        foreach (var name in characters)
            entries.Add(new PaletteEntry(PaletteKind.Character, StreamerMask.Name(StreamerKind.Character, name),
                new RemoteCommand(RemoteVerb.Focus, name)));
        if (labels != null)
            foreach (var name in characters)
                if (labels.TryGetValue(name, out var label) && !string.IsNullOrWhiteSpace(label))
                    entries.Add(new PaletteEntry(PaletteKind.Label, StreamerMask.Name(StreamerKind.Label, label.Trim()),
                        new RemoteCommand(RemoteVerb.Focus, name)));
        foreach (var name in Distinct(groups))
            entries.Add(new PaletteEntry(PaletteKind.Group, StreamerMask.Name(StreamerKind.Group, name),
                new RemoteCommand(RemoteVerb.CycleGroup, name)));
        foreach (var name in Distinct(profiles))
            entries.Add(new PaletteEntry(PaletteKind.Profile, StreamerMask.Name(StreamerKind.Profile, name),
                new RemoteCommand(RemoteVerb.Profile, name)));
        return entries;
    }

    private static List<string> Distinct(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
