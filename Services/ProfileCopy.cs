using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public static class ProfileCopy
{
    /// <summary>Copies every setting of <paramref name="src"/> onto <paramref name="dst"/> through a
    /// JSON clone, keeping dst's object identity (AppSettings proxies find profiles by name, and the
    /// settings window may hold dst). Moved from SettingsWindow.CopyProfileFull. The groups with keys that only dst
    /// had are gone with the copy: the settings both profiles belong to are handed in, so that those groups' alert
    /// choices go with them (IDEAS #31) and a later group of such a name starts with none.</summary>
    public static void CopyFull(AppSettings s, Profile src, Profile dst)
    {
        if (src == null || dst == null || ReferenceEquals(src, dst)) return;
        var json = JsonSerializer.Serialize(src);
        var clone = JsonSerializer.Deserialize<Profile>(json);
        if (clone == null) return;
        var before = GroupAlertChoices.GroupNames(s);
        foreach (var p in typeof(Profile).GetProperties())
            if (p.CanRead && p.CanWrite)
                p.SetValue(dst, p.GetValue(clone));
        GroupAlertChoices.Follow(s, before);
    }
}
