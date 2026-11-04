using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Profile operations for the new window: the navigation profile menu and
/// App › Profiles & backups. Every path that changes the active profile ends in
/// SwitchProfile, so App re-applies thumbnails, positions, crops and hotkeys.
/// The two dialogs (copy, character mapping) are the window's own: ProfileActions.Wpf.cs on Windows, the Linux app's on Linux.</summary>
public static partial class ProfileActions
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
        var name = ctx.Host.AskText(AccessKeys.Spoken(Str("L.S.Profile.New")), Str("L.S.Profile.NamePrompt"), "");
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
            var name = ctx.Host.AskText(AccessKeys.Spoken(Str("L.S.Profile.Copy")), Str("L.S.Profile.OnlyOne"), "");
            if (name == null) return;
            if (!New(ctx, name)) { ctx.Host.Toast(string.Format(Str("L.S.Profile.NameTaken"), name.Trim())); return; }
            ProfileCopy.CopyFull(ctx.S, ctx.S.Profiles[source], ctx.S.Profiles[name.Trim()]);
            ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
            ctx.Host.Reload();
            ctx.Host.Toast(string.Format(Str("L.S.Profile.Copied"), 1));
            return;
        }
        if (AskCopy(ctx, names) is not { } copy) return;
        var count = CopyLayout(ctx, copy.From, copy.To, copy.Full);
        ctx.Host.Reload();
        ctx.Host.Toast(string.Format(Str("L.S.Profile.Copied"), count));
    }

    /// <summary>The copy dialog: from which profile, to one profile or (To == null) every other one, every setting (Full) or
    /// the thumbnail positions only. Null when it was cancelled or closed.</summary>
    private static partial (string From, string? To, bool Full)? AskCopy(SettingsContext ctx, List<string> names);

    /// <summary>IDEAS #12: the look of the current profile as a code on the clipboard (copy is ClipboardText.Set in the app,
    /// a recorder in tests).</summary>
    public static void CopyLookCode(SettingsContext ctx, Func<string, bool> copy)
    {
        var code = ShareCode.Look(CharacterDirectory.Current(ctx.S));
        ctx.Host.Toast(copy(code)
            ? string.Format(Str("L.S.Profile.LookCopied"), ctx.S.LastUsedProfile)
            : Str("L.S.Presets.CodeCopyFailed"));
    }

    /// <summary>IDEAS P123: the current profile with its characters as a code on the clipboard (the look, and the per-character
    /// data of ProfileCode.CarriedStores; never a hotkey, a CPU mask, an audio volume or statistics). A profile that names more
    /// than 64 characters, or whose code would be longer than a code may be, says so and copies nothing.</summary>
    public static void CopyProfileCode(SettingsContext ctx, Func<string, bool> copy)
    {
        var code = ShareCode.WithCharacters(ctx.S, CharacterDirectory.Current(ctx.S));
        if (code == null) { ctx.Host.Toast(Str("L.S.Profile.CodeTooBig")); return; }
        ctx.Host.Toast(!copy(code) ? Str("L.S.Presets.CodeCopyFailed")
            : code.Length > ShareCode.DiscordSafeLength
                ? string.Format(Str("L.S.Profile.ProfileCopiedLong"), ctx.S.LastUsedProfile, code.Length)
                : string.Format(Str("L.S.Profile.ProfileCopied"), ctx.S.LastUsedProfile));
    }

    /// <summary>The window's mapping dialog for a profile code: the code's characters (in order) against the local ones; the
    /// chosen local character per code character, or null when the dialog was cancelled, closed or could not be opened.</summary>
    public static partial Dictionary<int, string>? AskMapping(SettingsContext ctx, IReadOnlyList<string> codeCharacters, IReadOnlyList<string> local);

    /// <summary>IDEAS #12 and P123: a new profile from a code: asks for the code, then for a name; the new profile is a copy of
    /// the current one with the code's look (its own characters for IDEAS #11 stay none), and becomes the one in use. A code
    /// with characters (EMP-P2:) also asks which of the player's characters each of its characters is (<paramref name="map"/>
    /// stands in for the dialog): cancelling that makes no profile. Nothing is overwritten.</summary>
    public static void FromCodeInteractive(SettingsContext ctx,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, Dictionary<int, string>?>? map = null)
    {
        var text = ctx.Host.AskText(Str("L.S.Profile.FromCodeTitle"), Str("L.S.Profile.FromCodePrompt"), "");
        if (text == null) return;
        if (ShareCode.IsProfileCode(text)) { FromProfileCode(ctx, text, map ?? ((codes, local) => AskMapping(ctx, codes, local))); return; }
        var read = ShareCode.ReadLook(text);
        if (read.Value is not { } look) { ctx.Host.Toast(Str(read.ErrorKey)); return; }
        var name = ctx.Host.AskText(Str("L.S.Profile.FromCodeTitle"), Str("L.S.Profile.NamePrompt"), "");
        if (name == null) return;
        var source = ctx.S.LastUsedProfile;
        if (!New(ctx, name)) { ctx.Host.Toast(string.Format(Str("L.S.Profile.NameTaken"), name.Trim())); return; }
        var made = ctx.S.Profiles[name.Trim()];
        if (ctx.S.Profiles.TryGetValue(source, out var from)) ProfileCopy.CopyFull(ctx.S, from, made);
        look.ApplyTo(made);
        ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
        ctx.Host.Reload();
        ctx.Host.Toast(string.Format(Str("L.S.Profile.FromCodeDone"), name.Trim()));
    }

    private static void FromProfileCode(SettingsContext ctx, string text,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, Dictionary<int, string>?> map)
    {
        var read = ShareCode.ReadWithCharacters(text);
        if (read.Value is not { } code) { ctx.Host.Toast(Str(read.ErrorKey)); return; }
        var name = ctx.Host.AskText(Str("L.S.Profile.FromCodeTitle"), Str("L.S.Profile.NamePrompt"), "");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0 || ctx.S.Profiles.ContainsKey(name)) { ctx.Host.Toast(string.Format(Str("L.S.Profile.NameTaken"), name)); return; }
        var mapping = map(code.Characters, CharacterDirectory.AllKnown(ctx.S, CharacterDirectory.Running(ctx)));
        if (mapping == null) return;                                            // cancelled: no profile, nothing changed
        var source = ctx.S.LastUsedProfile;
        if (!New(ctx, name)) { ctx.Host.Toast(string.Format(Str("L.S.Profile.NameTaken"), name)); return; }
        var made = ctx.S.Profiles[name];
        if (ctx.S.Profiles.TryGetValue(source, out var from)) ProfileCopy.CopyFull(ctx.S, from, made);
        var applied = code.ApplyTo(ctx.S, made, mapping);
        ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
        ctx.Host.Reload();
        ctx.Host.Toast(string.Format(Str("L.S.Profile.FromCodeDoneCharacters"), name, applied));
    }

    /// <summary>IDEAS #50: the active profile against another one. Asks which (never the active one itself), then goes
    /// through the settings that differ one by one: take the other profile's value, keep yours, or cancel, which stops the
    /// walk (values already taken stay taken). Nothing is written to the other profile; the startup backups are the undo.
    /// Names and values a player wrote only ever go in as string.Format arguments.</summary>
    public static void CompareInteractive(SettingsContext ctx)
    {
        var active = ctx.S.LastUsedProfile;
        if (!ctx.S.Profiles.TryGetValue(active, out var mine)) return;
        var others = ctx.Service.GetProfileNames().Where(n => n != active).ToList();
        if (others.Count == 0) { ctx.Host.Toast(Str("L.S.Profile.CompareNoOther")); return; }
        var name = ctx.Host.PickCharacter(Str("L.S.Profile.ComparePick"), others);   // the picker also takes a typed name
        if (name == null) return;
        if (name == active || !ctx.S.Profiles.TryGetValue(name, out var theirs)) { ctx.Host.Toast(Str("L.S.Profile.CompareNoOther")); return; }

        var rows = ProfileDiff.Compare(mine, theirs);
        if (rows.Count == 0) { ctx.Host.Toast(string.Format(Str("L.S.Profile.CompareNone"), name)); return; }
        int taken = 0;
        bool switched = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var label = row.LabelKey == null ? row.Property : Str(row.LabelKey);
            if (label == row.LabelKey) label = row.Property;                          // a title that is missing shows the name
            var answer = ctx.Host.ConfirmChoice(
                string.Format(Str("L.S.Profile.CompareTitle"), name, i + 1, rows.Count),
                string.Format(Str("L.S.Profile.CompareRow"), name, label, row.TextMine, row.TextOther),
                Str("L.S.Profile.CompareTake"), Str("L.S.Profile.CompareKeep"), SafeAnswer.Second);
            // A profile hotkey can switch the profile while the question is open: then nothing more is copied (into the
            // profile that is no longer the active one) and nothing is applied.
            if (ctx.S.LastUsedProfile != active || !ReferenceEquals(ctx.S.Profiles.GetValueOrDefault(active), mine)) { switched = true; break; }
            if (answer == null) break;
            if (answer == true)
            {
                ProfileDiff.CopyOne(theirs, mine, row.Property, ctx.S);
                taken++;
            }
        }
        if (taken > 0 && !switched)
        {
            ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
            ctx.Host.Reload();
        }
        ctx.Host.Toast(string.Format(Str("L.S.Profile.CompareDone"), taken, name));
    }

    public static void DeleteInteractive(SettingsContext ctx, string name)
    {
        if (ctx.S.Profiles.Count <= 1) { ctx.Host.Toast(Str("L.S.Profile.LastOne")); return; }
        if (!ctx.Host.Confirm(Str("L.S.Profile.DeleteTitle"), string.Format(Str("L.S.Profile.DeleteConfirm"), name))) return;
        if (Delete(ctx, name)) ctx.Host.Toast(string.Format(Str("L.S.Profile.Deleted"), name));
    }
}
