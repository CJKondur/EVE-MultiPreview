using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Profile operations for the new window: the navigation profile menu and
/// App › Profiles & backups. Every path that changes the active profile ends in
/// SwitchProfile, so App re-applies thumbnails, positions, crops and hotkeys.</summary>
public static class ProfileActions
{
    private static string Str(string key) => SettingsContext.Str(key);

    public static void Switch(SettingsContext ctx, string name) => ctx.Service.SwitchProfile(name);

    /// <summary>A new, empty profile, made active. False for a blank or taken name.</summary>
    public static bool New(SettingsContext ctx, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || ctx.S.Profiles.ContainsKey(name)) return false;
        ctx.S.Profiles[name] = new Profile();
        ctx.Service.SwitchProfile(name);
        return true;
    }

    /// <summary>Never deletes the last profile. Deleting the active one switches to another first.</summary>
    public static bool Delete(SettingsContext ctx, string name)
    {
        if (ctx.S.Profiles.Count <= 1 || !ctx.S.Profiles.ContainsKey(name)) return false;
        if (ctx.S.LastUsedProfile == name)
            ctx.Service.SwitchProfile(ctx.S.Profiles.Keys.First(k => k != name));
        return ctx.Service.DeleteProfile(name);
    }

    /// <summary>The old "Copy Profile" dialog's two modes: every setting (full) or thumbnail
    /// positions only, into one profile or (to == null) every other profile. Returns the count.</summary>
    public static int CopyLayout(SettingsContext ctx, string from, string? to, bool full)
    {
        if (!ctx.S.Profiles.TryGetValue(from, out var src)) return 0;
        var targets = ctx.S.Profiles.Where(kv => kv.Key != from && (to == null || kv.Key == to)).Select(kv => kv.Value).ToList();
        var activeHit = false;
        foreach (var dst in targets)
        {
            if (full) ProfileCopy.CopyFull(ctx.S, src, dst);
            else dst.ThumbnailPositions = new Dictionary<string, ThumbnailRect>(src.ThumbnailPositions);
            if (ctx.S.Profiles.TryGetValue(ctx.S.LastUsedProfile, out var active) && ReferenceEquals(active, dst)) activeHit = true;
        }
        if (full && activeHit) ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
        else if (targets.Count > 0) ctx.Applier.Request(ApplyScope.Thumbnails);
        return targets.Count;
    }

    public static void NewInteractive(SettingsContext ctx)
    {
        var name = ctx.Host.AskText(Str("L.S.Profile.New"), Str("L.S.Profile.NamePrompt"), "");
        if (name == null) return;
        ctx.Host.Toast(New(ctx, name)
            ? string.Format(Str("L.S.Profile.Created"), name.Trim())
            : string.Format(Str("L.S.Profile.NameTaken"), name.Trim()));
    }

    /// <summary>The old window's Copy Profile flow (OnCopyLayout). With one profile, name a new one
    /// and copy everything into it; otherwise the existing copy dialog (from, to or all profiles,
    /// every setting or positions only).</summary>
    public static void CopyInteractive(SettingsContext ctx)
    {
        var names = ctx.Service.GetProfileNames().ToList();
        if (names.Count < 2)
        {
            var source = ctx.S.LastUsedProfile;
            var name = ctx.Host.AskText(Str("L.S.Profile.Copy"), Str("L.S.Profile.OnlyOne"), "");
            if (name == null) return;
            if (!New(ctx, name)) { ctx.Host.Toast(string.Format(Str("L.S.Profile.NameTaken"), name.Trim())); return; }
            ProfileCopy.CopyFull(ctx.S, ctx.S.Profiles[source], ctx.S.Profiles[name.Trim()]);
            ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
            ctx.Host.Reload();
            ctx.Host.Toast(string.Format(Str("L.S.Profile.Copied"), 1));
            return;
        }
        var dialog = new Views.CopyLayoutDialog(names, ctx.S.LastUsedProfile) { Owner = ctx.Host.DialogOwner };
        if (dialog.ShowDialog() != true || dialog.SelectedFrom == null) return;
        var count = CopyLayout(ctx, dialog.SelectedFrom, dialog.IsAllProfiles ? null : dialog.SelectedTo, dialog.FullProfileCopy);
        ctx.Host.Reload();
        ctx.Host.Toast(string.Format(Str("L.S.Profile.Copied"), count));
    }

    public static void DeleteInteractive(SettingsContext ctx, string name)
    {
        if (ctx.S.Profiles.Count <= 1) { ctx.Host.Toast(Str("L.S.Profile.LastOne")); return; }
        if (!ctx.Host.Confirm(Str("L.S.Profile.DeleteTitle"), string.Format(Str("L.S.Profile.DeleteConfirm"), name))) return;
        if (Delete(ctx, name)) ctx.Host.Toast(string.Format(Str("L.S.Profile.Deleted"), name));
    }
}
