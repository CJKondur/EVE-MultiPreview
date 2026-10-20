using System;
using System.Collections.Generic;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>One snooze the Add row of Alerts › Events › Snoozed right now is about to make (IDEAS P80 (1)). The length is
/// null for "until MultiPreview restarts", as <see cref="EventSnoozes.Snooze"/> has it.</summary>
public readonly record struct SnoozeAddition(string Character, string EventKey, TimeSpan? Length);

/// <summary>What the Add row makes of what the player chose and typed. No window in it; the name is only ever data.</summary>
public static class SnoozeAdd
{
    /// <summary>The lengths the row offers, in order: the toast menu's three, with its texts.</summary>
    public static readonly IReadOnlyList<(string TextKey, TimeSpan? Length)> Lengths = new (string, TimeSpan?)[]
    {
        ("L.Toast.Snooze15", TimeSpan.FromMinutes(15)),
        ("L.Toast.Snooze60", TimeSpan.FromHours(1)),
        ("L.Toast.SnoozeRestart", null),
    };

    /// <summary>The snooze to make, or null when there is nothing to make: no character, no event, no time, or a name or
    /// an event key that the snooze file would leave out (a control or format character, half of a character, longer than
    /// <see cref="EventSnoozes.MaxName"/> / <see cref="EventSnoozes.MaxKey"/>), which would hold until the next start and
    /// be gone after it. A name has no space at its ends. It is never cut to fit: a cut name matches no character, so
    /// one that is too long is refused. Its case is kept: a snooze finds a character whatever its case.</summary>
    /// <param name="length">Null: until MultiPreview restarts. Zero or less: no time at all.</param>
    public static SnoozeAddition? TryBuild(string? character, string? eventKey, TimeSpan? length)
    {
        if (string.IsNullOrWhiteSpace(eventKey) || eventKey.Length > EventSnoozes.MaxKey || !EventSnoozes.Plain(eventKey)) return null;
        if (length is TimeSpan span && span <= TimeSpan.Zero) return null;
        var name = (character ?? "").Trim();
        if (name.Length == 0 || name.Length > EventSnoozes.MaxName || !EventSnoozes.Plain(name) || HasHalfCharacter(name)) return null;
        return new SnoozeAddition(name, eventKey, length);
    }

    private static bool HasHalfCharacter(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(text[i])) return true;
        }
        return false;
    }
}
