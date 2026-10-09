using System;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The one way to capture a hotkey group's Next or Previous key, shared by Clients ›
/// Groups and Home › Switching.</summary>
public static class GroupKeys
{
    /// <summary>Captures and stores the key (duplicate group names toast and store nothing), then asks
    /// for a save. True when a key was stored; the caller redraws.</summary>
    public static bool Capture(SettingsContext ctx, MergedGroup g, bool next)
    {
        var id = next ? HotkeyUses.GroupNextId(g.Name) : HotkeyUses.GroupPreviousId(g.Name);
        var key = ctx.Host.CaptureHotkey(id, next ? g.NextKey : g.PreviousKey);
        if (key == null) return false;
        if (!MergedGroups.SetKey(ctx.S, g, next, key))
        {
            ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.Groups.DuplicateName"), g.Name));
            return false;
        }
        ctx.Applier.Request(ApplyScope.Save);
        return true;
    }
}
