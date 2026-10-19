using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public static class ProfileCopy
{
    /// <summary>Copies every setting of <paramref name="src"/> onto <paramref name="dst"/> through a
    /// JSON clone, keeping dst's object identity (AppSettings proxies find profiles by name, and the
    /// settings window may hold dst). Moved from SettingsWindow.CopyProfileFull. The groups with keys that only dst
    /// had are gone with the copy: the settings both profiles belong to are handed in, so that those groups' alert
    /// choices go with them (IDEAS #31) and a later group of such a name starts with none. The target keeps its own
    /// characters for "Switch profiles by who is logged in" (IDEAS #11).</summary>
    public static void CopyFull(AppSettings s, Profile src, Profile dst)
    {
        if (src == null || dst == null || ReferenceEquals(src, dst)) return;
        var json = JsonSerializer.Serialize(src);
        var clone = JsonSerializer.Deserialize<Profile>(json);
        if (clone == null) return;
        var before = GroupAlertChoices.GroupNames(s);
        // IDEAS #11: which characters pick a profile belongs to that profile: a copy of every setting keeps the target's,
        // or two profiles with the same characters would tie and neither would ever be picked.
        var ownCharacters = dst.AutoSelectCharacters;
        foreach (var p in typeof(Profile).GetProperties())
            if (p.CanRead && p.CanWrite)
                p.SetValue(dst, p.GetValue(clone));
        dst.AutoSelectCharacters = ownCharacters;
        GroupAlertChoices.Follow(s, before);
    }
}
