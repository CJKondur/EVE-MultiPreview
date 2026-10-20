using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>One line of the hotkey cheat sheet: what the key does (a card's name, a character's or group's use) and the
/// key as the settings window writes it.</summary>
public sealed record HotkeySheetRow(string Action, string Keys);

/// <summary>IDEAS #16, the rows of the cheat sheet: every hotkey of the current profile that is bound.</summary>
public static class HotkeySheet
{
    /// <summary>The window shows this many rows and counts the rest.</summary>
    public const int MaxRows = 60;

    /// <summary>The list the conflict check and the refused-keys card read (<see cref="HotkeyUses.All"/>), so the three
    /// never disagree: shown hotkey cards that are bound, then this profile's character keys, then group keys. The sheet is
    /// a screen that can be shared: character and group names in it follow streamer mode (IDEAS #19).</summary>
    public static IReadOnlyList<HotkeySheetRow> Rows(AppSettings s, IEnumerable<SettingDescriptor> settings) =>
        HotkeyUses.All(s, settings, masked: true)
            .Select(u => new HotkeySheetRow(u.Label, HotkeyText.ToDisplay(u.Hotkey)))
            .Where(r => r.Keys.Length > 0)
            .ToList();
}
