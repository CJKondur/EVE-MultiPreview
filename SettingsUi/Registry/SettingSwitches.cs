using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Turns a toggle on the way its own card does, from outside a card (the "Make Stream Deck buttons" question, Plan 38;
/// the Allow of the pairing window, Plan 39): through the registry's Get/Set, listed in Recently changed with its Undo. It does
/// not save and does not run the card's OnChanged (the caller has no settings context, or runs it itself).</summary>
public static class SettingSwitches
{
    /// <summary>Sets the toggle <paramref name="settingId"/> to on and records the change. An id that is not a setting throws;
    /// a toggle already on is left as it is and listed nowhere. Returns whether it changed.</summary>
    public static bool TurnOn(SettingsService service, AppSettings s, string settingId)
    {
        var d = SettingsRegistry.Settings.Single(x => x.Id == settingId);
        var before = d.Get(s);
        if (SettingVM.Same(before, true)) return false;
        d.Set(s, true);
        service.Changes.Record(d, SettingChanges.ScopeOf(d, s), before, d.Get(s));
        return true;
    }
}
