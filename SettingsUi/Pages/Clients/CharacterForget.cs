using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>"Forget this character" (spec §8.4): every entry for the name, in every profile and in
/// the global stores, names matched ignoring case. Groups and slots themselves stay.</summary>
public static class CharacterForget
{
    public static void Purge(AppSettings s, string name)
    {
        bool Same(string other) => string.Equals(other, name, StringComparison.OrdinalIgnoreCase);
        void Keys<T>(Dictionary<string, T> d) { foreach (var key in d.Keys.Where(Same).ToList()) d.Remove(key); }

        foreach (var p in s.Profiles.Values)
        {
            Keys(p.Hotkeys);
            Keys(p.ThumbnailVisibility);
            Keys(p.ThumbnailPositions);
            Keys(p.CustomColors);
            Keys(p.SecondaryThumbnails);
            Keys(p.PerClientCores);
            Keys(p.PerClientAudioVolume);
            Keys(p.Crops);
            Keys(p.ClientPositions);
            Keys(p.StatOverlayCharacters);
            p.DontMinimizeClients.RemoveAll(Same);
            p.ClientSlotExcluded.RemoveAll(Same);
            foreach (var slot in p.ClientSlots) slot.Characters.RemoveAll(Same);
            foreach (var group in p.HotkeyGroups.Values) group.Characters.RemoveAll(Same);
            foreach (var group in p.Groups.Values) group.Members.RemoveAll(Same);
        }
        Keys(s.ThumbnailAnnotations);
        Keys(s.ThumbnailLabelStyles);
        Keys(s.PerCharacterStats);
        Keys(s.StatWindowPositions);
        foreach (var group in s.ThumbnailGroups) group.Members.RemoveAll(Same);
        s.QuickSwitchCardOrder.RemoveAll(Same);
    }

    /// <summary>Asks first (naming the character), purges, puts a running client back to defaults,
    /// saves at once with a re-apply (App re-registers hotkeys on Applied) and returns to Characters.</summary>
    public static void ForgetInteractive(SettingsContext ctx, string name)
    {
        if (!ctx.Host.Confirm(SettingsContext.Str("L.S.Character.ForgetTitle"),
                string.Format(SettingsContext.Str("L.S.Character.ForgetConfirm"), name))) return;
        Purge(ctx.S, name);
        var tm = ctx.Services.Thumbnails;
        if (tm != null)
        {
            tm.DestroySecondaryForCharacter(name);
            tm.SetCharacterVisibility(name, true);
            tm.UpdateCharacterLabel(name, null, null, 0);
            tm.SetClientVolume(name, 100);
            if (tm.IsExcludedFromCycle(name)) tm.ToggleCycleExclusion(name);
        }
        ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
        ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.Character.Forgotten"), name));
        ctx.Host.Navigate(ClientsPages.CharactersId);
    }
}
